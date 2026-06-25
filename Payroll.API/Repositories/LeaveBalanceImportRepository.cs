using Dapper;
using ExcelDataReader;
using MySqlConnector;
using Payroll.API.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Payroll.API.Repositories;

public class LeaveBalanceImportRepository(IConfiguration configuration)
{
    private static readonly string[] RequiredFields = ["Employee Number", "Leave Type Code", "Balance As Of Date", "Opening Balance"];

    static LeaveBalanceImportRepository()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private MySqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        return new MySqlConnection(connectionString);
    }

    public async Task<string> GetSampleCsvAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var leaveTypes = (await connection.QueryAsync<string>("SELECT Code FROM leave_types WHERE client_id=@ClientId AND is_active=TRUE ORDER BY Name", new { ClientId = clientId })).ToList();
        var rows = new List<string> { "Employee Number,Leave Type Code,Balance As Of Date,Opening Balance" };
        foreach (var leaveType in leaveTypes)
            rows.Add($"\"\",\"{leaveType.Replace("\"", "\"\"")}\",,");
        return string.Join(Environment.NewLine, rows);
    }

    public async Task<LeaveBalanceImportPreview> PreviewAsync(int clientId, IFormFile file, string encodingName, string? mappingJson)
    {
        var rows = await ParseFileAsync(file, encodingName);
        var columns = rows.FirstOrDefault() ?? [];
        var dataRows = rows.Skip(1).ToList();
        var mapping = string.IsNullOrWhiteSpace(mappingJson)
            ? AutoMap(columns)
            : JsonSerializer.Deserialize<LeaveBalanceImportMapping>(mappingJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? AutoMap(columns);
        var preview = new LeaveBalanceImportPreview { FileName = file.FileName, Columns = columns, Mapping = mapping, UnmappedFields = GetUnmappedFields(mapping) };
        if (preview.UnmappedFields.Count > 0) return preview;
        var employees = await GetEmployeesAsync(clientId);
        var leaveTypes = await GetLeaveTypesAsync(clientId);
        preview.ValidRecords = dataRows.Select((values, index) => BuildRow(index + 2, columns, values, mapping)).ToList();
        ValidateRows(preview.ValidRecords, employees, leaveTypes);
        preview.ErrorRecords = preview.ValidRecords.Where(row => !row.IsValid).ToList();
        preview.ValidRecords = preview.ValidRecords.Where(row => row.IsValid).ToList();
        return preview;
    }

    public async Task<LeaveBalanceImportResult> ImportAsync(FinalizeLeaveBalanceImportRequest request, string userEmail)
    {
        var employees = await GetEmployeesAsync(request.ClientId);
        var leaveTypes = await GetLeaveTypesAsync(request.ClientId);
        ValidateRows(request.ValidRecords, employees, leaveTypes);
        var validRows = request.ValidRecords.Where(row => row.IsValid).ToList();
        var errors = request.ErrorRecords.Concat(request.ValidRecords.Where(row => !row.IsValid)).ToList();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var logId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO leave_balance_import_logs (client_id, file_name, encoding, total_records, imported_records, skipped_records, mapping_json, created_by)
VALUES (@ClientId, @FileName, @Encoding, @TotalRecords, @ImportedRecords, @SkippedRecords, @MappingJson, @CreatedBy); SELECT LAST_INSERT_ID();", new { request.ClientId, request.FileName, request.Encoding, TotalRecords = validRows.Count + errors.Count, ImportedRecords = validRows.Count, SkippedRecords = errors.Count, MappingJson = JsonSerializer.Serialize(request.Mapping), CreatedBy = userEmail }, transaction);
        foreach (var row in validRows)
        {
            var employee = employees[Normalize(row.EmployeeNumber)];
            var leaveType = leaveTypes[Normalize(row.LeaveType)];
            var balanceDate = ParseDate(row.Date)!.Value;
            var count = decimal.Parse(row.Count, NumberStyles.Number, CultureInfo.InvariantCulture);
            await connection.ExecuteAsync(@"INSERT INTO employee_leave_balances (client_id, employee_id, leave_type_id, balance_date, balance_count)
VALUES (@ClientId, @EmployeeId, @LeaveTypeId, @BalanceDate, @BalanceCount)
ON DUPLICATE KEY UPDATE balance_count=VALUES(balance_count);", new { request.ClientId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, BalanceDate = balanceDate, BalanceCount = count }, transaction);
        }
        foreach (var error in errors)
            await connection.ExecuteAsync(@"INSERT INTO leave_balance_import_errors (import_log_id, row_no, employee_number, leave_type, date_text, count_text, error_message)
VALUES (@LogId, @RowNumber, @EmployeeNumber, @LeaveType, @Date, @Count, @ErrorMessage);", new { LogId = logId, error.RowNumber, error.EmployeeNumber, error.LeaveType, error.Date, error.Count, ErrorMessage = string.Join("; ", error.Errors) }, transaction);
        await transaction.CommitAsync();
        return new LeaveBalanceImportResult { LogId = logId, ImportedCount = validRows.Count, SkippedCount = errors.Count };
    }

    private async Task<Dictionary<string, EmployeeRef>> GetEmployeesAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.QueryAsync<EmployeeRef>(@"SELECT e.Id, e.EmployeeCode, e.IsActive, COALESCE(e.Department, '') AS Department,
COALESCE(e.Designation, '') AS Designation, COALESCE(e.Gender, '') AS Gender, COALESCE(w.Name, '') AS WorkLocation
FROM Employees e
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId", new { ClientId = clientId });
        return rows.GroupBy(row => Normalize(row.EmployeeCode)).ToDictionary(group => group.Key, group => group.First());
    }

    private async Task<Dictionary<string, LeaveTypeRef>> GetLeaveTypesAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.QueryAsync<LeaveTypeRef>(@"SELECT lt.Id, lt.Code, lt.Name, lt.Is_Active AS IsActive,
COALESCE(p.entitlement, 0) AS Entitlement, COALESCE(p.carry_forward_unused_leaves, FALSE) AS CarryForwardUnusedLeaves,
p.max_carry_forward_limit AS MaxCarryForwardLimit, p.effective_from AS EffectiveFrom, p.expires_on AS ExpiresOn,
COALESCE(a.applicability_mode, 'All employees') AS ApplicabilityMode, COALESCE(a.work_location, '') AS WorkLocation,
COALESCE(a.department, '') AS Department, COALESCE(a.designation, '') AS Designation, COALESCE(a.gender, '') AS Gender
FROM leave_types lt
LEFT JOIN leave_type_policies p ON p.leave_type_id=lt.Id
LEFT JOIN leave_type_applicability a ON a.leave_type_id=lt.Id
WHERE lt.client_id=@ClientId", new { ClientId = clientId });
        return rows.SelectMany(row => new[] { (Key: Normalize(row.Code), Value: row), (Key: Normalize(row.Name), Value: row) }).GroupBy(row => row.Key).ToDictionary(group => group.Key, group => group.First().Value);
    }

    private static async Task<List<List<string>>> ParseFileAsync(IFormFile file, string encodingName)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        await using var stream = file.OpenReadStream();
        if (extension is ".xlsx" or ".xls")
        {
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var rows = new List<List<string>>();
            do
            {
                while (reader.Read())
                {
                    var row = Enumerable.Range(0, reader.FieldCount).Select(index => FormatCell(reader.GetValue(index))).ToList();
                    if (row.Any(cell => !string.IsNullOrWhiteSpace(cell))) rows.Add(row);
                }
            } while (rows.Count == 0 && reader.NextResult());
            return rows;
        }
        using var textReader = new StreamReader(stream, GetEncoding(encodingName), detectEncodingFromByteOrderMarks: true);
        var csv = await textReader.ReadToEndAsync();
        return csv.Split(["\r\n", "\n"], StringSplitOptions.None).Where(line => !string.IsNullOrWhiteSpace(line)).Select(ParseCsvLine).ToList();
    }

    private static LeaveBalanceImportMapping AutoMap(List<string> columns)
    {
        string find(params string[] names) => columns.FirstOrDefault(column => names.Any(name => Normalize(column) == Normalize(name))) ?? string.Empty;
        return new LeaveBalanceImportMapping
        {
            EmployeeNumber = find("Employee Number", "Employee Code", "Employee No"),
            LeaveType = find("Leave Type Code", "Leave Type", "Leave Code"),
            Date = find("Balance As Of Date", "Balance Date", "Date"),
            Count = find("Opening Balance", "Count", "Balance", "Leave Count")
        };
    }

    private static List<string> GetUnmappedFields(LeaveBalanceImportMapping mapping)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(mapping.EmployeeNumber)) missing.Add("Employee Number");
        if (string.IsNullOrWhiteSpace(mapping.LeaveType)) missing.Add("Leave Type Code");
        if (string.IsNullOrWhiteSpace(mapping.Date)) missing.Add("Balance As Of Date");
        if (string.IsNullOrWhiteSpace(mapping.Count)) missing.Add("Opening Balance");
        return missing;
    }

    private static LeaveBalanceImportRow BuildRow(int rowNumber, List<string> columns, List<string> values, LeaveBalanceImportMapping mapping)
    {
        string value(string column) { var index = columns.FindIndex(item => item.Equals(column, StringComparison.OrdinalIgnoreCase)); return index >= 0 && index < values.Count ? values[index].Trim() : string.Empty; }
        return new LeaveBalanceImportRow { RowNumber = rowNumber, EmployeeNumber = value(mapping.EmployeeNumber), LeaveType = value(mapping.LeaveType), Date = value(mapping.Date), Count = value(mapping.Count) };
    }

    private static void ValidateRows(List<LeaveBalanceImportRow> rows, Dictionary<string, EmployeeRef> employees, Dictionary<string, LeaveTypeRef> leaveTypes)
    {
        foreach (var row in rows) ValidateRow(row, employees, leaveTypes);
        foreach (var duplicateGroup in rows
            .Where(row => !string.IsNullOrWhiteSpace(row.EmployeeNumber) && !string.IsNullOrWhiteSpace(row.LeaveType) && ParseDate(row.Date) is not null)
            .GroupBy(row => $"{Normalize(row.EmployeeNumber)}|{Normalize(row.LeaveType)}|{ParseDate(row.Date):yyyy-MM-dd}")
            .Where(group => group.Count() > 1))
        {
            foreach (var row in duplicateGroup)
            {
                row.Errors.Add($"Duplicate opening balance for employee '{row.EmployeeNumber}', leave type '{row.LeaveType}' and date '{ParseDate(row.Date):yyyy-MM-dd}'.");
                row.IsValid = false;
            }
        }
    }

    private static void ValidateRow(LeaveBalanceImportRow row, Dictionary<string, EmployeeRef> employees, Dictionary<string, LeaveTypeRef> leaveTypes)
    {
        row.Errors.Clear();
        EmployeeRef? employee = null;
        LeaveTypeRef? leaveType = null;
        if (string.IsNullOrWhiteSpace(row.EmployeeNumber)) row.Errors.Add("Employee Number is required.");
        else if (!employees.TryGetValue(Normalize(row.EmployeeNumber), out employee)) row.Errors.Add($"Employee Number '{row.EmployeeNumber}' does not exist for the selected client.");
        else if (!employee.IsActive) row.Errors.Add($"Employee '{row.EmployeeNumber}' is inactive.");

        if (string.IsNullOrWhiteSpace(row.LeaveType)) row.Errors.Add("Leave Type Code is required.");
        else if (!leaveTypes.TryGetValue(Normalize(row.LeaveType), out leaveType)) row.Errors.Add($"Leave Type Code '{row.LeaveType}' does not exist for the selected client.");
        else if (!leaveType.IsActive) row.Errors.Add($"Leave Type '{leaveType.Code}' ({leaveType.Name}) is inactive.");

        var balanceDate = ParseDate(row.Date);
        if (string.IsNullOrWhiteSpace(row.Date)) row.Errors.Add("Balance As Of Date is required.");
        else if (balanceDate is null) row.Errors.Add($"Balance As Of Date '{row.Date}' is invalid. Use YYYY-MM-DD.");
        else if (leaveType is not null)
        {
            if (leaveType.EffectiveFrom is not null && balanceDate.Value < leaveType.EffectiveFrom.Value.Date)
                row.Errors.Add($"Balance As Of Date cannot be before {leaveType.Code} effective date {leaveType.EffectiveFrom:yyyy-MM-dd}.");
            if (leaveType.ExpiresOn is not null && balanceDate.Value > leaveType.ExpiresOn.Value.Date)
                row.Errors.Add($"Balance As Of Date cannot be after {leaveType.Code} expiry date {leaveType.ExpiresOn:yyyy-MM-dd}.");
        }

        if (string.IsNullOrWhiteSpace(row.Count)) row.Errors.Add("Opening Balance is required.");
        else if (!decimal.TryParse(row.Count, NumberStyles.Number, CultureInfo.InvariantCulture, out var count)) row.Errors.Add($"Opening Balance '{row.Count}' must be numeric.");
        else
        {
            if (count < 0) row.Errors.Add("Opening Balance cannot be negative.");
            if (leaveType is not null && leaveType.Entitlement > 0)
            {
                var maxAllowed = leaveType.Entitlement + (leaveType.CarryForwardUnusedLeaves ? leaveType.MaxCarryForwardLimit ?? 0 : 0);
                if (count > maxAllowed)
                    row.Errors.Add($"Opening Balance {count:0.##} exceeds {leaveType.Code} maximum allowed balance {maxAllowed:0.##} (entitlement {leaveType.Entitlement:0.##}{(leaveType.CarryForwardUnusedLeaves ? $" + carry-forward {leaveType.MaxCarryForwardLimit.GetValueOrDefault():0.##}" : "")}).");
            }
        }

        if (employee is not null && leaveType is not null && leaveType.ApplicabilityMode != "All employees")
        {
            if (!Matches(leaveType.WorkLocation, employee.WorkLocation)) row.Errors.Add($"{leaveType.Code} applies only to work location '{leaveType.WorkLocation}', but employee is in '{employee.WorkLocation}'.");
            if (!Matches(leaveType.Department, employee.Department)) row.Errors.Add($"{leaveType.Code} applies only to department '{leaveType.Department}', but employee is in '{employee.Department}'.");
            if (!Matches(leaveType.Designation, employee.Designation)) row.Errors.Add($"{leaveType.Code} applies only to designation '{leaveType.Designation}', but employee designation is '{employee.Designation}'.");
            if (!Matches(leaveType.Gender, employee.Gender)) row.Errors.Add($"{leaveType.Code} applies only to gender '{leaveType.Gender}', but employee gender is '{employee.Gender}'.");
        }
        row.IsValid = row.Errors.Count == 0;
    }

    private static DateTime? ParseDate(string value)
    {
        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.Date;
        return double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var oaDate) && oaDate > 20000 ? DateTime.FromOADate(oaDate).Date : null;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var c = line[index];
            if (c == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { values.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        values.Add(current.ToString());
        return values;
    }

    private static Encoding GetEncoding(string encodingName)
    {
        try { return Encoding.GetEncoding(string.IsNullOrWhiteSpace(encodingName) ? "UTF-8" : encodingName); }
        catch { return Encoding.UTF8; }
    }

    private static string FormatCell(object? value) => value switch { null => "", DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
    private static string Normalize(string value) => new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static bool Matches(string criterion, string actual) => string.IsNullOrWhiteSpace(criterion) || Normalize(criterion) == Normalize(actual);
    private sealed class EmployeeRef { public int Id { get; set; } public string EmployeeCode { get; set; } = string.Empty; public bool IsActive { get; set; } public string WorkLocation { get; set; } = string.Empty; public string Department { get; set; } = string.Empty; public string Designation { get; set; } = string.Empty; public string Gender { get; set; } = string.Empty; }
    private sealed class LeaveTypeRef { public int Id { get; set; } public string Code { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public bool IsActive { get; set; } public decimal Entitlement { get; set; } public bool CarryForwardUnusedLeaves { get; set; } public decimal? MaxCarryForwardLimit { get; set; } public DateTime? EffectiveFrom { get; set; } public DateTime? ExpiresOn { get; set; } public string ApplicabilityMode { get; set; } = "All employees"; public string WorkLocation { get; set; } = string.Empty; public string Department { get; set; } = string.Empty; public string Designation { get; set; } = string.Empty; public string Gender { get; set; } = string.Empty; }
}
