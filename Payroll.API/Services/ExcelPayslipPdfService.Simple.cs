using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed partial class ExcelPayslipPdfService
{
    private static readonly XBrush SimpleBand = new XSolidBrush(XColor.FromArgb(211, 145, 145));
    internal sealed record SimpleField(string Label, string Value);
    private sealed record SimplePlan(double Size, IReadOnlyList<SimpleField> Information, double[] InformationHeights,
        IReadOnlyList<SimpleField> Income, IReadOnlyList<SimpleField> Deductions, double[] MoneyHeights,
        IReadOnlyList<SimpleField> Employer, double FooterHeight);
    private static string FieldKey(string label) => Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]", "");

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

    private static SimplePlan MakeSimplePlan(XGraphics gfx, ExcelPayslipBatch batch, ExcelPayslipRow row, int decimals = 2)
    {
        var information = SimpleInformation(batch, row);
        var income = row.Information.Where(item => FieldKey(item.Label) is "rate" or "ratemonthly" or "monthlyrate" or "rateday" or "rateperday" or "dailyrate")
            .Select(item => new SimpleField(FieldKey(item.Label) switch { "ratemonthly" or "monthlyrate" => "Rate (Monthly)", "rateday" or "rateperday" or "dailyrate" => "Rate/Day", _ => "Rate" },
                decimal.TryParse(item.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) ? Amount(rate, decimals) : item.Value)).ToList();
        income.AddRange(row.Earnings.Where(item => item.Amount != 0).Select(item => new SimpleField(FieldKey(item.Label) is "wages" or "earnedwages" ? "Earn Salary" : item.Label, Amount(item.Amount, decimals))));
        var deductions = row.Deductions.Where(item => item.Amount != 0).Select(item => new SimpleField(item.Label, Amount(item.Amount, decimals))).ToList();
        var employer = row.EmployerContributions.Where(item => item.Amount != 0 &&
            (FieldKey(item.Label).StartsWith("pf", StringComparison.Ordinal) || FieldKey(item.Label).StartsWith("epf", StringComparison.Ordinal) ||
             FieldKey(item.Label).StartsWith("employerpf", StringComparison.Ordinal) || FieldKey(item.Label).Contains("providentfund", StringComparison.Ordinal)))
            .Select(item => new SimpleField(item.Label, Amount(item.Amount, decimals))).ToList();
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
            var footerHeight = Math.Max(126, employer.Sum(field => Height(field.Label + ": " + field.Value, Width / 2 - 14, true)));
            if (Top + 25 + infoHeights.Sum() + 14 + 52 + moneyHeights.Sum() + 28 + 30 + footerHeight <= Bottom)
                return new(size, information, infoHeights, income, deductions, moneyHeights, employer, footerHeight);
        }
        throw new InvalidOperationException($"Excel row {row.SourceRow} has too much text to fit legibly on one simple salary slip. Shorten visible labels or reduce mapped components.");
    }

    private static void DrawSimplePage(XGraphics gfx, ExcelPayslipBatch batch, ExcelPayslipRow row, XImage? seal, int decimals)
    {
        var plan = MakeSimplePlan(gfx, batch, row, decimals);
        var bold = Font(plan.Size, true);
        var y = Top;
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
            Box(x + Width / 2 - 89, 89, 26, "Amount", fill: Pink);
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
        Box(Left, Width / 2, plan.FooterHeight, ""); Box(Left + Width / 2, Width / 2, plan.FooterHeight, "");
        var employerY = y;
        foreach (var field in plan.Employer)
        {
            var text = field.Label + ": " + field.Value;
            var height = Wrap(gfx, text, bold, Width / 2 - 14).Count * plan.Size * 1.36 + 12;
            Text(gfx, text, bold, Left + 7, employerY + 6, Width / 2 - 14, height - 12); employerY += height;
        }
        if (seal is not null) Image(gfx, seal, Left + Width / 2 + 47, y + 8, Width / 2 - 94, plan.FooterHeight - 16);
        else gfx.DrawString("This is a system-generated payslip and does not require a signature.", Font(8), XBrushes.Gray,
            new XRect(Left, y + plan.FooterHeight + 8, Width, 18), XStringFormats.TopCenter);

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
