using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private const string RosterAcceptanceAction = "Candidate acceptance recorded";

    // Letter release, HR-recorded acceptance and explicit Employee Master import
    // are separate steps. Acceptance attests the exact letter, never a signature.
    public async Task<IReadOnlyList<RecruitmentJoiningCandidate>> GetRosterJoiningCandidatesAsync(int clientId, AuthUser user, bool importReleasedForEss = false)
    {
        RosterJoiningReadAccess(user, clientId);
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        var rows = (await db.QueryAsync<RecruitmentDocumentRosterRow>(RosterJoiningRowsSql + " WHERE r.ClientId=@ClientId ORDER BY r.Id", new { ClientId = clientId }))
            .Where(row => CanSeeLocation(user, row.WorkLocationId, locations)).ToList();
        if (rows.Count == 0) return [];
        var releases = (await db.QueryAsync<RosterJoiningRelease>("SELECT Id,RosterId,PdfHash,RevisionKey,InputsHash FROM recruitment_document_roster_releases WHERE ClientId=@ClientId AND RosterId IN @Ids ORDER BY ReleasedAt DESC,Id DESC", new { ClientId = clientId, Ids = rows.Select(row => row.Id).ToArray() }))
            .GroupBy(release => release.RosterId).ToDictionary(group => group.Key, group => group.First());
        var audits = (await db.QueryAsync<RosterJoiningAudit>("SELECT RosterId,CAST(NewValuesJson AS CHAR) Payload FROM recruitment_document_roster_audit WHERE ClientId=@ClientId AND RosterId IN @Ids AND Action=@Action ORDER BY Id DESC", new { ClientId = clientId, Ids = rows.Select(row => row.Id).ToArray(), Action = RosterAcceptanceAction }))
            .GroupBy(audit => audit.RosterId).ToDictionary(group => group.Key, group => group.Select(audit => audit.Payload).ToList());
        var clientName = await db.ExecuteScalarAsync<string>("SELECT Name FROM clients WHERE Id=@ClientId", new { ClientId = clientId }) ?? "";
        return rows.Where(row => releases.ContainsKey(row.Id)).Select(row =>
        {
            var release = releases[row.Id];
            var accepted = audits.GetValueOrDefault(row.Id)?.Any(payload => IsRosterAcceptanceForRelease(payload, release.Id, release.PdfHash, release.RevisionKey)) == true;
            return RosterJoiningProjection(row, clientName, release.Id, accepted, importReleasedForEss);
        }).ToList();
    }

    public async Task<RecruitmentJoiningBatchResult> AcceptSelectedRosterOffersAsync(int clientId, IReadOnlyList<long> ids, AuthUser user)
    {
        IssueAccess(user, clientId);
        ValidateIds(ids.ToList());
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@ClientId AND IsActive=TRUE)", new { ClientId = clientId }))
            throw new InvalidOperationException("Select an active client before recording offer acceptance.");
        var result = new RecruitmentJoiningBatchResult();
        foreach (var id in ids)
        {
            var item = new RecruitmentJoiningItemResult { SourceType = "Roster", SourceId = id, RosterRowId = id, RowKey = "roster:" + id };
            result.Items.Add(item);
            await using var transaction = await db.BeginTransactionAsync();
            try
            {
                // Release/edit also lock the roster row, so a concurrent repeat
                // click cannot write a second acceptance event for this release.
                var row = await db.QuerySingleOrDefaultAsync<RecruitmentDocumentRosterRow>(RosterJoiningRowsSql + " WHERE r.Id=@Id AND r.ClientId=@ClientId FOR UPDATE", new { Id = id, ClientId = clientId }, transaction);
                if (row is null || !CanSeeLocation(user, row.WorkLocationId, locations))
                    throw new InvalidOperationException("The selected released letter is unavailable in your client or work locations.");
                item.CandidateName = row.CandidateName;
                item.EmployeeId = row.EmployeeId; item.EmployeeCode = row.EmployeeCode;
                if (row.ProfileStatus == "Archived") throw new InvalidOperationException("The linked candidate is archived. Review the candidate before recording acceptance.");
                var release = await db.QueryFirstOrDefaultAsync<RosterJoiningRelease>("SELECT Id,RosterId,PdfHash,RevisionKey,InputsHash FROM recruitment_document_roster_releases WHERE ClientId=@ClientId AND RosterId=@Id ORDER BY ReleasedAt DESC,Id DESC LIMIT 1 FOR UPDATE", new { Id = id, ClientId = clientId }, transaction)
                    ?? throw new InvalidOperationException("Release this offer letter before recording candidate acceptance.");
                var payloads = await db.QueryAsync<string>("SELECT CAST(NewValuesJson AS CHAR) FROM recruitment_document_roster_audit WHERE RosterId=@Id AND ClientId=@ClientId AND Action=@Action", new { Id = id, ClientId = clientId, Action = RosterAcceptanceAction }, transaction);
                if (payloads.Any(payload => IsRosterAcceptanceForRelease(payload, release.Id, release.PdfHash, release.RevisionKey)))
                {
                    item.Status = "Already accepted"; item.Message = "Acceptance for this released letter was already recorded.";
                }
                else
                {
                    var receipt = JsonSerializer.Serialize(new { version = 1, releaseId = release.Id, release.PdfHash, release.RevisionKey, acceptedForJoining = true, source = "HR bulk confirmation" }, Json);
                    await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,@Action,@Receipt,@UserId)", new { Id = id, ClientId = clientId, Action = RosterAcceptanceAction, Receipt = receipt, UserId = user.Id }, transaction);
                    item.Status = "Accepted"; item.Message = "HR recorded candidate acceptance. Use Employees > Import selected from Talent Acquisition after joining.";
                }
                await transaction.CommitAsync();
            }
            catch (InvalidOperationException error)
            {
                await transaction.RollbackAsync(); item.Status = "Review"; item.Message = error.Message;
            }
        }
        return result;
    }

    public async Task<RecruitmentJoiningBatchResult> ImportSelectedRosterJoiningCandidatesAsync(int clientId, IReadOnlyList<long> ids, AuthUser user, bool importReleasedForEss = false)
    {
        RosterJoiningReadAccess(user, clientId);
        if (!user.Permissions.Any(permission => permission is "employees.manage" or "settings.manage")) throw new UnauthorizedAccessException();
        ValidateIds(ids.ToList());
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        await EmployeeRepository.PrepareRecruitmentSaveAsync(db);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        var result = new RecruitmentJoiningBatchResult();
        foreach (var id in ids)
        {
            var item = new RecruitmentJoiningItemResult { SourceType = "Roster", SourceId = id, RosterRowId = id, RowKey = "roster:" + id };
            result.Items.Add(item);
            await using var transaction = await db.BeginTransactionAsync();
            try
            {
                // The original release path uses this same client/row lock order.
                // Employee identity is re-read under it before any explicit hire.
                var client = await db.QuerySingleOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, transaction)
                    ?? throw new InvalidOperationException("Select an active client before importing employees.");
                var row = await db.QuerySingleOrDefaultAsync<RecruitmentDocumentRosterRow>(RosterJoiningRowsSql + " WHERE r.Id=@Id AND r.ClientId=@ClientId FOR UPDATE", new { Id = id, ClientId = clientId }, transaction);
                if (row is null || !CanSeeLocation(user, row.WorkLocationId, locations)) throw new InvalidOperationException("The selected candidate is unavailable in your client or work locations.");
                item.CandidateName = row.CandidateName; item.EmployeeId = row.EmployeeId; item.EmployeeCode = row.EmployeeCode;
                if (row.EmployeeId is > 0 && !string.IsNullOrWhiteSpace(row.EmployeeCode))
                {
                    item.Status = "Already imported"; item.Message = "The candidate already has an Employee Master link. No employee details were changed.";
                    await transaction.CommitAsync(); continue;
                }
                if (row.ProfileStatus == "Archived") throw new InvalidOperationException("The linked candidate is archived. Review the candidate before importing.");
                var release = await db.QueryFirstOrDefaultAsync<RosterJoiningRelease>("SELECT Id,RosterId,PdfHash,RevisionKey,InputsHash FROM recruitment_document_roster_releases WHERE ClientId=@ClientId AND RosterId=@Id ORDER BY ReleasedAt DESC,Id DESC LIMIT 1 FOR UPDATE", new { Id = id, ClientId = clientId }, transaction)
                    ?? throw new InvalidOperationException("Release the offer letter and record candidate acceptance before importing.");
                var payloads = await db.QueryAsync<string>("SELECT CAST(NewValuesJson AS CHAR) FROM recruitment_document_roster_audit WHERE RosterId=@Id AND ClientId=@ClientId AND Action=@Action", new { Id = id, ClientId = clientId, Action = RosterAcceptanceAction }, transaction);
                if (!importReleasedForEss && !payloads.Any(payload => IsRosterAcceptanceForRelease(payload, release.Id, release.PdfHash, release.RevisionKey)))
                    throw new InvalidOperationException("Record candidate acceptance for the latest released offer before importing.");
                if (row.WorkLocationId is null or <= 0) throw new InvalidOperationException("Link the candidate's existing client work location before importing.");
                var location = await ActiveLocationAsync(db, clientId, row.WorkLocationId.Value, transaction);
                var organization = await db.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1", transaction: transaction);
                var values = ReadValues(row.ValuesJson); ApplyLocationValues(values, location);
                PrepareReleaseValues(row, values, client.Name, organization?.Name ?? "", row.OfferNumber);
                if (Hash(SerializeValues(values)) != release.InputsHash)
                    throw new InvalidOperationException("Candidate or offer details changed after release. Release and confirm the updated letter before importing.");
                var current = await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, transaction, clientId);
                await LinkReleasedEmployeeAsync(db, transaction, row, values, location, current, user, row.OfferNumber, locations.Select(item => item.Id).ToHashSet(), importReleasedForEss);
                item.EmployeeId = row.EmployeeId;
                item.EmployeeCode = await db.ExecuteScalarAsync<string>("SELECT EmployeeCode FROM employees WHERE Id=@Id AND ClientId=@ClientId", new { Id = row.EmployeeId, ClientId = clientId }, transaction) ?? "";
                item.Status = "Imported"; item.Message = "Candidate imported into Employee Master.";
                await transaction.CommitAsync();
            }
            catch (InvalidOperationException error)
            {
                await transaction.RollbackAsync(); item.Status = "Review"; item.Message = error.Message;
            }
            catch (MySqlException error) when (error.Number is 1062 or 1452)
            {
                await transaction.RollbackAsync(); item.Status = "Review";
                item.Message = "The employee identity or required configuration conflicts with an existing record. Review this candidate before importing.";
            }
        }
        return result;
    }

    private static void RosterJoiningReadAccess(AuthUser user, int clientId)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || clientId <= 0 || !SelectedClientAccess.Allows(user, clientId)
            || user.ClientId.HasValue && user.ClientId != clientId
            || !(RecruitmentPermissions.Has(user, "recruitment.candidate.manage", "recruitment.offer.manage", "recruitment.offer.issue")
                 || user.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase) || user.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("The joining list is outside your permitted client scope.");
    }

    internal static bool IsRosterAcceptanceForRelease(string payload, long releaseId, string pdfHash, string revisionKey)
    {
        if (releaseId <= 0 || pdfHash.Length != 64 || revisionKey.Length != 64) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var value) && value == 1
                && root.TryGetProperty("releaseId", out var release) && release.ValueKind == JsonValueKind.Number && release.TryGetInt64(out var id) && id == releaseId
                && root.TryGetProperty("pdfHash", out var pdf) && pdf.ValueKind == JsonValueKind.String && pdf.GetString() == pdfHash
                && root.TryGetProperty("revisionKey", out var revision) && revision.ValueKind == JsonValueKind.String && revision.GetString() == revisionKey
                && root.TryGetProperty("acceptedForJoining", out var accepted) && accepted.ValueKind == JsonValueKind.True
                && root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == "HR bulk confirmation";
        }
        catch (JsonException) { return false; }
    }

    internal static RecruitmentJoiningCandidate RosterJoiningProjection(RecruitmentDocumentRosterRow row, string clientName, long releaseId, bool accepted, bool importReleasedForEss = false)
    {
        var values = ReadValues(row.ValuesJson);
        DateTime.TryParseExact(values.GetValueOrDefault("joiningDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var joining);
        decimal.TryParse(values.GetValueOrDefault("annualCtc", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var ctc);
        var linked = row.EmployeeId is > 0 && !string.IsNullOrWhiteSpace(row.EmployeeCode);
        return new() { SourceType = "Roster", SourceId = row.Id, RowKey = "roster:" + row.Id, RosterRowId = row.Id, ReleaseId = releaseId,
            AcceptanceRecorded = accepted, OfferId = 0, CandidateId = row.CandidateId, CandidateCode = row.CandidateCode, CandidateName = row.CandidateName,
            ClientId = row.ClientId, ClientName = clientName, PositionTitle = values.GetValueOrDefault("positionTitle", ""),
            JobLocation = row.WorkLocation, ProposedJoiningDate = joining, OfferedCtc = ctc, Currency = "INR", OfferStatus = accepted ? "Accepted" : "Released",
            EmployeeId = row.EmployeeId, EmployeeCode = row.EmployeeCode, ImportStatus = linked ? "Imported" : accepted || importReleasedForEss ? "Ready" : "Review",
            Issue = linked || accepted || importReleasedForEss ? "" : "Record candidate acceptance before importing into Employee Master." };
    }

    private const string RosterJoiningRowsSql = """
SELECT r.Id,r.ClientId,r.CandidateId,r.TemplateId,r.WorkLocationId,r.OfferNumber,CAST(r.ValuesJson AS CHAR) ValuesJson,
       c.CandidateCode,TRIM(CONCAT_WS(' ',c.FirstName,c.LastName)) CandidateName,c.Email,c.Phone,c.ProfileStatus,
       COALESCE(r.EmployeeId,c.EmployeeId) EmployeeId,COALESCE(e.EmployeeCode,'') EmployeeCode,COALESCE(w.Name,'') WorkLocation
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
LEFT JOIN employees e ON e.Id=COALESCE(r.EmployeeId,c.EmployeeId) AND e.ClientId=r.ClientId
LEFT JOIN worklocations w ON w.Id=r.WorkLocationId AND w.ClientId=r.ClientId
""";
    private sealed class RosterJoiningRelease { public long Id { get; set; } public long RosterId { get; set; } public string PdfHash { get; set; } = ""; public string RevisionKey { get; set; } = ""; public string InputsHash { get; set; } = ""; }
    private sealed class RosterJoiningAudit { public long RosterId { get; set; } public string Payload { get; set; } = ""; }
}
