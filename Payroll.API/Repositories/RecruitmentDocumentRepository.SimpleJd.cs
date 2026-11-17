using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    // A role guide for an existing employee list is deliberately separate from
    // requisition-governed hiring JDs and their approval/eligibility contracts.
    private static Task EnsureSimpleJdSchemaAsync(MySqlConnection db) => db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS recruitment_simple_job_descriptions (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,TitleKey CHAR(64) NOT NULL,
 Title VARCHAR(180) NOT NULL,ContentJson JSON NOT NULL,CreatedByUserId INT NOT NULL,
 CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 UNIQUE KEY UX_simple_jd_client_role(ClientId,TitleKey));
CREATE TABLE IF NOT EXISTS recruitment_document_roster_jds (
 RosterId BIGINT PRIMARY KEY,ClientId INT NOT NULL,TitleKey CHAR(64) NOT NULL,
 SimpleJdId BIGINT NULL,ExistingJobDescriptionId BIGINT NULL,LinkedByUserId INT NOT NULL,
 LinkedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 INDEX IX_roster_jd_client(ClientId),INDEX IX_roster_jd_simple(SimpleJdId));
""");

    private static Task<bool> SimpleJdTableExistsAsync(MySqlConnection db, string name, MySqlTransaction? tx = null) => db.ExecuteScalarAsync<bool>(
        "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@Name)", new { Name = name }, tx);

    private static Task<bool> RosterHasSimpleJdLocationAsync(MySqlConnection db, MySqlTransaction? tx = null) => db.ExecuteScalarAsync<bool>(
        "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='recruitment_document_roster' AND column_name='WorkLocationId')", transaction: tx);

    internal static void RequireSimpleJdWriteAccess(AuthUser user, int clientId)
    {
        Access(user, clientId);
        if (!RecruitmentPermissions.Has(user, "recruitment.candidate.manage", "recruitment.offer.manage"))
            throw new UnauthorizedAccessException("Employee list editing permission is required to create a Simple JD.");
    }

    internal static string ResolveSimpleJdTitle(string? savedTitle, string? requestedTitle)
    {
        var saved = (savedTitle ?? "").Trim();
        var requested = (requestedTitle ?? "").Trim();
        if (saved.Length > 0)
        {
            if (requested.Length > 0 && RecruitmentSimpleJobDescription.NormalizeTitleKey(saved)
                != RecruitmentSimpleJobDescription.NormalizeTitleKey(requested))
                throw new InvalidOperationException("The position name changed. Refresh the employee list before creating its JD.");
            return RecruitmentSimpleJobDescription.Generate(saved).Title;
        }
        return RecruitmentSimpleJobDescription.Generate(requested).Title;
    }

    internal static bool SimpleJdLinkMatchesTitle(string? savedTitle, string? linkedTitleKey, string? contentTitle)
    {
        try
        {
            var key = Hash(RecruitmentSimpleJobDescription.NormalizeTitleKey(savedTitle!));
            return key == linkedTitleKey && key == Hash(RecruitmentSimpleJobDescription.NormalizeTitleKey(contentTitle!));
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool CanUseExistingSimpleJd(AuthUser user, int? rosterLocation, int? nativeLocation,
        string status, List<WorkLocation> locations) => RecruitmentPermissions.CanViewJobDescriptions(user)
        && status == "Approved" && rosterLocation == nativeLocation
        && CanSeeLocation(user, nativeLocation, locations)
        && (nativeLocation is null || locations.Any(location => location.Id == nativeLocation && location.IsActive));

    internal static void ValidateSimpleJdSelection(IReadOnlyCollection<long>? ids, IReadOnlyDictionary<string, string>? revisions)
    {
        if (ids is null || ids.Count is < 1 or > 200 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw new InvalidOperationException("Select 1 to 200 distinct employee list rows.");
        if (revisions is null || ids.Any(id => !revisions.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var revision) || string.IsNullOrWhiteSpace(revision)))
            throw new InvalidOperationException("Refresh the employee list before creating the selected JDs.");
    }

    public async Task<List<RecruitmentRosterSimpleJdLink>> SimpleJdLinksAsync(int clientId, AuthUser user)
    {
        Access(user, clientId);
        await using var db = Db(); await db.OpenAsync();
        return (await ReadVisibleSimpleJdLinksAsync(db, clientId, user)).Select(link =>
            new RecruitmentRosterSimpleJdLink(link.RosterId, link.SimpleJdId ?? link.ExistingJobDescriptionId!.Value, link.Source, link.Title)).ToList();
    }

    private static async Task<List<SimpleJdStoredLink>> ReadVisibleSimpleJdLinksAsync(MySqlConnection db, int clientId, AuthUser user, MySqlTransaction? tx = null)
    {
        if (!await SimpleJdTableExistsAsync(db, "recruitment_document_roster_jds", tx)
            || !await SimpleJdTableExistsAsync(db, "recruitment_simple_job_descriptions", tx)) return [];
        var locationColumn = await RosterHasSimpleJdLocationAsync(db, tx) ? "r.WorkLocationId" : "NULL";
        var links = await db.QueryAsync<SimpleJdStoredLink>($"""
SELECT link.RosterId,link.ClientId,link.TitleKey,link.SimpleJdId,link.ExistingJobDescriptionId,
 {locationColumn} WorkLocationId,CAST(r.ValuesJson AS CHAR) ValuesJson,
 NULLIF(nativeRequest.BranchId,0) NativeWorkLocationId,(nativeRequest.Id IS NOT NULL) NativeRequestAvailable,COALESCE(native.Status,'') NativeStatus,
 COALESCE(simple.Title,native.Title,'') Title,CASE WHEN link.SimpleJdId IS NOT NULL THEN 'Simple' ELSE 'Existing' END Source
FROM recruitment_document_roster_jds link
JOIN recruitment_document_roster r ON r.Id=link.RosterId AND r.ClientId=link.ClientId
JOIN recruitment_candidates candidate ON candidate.Id=r.CandidateId AND candidate.ClientId=r.ClientId
LEFT JOIN recruitment_simple_job_descriptions simple ON simple.Id=link.SimpleJdId AND simple.ClientId=link.ClientId
LEFT JOIN recruitment_job_description_versions native ON native.Id=link.ExistingJobDescriptionId AND native.ClientId=link.ClientId
LEFT JOIN recruitment_requisitions nativeRequest ON nativeRequest.Id=native.RequisitionId AND nativeRequest.ClientId=link.ClientId
WHERE link.ClientId=@ClientId ORDER BY link.RosterId
""", new { ClientId = clientId }, tx);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId, tx);
        return links.Where(link => CanSeeLocation(user, link.WorkLocationId, locations)
            && (link.Source == "Simple" || link.NativeRequestAvailable && CanUseExistingSimpleJd(user, link.WorkLocationId, link.NativeWorkLocationId, link.NativeStatus, locations))
            && SimpleJdLinkMatchesTitle(ReadValues(link.ValuesJson).GetValueOrDefault("positionTitle"), link.TitleKey, link.Title))
            .ToList();
    }

    public async Task<RecruitmentRosterSimpleJd?> SimpleJdAsync(int clientId, long rosterId, AuthUser user)
    {
        Access(user, clientId);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var link = (await ReadVisibleSimpleJdLinksAsync(db, clientId, user, tx)).FirstOrDefault(row => row.RosterId == rosterId);
        if (link is null) return null;
        var description = await LoadSimpleJdAsync(db, clientId, (link.SimpleJdId ?? link.ExistingJobDescriptionId)!.Value, link.Source, tx);
        await tx.CommitAsync();
        return description is not null && SimpleJdLinkMatchesTitle(ReadValues(link.ValuesJson).GetValueOrDefault("positionTitle"), link.TitleKey, description.Title) ? description : null;
    }

    public async Task<RecruitmentRosterSimpleJdCreated> CreateSimpleJdAsync(int clientId, long rosterId, CreateRecruitmentRosterSimpleJd request, AuthUser user)
    {
        RequireSimpleJdWriteAccess(user, clientId);
        var revisions = new Dictionary<string, string> { [rosterId.ToString(CultureInfo.InvariantCulture)] = request.Revision };
        var result = await SaveSimpleJdsAsync(clientId, [rosterId], revisions, new Dictionary<long, string> { [rosterId] = request.PositionTitle }, user);
        return new(result.Descriptions[rosterId], [rosterId], result.Created > 0);
    }

    public async Task<RecruitmentRosterSimpleJdsCreated> CreateSimpleJdsAsync(int clientId, CreateRecruitmentRosterSimpleJds request, AuthUser user)
    {
        RequireSimpleJdWriteAccess(user, clientId);
        var result = await SaveSimpleJdsAsync(clientId, request.Ids, request.Revisions, new Dictionary<long, string>(), user);
        return new(result.Created, result.Reused, result.Descriptions.Count);
    }

    private async Task<SimpleJdSaveResult> SaveSimpleJdsAsync(int clientId, List<long> ids, Dictionary<string, string> revisions,
        Dictionary<long, string> requestedTitles, AuthUser user)
    {
        ValidateSimpleJdSelection(ids, revisions);
        await using var db = Db(); await db.OpenAsync();
        if (!await SimpleJdTableExistsAsync(db, "recruitment_document_roster")) throw new InvalidOperationException("The employee list row was not found.");
        var hasLocation = await RosterHasSimpleJdLocationAsync(db);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        var initial = await ReadSimpleJdRosterRowsAsync(db, clientId, ids, hasLocation);
        ValidateSimpleJdRows(initial, ids, revisions, requestedTitles, user, locations);
        await EnsureSimpleJdSchemaAsync(db);

        await using var tx = await db.BeginTransactionAsync();
        var rows = await ReadSimpleJdRosterRowsAsync(db, clientId, ids, hasLocation, tx);
        locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId, tx);
        var titles = ValidateSimpleJdRows(rows, ids, revisions, requestedTitles, user, locations);
        var priorLinks = (await db.QueryAsync<SimpleJdStoredLink>("SELECT RosterId,ClientId,TitleKey,SimpleJdId,ExistingJobDescriptionId FROM recruitment_document_roster_jds WHERE RosterId IN @Ids FOR UPDATE", new { Ids = ids }, tx)).ToDictionary(link => link.RosterId);
        var savedRoles = (await db.QueryAsync<SimpleJdStoredRole>("SELECT Id,ClientId,TitleKey,Title,CAST(ContentJson AS CHAR) ContentJson FROM recruitment_simple_job_descriptions WHERE ClientId=@ClientId", new { ClientId = clientId }, tx))
            .ToDictionary(role => role.TitleKey, StringComparer.Ordinal);
        var nativeRoles = RecruitmentPermissions.CanViewJobDescriptions(user)
            ? (await db.QueryAsync<SimpleJdNativeRole>("""
SELECT j.Id,j.Title,NULLIF(r.BranchId,0) WorkLocationId FROM recruitment_job_description_versions j
JOIN recruitment_requisitions r ON r.Id=j.RequisitionId AND r.ClientId=j.ClientId
WHERE j.ClientId=@ClientId AND j.Status='Approved'
AND NOT EXISTS(SELECT 1 FROM recruitment_job_description_versions newer WHERE newer.RequisitionId=j.RequisitionId
 AND newer.Status='Approved' AND (newer.VersionNumber>j.VersionNumber OR (newer.VersionNumber=j.VersionNumber AND newer.Id>j.Id)))
""", new { ClientId = clientId }, tx)).Where(role => CanSeeLocation(user, role.WorkLocationId, locations)).ToList()
            : [];
        var created = new HashSet<string>(StringComparer.Ordinal);
        var reused = new HashSet<string>(StringComparer.Ordinal);
        var loadedDescriptions = new Dictionary<string, RecruitmentRosterSimpleJd>(StringComparer.Ordinal);
        var result = new SimpleJdSaveResult();
        foreach (var row in rows)
        {
            var title = titles[row.Id];
            var roleKey = Hash(RecruitmentSimpleJobDescription.NormalizeTitleKey(title));
            long? simpleId = null, nativeId = null;
            string source;
            if (savedRoles.TryGetValue(roleKey, out var savedRole)) { simpleId = savedRole.Id; source = "Simple"; }
            else
            {
                var matches = nativeRoles.Where(role => CanUseExistingSimpleJd(user, row.WorkLocationId, role.WorkLocationId, "Approved", locations)
                    && SimpleJdLinkMatchesTitle(title, roleKey, role.Title)).ToList();
                if (matches.Count == 1) { nativeId = matches[0].Id; source = "Existing"; }
                else
                {
                    var content = RecruitmentSimpleJobDescription.Generate(title);
                    var affected = await db.ExecuteAsync("""
INSERT IGNORE INTO recruitment_simple_job_descriptions(ClientId,TitleKey,Title,ContentJson,CreatedByUserId,CreatedAtUtc)
VALUES(@ClientId,@TitleKey,@Title,@ContentJson,@UserId,UTC_TIMESTAMP(6))
""", new { ClientId = clientId, TitleKey = roleKey, content.Title, ContentJson = JsonSerializer.Serialize(content, Json), UserId = user.Id }, tx);
                    // A locking read sees a concurrent winner even when this
                    // transaction's earlier repeatable-read snapshot did not.
                    savedRole = await db.QuerySingleAsync<SimpleJdStoredRole>("SELECT Id,ClientId,TitleKey,Title,CAST(ContentJson AS CHAR) ContentJson FROM recruitment_simple_job_descriptions WHERE ClientId=@ClientId AND TitleKey=@TitleKey FOR UPDATE", new { ClientId = clientId, TitleKey = roleKey }, tx);
                    savedRoles[roleKey] = savedRole; simpleId = savedRole.Id; source = "Simple";
                    if (affected > 0) created.Add("Simple:" + simpleId);
                }
            }
            var descriptionKey = source + ":" + (simpleId ?? nativeId);
            if (!created.Contains(descriptionKey)) reused.Add(descriptionKey);
            var values = ReadValues(row.ValuesJson);
            if (string.IsNullOrWhiteSpace(values.GetValueOrDefault("positionTitle")))
            {
                values["positionTitle"] = title;
                var serialized = SerializeValues(values);
                await db.ExecuteAsync("UPDATE recruitment_document_roster SET ValuesJson=@Values,UpdatedByUserId=@UserId,UpdatedAt=CURRENT_TIMESTAMP(6) WHERE Id=@Id AND ClientId=@ClientId", new { Values = serialized, UserId = user.Id, row.Id, ClientId = clientId }, tx);
                await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Simple JD position',@Old,@New,@UserId)", new { row.Id, ClientId = clientId, Old = row.ValuesJson, New = serialized, UserId = user.Id }, tx);
            }
            var previous = priorLinks.GetValueOrDefault(row.Id);
            var linkChanged = previous is null || previous.ClientId != clientId || previous.TitleKey != roleKey
                || previous.SimpleJdId != simpleId || previous.ExistingJobDescriptionId != nativeId;
            if (linkChanged)
            {
                await db.ExecuteAsync("""
INSERT INTO recruitment_document_roster_jds(RosterId,ClientId,TitleKey,SimpleJdId,ExistingJobDescriptionId,LinkedByUserId,LinkedAtUtc)
VALUES(@RosterId,@ClientId,@TitleKey,@SimpleJdId,@ExistingJobDescriptionId,@UserId,UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE ClientId=VALUES(ClientId),TitleKey=VALUES(TitleKey),SimpleJdId=VALUES(SimpleJdId),ExistingJobDescriptionId=VALUES(ExistingJobDescriptionId),LinkedByUserId=VALUES(LinkedByUserId),LinkedAtUtc=UTC_TIMESTAMP(6)
""", new { RosterId = row.Id, ClientId = clientId, TitleKey = roleKey, SimpleJdId = simpleId, ExistingJobDescriptionId = nativeId, UserId = user.Id }, tx);
                await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Simple JD linked',@Old,@New,@UserId)", new
                {
                    row.Id, ClientId = clientId, Old = previous is null ? null : JsonSerializer.Serialize(new { previous.SimpleJdId, previous.ExistingJobDescriptionId, previous.TitleKey }, Json),
                    New = JsonSerializer.Serialize(new { SimpleJdId = simpleId, ExistingJobDescriptionId = nativeId, TitleKey = roleKey }, Json), UserId = user.Id
                }, tx);
            }
            if (!loadedDescriptions.TryGetValue(descriptionKey, out var description))
            {
                description = await LoadSimpleJdAsync(db, clientId, (simpleId ?? nativeId)!.Value, source, tx)
                    ?? throw new InvalidOperationException("The linked job description could not be loaded.");
                loadedDescriptions[descriptionKey] = description;
            }
            result.Descriptions[row.Id] = description;
        }
        result.Created = created.Count; result.Reused = reused.Count;
        await tx.CommitAsync();
        return result;
    }

    private static async Task<List<SimpleJdRosterRow>> ReadSimpleJdRosterRowsAsync(MySqlConnection db, int clientId, List<long> ids, bool hasLocation, MySqlTransaction? tx = null) =>
        (await db.QueryAsync<SimpleJdRosterRow>($"""
SELECT r.Id,{(hasLocation ? "r.WorkLocationId" : "NULL")} WorkLocationId,CAST(r.ValuesJson AS CHAR) ValuesJson
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
WHERE r.ClientId=@ClientId AND r.Id IN @Ids ORDER BY r.Id{(tx is null ? "" : " FOR UPDATE")}
""", new { ClientId = clientId, Ids = ids }, tx)).ToList();

    internal static Dictionary<long, string> ValidateSimpleJdRows(List<SimpleJdRosterRow> rows, List<long> ids,
        Dictionary<string, string> revisions, Dictionary<long, string> requestedTitles, AuthUser user, List<WorkLocation> locations)
    {
        if (rows.Count != ids.Count || rows.Any(row => !CanSeeLocation(user, row.WorkLocationId, locations))) throw new UnauthorizedAccessException("An employee list row is outside your permitted scope.");
        var titles = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            if (RowRevision(row.ValuesJson, row.WorkLocationId) != revisions[row.Id.ToString(CultureInfo.InvariantCulture)])
                throw new InvalidOperationException("An employee list row changed. Refresh before creating its JD.");
            titles[row.Id] = ResolveSimpleJdTitle(ReadValues(row.ValuesJson).GetValueOrDefault("positionTitle"), requestedTitles.GetValueOrDefault(row.Id));
        }
        return titles;
    }

    private static async Task<RecruitmentRosterSimpleJd?> LoadSimpleJdAsync(MySqlConnection db, int clientId, long id, string source, MySqlTransaction? tx = null)
    {
        if (source == "Simple")
        {
            var json = await db.QuerySingleOrDefaultAsync<string>("SELECT CAST(ContentJson AS CHAR) FROM recruitment_simple_job_descriptions WHERE Id=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }, tx);
            if (json is null) return null;
            var content = JsonSerializer.Deserialize<SimpleJobDescriptionContent>(json, Json) ?? throw new InvalidOperationException("The Simple JD content is unavailable.");
            return new() { Id = id, ClientId = clientId, Source = "Simple", Status = "Ready", Title = content.Title, Summary = content.Summary, RolePurpose = content.RolePurpose, Responsibilities = content.Responsibilities, Skills = content.Skills };
        }
        var native = await db.QuerySingleOrDefaultAsync<RecruitmentRosterSimpleJd>("SELECT Id,ClientId,Title,Summary,RolePurpose,Status FROM recruitment_job_description_versions WHERE Id=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }, tx);
        if (native is null) return null;
        native.Source = "Existing";
        native.Responsibilities = (await db.QueryAsync<string>("SELECT ResponsibilityText FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id }, tx)).ToList();
        native.Skills = (await db.QueryAsync<string>("SELECT SkillName FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id }, tx)).ToList();
        native.Qualifications = (await db.QueryAsync<string>("SELECT TRIM(CONCAT_WS(' ',QualificationName,NULLIF(Specialization,''))) FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id }, tx)).ToList();
        native.Certifications = (await db.QueryAsync<string>("SELECT CertificationName FROM recruitment_jd_certification_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id }, tx)).ToList();
        native.Languages = (await db.QueryAsync<string>("SELECT LanguageName FROM recruitment_jd_language_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id }, tx)).ToList();
        native.Benefits = (await db.QueryAsync<string>("SELECT BenefitName FROM recruitment_jd_benefits WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id }, tx)).ToList();
        return native;
    }

    internal sealed class SimpleJdRosterRow { public long Id { get; set; } public int? WorkLocationId { get; set; } public string ValuesJson { get; set; } = "{}"; }
    private sealed class SimpleJdStoredRole { public long Id { get; set; } public int ClientId { get; set; } public string TitleKey { get; set; } = ""; public string Title { get; set; } = ""; public string ContentJson { get; set; } = "{}"; }
    private sealed class SimpleJdNativeRole { public long Id { get; set; } public string Title { get; set; } = ""; public int? WorkLocationId { get; set; } }
    private sealed class SimpleJdStoredLink { public long RosterId { get; set; } public int ClientId { get; set; } public string TitleKey { get; set; } = ""; public long? SimpleJdId { get; set; } public long? ExistingJobDescriptionId { get; set; } public int? WorkLocationId { get; set; } public int? NativeWorkLocationId { get; set; } public bool NativeRequestAvailable { get; set; } public string NativeStatus { get; set; } = ""; public string ValuesJson { get; set; } = "{}"; public string Title { get; set; } = ""; public string Source { get; set; } = "Simple"; }
    private sealed class SimpleJdSaveResult { public int Created { get; set; } public int Reused { get; set; } public Dictionary<long, RecruitmentRosterSimpleJd> Descriptions { get; } = []; }
}
