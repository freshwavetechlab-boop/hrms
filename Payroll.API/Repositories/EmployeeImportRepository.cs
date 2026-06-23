using Dapper;
using ExcelDataReader;
using MySqlConnector;
using Payroll.API.Models;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Payroll.API.Repositories;

public class EmployeeImportRepository(IConfiguration configuration)
{
    static EmployeeImportRepository() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    private static readonly string[] SampleColumns = [
        "ClientCode", "EmployeeCode", "FirstName", "LastName", "Gender", "DateOfJoining", "WorkEmail", "Department", "Designation", "WorkLocation",
        "ReportingManagerCode", "PortalAccess", "SalaryStructureId", "AnnualCtc", "BASIC", "HRA", "SPAL", "PF", "DOB", "PAN", "Aadhaar", "Mobile", "PersonalAddress",
        "Bank", "Account", "IFSC", "PaymentMode", "IsActive"
    ];

    private static readonly string[] SampleRow = [
        "ACME", "EMP001", "Aarav", "Sharma", "Male", "2026-04-01", "aarav@example.com", "Engineering", "Software Engineer", "Head Office",
        "", "true", "201", "900000", "30000", "15000", "30000", "1800", "1995-01-20", "ABCDE1234F", "123412341234", "9876543210", "Bengaluru",
        "HDFC", "1234567890", "HDFC0001234", "Bank Transfer", "true"
    ];

    public static byte[] SampleExcel() => Xlsx([SampleColumns, SampleRow]);

    public async Task<EmployeeImportResult> ImportAsync(int clientId, IFormFile file)
    {
        var result = new EmployeeImportResult();
        var rows = await ParseAsync(file);
        if (rows.Count < 2) { result.Errors.Add("Data rows required hain."); return result; }
        var columns = rows[0];

        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        var locations = (await db.QueryAsync<LocationRef>("SELECT Id, Name FROM WorkLocations")).ToDictionary(x => Key(x.Name), x => x.Id);
        var clients = await ClientMapAsync(db);
        var managers = (await db.QueryAsync<EmployeeRef>("SELECT Id, ClientId, EmployeeCode FROM Employees")).ToDictionary(x => $"{x.ClientId}:{Key(x.EmployeeCode)}", x => x.Id);
        var components = await SalaryComponentsAsync(db);
        await using var tx = await db.BeginTransactionAsync();

        foreach (var values in rows.Skip(1))
        {
            var rowNo = result.ImportedCount + result.UpdatedCount + result.SkippedCount + 2;
            if (values.All(string.IsNullOrWhiteSpace)) continue;
            var employee = Build(clientId, columns, values, locations, clients, managers, components);
            if (employee.ClientId <= 0 || string.IsNullOrWhiteSpace(employee.EmployeeCode) || string.IsNullOrWhiteSpace(employee.FirstName))
            {
                result.SkippedCount++;
                result.Errors.Add($"Row {rowNo}: Client, EmployeeCode aur FirstName required hain.");
                continue;
            }
            var existingId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM Employees WHERE ClientId=@ClientId AND EmployeeCode=@EmployeeCode", employee, tx);
            if (existingId is null)
            {
                await db.ExecuteAsync(@"INSERT INTO Employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,WorkLocationId,ReportingManagerId,PortalAccess,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive)
VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@WorkLocationId,@ReportingManagerId,@PortalAccess,@SalaryStructureId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive);", employee, tx);
                result.ImportedCount++;
            }
            else
            {
                employee.Id = existingId.Value;
                await db.ExecuteAsync(@"UPDATE Employees SET FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,WorkLocationId=@WorkLocationId,ReportingManagerId=@ReportingManagerId,PortalAccess=@PortalAccess,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,SalaryJson=@SalaryJson,PersonalJson=@PersonalJson,PaymentJson=@PaymentJson,IsActive=@IsActive WHERE Id=@Id", employee, tx);
                result.UpdatedCount++;
            }
        }
        await tx.CommitAsync();
        return result;
    }

    private static Employee Build(int selectedClientId, List<string> columns, List<string> values, Dictionary<string, int> locations, Dictionary<string, int> clients, Dictionary<string, int> managers, List<SalaryComponentRef> components)
    {
        string v(params string[] names) { var i = columns.FindIndex(c => names.Any(n => Key(c) == Key(n))); return i >= 0 && i < values.Count ? values[i].Trim() : ""; }
        var clientText = v("ClientId", "ClientCode", "ClientName", "Client");
        var clientId = selectedClientId > 0 ? selectedClientId : int.TryParse(clientText, out var parsedClientId) ? parsedClientId : clients.GetValueOrDefault(Key(clientText));
        var personal = new Dictionary<string, string> { ["dob"] = v("DOB", "DateOfBirth"), ["pan"] = v("PAN"), ["aadhaar"] = v("Aadhaar"), ["mobile"] = v("Mobile"), ["address"] = v("PersonalAddress", "Address") }.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value);
        var payment = new Dictionary<string, string> { ["bank"] = v("Bank"), ["account"] = v("Account", "AccountNo"), ["ifsc"] = v("IFSC"), ["mode"] = v("PaymentMode") }.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value);
        var locationText = v("WorkLocationId", "WorkLocation");
        var managerText = v("ReportingManagerId", "ReportingManagerCode");
        return new Employee
        {
            ClientId = clientId,
            EmployeeCode = v("EmployeeCode", "Employee Code", "Employee No"),
            FirstName = v("FirstName", "First Name"),
            LastName = v("LastName", "Last Name"),
            Gender = v("Gender"),
            DateOfJoining = v("DateOfJoining", "DOJ", "Joining Date"),
            WorkEmail = v("WorkEmail", "Email"),
            Department = v("Department"),
            Designation = v("Designation"),
            WorkLocationId = int.TryParse(locationText, out var locationId) ? locationId : locations.GetValueOrDefault(Key(locationText)),
            ReportingManagerId = int.TryParse(managerText, out var managerId) ? managerId : managers.GetValueOrDefault($"{clientId}:{Key(managerText)}"),
            PortalAccess = Bool(v("PortalAccess")),
            SalaryStructureId = v("SalaryStructureId", "Salary Template Id"),
            AnnualCtc = decimal.TryParse(v("AnnualCtc", "Annual CTC"), NumberStyles.Number, CultureInfo.InvariantCulture, out var ctc) ? ctc : 0,
            SalaryJson = SalaryJson(v("SalaryJson"), columns, values, components),
            PersonalJson = personal.Count == 0 ? Json(v("PersonalJson")) : JsonSerializer.Serialize(personal),
            PaymentJson = payment.Count == 0 ? Json(v("PaymentJson")) : JsonSerializer.Serialize(payment),
            IsActive = !v("IsActive", "Active").Equals("false", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string SalaryJson(string legacyJson, List<string> columns, List<string> values, List<SalaryComponentRef> components)
    {
        var overrides = new Dictionary<string, string>();
        string valueFor(params string[] names) { var i = columns.FindIndex(c => names.Any(n => Key(c) == Key(n))); return i >= 0 && i < values.Count ? values[i].Trim() : ""; }
        foreach (var component in components)
        {
            var value = valueFor(component.Code, component.Name, $"{component.Code}Monthly", $"{component.Name}Monthly", $"{component.Code}Amount", $"{component.Name}Amount");
            if (!string.IsNullOrWhiteSpace(value)) overrides[component.Id.ToString(CultureInfo.InvariantCulture)] = value.Replace(",", "");
        }
        return overrides.Count > 0 ? JsonSerializer.Serialize(overrides) : Json(legacyJson);
    }

    private static async Task<Dictionary<string, int>> ClientMapAsync(MySqlConnection db)
    {
        var clients = await db.QueryAsync<ClientRef>("SELECT Id, Name, Code FROM Clients");
        return clients.SelectMany(client => new[] { (Key: Key(client.Name), client.Id), (Key: Key(client.Code), client.Id) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key)
            .ToDictionary(x => x.Key, x => x.First().Id);
    }

    private static async Task<List<SalaryComponentRef>> SalaryComponentsAsync(MySqlConnection db)
    {
        var setup = await db.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1") ?? "{}";
        using var document = JsonDocument.Parse(setup);
        if (!document.RootElement.TryGetProperty("salaryComponents", out var items)) return [];
        return items.EnumerateArray().Select(item => new SalaryComponentRef
        {
            Id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Code = item.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "",
            Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""
        }).Where(x => x.Id > 0 && (!string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.Name))).ToList();
    }

    private static async Task<List<List<string>>> ParseAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        if (Path.GetExtension(file.FileName).ToLowerInvariant() is ".xlsx" or ".xls")
        {
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var rows = new List<List<string>>();
            while (reader.Read())
            {
                var row = Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader.GetValue(i))).ToList();
                if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
            }
            return rows;
        }
        using var text = new StreamReader(stream, Encoding.UTF8, true);
        return (await text.ReadToEndAsync()).Split(["\r\n", "\n"], StringSplitOptions.None).Where(x => !string.IsNullOrWhiteSpace(x)).Select(Csv).ToList();
    }

    private static List<string> Csv(string line)
    {
        var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        return row;
    }

    private static string Json(string value) { try { if (!string.IsNullOrWhiteSpace(value)) JsonDocument.Parse(value); return string.IsNullOrWhiteSpace(value) ? "{}" : value; } catch { return "{}"; } }
    private static string Cell(object? value) => value is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static bool Bool(string value) => value is "1" or "true" or "TRUE" or "Yes" or "yes";
    private static string Key(string value) => new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static byte[] Xlsx(IEnumerable<string[]> rows)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(archive, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Add(archive, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(archive, "xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Employees" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(archive, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add(archive, "xl/worksheets/sheet1.xml", SheetXml(rows));
        }
        return stream.ToArray();
    }

    private static string SheetXml(IEnumerable<string[]> rows)
    {
        var xml = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        var rowNumber = 1;
        foreach (var row in rows)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
            for (var index = 0; index < row.Length; index++)
            {
                var cellRef = $"{Column(index)}{rowNumber}";
                xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(row[index])}</t></is></c>");
            }
            xml.Append("</row>");
            rowNumber++;
        }
        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static string Column(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class LocationRef { public int Id { get; set; } public string Name { get; set; } = ""; }
    private sealed class EmployeeRef { public int Id { get; set; } public int ClientId { get; set; } public string EmployeeCode { get; set; } = ""; }
    private sealed class ClientRef { public int Id { get; set; } public string Name { get; set; } = ""; public string Code { get; set; } = ""; }
    private sealed class SalaryComponentRef { public int Id { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
}
