using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipDashboardTests
{
    private static ExcelPayslipBatchSummary Batch(int number, string month, int saveDay, int clientId = 11) => new()
    {
        Id = number.ToString("x32"), ClientId = clientId, Month = month, RowCount = number,
        SourceFileName = $"Salary-{number}.xlsx", CreatedAtUtc = new DateTime(2026, 10, saveDay, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void RepeatUploadsRemainInHistoryButOnlyLatestSavedBatchCountsInMonthlyTrend()
    {
        var oldSeptember = Batch(1, "2026-09", 1);
        var october = Batch(2, "2026-10", 2);
        var newSeptember = Batch(3, "2026-09", 3);
        var result = ExcelPayslipRepository.SelectDashboardBatches(11, [oldSeptember, october, newSeptember], null)!;
        Assert.Equal(new[] { newSeptember.Id, october.Id, oldSeptember.Id }, result.History.Select(batch => batch.Id));
        Assert.Same(newSeptember, result.SelectedBatch); // Latest save, even when its salary month is earlier.
        Assert.Equal(new[] { newSeptember.Id, october.Id }, result.TrendBatches.Select(batch => batch.Id));
        Assert.Equal(5, result.TrendBatches.Sum(batch => batch.RowCount));
    }

    [Fact]
    public void SelectingAnOlderVersionDoesNotReplaceTheLatestVersionUsedByTrend()
    {
        var oldSeptember = Batch(1, "2026-09", 1); var latestSeptember = Batch(2, "2026-09", 2);
        var result = ExcelPayslipRepository.SelectDashboardBatches(11, [latestSeptember, oldSeptember], oldSeptember.Id)!;
        Assert.Same(oldSeptember, result.SelectedBatch);
        Assert.Same(latestSeptember, Assert.Single(result.TrendBatches));
        Assert.Equal(2, result.History.Count);
    }

    [Fact]
    public void TrendUsesLastTwelveSalaryMonthsRegardlessOfUploadOrderAndKeepsFullHistory()
    {
        var rows = Enumerable.Range(0, 15).Select(index => Batch(index + 1, new DateTime(2025, 1, 1).AddMonths(index).ToString("yyyy-MM"), 20 - index)).ToArray();
        var result = ExcelPayslipRepository.SelectDashboardBatches(11, rows, null)!;
        Assert.Equal(15, result.History.Count);
        Assert.Equal(12, result.TrendBatches.Count);
        Assert.Equal("2025-04", result.TrendBatches[0].Month);
        Assert.Equal("2026-03", result.TrendBatches[^1].Month);
        Assert.Equal("2025-01", result.SelectedBatch!.Month);
    }

    [Fact]
    public void ClientScopeFiltersBothHistoryAndSelectionWithoutFallingBackForUnknownBatch()
    {
        var own = Batch(1, "2026-09", 1); var other = Batch(2, "2026-10", 2, clientId: 12);
        var result = ExcelPayslipRepository.SelectDashboardBatches(11, [other, own], null)!;
        Assert.Same(own, result.SelectedBatch);
        Assert.Same(own, Assert.Single(result.History));
        Assert.Same(own, Assert.Single(result.TrendBatches));
        Assert.Null(ExcelPayslipRepository.SelectDashboardBatches(11, [other, own], other.Id));
        Assert.Null(ExcelPayslipRepository.SelectDashboardBatches(11, [own], new string('f', 32)));
    }

    [Fact]
    public void SameTimestampUsesTheSameIdTieBreakAsBatchHistory()
    {
        var first = Batch(1, "2026-09", 1); var second = Batch(2, "2026-09", 1);
        var result = ExcelPayslipRepository.SelectDashboardBatches(11, [first, second], null)!;
        Assert.Same(second, result.SelectedBatch);
        Assert.Same(second, Assert.Single(result.TrendBatches));
    }

    [Fact]
    public void ClientWithoutUploadsHasAnEmptyDashboard()
    {
        var result = ExcelPayslipRepository.SelectDashboardBatches(11, [Batch(1, "2026-09", 1, clientId: 12)], null)!;
        Assert.Null(result.SelectedBatch); Assert.Empty(result.History); Assert.Empty(result.TrendBatches);
    }

    [Fact]
    public async Task MalformedSelectedBatchIsRejectedBeforeAnyDatabaseAccess()
    {
        var repository = new ExcelPayslipRepository(new ConfigurationBuilder().Build(), null!, null!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetDashboardAsync(11, "not-a-batch-id"));
        Assert.Contains("valid saved batch", error.Message);
    }
}
