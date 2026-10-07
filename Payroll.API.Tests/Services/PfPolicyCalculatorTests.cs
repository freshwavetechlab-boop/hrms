using System.Reflection;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class PfPolicyCalculatorTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);
    private static PfPolicyVersion Version(int number, DateOnly from, decimal? ceiling = 15000m, string basis = "CalendarDays", bool employer = false) => new()
    {
        Id = $"v{number}", VersionNumber = number, ClientId = 11, SalaryStructureId = "1", Name = $"PF {number}", EffectiveFrom = from,
        Status = "Published", BaseComponentCode = "BASIC", CeilingBasis = basis,
        Contributions = new[] { new PfContributionRule { ComponentId = "109", ComponentCode = "PF", StatutoryType = "PF Employee", RatePercent = 12m, MonthlyWageCeiling = ceiling } }
            .Concat(employer ? [new PfContributionRule { ComponentId = "201", ComponentCode = "EPF_ER", StatutoryType = "PF Employer", RatePercent = 13m, MonthlyWageCeiling = ceiling }] : []).ToList()
    };
    private static PfPolicyAttendanceDay[] Days(DateOnly? half = null, decimal defaultPaid = 1m) => Enumerable.Range(0, 30).Select(i => new PfPolicyAttendanceDay(Start.AddDays(i), Start.AddDays(i) == half ? .5m : defaultPaid, defaultPaid == 0 ? "A" : "Present")).ToArray();
    private static PfComponentSnapshot Calculate(PfPolicyPeriod period, decimal monthlyBase = 30000m, int payrollDays = 30, decimal paid = 30m) =>
        PfPolicyCalculator.Calculate(period, "1", "109", "PF", "PF Employee", new Dictionary<string, decimal> { ["BASIC"] = monthlyBase }, payrollDays, paid)!;

    [Fact]
    public void FutureAndOtherTemplateVersionsDoNotOptInLegacyPayroll()
    {
        Assert.Empty(PfPolicyCalculator.ApplicableVersions([Version(1, End.AddDays(1))], "1", Start, End));
        Assert.Empty(PfPolicyCalculator.ApplicableVersions([Version(1, Start)], "another", Start, End));
        Assert.Null(PfPolicyCalculator.Calculate(null, "1", "109", "PF", "PF Employee", new Dictionary<string, decimal>(), 30, 30));
    }

    [Fact]
    public void MidMonthVersionRequiresExplicitBaselineAndDatedAttendance()
    {
        Assert.Throws<InvalidOperationException>(() => PfPolicyCalculator.ApplicableVersions([Version(2, new(2026, 9, 23))], "1", Start, End));
        var versions = new[] { Version(1, Start), Version(2, new(2026, 9, 23), 25000m) };
        Assert.Throws<InvalidOperationException>(() => Calculate(new(Start, End, versions, [])));
        Assert.Throws<InvalidOperationException>(() => Calculate(new(Start, End, versions, Days().Skip(1).ToArray())));
        Assert.Throws<InvalidOperationException>(() => Calculate(new(Start, End, versions, Days()), paid: 29.5m));
    }

    [Fact]
    public void SplitCeilingsAreAllocatedAndContributionsRoundedOnce()
    {
        var period = new PfPolicyPeriod(Start, End, [Version(1, Start), Version(2, new(2026, 9, 23), 25000m)], Days());
        var result = Calculate(period);
        Assert.Equal(2120m, result.Contribution);
        Assert.Equal(17666.666667m, result.EpfWages);
        Assert.Equal(22, result.Segments[0].CalendarDays); Assert.Equal(8, result.Segments[1].CalendarDays);
        Assert.Equal("v1", result.Segments[0].VersionId); Assert.Equal("v2", result.Segments[1].VersionId);
        Assert.Equal(11000m, result.Segments[0].WageBase);
        Assert.Null(result.EdliWages); // Optional EDLI override is not silently inferred from PF.
    }

    [Fact]
    public void SeptemberSeventeenthUsesSixteenOldAndFourteenNewCeilingDays()
    {
        var period = new PfPolicyPeriod(Start, End, [Version(1, Start), Version(2, new(2026, 9, 17), 25000m)], Days());
        var result = Calculate(period);
        Assert.Equal(2360m, result.Contribution);
        Assert.Equal(19666.666667m, result.EpfWages);
        Assert.Equal(16, result.Segments[0].CalendarDays); Assert.Equal(14, result.Segments[1].CalendarDays);
        Assert.Equal(new DateOnly(2026, 9, 16), result.Segments[0].To);
        Assert.Equal(new DateOnly(2026, 9, 17), result.Segments[1].From);
        Assert.Equal(8000m, result.Segments[0].WageBase);
    }

    [Theory]
    [InlineData("Draft", false)]
    [InlineData("Failed", false)]
    [InlineData("Queued", false)]
    [InlineData("Processing", false)]
    [InlineData("Pending Approval", true)]
    [InlineData("Approved", true)]
    [InlineData("Partially Paid", true)]
    [InlineData("Paid", true)]
    public void FrozenPolicyRunsAllowExplicitDraftCorrectionButProtectApprovals(string status, bool protectedRun)
    {
        Assert.Equal(protectedRun, PayRunRepository.IsProtectedPolicyRun(status, ["[{\"pfPolicy\":{\"SchemaVersion\":1}}]"]));
        Assert.Equal(protectedRun, PayRunRepository.IsProtectedPolicyRun(status, ["[{\"attendancePolicy\":{\"SchemaVersion\":1}}]" ]));
        Assert.False(PayRunRepository.IsProtectedPolicyRun(status, ["[{\"Id\":\"PF\",\"amount\":1800}]" ]));
    }

    [Fact]
    public void HalfDayUsesItsActualDateAndPayableDayCapBasis()
    {
        var boundary = new DateOnly(2026, 9, 23);
        var versions = new[] { Version(1, Start, 15000m, "PayableDays"), Version(2, boundary, 25000m, "PayableDays") };
        var result = Calculate(new(Start, End, versions, Days(boundary)), paid: 29.5m);
        Assert.Equal(2070m, result.Contribution);
        Assert.Equal(22m, result.Segments[0].PayableDays); Assert.Equal(7.5m, result.Segments[1].PayableDays);
        var before = Calculate(new(Start, End, versions, Days(boundary.AddDays(-1))), paid: 29.5m);
        Assert.Equal(2090m, before.Contribution);
        Assert.Equal(21.5m, before.Segments[0].PayableDays); Assert.Equal(8m, before.Segments[1].PayableDays);
    }

    [Fact]
    public void CalendarCapUsesCalendarCycleLengthEvenWhenSalaryDivisorIsThirty()
    {
        var first = new DateOnly(2026, 10, 1); var last = new DateOnly(2026, 10, 31);
        Assert.Equal(1800m, Calculate(new(first, last, [Version(1, first)], []), payrollDays: 30, paid: 30).Contribution);
        var days = Enumerable.Range(0, 31).Select(i => new PfPolicyAttendanceDay(first.AddDays(i), i == 30 ? 0 : 1, i == 30 ? "A" : "Present")).ToArray();
        var split = Calculate(new(first, last, [Version(1, first), Version(2, new(2026, 10, 23), 25000m)], days), payrollDays: 30, paid: 30);
        Assert.Equal(decimal.Round((15000m * 22 / 31 + 25000m * 9 / 31) * .12m, 2), split.Contribution);
    }

    [Fact]
    public void AbsentDaysAreZeroAndWeeklyOffOnlyDaysRetainTheirResolvedPayableValues()
    {
        var versions = new[] { Version(1, Start), Version(2, new(2026, 9, 23), 25000m) };
        Assert.Equal(0m, Calculate(new(Start, End, versions, Days(defaultPaid: 0)), paid: 0).Contribution);
        var dates = Days(defaultPaid: 0).Select((d, i) => new PfPolicyAttendanceDay(d.Date, i is 5 or 12 or 19 or 26 ? 1 : 0, i is 5 or 12 or 19 or 26 ? "WO" : "A", "wo-v1")).ToArray();
        Assert.Equal(246.62m, Calculate(new(Start, End, versions, dates), monthlyBase: 15414m, paid: 4m).Contribution);
        Assert.All(Calculate(new(Start, End, versions, dates), monthlyBase: 15414m, paid: 4m).Segments.SelectMany(s => s.Attendance), d => Assert.Equal("wo-v1", d.AttendanceRuleVersionId));
    }

    [Fact]
    public void OneVersionAcceptsMonthlyHalfDaysWithoutInventingDateAllocationOrShift()
    {
        var result = Calculate(new(Start, End, [Version(1, Start, 25000m)], []), monthlyBase: 15414m, paid: 27.5m);
        Assert.Equal(1695.54m, result.Contribution); Assert.Empty(result.Segments[0].Attendance);
    }

    [Fact]
    public void SnapshotHasFrozenInputsAndUnsupportedComponentsStayOnExistingFormulas()
    {
        var version = Version(1, Start, 25000m); version.EdliMonthlyWageCeiling = 15000m;
        var result = Calculate(new(Start, End, [version], []), monthlyBase: 15414m);
        var saved = JsonSerializer.Serialize(result);
        version.Contributions[0].RatePercent = 99;
        Assert.Equal(saved, JsonSerializer.Serialize(result)); Assert.Equal(15000m, result.EdliWages);
        Assert.Null(PfPolicyCalculator.Calculate(new(Start, End, [version], []), "1", "202", "EPS", "EPS", new Dictionary<string, decimal> { ["BASIC"] = 15414 }, 30, 30));
    }

    [Fact]
    public void InvalidDefinitionsAndMetadataFailClearly()
    {
        var request = new SavePfPolicyVersionRequest { SalaryStructureId = "1", Name = "PF", Reason = "change", EffectiveFrom = Start, BaseComponentCode = "BASIC", Contributions = [new() { ComponentId = "109", RatePercent = 12 }] };
        Assert.Contains("choose", PfPolicyCalculator.ValidateDefinition(request)); request.CeilingBasis = "CalendarDays";
        Assert.Null(PfPolicyCalculator.ValidateDefinition(request)); request.Contributions = null!;
        Assert.NotNull(PfPolicyCalculator.ValidateDefinition(request));
        Assert.Throws<InvalidOperationException>(() => PfPolicyCalculator.Calculate(new(Start, End, [Version(1, Start)], []), "1", "109", "CHANGED", "PF Employee", new Dictionary<string, decimal> { ["BASIC"] = 15414 }, 30, 30));
    }

    [Fact]
    public void PayrollKeepsCanonicalLinesSummariesAndUnmappedEsiEps()
    {
        var legacy = Build(30, null);
        var policy = Build(30, new(Start, End, [Version(1, Start, 25000m, employer: true)], []));
        Assert.Equal(15414m, legacy.GrossPay); Assert.Equal(13498.40m, legacy.NetPay);
        Assert.Equal(15414m, policy.GrossPay); Assert.Equal(13448.72m, policy.NetPay);
        Assert.Equal(1965.28m, policy.StatutoryDeductions); Assert.Equal(0m, policy.OneTimeDeductions);
        using var oldJson = JsonDocument.Parse(legacy.DetailsJson); using var json = JsonDocument.Parse(policy.DetailsJson);
        Assert.False(PayRunRepository.HasPfPolicySnapshot(legacy.DetailsJson)); Assert.True(PayRunRepository.HasPfPolicySnapshot(policy.DetailsJson));
        var oldLines = oldJson.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("Id").GetString()!);
        var lines = json.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("Id").GetString()!);
        Assert.Equal(oldLines.Keys.Order(), lines.Keys.Order());
        Assert.Equal(oldLines["ESIC"].GetRawText(), lines["ESIC"].GetRawText()); Assert.Equal(oldLines["EPS"].GetRawText(), lines["EPS"].GetRawText());
        Assert.Equal(2003.82m, lines["EPF_ER"].GetProperty("amount").GetDecimal()); Assert.False(lines["PF"].GetProperty("ProRata").GetBoolean());
        Assert.Equal(policy.NetPay, lines["NET_PAY"].GetProperty("amount").GetDecimal());
        Assert.Equal("PF Employee", lines["PF"].GetProperty("StatutoryType").GetString());
        Assert.Equal(15414m, lines["PF"].GetProperty("pfPolicy").GetProperty("EpfWages").GetDecimal());
    }

    [Fact]
    public void RemovedOrReclassifiedMappedComponentsCannotSilentlyFallBack()
    {
        var period = new PfPolicyPeriod(Start, End, [Version(1, Start)], []);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Build(30, period, removePf: true)).InnerException);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Build(30, period, reclassifyPf: true)).InnerException);
    }

    [Fact]
    public void WeeklyOffPendingAndStaleMonthlyTotalsBlockBeforeFinalPayroll()
    {
        var days = Enumerable.Range(0, 30).Select(i => new WeeklyOffResolvedDay(1, Start.AddDays(i).ToDateTime(TimeOnly.MinValue), "Present", 1, "wo-v1", 1, "Recorded", false, "No shift")).ToArray();
        Assert.Equal("", PayRunRepository.AttendancePolicyError(Start.ToDateTime(TimeOnly.MinValue), End.ToDateTime(TimeOnly.MinValue), days, 30, 30));
        Assert.Contains("does not match", PayRunRepository.AttendancePolicyError(Start.ToDateTime(TimeOnly.MinValue), End.ToDateTime(TimeOnly.MinValue), days, 29, 29));
        days[0] = days[0] with { Pending = true, Status = "WO" };
        Assert.Contains("unresolved", PayRunRepository.AttendancePolicyError(Start.ToDateTime(TimeOnly.MinValue), End.ToDateTime(TimeOnly.MinValue), days, 30, 30));
        Assert.True(PayRunRepository.HasAttendancePolicySnapshot("[{\"Id\":\"GROSS_EARNED\",\"attendancePolicy\":{\"SchemaVersion\":1}}]"));
    }

    private static PayRunEmployee Build(decimal paid, PfPolicyPeriod? period, bool removePf = false, bool reclassifyPf = false)
    {
        var repo = typeof(PayRunRepository); var sourceType = repo.GetNestedType("PayRunSourceEmployee", BindingFlags.NonPublic)!;
        var source = Activator.CreateInstance(sourceType)!;
        void Set(string name, object value) => sourceType.GetProperty(name)!.SetValue(source, value);
        Set("Id", 1); Set("ClientId", 11); Set("EmployeeCode", "TEST1"); Set("FirstName", "Test"); Set("SalaryStructureId", "1"); Set("AnnualCtc", 214380m);
        if (period is not null) Set("PfPolicyPeriod", period);
        object Component(string id, string code, string category, string type, bool proRata, int priority) => new { id, code, name = code, category, componentRole = category == "Benefit" ? "Employer Contribution" : category == "Deduction" ? "Statutory Deduction" : "Regular Earning", statutoryType = type, proRata, priority, active = true, calculationType = "Formula", formula = "0" };
        object Line(string id, string formula, string proRataOverride = "") => new { componentId = id, calculationType = "Formula", formula, proRataOverride };
        var components = new[] { Component("101", "BASIC", "Earning", "None", true, 10), Component("109", "PF", reclassifyPf ? "Benefit" : "Deduction", "PF Employee", true, 110), Component("110", "ESIC", "Deduction", "ESI Employee", false, 120), Component("201", "EPF_ER", "Benefit", "PF Employer", true, 210), Component("202", "EPS", "Benefit", "EPS", true, 220) };
        var lines = new List<object> { Line("101", "15414"), Line("110", "BASIC * PAYABLE_DAYS / PAYROLL_DAYS * 0.75%"), Line("201", "MIN(BASIC * PAYABLE_DAYS / PAYROLL_DAYS,15000) * 13%"), Line("202", "BASIC * PAYABLE_DAYS / PAYROLL_DAYS * 3.25%") };
        if (!removePf) lines.Add(Line("109", "MIN(BASIC * PAYABLE_DAYS / PAYROLL_DAYS,15000) * 12%", "false"));
        var setup = JsonSerializer.Serialize(new { salaryComponents = components, salaryStructures = new[] { new { id = "1", clientId = "11", active = true, annualCtc = "214380", lines } } });
        return (PayRunEmployee)repo.GetMethod("BuildEmployee", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [0, source, setup, "2026-09", 30, paid, paid, Array.Empty<PayrollAdjustment>(), 0m, 0m, 0m, false, "TDS"])!;
    }
}
