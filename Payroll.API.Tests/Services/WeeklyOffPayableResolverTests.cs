using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class WeeklyOffPayableResolverTests
{
    private static readonly DateTime Sunday = new(2026, 9, 6);
    private static readonly HashSet<string> Unpaid = new(StringComparer.OrdinalIgnoreCase) { "UL" };
    private static WeeklyOffRuleVersion Rule(string mode, int version = 1, DateTime? effective = null) => new()
    { VersionId = $"rule-{version}", VersionNumber = version, Mode = mode, EffectiveFrom = effective ?? new DateTime(2026, 9, 1) };
    private static WeeklyOffAttendanceDay Day(DateTime date, string status, decimal value = 0) => new(11, date, status, value);
    private static WeeklyOffResolvedDay Off(string mode, string? left, string? right, decimal leftValue = 0, decimal rightValue = 0)
    {
        var days = new List<WeeklyOffAttendanceDay> { Day(Sunday, "WO", 1) };
        if (left is not null) days.Add(Day(Sunday.AddDays(-1), left, leftValue));
        if (right is not null) days.Add(Day(Sunday.AddDays(1), right, rightValue));
        return Assert.Single(WeeklyOffPayableResolver.Resolve(days, [Rule(mode)], "Sunday off", null, Unpaid, Sunday, Sunday));
    }

    [Theory]
    [InlineData("BothAdjacentAbsent", "A", "A", 0, false)]
    [InlineData("BothAdjacentAbsent", "A", "Present", 1, false)]
    [InlineData("BothAdjacentAbsent", "A", null, 1, true)]
    [InlineData("BothAdjacentAbsent", "Present", null, 1, false)]
    [InlineData("BothAdjacentAbsent", null, null, 1, true)]
    [InlineData("EitherAdjacentAbsent", "A", "Present", 0, false)]
    [InlineData("EitherAdjacentAbsent", "A", null, 0, false)]
    [InlineData("EitherAdjacentAbsent", "Present", null, 1, true)]
    [InlineData("EitherAdjacentAbsent", "Present", "Present", 1, false)]
    [InlineData("BothAdjacentAbsent", "UL", "LOP", 0, false)]
    [InlineData("EitherAdjacentAbsent", "CL", "Present", 1, false)]
    public void SandwichLogicDistinguishesKnownAbsenceFromMissing(string mode, string? left, string? right, decimal payable, bool pending)
    {
        var actual = Off(mode, left, right);
        Assert.Equal(payable, actual.PayableValue);
        Assert.Equal(pending, actual.Pending);
    }

    [Fact]
    public void HalfDaysAndPartiallyPaidUnpaidLeaveAreNotFullAbsences()
    {
        Assert.Equal(1, Off("BothAdjacentAbsent", "A", "Present", rightValue: .5m).PayableValue);
        Assert.Equal(1, Off("BothAdjacentAbsent", "A", "UL", rightValue: .5m).PayableValue);
    }

    [Fact]
    public void LegacyAndPaidRulesKeepExistingAmountsAndHalfDays()
    {
        WeeklyOffAttendanceDay[] days = [Day(Sunday, "WO", 0), Day(Sunday.AddDays(1), "H", 0), Day(Sunday.AddDays(2), "Present", .5m), Day(Sunday.AddDays(3), "A", 0)];
        foreach (WeeklyOffRuleVersion[] rules in new[] { Array.Empty<WeeklyOffRuleVersion>(), new[] { Rule("Paid") }, new[] { Rule("Unpaid", effective: Sunday.AddDays(10)) } })
        {
            var actual = WeeklyOffPayableResolver.Resolve(days, rules, "Sunday off", null, Unpaid, Sunday, Sunday.AddDays(3));
            Assert.Equal(new decimal[] { 1, 1, .5m, 0 }, actual.Select(d => d.PayableValue));
            Assert.DoesNotContain(actual, d => d.Pending);
        }
    }

    [Fact]
    public void RuleIsChosenForActualDateAndHolidayRemainsPaid()
    {
        var cutover = new DateTime(2026, 9, 17);
        WeeklyOffRuleVersion[] versions = [Rule("Paid"), Rule("Unpaid", 2, cutover)];
        var actual = WeeklyOffPayableResolver.Resolve([Day(cutover.AddDays(-1), "WO"), Day(cutover, "WO"), Day(cutover.AddDays(1), "H")], versions, "Sunday off", null, Unpaid, cutover.AddDays(-1), cutover.AddDays(1));
        Assert.Equal(new decimal[] { 1, 0, 1 }, actual.Select(d => d.PayableValue));
        Assert.Equal(new[] { 1, 2, 2 }, actual.Select(d => d.RuleVersionNumber!.Value));
    }

    [Fact]
    public void AlternateSaturdayBlockUsesFridayAndMonday()
    {
        var saturday = new DateTime(2026, 9, 12);
        var actual = WeeklyOffPayableResolver.Resolve([Day(saturday.AddDays(-1), "A"), Day(saturday, "WO", 1), Day(saturday.AddDays(1), "WO", 1), Day(saturday.AddDays(2), "A")],
            [Rule("BothAdjacentAbsent")], "Second & Fourth Saturday + Sunday off", null, Unpaid, saturday, saturday.AddDays(1));
        Assert.All(actual, d => { Assert.Equal(0, d.PayableValue); Assert.Equal(saturday.AddDays(-1), d.PreviousWorkingDate); Assert.Equal(saturday.AddDays(2), d.NextWorkingDate); });
        Assert.True(new AttendanceWorkWeek("Second & Fourth Saturday + Sunday off", null).IsWorking(new DateTime(2026, 9, 5)));
    }

    [Fact]
    public void CustomConfiguredWeekDoesNotAssumeSundayOff()
    {
        var friday = new DateTime(2026, 9, 4);
        const string config = "{\"workingDays\":[0,1,2,3,4],\"offSaturdays\":[]}";
        var actual = WeeklyOffPayableResolver.Resolve([Day(friday.AddDays(-1), "A"), Day(friday, "WO", 1), Day(friday.AddDays(1), "WO", 1), Day(friday.AddDays(2), "A")],
            [Rule("BothAdjacentAbsent")], "Branch roster", config, Unpaid, friday, friday.AddDays(1));
        Assert.All(actual, d => { Assert.Equal(0, d.PayableValue); Assert.Equal(friday.AddDays(2), d.NextWorkingDate); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"workingDays\":[\"Sunday\"]}")]
    [InlineData("[]")]
    public void UnknownOrInvalidCalendarRequiresReview(string? config)
    {
        var actual = Assert.Single(WeeklyOffPayableResolver.Resolve([Day(Sunday.AddDays(-1), "A"), Day(Sunday, "WO", 1), Day(Sunday.AddDays(1), "A")],
            [Rule("BothAdjacentAbsent")], "Unconfigured custom week", config, Unpaid, Sunday, Sunday));
        Assert.True(actual.Pending);
        Assert.Equal(1, actual.PayableValue);
        Assert.Null(actual.PreviousWorkingDate);
    }

    [Fact]
    public void BoundaryMissingNextMonthIsPendingUntilActualNeighborArrives()
    {
        var sunday = new DateTime(2026, 5, 31);
        var versions = new[] { Rule("BothAdjacentAbsent", effective: new DateTime(2026, 5, 1)) };
        var days = new List<WeeklyOffAttendanceDay> { Day(sunday.AddDays(-1), "A"), Day(sunday, "WO", 1) };
        var pending = Assert.Single(WeeklyOffPayableResolver.Resolve(days, versions, "Sunday off", null, Unpaid, sunday, sunday));
        Assert.True(pending.Pending);
        Assert.Equal(new DateTime(2026, 6, 1), pending.NextWorkingDate);
        days.Add(Day(new DateTime(2026, 6, 1), "A"));
        var resolved = Assert.Single(WeeklyOffPayableResolver.Resolve(days, versions, "Sunday off", null, Unpaid, sunday, sunday));
        Assert.False(resolved.Pending); Assert.Equal(0, resolved.PayableValue);
        Assert.True(WeeklyOffPayableResolver.IsAffectedByChange(resolved, new DateTime(2026, 6, 1), new DateTime(2026, 6, 1)));
        Assert.False(WeeklyOffPayableResolver.IsAffectedByChange(resolved, new DateTime(2026, 6, 2), new DateTime(2026, 6, 30)));
    }

    [Fact]
    public void PublishedCalendarDoesNotChangeWithLiveDropdownConfiguration()
    {
        var version = Rule("BothAdjacentAbsent") with { WorkWeekConfigs = new() { ["Branch"] = "{\"workingDays\":[1,2,3,4,5,6]}" } };
        var actual = Assert.Single(WeeklyOffPayableResolver.Resolve([Day(Sunday.AddDays(-1), "A"), Day(Sunday, "WO", 1), Day(Sunday.AddDays(1), "A")],
            [version], "Branch", "{\"workingDays\":[0,1,2,3,4,5,6]}", Unpaid, Sunday, Sunday));
        Assert.Equal(0, actual.PayableValue); Assert.False(actual.Pending);
    }

    [Fact]
    public void NewVersionsCannotOverwritePublishedHistoryOrIgnoreStaleRevision()
    {
        var version = Rule("Paid");
        Assert.NotNull(WeeklyOffPayableResolver.ValidatePublication([version], new() { ExpectedLatestVersion = 0, EffectiveFrom = Sunday }));
        Assert.NotNull(WeeklyOffPayableResolver.ValidatePublication([version], new() { ExpectedLatestVersion = 1, EffectiveFrom = version.EffectiveFrom }));
        Assert.Null(WeeklyOffPayableResolver.ValidatePublication([version], new() { ExpectedLatestVersion = 1, EffectiveFrom = Sunday }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PublishWeeklyOffRuleRequest>("{\"ClientId\":11}"));
    }

    [Fact]
    public void SettingsRoundTripPreservesDeviceCredentialsRulesAndVersionHistory()
    {
        var original = new AttendanceIntegrationRepository.IntegrationSettings
        {
            Devices = [new() { DeviceId = "test-device", TokenHash = "test-hash" }],
            Rules = new() { AllowManualHalfDay = true }, WeeklyOffRuleVersions = [Rule("BothAdjacentAbsent")]
        };
        var restored = JsonSerializer.Deserialize<AttendanceIntegrationRepository.IntegrationSettings>(JsonSerializer.Serialize(original))!;
        Assert.Equal("test-hash", Assert.Single(restored.Devices).TokenHash);
        Assert.True(restored.Rules.AllowManualHalfDay);
        Assert.Equal("rule-1", Assert.Single(restored.WeeklyOffRuleVersions).VersionId);
    }

    [Fact]
    public void NonCalendarCycleMapsBothSidesOfBoundaryToCorrectMonth()
    {
        var first = WeeklyOffAttendanceService.CycleFor(new DateTime(2026, 8, 26), 26, 25);
        var last = WeeklyOffAttendanceService.CycleFor(new DateTime(2026, 9, 25), 26, 25);
        Assert.Equal(first, last); Assert.Equal("2026-09", first.Month);
        Assert.Equal(new DateTime(2026, 8, 26), first.Start); Assert.Equal(new DateTime(2026, 9, 25), first.End);
        Assert.Equal("2026-10", WeeklyOffAttendanceService.CycleFor(new DateTime(2026, 9, 26), 26, 25).Month);
    }
}
