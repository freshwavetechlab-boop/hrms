using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Same prefix + padded max-suffix sequence as smartBulkImport.buildEmployeeCodes.
    // Reuse a verified master identity, then allocate against workspace history and reserved master codes.
    // Employee Master is read-only; name alone never establishes an employee match.
    internal static void AssignEmployeeCodes(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source,
        string clientCode, IEnumerable<string> historicalCodes, IEnumerable<MasterEmployeeIdentity>? masterEmployees = null)
    {
        var missing = batch.Rows.Where(row => string.IsNullOrWhiteSpace(row.EmployeeCode)).ToArray();
        var masters = masterEmployees?.Where(employee => employee.ClientId == batch.ClientId).ToArray() ?? [];
        ReuseMasterEmployeeCodes(batch, masters);
        var splitGroups = batch.Rows.GroupBy(row => ExcelPayslipRowIdentity.NormalizeName(row.EmployeeName))
            .Where(group => ExcelPayslipRowIdentity.IsVerifiedLocationSplit(group, batch.Month)).Select(group => group.ToArray()).ToArray();
        foreach (var group in splitGroups)
        {
            var codes = group.Select(row => row.EmployeeCode).Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (codes.Length == 1) foreach (var row in group.Where(row => string.IsNullOrWhiteSpace(row.EmployeeCode))) row.EmployeeCode = codes[0];
        }
        var provided = batch.Rows.Where(row => !string.IsNullOrWhiteSpace(row.EmployeeCode)).ToArray();
        var duplicate = provided.GroupBy(row => ExcelPayslipVarianceService.NormalizeText(row.EmployeeCode))
            .FirstOrDefault(group => group.Count() > 1 && !ExcelPayslipRowIdentity.IsVerifiedLocationSplit(group, batch.Month));
        if (duplicate is not null)
            throw new InvalidOperationException($"Employee code {duplicate.First().EmployeeCode} appears more than once. Give each employee a unique code before saving.");

        var unassigned = missing.Where(row => string.IsNullOrWhiteSpace(row.EmployeeCode)).ToArray();
        if (unassigned.Length > 0)
        {
            if (batch.EmployeeCodeGeneration is { Enabled: false })
                throw new InvalidOperationException("Some employees still have no code after checking existing Master matches. Enable Generate blanks or map their existing Employee Code before saving.");
            var prefix = ExcelPayslipVarianceService.NormalizeText(clientCode);
            if (batch.EmployeeCodeGeneration is not null && prefix.Length == 0)
                throw new InvalidOperationException("Set this client's code before generating employee codes.");
            var used = historicalCodes.Concat(masters.Select(employee => employee.EmployeeCode)).Concat(provided.Select(row => row.EmployeeCode))
                .Select(ExcelPayslipVarianceService.NormalizeText).Where(code => code.Length > 0).ToHashSet(StringComparer.Ordinal);
            var expression = new Regex("^" + Regex.Escape(prefix) + @"([0-9]+)$", RegexOptions.CultureInvariant);
            var suffixes = used.Select(code => expression.Match(code)).Where(match => match.Success).Select(match => match.Groups[1].Value).ToArray();
            var width = batch.EmployeeCodeGeneration?.Digits ?? Math.Clamp(suffixes.Length > 0 ? suffixes.Max(suffix => suffix.Length) : 5, 1, 12);
            if (width is < 1 or > 12) throw new InvalidOperationException("Employee code sequence digits must be between 1 and 12.");
            var next = suffixes.Select(suffix => BigInteger.Parse(suffix, CultureInfo.InvariantCulture)).DefaultIfEmpty(BigInteger.Zero).Max() + 1;
            foreach (var row in unassigned)
            {
                if (!string.IsNullOrWhiteSpace(row.EmployeeCode)) continue;
                string code;
                do { code = prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'); next++; }
                while (!used.Add(code));
                if (code.Length > 80) throw new InvalidOperationException("The generated employee code exceeds 80 characters. Map a shorter employee code in the workbook.");
                row.EmployeeCode = code;
                foreach (var peer in splitGroups.FirstOrDefault(group => group.Contains(row)) ?? [])
                    if (string.IsNullOrWhiteSpace(peer.EmployeeCode)) peer.EmployeeCode = code;
            }
        }
        if (source is null) return;

        var column = source.Columns.SingleOrDefault(item => item.Kind == "employeeCode");
        var appended = column is null;
        if (column is null)
        {
            if (source.ColumnCount >= 1500)
                throw new InvalidOperationException("The worksheet already has 1500 columns. Map an Employee Code column before saving.");
            column = new() { ColumnIndex = source.ColumnCount++, Kind = "employeeCode", Label = "Employee Code", SourceHeader = "Employee Code" };
            source.Columns.Add(column);
            var header = source.Rows.SingleOrDefault(row => row.SourceRow == source.HeaderRow);
            if (header is null) { header = new() { SourceRow = source.HeaderRow }; source.Rows.Add(header); }
            SetCell(header, column.ColumnIndex, "Employee Code");
        }
        var sourceRows = source.Rows.ToDictionary(row => row.SourceRow);
        foreach (var row in appended ? batch.Rows : missing.AsEnumerable())
            SetCell(sourceRows[row.SourceRow], column.ColumnIndex, row.EmployeeCode);
    }

    private static void SetCell(ExcelPayslipCalculationSourceRow row, int column, string value)
    {
        while (row.Cells.Count <= column) row.Cells.Add(null);
        row.Cells[column] = new() { Value = value };
    }

    // Scoped sequence metadata only: no employee identities or source amounts are returned.
    public async Task<ExcelPayslipCodeOptions> GetEmployeeCodeOptionsAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var client = await db.QueryFirstOrDefaultAsync<Client>("SELECT Id,Name,Code,IsActive FROM clients WHERE Id=@ClientId", new { ClientId = clientId }, tx);
        if (client is null || !IsSupportedClient(client)) throw new InvalidOperationException("Select an active client.");
        var codes = await db.QueryAsync<string>(@"SELECT EmployeeCode FROM employees WHERE ClientId=@ClientId
UNION SELECT codes.employee_code FROM excel_payslip_batches batches
CROSS JOIN JSON_TABLE(batches.batch_json,'$.rows[*]' COLUMNS(employee_code VARCHAR(80) PATH '$.employeeCode' NULL ON EMPTY)) codes
WHERE batches.client_id=@ClientId AND codes.employee_code IS NOT NULL", new { ClientId = clientId }, tx);
        var prefix = ExcelPayslipVarianceService.NormalizeText(client.Code);
        var expression = new Regex("^" + Regex.Escape(prefix) + @"([0-9]+)$", RegexOptions.CultureInvariant);
        var suffixes = codes.Select(ExcelPayslipVarianceService.NormalizeText).Select(code => expression.Match(code))
            .Where(match => match.Success).Select(match => match.Groups[1].Value).ToArray();
        var digits = Math.Clamp(suffixes.Length > 0 ? suffixes.Max(suffix => suffix.Length) : 5, 1, 12);
        var next = suffixes.Select(suffix => BigInteger.Parse(suffix, CultureInfo.InvariantCulture)).DefaultIfEmpty(BigInteger.Zero).Max() + 1;
        await tx.RollbackAsync();
        return new(prefix, digits, next.ToString(CultureInfo.InvariantCulture));
    }
}
