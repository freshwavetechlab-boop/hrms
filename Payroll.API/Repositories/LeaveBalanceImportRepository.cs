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
    private static readonly string[] RequiredFields = ["Employee Number", "Leave Type", "Date", "Count"];

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

    public async Task<LeaveBalanceImportPreview> PreviewAsync(IFormFile file, string encodingName, string? mappingJson)
    {
        var rows = await ParseFileAsync(file, encodingName);
        var columns = rows.FirstOrDefault() ?? [];
        var dataRows = rows.Skip(1).ToList();
        var mapping = string.IsNullOrWhiteSpace(mappingJson)
            ? AutoMap(columns)
            : JsonSerializer.Deserialize<LeaveBalanceImportMapping>(mappingJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? AutoMap(columns);
        var preview = new LeaveBalanceImportPreview { FileName = file.FileName, Columns = columns, Mapping = mapping, UnmappedFields = GetUnmappedFields(mapping) };
        if (preview.UnmappedFields.Count > 0) return preview;
        var employees = await GetEmployeesAsync();
        var leaveTypes = await GetLeaveTypesAsync();
        foreach (var row in dataRows.Select((values, index) => BuildRow(index + 2, columns, values, mapping)))
            ValidateRow(row, employees, leaveTypes);
        preview.ValidRecords = dataRows.Select((values, index) => BuildRow(index + 2, columns, values, mapping)).ToList();
        foreach (var row in preview.ValidRecords)
            ValidateRow(row, employees, leaveTypes);
        preview.ErrorRecords = preview.ValidRecords.Where(row => !row.IsValid).ToList();
        preview.ValidRecords = preview.ValidRecords.Where(row => row.IsValid).ToList();
        return preview;
    }

    public async Task<LeaveBalanceImportResult> ImportAsync(FinalizeLeaveBalanceImportRequest request, string userEmail)
    {
        var employees = await GetEmployeesAsync();
        var leaveTypes = await GetLeaveTypesAsync();
        foreach (var row in request.ValidRecords)
            ValidateRow(row, employees, leaveTypes);
        var validRows = request.ValidRecords.Where(row => row.IsValid).ToList();
        var errors = request.ErrorRecords.Concat(request.ValidRecords.Where(row => !row.IsValid)).ToList();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var logId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO leave_balance_import_logs (file_name, encoding, total_records, imported_records, skipped_records, mapping_json, created_by)
VALUES (@FileName, @Encoding, @TotalRecords, @ImportedRecords, @SkippedRecords, @MappingJson, @CreatedBy); SELECT LAST_INSERT_ID();", new { request.FileName, request.Encoding, TotalRecords = validRows.Count + errors.Count, ImportedRecords = validRows.Count, SkippedRecords = errors.Count, MappingJson = JsonSerializer.Serialize(request.Mapping), CreatedBy = userEmail }, transaction);
        foreach (var row in validRows)
        {
            var employee = employees[Normalize(row.EmployeeNumber)];
            var leaveType = leaveTypes[Normalize(row.LeaveType)];
            var balanceDate = ParseDate(row.Date)!.Value;
            var count = decimal.Parse(row.Count, NumberStyles.Number, CultureInfo.InvariantCulture);
            await connection.ExecuteAsync(@"INSERT INTO employee_leave_balances (employee_id, leave_type_id, balance_date, balance_count)
VALUES (@EmployeeId, @LeaveTypeId, @BalanceDate, @BalanceCount)
ON DUPLICATE KEY UPDATE balance_count=VALUES(balance_count);", new { EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, BalanceDate = balanceDate, BalanceCount = count }, transaction);
        }
        foreach (var error in errors)
            await connection.ExecuteAsync(@"INSERT INTO leave_balance_import_errors (import_log_id, row_no, employee_number, leave_type, date_text, count_text, error_message)
VALUES (@LogId, @RowNumber, @EmployeeNumber, @LeaveType, @Date, @Count, @ErrorMessage);", new { LogId = logId, error.RowNumber, error.EmployeeNumber, error.LeaveType, error.Date, error.Count, ErrorMessage = string.Join("; ", error.Errors) }, transaction);
        await transaction.CommitAsync();
        return new LeaveBalanceImportResult { LogId = logId, ImportedCount = validRows.Count, SkippedCount = errors.Count };
    }

    private async Task<Dictionary<string, EmployeeRef>> GetEmployeesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.QueryAsync<EmployeeRef>("SELECT Id, EmployeeCode FROM Employees WHERE IsActive = TRUE");
        return rows.GroupBy(row => Normalize(row.EmployeeCode)).ToDictionary(group => group.Key, group => group.First());
    }

    private async Task<Dictionary<string, LeaveTypeRef>> GetLeaveTypesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.QueryAsync<LeaveTypeRef>("SELECT Id, Code, Name FROM leave_types WHERE is_active = TRUE");
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
        return new LeaveBalanceImportMapping { EmployeeNumber = find("Employee Number", "Employee Code", "Employee No"), LeaveType = find("Leave Type", "Leave Code"), Date = find("Date", "Balance Date"), Count = find("Count", "Balance", "Leave Count") };
    }

    private static List<string> GetUnmappedFields(LeaveBalanceImportMapping mapping)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(mapping.EmployeeNumber)) missing.Add("Employee Number");
        if (string.IsNullOrWhiteSpace(mapping.LeaveType)) missing.Add("Leave Type");
        if (string.IsNullOrWhiteSpace(mapping.Date)) missing.Add("Date");
        if (string.IsNullOrWhiteSpace(mapping.Count)) missing.Add("Count");
        return missing;
    }

    private static LeaveBalanceImportRow BuildRow(int rowNumber, List<string> columns, List<string> values, LeaveBalanceImportMapping mapping)
    {
        string value(string column) { var index = columns.FindIndex(item => item.Equals(column, StringComparison.OrdinalIgnoreCase)); return index >= 0 && index < values.Count ? values[index].Trim() : string.Empty; }
        return new LeaveBalanceImportRow { RowNumber = rowNumber, EmployeeNumber = value(mapping.EmployeeNumber), LeaveType = value(mapping.LeaveType), Date = value(mapping.Date), Count = value(mapping.Count) };
    }

    private static void ValidateRow(LeaveBalanceImportRow row, Dictionary<string, EmployeeRef> employees, Dictionary<string, LeaveTypeRef> leaveTypes)
    {
        row.Errors.Clear();
        if (string.IsNullOrWhiteSpace(row.EmployeeNumber) || !employees.ContainsKey(Normalize(row.EmployeeNumber))) row.Errors.Add("Employee number does not exist.");
        if (string.IsNullOrWhiteSpace(row.LeaveType) || !leaveTypes.ContainsKey(Normalize(row.LeaveType))) row.Errors.Add("Leave type does not exist.");
        if (ParseDate(row.Date) is null) row.Errors.Add("Date format is invalid.");
        if (!decimal.TryParse(row.Count, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) row.Errors.Add("Count must be numeric.");
        row.IsValid = row.Errors.Count == 0;
    }

    private static DateTime? ParseDate(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.Date;
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
    private sealed class EmployeeRef { public int Id { get; set; } public string EmployeeCode { get; set; } = string.Empty; }
    private sealed class LeaveTypeRef { public int Id { get; set; } public string Code { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; }
}
