using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class AttendancePunchCalculatorTests
{
    private static readonly DateTime Date = new(2026, 9, 1);
    private static AttendancePunchCalculator.Punch In(int hour, int minute = 0) => new("CheckIn", Date.AddHours(hour).AddMinutes(minute));
    private static AttendancePunchCalculator.Punch Out(int hour, int minute = 0) => new("CheckOut", Date.AddHours(hour).AddMinutes(minute));

    [Theory]
    [InlineData(18, 9, 1)]
    [InlineData(17, 8, 1)]
    [InlineData(13, 4, .5)]
    [InlineData(12, 3, 0)]
    [InlineData(23, 12, 1)]
    public void ShiftThresholdsAndMaximumAreApplied(int outHour, decimal hours, decimal payable)
    {
        var result = AttendancePunchCalculator.Calculate([Out(outHour), In(9)], Date, new(), new());
        Assert.Equal(hours, result.Hours); Assert.Equal(payable, result.Payable);
    }

    [Fact]
    public void MissingPunchesAreVisibleAndUnpaid()
    {
        var incoming = AttendancePunchCalculator.Calculate([In(9)], Date, new(), new());
        var outgoing = AttendancePunchCalculator.Calculate([Out(18)], Date, new(), new());
        Assert.Equal(0, incoming.Payable); Assert.Contains("Missing punch out", incoming.Remarks);
        Assert.Equal(0, outgoing.Payable); Assert.Contains("Missing punch in", outgoing.Remarks); Assert.Equal(TimeSpan.FromHours(18), outgoing.CheckOut);
    }

    [Fact]
    public void PairCalculationExcludesBreaksAndFirstLastIncludesThem()
    {
        AttendancePunchCalculator.Punch[] punches = [Out(18), In(14), Out(12), In(9)];
        var settings = new AttendanceSettings();
        Assert.Equal(9, AttendancePunchCalculator.Calculate(punches, Date, settings, new()).Hours);
        settings.WorkingHoursCalculation = "Every valid check-in and check-out";
        var result = AttendancePunchCalculator.Calculate(punches, Date, settings, new());
        Assert.Equal(7, result.Hours); Assert.Equal(.5m, result.Payable);
        var incomplete = AttendancePunchCalculator.Calculate([.. punches, In(19)], Date, settings, new());
        Assert.Equal(0, incomplete.Payable);
    }

    [Fact]
    public void ConfiguredGraceUsesStrictBoundaryAndUnsetGraceAddsNoFlags()
    {
        AttendancePunchCalculator.Punch[] punches = [In(9, 10), Out(17, 50)];
        var rules = new AttendanceRules { LateGraceMinutes = 10, EarlyGraceMinutes = 10 };
        Assert.DoesNotContain("Late", AttendancePunchCalculator.Calculate(punches, Date, new(), rules).Remarks);
        Assert.DoesNotContain("Early", AttendancePunchCalculator.Calculate(punches, Date, new(), rules).Remarks);
        rules.LateGraceMinutes = rules.EarlyGraceMinutes = 9;
        var result = AttendancePunchCalculator.Calculate(punches, Date, new(), rules);
        Assert.Contains("Late coming", result.Remarks); Assert.Contains("Early going", result.Remarks);
        Assert.Equal("", AttendancePunchCalculator.Calculate(punches, Date, new(), new()).Remarks);
    }

    [Fact]
    public void OvernightOutBelongsToShiftStartDate()
    {
        var settings = new AttendanceSettings { CheckInTime = TimeSpan.FromHours(22), CheckOutTime = TimeSpan.FromHours(6) };
        var nextMorning = Date.AddDays(1).AddHours(6);
        Assert.Equal(Date, AttendancePunchCalculator.AttendanceDate(nextMorning, settings));
        var bounds = AttendancePunchCalculator.Bounds(Date, settings);
        Assert.InRange(nextMorning, bounds.Start, bounds.End);
        Assert.Equal(Date.AddDays(1), AttendancePunchCalculator.AttendanceDate(Date.AddDays(1).AddHours(22), settings));
        var result = AttendancePunchCalculator.Calculate([In(22), new("CheckOut", nextMorning)], Date, settings, new());
        Assert.Equal(8, result.Hours); Assert.Equal(1, result.Payable);
    }
}
