using System.Data;
using System.Globalization;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    // Rendering is a read-only check. It never initializes schema, allocates an
    // offer number, stores a PDF/receipt or queues a message. Release still checks
    // everything again inside its own write transaction.
    public async Task<RecruitmentRosterReleasePreflight> PreflightReleaseAsync(int clientId, RecruitmentRosterReleaseSelection request, AuthUser user)
    {
        IssueAccess(user, clientId);
        ValidateIds(request.Ids);
        if (request.Revisions is { Count: > 200 }) throw new InvalidOperationException("Select fewer rows to release.");
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync("SET TRANSACTION READ ONLY");
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var allowed = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId, tx);
        var rows = (await db.QueryAsync<RecruitmentDocumentRosterRow>("""
SELECT r.*,CAST(r.ValuesJson AS CHAR) ValuesJson,c.CandidateCode,TRIM(CONCAT(c.FirstName,' ',c.LastName)) CandidateName,c.Email,c.Phone,COALESCE(r.EmployeeId,c.EmployeeId) EmployeeId
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId AND c.ProfileStatus<>'Archived'
WHERE r.ClientId=@ClientId AND r.Id IN @Ids ORDER BY r.Id
""", new { ClientId = clientId, request.Ids }, tx)).ToList();
        if (rows.Count != request.Ids.Count) throw new UnauthorizedAccessException("One or more selected employee rows are unavailable in your permitted locations.");

        await RecruitmentEmployeeMasterSync.ApplyAsync(db, rows, tx);
        if (rows.Any(row => !CanSeeLocation(user, row.WorkLocationId, allowed)))
            throw new UnauthorizedAccessException("One or more selected employee rows are unavailable in your permitted locations.");
        var client = await db.QuerySingleOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { ClientId = clientId }, tx)
            ?? throw new InvalidOperationException("Select an active client before releasing offer letters.");
        var organization = await db.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1", transaction: tx);
        var logo = LogoBytes(organization?.LogoDataUrl);
        var asset = request.ApplySignature
            ? await db.QuerySingleOrDefaultAsync<SigningStored>("SELECT ImageBytes Bytes,Sha256 FROM recruitment_document_signing_assets WHERE ClientId=@ClientId", new { ClientId = clientId }, tx)
            : null;
        var result = new RecruitmentRosterReleasePreflight { CheckedAtUtc = DateTime.UtcNow };
        if (request.ApplySignature && asset is null)
            result.Issues.Add("Upload this client's signature and stamp image before applying it, or turn Apply signature off.");
        if (asset is not null)
        {
            try { DocumentSigningImage.Validate(asset.Bytes); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidDataException)
            { result.Issues.Add(error.Message); }
        }
        var india = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(new DateTimeOffset(result.CheckedAtUtc, TimeSpan.Zero), OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
        long bytes = 0;
        foreach (var row in rows)
        {
            var check = new RecruitmentRosterReleaseCheck
            {
                RosterId = row.Id, ProfileCode = row.CandidateCode, TemplateId = row.TemplateId,
                Revision = RowRevision(row.ValuesJson, row.WorkLocationId)
            };
            result.Items.Add(check);
            if (request.Revisions is not null && (!request.Revisions.TryGetValue(row.Id.ToString(CultureInfo.InvariantCulture), out var expected) || expected != check.Revision))
                check.Issues.Add("This row changed. Refresh and review its current details before release.");
            if (row.WorkLocationId is null or <= 0)
                check.Issues.Add("Link an existing work location before releasing this offer letter. An unsigned draft can still be downloaded.");
            try
            {
                var template = await TemplateAsync(db, clientId, row.TemplateId, tx);
                check.TemplateName = template.TemplateName;
                if (check.Issues.Count > 0 || result.Issues.Count > 0) continue;
                // Use the same location overlay and employee projection as release;
                // no locking read is required for this consistent read snapshot.
                var location = await db.QuerySingleOrDefaultAsync<WorkLocation>("SELECT * FROM worklocations WHERE Id=@Id AND ClientId=@ClientId AND IsActive=TRUE", new { Id = row.WorkLocationId, ClientId = clientId }, tx)
                    ?? throw new InvalidOperationException("Select an existing active work location belonging to this client.");
                var values = ReadValues(row.ValuesJson);
                ApplyLocationValues(values, location);
                var offerNumber = row.OfferNumber.Length > 0 ? row.OfferNumber : NewOfferNumber(client.Code, india.Year, row.Id);
                PrepareReleaseValues(row, values, client.Name, organization?.Name ?? "", offerNumber);
                values["offerDate"] = india.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                var rendered = pdf.Create(template.BodyTemplate, values, logo, draft: false, signatureAndStamp: asset?.Bytes);
                if (rendered.Bytes is null) check.Issues.Add(rendered.Error);
                else if (rendered.Bytes.Length > 10 * 1024 * 1024) check.Issues.Add("The released PDF would exceed 10 MB. Reduce the signature image size.");
                else bytes += rendered.Bytes.Length;
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidDataException)
            { check.Issues.Add(error.Message); }
        }
        if (bytes > 32 * 1024 * 1024) result.Issues.Add("Release fewer letters at a time (32 MB limit).");
        await tx.CommitAsync();
        return result;
    }
}
