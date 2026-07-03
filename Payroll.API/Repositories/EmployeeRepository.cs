using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Payroll.API.Repositories;

public class EmployeeRepository(IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<Guid, EmployeeImportJobStatus> ImportJobs = new();
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task<IEnumerable<Employee>> GetAsync() { await using var db = Connection(); await db.OpenAsync(); var rows = (await db.QueryAsync<Employee>("SELECT * FROM employees ORDER BY FirstName, LastName")).ToList(); await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, rows); return rows; }
    public async Task<int> SaveAsync(Employee employee) { await using var db = Connection(); await db.OpenAsync(); if (employee.Id == 0) employee.Id = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,Grade,WorkLocationId,ReportingManagerId,PortalAccess,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@Grade,@WorkLocationId,@ReportingManagerId,@PortalAccess,@SalaryStructureId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", employee); else await db.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId,EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,Grade=@Grade,WorkLocationId=@WorkLocationId,ReportingManagerId=@ReportingManagerId,PortalAccess=@PortalAccess,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,IsActive=@IsActive WHERE Id=@Id", employee); await PayrollDataTableStore.SyncEmployeeTablesAsync(db, employee); await db.ExecuteAsync("UPDATE employees SET SalaryJson=@SalaryJson,PersonalJson=@PersonalJson,PaymentJson=@PaymentJson WHERE Id=@Id", employee); return employee.Id; }
    public async Task<EmployeeDeletePreview?> GetDeletePreviewAsync(int id)
    {
        await using var db = Connection(); await db.OpenAsync();
        var employee = await db.QueryFirstOrDefaultAsync<(int Id, string EmployeeCode, string FirstName, string LastName)>("SELECT Id,EmployeeCode,FirstName,LastName FROM employees WHERE Id=@id", new { id });
        if (employee.Id == 0) return null;
        var links = new List<string>();
        async Task Add(string label, string table, string column, string filter = "") { var count = await CountSafeAsync(db, table, column, id, filter); if (count > 0) links.Add($"{count} {label}{(count == 1 ? "" : "s")}"); }
        await Add("reporting employee", "employees", "ReportingManagerId", "AND IsActive=TRUE");
        await Add("login user", "authusers", "EmployeeId", "AND IsActive=TRUE");
        await Add("pay run row", "payrunemployees", "EmployeeId");
        await Add("payroll adjustment", "payrolladjustments", "EmployeeId");
        await Add("attendance group mapping", "attendance_group_employees", "employee_id");
        await Add("geo fence mapping", "attendance_geo_fence_rule_employees", "employee_id");
        await Add("monthly attendance row", "employee_monthly_attendance", "employee_id");
        await Add("daily attendance row", "employee_daily_attendance", "employee_id");
        await Add("leave balance", "employee_leave_balances", "employee_id");
        await Add("ESS leave request", "essleaverequests", "EmployeeId");
        await Add("tax declaration", "employee_tax_declaration_headers", "employee_id");
        await Add("tax computation", "tax_computation_snapshots", "employee_id");
        var name = $"{employee.FirstName} {employee.LastName}".Trim();
        return new EmployeeDeletePreview(employee.Id, employee.EmployeeCode, string.IsNullOrWhiteSpace(name) ? employee.EmployeeCode : name, links, links.Count == 0);
    }
    public async Task<(bool Ok, string Error)> DeleteAsync(int id)
    {
        var preview = await GetDeletePreviewAsync(id);
        if (preview is null) return (false, "Employee not found.");
        if (preview.Links.Count > 0) return (false, $"Cannot delete {preview.EmployeeName}. Linked records: {string.Join(" | ", preview.Links)}");
        await using var db = Connection(); await db.OpenAsync();
        await db.ExecuteAsync("UPDATE employees SET IsActive=FALSE WHERE Id=@id", new { id });
        return (true, "");
    }

    public async Task<EmployeeImportResult> ImportCsvAsync(int clientId, IFormFile file)
    {
        return await ImportRowsAsync(clientId, await ParseImportFileAsync(file));
    }

    public async Task<EmployeeImportJobStatus> StartImportCsvJobAsync(int clientId, IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new EmployeeImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        ImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetJob(job.JobId, current => current with { State = "Processing" });
            var result = await ImportRowsAsync(clientId, rows, (completed, inserted, updated) => SetJob(job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
            SetJob(job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
        });
        return job;
    }

    public EmployeeImportJobStatus? GetImportJob(Guid jobId) => ImportJobs.TryGetValue(jobId, out var job) ? job : null;

    public async Task<byte[]> BuildImportTemplateAsync(int clientId)
    {
        await using var db = Connection(); await db.OpenAsync();
        var client = await db.QueryFirstOrDefaultAsync<(int Id, string Name)>("SELECT Id, Name FROM clients WHERE Id=@clientId", new { clientId });
        var drops = (await db.QueryAsync<(string Type, string Value)>("SELECT Type, Value FROM dropdownmasters WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@clientId) AND Type IN ('Department','Designation','Employee Grade') ORDER BY Type, Value", new { clientId })).ToList();
        var locations = (await db.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM worklocations WHERE ClientId=@clientId AND IsActive=TRUE ORDER BY Name", new { clientId })).ToList();
        string First(string type, string fallback) => drops.FirstOrDefault(item => item.Type == type).Value ?? fallback;
        var headers = new[] { "Employee Code", "First Name", "Last Name", "Gender", "Date Of Joining", "Work Email", "Department", "Designation", "Grade", "Work Location", "Annual CTC", "Date Of Birth", "Mobile", "PAN", "Aadhaar", "UAN Number", "Address", "Correspondence Address", "Permanent Address" };
        var example = new[] { "EMP001", "Rahul", "Sharma", "Male", "2026-04-01", "rahul@example.com", First("Department", ""), First("Designation", ""), First("Employee Grade", ""), locations.FirstOrDefault().Name ?? "", "600000", "1995-01-15", "9876543210", "ABCDE1234F", "123412341234", "100200300400", "Local address", "Correspondence address", "Permanent address" };
        var masters = new List<string[]> { new[] { "Master Type", "Value", "Id" } };
        if (client.Id > 0) masters.Add(new[] { "Client", client.Name, client.Id.ToString() });
        masters.AddRange(drops.Select(item => new[] { item.Type, item.Value, "" }));
        masters.AddRange(locations.Select(item => new[] { "Work Location", item.Name, item.Id.ToString() }));
        return BuildXlsx(("Employees", new[] { headers, example }), ("Masters", masters));
    }

    async Task<EmployeeImportResult> ImportRowsAsync(int clientId, List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        try
        {
            if (rows.Count < 2) return new EmployeeImportResult(0, 0, 0, ["Import file has no data rows."]);
            var header = rows[0].Select(Norm).ToList();
            var validDrops = (await db.QueryAsync<(string Type, string Value)>("SELECT Type, Value FROM dropdownmasters WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@clientId) AND Type IN ('Department','Designation','Employee Grade')", new { clientId }, tx)).GroupBy(x => x.Type).ToDictionary(x => x.Key, x => x.Select(v => v.Value).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var locations = (await db.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM worklocations WHERE ClientId=@clientId AND IsActive=TRUE", new { clientId }, tx)).ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
            var inserted = 0; var updated = 0; var completed = 0; var errors = new List<string>();
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i]; if (row.All(string.IsNullOrWhiteSpace)) continue;
                string V(string name) => header.IndexOf(Norm(name)) is var ix && ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
                var rowErrors = new List<string>();
                var code = V("Employee Code"); var first = V("First Name"); var email = V("Work Email");
                if (string.IsNullOrWhiteSpace(code)) rowErrors.Add($"Row {i + 1}: Employee Code is required.");
                if (string.IsNullOrWhiteSpace(first)) rowErrors.Add($"Row {i + 1}: First Name is required.");
                if (!DateOk(V("Date Of Joining"))) rowErrors.Add($"Row {i + 1}: Date Of Joining must be yyyy-MM-dd.");
                if (!DateOk(V("Date Of Birth"))) rowErrors.Add($"Row {i + 1}: Date Of Birth must be yyyy-MM-dd.");
                ValidateMaster("Department", V("Department"), validDrops, rowErrors, i + 1);
                ValidateMaster("Designation", V("Designation"), validDrops, rowErrors, i + 1);
                ValidateMaster("Employee Grade", V("Grade"), validDrops, rowErrors, i + 1, "Grade");
                var workLocation = V("Work Location");
                if (!string.IsNullOrWhiteSpace(workLocation) && !locations.ContainsKey(workLocation)) rowErrors.Add($"Row {i + 1}: Work Location \"{workLocation}\" is not in Work Locations.");
                if (rowErrors.Count > 0) { errors.AddRange(rowErrors); completed++; progress?.Invoke(completed, inserted, updated); continue; }
                var existingId = await db.ExecuteScalarAsync<int?>(@"SELECT Id FROM employees WHERE ClientId=@clientId AND (EmployeeCode=@code OR (WorkEmail<>'' AND WorkEmail=@email)) ORDER BY EmployeeCode=@code DESC LIMIT 1", new { clientId, code, email }, tx);
                var personal = JsonSerializer.Serialize(new { dateOfBirth = V("Date Of Birth"), mobile = V("Mobile"), panNumber = V("PAN"), aadhaarNumber = V("Aadhaar"), uanNumber = V("UAN Number"), address = V("Address"), correspondenceAddress = V("Correspondence Address"), permanentAddress = V("Permanent Address") });
                var args = new { Id = existingId ?? 0, ClientId = clientId, EmployeeCode = code, FirstName = first, LastName = V("Last Name"), Gender = V("Gender"), DateOfJoining = DbDate(V("Date Of Joining")), WorkEmail = email, Department = V("Department"), Designation = V("Designation"), Grade = V("Grade"), WorkLocationId = string.IsNullOrWhiteSpace(workLocation) ? 0 : locations[workLocation], AnnualCtc = decimal.TryParse(V("Annual CTC"), out var ctc) ? ctc : 0, PersonalJson = personal, SalaryJson = "{}", PaymentJson = "{}", IsActive = true };
                var id = existingId ?? (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,Grade,WorkLocationId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@Grade,@WorkLocationId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", args, tx);
                if (existingId is null) inserted++; else { updated++; await db.ExecuteAsync(@"UPDATE employees SET EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,Grade=@Grade,WorkLocationId=@WorkLocationId,AnnualCtc=@AnnualCtc,PersonalJson=@PersonalJson,IsActive=TRUE WHERE Id=@Id", args, tx); }
                await db.ExecuteAsync(@"INSERT INTO employeepersonaldetails (EmployeeId,DateOfBirth,Mobile,PanNumber,AadhaarNumber,UanNumber,Address,CorrespondenceAddress,PermanentAddress)
VALUES (@Id,@DateOfBirth,@Mobile,@Pan,@Aadhaar,@Uan,@Address,@Correspondence,@Permanent)
ON DUPLICATE KEY UPDATE DateOfBirth=@DateOfBirth,Mobile=@Mobile,PanNumber=@Pan,AadhaarNumber=@Aadhaar,UanNumber=@Uan,Address=@Address,CorrespondenceAddress=@Correspondence,PermanentAddress=@Permanent", new { Id = id, DateOfBirth = DbDate(V("Date Of Birth")), Mobile = V("Mobile"), Pan = V("PAN"), Aadhaar = V("Aadhaar"), Uan = V("UAN Number"), Address = V("Address"), Correspondence = V("Correspondence Address"), Permanent = V("Permanent Address") }, tx);
                completed++; progress?.Invoke(completed, inserted, updated);
            }
            if (errors.Count > 0) { await tx.RollbackAsync(); return new EmployeeImportResult(totalRows, 0, 0, errors); }
            await tx.CommitAsync(); return new EmployeeImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex) { await tx.RollbackAsync(); return new EmployeeImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]); }
    }

    static void SetJob(Guid jobId, Func<EmployeeImportJobStatus, EmployeeImportJobStatus> update) => ImportJobs.AddOrUpdate(jobId, _ => update(new EmployeeImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));
    static async Task<int> CountSafeAsync(MySqlConnection db, string table, string column, int id, string filter) { try { return await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {column}=@id {filter}", new { id }); } catch { return 0; } }
    static void ValidateMaster(string type, string value, Dictionary<string, HashSet<string>> masters, List<string> errors, int row, string? label = null) { if (!string.IsNullOrWhiteSpace(value) && (!masters.TryGetValue(type, out var values) || !values.Contains(value))) errors.Add($"Row {row}: {label ?? type} \"{value}\" is not in Dropdown Masters."); }
    static bool DateOk(string value) => string.IsNullOrWhiteSpace(value) || DateTime.TryParseExact(value, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _);
    static string? DbDate(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    static string Norm(string value) => value.Replace(" ", "").Replace("_", "").ToLowerInvariant();
    static async Task<List<List<string>>> ParseImportFileAsync(IFormFile file) { using var ms = new MemoryStream(); await file.CopyToAsync(ms); var bytes = ms.ToArray(); return file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ParseXlsx(bytes) : ParseCsv(Encoding.UTF8.GetString(bytes)); }
    static List<List<string>> ParseCsv(string text) { var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var q = false; for (var i = 0; i < text.Length; i++) { var c = text[i]; if (q && c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else if (c == '"') q = !q; else if (!q && c == ',') { row.Add(cell.ToString()); cell.Clear(); } else if (!q && (c == '\n' || c == '\r')) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; } else cell.Append(c); } row.Add(cell.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row); return rows; }
    static List<List<string>> ParseXlsx(byte[] bytes) { using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); var shared = ReadSharedStrings(zip); var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("Employees sheet not found."); using var stream = sheet.Open(); var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; var rows = new List<List<string>>(); foreach (var row in doc.Descendants(ns + "row")) { var values = new List<string>(); foreach (var cell in row.Elements(ns + "c")) { var index = CellIndex((string?)cell.Attribute("r") ?? "A1"); while (values.Count < index) values.Add(""); var type = (string?)cell.Attribute("t") ?? ""; var raw = type == "inlineStr" ? cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? "" : cell.Element(ns + "v")?.Value ?? ""; values.Add(type == "s" && int.TryParse(raw, out var si) && si >= 0 && si < shared.Count ? shared[si] : raw); } rows.Add(values); } return rows; }
    static List<string> ReadSharedStrings(ZipArchive zip) { var entry = zip.GetEntry("xl/sharedStrings.xml"); if (entry is null) return []; using var stream = entry.Open(); var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; return doc.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList(); }
    static int CellIndex(string reference) { var n = 0; foreach (var c in reference.TakeWhile(char.IsLetter)) n = n * 26 + char.ToUpperInvariant(c) - 'A' + 1; return Math.Max(0, n - 1); }
    static byte[] BuildXlsx(params (string Name, IEnumerable<string[]> Rows)[] sheets) { using var ms = new MemoryStream(); using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) { Add(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>"""); Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""); Add(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>"""); Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>"""); Add(zip, "xl/workbook.xml", WorkbookXml(sheets.Select((s, i) => (s.Name, i + 1)))); foreach (var (sheet, ix) in sheets.Select((s, i) => (s, i + 1))) Add(zip, $"xl/worksheets/sheet{ix}.xml", SheetXml(sheet.Rows)); } return ms.ToArray(); }
    static void Add(ZipArchive zip, string path, string text) { var entry = zip.CreateEntry(path); using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(text); }
    static string WorkbookXml(IEnumerable<(string Name, int Index)> sheets) => new XDocument(new XElement(XName.Get("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), new XElement(XName.Get("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), sheets.Select(s => new XElement(XName.Get("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("name", s.Name), new XAttribute("sheetId", s.Index), new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), $"rId{s.Index}")))))).ToString(SaveOptions.DisableFormatting);
    static string SheetXml(IEnumerable<string[]> rows) => new XDocument(new XElement(XName.Get("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XElement(XName.Get("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), rows.Select((row, r) => new XElement(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("r", r + 1), row.Select((cell, c) => new XElement(XName.Get("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XAttribute("r", $"{Col(c + 1)}{r + 1}"), new XAttribute("t", "inlineStr"), new XElement(XName.Get("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), new XElement(XName.Get("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), cell ?? ""))))))))).ToString(SaveOptions.DisableFormatting);
    static string Col(int n) { var s = ""; while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; } return s; }
}

public record EmployeeImportResult(int TotalRows, int Inserted, int Updated, List<string> Errors);
public record EmployeeImportJobStatus(Guid JobId, string State, int TotalRows, int CompletedRows, int Inserted, int Updated, List<string> Errors);
public record EmployeeDeletePreview(int EmployeeId, string EmployeeCode, string EmployeeName, List<string> Links, bool CanDelete);
