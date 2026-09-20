using System.Globalization;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentBudgetValidationTests
{
    [Theory]
    [InlineData(true, "1000000")]
    [InlineData(true, "1234567.89")]
    [InlineData(true, "0.01")]
    [InlineData(true, "9999999999999999.99")]
    [InlineData(false, "0")]
    [InlineData(false, "135790.20")]
    public void Arbitrary_numeric_budgets_do_not_need_a_master(bool approvalRequired, string amount)
    {
        var request = new SaveRecruitmentRequisition { BudgetAvailable = approvalRequired, BudgetAmount = decimal.Parse(amount, CultureInfo.InvariantCulture) };
        Assert.Empty(RecruitmentRepository.ValidateBudgetAmount(request));
    }

    [Theory]
    [InlineData(true, "0", "greater than zero")]
    [InlineData(true, "-1", "negative")]
    [InlineData(false, "-1", "negative")]
    [InlineData(true, "10000000000000000", "numeric limit")]
    [InlineData(false, "10000000000000000", "numeric limit")]
    [InlineData(true, "123.456", "two decimal places")]
    [InlineData(false, "123.456", "two decimal places")]
    public async Task Invalid_budget_returns_validation_before_database_access_for_creates_and_edits(bool required, string amount, string expected)
    {
        var repository = new RecruitmentRepository(new ConfigurationBuilder().Build());
        foreach (var id in new[] { 0L, 123L })
        {
            var request = new SaveRecruitmentRequisition { Id = id, BudgetAvailable = required, BudgetAmount = decimal.Parse(amount, CultureInfo.InvariantCulture) };
            var (row, error) = await repository.SaveDraftAsync(request, new AuthUser());
            Assert.Null(row);
            Assert.Contains(expected, error);
        }
    }
}
