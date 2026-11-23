using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private const string DocumentLayoutMigrationKey = "document_layout_flow_v2";
    private static readonly Regex GeneratedFlowTemplateCode = new("^DOC_LAYOUT_FLOW_V2_[0-9]+_[a-f0-9]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex LegacySignatureText = new(@"^\[\s*Signature\s*(?:&|and)\s*Stamp(?:\s+of\s+[^\]\r\n]{1,100})?\s*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // Explicit owner-run configuration migration only. No page, save or startup
    // calls this routine. Existing issued letters retain their original template.
    public async Task<DocumentLayoutMigrationResult> MigrateDocumentLayoutsAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        return await MigrateDocumentLayoutsAsync(db);
    }

    internal static async Task<DocumentLayoutMigrationResult> MigrateDocumentLayoutsAsync(MySqlConnection db)
    {
        await using var tx = await db.BeginTransactionAsync();
        // Release locks its client before its rows/template. Use the same order
        // across the configured active clients, including users of global templates.
        await db.QueryAsync<int>("SELECT Id FROM clients WHERE IsActive=TRUE ORDER BY Id FOR UPDATE", transaction: tx);
        var sources = (await db.QueryAsync<RecruitmentTemplate>("""
SELECT t.* FROM recruitment_templates t
WHERE t.IsActive=TRUE AND t.TemplateType='Offer Letter'
 AND (t.ClientId=0 OR EXISTS(SELECT 1 FROM clients c WHERE c.Id=t.ClientId AND c.IsActive=TRUE))
ORDER BY t.Id FOR UPDATE
""", transaction: tx)).ToList();
        var result = new DocumentLayoutMigrationResult();
        foreach (var source in sources)
        {
            if (GeneratedFlowTemplateCode.IsMatch(source.TemplateCode)) continue;
            if (!TryBuildDocumentFlowLayout(source.BodyTemplate, out var corrected))
            {
                result.SkippedTemplates++;
                continue;
            }
            var sourceHash = Hash(source.BodyTemplate);
            var correctedHash = Hash(corrected);
            var code = $"DOC_LAYOUT_FLOW_V2_{source.Id}_{sourceHash[..16]}";
            var clone = await db.QuerySingleOrDefaultAsync<RecruitmentTemplate>(
                "SELECT * FROM recruitment_templates WHERE ClientId=@ClientId AND TemplateCode=@Code FOR UPDATE", new { source.ClientId, Code = code }, tx);
            var created = clone is null;
            if (clone is not null && (!clone.IsActive || clone.IsHtml || clone.TemplateType != source.TemplateType
                || clone.SubjectTemplate != source.SubjectTemplate || Hash(clone.BodyTemplate) != correctedHash))
                throw new InvalidOperationException($"Flexible document template version for source template {source.Id} has changed. Review its configuration before retrying; no migration changes were committed.");
            var templateId = clone?.Id ?? await db.ExecuteScalarAsync<int>("""
INSERT INTO recruitment_templates(ClientId,TemplateType,TemplateCode,TemplateName,SubjectTemplate,BodyTemplate,IsHtml,IsActive)
VALUES(@ClientId,@TemplateType,@Code,@Name,@SubjectTemplate,@Body,FALSE,TRUE);SELECT LAST_INSERT_ID();
""", new { source.ClientId, source.TemplateType, Code = code, Name = $"Offer letter - flexible text v2 ({source.Id}/{sourceHash[..8]})", source.SubjectTemplate, Body = corrected }, tx);
            if (created)
            {
                result.CreatedTemplates++;
                await db.ExecuteAsync("""
INSERT INTO recruitment_admin_audit(EntityType,EntityId,Action,OldValueJson,NewValueJson,ChangedByUserId)
VALUES('RecruitmentTemplate',@Id,'CreateSimilar',@Old,@New,NULL)
""", new { Id = templateId,
                    Old = JsonSerializer.Serialize(new { Migration = DocumentLayoutMigrationKey, SourceTemplateId = source.Id, SourceTemplateHash = sourceHash }),
                    New = JsonSerializer.Serialize(new { Migration = DocumentLayoutMigrationKey, SourceTemplateId = source.Id, TemplateId = templateId, TemplateHash = correctedHash, source.ClientId }) }, tx);
            }
            var rows = (await db.QueryAsync<LayoutMigrationTarget>("""
SELECT r.Id,r.ClientId,r.CandidateId,r.TemplateId,r.TemplateHash,r.WorkLocationId,
 CAST(r.ValuesJson AS CHAR) ValuesJson,r.SourceGroup,r.SourceFileHash
FROM recruitment_document_roster r
JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId AND c.ProfileStatus<>'Archived'
JOIN clients owner ON owner.Id=r.ClientId AND owner.IsActive=TRUE
WHERE r.TemplateId=@SourceId AND (@TemplateClientId=0 OR r.ClientId=@TemplateClientId)
 AND r.OfferNumber='' AND NOT EXISTS(SELECT 1 FROM recruitment_document_roster_releases released WHERE released.RosterId=r.Id)
ORDER BY r.ClientId,r.Id FOR UPDATE
""", new { SourceId = source.Id, TemplateClientId = source.ClientId }, tx)).ToList();
            var rebound = 0;
            foreach (var row in rows)
            {
                if (!row.TemplateHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedRows++;
                    continue;
                }
                var before = new { Migration = DocumentLayoutMigrationKey, row.TemplateId, row.TemplateHash, row.CandidateId, row.WorkLocationId, row.SourceGroup, row.SourceFileHash, CurrentValuesHash = Hash(row.ValuesJson) };
                var after = new { Migration = DocumentLayoutMigrationKey, SourceTemplateId = source.Id, TemplateId = templateId, TemplateHash = correctedHash, RosterId = row.Id, row.CandidateId, row.WorkLocationId, row.SourceGroup, row.SourceFileHash, CurrentValuesHash = Hash(row.ValuesJson) };
                if (await db.ExecuteAsync("""
UPDATE recruitment_document_roster SET TemplateId=@TemplateId,TemplateHash=@TemplateHash,UpdatedAt=CURRENT_TIMESTAMP(6)
WHERE Id=@Id AND ClientId=@ClientId AND TemplateId=@SourceId AND TemplateHash=@SourceHash AND OfferNumber=''
 AND NOT EXISTS(SELECT 1 FROM recruitment_document_roster_releases released WHERE released.RosterId=@Id)
""", new { row.Id, row.ClientId, TemplateId = templateId, TemplateHash = correctedHash, SourceId = source.Id, SourceHash = sourceHash }, tx) != 1)
                    throw new InvalidOperationException("An unreleased document row changed during migration. Refresh its configuration and retry; no migration changes were committed.");
                await db.ExecuteAsync("""
INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId)
VALUES(@Id,@ClientId,'Template layout corrected',@Old,@New,0)
""", new { row.Id, row.ClientId, Old = JsonSerializer.Serialize(before), New = JsonSerializer.Serialize(after) }, tx);
                rebound++;
            }
            result.ReboundRows += rebound;
            result.Items.Add(new(source.Id, templateId, source.ClientId, created, rebound));
        }
        await tx.CommitAsync();
        return result;
    }

    // Recognizes one legacy positioned format, independently of client names,
    // template IDs, employee IDs or fixed hashes. Custom/rich layouts are skipped.
    internal static bool TryBuildDocumentFlowLayout(string? body, out string corrected)
    {
        corrected = "";
        if (!LayoutDocumentPdfService.IsLayout(body)) return false;
        try
        {
            var layout = LayoutDocumentPdfService.Parse(body!);
            var changed = false;
            for (var index = 0; index < layout.Sections.Count; index++)
                if (TryBuildFlowEndorsement(layout.Sections[index], out var flow))
                {
                    layout.Sections[index] = flow;
                    changed = true;
                }
            if (!changed) return false;
            var candidate = LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, Json);
            LayoutDocumentPdfService.Parse(candidate);
            corrected = candidate;
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool TryBuildFlowEndorsement(DocumentSection section, out DocumentSection flow)
    {
        flow = section;
        var e = section.Elements;
        if (section.Flow || section.Pages != 1 || e.Count != 15 || e.Any(item => item.Kind != "text" || item.Runs.Count > 0)
            || !e[0].Text.Trim().Equals("ENDORSEMENT", StringComparison.OrdinalIgnoreCase)
            || !HasLayoutToken(e[2].Text, "institutionShortName") || !HasLayoutToken(e[2].Text, "institutionLocation")
            || !Regex.IsMatch(e[14].Text, @"^\s*\{\{\s*candidateName\s*\}\}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !LegacySignatureText.IsMatch(e[8].Text)) return false;
        static bool Near(double a, double b) => Math.Abs(a - b) <= .1;
        static bool SameStyle(DocumentText a, DocumentText b) => a.FontSize == b.FontSize && a.LineHeight == b.LineHeight
            && a.FontFamily == b.FontFamily && a.Bold == b.Bold && a.Italic == b.Italic && a.Align == b.Align;
        if (e.Skip(1).Any(item => !Near(item.X, e[1].X) || !Near(item.Width, e[1].Width))
            || e.Zip(e.Skip(1)).Any(pair => pair.Second.Y <= pair.First.Y)
            || !SameStyle(e[2], e[3]) || !e.Skip(4).Take(3).All(item => SameStyle(e[4], item))
            || !e.Skip(9).Take(3).All(item => SameStyle(e[9], item))) return false;
        // The recognized source contains one line per positioned text fragment.
        // Never merge taller blocks or overlapping/multi-column custom layouts.
        if (e.Any(item => item.Height + .1 < item.LineHeight || item.Height > item.LineHeight * 1.5)
            || e.Zip(e.Skip(1)).Any(pair => pair.Second.Y + .1 < pair.First.Y + pair.First.FontSize)) return false;
        double Gap(int last, int next) => Math.Max(0, e[next].Y - e[last].Y - e[last].LineHeight);
        var signatureHeight = Math.Min(60, e[9].Y - e[8].Y - 4);
        if (signatureHeight < e[8].Height || section.Bottom <= e[14].Y + e[14].LineHeight) return false;
        DocumentBlock Block(int first, int last, string separator, double after, string kind = "paragraph", double height = 0, string? align = null) => new()
        {
            Kind = kind, Height = height, Text = string.Join(separator, e.Skip(first).Take(last - first + 1).Select(item => item.Text)),
            FontSize = e[first].FontSize, LineHeight = e[first].LineHeight, FontFamily = e[first].FontFamily,
            Bold = e[first].Bold, Italic = e[first].Italic, Align = align ?? e[first].Align, SpaceAfter = after
        };
        flow = new()
        {
            Flow = true, Pages = 1, Left = e[1].X, Top = e[0].Y, Width = e[1].Width, Bottom = section.Bottom,
            Blocks = [Block(0, 0, "", Gap(0, 1), align: "center"), Block(1, 1, "", Gap(1, 2)),
                Block(2, 3, " ", Gap(3, 4)), Block(4, 6, " ", Gap(6, 7)), Block(7, 7, "", Gap(7, 8)),
                Block(8, 8, "", e[9].Y - e[8].Y - signatureHeight, "signature", signatureHeight),
                Block(9, 11, "\n", Gap(11, 12)), Block(12, 12, "", Gap(12, 13)), Block(13, 13, "", Gap(13, 14)), Block(14, 14, "", 0)]
        };
        return true;
    }

    private static bool HasLayoutToken(string text, string key) => Regex.IsMatch(text, @"\{\{\s*" + key + @"\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class LayoutMigrationTarget
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public int TemplateId { get; set; }
        public string TemplateHash { get; set; } = "";
        public int? WorkLocationId { get; set; }
        public string ValuesJson { get; set; } = "{}";
        public string SourceGroup { get; set; } = "";
        public string SourceFileHash { get; set; } = "";
    }
}
