using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class PayslipPresentationTests
{
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
