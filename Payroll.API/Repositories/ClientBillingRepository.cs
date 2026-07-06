using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Payroll.API.Repositories;

public class ClientBillingRepository(IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> ImportJobs = new();
    private static readonly string[] RateCardTypes = ["All", "Service Charge", "Reimbursement", "Bonus", "Statutory Compliance Charges"];
    private static readonly string[] RateTypes = ["Percentage", "Fixed"];
    private static readonly string[] ImportHeaders = ["Client Id", "Work Location Id", "Rate Card Type", "Rate Type", "Value", "Tax Basis", "GST Rate %", "Effective From", "Effective To", "Active"];
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
    }

    public async Task<ClientBillingModule> GetModuleAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var enabled = await db.ExecuteScalarAsync<bool?>("SELECT IsEnabled FROM client_billing_settings WHERE Id=1");
        return new ClientBillingModule { IsEnabled = enabled ?? false };
    }

    public async Task SaveModuleAsync(ClientBillingModule module)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        await db.ExecuteAsync(@"INSERT INTO client_billing_settings (Id,IsEnabled) VALUES (1,@IsEnabled)
ON DUPLICATE KEY UPDATE IsEnabled=@IsEnabled, UpdatedAt=CURRENT_TIMESTAMP", module);
    }

    public async Task<IEnumerable<ClientBillingConfiguration>> GetAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        return await db.QueryAsync<ClientBillingConfiguration>(@"SELECT b.*, c.Name AS ClientName, COALESCE(w.Name,'All locations') AS WorkLocationName
FROM client_billing_configurations b
JOIN clients c ON c.Id=b.ClientId
LEFT JOIN worklocations w ON w.Id=b.WorkLocationId
ORDER BY c.Name, WorkLocationName, b.RateCardType, b.EffectiveFrom DESC, b.Id DESC");
    }

    public async Task<(long Id, string Error)> SaveAsync(ClientBillingConfiguration row)
    {
        var error = Validate(row);
        if (!string.IsNullOrWhiteSpace(error)) return (0, error);
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var clientExists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", row);
        if (clientExists == 0) return (0, "Select an active client.");
        if (row.WorkLocationId is > 0)
        {
            var locationExists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations WHERE Id=@WorkLocationId AND ClientId=@ClientId AND IsActive=TRUE", row);
            if (locationExists == 0) return (0, "Select an active work location for the selected client.");
        }
        row.WorkLocationId = row.WorkLocationId is > 0 ? row.WorkLocationId : null;
        if (row.Id <= 0)
        {
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO client_billing_configurations (ClientId,WorkLocationId,RateCardType,RateType,Value,TaxInclusive,GstRatePercent,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@ClientId,@WorkLocationId,@RateCardType,@RateType,@Value,@TaxInclusive,@GstRatePercent,@EffectiveFrom,@EffectiveTo,@IsActive); SELECT LAST_INSERT_ID();", row);
            return (id, "");
        }
        await db.ExecuteAsync(@"UPDATE client_billing_configurations SET ClientId=@ClientId,WorkLocationId=@WorkLocationId,RateCardType=@RateCardType,RateType=@RateType,Value=@Value,TaxInclusive=@TaxInclusive,GstRatePercent=@GstRatePercent,EffectiveFrom=@EffectiveFrom,EffectiveTo=@EffectiveTo,IsActive=@IsActive,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
        return (row.Id, "");
    }

    public async Task<byte[]> BuildImportTemplateAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var clients = (await db.QueryAsync<(int Id, string Name, string Code)>("SELECT Id, Name, COALESCE(Code, '') Code FROM clients WHERE IsActive=TRUE ORDER BY Name")).ToList();
        var locations = (await db.QueryAsync<(int Id, int ClientId, string Name)>("SELECT Id, ClientId, Name FROM worklocations WHERE IsActive=TRUE ORDER BY ClientId, Name")).ToList();
        var sampleClient = clients.FirstOrDefault();
        var sample = new[] { sampleClient.Id > 0 ? sampleClient.Id.ToString(CultureInfo.InvariantCulture) : "", "", "All", "Percentage", "0", "Excluding", "18", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "", "TRUE" };
        var reference = new List<string[]>
        {
            new[] { "Clients", "", "" },
            new[] { "Client Id", "Client Name", "Client Code" }
        };
        reference.AddRange(clients.Select(client => new[] { client.Id.ToString(CultureInfo.InvariantCulture), client.Name, client.Code }));
        reference.Add(Array.Empty<string>());
        reference.AddRange(new[]
        {
            new[] { "Work Locations", "", "" },
            new[] { "Work Location Id", "Client Id", "Work Location Name" }
        });
        reference.AddRange(locations.Select(location => new[] { location.Id.ToString(CultureInfo.InvariantCulture), location.ClientId.ToString(CultureInfo.InvariantCulture), location.Name }));
        reference.Add(Array.Empty<string>());
        reference.AddRange(new[]
        {
            new[] { "Options", "Values", "Notes" },
            new[] { "Rate Card Type", string.Join(", ", RateCardTypes), "" },
            new[] { "Rate Type", string.Join(", ", RateTypes), "" },
            new[] { "Tax Basis", "Excluding, Inclusive", "" },
            new[] { "Active", "TRUE, FALSE", "" },
            new[] { "Work Location Id", "Blank or 0", "Applies to all locations for that client" }
        });
        return BuildXlsx(("Client Billing", new[] { ImportHeaders, sample }), ("Reference", reference));
    }

    public async Task<ClientImportJobStatus> StartImportJobAsync(IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        ImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetImportJob(job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportRowsAsync(rows, (completed, inserted, updated) => SetImportJob(job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetImportJob(job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetImportJob(job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetImportJob(Guid jobId) => ImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportRowsAsync(List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            if (rows.Count < 2 || totalRows == 0)
                return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);

            var header = rows[0].Select(Norm).ToList();
            var clients = (await db.QueryAsync<int>("SELECT Id FROM clients WHERE IsActive=TRUE", transaction: tx)).ToHashSet();
            var locations = (await db.QueryAsync<(int Id, int ClientId)>("SELECT Id, ClientId FROM worklocations WHERE IsActive=TRUE", transaction: tx)).ToDictionary(item => item.Id, item => item.ClientId);
            var inserted = 0;
            var updated = 0;
            var completed = 0;
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                string V(params string[] names)
                {
                    foreach (var name in names)
                    {
                        var ix = header.IndexOf(Norm(name));
                        if (ix >= 0 && ix < row.Count) return row[ix].Trim();
                    }
                    return "";
                }

                var rowNumber = i + 1;
                var rowErrors = new List<string>();
                var clientId = int.TryParse(V("Client Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedClientId) ? parsedClientId : 0;
                var locationId = int.TryParse(V("Work Location Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLocationId) ? parsedLocationId : 0;
                var rateCardType = NormalizeOption(V("Rate Card Type"), RateCardTypes);
                var rateType = NormalizeOption(V("Rate Type"), RateTypes);
                var valueText = V("Value");
                var gstText = V("GST Rate %");
                var taxText = V("Tax Basis");
                var activeText = V("Active");
                var effectiveFrom = ParseImportDate(V("Effective From"), out var fromOk);
                var effectiveTo = ParseOptionalImportDate(V("Effective To", "Effective To Date"), out var toOk);
                var value = ParseDecimal(valueText, out var valueOk);
                var gstOk = true;
                var gst = string.IsNullOrWhiteSpace(gstText) ? 18m : ParseDecimal(gstText, out gstOk);
                if (string.IsNullOrWhiteSpace(gstText)) gstOk = true;
                var taxInclusive = ParseTaxBasis(taxText, out var taxOk);
                var isActive = ParseImportFlag(activeText, true);

                if (clientId <= 0) rowErrors.Add($"Row {rowNumber}: Client Id is required.");
                else if (!clients.Contains(clientId)) rowErrors.Add($"Row {rowNumber}: Client Id {clientId} was not found.");
                if (locationId > 0 && (!locations.TryGetValue(locationId, out var locationClientId) || locationClientId != clientId)) rowErrors.Add($"Row {rowNumber}: Work Location Id {locationId} was not found for Client Id {clientId}.");
                if (!RateCardTypes.Contains(rateCardType)) rowErrors.Add($"Row {rowNumber}: Rate Card Type is invalid.");
                if (!RateTypes.Contains(rateType)) rowErrors.Add($"Row {rowNumber}: Rate Type is invalid.");
                if (!valueOk || value < 0) rowErrors.Add($"Row {rowNumber}: Value must be a non-negative number.");
                if (!taxOk) rowErrors.Add($"Row {rowNumber}: Tax Basis must be Excluding or Inclusive.");
                if (!gstOk || gst < 0 || gst > 100) rowErrors.Add($"Row {rowNumber}: GST Rate % must be between 0 and 100.");
                if (!fromOk) rowErrors.Add($"Row {rowNumber}: Effective From is required as a valid date.");
                if (!toOk) rowErrors.Add($"Row {rowNumber}: Effective To must be a valid date when filled.");
                if (fromOk && effectiveTo.HasValue && effectiveTo.Value.Date < effectiveFrom.Date) rowErrors.Add($"Row {rowNumber}: Effective To cannot be before Effective From.");
                if (!IsImportFlag(activeText)) rowErrors.Add($"Row {rowNumber}: Active must be TRUE/FALSE.");
                var key = $"{clientId}:{locationId}:{rateCardType}:{rateType}:{effectiveFrom:yyyy-MM-dd}";
                if (rowErrors.Count == 0 && !seen.Add(key)) rowErrors.Add($"Row {rowNumber}: Billing configuration duplicates an earlier row for the same client/location/type/effective date.");

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors);
                    completed++;
                    progress?.Invoke(completed, inserted, updated);
                    continue;
                }

                var args = new
                {
                    ClientId = clientId,
                    WorkLocationId = locationId > 0 ? locationId : (int?)null,
                    WorkLocationRef = locationId,
                    RateCardType = rateCardType,
                    RateType = rateType,
                    Value = value,
                    TaxInclusive = taxInclusive,
                    GstRatePercent = gst,
                    EffectiveFrom = effectiveFrom.Date,
                    EffectiveTo = effectiveTo?.Date,
                    IsActive = isActive
                };
                var existingId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM client_billing_configurations
WHERE ClientId=@ClientId AND COALESCE(WorkLocationId,0)=@WorkLocationRef AND RateCardType=@RateCardType AND RateType=@RateType AND EffectiveFrom=@EffectiveFrom
ORDER BY Id LIMIT 1", args, tx);
                if (existingId is null)
                {
                    await db.ExecuteScalarAsync<long>(@"INSERT INTO client_billing_configurations (ClientId,WorkLocationId,RateCardType,RateType,Value,TaxInclusive,GstRatePercent,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@ClientId,@WorkLocationId,@RateCardType,@RateType,@Value,@TaxInclusive,@GstRatePercent,@EffectiveFrom,@EffectiveTo,@IsActive); SELECT LAST_INSERT_ID();", args, tx);
                    inserted++;
                }
                else
                {
                    await db.ExecuteAsync(@"UPDATE client_billing_configurations SET Value=@Value,TaxInclusive=@TaxInclusive,GstRatePercent=@GstRatePercent,EffectiveTo=@EffectiveTo,IsActive=@IsActive,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = existingId.Value, args.Value, args.TaxInclusive, args.GstRatePercent, args.EffectiveTo, args.IsActive }, tx);
                    updated++;
                }

                completed++;
                progress?.Invoke(completed, inserted, updated);
            }

            if (errors.Count > 0)
            {
                await tx.RollbackAsync();
                return new ClientImportResult(totalRows, 0, 0, errors);
            }

            await tx.CommitAsync();
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
    }

    static string Validate(ClientBillingConfiguration row)
    {
        if (row.ClientId <= 0) return "Select a client.";
        if (!RateCardTypes.Contains(row.RateCardType)) return "Select a valid rate card type.";
        if (!RateTypes.Contains(row.RateType)) return "Select a valid rate type.";
        if (row.Value < 0) return "Value cannot be negative.";
        if (row.GstRatePercent < 0 || row.GstRatePercent > 100) return "GST rate must be between 0 and 100.";
        if (row.EffectiveFrom == default) return "Effective from date is required.";
        if (row.EffectiveTo.HasValue && row.EffectiveTo.Value.Date < row.EffectiveFrom.Date) return "Effective to date cannot be before effective from.";
        return "";
    }

    static async Task EnsureTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS client_billing_settings (
    Id TINYINT PRIMARY KEY,
    IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS client_billing_configurations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    WorkLocationId INT NULL,
    RateCardType VARCHAR(80) NOT NULL,
    RateType VARCHAR(30) NOT NULL,
    Value DECIMAL(18,4) NOT NULL DEFAULT 0,
    TaxInclusive BOOLEAN NOT NULL DEFAULT FALSE,
    GstRatePercent DECIMAL(8,4) NOT NULL DEFAULT 18,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_ClientBilling_Client (ClientId, IsActive, EffectiveFrom),
    INDEX IX_ClientBilling_Location (WorkLocationId, IsActive, EffectiveFrom),
    INDEX IX_ClientBilling_Type (RateCardType, RateType)
);");
        await EnsureColumnAsync(db, "client_billing_configurations", "GstRatePercent", "DECIMAL(8,4) NOT NULL DEFAULT 18 AFTER TaxInclusive");
    }

    static async Task EnsureColumnAsync(MySqlConnection db, string tableName, string columnName, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    static void SetImportJob(Guid jobId, Func<ClientImportJobStatus, ClientImportJobStatus> update) =>
        ImportJobs.AddOrUpdate(jobId, _ => update(new ClientImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));

    static string NormalizeOption(string value, string[] options) =>
        options.FirstOrDefault(option => option.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    static bool IsImportFlag(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        new[] { "true", "yes", "active", "1" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ||
        new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    static bool ParseImportFlag(string value, bool defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue :
        new[] { "true", "yes", "active", "1" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? true :
        new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? false : defaultValue;

    static bool ParseTaxBasis(string value, out bool ok)
    {
        var clean = value.Trim();
        ok = true;
        if (string.IsNullOrWhiteSpace(clean) || new[] { "excluding", "exclusive", "exclude", "false", "no", "0" }.Contains(clean, StringComparer.OrdinalIgnoreCase)) return false;
        if (new[] { "inclusive", "including", "include", "true", "yes", "1" }.Contains(clean, StringComparer.OrdinalIgnoreCase)) return true;
        ok = false;
        return false;
    }

    static decimal ParseDecimal(string value, out bool ok)
    {
        ok = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result);
        if (!ok) ok = decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out result);
        return result;
    }

    static DateTime ParseImportDate(string value, out bool ok)
    {
        ok = TryParseImportDate(value, out var date);
        return date;
    }

    static DateTime? ParseOptionalImportDate(string value, out bool ok)
    {
        if (string.IsNullOrWhiteSpace(value)) { ok = true; return null; }
        ok = TryParseImportDate(value, out var date);
        return ok ? date : null;
    }

    static bool TryParseImportDate(string value, out DateTime date)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 0)
        {
            date = DateTime.FromOADate(serial).Date;
            return true;
        }
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    static string Norm(string value) => value.Replace(" ", "").Replace("_", "").Replace("-", "").Replace("%", "").ToLowerInvariant();

    static async Task<List<List<string>>> ParseImportFileAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        return file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ParseXlsx(bytes) : ParseCsv(Encoding.UTF8.GetString(bytes));
    }

    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted && ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (!quoted && (ch == '\n' || ch == '\r'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = [];
            }
            else cell.Append(ch);
        }
        row.Add(cell.ToString());
        if (row.Any(value => value.Length > 0)) rows.Add(row);
        return rows;
    }

    static List<List<string>> ParseXlsx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("Import sheet not found.");
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var index = CellIndex((string?)cell.Attribute("r") ?? "A1");
                while (values.Count < index) values.Add("");
                var type = (string?)cell.Attribute("t") ?? "";
                var raw = type == "inlineStr" ? cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? "" : cell.Element(ns + "v")?.Value ?? "";
                values.Add(type == "s" && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < shared.Count ? shared[sharedIndex] : raw);
            }
            rows.Add(values);
        }
        return rows;
    }

    static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns + "si").Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToList();
    }

    static int CellIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) n = n * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        return Math.Max(0, n - 1);
    }

    static byte[] BuildXlsx(params (string Name, IEnumerable<string[]> Rows)[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var sheetOverrides = string.Concat(sheets.Select((_, index) => $"""<Override PartName="/xl/worksheets/sheet{index + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""));
            var sheetRelationships = string.Concat(sheets.Select((_, index) => $"""<Relationship Id="rId{index + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{index + 1}.xml"/>"""));
            Add(zip, "[Content_Types].xml", $"""<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>{sheetOverrides}</Types>""");
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/_rels/workbook.xml.rels", $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{sheetRelationships}<Relationship Id="rId{sheets.Length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>""");
            Add(zip, "xl/workbook.xml", WorkbookXml(sheets.Select((sheet, index) => (sheet.Name, index + 1))));
            foreach (var (sheet, index) in sheets.Select((sheet, index) => (sheet, index + 1)))
                Add(zip, $"xl/worksheets/sheet{index}.xml", SheetXml(sheet.Rows));
        }
        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    static string WorkbookXml(IEnumerable<(string Name, int Index)> sheets) =>
        new XDocument(new XElement(XName.Get("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"),
            new XElement(XName.Get("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                sheets.Select(sheet => new XElement(XName.Get("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                    new XAttribute("name", sheet.Name),
                    new XAttribute("sheetId", sheet.Index),
                    new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), $"rId{sheet.Index}")))))).ToString(SaveOptions.DisableFormatting);

    static string SheetXml(IEnumerable<string[]> rows) =>
        new XDocument(new XElement(XName.Get("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            new XElement(XName.Get("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                rows.Select((row, rowIndex) => new XElement(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                    new XAttribute("r", rowIndex + 1),
                    row.Select((cell, colIndex) => new XElement(XName.Get("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                        new XAttribute("r", $"{Col(colIndex + 1)}{rowIndex + 1}"),
                        new XAttribute("t", "inlineStr"),
                        new XElement(XName.Get("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                            new XElement(XName.Get("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), cell ?? ""))))))))).ToString(SaveOptions.DisableFormatting);

    static string Col(int n)
    {
        var value = "";
        while (n > 0)
        {
            n--;
            value = (char)('A' + n % 26) + value;
            n /= 26;
        }
        return value;
    }
}
