using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Used only by the reviewed source-month reader, for rows the ordinary
    // employment annotation could not link. The saved source is never rewritten.
    internal static ExcelPayslipRow CapturedSourceIdentity(ExcelPayslipRow row, ExcelPayslipCalculationSource? source,
        Dictionary<string, string> information, List<ExcelPayrollSetupIssue> issues, ExcelPayslipCodeGeneration? generation = null)
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
        if (codes.Length != 1 || Cell(codes[0].ColumnIndex) is not { } code || !Literal(code)) return row;
        if (!string.IsNullOrWhiteSpace(code.Value))
        {
            // AssignEmployeeCodes replaces an empty code with an untyped SetCell,
            // whereas the complete typed workbook reader retains Text/Number for
            // supplied codes. Recognize that producer shape only in this reviewed
            // reader, with two independently captured government identifiers.
            var panColumns = source.Columns.Where(column => ExcelPayrollSetupRepository.Normalize(column.SourceHeader)
                is "PAN" or "PANNO" or "PANNUMBER").ToArray();
            if (source.CaptureVersion != 1 || !source.CompleteSheetCapture || generation is { Enabled: false }
                || code.DataType != "" || code.Value != row.EmployeeCode || !Regex.IsMatch(code.Value, "^[A-Za-z][A-Za-z0-9]*[0-9]+$")
                || panColumns.Length != 1 || Cell(panColumns[0].ColumnIndex) is not { } pan || !Literal(pan) || pan.DataType != "Text"
                || !Regex.IsMatch(Normalize(pan.Value), "^[A-Z]{5}[0-9]{4}[A-Z]$") || identifiers.Length != 1
                || !source.Columns.Any(column => ExcelPayrollSetupRepository.Normalize(column.SourceHeader)
                    is "AADHAR" or "AADHAAR" or "AADHARNO" or "AADHAARNO" or "AADHARNUMBER" or "AADHAARNUMBER"
                    && Cell(column.ColumnIndex) is { DataType: "Text" or "Number" } id && Literal(id) && Normalize(id.Value) == identifiers[0])) return row;
            information["PAN"] = Normalize(pan.Value);
        }
        // A generated display code cannot contradict a real, unique government
        // identifier when the captured employee-code cell itself was empty.
        return new() { Id = row.Id, SourceRow = row.SourceRow, EmployeeName = row.EmployeeName, EmployeeCode = "", Information = row.Information };
    }
}
