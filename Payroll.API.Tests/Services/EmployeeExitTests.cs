using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class EmployeeExitTests
{
    [Fact]
    public void SuggestedWorkWeekIsAUsableCalendarForTheExistingAttendanceEngine()
    {
        var calendar = new AttendanceWorkWeek("Sunday off", ExcelPayrollSetupRepository.SundayOffConfiguration);
        Assert.True(calendar.IsConfigured);
        for (var day = new DateTime(2026, 9, 1); day.Month == 9; day = day.AddDays(1))
            Assert.Equal(day.DayOfWeek != DayOfWeek.Sunday, calendar.IsWorking(day));
    }

    [Fact]
    public void StatusRespectsClientDatesAndKeepsLastDayDuringAFutureRehire()
    {
        Employee[] people = [new() { Id = 1, ClientId = 33, IsActive = true }, new() { Id = 2, ClientId = 33, IsActive = true }, new() { Id = 3, ClientId = 33, IsActive = true }];
        EmployeeRepository.LifecycleAction[] history = [new() { EmployeeId = 1, ClientId = 33, ActionType = "Resign", EffectiveFrom = new(2026, 10, 21) },
            new() { EmployeeId = 2, ClientId = 33, ActionType = "Resign", EffectiveFrom = new(2026, 9, 21) }, new() { EmployeeId = 2, ClientId = 33, ActionType = "Rehire", EffectiveFrom = new(2026, 11, 1) },
            new() { EmployeeId = 3, ClientId = 99, ActionType = "Resign", EffectiveFrom = new(2026, 9, 21) }];
        EmployeeRepository.ApplyEmploymentStatus(people, history, new(2026, 10, 17));
        Assert.Equal("Exit scheduled", people[0].EmploymentStatus); Assert.True(people[0].IsActive); Assert.Equal("2026-10-20", people[0].LastWorkingDate);
        Assert.Equal("Resigned", people[1].EmploymentStatus); Assert.False(people[1].IsActive); Assert.Equal("2026-09-20", people[1].LastWorkingDate);
        Assert.Equal("Active", people[2].EmploymentStatus); Assert.Null(people[2].LastWorkingDate);
    }

    [Fact]
    public void EmploymentAnnotationsDoNotInvalidateAlreadyQueuedMailHashes()
    {
        var batch = new ExcelPayslipBatch { ClientId = 11, Month = "2026-09", Rows = [new() { Id = "row-1", EmployeeName = "Example", NetPay = 12345.67m }] };
        var original = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions))));
        Assert.Equal(original, NotificationRepository.ExcelPayslipBatchHash(batch));
        batch.Rows[0].Employment = new(100, "EX001", "Resigned", "2026-10-15", false, false);
        Assert.Equal(original, NotificationRepository.ExcelPayslipBatchHash(batch));
        batch.Rows[0].NetPay++;
        Assert.NotEqual(original, NotificationRepository.ExcelPayslipBatchHash(batch));
    }

    [Fact]
    public void ExitsBlockFuturePdfsAndEmailsWithoutBlockingTheirHistoricalMonth()
    {
        var row = new ExcelPayslipRow { Id = "row-1", EmployeeName = "Example", NetPay = 100, Earnings = [new() { Label = "Salary", Amount = 100 }] };
        var batch = new ExcelPayslipBatch { Month = "2026-09", Rows = [row] };
        var request = new ExcelPayslipSelection { RowIds = [row.Id], AcknowledgeWarnings = true };
        row.Employment = new(100, "EX001", "Resigned", "2026-09-30", false, false);
        Assert.Null(ExcelPayslipRepository.SelectRows(batch, request).Error);
        batch.Month = "2026-10";
        row.Employment = row.Employment with { BlockedForMonth = true };
        Assert.Contains("left before", ExcelPayslipRepository.SelectRows(batch, request).Error);
        Assert.Single(batch.Rows);
        Assert.Equal(100, batch.Rows[0].NetPay);
    }

    [Theory]
    [InlineData("2026-09-30", "2026-10-01")]
    [InlineData("2028-02-29", "2028-03-01")]
    [InlineData("2026-12-31", "2027-01-01")]
    public void LastWorkingDateRemainsInclusive(string lastDay, string inactiveFrom) => Assert.Equal(DateTime.Parse(inactiveFrom), EmployeeEmploymentRules.InactiveFrom(lastDay));
}
