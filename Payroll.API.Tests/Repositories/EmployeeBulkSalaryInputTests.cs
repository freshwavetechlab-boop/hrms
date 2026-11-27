using Payroll.API.Models;
using Payroll.API.Repositories;
using static Payroll.API.Repositories.EmployeeRepository;

namespace Payroll.API.Tests.Repositories;

public sealed class EmployeeBulkSalaryInputTests
{
    private static readonly List<string> Headers = ["Basic [SALARY_INPUT:10]", "Salary Template Id", "Salary Effective From", "Annual CTC"];
    private static readonly Dictionary<string, int> HeaderMap = new() { ["salarytemplateid"] = 1, ["salaryeffectivefrom"] = 2, ["annualctc"] = 3 };
    private static BulkSalaryRule Rule(string calculation = "Fixed Amount", string expression = "0") =>
        new("20", 1, "1", "10", "BASIC", "Basic", calculation, expression);
    private static BulkSalaryPatch? Parse(string amount, string template, string from, string ctc, params BulkSalaryRule[] rules) =>
        ReadBulkSalaryPatch([amount, template, from, ctc], HeaderMap, ReadSalaryInputColumns(Headers), rules.ToLookup(rule => rule.TemplateId));

    [Theory]
    [InlineData("", "25000.25", "300003")]
    [InlineData("", "0", "0")]
    [InlineData("", "", null)]
    [InlineData("100000", "8333.33", "100000")]
    [InlineData("1.50", "0.13", "1.5")]
    [InlineData("9999999999999999.99", "833333333333333.33", "9999999999999999.99")]
    public void Monthly_CTC_uses_the_same_annual_budget_in_review_and_dated_salary(string annual, string monthly, string? expected)
    {
        List<string> header = ["Employee Code", "Annual CTC", "Monthly_CTC"];
        var row = new List<string> { "E1", annual, monthly };
        var workbook = new EmployeeImportWorkbook(new() { ["Employees"] = [header, row] });
        var employee = new Employee { Id = 1, EmployeeCode = "E1", AnnualCtc = 999 };
        var review = Assert.Single(BuildImportIdentityPreflightRows(workbook, "update", [employee]));
        Assert.Empty(review.BlockingReasons);
        Assert.Equal(expected, review.Changes.SingleOrDefault(change => change.Field == "annualCtc")?.NewValue);

        var map = new Dictionary<string, int>(HeaderMap) { ["monthlyctc"] = 4 };
        var patch = ReadBulkSalaryPatch(["100", "20", "2026-10-01", annual, monthly], map, ReadSalaryInputColumns(Headers), new[] { Rule() }.ToLookup(rule => rule.TemplateId));
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), patch!.AnnualCtc);
    }

    [Theory]
    [InlineData("100000", "8333.34")]
    [InlineData("0", "1")]
    [InlineData("", "-10")]
    [InlineData("", "1.011")]
    [InlineData("", "833333333333333.34")]
    [InlineData("9999999999999999.99000000000000000001", "833333333333333.33")]
    public void Invalid_monthly_budget_blocks_new_and_existing_rows(string annual, string monthly)
    {
        var workbook = new EmployeeImportWorkbook(new() { ["Employees"] = [["Employee Code", "Annual CTC", "Monthly CTC"], ["E1", annual, monthly], ["NEW", annual, monthly]] });
        var review = BuildImportIdentityPreflightRows(workbook, "upsert", [new Employee { Id = 1, EmployeeCode = "E1" }]);
        Assert.All(review, row => {
            Assert.Contains(row.BlockingReasons, reason => reason.Contains("CTC", StringComparison.Ordinal));
            Assert.False(row.CanResolveConflict);
        });
    }

    [Fact]
    public void Empty_row_is_a_noop_but_zero_is_an_explicit_input()
    {
        Assert.Null(Parse("", "", "", "", Rule()));
        var patch = Parse("0", "20", "2026-10-01", "", Rule())!;
        Assert.Equal(0m, patch.Inputs["10"]);
        Assert.Null(patch.AnnualCtc);
        var result = MergeBulkSalaryInputs(new Dictionary<string, decimal> { ["basic"] = 250m, ["11"] = 73.5m },
            patch, new Dictionary<string, BulkSalaryRule> { ["10"] = Rule() });
        Assert.Equal(2, result.Count);
        Assert.Equal(0m, result["10"]);
        Assert.Equal(73.5m, result["11"]);
        Assert.False(result.ContainsKey("BASIC"));
    }

    [Theory]
    [InlineData("Formula", "ROUND(BASIC * 0.5%,0)")]
    [InlineData("Percentage of Component", "30")]
    [InlineData("Percentage of CTC", "10")]
    [InlineData("Slab Based", "0")]
    public void Calculated_lines_cannot_be_written_but_their_template_accepts_CTC(string method, string expression)
    {
        Assert.Throws<InvalidOperationException>(() => Parse("20", "20", "2026-10-01", "", Rule(method, expression)));
        var patch = Parse("", "20", "2026-10-01", "0", Rule(method, expression))!;
        Assert.Equal(0m, patch.AnnualCtc);
        Assert.Empty(patch.Inputs);
    }

    [Theory]
    [InlineData("", "20", "", "25")]
    [InlineData("", "20", "2026-10-01", "")]
    [InlineData("1", "99", "2026-10-01", "")]
    [InlineData("-1", "20", "2026-10-01", "")]
    [InlineData("-0.000000000000000000000000000000000000000001", "20", "2026-10-01", "")]
    [InlineData("1000000000.00000000000000000000000000001", "20", "2026-10-01", "")]
    [InlineData("1.23456000junk", "20", "2026-10-01", "")]
    [InlineData("3.42E+2", "20", "2026-10-01", "")]
    [InlineData("1,23", "20", "2026-10-01", "")]
    [InlineData("", "20", "2026-10-01", "1,23")]
    public void Invalid_context_or_literal_is_blocked(string amount, string template, string date, string annual) =>
        Assert.Throws<InvalidOperationException>(() => Parse(amount, template, date, annual, Rule()));

    [Theory]
    [InlineData("12345.678900000017", "12345.6789", "Fixed Amount")]
    [InlineData("40500.112500000011", "40500.1125", "Fixed Amount")]
    [InlineData("8765.430000000017", "8765.43", "Fixed Amount")]
    [InlineData("625.123456", "625.1235", "Daily Rate")]
    [InlineData("625.12345", "625.1235", "Daily Rate")]
    [InlineData("1.00001", "1", "Fixed Amount")]
    [InlineData("1.2344499999999999999999999999999999999", "1.2344", "Daily Rate")]
    [InlineData("999999999.99999", "1000000000", "Fixed Amount")]
    [InlineData("1000000000.000000", "1000000000", "Fixed Amount")]
    public void Component_input_preserves_four_decimals_and_rounds_longer_values(string raw, string expected, string method)
    {
        var patch = Parse(raw, "20", "2026-10-01", "", Rule(method));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), patch!.Inputs["10"]);
    }

    [Fact]
    public void Daily_rate_input_accepts_zero_and_calculated_net_total_is_read_only()
    {
        Assert.Equal(0m, Parse("0", "20", "2026-10-01", "", Rule("Daily Rate"))!.Inputs["10"]);
        Assert.Throws<InvalidOperationException>(() => Parse("1", "20", "2026-10-01", "", Rule() with { Code = "NET_PAY" }));
        Assert.Equal(2.25m, Parse("2.25", "20", "2026-10-01", "", Rule("Daily Rate"))!.Inputs["10"]);
    }

    [Fact]
    public void History_uses_dated_values_and_never_copies_a_future_salary_into_an_earlier_gap()
    {
        var master = new Employee { SalaryStructureId = "future", AnnualCtc = 999, SalaryComponents = new() { ["10"] = 99 } };
        var patch = Parse("0", "20", "2026-10-01", "", Rule())!;
        var future = new ClosedSalaryHistoryRow { Id = 9, EffectiveFrom = new(2026, 11, 01), SalaryStructureId = "future", SalaryJson = "{\"10\":99}" };
        var effective = new ClosedSalaryHistoryRow { Id = 7, EffectiveFrom = new(2026, 08, 01), EffectiveTo = new(2026, 10, 31),
            SalaryStructureId = "20", AnnualCtc = 200, SalaryJson = "{\"10\":5,\"11\":15}" };
        Assert.Empty(BulkSalaryBasis(master, [future], patch).SalaryComponents);
        var basis = BulkSalaryBasis(master, [effective, future], patch);
        Assert.Equal(200, basis.AnnualCtc);
        Assert.Equal(15, basis.SalaryComponents["11"]);
        Assert.Equal(99, master.SalaryComponents["10"]);
    }

    [Fact]
    public void Current_master_fallback_is_retained_for_earlier_empty_or_unversioned_payroll_and_future_changes()
    {
        var employee = new Employee { DateOfJoining = "2021-01-01" };
        var snapshot = new ClosedSalaryHistoryRow { Id = 1, EffectiveFrom = new(2021, 01, 01), SalaryJson = "{\"10\":20}" };
        var change = new DateTime(2021, 03, 01);
        Assert.True(CanProjectBulkSalary(employee, [snapshot], change));
        Assert.False(CanProjectBulkSalary(employee, [], change));
        Assert.False(CanProjectBulkSalary(employee, [snapshot], DateTime.UtcNow.AddDays(10)));
        snapshot.SalaryJson = "{}";
        Assert.False(CanProjectBulkSalary(employee, [snapshot], change));
        snapshot.SalaryJson = "{\"10\":20}";
        snapshot.EffectiveTo = new(2021, 01, 31);
        Assert.False(CanProjectBulkSalary(employee, [snapshot], change));
    }

    private static BulkSalaryPatch? Assignment(string mode = "update", string enabled = "TRUE", string employee = "5",
        string date = "2026-10-01", string ctc = "", string template = "20")
    {
        var map = new Dictionary<string, int> { ["employeeid"] = 0, ["employeecode"] = 1, ["salarytemplateid"] = 2,
            ["salaryeffectivefrom"] = 3, ["assignsalarytemplateonly"] = 4 };
        if (ctc.Length > 0) map["annualctc"] = 5;
        return ReadBulkSalaryPatch([employee, "E5", template, date, enabled, ctc], map, [],
            new[] { Rule("Formula", "BASIC * 12%") }.ToLookup(rule => rule.TemplateId), mode);
    }

    [Fact]
    public void Explicit_template_only_assignment_works_with_no_annual_ctc_or_manual_components()
    {
        var patch = Assignment()!;
        Assert.True(patch.AssignmentOnly);
        Assert.Equal("20", patch.TemplateId);
        Assert.Null(patch.AnnualCtc);
        Assert.Empty(patch.Inputs);
        var empty = BulkSalaryBasis(new Employee(), [], patch);
        Assert.Equal(0m, empty.AnnualCtc);
        Assert.Empty(empty.SalaryComponents);
        var employee = new Employee { AnnualCtc = 9876m, SalaryJson = "{\"99\":\"5.25\"}" };
        var basis = BulkSalaryBasis(employee, [], patch);
        Assert.Equal(9876m, basis.AnnualCtc);
        Assert.Equal(5.25m, basis.SalaryComponents["99"]);
    }

    [Theory]
    [InlineData("insert", "TRUE", "5", "2026-10-01", "", "20")]
    [InlineData("upsert", "TRUE", "5", "2026-10-01", "", "20")]
    [InlineData("update", "", "5", "2026-10-01", "", "20")]
    [InlineData("update", "TRUE", "", "2026-10-01", "", "20")]
    [InlineData("update", "TRUE", "5", "", "", "20")]
    [InlineData("update", "TRUE", "5", "2026-10-01", "0", "20")]
    [InlineData("update", "TRUE", "5", "2026-10-01", "", "999")]
    public void Template_only_assignment_requires_an_existing_target_and_cannot_change_amounts(
        string mode, string marker, string employee, string date, string ctc, string template) =>
        Assert.Throws<InvalidOperationException>(() => Assignment(mode, marker, employee, date, ctc, template));

    [Fact]
    public void A_template_only_assignment_blocks_overlapping_pay_and_ambiguous_future_inputs()
    {
        var patch = Assignment()!;
        var employee = new Employee { AnnualCtc = 600 };
        var first = new ClosedSalaryHistoryRow { Id = 1, EffectiveFrom = new(2026, 10, 1), AnnualCtc = 300 };
        var other = new ClosedSalaryHistoryRow { Id = 2, EffectiveFrom = new(2026, 10, 1), AnnualCtc = 600 };
        Assert.Throws<InvalidOperationException>(() => BulkSalaryBasis(employee, [first, other], patch));
        other.EffectiveFrom = new(2026, 11, 1);
        Assert.Throws<InvalidOperationException>(() => BulkSalaryBasis(employee, [other], patch));
        Assert.Equal(300, BulkSalaryBasis(employee, [first, other], patch).AnnualCtc);
    }

    [Fact]
    public void Force_defaults_does_not_replace_existing_dated_amounts_or_override_closed_and_overlapping_records()
    {
        var force = Assignment()! with { ForceSalaryDefaults = true };
        var master = new Employee { SalaryStructureId = "future", AnnualCtc = 240000, SalaryComponents = new() { ["10"] = 20000 } };
        var existing = new ClosedSalaryHistoryRow { Id = 1, EffectiveFrom = force.From, SalaryStructureId = "current",
            AnnualCtc = 120000, SalaryJson = "{\"10\":10000}" };
        var future = new ClosedSalaryHistoryRow { Id = 2, EffectiveFrom = force.From.AddMonths(1), SalaryStructureId = "future",
            ActionType = "Bulk Salary Input", IsBulkSalaryInput = true, AnnualCtc = 240000, SalaryJson = "{\"10\":20000}" };
        var gap = BulkSalaryBasis(master, [future], force);
        Assert.Equal(0m, gap.AnnualCtc);
        Assert.Empty(gap.SalaryComponents);
        var dated = BulkSalaryBasis(master, [existing, future], force);
        Assert.Equal(120000m, dated.AnnualCtc);
        Assert.Equal(10000m, dated.SalaryComponents["10"]);
        future.EffectiveFrom = force.From;
        Assert.Throws<InvalidOperationException>(() => BulkSalaryBasis(master, [existing, future], force));
        existing.ActionType = "Closed Excel Salary";
        Assert.Throws<InvalidOperationException>(() => BulkSalaryBasis(master, [existing], force));
        Assert.Equal(20000m, master.SalaryComponents["10"]);
    }

    [Fact]
    public void Assignment_targets_are_explicit_client_ids_and_never_guessed_by_code_or_profile()
    {
        var employee = new Employee { Id = 8, EmployeeCode = "CLIENT8", FirstName = "Test" };
        var sameProfile = new Employee { Id = 9, EmployeeCode = "CLIENT9", FirstName = "Test" };
        List<List<string>> rows = [["Employee ID", "Employee Code"], ["8", "client8"], ["999", "CLIENT9"], ["9", "CLIENT8"]];
        var result = BuildTemplateAssignmentRows(rows, new() { ["employeeid"] = 0, ["employeecode"] = 1 }, "Employees", [employee, sameProfile]);
        Assert.Equal(8, result[0].MatchedEmployeeId);
        Assert.Empty(result[0].BlockingReasons);
        Assert.Equal(["Exact Employee ID", "Exact Employee Code"], result[0].MatchReasons);
        Assert.All(result.Skip(1), row => { Assert.Null(row.MatchedEmployeeId); Assert.Equal("Blocked", row.MatchStatus); Assert.False(row.CanResolveConflict); });
        rows.Add(["8", "CLIENT8"]);
        result = BuildTemplateAssignmentRows(rows, new() { ["employeeid"] = 0, ["employeecode"] = 1 }, "Employees", [employee, sameProfile]);
        Assert.Contains("more than once", result[0].BlockingReasons.Single());
        Assert.Contains("more than once", result[3].BlockingReasons.Single());
    }

    [Fact]
    public void Empty_dated_employee_input_remains_empty_when_next_month_has_amounts()
    {
        var current = new Employee { DateOfJoining = "2021-01-01", SalaryComponents = new() { ["10"] = 1500 } };
        var historical = new ClosedSalaryHistoryRow { EffectiveFrom = new(2021, 1, 1), SalaryJson = "{}", IsBulkSalaryInput = true };
        var date = new DateTime(2021, 2, 1);
        var patch = new BulkSalaryPatch("20", date, null, []);
        Assert.Empty(BulkSalaryBasis(current, [historical], patch).SalaryComponents);
        Assert.True(CanProjectBulkSalary(current, [historical], date));
        historical.IsBulkSalaryInput = false;
        Assert.Equal(1500m, BulkSalaryBasis(current, [historical], patch).SalaryComponents["10"]);
    }

    [Fact]
    public void Correct_assignment_in_closed_or_overlapping_rows_needs_no_salary_edit()
    {
        var patch = Assignment()!;
        var closed = new ClosedSalaryHistoryRow { EffectiveFrom = patch.From, SalaryStructureId = patch.TemplateId, ActionType = "Closed Excel Salary" };
        var overlapping = new ClosedSalaryHistoryRow { EffectiveFrom = patch.From.AddDays(-1), SalaryStructureId = patch.TemplateId };
        Assert.True(AlreadyAssignedAtDate([closed, overlapping], patch));
        overlapping.SalaryStructureId = "other";
        Assert.False(AlreadyAssignedAtDate([closed, overlapping], patch));
        Assert.False(AlreadyAssignedAtDate([], patch));
        Assert.False(AlreadyAssignedAtDate([closed], patch with { AssignmentOnly = false }));
    }
}
