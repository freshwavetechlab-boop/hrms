using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Dapper;
using ExcelDataReader;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    internal sealed record SourceSheet(string Name, List<SourceRow> Rows);
    internal sealed record SourceRow(int Row, List<string> Values);
    internal static string HeaderKey(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    internal static string ProfileName(string text) => Regex.Replace(text.Trim(), @"^(?:mr|mrs|ms|miss|dr|prof|shri|smt)\.?\s+", "", RegexOptions.IgnoreCase).Trim();
    internal static string NameKey(string text) => HeaderKey(ProfileName(text));
    internal static string[] Phones(string value) => Regex.Split(value, @"[;,/|\r\n]+")
        .Select(v => Regex.Replace(v, @"\D", ""))
        .Select(v => v.Length == 12 && v.StartsWith("91") ? v[2..] : v.Length == 11 && v.StartsWith('0') ? v[1..] : v)
        .Where(v => v.Length == 10).Distinct(StringComparer.Ordinal).ToArray();
    internal static List<SourceSheet> ReadWorkbook(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 10 * 1024 * 1024) throw new InvalidOperationException("Upload an .xlsx employee list of up to 10 MB.");
        Dictionary<string, Dictionary<string, string>> numericCaches;
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            if (zip.Entries.Count > 2000 || zip.Entries.Any(e => e.Length > 64L * 1024 * 1024) || zip.Entries.Sum(e => e.Length) > 64L * 1024 * 1024) throw new InvalidOperationException("The employee workbook exceeds the supported expanded size.");
            numericCaches = NumericCaches(zip);
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(bytes); using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        var result = new List<SourceSheet>(); var cellCount = 0;
        do
        {
            if (result.Count >= 30) throw new InvalidOperationException("Use a workbook with at most 30 worksheets.");
            var rows = new List<SourceRow>(); var number = 0;
            while (reader.Read())
            {
                if (++number > 5000 || reader.FieldCount > 200) throw new InvalidOperationException("Use at most 5000 source rows and 200 columns per employee worksheet.");
                var values = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    var cache = numericCaches.GetValueOrDefault(reader.Name ?? "Sheet")?.GetValueOrDefault(CellAddress(i, number));
                    var text = cache is not null && value is not DateTime ? cache : value switch { null => "", DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), double n => ExpandNumber(n.ToString("R", CultureInfo.InvariantCulture)), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
                    if (text.Length > 4000 || ++cellCount > 500000) throw new InvalidOperationException("The employee worksheet contains an oversized field or too many cells.");
                    values.Add(text.Trim());
                }
                if (values.Any(v => v.Length > 0)) rows.Add(new(number, values));
            }
            result.Add(new(reader.Name ?? "Sheet", rows));
        } while (reader.NextResult());
        return result;
    }
    private static string CellAddress(int column, int row)
    {
        var letters = ""; for (var n = column + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
    internal static string ExpandNumber(string value)
    {
        var match = Regex.Match(value, @"^([+-]?)(\d*)(?:\.(\d*))?[eE]([+-]?\d+)$");
        if (!match.Success) return value;
        if (!int.TryParse(match.Groups[4].Value, out var exponent) || exponent is < -400 or > 400) throw new InvalidOperationException("A numeric cell exceeds the supported range.");
        var digits = match.Groups[2].Value + match.Groups[3].Value; var point = match.Groups[2].Length + exponent;
        return match.Groups[1].Value + (point <= 0 ? "0." + new string('0', -point) + digits : point >= digits.Length ? digits + new string('0', point - digits.Length) : digits[..point] + "." + digits[point..]);
    }
    private static Dictionary<string, Dictionary<string, string>> NumericCaches(ZipArchive zip)
    {
        XDocument Read(ZipArchiveEntry entry)
        {
            using var source = entry.Open(); using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        var workbook = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("The workbook metadata is missing.");
        var relationships = zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidOperationException("The workbook worksheet references are missing.");
        var rels = Read(relationships).Descendants().Where(e => e.Name.LocalName == "Relationship").ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => ((string?)e.Attribute("Target") ?? "", (string?)e.Attribute("TargetMode") ?? ""));
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var sheet in Read(workbook).Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "Sheet"; var id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";
            if (!rels.TryGetValue(id, out var rel) || rel.Item2.Equals("External", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An external or unresolved worksheet reference needs review.");
            var path = new Uri(new Uri("https://xlsx.invalid/xl/workbook.xml"), rel.Item1);
            if (path.Host != "xlsx.invalid") throw new InvalidOperationException("External worksheet references are not supported.");
            var entry = zip.GetEntry(path.AbsolutePath.TrimStart('/')); if (entry is null) continue;
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in Read(entry).Descendants().Where(e => e.Name.LocalName == "c"))
            {
                var type = (string?)cell.Attribute("t"); var address = (string?)cell.Attribute("r");
                if (type is not (null or "n" or "e") || address is null) continue;
                var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                if (value is not null && value.Length > 0) cells[address] = ExpandNumber(value);
                else if (cell.Elements().Any(e => e.Name.LocalName == "f")) cells[address] = "#MISSING_CACHED_FORMULA_RESULT";
            }
            result[name] = cells;
        }
        return result;
    }
    internal static Dictionary<string, int> ResolveMapping(RecruitmentDocumentLayout layout, List<string> headers, Dictionary<string, int> requested)
    {
        if (requested is null || requested.Count > 80 || requested.Any(p => !layout.Inputs.Any(f => f.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase)) || p.Value < -1 || p.Value >= headers.Count))
            throw new InvalidOperationException("The source mapping contains an invalid field or column.");
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layout.Inputs)
        {
            if (requested.TryGetValue(field.Key, out var chosen)) { result[field.Key] = chosen; continue; }
            var aliases = field.SourceHeaders.Select(HeaderKey).ToHashSet();
            var matches = headers.Select((h, i) => (h, i)).Where(p => aliases.Contains(HeaderKey(p.h)) && p.h.Trim().Length > 0).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Multiple columns match " + field.Label + ". Choose the source column explicitly.");
            result[field.Key] = matches.SingleOrDefault().h is null ? -1 : matches[0].i;
        }
        return result;
    }
    internal static Dictionary<string, string> NormalizeInputs(RecruitmentDocumentLayout layout, Dictionary<string, string> raw)
    {
        var prepared = LayoutDocumentPdfService.PrepareValues(layout, raw, false, out _);
        return layout.Inputs.ToDictionary(f => f.Key, f => {
            var value = raw.GetValueOrDefault(f.Key, f.DefaultValue).Trim();
            return value.Length == 0 || f.EmptyValues.Contains(value, StringComparer.OrdinalIgnoreCase) ? "" : prepared.GetValueOrDefault(f.Key, value);
        }, StringComparer.OrdinalIgnoreCase);
    }
    public async Task<RecruitmentRosterPreview> PreviewAsync(byte[] file, string fileName, RecruitmentRosterImport request, AuthUser user)
    {
        Access(user, request.ClientId);
        IntakeAccess(user);
        if (request.SourceGroup is null || request.SourceGroup.Trim().Length is < 1 or > 180 || request.SourceGroup.Any(char.IsControl)
            || request.HeaderRow is < 1 or > 100 || request.SheetName is null || request.SheetName.Length > 100
            || request.Defaults is null || request.Defaults.Count > 80 || request.Defaults.Any(p => p.Value is null || p.Value.Length > 4000)
            || request.SourceRows is null || request.SourceRows.Count > 2000)
            throw new InvalidOperationException("Select the client/template, source group and a valid header row.");
        fileName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || fileName.Length > 255) throw new InvalidOperationException("Upload an .xlsx employee list.");
        await using var db = Db(); await db.OpenAsync();
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@Id AND IsActive=TRUE)", new { Id = request.ClientId })) throw new InvalidOperationException("Select an active client.");
        if (request.WorkLocationId is <= 0) throw new InvalidOperationException("Select an existing work location or leave it unassigned.");
        await RequireLocationAsync(db, user, request.ClientId, request.WorkLocationId);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, request.ClientId);
        var template = await TemplateAsync(db, request.ClientId, request.TemplateId); var layout = LayoutDocumentPdfService.Parse(template.BodyTemplate);
        if (!layout.Inputs.Any(f => f.Key == "candidateName") || !layout.Inputs.Any(f => f.Key is "candidateEmail" or "candidatePhone"))
            throw new InvalidOperationException("The roster template must define candidateName and an email or phone input.");
        if (request.Defaults.Keys.Any(k => !layout.Inputs.Any(f => f.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException("A default value names an unsupported document field.");
        var sheets = ReadWorkbook(file);
        var sheet = request.SheetName.Length == 0 ? sheets[0] : sheets.SingleOrDefault(s => s.Name == request.SheetName) ?? throw new InvalidOperationException("The chosen worksheet was not found.");
        var header = sheet.Rows.SingleOrDefault(r => r.Row == request.HeaderRow) ?? throw new InvalidOperationException("Choose the actual employee header row.");
        var mapping = ResolveMapping(layout, header.Values, request.Mapping);
        if (mapping.GetValueOrDefault("candidateName", -1) < 0) throw new InvalidOperationException("Map the candidate name column before previewing this list.");
        var preview = new RecruitmentRosterPreview { FileName = fileName, FileHash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), Sheets = sheets.Select(s => s.Name).ToList(), SheetName = sheet.Name, Headers = header.Values, Mapping = mapping, Fields = layout.Inputs };
        foreach (var source in sheet.Rows.Where(r => r.Row > request.HeaderRow))
        {
            var raw = layout.Inputs.ToDictionary(f => f.Key, f => mapping[f.Key] >= 0 ? source.Values.ElementAtOrDefault(mapping[f.Key]) ?? "" : request.Defaults.GetValueOrDefault(f.Key, f.DefaultValue), StringComparer.OrdinalIgnoreCase);
            var name = raw.GetValueOrDefault("candidateName", "").Trim();
            if (name.Length == 0 || HeaderKey(name) == HeaderKey(header.Values[mapping["candidateName"]]) || Regex.IsMatch(name, @"^(grand\s*)?total(?:s|\s|:|$)", RegexOptions.IgnoreCase)) continue;
            var values = NormalizeInputs(layout, raw);
            var row = new RecruitmentRosterPreviewRow { SourceRow = source.Row, WorkLocationId = request.WorkLocationId, CandidateName = name, Email = raw.GetValueOrDefault("candidateEmail", "").Trim(), Phone = Phones(raw.GetValueOrDefault("candidatePhone", "")).FirstOrDefault() ?? "", PositionTitle = values.GetValueOrDefault("positionTitle", ""), WorkLocation = values.GetValueOrDefault("jobLocation", ""), Values = values,
                SourceValues = header.Values.Select((h, i) => new { Key = (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + h, Value = source.Values.ElementAtOrDefault(i) ?? "" }).ToDictionary(p => p.Key, p => p.Value) };
            if (name.Length > 240 || name.Split(' ', 2)[0].Length > 120 || name.Split(' ', 2).ElementAtOrDefault(1)?.Length > 120) row.ImportIssues.Add("Candidate name is too long for the profile");
            if (row.Email.Length > 190 || row.Email.Length > 0 && !Regex.IsMatch(row.Email, @"^[^\s@<>;,]+@[^\s@<>;,]+\.[^\s@<>;,]+$")) row.ImportIssues.Add("Review the email address");
            if (row.Phone.Length == 0 && row.Email.Length == 0) row.ImportIssues.Add("A valid email or phone is required");
            preview.Rows.Add(row);
        }
        if (preview.Rows.Count is < 1 or > 2000) throw new InvalidOperationException("The chosen worksheet must contain 1 to 2000 named employee rows.");
        // Source duplicates require review. Names alone never merge profiles.
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in preview.Rows)
            foreach (var identity in Identities(row))
                if (seen.TryGetValue(identity, out var previous)) { row.ImportIssues.Add("Contact identity is repeated in source row " + previous); preview.Rows.First(r => r.SourceRow == previous).ImportIssues.Add("Contact identity is repeated in source row " + row.SourceRow); }
                else seen[identity] = row.SourceRow;
        var emails = preview.Rows.Where(r => r.Email.Length > 0).Select(r => r.Email.ToLowerInvariant()).Distinct().ToArray();
        var phones = preview.Rows.SelectMany(r => Phones(r.Values.GetValueOrDefault("candidatePhone", r.Phone))).Distinct().ToArray();
        var matches = (await db.QueryAsync<IdentityCandidate>("SELECT Id,ClientId,FirstName,LastName,NormalizedEmail,NormalizedPhone,ProfileStatus FROM recruitment_candidates WHERE NormalizedEmail IN @Emails OR RIGHT(NormalizedPhone,10) IN @Phones", new { Emails = emails, Phones = phones })).ToArray();
        await EnsureAsync(db);
        var existingInputs = (await db.QueryAsync<ExistingInput>("SELECT CandidateId,WorkLocationId,CAST(ValuesJson AS CHAR) ValuesJson FROM recruitment_document_roster WHERE ClientId=@ClientId AND TemplateId=@TemplateId AND SourceGroup=@SourceGroup", new { request.ClientId, request.TemplateId, SourceGroup = request.SourceGroup.Trim() })).ToDictionary(r => r.CandidateId);
        foreach (var row in preview.Rows)
        {
            var identities = Identities(row).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = matches.Where(c => identities.Contains("email:" + c.NormalizedEmail) || identities.Contains("phone:" + (c.NormalizedPhone.Length > 10 ? c.NormalizedPhone[^10..] : c.NormalizedPhone))).DistinctBy(c => c.Id).ToArray();
            if (found.Length > 1) row.ImportIssues.Add("Email and phone match multiple existing profiles; review identity");
            else if (found.Length == 1)
            {
                var candidate = found[0];
                if (candidate.ClientId != request.ClientId) row.ImportIssues.Add("An existing profile in another client uses this contact; resolve it before import");
                else if (candidate.ProfileStatus == "Archived") row.ImportIssues.Add("An archived profile uses this contact; review or restore it before import");
                else if (NameKey(candidate.FirstName + " " + candidate.LastName) != NameKey(row.CandidateName)) row.ImportIssues.Add("The contact matches a different saved candidate name; review identity");
                else
                {
                    row.CandidateId = candidate.Id; row.MatchStatus = "Existing profile";
                    if (existingInputs.TryGetValue(candidate.Id, out var old))
                    {
                        if (!CanSeeLocation(user, old.WorkLocationId, locations)) row.ImportIssues.Add("The existing document row is outside your permitted work locations");
                        else
                        {
                            row.ExistingRevision = RowRevision(old.ValuesJson, old.WorkLocationId);
                            row.WorkLocationId ??= old.WorkLocationId;
                            row.Values = MergeValues(old.ValuesJson, row.Values);
                        }
                    }
                }
            }
            if (row.ImportIssues.Count > 0) row.MatchStatus = "Review identity";
            var location = locations.FirstOrDefault(item => item.Id == row.WorkLocationId);
            if (location is not null) { ApplyLocationValues(row.Values, location); row.WorkLocation = location.Name; }
            LayoutDocumentPdfService.PrepareValues(layout, row.Values, false, out var missing); row.DocumentIssues = missing;
            if (row.WorkLocationId.HasValue && location is null) row.DocumentIssues.Add("The linked work location is unavailable or inactive");
        }
        var fingerprint = JsonSerializer.Serialize(new { request.ClientId, request.TemplateId, request.WorkLocationId, Group = request.SourceGroup.Trim(), preview.FileHash, TemplateHash = Hash(template.BodyTemplate), preview.SheetName, request.HeaderRow, Mapping = mapping.OrderBy(p => p.Key).ToArray(), Defaults = request.Defaults.OrderBy(p => p.Key).ToArray(), Existing = preview.Rows.Select(r => new { r.SourceRow, r.ExistingRevision, r.WorkLocationId }), Locations = locations.Where(l => preview.Rows.Any(r => r.WorkLocationId == l.Id)).OrderBy(l => l.Id) }, Json);
        preview.PreviewToken = Hash(fingerprint); return preview;
    }
    private static IEnumerable<string> Identities(RecruitmentRosterPreviewRow row)
    {
        if (row.Email.Length > 0) yield return "email:" + row.Email;
        foreach (var phone in Phones(row.Values.GetValueOrDefault("candidatePhone", row.Phone))) yield return "phone:" + phone;
    }
    private sealed class IdentityCandidate
    {
        public long Id { get; set; } public int ClientId { get; set; } public string FirstName { get; set; } = ""; public string LastName { get; set; } = "";
        public string NormalizedEmail { get; set; } = ""; public string NormalizedPhone { get; set; } = ""; public string ProfileStatus { get; set; } = "";
    }
    private sealed class ExistingInput { public long CandidateId { get; set; } public int? WorkLocationId { get; set; } public string ValuesJson { get; set; } = "{}"; }

    public async Task<object> InspectAsync(byte[] file, string fileName, int clientId, int templateId, int headerRow, AuthUser user)
    {
        Access(user, clientId);
        IntakeAccess(user);
        if (headerRow is < 1 or > 100 || !fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Select an .xlsx workbook and its header row (1 to 100).");
        await using var db = Db(); await db.OpenAsync(); await TemplateAsync(db, clientId, templateId);
        return ReadWorkbook(file).Select(s => new { s.Name, Headers = s.Rows.FirstOrDefault(r => r.Row == headerRow)?.Values ?? [], Sample = s.Rows.FirstOrDefault(r => r.Row > headerRow)?.Values ?? [] }).ToArray();
    }
}
