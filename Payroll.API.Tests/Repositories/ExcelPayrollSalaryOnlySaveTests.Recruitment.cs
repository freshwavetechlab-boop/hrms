using System.Text.Json;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed partial class ExcelPayrollSalaryOnlySaveTests
{
    [Fact]
    public async Task RecruitmentAnnualBaseUsesTheSalaryWriterAndPreservesExistingPayAndProtectedData()
    {
        using var db = new RecordingConnection(); using var tx = db.BeginTransaction();
        var employee = Candidate(); employee.AnnualCtc = 0; employee.SalaryStructureId = "";
        var originalPay = JsonSerializer.Serialize(employee.SalaryComponents);
        Assert.True(EmployeeRepository.ApplyRecruitmentSalarySeed(employee, new() { ["BASIC"] = 40065 }, 480780, hasComponentHistory: true));
        await EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db, tx, employee);
        Assert.Equal(480780, db.AnnualCtc); Assert.Empty(db.Template);
        Assert.Equal(originalPay, JsonSerializer.Serialize(db.Salary));
        Assert.Equal("123460000001", db.BaseBankText);
        Assert.All(db.Commands.Where(c => c.IsWrite), c =>
        {
            Assert.DoesNotContain("payment", c.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("personal", c.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payrun", c.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attendance", c.Sql, StringComparison.OrdinalIgnoreCase);
        });
    }
}
