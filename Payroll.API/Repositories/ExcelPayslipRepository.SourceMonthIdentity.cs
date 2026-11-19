using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Used only by the reviewed source-month reader, for rows the ordinary
    // employment annotation could not link. The saved source is never rewritten.
    internal static ExcelPayslipRow CapturedSourceIdentity(ExcelPayslipRow row, ExcelPayslipCalculationSource? source,
        Dictionary<string, string> information, List<ExcelPayrollSetupIssue> issues)
    {
        if (source is null) return row;
        var captured = source.Rows.Where(value => value.SourceRow == row.SourceRow).ToArray();
        if (captured.Length != 1) return row;
        ExcelPayslipCalculationCell? Cell(int index) => index >= 0 && index < captured[0].Cells.Count ? captured[0].Cells[index] : null;
        static bool Literal(ExcelPayslipCalculationCell cell) => (cell.DataType is "" or "Text" or "Number" or "Blank") && string.IsNullOrWhiteSpace(cell.FormulaText)
            && !cell.MissingCachedValue && string.IsNullOrWhiteSpace(cell.Error) && string.IsNullOrWhiteSpace(cell.CalculationError);
        string Normalize(string value) => ExcelPayrollSetupRepository.PayrollIdentityValue(value);
        var aadhaar = new List<string>();
        foreach (var column in source.Columns.Where(column => ExcelPayrollSetupRepository.Normalize(column.SourceHeader)
            is "AADHAR" or "AADHAAR" or "AADHARNO" or "AADHAARNO" or "AADHARNUMBER" or "AADHAARNUMBER"))
        {
            var cell = Cell(column.ColumnIndex);
            if (cell is null || !Literal(cell))
            {
                issues.Add(new("Error", "The captured Aadhaar identifier must be an error-free literal before source-month identity review.", row.Id));
                continue;
            }
            var value = Normalize(cell.Value);
            if (Regex.IsMatch(value, "^[2-9][0-9]{11}$")) aadhaar.Add(value);
        }
        foreach (var key in new[] { "AADHAR", "AADHAAR", "AADHAARNUMBER" })
            if (information.TryGetValue(key, out var value) && Regex.IsMatch(Normalize(value), "^[2-9][0-9]{11}$")) aadhaar.Add(Normalize(value));
        var identifiers = aadhaar.Distinct(StringComparer.Ordinal).ToArray();
        if (identifiers.Length > 1) issues.Add(new("Error", "Captured Aadhaar columns disagree; review the original source identity.", row.Id));
        else if (identifiers.Length == 1) information["AADHAAR"] = identifiers[0];

        var codes = source.Columns.Where(column => column.Kind == "employeeCode").ToArray();
        if (codes.Length != 1 || Cell(codes[0].ColumnIndex) is not { } code || !Literal(code) || !string.IsNullOrWhiteSpace(code.Value)) return row;
        // A generated display code cannot contradict a real, unique government
        // identifier when the captured employee-code cell itself was empty.
        return new() { Id = row.Id, SourceRow = row.SourceRow, EmployeeName = row.EmployeeName, EmployeeCode = "", Information = row.Information };
    }
}
