using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
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
        var provided = batch.Rows.Where(row => !string.IsNullOrWhiteSpace(row.EmployeeCode)).ToArray();
        var duplicate = provided.GroupBy(row => ExcelPayslipVarianceService.NormalizeText(row.EmployeeCode)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Employee code {duplicate.First().EmployeeCode} appears more than once. Give each employee a unique code before saving.");

        var unassigned = missing.Where(row => string.IsNullOrWhiteSpace(row.EmployeeCode)).ToArray();
        if (unassigned.Length > 0)
        {
            var prefix = ExcelPayslipVarianceService.NormalizeText(clientCode);
            var used = historicalCodes.Concat(masters.Select(employee => employee.EmployeeCode)).Concat(provided.Select(row => row.EmployeeCode))
                .Select(ExcelPayslipVarianceService.NormalizeText).Where(code => code.Length > 0).ToHashSet(StringComparer.Ordinal);
            var expression = new Regex("^" + Regex.Escape(prefix) + @"([0-9]+)$", RegexOptions.CultureInvariant);
            var suffixes = used.Select(code => expression.Match(code)).Where(match => match.Success).Select(match => match.Groups[1].Value).ToArray();
            var width = Math.Clamp(suffixes.Length > 0 ? suffixes.Max(suffix => suffix.Length) : 5, 1, 12);
            var next = suffixes.Select(suffix => BigInteger.Parse(suffix, CultureInfo.InvariantCulture)).DefaultIfEmpty(BigInteger.Zero).Max() + 1;
            foreach (var row in unassigned)
            {
                string code;
                do { code = prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'); next++; }
                while (!used.Add(code));
                if (code.Length > 80) throw new InvalidOperationException("The generated employee code exceeds 80 characters. Map a shorter employee code in the workbook.");
                row.EmployeeCode = code;
            }
        }
        if (source is null) return;

        var column = source.Columns.SingleOrDefault(item => item.Kind == "employeeCode");
        var appended = column is null;
        if (column is null)
        {
            if (source.ColumnCount >= 512)
                throw new InvalidOperationException("The worksheet already has 512 columns. Map an Employee Code column before saving.");
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
}
