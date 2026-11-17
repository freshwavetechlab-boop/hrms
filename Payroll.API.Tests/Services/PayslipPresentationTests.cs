using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Payroll.API.Tests.Services;

public class PayslipPresentationTests
{
    [Theory]
    [InlineData(100, 15, 100, 15)] // all approved adjustments already itemized
    [InlineData(100, 15, 0, 0)] // legacy/manual aggregates only
    [InlineData(100, 15, 70, 10)] // manual remainder plus itemized period adjustments
    [InlineData(0, 0, 0, 0)]
    public void ItemizedPeriodAdjustmentsAndManualRemaindersAppearExactlyOnce(decimal periodEarnings,
        decimal periodDeductions, decimal itemizedEarnings, decimal itemizedDeductions)
    {
        var row = new EssMssRepository.EssPayslipRow
        {
            GrossPay = 1000, StatutoryDeductions = 120, OneTimeEarnings = periodEarnings,
            OneTimeDeductions = periodDeductions, NetPay = 880 + periodEarnings - periodDeductions,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new object[] {
                new { Id = "BASIC", Name = "Basic", Category = "Earning", monthlyAmount = 2000m, amount = 1000m },
                new { Id = "PF", Name = "PF", Category = "Deduction", monthlyAmount = 120m, amount = 120m },
                new { Id = "EXTRA_DUTY", Name = "Extra duty", Category = "Earning", monthlyAmount = itemizedEarnings, amount = itemizedEarnings },
                new { Id = "PENALTY", Name = "Penalty", Category = "Deduction", monthlyAmount = itemizedDeductions, amount = itemizedDeductions },
                new { Id = "EMPLOYER_PF", Name = "Employer PF", Category = "Benefit", monthlyAmount = 130m, amount = 130m }
            })
        };
        var before = System.Text.Json.JsonSerializer.Serialize(row);
        var html = EssMssRepository.BuildPayslipHtml(new Organization(), new EssMssRepository.EssPayslipTemplate(), row, new EssMssRepository.EssPayslipYtd());
        var totals = ItemizedSalaryTotals(html);
        Assert.Equal(1000 + periodEarnings, totals.Earnings);
        Assert.Equal(120 + periodDeductions, totals.Deductions);
        Assert.Equal(periodEarnings > itemizedEarnings, html.Contains("One Time Earnings"));
        Assert.Equal(periodDeductions > itemizedDeductions, html.Contains("One Time Deductions"));
        Assert.DoesNotContain("Employer PF</td>", html);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(row));
    }

    [Fact]
    public void LegacyAggregateLineIsNotRepeatedAndMissingRegularPayIsNotInvented()
    {
        var row = new EssMssRepository.EssPayslipRow { GrossPay = 1000, OneTimeEarnings = 100,
            DetailsJson = """[{"id":"BASIC","name":"Basic","category":"Earning","amount":1000,"monthlyAmount":1000},{"id":"ONE_TIME_EARNINGS","name":"Legacy period amount","category":"Earning","amount":100,"monthlyAmount":100}]""" };
        var html = EssMssRepository.BuildPayslipHtml(new Organization(), new EssMssRepository.EssPayslipTemplate(), row, new EssMssRepository.EssPayslipYtd());
        Assert.Equal(1100, ItemizedSalaryTotals(html).Earnings);
        Assert.DoesNotContain("One Time Earnings", html);
        row.DetailsJson = """[{"id":"BASIC","name":"Incomplete regular detail","category":"Earning","amount":900,"monthlyAmount":900}]""";
        html = EssMssRepository.BuildPayslipHtml(new Organization(), new EssMssRepository.EssPayslipTemplate(), row, new EssMssRepository.EssPayslipYtd());
        Assert.Equal(1000, ItemizedSalaryTotals(html).Earnings); // saved period amount100, never an invented200
        Assert.Contains(">1,100.00</td>", html); // reported saved total remains visible
    }

    private static (decimal Earnings, decimal Deductions) ItemizedSalaryTotals(string html)
    {
        var body = Regex.Match(html, "<table class=\"salary\".*?<tbody>(.*?)</tbody>", RegexOptions.Singleline).Groups[1].Value;
        decimal earnings = 0, deductions = 0;
        foreach (Match row in Regex.Matches(body, "<tr>(.*?)</tr>", RegexOptions.Singleline))
        {
            var cells = Regex.Matches(row.Groups[1].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline);
            Assert.Equal(5, cells.Count);
            if (decimal.TryParse(cells[2].Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var e)) earnings += e;
            if (decimal.TryParse(cells[4].Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) deductions += d;
        }
        return (earnings, deductions);
    }

    [Fact]
    public void WeeklyOffsHolidaysAndAbsencesDoNotCountAsLeave()
    {
        var rows = new[]
        {
            new PayRunLeaveBreakdown { Code = "WO", Days = 4 },
            new PayRunLeaveBreakdown { Code = "H", Days = 1 },
            new PayRunLeaveBreakdown { Code = "A", Days = 2 },
            new PayRunLeaveBreakdown { Code = "CL", Days = 1.5m },
            new PayRunLeaveBreakdown { Code = "LWP", Days = 1 }
        };
        Assert.Equal(2.5m, PayslipPresentation.LeaveDays(rows));
        Assert.Equal(0m, PayslipPresentation.LeaveDays(rows.Take(3)));
    }

    [Theory]
    [InlineData("9.1801E+14", "918010000000000")]
    [InlineData("9.18012345678901E+14", "918012345678901")]
    [InlineData("123456789012345678901234567890E+0", "123456789012345678901234567890")]
    [InlineData("001234567890", "001234567890")]
    [InlineData("1.25E+1", "1.25E+1")]
    [InlineData("1E+999", "1E+999")]
    [InlineData("", "")]
    public void AccountIdentifiersExpandExactlyWithoutFloatingPoint(string input, string expected) =>
        Assert.Equal(expected, PayslipPresentation.AccountNumber(input));

    [Fact]
    public void ConfiguredTemplateControlsClientBankLogoYtdThemeAndNote()
    {
        var row = new EssMssRepository.EssPayslipRow
        {
            EmployeeCode = "PLRS001", EmployeeName = "Test Employee", ClientName = "PLRS <Client>",
            PayPeriod = "2026-06", BankAccountNo = "9.1801E+14", TotalWorkingDays = 30, PayableDays = 30
        };
        var organization = new Organization { Name = "Employer", LogoDataUrl = "data:image/png;base64,TEST" };
        var template = new EssMssRepository.EssPayslipTemplate { Theme = "Modern", Note = "Configured footer <safe>", ShowClient = true, ShowBank = true, ShowYtd = true, ShowLogo = true };
        var html = EssMssRepository.BuildPayslipHtml(organization, template, row, new EssMssRepository.EssPayslipYtd(), 0);
        Assert.Contains("Client: PLRS &lt;Client&gt;", html);
        Assert.Contains("918010000000000", html);
        Assert.DoesNotContain("9.1801E+14", html);
        Assert.Contains("<span>Leaves</span><strong>0.00</strong>", html);
        Assert.Contains("<span>Date of Joining</span><strong>-</strong>", html);
        Assert.Contains("slip modern", html);
        Assert.Contains("YTD Gross", html);
        Assert.Contains("data:image/png;base64,TEST", html);
        Assert.Contains("Configured footer &lt;safe&gt;", html);
        template.ShowClient = template.ShowBank = template.ShowYtd = template.ShowLogo = false;
        var hidden = EssMssRepository.BuildPayslipHtml(organization, template, row, new EssMssRepository.EssPayslipYtd());
        Assert.DoesNotContain("Client: ", hidden);
        Assert.DoesNotContain("Account #", hidden);
        Assert.DoesNotContain("YTD Gross", hidden);
        Assert.DoesNotContain("data:image/png;base64,TEST", hidden);
    }

    [Fact]
    public void NotificationTemplateIncludesTrustedPayslipAndEscapesOtherValues()
    {
        var html = NotificationRepository.RenderPayslipBody(
            "<p>Hello {{employeeName}}, your payslip:</p>{{payslipHtml}}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["employeeName"] = "<script>bad</script>" },
            "<!doctype html><html><head><style>.slip{color:black}</style></head><body><main class=\"slip\">Configured payslip</main></body></html>");
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
        Assert.Contains("<main class=\"slip\">Configured payslip</main>", html);
        Assert.Contains("<style>.slip{color:black}</style>", html);
        Assert.DoesNotContain("<body>", html);
        Assert.DoesNotContain("{{payslipHtml}}", html);
    }
}
