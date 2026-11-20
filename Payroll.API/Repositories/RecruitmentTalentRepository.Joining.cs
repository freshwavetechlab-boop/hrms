using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    internal static AuthUser JoiningUser(AuthUser user, int clientId)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !SelectedClientAccess.Allows(user, clientId))
            throw new UnauthorizedAccessException("The selected client is outside your access.");
        return SelectedClientAccess.ForRequest(user, clientId);
    }

    internal static long[] JoiningSelection(RecruitmentJoiningSelection request)
    {
        if (request.ClientId <= 0 || request.OfferIds is null || request.RosterRowIds is null || request.OfferIds.Count + request.RosterRowIds.Count is < 1 or > 500 || request.OfferIds.Concat(request.RosterRowIds).Any(id => id <= 0))
            throw new InvalidOperationException("Select a client and between 1 and 500 released offers.");
        return request.OfferIds.Distinct().ToArray();
    }

    public async Task<RecruitmentJoiningBatchResult> AcceptSelectedOffersAsync(RecruitmentJoiningSelection request, AuthUser user)
    {
        var ids = JoiningSelection(request);
        var scoped = JoiningUser(user, request.ClientId);
        await using var db = Db(); await db.OpenAsync();
        var available = (await OfferRowsAsync(db, scoped, null, null)).ToDictionary(row => row.Id);
        var latest = available.Values.GroupBy(row => row.ApplicationId).ToDictionary(group => group.Key, group => group.Max(row => row.Id));
        var result = new RecruitmentJoiningBatchResult();
        foreach (var id in ids)
        {
            var item = new RecruitmentJoiningItemResult { OfferId = id, SourceId = id, RowKey = "offer:" + id }; result.Items.Add(item);
            if (!available.TryGetValue(id, out var offer)) { item.Message = "This offer is unavailable in the selected client or stage."; continue; }
            if (RecruitmentAccessScope.IsRestricted(scoped) && !await RecruitmentAccessScope.CanAccessApplicationWorkLocationAsync(db, scoped, offer.ApplicationId))
            { item.Message = "This offer is outside your work-location scope."; continue; }
            item.ApplicationId = offer.ApplicationId; item.CandidateName = offer.CandidateName;
            if (latest[offer.ApplicationId] != offer.Id) { item.Message = "A newer offer supersedes this letter. Select the current released offer."; continue; }
            if (offer.Status == "Accepted") { item.Status = "Already accepted"; continue; }
            if (offer.Status is not ("Pending Candidate" or "Released")) { item.Message = "Select an offer that has already been released to the candidate."; continue; }
            try
            {
                var (row, error) = await UpdateOfferStatusAsync(id, "Accepted", "Candidate acceptance recorded by HR using selected released offers.", scoped);
                if (row is null) { item.Message = error; continue; }
                item.Status = "Accepted";
                item.Message = "Candidate acceptance recorded by HR. No candidate signature was created.";
            }
            catch (InvalidOperationException error) { item.Message = error.Message; }
        }
        return result;
    }

    public async Task<IReadOnlyList<RecruitmentJoiningCandidate>> GetJoiningCandidatesAsync(int clientId, AuthUser user, bool importReleasedForEss = false)
    {
        var scoped = JoiningUser(user, clientId);
        await using var db = Db(); await db.OpenAsync();
        var permitted = (await OfferRowsAsync(db, scoped, null, null)).GroupBy(row => row.ApplicationId)
            .Select(group => group.MaxBy(row => row.Id)!).Where(row => row.Status is "Accepted" or "Pending Candidate" or "Released").ToDictionary(row => row.Id);
        if (permitted.Count == 0) return [];
        var rows = (await db.QueryAsync<RecruitmentJoiningCandidate>(@"SELECT o.Id OfferId,o.ApplicationId,a.CandidateId,c.CandidateCode,
TRIM(CONCAT_WS(' ',c.FirstName,NULLIF(c.LastName,''))) CandidateName,a.ClientId,client.Name ClientName,
p.PositionTitle,p.JobLocation,p.BranchId WorkLocationId,o.ProposedJoiningDate,o.OfferedCtc,o.Currency,o.Status OfferStatus,
COALESCE(a.JoinedEmployeeId,c.EmployeeId) EmployeeId,COALESCE(employee.EmployeeCode,'') EmployeeCode,
CASE WHEN COALESCE(a.JoinedEmployeeId,c.EmployeeId) IS NOT NULL AND (employee.Id IS NULL OR employee.ClientId<>a.ClientId) THEN 'The existing employee link needs review.'
 WHEN a.CurrentStatus IN ('Rejected','Withdrawn','Offer Rejected','Offer Withdrawn') THEN 'This application is no longer joining.'
 WHEN @ImportReleasedForEss=FALSE AND EXISTS(SELECT 1 FROM recruitment_candidate_checklist_items checklist WHERE checklist.ApplicationId=a.Id AND checklist.Mandatory=TRUE AND checklist.Status<>'Completed') THEN 'Mandatory pre-onboarding items remain incomplete.'
 ELSE '' END Issue
FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId
JOIN recruitment_candidates c ON c.Id=a.CandidateId JOIN recruitment_open_positions p ON p.Id=a.PositionId
JOIN clients client ON client.Id=a.ClientId LEFT JOIN employees employee ON employee.Id=COALESCE(a.JoinedEmployeeId,c.EmployeeId)
WHERE o.Id IN @Ids AND a.ClientId=@ClientId AND o.Status IN ('Accepted','Pending Candidate','Released') ORDER BY o.ProposedJoiningDate,c.FirstName,o.Id", new { Ids = permitted.Keys.ToArray(), ClientId = clientId, ImportReleasedForEss = importReleasedForEss })).ToList();
        foreach (var row in rows)
        {
            row.SourceId = row.OfferId; row.RowKey = "offer:" + row.OfferId;
            row.AcceptanceRecorded = row.OfferStatus == "Accepted";
            if (row.Issue.Length == 0 && row.EmployeeId.HasValue) row.ImportStatus = "Imported";
            else if (row.Issue.Length > 0) row.ImportStatus = "Review";
            else if (!row.AcceptanceRecorded && !importReleasedForEss) row.ImportStatus = "Pending acceptance";
        }
        return await RecruitmentAccessScope.FilterWorkLocationsAsync(db, scoped, rows, row => row.ClientId, row => row.WorkLocationId);
    }

    internal static ConvertCandidateToEmployeeRequest JoiningDefaults(RecruitmentJoiningCandidate row, IReadOnlyList<int> locationIds, IReadOnlyList<long> templateIds, bool importReleasedForEss = false) => new()
    {
        EmployeeCode = row.CandidateCode.Trim(), DateOfJoining = row.ProposedJoiningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        WorkLocationId = locationIds.Count == 1 ? locationIds[0] : 0,
        SalaryStructureId = !importReleasedForEss && templateIds.Count == 1 ? templateIds[0].ToString(CultureInfo.InvariantCulture) : "",
        AnnualCtc = row.OfferedCtc, PortalAccess = importReleasedForEss
    };

    public async Task<RecruitmentJoiningBatchResult> ImportSelectedJoiningCandidatesAsync(RecruitmentJoiningSelection request, AuthUser user)
    {
        var ids = JoiningSelection(request);
        var scoped = JoiningUser(user, request.ClientId);
        if (!RecruitmentJoiningEndpoints.CanImport(scoped)) throw new UnauthorizedAccessException();
        var available = (await GetJoiningCandidatesAsync(request.ClientId, scoped, request.ImportReleasedForEss)).ToDictionary(row => row.OfferId);
        await using var db = Db(); await db.OpenAsync();
        var templates = (await db.QueryAsync<long>("SELECT Id FROM salarystructures WHERE ClientId=@ClientId AND Active=TRUE ORDER BY Id", new { ClientId = request.ClientId })).ToArray();
        var result = new RecruitmentJoiningBatchResult();
        foreach (var id in ids)
        {
            var item = new RecruitmentJoiningItemResult { OfferId = id, SourceId = id, RowKey = "offer:" + id }; result.Items.Add(item);
            if (!available.TryGetValue(id, out var row)) { item.Message = "This accepted offer is unavailable in the selected client or has been superseded."; continue; }
            item.ApplicationId = row.ApplicationId; item.CandidateName = row.CandidateName;
            if (!row.AcceptanceRecorded && !request.ImportReleasedForEss) { item.Message = "Record candidate acceptance before importing this offer."; continue; }
            if (row.ImportStatus == "Imported") { item.Status = "Already imported"; item.EmployeeId = row.EmployeeId; item.EmployeeCode = row.EmployeeCode; continue; }
            if (row.ImportStatus == "Review") { item.Message = row.Issue; continue; }
            try
            {
                var locations = row.WorkLocationId > 0
                    ? (await db.QueryAsync<int>("SELECT Id FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND Id=@Id", new { ClientId = request.ClientId, Id = row.WorkLocationId })).ToArray()
                    : string.IsNullOrWhiteSpace(row.JobLocation) ? [] : (await db.QueryAsync<int>("SELECT Id FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND (Name=@Location OR City=@Location) ORDER BY Id", new { ClientId = request.ClientId, Location = row.JobLocation })).ToArray();
                if (row.WorkLocationId > 0 && locations.Length == 0) { item.Message = "The joining work location is unavailable or inactive in this client."; continue; }
                var (employee, error) = await ConvertToEmployeeAsync(row.ApplicationId, JoiningDefaults(row, locations, templates, request.ImportReleasedForEss), scoped, request.ImportReleasedForEss, id);
                if (employee is null) { item.Message = error; continue; }
                item.Status = "Imported"; item.EmployeeId = employee.Id; item.EmployeeCode = employee.EmployeeCode;
            }
            catch (InvalidOperationException error) { item.Message = error.Message; }
            catch (MySqlException error) when (error.Number is 1062 or 1452) { item.Message = "The employee identity or required configuration conflicts with an existing record. Review this candidate before importing."; }
        }
        return result;
    }
}
