using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayRunEmployeeRecalculationTests
{
    private static PayRun Run(string status = "Draft") => new() { Id = 4, ClientId = 9, PayPeriod = "2026-10", Status = status, RunType = "Regular" };
    private static PayRunEmployee Row() => new() { Id = 7, PayRunId = 4, ClientId = 9, EmployeeId = 11, PaymentStatus = "Pending", PresentDays = null };

    [Theory]
    [InlineData("Approved")][InlineData("Pending Approval")][InlineData("Paid")][InlineData("Partially Paid")][InlineData("Processing")][InlineData("Failed")]
    public void Correction_never_recalls_approves_or_processes_a_non_draft(string status) => Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(status), Row()));

    [Fact]
    public void Target_requires_exact_membership_unpaid_regular_draft_but_allows_genuine_unknown_presence()
    {
        Assert.Empty(PayRunRepository.EmployeeRecalculationBlockers(Run(), Row()));
        var wrong = Row(); wrong.ClientId = 10; Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(), wrong));
        wrong = Row(); wrong.PayRunId = 5; Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(), wrong));
        wrong = Row(); wrong.PaymentStatus = "Paid"; Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(), wrong));
        wrong = Row(); wrong.PaymentDate = DateTime.UtcNow; Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(), wrong));
        wrong = Row(); wrong.IsSkipped = true; Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(), wrong));
        var offCycle = Run(); offCycle.RunType = "Off Cycle"; Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(offCycle, Row()));
        Assert.NotEmpty(PayRunRepository.EmployeeRecalculationBlockers(Run(), null));
    }

    [Fact]
    public void Confirmation_requires_fresh_fingerprint_and_bounded_reason()
    {
        var valid = new RecalculatePayRunEmployeeRequest(new string('a', 64), true, "Attendance corrected in the source module");
        PayRunRepository.ValidateEmployeeRecalculationRequest(valid);
        foreach (var invalid in new[] { valid with { Confirmed = false }, valid with { Reason = " " }, valid with { Reason = new string('a', 501) }, valid with { ExpectedFingerprint = "old" }, valid with { ExpectedFingerprint = new string('z', 64) } })
            Assert.Throws<InvalidOperationException>(() => PayRunRepository.ValidateEmployeeRecalculationRequest(invalid));
    }

    [Fact]
    public void Component_change_review_retains_category_identity_and_ignores_summary_duplicates()
    {
        const string before = "[{\"Id\":\"BASIC\",\"Name\":\"Basic\",\"Category\":\"Earning\",\"amount\":1000},{\"Id\":\"PF\",\"Name\":\"PF\",\"Category\":\"Deduction\",\"amount\":120},{\"Id\":\"NET\",\"Category\":\"Summary\",\"amount\":880}]";
        var changes = PayRunRepository.EmployeeRecalculationChanges(before, before.Replace("1000", "2000").Replace("120", "240").Replace("880", "1760"));
        Assert.Equal(2, changes.Count); Assert.Contains(changes, change => change.Code == "BASIC" && change.Before == 1000 && change.After == 2000);
        Assert.Contains(changes, change => change.Code == "PF" && change.Before == 120 && change.After == 240);
    }

    [Fact]
    public void Frozen_pf_binding_keeps_published_rule_but_allows_recomputed_wages_and_contribution()
    {
        var part = new PfPolicySegmentSnapshot("published-rule", 2, new(2026, 10, 1), new(2026, 10, 31), "BASIC", 10000, 31, 20, "PayableDays", 15000, 6000, 12, 720, null, []);
        var policy = new PfComponentSnapshot(1, "template", "pf", "PF", "PF Employee", new(2026, 10, 1), new(2026, 10, 31), 30, 20, 720, 6000, null, [part]);
        string Details(PfComponentSnapshot p) => JsonSerializer.Serialize(new[] { new { Id = "PF", Category = "Deduction", amount = p.Contribution, pfPolicy = p } });
        var changedAmount = policy with { Contribution = 840, Segments = [part with { MonthlyBase = 12000, WageBase = 7000, UnroundedContribution = 840 }] };
        Assert.Equal(PayRunRepository.EmployeeRecalculationPolicyBinding(Details(policy)), PayRunRepository.EmployeeRecalculationPolicyBinding(Details(changedAmount)));
        Assert.NotEqual(PayRunRepository.EmployeeRecalculationPolicyBinding(Details(policy)), PayRunRepository.EmployeeRecalculationPolicyBinding(Details(policy with { Segments = [part with { VersionId = "new-rule" }] })));
        Assert.NotEqual(PayRunRepository.EmployeeRecalculationPolicyBinding(Details(policy)), PayRunRepository.EmployeeRecalculationPolicyBinding("[]"));
    }

    [Fact]
    public void Payable_only_proof_is_bound_semantically_even_when_mysql_reorders_json_properties()
    {
        const string one = "[{\"Id\":\"ATTENDANCE_INPUT\",\"actualPresentDays\":null,\"sourceEvidence\":{\"version\":1,\"clientId\":9,\"employeeId\":11,\"payPeriod\":\"2026-10\",\"payrollDays\":30,\"payableDays\":20}}]";
        const string two = "[{\"sourceEvidence\":{\"payableDays\":20,\"employeeId\":11,\"payrollDays\":30,\"payPeriod\":\"2026-10\",\"clientId\":9,\"version\":1},\"actualPresentDays\":null,\"Id\":\"ATTENDANCE_INPUT\"}]";
        Assert.Equal(PayRunRepository.EmployeeRecalculationPolicyBinding(one), PayRunRepository.EmployeeRecalculationPolicyBinding(two));
        Assert.NotEqual(PayRunRepository.EmployeeRecalculationPolicyBinding(one), PayRunRepository.EmployeeRecalculationPolicyBinding(two.Replace("\"payableDays\":20", "\"payableDays\":21")));
    }

    [Fact]
    public void Period_employer_costs_keep_receipt_ids_without_removing_other_benefits_or_cash_lines()
    {
        const string original = "[{\"Id\":\"WAGE\",\"Category\":\"Earning\",\"amount\":1000},{\"Id\":\"OTHER_BENEFIT\",\"Category\":\"Benefit\",\"draftEmployerCost\":true,\"amount\":17},{\"Id\":\"NET_PAY\",\"Category\":\"Summary\",\"amount\":1000}]";
        var cost = new PayrollAdjustment { Id = 31, ComponentCode = "EPF_ER", ComponentName = "Extra employer PF", ReasonCode = "DRAFT_EMPLOYER_COST", AdjustmentType = "Benefit", Amount = 25, Taxable = false };
        using var result = JsonDocument.Parse(PayRunRepository.AppendRecalculationEmployerCosts(original, [cost]));
        Assert.Equal(4, result.RootElement.GetArrayLength()); Assert.Equal(17, result.RootElement[1].GetProperty("amount").GetDecimal());
        Assert.Equal(1000, result.RootElement[2].GetProperty("amount").GetDecimal()); Assert.Equal(31, result.RootElement[3].GetProperty("adjustmentIds")[0].GetInt32());
        Assert.Equal("Reviewed period employer cost", result.RootElement[3].GetProperty("source").GetString());
        cost.Taxable = true; Assert.Throws<InvalidOperationException>(() => PayRunRepository.AppendRecalculationEmployerCosts(original, [cost]));
    }

    [Fact]
    public void Auth_requires_active_password_complete_payroll_operator_and_permitted_client()
    {
        var context = new DefaultHttpContext(); Assert.False(PayRunEmployeeRecalculationEndpoints.CanUse(context, 9));
        var user = new AuthUser { IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [9, 10], Permissions = ["payroll.run"] };
        context.Items["User"] = user;
        Assert.True(PayRunEmployeeRecalculationEndpoints.CanUse(context, 9)); Assert.False(PayRunEmployeeRecalculationEndpoints.CanUse(context, 11));
        user.MustChangePassword = true; Assert.False(PayRunEmployeeRecalculationEndpoints.CanUse(context, 9)); user.MustChangePassword = false;
        user.IsActive = false; Assert.False(PayRunEmployeeRecalculationEndpoints.CanUse(context, 9)); user.IsActive = true;
        user.Permissions = ["payroll.approve"]; Assert.False(PayRunEmployeeRecalculationEndpoints.CanUse(context, 9));
    }
}
