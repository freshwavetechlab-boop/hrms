using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class AttendanceShiftTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualHalfDayUsesClientOptInAndDoesNotInventPunches(bool configuredShift)
    {
        var settings = new AttendanceSettings { ClientId = 20, Shift = configuredShift ? Shift() : null };
        Assert.Null(AttendancePunchCalculator.CalculateManualHalfDay("P.5", settings));
        settings.Rules.AllowManualHalfDay = true;
        var result = AttendancePunchCalculator.CalculateManualHalfDay("P.5", settings)!;
        Assert.Equal(.5m, result.Payable);
        Assert.Equal(0, result.Hours);
        Assert.Null(result.CheckIn); Assert.Null(result.CheckOut);
        Assert.Equal("Manual half day", result.Remarks);
        Assert.Null(AttendancePunchCalculator.CalculateManualHalfDay("Present", settings));
        Assert.Null(AttendancePunchCalculator.CalculateManualHalfDay("CL", settings));
        Assert.Null(AttendancePunchCalculator.CalculateManualHalfDay("P.5", new() { ClientId = 30 }));
    }

    private static readonly DateTime Day = new(2026, 9, 1);
    private static AttendanceShift Shift(int id = 1) => new()
    {
        Id = id, ClientId = 20, ShiftCode = "DAY", ShiftName = "Day shift", StartTime = TimeSpan.FromHours(9), EndTime = TimeSpan.FromHours(18),
        BreakMinutes = 60, GraceMinutes = 10, MinimumFullDayHours = 8, MinimumHalfDayHours = 4, EffectiveFrom = Day
    };
    private static AttendanceShiftResolver Resolver(AttendanceShift shift) => new(new() { ClientId = 20, ShiftId = shift.Id }, [shift], new Dictionary<int, int>());

    [Theory]
    [InlineData(18, 8, 1)]
    [InlineData(14, 4, .5)]
    [InlineData(12, 2, 0)]
    public void FixedWindowAndBreakDeterminePayableDuration(int outHour, decimal hours, decimal payable)
    {
        var settings = Resolver(Shift()).Resolve(1, Day);
        var result = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(8), TimeSpan.FromHours(outHour), Day, settings)!;
        Assert.Equal(hours, result.Hours); Assert.Equal(payable, result.Payable);
        Assert.Equal(8, AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(8), TimeSpan.FromHours(20), Day, settings)!.Hours);
    }

    [Fact]
    public void ShiftGraceOverridesClientGraceAndOnlyFlagsOutsideBoundary()
    {
        var settings = Resolver(Shift()).Resolve(1, Day);
        var rules = new AttendanceRules { LateGraceMinutes = 0, EarlyGraceMinutes = 0 };
        AttendancePunchCalculator.Result Calculate(int minute) => AttendancePunchCalculator.Calculate([
            new("CheckIn", Day.AddHours(9).AddMinutes(minute)), new("CheckOut", Day.AddHours(18).AddMinutes(-minute))], Day, settings, rules);
        Assert.DoesNotContain("Late", Calculate(10).Remarks); Assert.DoesNotContain("Early", Calculate(10).Remarks);
        Assert.Contains("Late coming", Calculate(11).Remarks); Assert.Contains("Early going", Calculate(11).Remarks);
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(4, .5)]
    [InlineData(3, 0)]
    public void FlexibleUsesDurationWithoutClockPenalties(decimal hours, decimal payable)
    {
        var shift = Shift(); shift.ShiftType = "Flexible"; shift.StartTime = shift.EndTime = null;
        var settings = Resolver(shift).Resolve(1, Day);
        var result = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(13), TimeSpan.FromHours(14 + (double)hours), Day, settings)!;
        Assert.Equal(hours, result.Hours); Assert.Equal(payable, result.Payable);
        Assert.DoesNotContain("Late", result.Remarks); Assert.DoesNotContain("Early", result.Remarks);
    }

    [Fact]
    public void OvernightUsesStartDateEvenOnLastEffectiveNightAndHandlesLateInAfterMidnight()
    {
        var shift = Shift(); shift.StartTime = TimeSpan.FromHours(21); shift.EndTime = TimeSpan.FromHours(6); shift.IsOvernight = true; shift.EffectiveTo = Day;
        var resolver = Resolver(shift);
        var resolved = resolver.ResolvePunch(1, Day.AddDays(1).AddHours(6));
        Assert.Equal(Day, resolved.Date); Assert.Equal(shift.Id, resolved.Settings.ShiftId);
        var full = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(21), TimeSpan.FromHours(6), Day, resolved.Settings)!;
        Assert.Equal(8, full.Hours); Assert.Equal(1, full.Payable);
        var late = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(2), TimeSpan.FromHours(6), Day, resolved.Settings)!;
        Assert.Equal(3, late.Hours); Assert.Equal(0, late.Payable);
        var beforeMidnight = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(21), TimeSpan.FromHours(23), Day, resolved.Settings)!;
        Assert.Equal(1, beforeMidnight.Hours);
        Assert.Null(resolver.Resolve(1, Day.AddDays(1)).Shift);
    }

    [Fact]
    public void AssignmentPrecedesDefaultAndInactiveExpiredOrForeignShiftsFallBack()
    {
        var assigned = Shift(1); var fallback = Shift(2);
        var settings = new AttendanceSettings { ClientId = 20, ShiftId = 2 };
        var resolver = new AttendanceShiftResolver(settings, [assigned, fallback], new Dictionary<int, int> { [117] = 1 });
        Assert.Equal(1, resolver.Resolve(117, Day).ShiftId); Assert.Equal(2, resolver.Resolve(118, Day).ShiftId);
        assigned.IsActive = false; Assert.Equal(2, resolver.Resolve(117, Day).ShiftId);
        assigned.IsActive = true; assigned.EffectiveTo = Day.AddDays(-1); Assert.Equal(2, resolver.Resolve(117, Day).ShiftId);
        assigned.EffectiveTo = null; assigned.EffectiveFrom = Day.AddDays(1); Assert.Equal(2, resolver.Resolve(117, Day).ShiftId);
        assigned.EffectiveFrom = Day; assigned.ClientId = 30;
        resolver = new(settings, [assigned, fallback], new Dictionary<int, int> { [117] = 1 });
        Assert.Equal(2, resolver.Resolve(117, Day).ShiftId);
        fallback.IsActive = false; Assert.Same(settings, resolver.Resolve(117, Day));
    }

    [Fact]
    public void NightExpirySeparatesNextMorningOutFromDefaultDayShiftIn()
    {
        var night = Shift(); night.StartTime = TimeSpan.FromHours(21); night.EndTime = TimeSpan.FromHours(6); night.IsOvernight = true; night.EffectiveTo = Day;
        var day = Shift(2);
        var resolver = new AttendanceShiftResolver(new() { ClientId = 20, ShiftId = day.Id }, [night, day], new Dictionary<int, int> { [117] = night.Id });
        var outTime = Day.AddDays(1).AddHours(6); var inTime = Day.AddDays(1).AddHours(9);
        Assert.Equal(Day, resolver.ResolvePunch(117, outTime).Date);
        Assert.Equal(Day.AddDays(1), resolver.ResolvePunch(117, inTime).Date);
        Assert.Equal(day.Id, resolver.ResolvePunch(117, inTime).Settings.ShiftId);
        var nightBounds = resolver.Bounds(117, Day); var dayBounds = resolver.Bounds(117, Day.AddDays(1));
        Assert.Equal(nightBounds.End, dayBounds.Start);
        Assert.InRange(outTime, nightBounds.Start, nightBounds.End); Assert.True(inTime >= nightBounds.End);
    }

    [Fact]
    public void NoShiftRetainsLegacyHoursAndFlagsExactly()
    {
        var settings = new AttendanceSettings { ClientId = 20, CheckInTime = TimeSpan.FromHours(21), CheckOutTime = TimeSpan.FromHours(6) };
        var resolver = new AttendanceShiftResolver(settings, [], new Dictionary<int, int>());
        var resolved = resolver.ResolvePunch(1, Day.AddDays(1).AddHours(6));
        Assert.Same(settings, resolved.Settings); Assert.Equal(Day, resolved.Date);
        var result = AttendancePunchCalculator.Calculate([new("CheckIn", Day.AddHours(21)), new("CheckOut", Day.AddDays(1).AddHours(6))], Day, settings, new());
        Assert.Equal(9, result.Hours); Assert.Equal(1, result.Payable); Assert.Equal("", result.Remarks);
        Assert.Null(AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(21), TimeSpan.FromHours(6), Day, settings));
    }

    [Fact]
    public void ValidationRejectsMissingIdentityInvalidTimesDatesAndThresholds()
    {
        var shift = Shift(); Assert.Null(LeaveAttendanceRepository.ValidateShift(shift));
        shift.ClientId = 0; Assert.Contains("Client", LeaveAttendanceRepository.ValidateShift(shift)); shift.ClientId = 20;
        shift.ShiftCode = " "; Assert.Contains("code", LeaveAttendanceRepository.ValidateShift(shift)); shift.ShiftCode = "DAY";
        shift.ShiftName = " "; Assert.Contains("name", LeaveAttendanceRepository.ValidateShift(shift)); shift.ShiftName = "Day shift";
        shift.StartTime = null; Assert.Contains("times", LeaveAttendanceRepository.ValidateShift(shift)); shift.StartTime = TimeSpan.FromHours(9);
        shift.EndTime = TimeSpan.FromHours(6); Assert.Contains("End time", LeaveAttendanceRepository.ValidateShift(shift)); shift.EndTime = TimeSpan.FromHours(18);
        shift.MinimumFullDayHours = 4; Assert.Contains("full-day", LeaveAttendanceRepository.ValidateShift(shift)); shift.MinimumFullDayHours = 8;
        shift.EffectiveTo = Day.AddDays(-1); Assert.Contains("effective", LeaveAttendanceRepository.ValidateShift(shift)); shift.EffectiveTo = null;
        shift.GraceMinutes = -1; Assert.Contains("Grace", LeaveAttendanceRepository.ValidateShift(shift));
    }

    [Fact]
    public void FlexibleOneOptionalTimeDoesNotBecomeOvernightAndFixedInvalidOutIsUnpaid()
    {
        var shift = Shift(); shift.ShiftType = "Flexible"; shift.StartTime = TimeSpan.FromHours(21); shift.EndTime = null;
        var settings = Resolver(shift).Resolve(1, Day);
        Assert.Equal(Day, AttendancePunchCalculator.AttendanceDate(Day.AddHours(6), settings));
        Assert.Equal((Day, Day.AddDays(1)), AttendancePunchCalculator.Bounds(Day, settings));
        shift.ShiftType = "Fixed"; shift.StartTime = TimeSpan.FromHours(9); shift.EndTime = TimeSpan.FromHours(18);
        Assert.Equal(0, AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(10), TimeSpan.FromHours(9), Day, Resolver(shift).Resolve(1, Day))!.Payable);
    }
}
