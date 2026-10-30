using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipOverviewTests
{
    const string A = "0123456789abcdef0123456789abcdef", B = "abcdef0123456789abcdef0123456789";
    private static ExcelPayslipRow Row(string code = "GAD-1", string name = "Synthetic Person", int? masterId = 10, decimal net = 100) => new()
    { Id = Guid.NewGuid().ToString(), SourceRow = 6, EmployeeCode = code, EmployeeName = name, NetPay = net, Earnings = [new() { Label = "Basic", Amount = 100 }], Employment = masterId.HasValue ? new(masterId, code, "Active", null, false, false) : null };
    private static ExcelPayslipOverviewInput Input(string id = A, int client = 42, string month = "2026-09", params ExcelPayslipRow[] rows) => new(new()
    { Id = id, ClientId = client, ClientName = "Synthetic client " + client, Month = month, SourceFileName = "Synthetic.xlsx", SheetName = "Salary", HeaderRow = 5, Rows = rows.Length > 0 ? rows.ToList() : [Row()] }, true);
    private static ExcelPayslipOverviewRequest Request(params ExcelPayslipOverviewInput[] inputs) => new() { Scopes = inputs.Select(i => new ExcelPayslipOverviewScope(i.Batch.ClientId, i.Batch.Id)).ToList() };

    [Fact]
    public void NoDefaultActiveOrLatestVersionIsInvented()
    {
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipOverviewService.Validate(new()));
        var old = Input(); var newer = Input(B);
        var result = ExcelPayslipOverviewService.Build([old], Request(old));
        Assert.Single(result.Rows); Assert.Equal(A, result.Rows[0].BatchId); Assert.Contains("not a persisted active set", result.ScopeLabel);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipOverviewService.Build([old, newer], Request(old)));
    }

    [Fact]
    public void ReadyReviewAndBlockedRowsAreAllIncluded()
    {
        var blocked = Row("GAD-3", "Synthetic Exit", 12); blocked.Employment = new(12, "GAD-3", "Resigned", "2026-08-31", true, false);
        var input = Input(A, 42, "2026-09", Row(), Row("GAD-2", "Synthetic Review", 11, 120), blocked);
        var result = ExcelPayslipOverviewService.Build([input], Request(input));
        Assert.Equal(3, result.Totals.SourceRows); Assert.Equal(1, result.Totals.ReadyRows); Assert.Equal(1, result.Totals.ReviewRows); Assert.Equal(1, result.Totals.BlockedRows);
        Assert.All(result.Rows, row => Assert.NotEmpty(row.Reasons));
    }

    [Fact]
    public void ClientScopedMasterIdentitiesNeverBecomeGlobalPersons()
    {
        var first = Input(); var second = Input(B, 43);
        var result = ExcelPayslipOverviewService.Build([first, second], Request(first, second));
        Assert.Equal(2, result.Totals.SourceRows); Assert.Equal(2, result.Totals.UniqueScopedCodeGroups); Assert.Equal(2, result.Totals.MatchedMasterRecords);
    }

    [Fact]
    public void ConflictingNamesForOneCodeAreNotMergedOrCountedAsVerifiedMatches()
    {
        var input = Input(A, 42, "2026-09", Row(), Row(name: "Different Synthetic", masterId: 11));
        var result = ExcelPayslipOverviewService.Build([input], Request(input));
        Assert.Equal(2, result.Totals.ConflictRows); Assert.Equal(0, result.Totals.MatchedMasterRecords); Assert.Equal(0, result.Totals.UniqueScopedCodeGroups);
    }

    [Fact]
    public void OverlappingVersionsRemainTwoRowsAndOneScopedIdentity()
    {
        var first = Input(); var second = Input(B);
        var result = ExcelPayslipOverviewService.Build([first, second], Request(first, second));
        Assert.Equal(2, result.Totals.SourceRows); Assert.Equal(2, result.Totals.OverlapRows); Assert.Equal(2, result.Totals.ReviewRows);
        Assert.Equal(1, result.Totals.MatchedMasterRecords); Assert.Equal(1, result.Totals.UniqueScopedCodeGroups);
        var differentMonth = Input(B, 42, "2026-08");
        Assert.Equal(0, ExcelPayslipOverviewService.Build([first, differentMonth], Request(first, differentMonth)).Totals.OverlapRows);
    }

    [Fact]
    public void EmployeeGroupsAndExcludedRowsCoverTheFilteredScopeBeforePagination()
    {
        var input = Input(A, 42, "2026-09", Row(), Row("", "Synthetic Missing Code", 20),
            Row("CONFLICT", "First Synthetic Identity", 30), Row("CONFLICT", "Other Synthetic Identity", 31),
            Row("UNMATCHED", "Synthetic New Code", null));
        var request = Request(input); request.PageSize = 1;
        var first = ExcelPayslipOverviewService.Build([input], request);
        request.Page = 5;
        var last = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(5, first.Totals.SourceRows); Assert.Single(first.Rows); Assert.Single(last.Rows);
        Assert.Equal(2, first.Totals.UniqueScopedCodeGroups); Assert.Equal(3, first.Totals.UncountedEmployeeRows);
        Assert.Equal(first.Totals.UniqueScopedCodeGroups, last.Totals.UniqueScopedCodeGroups);
        Assert.Equal(first.Totals.UncountedEmployeeRows, last.Totals.UncountedEmployeeRows);
        request.MasterStatus = "Matched";
        var matched = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(2, matched.Totals.MatchedMasterRecords);
        Assert.Equal(1, matched.Totals.UniqueScopedCodeGroups); Assert.Equal(1, matched.Totals.UncountedEmployeeRows);
        Assert.Contains(matched.Rows, row => row.EmployeeCode.Length == 0 && row.MasterEmployeeId == 20);
    }

    [Fact]
    public void OverlapOnlyReturnsSixParticipatingRowsInThreeGroupsAcrossPages()
    {
        var first = Input(A, 42, "2026-09", Row(), Row("GAD-2", "Second Synthetic", 11),
            Row("GAD-3", "Third Synthetic", 12), Row("SINGLE", "Synthetic Nonoverlap", 13, 999));
        var second = Input(B, 42, "2026-09", Row(), Row("GAD-2", "Second Synthetic", 11), Row("GAD-3", "Third Synthetic", 12));
        var request = Request(first, second); request.OverlapOnly = true; request.PageSize = 2;
        var pageOne = ExcelPayslipOverviewService.Build([first, second], request);
        request.Page = 3;
        var pageThree = ExcelPayslipOverviewService.Build([first, second], request);
        Assert.Equal(7, pageOne.ScopeTotalRows); Assert.Equal(6, pageOne.Totals.SourceRows);
        Assert.Equal(6, pageOne.Totals.OverlapRows); Assert.Equal(3, pageOne.Totals.UniqueScopedCodeGroups);
        Assert.Equal(3, pageOne.Totals.SourceRows - pageOne.Totals.UniqueScopedCodeGroups); // Six participants are three repeated rows, not six extra employees.
        Assert.Equal(0, pageOne.Totals.UncountedEmployeeRows); Assert.Equal(600, pageOne.Totals.SourceNetPay);
        Assert.Equal(600, pageOne.Totals.SourceGrossEarnings); Assert.Equal(0, pageOne.Totals.SourceDeductions);
        Assert.Equal(pageOne.Totals.SourceRows, pageThree.Totals.SourceRows);
        Assert.Equal(pageOne.Totals.SourceNetPay, pageThree.Totals.SourceNetPay);
        Assert.Equal(2, pageOne.Rows.Count); Assert.Equal(2, pageThree.Rows.Count);
        Assert.All(pageOne.Rows.Concat(pageThree.Rows), row => Assert.True(row.ScopeOverlap));
        Assert.DoesNotContain(pageOne.Rows.Select(row => row.Key), key => pageThree.Rows.Any(row => row.Key == key));
        request.Search = "GAD-1";
        var searched = ExcelPayslipOverviewService.Build([first, second], request);
        Assert.Equal(2, searched.Totals.SourceRows); Assert.Equal(1, searched.Totals.UniqueScopedCodeGroups);
        Assert.Equal(200, searched.Totals.SourceNetPay);
        request.Search = ""; request.Readiness = "Ready";
        var ready = ExcelPayslipOverviewService.Build([first, second], request);
        Assert.Equal(0, ready.Totals.SourceRows); Assert.Equal(0, ready.Totals.SourceNetPay);
    }

    [Fact]
    public void OverlapOnlyDoesNotCombineDifferentClientsOrPeriods()
    {
        var september = Input(); var october = Input(B, 42, "2026-10");
        var otherClient = Input("11111111111111111111111111111111", 43);
        var request = Request(september, october, otherClient);
        var all = ExcelPayslipOverviewService.Build([september, october, otherClient], request);
        Assert.Equal(3, all.Totals.SourceRows); Assert.Equal(2, all.Totals.UniqueScopedCodeGroups);
        Assert.Equal(0, all.Totals.OverlapRows);
        request.OverlapOnly = true;
        var overlapping = ExcelPayslipOverviewService.Build([september, october, otherClient], request);
        Assert.Empty(overlapping.Rows); Assert.Equal(0, overlapping.Totals.SourceRows);
        Assert.Equal(0, overlapping.Totals.UncountedEmployeeRows); Assert.Equal(0, overlapping.Totals.SourceNetPay);
    }

    [Fact]
    public void MissingCodeOrUnmatchedRowsDoNotProveMissingMasterEmployees()
    {
        var input = Input(A, 42, "2026-09", Row("", masterId: null), Row("GAD-NEW", "Synthetic Unmatched", null));
        var result = ExcelPayslipOverviewService.Build([input], Request(input));
        Assert.Equal(1, result.Totals.MissingCodeRows); Assert.Equal(1, result.Totals.UnmatchedRows); Assert.Equal(0, result.Totals.MatchedMasterRecords);
        Assert.All(result.Rows, r => Assert.Contains("Not previewed", r.SetupDecision));
    }

    [Fact]
    public void CountFilteringPaginationAndRepeatedReadRequestsStayConsistent()
    {
        var input = Input(A, 42, "2026-09", Row("GAD-1"), Row("GAD-2", "Second Synthetic", 11), Row("GAD-3", "Third Synthetic", 12, 120));
        var request = Request(input); request.Readiness = "Ready"; request.PageSize = 1;
        var first = ExcelPayslipOverviewService.Build([input], request); request.Page = 2;
        var second = ExcelPayslipOverviewService.Build([input], request); var repeated = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(3, first.ScopeTotalRows); Assert.Equal(2, first.Totals.SourceRows); Assert.Single(first.Rows); Assert.Single(second.Rows);
        Assert.NotEqual(first.Rows[0].Key, second.Rows[0].Key); Assert.Equal(second.Snapshot, repeated.Snapshot); Assert.Equal(2, second.Totals.ReadyRows);
        request.Search = "No such synthetic"; Assert.Empty(ExcelPayslipOverviewService.Build([input], request).Rows);
    }

    [Fact]
    public void SearchCannotHideAnIdentityConflictElsewhereInTheSelectedScope()
    {
        var input = Input(A, 42, "2026-09", Row(name: "First Synthetic"), Row(name: "Second Synthetic", masterId: 11));
        var request = Request(input); request.Search = "First Synthetic";
        var result = ExcelPayslipOverviewService.Build([input], request);
        Assert.Single(result.Rows); Assert.Equal("Conflict", result.Rows[0].MasterStatus); Assert.Equal(1, result.Totals.ConflictRows);
    }

    [Fact]
    public void OverviewJsonExcludesPrivateInformationEmailAndIndividualSalaryAmounts()
    {
        var row = Row("EMP-111122223333", "Synthetic Person ABCDE1234F"); row.Email = "private@example.invalid";
        row.Information = [new() { Label = "UAN", Value = "111122223333" }, new() { Label = "Bank account", Value = "555566667777" }];
        var input = Input(A, 42, "2026-09", row); var result = ExcelPayslipOverviewService.Build([input], Request(input));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("111122223333", json); Assert.DoesNotContain("555566667777", json); Assert.DoesNotContain("ABCDE1234F", json); Assert.DoesNotContain(row.Email, json);
        Assert.DoesNotContain("Information", json); Assert.DoesNotContain("\"Earnings\":", json); Assert.DoesNotContain("\"NetPay\":", json);
        Assert.Equal(100, result.Totals.SourceNetPay);
        var individualRows = JsonSerializer.Serialize(result.Rows);
        Assert.DoesNotContain("SourceNetPay", individualRows); Assert.DoesNotContain("Amount", individualRows);
    }

    [Fact]
    public void DuplicateMasterCodeAndPrivateOnlySearchFailClosed()
    {
        var input = Input(); var request = Request(input);
        var result = ExcelPayslipOverviewService.Build([input], request, new HashSet<(int, string)> { (42, "GAD-1") });
        Assert.Equal("Conflict", result.Rows.Single().MasterStatus); Assert.Equal(0, result.Totals.MatchedMasterRecords);
        input.Batch.Rows[0].Information.Add(new() { Label = "Private", Value = "Not searchable" }); request.Search = "Not searchable";
        Assert.Empty(ExcelPayslipOverviewService.Build([input], request).Rows);
    }

    [Fact]
    public void MoneyTotalsCoverAllFilteredRowsBeforePaginationAndNeverOnlyTheVisiblePage()
    {
        var input = Input(A, 42, "2026-09", Row("GAD-1"), Row("GAD-2", "Second Synthetic", 11), Row("GAD-3", "Third Synthetic", 12, 120));
        var request = Request(input); request.PageSize = 1;
        var first = ExcelPayslipOverviewService.Build([input], request); request.Page = 2;
        var second = ExcelPayslipOverviewService.Build([input], request);
        Assert.Single(first.Rows); Assert.Single(second.Rows);
        Assert.Equal(300, first.Totals.SourceGrossEarnings); Assert.Equal(320, first.Totals.SourceNetPay);
        Assert.Equal(first.Totals.SourceNetPay, second.Totals.SourceNetPay);
        request.Readiness = "Ready";
        Assert.Equal(200, ExcelPayslipOverviewService.Build([input], request).Totals.SourceNetPay);
        request.Readiness = "All"; request.Search = "Third Synthetic";
        Assert.Equal(120, ExcelPayslipOverviewService.Build([input], request).Totals.SourceNetPay);
        request.Search = "No such synthetic";
        var empty = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(0, empty.Totals.SourceRows); Assert.Equal(0, empty.Totals.SourceNetPay);
        Assert.Equal(0, empty.Totals.SourceGrossEarnings); Assert.Equal(0, empty.Totals.SourceDeductions);
    }

    [Fact]
    public void MoneyTotalsRespectMasterFilterAndIncludeBlockedRowsOnlyWhenSelected()
    {
        var matched = Row("MATCHED", net: 100);
        var unmatched = Row("NEW", "Synthetic Unmatched", null, 80);
        var blocked = Row("EXIT", "Synthetic Exit", 12, 50); blocked.Employment = new(12, "EXIT", "Resigned", "2026-08-31", true, false);
        var input = Input(A, 42, "2026-09", matched, unmatched, blocked); var request = Request(input);
        Assert.Equal(230, ExcelPayslipOverviewService.Build([input], request).Totals.SourceNetPay);
        request.MasterStatus = "Unmatched";
        Assert.Equal(80, ExcelPayslipOverviewService.Build([input], request).Totals.SourceNetPay);
        request.MasterStatus = "All"; request.Readiness = "Blocked";
        Assert.Equal(50, ExcelPayslipOverviewService.Build([input], request).Totals.SourceNetPay);
    }

    [Fact]
    public void MoneyUsesDeclaredPayslipTotalsAndRoundsEachEmployeeBeforeSumming()
    {
        var first = Row("GAD-1", net: 9.005m); first.Earnings[0].Amount = 10.005m; first.Deductions = [new() { Label = "Deduction", Amount = 1 }];
        var second = Row("GAD-2", "Synthetic Second", 11, 19.005m); second.Earnings[0].Amount = 20.005m; second.Deductions = [new() { Label = "Deduction", Amount = 1 }];
        var input = Input(A, 42, "2026-09", first, second); var request = Request(input);
        var result = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(30.02m, result.Totals.SourceGrossEarnings); Assert.Equal(2m, result.Totals.SourceDeductions); Assert.Equal(28.02m, result.Totals.SourceNetPay);
        Assert.Contains("before summing", result.Totals.MonetaryRoundingBasis);
        first.DeclaredGross = 40.005m; first.DeclaredDeductions = 5.005m;
        result = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(60.02m, result.Totals.SourceGrossEarnings); Assert.Equal(6.01m, result.Totals.SourceDeductions);
        Assert.Equal(28.02m, result.Totals.SourceNetPay); // Never recompute an authoritative saved source net.
    }

    [Fact]
    public void MoneyReusesPdfDisplayPrecisionForCachedTailsNegativeAndZeroAmounts()
    {
        var positive = Row("GAD-1", net: 14129.499999999998m);
        positive.Earnings[0].Amount = 14129.499999999998m;
        var negative = Row("GAD-2", "Synthetic Correction", 11, -0.005m); negative.Earnings[0].Amount = -0.005m;
        negative.Deductions = [new() { Label = "Correction", Amount = -0.005m }];
        var zero = Row("GAD-3", "Synthetic Zero", 12, 0); zero.Earnings.Clear();
        var input = Input(A, 42, "2026-09", positive, negative, zero);
        var result = ExcelPayslipOverviewService.Build([input], Request(input));
        Assert.Equal(14129.49m, result.Totals.SourceNetPay); Assert.Equal(14129.49m, result.Totals.SourceGrossEarnings);
        Assert.Equal(-0.01m, result.Totals.SourceDeductions);
        Assert.Equal(14129.499999999998m, positive.NetPay); // Formatting/aggregation never mutates the saved source.
    }

    [Fact]
    public void OverlapAmountsRemainSourceRowSumsAndAreFlaggedRatherThanSilentlyDeduplicated()
    {
        var first = Input(); var second = Input(B);
        var result = ExcelPayslipOverviewService.Build([first, second], Request(first, second));
        Assert.Equal(2, result.Totals.OverlapRows); Assert.Equal(200, result.Totals.SourceNetPay);
        Assert.Contains("source amounts", result.Totals.MonetaryRoundingBasis);
    }

    [Fact]
    public void SnapshotBindsFinancialValuesEvenWhenDisplayRoundingAndIdentityDoNotChange()
    {
        var input = Input(); var request = Request(input);
        input.Batch.Rows[0].NetPay = 100.001m;
        var before = ExcelPayslipOverviewService.Build([input], request);
        input.Batch.Rows[0].NetPay = 100.002m;
        var after = ExcelPayslipOverviewService.Build([input], request);
        Assert.Equal(before.Totals.SourceNetPay, after.Totals.SourceNetPay);
        Assert.NotEqual(before.Snapshot, after.Snapshot);
        input.Batch.Rows[0].DeclaredDeductions = 0.001m;
        Assert.NotEqual(after.Snapshot, ExcelPayslipOverviewService.Build([input], request).Snapshot);
    }
}
