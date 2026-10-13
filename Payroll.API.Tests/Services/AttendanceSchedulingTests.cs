using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class AttendanceSchedulingTests
{
    private static readonly DateTime Day = new(2026, 10, 12);
    private static AttendanceShift Shift(int id = 1) => new()
    {
        Id = id, ClientId = 20, ShiftCode = "FLEX", ShiftName = "Flexible", ShiftType = "Flexible",
        MinimumFullDayHours = 8, MinimumHalfDayHours = 4, EffectiveFrom = Day,
        Rules = new() { HoursMethod = "Pairs", BreakMode = "None", MaximumHours = 24 }
    };
    private static EmployeeShiftPolicy Policy(int? shiftId = 1) => new() { ClientId = 20, EmployeeId = 10, AttendanceMode = "Punch", ShiftId = shiftId, AllowedShiftIds = [1, 2] };
    private static AttendanceShiftAssignment Assignment(int shiftId = 2) => new() { ClientId = 20, EmployeeId = 10, ShiftId = shiftId, EffectiveFrom = Day, EffectiveTo = Day.AddDays(6), IsActive = true };
    private static AttendanceShiftResolver Resolver(AttendanceShift shift, EmployeeShiftPolicy? policy = null, params AttendanceShiftAssignment[] roster) =>
        new(new() { ClientId = 20, ShiftId = 1 }, [shift, Shift(2)], new Dictionary<int, int>(), [policy ?? Policy()], roster);

    [Theory]
    [InlineData(6, 14, 8, 1)]
    [InlineData(13, 21, 8, 1)]
    [InlineData(14, 18, 4, .5)]
    [InlineData(14, 17, 3, 0)]
    public void FlexibleCountsHoursNotArrivalTime(int start, int end, decimal hours, decimal payable)
    {
        var result = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(start), TimeSpan.FromHours(end), Day, Resolver(Shift()).Resolve(10, Day))!;
        Assert.Equal(hours, result.Hours); Assert.Equal(payable, result.Payable); Assert.DoesNotContain("Late", result.Remarks); Assert.DoesNotContain("Early", result.Remarks);
    }

    [Theory]
    [InlineData("Pairs", "None", 8)]
    [InlineData("Span", "None", 9)]
    [InlineData("Span", "Fixed", 8)]
    public void PairsAndSpanHaveExplicitBreakTreatment(string method, string deduction, decimal hours)
    {
        var shift = Shift(); shift.Rules.HoursMethod = method; shift.Rules.BreakMode = deduction; shift.BreakMinutes = 60;
        var settings = Resolver(shift).Resolve(10, Day);
        var result = AttendancePunchCalculator.Calculate([new("CheckIn", Day.AddHours(9)), new("CheckOut", Day.AddHours(13)), new("CheckIn", Day.AddHours(14)), new("CheckOut", Day.AddHours(18))], Day, settings, new());
        Assert.Equal(hours, result.Hours); Assert.Equal(1, result.Payable);
    }

    [Fact]
    public void SplitShiftExcludesGapsAndDoesNotDoubleCountDay()
    {
        var shift = Shift(); shift.ShiftType = "Fixed"; shift.StartTime = TimeSpan.FromHours(9); shift.EndTime = TimeSpan.FromHours(20);
        shift.Rules.Segments = [new(540, 780), new(960, 1200)];
        Assert.Null(LeaveAttendanceRepository.ValidateShift(shift));
        var settings = Resolver(shift).Resolve(10, Day);
        var result = AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(8), TimeSpan.FromHours(21), Day, settings)!;
        Assert.Equal(8, result.Hours); Assert.Equal(1, result.Payable);
        shift.Rules.Segments.Add(new(700, 900));
        Assert.Contains("non-overlapping", LeaveAttendanceRepository.ValidateShift(shift));
    }

    [Fact]
    public void OvernightSplitIntervalsRemainOnStartDay()
    {
        var shift = Shift(); shift.ShiftType = "Fixed"; shift.StartTime = TimeSpan.FromHours(21); shift.EndTime = TimeSpan.FromHours(6); shift.IsOvernight = true;
        shift.Rules.Segments = [new(1260, 1440), new(1500, 1800)];
        Assert.Null(LeaveAttendanceRepository.ValidateShift(shift));
        var resolver = Resolver(shift);
        var resolved = resolver.ResolvePunch(10, Day.AddDays(1).AddHours(6));
        Assert.Equal(Day, resolved.Date);
        Assert.Equal(8, AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(21), TimeSpan.FromHours(6), Day, resolved.Settings)!.Hours);
    }

    [Fact]
    public void FlexibleBoundaryKeepsNightHoursTogether()
    {
        var shift = Shift(); shift.Rules.DayBoundary = TimeSpan.FromHours(4);
        var resolver = Resolver(shift);
        var resolved = resolver.ResolvePunch(10, Day.AddDays(1).AddHours(3));
        Assert.Equal(Day, resolved.Date);
        Assert.Equal(8, AttendancePunchCalculator.CalculateReview(TimeSpan.FromHours(19), TimeSpan.FromHours(3), Day, resolved.Settings)!.Hours);
        Assert.Equal(Day.AddHours(4), resolver.Bounds(10, Day).Start);
        Assert.Equal(Day.AddDays(1), resolver.ResolvePunch(10, Day.AddDays(1).AddHours(4)).Date);
    }

    [Fact]
    public void RosterOverridesPolicyDefaultOnlyOnAssignedDays()
    {
        var roster = Assignment(); roster.WeekDays = "1";
        var resolver = Resolver(Shift(), Policy(), roster);
        Assert.Equal(2, resolver.Resolve(10, Day).ShiftId);
        Assert.Equal(1, resolver.Resolve(10, Day.AddDays(1)).ShiftId);
        Assert.Equal(1, resolver.Resolve(10, Day.AddDays(7)).ShiftId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("foreign")]
    [InlineData("inactive")]
    [InlineData("disallowed")]
    [InlineData("overlap")]
    [InlineData("multiplePolicies")]
    public void StrictPoliciesNeverSilentlyFallback(string failure)
    {
        var shift = Shift(); var policy = Policy(); var assignments = new List<AttendanceShiftAssignment>();
        if (failure == "missing") policy.ShiftId = null;
        if (failure == "expired") shift.EffectiveTo = Day.AddDays(-1);
        if (failure == "foreign") shift.ClientId = 30;
        if (failure == "inactive") shift.IsActive = false;
        if (failure == "disallowed") policy.AllowedShiftIds = [2];
        if (failure == "overlap") assignments = [Assignment(), Assignment()];
        var resolver = failure == "multiplePolicies" ? new AttendanceShiftResolver(new() { ClientId = 20 }, [shift], new Dictionary<int, int>(), [policy, Policy()]) : Resolver(shift, policy, assignments.ToArray());
        var settings = resolver.Resolve(10, Day);
        Assert.NotNull(settings.ResolutionError);
        Assert.Throws<InvalidOperationException>(() => AttendancePunchCalculator.Calculate([], Day, settings, new()));
    }

    [Fact]
    public void MonthlyAndMissingPunchesAreExplicitErrors()
    {
        var monthly = Policy(); monthly.AttendanceMode = "Monthly";
        Assert.Contains("monthly attendance", Resolver(Shift(), monthly).Resolve(10, Day).ResolutionError, StringComparison.OrdinalIgnoreCase);
        var strict = Resolver(Shift()).Resolve(10, Day);
        Assert.NotNull(AttendancePunchCalculator.ReviewError(null, TimeSpan.FromHours(18), strict));
        var partial = AttendancePunchCalculator.Calculate([new("CheckIn", Day.AddHours(9))], Day, strict, new());
        Assert.Equal(0, partial.Payable); Assert.Contains("Missing punch out", partial.Remarks);
    }

    [Fact]
    public void PolicyValidatesModesAllowedShiftsAndDefault()
    {
        var policy = Policy(); Assert.Null(LeaveAttendanceRepository.ValidatePolicyShifts(policy, 1));
        Assert.NotNull(LeaveAttendanceRepository.ValidatePolicyShifts(policy, 3));
        policy.AllowedShiftIds = []; Assert.NotNull(LeaveAttendanceRepository.ValidatePolicyShifts(policy, null));
        policy.AttendanceMode = "Monthly"; Assert.Null(LeaveAttendanceRepository.ValidatePolicyShifts(policy, null));
        Assert.NotNull(LeaveAttendanceRepository.ValidatePolicyShifts(policy, 1));
    }

    [Fact]
    public void OverlapChecksWeekdaysNotJustDateRanges()
    {
        var a = Assignment(); a.WeekDays = "1,3";
        var b = Assignment(); b.WeekDays = "2,4";
        Assert.False(LeaveAttendanceRepository.AssignmentsOverlap(a, b));
        b.WeekDays = "3"; Assert.True(LeaveAttendanceRepository.AssignmentsOverlap(a, b));
        b.EffectiveFrom = Day.AddDays(7); b.EffectiveTo = Day.AddDays(13); Assert.False(LeaveAttendanceRepository.AssignmentsOverlap(a, b));
    }

    [Fact]
    public void AssignmentRequiresEmployeesActualWeekdaysAndReason()
    {
        var request = new SaveShiftAssignmentsRequest { ClientId = 20, ShiftId = 1, EmployeeIds = [10], EffectiveFrom = Day, EffectiveTo = Day, WeekDays = "1", Reason = "October roster" };
        Assert.Null(LeaveAttendanceRepository.ValidateShiftAssignment(request));
        request.WeekDays = "2"; Assert.NotNull(LeaveAttendanceRepository.ValidateShiftAssignment(request));
        request.WeekDays = "1,1"; Assert.NotNull(LeaveAttendanceRepository.ValidateShiftAssignment(request));
        request.WeekDays = "1"; request.Reason = ""; Assert.NotNull(LeaveAttendanceRepository.ValidateShiftAssignment(request));
    }

    [Fact]
    public void AdjacentNightAndDayCannotOverlapInClockTime()
    {
        var night = Shift(); night.ShiftType = "Fixed"; night.StartTime = TimeSpan.FromHours(21); night.EndTime = TimeSpan.FromHours(6); night.IsOvernight = true;
        var day = Shift(2); day.ShiftType = "Fixed"; day.StartTime = TimeSpan.FromHours(5); day.EndTime = TimeSpan.FromHours(14);
        Assert.True(LeaveAttendanceRepository.ShiftWindowsOverlap(night, Day, day, Day.AddDays(1)));
        day.StartTime = TimeSpan.FromHours(6);
        Assert.False(LeaveAttendanceRepository.ShiftWindowsOverlap(night, Day, day, Day.AddDays(1)));
    }

    [Fact]
    public void ImpossibleThresholdsAndUnsupportedRuleCombinationsAreRejected()
    {
        var shift = Shift(); shift.Rules.MaximumHours = 7; Assert.NotNull(LeaveAttendanceRepository.ValidateShift(shift));
        shift.Rules.MaximumHours = 24; shift.Rules.Segments = [new(540, 1020)]; Assert.NotNull(LeaveAttendanceRepository.ValidateShift(shift));
        shift.Rules.Segments = []; shift.Rules.DayBoundary = TimeSpan.FromHours(25); Assert.NotNull(LeaveAttendanceRepository.ValidateShift(shift));
        shift.Rules.DayBoundary = null; shift.ShiftType = "Fixed"; shift.StartTime = TimeSpan.FromHours(9); shift.EndTime = TimeSpan.FromHours(16);
        Assert.Contains("available working time", LeaveAttendanceRepository.ValidateShift(shift));
    }
}
