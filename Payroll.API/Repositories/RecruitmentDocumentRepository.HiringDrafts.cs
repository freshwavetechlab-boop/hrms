using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private static Task EnsureHiringDraftSchemaAsync(MySqlConnection db) => db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS recruitment_roster_jd_hiring_drafts (
 ClientId INT NOT NULL,TitleKey CHAR(64) NOT NULL,WorkLocationKey INT NOT NULL,
 SimpleJdId BIGINT NOT NULL,RequisitionId BIGINT NOT NULL,JobDescriptionVersionId BIGINT NOT NULL,
 CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 PRIMARY KEY(ClientId,TitleKey,WorkLocationKey),UNIQUE KEY UX_roster_hiring_request(RequisitionId));
""");

    private static async Task EnsureHiringDraftAsync(IConfiguration configuration, MySqlConnection db, MySqlTransaction tx, int clientId,
        long simpleId, string titleKey, SimpleJobDescriptionContent content, int? workLocationId, int actorId)
    {
        var key = workLocationId ?? 0;
        var existing = await db.QuerySingleOrDefaultAsync<HiringDraftLink>("""
SELECT link.RequisitionId,link.JobDescriptionVersionId
FROM recruitment_roster_jd_hiring_drafts link
JOIN recruitment_requisitions r ON r.Id=link.RequisitionId AND r.ClientId=link.ClientId
JOIN recruitment_job_description_versions jd ON jd.Id=link.JobDescriptionVersionId AND jd.RequisitionId=r.Id AND jd.ClientId=r.ClientId
WHERE link.ClientId=@ClientId AND link.TitleKey=@TitleKey AND link.WorkLocationKey=@Key FOR UPDATE
""", new { ClientId = clientId, TitleKey = titleKey, Key = key }, tx);
        if (existing is not null) return; // Never overwrite an edited/approved role.
        var location = workLocationId.HasValue ? await db.QuerySingleOrDefaultAsync<WorkLocation>(
            "SELECT * FROM worklocations WHERE Id=@Id AND ClientId=@ClientId", new { Id = workLocationId, ClientId = clientId }, tx) : null;
        if (workLocationId.HasValue && location is null) throw new InvalidOperationException("The employee-list work location is unavailable for its hiring draft.");
        var textLocation = location?.Name ?? "";
        if (textLocation.Length > 160) textLocation = string.Join(", ", new[] { location!.City, location.State }.Where(text => !string.IsNullOrWhiteSpace(text)));
        // Reserve through the existing non-reusable RFR sequence, outside the
        // caller's transaction, exactly as the normal draft flow does.
        await using var numbers = new MySqlConnection(configuration.GetConnectionString("Default")); await numbers.OpenAsync();
        var number = await RecruitmentRepository.NextRfrNumberAsync(numbers, clientId);
        var requestId = await RecruitmentRepository.InsertRosterHiringDraftAsync(db, tx, clientId, workLocationId,
            textLocation, $"roster-jd:{simpleId}:location:{key}", number, content, actorId);
        var descriptionId = await RecruitmentPipelineRepository.InsertRosterDescriptionDraftAsync(db, tx, clientId, requestId, content, actorId);
        await db.ExecuteAsync("""
INSERT INTO recruitment_roster_jd_hiring_drafts(ClientId,TitleKey,WorkLocationKey,SimpleJdId,RequisitionId,JobDescriptionVersionId)
VALUES(@ClientId,@TitleKey,@Key,@SimpleId,@RequestId,@DescriptionId)
ON DUPLICATE KEY UPDATE SimpleJdId=VALUES(SimpleJdId),RequisitionId=VALUES(RequisitionId),JobDescriptionVersionId=VALUES(JobDescriptionVersionId)
""", new { ClientId = clientId, TitleKey = titleKey, Key = key, SimpleId = simpleId, RequestId = requestId, DescriptionId = descriptionId }, tx);
    }

    // Bootstrap upgrades already-created Simple JDs once, using their original
    // owner and location. The durable mapping makes subsequent starts no-ops.
    public Task InitializeHiringDraftsAsync() => MigrateHiringDraftsAsync(configuration);

    public static async Task MigrateHiringDraftsAsync(IConfiguration configuration)
    {
        await using var db = new MySqlConnection(configuration.GetConnectionString("Default")); await db.OpenAsync();
        if (!await SimpleJdTableExistsAsync(db, "recruitment_document_roster_jds")
            || !await SimpleJdTableExistsAsync(db, "recruitment_simple_job_descriptions")) return;
        await EnsureHiringDraftSchemaAsync(db);
        var sources = (await db.QueryAsync<ExistingSimpleDraft>("""
SELECT DISTINCT simple.Id,simple.ClientId,simple.TitleKey,CAST(simple.ContentJson AS CHAR) ContentJson,
 r.WorkLocationId,simple.CreatedByUserId
FROM recruitment_simple_job_descriptions simple
JOIN recruitment_document_roster_jds link ON link.SimpleJdId=simple.Id AND link.ClientId=simple.ClientId
JOIN recruitment_document_roster r ON r.Id=link.RosterId AND r.ClientId=link.ClientId
JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
WHERE c.ProfileStatus<>'Archived' AND NOT EXISTS(
 SELECT 1 FROM recruitment_roster_jd_hiring_drafts saved
 WHERE saved.ClientId=simple.ClientId AND saved.TitleKey=simple.TitleKey AND saved.WorkLocationKey=COALESCE(r.WorkLocationId,0))
ORDER BY simple.ClientId,simple.Id,r.WorkLocationId
""")).ToList();
        foreach (var source in sources)
        {
            var content = JsonSerializer.Deserialize<SimpleJobDescriptionContent>(source.ContentJson, Json)
                ?? throw new InvalidOperationException("An existing Simple JD has invalid content.");
            await using var tx = await db.BeginTransactionAsync();
            await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = source.ClientId }, tx);
            await EnsureHiringDraftAsync(configuration, db, tx, source.ClientId, source.Id, source.TitleKey, content, source.WorkLocationId, source.CreatedByUserId);
            await tx.CommitAsync();
        }
    }

    private sealed class HiringDraftLink { public long RequisitionId { get; set; } public long JobDescriptionVersionId { get; set; } }
    private sealed class ExistingSimpleDraft {
        public long Id { get; set; } public int ClientId { get; set; } public string TitleKey { get; set; } = "";
        public string ContentJson { get; set; } = "{}"; public int? WorkLocationId { get; set; } public int CreatedByUserId { get; set; }
    }
}
