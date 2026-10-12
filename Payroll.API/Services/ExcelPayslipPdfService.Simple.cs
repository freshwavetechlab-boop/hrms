using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed partial class ExcelPayslipPdfService
{
    private static readonly XBrush SimpleBand = new XSolidBrush(XColor.FromArgb(211, 145, 145));
    private const string EmployerNote = "Paid by the employer; does not reduce your In Hand Salary.";
    private const double EmployerWidth = Width * .65;
    internal sealed record SimpleField(string Label, string Value);
    private sealed record SimplePlan(double Size, IReadOnlyList<SimpleField> Information, double[] InformationHeights,
        IReadOnlyList<SimpleField> Income, IReadOnlyList<SimpleField> Deductions, double[] MoneyHeights,
        IReadOnlyList<SimpleField> Employer, double[] EmployerHeights, decimal EmployerTotal,
        double EmployerTotalHeight, double FooterHeight, double FooterNoteHeight);
    private static string FieldKey(string label) => Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]", "");

    private static string EmployerLabel(string label)
    {
        var key = FieldKey(label);
        var name = key.StartsWith("pf", StringComparison.Ordinal) || key.StartsWith("epf", StringComparison.Ordinal)
            || key.StartsWith("employerpf", StringComparison.Ordinal) || key.StartsWith("employerepf", StringComparison.Ordinal)
            || key.Contains("providentfund", StringComparison.Ordinal) ? "Employer EPF"
            : key.StartsWith("esi", StringComparison.Ordinal) || key.StartsWith("employeresi", StringComparison.Ordinal)
                ? "Employer ESIC" : null;
        if (name is null) return label;
        var percentage = Regex.Match(label, @"\d+(?:\.\d+)?\s*%");
        return percentage.Success ? $"{name} ({percentage.Value})" : name;
    }

    internal static IReadOnlyList<SimpleField> SimpleInformation(ExcelPayslipBatch batch, ExcelPayslipRow row)
    {
        string Value(params string[] aliases) => row.Information.FirstOrDefault(item => aliases.Contains(FieldKey(item.Label)))?.Value ?? "";
        var selectedUan = row.Information.FirstOrDefault(item => FieldKey(item.Label) == "uan");
        var uans = row.Information.Where(item => FieldKey(item.Label).StartsWith("uan", StringComparison.Ordinal))
            .Select(item => item.Value.Trim().TrimStart('\'')).Where(value => Regex.IsMatch(value, "^[0-9]{12}$")).Distinct().ToArray();
        if (selectedUan is null && uans.Length > 1) throw new InvalidOperationException($"Excel row {row.SourceRow} contains different UAN numbers. Review the source and label the approved column UAN before using the simple salary slip.");
        return [
            new("Employee Name", row.EmployeeName), new("Department", batch.ClientName),
            new("UAN", selectedUan?.Value ?? uans.FirstOrDefault() ?? Value("uan", "uane", "uanab")), new("Esic No", Value("esicno", "esino", "esicnumber", "esinumber")),
            new("Designation", Value("designation", "designationmanpowercategory", "manpowercategory")), new("Number of Working Days Attended", Value("numberofworkingdaysattended", "attendance", "daysattended", "workingdays", "paiddays")),
            new("Account No", Value("accountno", "accountnumber", "bankaccountnumber", "bankaccountno")), new("IFSC Code", Value("ifsccode", "ifsc"))
        ];
    }

    internal static IReadOnlyList<SimpleField> SimpleRates(ExcelPayslipRow row, ExcelPayslipCalculationSource? source, int decimals)
    {
        return row.Information.Where(item => FieldKey(item.Label) is "rate" or "ratemonthly" or "monthlyrate" or "rateday" or "rateperday" or "dailyrate")
            .Select(item =>
            {
                var key = FieldKey(item.Label);
                var daily = key is "rateday" or "rateperday" or "dailyrate";
                var numeric = decimal.TryParse(item.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate);
                if (!daily && numeric && TryRateDays(source, row, item.Label, rate, out var days))
                {
                    rate /= days;
                    daily = true;
                }
                var label = daily ? "Rate/Day" : key is "ratemonthly" or "monthlyrate" ? "Rate (Monthly)" : "Rate";
                return new SimpleField(label, numeric ? Amount(rate, decimals) : item.Value);
            }).ToArray();
    }

    // Recognize the saved monthly-rate / calculation-days * attendance formula.
    // Do not guess calendar days or divide by attendance (which may be partial or zero).
    private static bool TryRateDays(ExcelPayslipCalculationSource? source, ExcelPayslipRow row, string label, decimal rate, out decimal days)
    {
        days = 0;
        if (source is null) return false;
        var rateColumns = source.Columns.Where(c => c.Kind == "info" && c.Label == label).ToArray();
        if (rateColumns.Length != 1) return false;
        var rateAddress = ExcelPayslipCalculationService.Address(rateColumns[0].ColumnIndex, row.SourceRow);
        var sourceRow = source.Rows.FirstOrDefault(r => r.SourceRow == row.SourceRow);
        if (sourceRow is null) return false;
        ExcelPayslipCalculationCell? Cell(int column, int number) => source.Rows.FirstOrDefault(r => r.SourceRow == number)?.Cells.ElementAtOrDefault(column);
        bool Number(ExcelPayslipCalculationCell? cell, out decimal value)
        {
            value = 0;
            return cell is { Error: null, CalculationError: null, MissingCachedValue: false }
                && decimal.TryParse(cell.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }
        bool Address(string reference, out (int Column, int Row) address)
        {
            address = default;
            try { address = ExcelPayslipCalculationService.ParseAddress(reference); return true; }
            catch (InvalidOperationException) { return false; }
            catch (OverflowException) { return false; }
        }
        if (!Number(Cell(rateColumns[0].ColumnIndex, row.SourceRow), out var savedRate) || savedRate != rate) return false;
        var divisors = new List<decimal>();
        foreach (var column in source.Columns.Where(c => c.Kind == "earning"))
        {
            var formula = sourceRow.Cells.ElementAtOrDefault(column.ColumnIndex)?.FormulaText;
            if (string.IsNullOrWhiteSpace(formula)) continue;
            var match = Regex.Match(formula,
                @"^\s*=?\s*(?<open>\()?\s*(?<rate>\$?[A-Z]{1,3}\$?[1-9][0-9]*)\s*/\s*(?<days>\$?[A-Z]{1,3}\$?[1-9][0-9]*|[0-9]+(?:\.[0-9]+)?)\s*(?(open)\))\s*\*\s*(?<attendance>\$?[A-Z]{1,3}\$?[1-9][0-9]*)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || match.Groups["rate"].Value.Replace("$", "").ToUpperInvariant() != rateAddress) continue;
            if (!Address(match.Groups["attendance"].Value, out var attendance)) continue;
            if (attendance.Row != row.SourceRow || !source.Columns.Any(c => c.ColumnIndex == attendance.Column && c.Kind == "info")) continue;
            var divisor = match.Groups["days"].Value;
            decimal value;
            if (!decimal.TryParse(divisor, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                if (!Address(divisor, out var address)) continue;
                if (!Number(Cell(address.Column, address.Row), out value)) continue;
            }
            if (value is > 0 and <= 31) divisors.Add(value);
        }
        if (divisors.Count == 0 || divisors.Distinct().Count() != 1) return false;
        days = divisors[0];
        return true;
    }

    private static SimplePlan MakeSimplePlan(XGraphics gfx, ExcelPayslipBatch batch, ExcelPayslipRow row, int decimals = 2,
        ExcelPayslipCalculationSource? calculationSource = null)
    {
        var information = SimpleInformation(batch, row);
        var income = SimpleRates(row, calculationSource, decimals).ToList();
        income.AddRange(row.Earnings.Where(item => item.Amount != 0).Select(item => new SimpleField(FieldKey(item.Label) is "wages" or "earnedwages" ? "Earn Salary" : item.Label, Amount(item.Amount, decimals))));
        var deductions = row.Deductions.Where(item => item.Amount != 0).Select(item => new SimpleField(item.Label, Amount(item.Amount, decimals))).ToList();
        var employerAmounts = row.EmployerContributions.Where(item => item.Amount != 0).ToList();
        var employer = employerAmounts.Select(item => new SimpleField(EmployerLabel(item.Label), Amount(item.Amount, decimals))).ToList();
        var employerTotal = employerAmounts.Sum(item => item.Amount);
        for (var size = 11d; size >= 7; size -= .5)
        {
            double Height(string text, double width, bool bold = false) => Math.Max(1, Wrap(gfx, text, Font(size, bold), width).Count) * size * 1.36 + 12;
            var infoHeights = Enumerable.Range(0, 4).Select(index => information.Skip(index * 2).Take(2)
                .Max(field => Math.Max(Height(field.Label, 119, true), Height(field.Value, Width / 2 - 147, true)))).ToArray();
            double MoneyHeight(SimpleField field) => Math.Max(Height(field.Label, Width / 2 - 103, true),
                decimal.TryParse(field.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? 0 : Height(field.Value, 75, true));
            var moneyHeights = Enumerable.Range(0, Math.Max(4, Math.Max(income.Count, deductions.Count))).Select(index =>
                Math.Max(28, Math.Max(index < income.Count ? MoneyHeight(income[index]) : 0,
                    index < deductions.Count ? MoneyHeight(deductions[index]) : 0))).ToArray();
            var employerHeights = employer.Select(field => Math.Max(28, Height(field.Label, EmployerWidth - 103, true))).ToArray();
            var employerTotalHeight = Math.Max(28, Height("Total Employer Contributions", EmployerWidth - 103, true));
            var footerHeight = Math.Max(76, 26 + employerHeights.Sum() + employerTotalHeight);
            var noteHeight = Wrap(gfx, EmployerNote, Font(8), Width - 14).Count * 8 * 1.36 + 12;
            if (Top + HeaderHeight + 25 + infoHeights.Sum() + 14 + 52 + moneyHeights.Sum() + 28 + 30 + footerHeight + noteHeight + 26 <= Bottom)
                return new(size, information, infoHeights, income, deductions, moneyHeights, employer, employerHeights,
                    employerTotal, employerTotalHeight, footerHeight, noteHeight);
        }
        throw new InvalidOperationException($"Excel row {row.SourceRow} has too much text to fit legibly on one simple salary slip. Shorten visible labels or reduce mapped components.");
    }

    private static void DrawSimplePage(XGraphics gfx, ExcelPayslipBatch batch, ExcelPayslipRow row, XImage logo, XImage? seal, int decimals,
        ExcelPayslipCalculationSource? calculationSource)
    {
        var plan = MakeSimplePlan(gfx, batch, row, decimals, calculationSource);
        var bold = Font(plan.Size, true);
        DrawBrandingHeader(gfx, logo);
        var y = Top + HeaderHeight;
        Box(Left, Width - 185, 25, "Salary Slip", centered: true, fill: Pink);
        Box(Left + Width - 185, 54, 25, "Month", fill: Pink);
        Box(Left + Width - 131, 131, 25, MonthLabel(batch.Month), fill: Pink);
        y += 25;
        for (var index = 0; index < 4; index++)
        {
            var height = plan.InformationHeights[index];
            for (var side = 0; side < 2; side++)
            {
                var field = plan.Information[index * 2 + side]; var x = Left + side * Width / 2;
                Box(x, 133, height, field.Label);
                Box(x + 133, Width / 2 - 133, height, field.Value);
            }
            y += height;
        }
        Box(Left, Width, 14, ""); y += 14;
        Box(Left, Width / 2, 26, "Income", fill: SimpleBand); Box(Left + Width / 2, Width / 2, 26, "Deductions", fill: SimpleBand); y += 26;
        for (var side = 0; side < 2; side++)
        {
            var x = Left + side * Width / 2;
            Box(x, Width / 2 - 89, 26, "Particulars", fill: Pink);
            Box(x + Width / 2 - 89, 89, 26, "Amount", centered: true, fill: Pink);
        }
        y += 26;
        for (var index = 0; index < plan.MoneyHeights.Length; index++)
        {
            var height = plan.MoneyHeights[index];
            MoneyCells(index < plan.Income.Count ? plan.Income[index] : null, Left, height);
            MoneyCells(index < plan.Deductions.Count ? plan.Deductions[index] : null, Left + Width / 2, height);
            y += height;
        }
        MoneyCells(null, Left, 28);
        MoneyCells(new("Deduction", Amount(TotalsFor(row).Deductions, decimals)), Left + Width / 2, 28); y += 28;
        Box(Left, Width - 140, 30, "In Hand Salary", centered: true, fill: SimpleBand);
        Box(Left + Width - 140, 140, 30, "");
        Money(gfx, row.NetPay, bold, Left + Width - 133, y + 5, 126, 20, decimals); y += 30;
        var footerY = y;
        Box(Left, EmployerWidth, plan.FooterHeight, "");
        Box(Left + EmployerWidth, Width - EmployerWidth, plan.FooterHeight, "");
        Box(Left, EmployerWidth - 89, 26, "Employer Contributions", fill: Pink);
        Box(Left + EmployerWidth - 89, 89, 26, "Amount", centered: true, fill: Pink); y += 26;
        for (var index = 0; index < plan.Employer.Count; index++)
        {
            EmployerCells(plan.Employer[index], plan.EmployerHeights[index]);
            y += plan.EmployerHeights[index];
        }
        EmployerCells(new("Total Employer Contributions", Amount(plan.EmployerTotal, decimals)), plan.EmployerTotalHeight, Pink);
        if (seal is not null) Image(gfx, seal, Left + EmployerWidth + 12, footerY + 8, Width - EmployerWidth - 24, plan.FooterHeight - 16);
        y = footerY + plan.FooterHeight;
        Box(Left, Width, plan.FooterNoteHeight, "", fill: Pale);
        Text(gfx, EmployerNote, Font(8), Left + 7, y + 6, Width - 14, plan.FooterNoteHeight - 12);
        y += plan.FooterNoteHeight;
        if (seal is null) gfx.DrawString("This is a system-generated payslip and does not require a signature.", Font(8), XBrushes.Gray,
            new XRect(Left, y + 8, Width, 18), XStringFormats.TopCenter);

        void EmployerCells(SimpleField field, double height, XBrush? fill = null)
        {
            Box(Left, EmployerWidth - 89, height, field.Label, fill: fill);
            Box(Left + EmployerWidth - 89, 89, height, "", fill: fill);
            if (decimal.TryParse(field.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                Money(gfx, value, bold, Left + EmployerWidth - 82, y + 5, 75, height - 10, decimals);
        }

        void Box(double x, double width, double height, string text, bool centered = false, XBrush? fill = null)
        {
            if (fill is not null) gfx.DrawRectangle(Border, fill, x, y, width, height);
            else gfx.DrawRectangle(Border, x, y, width, height);
            if (!string.IsNullOrEmpty(text)) Text(gfx, text, bold, x + 7, y + 5, width - 14, height - 10, centered);
        }
        void MoneyCells(SimpleField? field, double x, double height)
        {
            Box(x, Width / 2 - 89, height, field?.Label ?? "");
            var numeric = decimal.TryParse(field?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value);
            Box(x + Width / 2 - 89, 89, height, numeric ? "" : field?.Value ?? "");
            if (numeric) Money(gfx, value, bold, x + Width / 2 - 82, y + 5, 75, height - 10, decimals);
        }
    }
}
