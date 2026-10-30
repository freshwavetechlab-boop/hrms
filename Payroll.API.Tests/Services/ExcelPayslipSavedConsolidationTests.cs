using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipSavedConsolidationTests
{
    private const string CanonicalId = "cccccccccccccccccccccccccccccccc";
    private const string OlderId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private sealed record Fixture(ExcelPayslipBatch Canonical, ExcelPayslipCalculationSource CanonicalSource,
        ExcelPayslipBatch Older, ExcelPayslipCalculationSource OlderSource);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static Fixture Example(int count = 2)
    {
        var canonical = new ExcelPayslipBatch
        {
            Id = CanonicalId, ClientId = 42, Month = "2026-09", SourceFileName = "Synthetic source.xlsx",
            SheetName = "Salary", HeaderRow = 2, CreatedAtUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            Rows = Enumerable.Range(0, count).Select(index => new ExcelPayslipRow
            {
                Id = Guid.NewGuid().ToString("N"), SourceRow = index + 3, EmployeeCode = "SYN-" + index.ToString("D4"),
                EmployeeName = "Synthetic Person " + index, Email = "synthetic" + index + "@example.invalid",
                Information = [new() { Label = "Location", Value = "Synthetic location" }],
                Earnings = [new() { Label = "Basic", Amount = 10000m }, new() { Label = "HRA", Amount = 5000m }],
                Deductions = [new() { Label = "Employee PF", Amount = 1200m }],
                EmployerContributions = [new() { Label = "Employer PF", Amount = 1300m }],
                NetPay = 13800m, DeclaredGross = 15000m, DeclaredDeductions = 1200m
            }).ToList()
        };
        var source = new ExcelPayslipCalculationSource
        {
            SheetName = "Salary", HeaderRow = 2, ColumnCount = 5, MonthDaysCell = "$L$4", AttendanceColumnIndex = 4,
            Columns = [new() { ColumnIndex = 0, SourceHeader = "Code", Kind = "employeeCode" },
                new() { ColumnIndex = 1, SourceHeader = "Basic", Kind = "earning", ComponentId = "basic" },
                new() { ColumnIndex = 2, SourceHeader = "Employee PF", Kind = "deduction", ComponentId = "pf" },
                new() { ColumnIndex = 3, SourceHeader = "Employer PF", Kind = "employer", ComponentId = "pf-er" },
                new() { ColumnIndex = 4, SourceHeader = "Payable units", Kind = "information" }],
            Rows = Enumerable.Range(0, count).Select(index => new ExcelPayslipCalculationSourceRow
            {
                SourceRow = index + 3,
                Cells = [new() { Value = "SYN-" + index.ToString("D4") }, new() { Value = "10000", FormulaText = "10000*E" + (index + 3) + "/$L$4" },
                    new() { Value = "1200", FormulaText = "B" + (index + 3) + "*12%" },
                    new() { Value = "1300", FormulaText = "B" + (index + 3) + "*13%" },
                    new() { Value = "30", FormulaText = "IFERROR(VLOOKUP(A" + (index + 3) + ",'[Attendance.xlsx]Sep'!$A$2:$E$100,5,FALSE),0)" }]
            }).ToList()
        };
        var old = Copy(canonical); old.Id = OlderId; old.CreatedAtUtc = canonical.CreatedAtUtc.AddDays(-1);
        foreach (var row in old.Rows) row.Id = Guid.NewGuid().ToString("N");
        return new(canonical, source, old, Copy(source));
    }

    private static ExcelPayslipConsolidationPreview Compare(Fixture fixture) =>
        ExcelPayslipSavedConsolidation.Compare(fixture.Canonical, fixture.CanonicalSource, [(fixture.Older, fixture.OlderSource)]);

    [Fact]
    public void TwoIdentical830RowVersionsRemain830RowsWithNoSnapshotOrFormulaMutation()
    {
        var fixture = Example(830);
        var before = new[] { Snapshot(fixture.Canonical), Snapshot(fixture.Older), Snapshot(fixture.CanonicalSource), Snapshot(fixture.OlderSource) };
        var result = Compare(fixture);
        Assert.True(result.CanConsolidate, string.Join(" | ", result.Blockers));
        Assert.Equal(830, result.CanonicalRows); Assert.Equal(830, result.MatchedRows);
        var source = Assert.Single(result.Sources);
        Assert.Equal(830, source.Rows); Assert.Equal(830, source.MatchedRows);
        Assert.True(source.FinancialMatch); Assert.True(source.IdentityMatch); Assert.True(source.FormulaSourcePresent);
        Assert.True(source.FormulaSourceUnchanged);
        Assert.Equal(before, new[] { Snapshot(fixture.Canonical), Snapshot(fixture.Older), Snapshot(fixture.CanonicalSource), Snapshot(fixture.OlderSource) });
    }

    [Fact]
    public void FiveEarlierRowsLinkInsideTheFullBatchWithoutAddingFiveMorePeopleOrChangingSnapshots()
    {
        var fixture=Example(105);
        fixture.Older.Rows=new[]{0,7,18,44,104}.Select(index=>fixture.Older.Rows[index]).ToList();
        var before=Snapshot(fixture);
        var preview=Compare(fixture);
        Assert.True(preview.CanConsolidate,string.Join(" | ",preview.Blockers));
        Assert.Equal(105,preview.CanonicalRows);Assert.Equal(5,preview.MatchedRows);
        var retained=Assert.Single(preview.Sources);
        Assert.Equal(5,retained.Rows);Assert.Equal(5,retained.MatchedRows);
        Assert.True(retained.IdentityMatch);Assert.True(retained.FinancialMatch);
        Assert.Equal(before,Snapshot(fixture));
    }

    [Theory]
    [InlineData("orphan-row")][InlineData("code")][InlineData("name")]
    [InlineData("amount")][InlineData("employer")][InlineData("blank-full-batch-code")]
    public void SubsetGroupingCannotHideAnyUnmatchedSourceOrUnreviewedCanonicalIdentity(string change)
    {
        var fixture=Example(10);fixture.Older.Rows=fixture.Older.Rows.Take(5).ToList();
        switch(change)
        {
            case "orphan-row":fixture.Older.Rows[0].SourceRow=99;break;
            case "code":fixture.Older.Rows[0].EmployeeCode="other";break;
            case "name":fixture.Older.Rows[0].EmployeeName="other";break;
            case "amount":fixture.Older.Rows[0].NetPay+=.01m;break;
            case "employer":fixture.Older.Rows[0].EmployerContributions[0].Amount+=.01m;break;
            case "blank-full-batch-code":fixture.Canonical.Rows[^1].EmployeeCode="";break;
        }
        Assert.False(Compare(fixture).CanConsolidate);
    }

    [Fact]
    public void SubsetDeliveryKeepsItsOriginalReceiptAndNeverMarksNewRowsAsPreviouslySent()
    {
        var fixture=Example(10);fixture.Older.Rows=fixture.Older.Rows.Take(5).ToList();
        var status=fixture.Older.Rows.Select(row=>new ExcelPayslipRowDeliveryStatus{RowId=row.Id,
            RecipientDeliveries=[new(){Mode="Individual",Status="Sent",SentAtUtc=DateTime.UnixEpoch}]}).ToList();
        var before=Snapshot(new{fixture,status});
        var history=ExcelPayslipSavedConsolidation.BindHistoricalDeliveries(fixture.Canonical,fixture.Older,status);
        Assert.Equal(5,history.Count);
        Assert.All(history,row=>Assert.Contains(row.RowId,fixture.Canonical.Rows.Take(5).Select(r=>r.Id)));
        Assert.All(fixture.Canonical.Rows.Skip(5),row=>Assert.DoesNotContain(history,h=>h.RowId==row.Id));
        Assert.Equal(before,Snapshot(new{fixture,status}));
    }

    [Fact]
    public void VersionTwoBindingsAllowCompleteOldSubsetWhileLegacyVersionOneStillRequiresFullCoverage()
    {
        var rows=Enumerable.Range(1,5).Select(i=>(i,"canonical-"+i,"source-"+i)).ToArray();
        Assert.True(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(2,105,rows));
        Assert.True(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(1,5,rows));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(1,105,rows));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(2,4,rows));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(2,105,[]));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(3,105,rows));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(2,105,[rows[0],rows[0]]));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(2,105,[(1,"","source")]));
        Assert.False(ExcelPayslipSavedConsolidation.SourceRowBindingsValid(2,105,[(1,"canonical","source"),(2,"canonical","other")]));
    }

    [Theory]
    [InlineData("other-client")][InlineData("other-month")][InlineData("same-id")]
    [InlineData("missing-row")][InlineData("additional-row")][InlineData("duplicate-source-row")]
    [InlineData("missing-code")][InlineData("different-code")][InlineData("different-name")]
    [InlineData("duplicate-row-id")][InlineData("missing-row-id")]
    public void UnsafeIdentityOrScopeNeverFallsBackToNameOnlyMatching(string problem)
    {
        var fixture = Example();
        switch (problem)
        {
            case "other-client": fixture.Older.ClientId++; break;
            case "other-month": fixture.Older.Month = "2026-08"; break;
            case "same-id": fixture.Older.Id = fixture.Canonical.Id; break;
            case "missing-row": fixture.Canonical.Rows.RemoveAt(1); break;
            case "additional-row": var extra = Copy(fixture.Older.Rows[0]); extra.SourceRow = 99; extra.Id = Guid.NewGuid().ToString("N"); extra.EmployeeCode = "SYN-EXTRA"; fixture.Older.Rows.Add(extra); break;
            case "duplicate-source-row": fixture.Older.Rows[1].SourceRow = fixture.Older.Rows[0].SourceRow; break;
            case "missing-code": fixture.Older.Rows[0].EmployeeCode = " "; break;
            case "different-code": fixture.Older.Rows[0].EmployeeCode = "DIFFERENT"; break;
            case "different-name": fixture.Older.Rows[0].EmployeeName = "Another synthetic person"; break;
            case "duplicate-row-id": fixture.Older.Rows[1].Id = fixture.Older.Rows[0].Id; break;
            case "missing-row-id": fixture.Older.Rows[0].Id = ""; break;
        }
        Assert.False(Compare(fixture).CanConsolidate);
    }

    [Fact]
    public void EmptyOrRepeatedVersionSelectionIsNotASecondEmployeeSet()
    {
        var fixture = Example();
        Assert.False(ExcelPayslipSavedConsolidation.Compare(fixture.Canonical, fixture.CanonicalSource, []).CanConsolidate);
        Assert.False(ExcelPayslipSavedConsolidation.Compare(fixture.Canonical, fixture.CanonicalSource,
            [(fixture.Older, fixture.OlderSource), (fixture.Older, fixture.OlderSource)]).CanConsolidate);
    }

    [Fact]
    public void NameWhitespaceAndCaseDoNotCreateAnArtificialDifferenceWhenSourceRowAndCodeAreExact()
    {
        var fixture = Example(); fixture.Older.Rows[0].EmployeeName = "  SYNTHETIC   PERSON 0 ";
        Assert.True(Compare(fixture).CanConsolidate);
        fixture.Older.Rows[0].SourceRow = 100;
        Assert.False(Compare(fixture).CanConsolidate);
    }

    [Theory]
    [InlineData("earning")][InlineData("deduction")][InlineData("employer")][InlineData("net")]
    [InlineData("declared-gross")][InlineData("declared-deductions")][InlineData("category")]
    [InlineData("missing-zero")][InlineData("label")][InlineData("duplicate-label")]
    public void EveryFinancialCategoryAndComponentRemainsPartOfExactVersionEligibility(string problem)
    {
        var fixture = Example(); var row = fixture.Older.Rows[0];
        switch (problem)
        {
            case "earning": row.Earnings[0].Amount += 1; break;
            case "deduction": row.Deductions[0].Amount += 1; break;
            case "employer": row.EmployerContributions[0].Amount += 1; break;
            case "net": row.NetPay += 1; break;
            case "declared-gross": row.DeclaredGross += 1; break;
            case "declared-deductions": row.DeclaredDeductions += 1; break;
            case "category": row.Deductions.Add(row.EmployerContributions[0]); row.EmployerContributions.Clear(); break;
            case "missing-zero": row.Earnings.Add(new() { Label = "Explicit zero allowance", Amount = 0 }); break;
            case "label": row.EmployerContributions[0].Label = "Different employer contribution"; break;
            case "duplicate-label": row.Earnings[1].Label = row.Earnings[0].Label; break;
        }
        Assert.False(Compare(fixture).CanConsolidate);
    }

    [Theory]
    [InlineData("0.01")][InlineData("0.0001")]
    public void ADisplayRoundingAllowanceDoesNotMakeDifferentSavedSnapshotsExactDuplicates(string difference)
    {
        var fixture = Example(); fixture.Older.Rows[0].NetPay += decimal.Parse(difference, System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(Compare(fixture).CanConsolidate);
    }

    [Fact]
    public void CanonicalFormulaSourceIsRequiredAndOlderFormulaDifferencesAreDisclosedWithoutReplacement()
    {
        var fixture = Example();
        Assert.False(ExcelPayslipSavedConsolidation.Compare(fixture.Canonical, null, [(fixture.Older, fixture.OlderSource)]).CanConsolidate);
        var before = Snapshot(fixture.CanonicalSource);
        fixture.OlderSource.Rows[0].Cells[3]!.FormulaText = "MIN(B3,15000)*13%";
        var result = Compare(fixture);
        Assert.True(result.CanConsolidate, string.Join(" | ", result.Blockers));
        Assert.False(Assert.Single(result.Sources).FormulaSourceUnchanged);
        Assert.Equal(before, Snapshot(fixture.CanonicalSource));
        Assert.Equal("B3*13%", fixture.CanonicalSource.Rows[0].Cells[3]!.FormulaText);
    }

    [Fact]
    public void SnapshotBindingDetectsFormulaCachedValueAttendanceAndIdentityChanges()
    {
        var fixture = Example(); var baseline = ExcelPayslipSavedConsolidation.SnapshotHash(fixture.Canonical, fixture.CanonicalSource);
        var formulaChanged = Copy(fixture.CanonicalSource); formulaChanged.Rows[0].Cells[1]!.FormulaText = "9999*E3/$L$4";
        Assert.NotEqual(baseline, ExcelPayslipSavedConsolidation.SnapshotHash(fixture.Canonical, formulaChanged));
        var cachedChanged = Copy(fixture.CanonicalSource); cachedChanged.Rows[0].Cells[1]!.Value = "9999";
        Assert.NotEqual(baseline, ExcelPayslipSavedConsolidation.SnapshotHash(fixture.Canonical, cachedChanged));
        var attendanceChanged = Copy(fixture.CanonicalSource); attendanceChanged.MonthDaysCell = "$L$5";
        Assert.NotEqual(baseline, ExcelPayslipSavedConsolidation.SnapshotHash(fixture.Canonical, attendanceChanged));
        var identityChanged = Copy(fixture.Canonical); identityChanged.Rows[0].EmployeeCode = "SYN-OTHER";
        Assert.NotEqual(baseline, ExcelPayslipSavedConsolidation.SnapshotHash(identityChanged, fixture.CanonicalSource));
    }

    [Fact]
    public void TheSameVersionRulesWorkInTheNextMonthWithoutSeptemberHardcoding()
    {
        var fixture = Example(); fixture.Canonical.Month = fixture.Older.Month = "2026-10";
        fixture.CanonicalSource.MonthDaysCell = fixture.OlderSource.MonthDaysCell = "$M$4";
        Assert.True(Compare(fixture).CanConsolidate);
    }

    [Theory]
    [InlineData("canonical-email")][InlineData("canonical-row-id")][InlineData("canonical-cached-value")][InlineData("canonical-formula")]
    public void PreviewTokenAlwaysBindsTheCanonicalSnapshotEvenWhenItsOlderSourceAlreadyDiffers(string change)
    {
        var fixture = Example(); fixture.OlderSource.Rows[0].Cells[3]!.FormulaText = "MIN(B3,15000)*13%";
        var before = Compare(fixture); Assert.True(before.CanConsolidate);
        switch (change)
        {
            case "canonical-email": fixture.Canonical.Rows[0].Email = "changed@example.invalid"; break;
            case "canonical-row-id": fixture.Canonical.Rows[0].Id = Guid.NewGuid().ToString("N"); break;
            case "canonical-cached-value": fixture.CanonicalSource.Rows[0].Cells[1]!.Value = "9999"; break;
            case "canonical-formula": fixture.CanonicalSource.Rows[0].Cells[3]!.FormulaText = "B3*0.13"; break;
        }
        var after = Compare(fixture);
        Assert.True(after.CanConsolidate); // These edits do not alter the displayed financial row equality.
        Assert.NotEqual(before.Token, after.Token); // The operation must nevertheless require a fresh exact-source preview.
    }

    [Fact]
    public void PreviewTokenIgnoresItsOwnValueAndOrderOfTheSameExplicitSourceSelection()
    {
        var fixture = Example(); var another = Copy(fixture.Older); another.Id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var first = ExcelPayslipSavedConsolidation.Compare(fixture.Canonical, fixture.CanonicalSource,
            [(fixture.Older, fixture.OlderSource), (another, Copy(fixture.OlderSource))]);
        var second = ExcelPayslipSavedConsolidation.Compare(fixture.Canonical, fixture.CanonicalSource,
            [(another, Copy(fixture.OlderSource)), (fixture.Older, fixture.OlderSource)]);
        Assert.True(first.CanConsolidate); Assert.True(second.CanConsolidate);
        Assert.Equal(first.Token, second.Token);
        first.Token = "some previous rendering of the token";
        Assert.Equal(second.Token, ExcelPayslipSavedConsolidation.PreviewToken(first));
        Assert.Equal(2, first.CanonicalRows); Assert.Equal(2, first.MatchedRows);
    }

    [Fact]
    public void HistoricalDeliveryRetainsItsOriginalVersionRecipientOutcomeAndTimestampWithoutSendingAgain()
    {
        var fixture = Example(); var sentAt = new DateTime(2026, 10, 3, 4, 5, 6, DateTimeKind.Utc);
        var status = new ExcelPayslipRowDeliveryStatus
        {
            RowId = fixture.Older.Rows[0].Id, Status = "Sent", CanSend = false,
            RecipientDeliveries = [new() { Mode = "Individual", Status = "Delivered", Email = "prior@example.invalid", SentAtUtc = sentAt },
                new() { Mode = "Combined", Status = "Sent", Email = "coordinator@example.invalid", SentAtUtc = sentAt.AddSeconds(1) }]
        };
        var canonicalBefore = Snapshot(fixture.Canonical); var sourceBefore = Snapshot(fixture.Older); var deliveryBefore = Snapshot(status);
        var history = ExcelPayslipSavedConsolidation.BindHistoricalDeliveries(fixture.Canonical, fixture.Older, [status]);
        Assert.Equal(2, history.Count);
        Assert.All(history, item =>
        {
            Assert.True(item.FromConsolidatedVersion); Assert.Equal(fixture.Older.Id, item.SourceBatchId);
            Assert.Equal(fixture.Older.Rows[0].Id, item.SourceRowId); Assert.Equal(fixture.Canonical.Rows[0].Id, item.RowId);
            Assert.Contains("not a send of the canonical version", item.Message);
        });
        Assert.Contains(history, item => item.Status == "Delivered" && item.Mode == "Individual" && item.SentAtUtc == sentAt);
        Assert.Contains(history, item => item.Status == "Sent" && item.Mode == "Combined");
        Assert.Equal(canonicalBefore, Snapshot(fixture.Canonical)); Assert.Equal(sourceBefore, Snapshot(fixture.Older)); Assert.Equal(deliveryBefore, Snapshot(status));
    }

    [Theory]
    [InlineData("wrong-client")][InlineData("wrong-period")][InlineData("wrong-source-id")]
    [InlineData("wrong-code")][InlineData("different-employer-amount")][InlineData("ambiguous-row-id")]
    public void UnboundOrChangedSourceContentCannotAcquirePriorDeliveryEvidence(string change)
    {
        var fixture = Example(); var status = new ExcelPayslipRowDeliveryStatus
        {
            RowId = fixture.Older.Rows[0].Id, RecipientDeliveries = [new() { Mode = "Individual", Status = "Sent" }]
        };
        switch (change)
        {
            case "wrong-client": fixture.Older.ClientId++; break;
            case "wrong-period": fixture.Older.Month = "2026-08"; break;
            case "wrong-source-id": status.RowId = "not-a-source-row"; break;
            case "wrong-code": fixture.Older.Rows[0].EmployeeCode = "SYN-OTHER"; break;
            case "different-employer-amount": fixture.Older.Rows[0].EmployerContributions[0].Amount += .01m; break;
            case "ambiguous-row-id": fixture.Older.Rows[1].Id = fixture.Older.Rows[0].Id; break;
        }
        Assert.Empty(ExcelPayslipSavedConsolidation.BindHistoricalDeliveries(fixture.Canonical, fixture.Older, [status]));
    }

    [Fact]
    public void OverallSentWithoutRecipientEvidenceDoesNotInventAConsolidatedDelivery()
    {
        var fixture = Example(); var status = new ExcelPayslipRowDeliveryStatus { RowId = fixture.Older.Rows[0].Id, Status = "Sent", RecipientDeliveries = [] };
        Assert.Empty(ExcelPayslipSavedConsolidation.BindHistoricalDeliveries(fixture.Canonical, fixture.Older, [status]));
    }

    [Fact]
    public void RawGroupingMetadataDoesNotHideChangedOrUnavailableSnapshotVersions()
    {
        var canonical = new string('c', 64); var source = new string('a', 64);
        var expected = new Dictionary<string, string> { [OlderId] = source };
        Assert.True(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonical, canonical, new Dictionary<string, string?> { [OlderId] = source }, expected));
        Assert.False(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(null, canonical, new Dictionary<string, string?> { [OlderId] = source }, expected));
        Assert.False(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(new string('d', 64), canonical, new Dictionary<string, string?> { [OlderId] = source }, expected));
        Assert.False(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonical, canonical, new Dictionary<string, string?> { [OlderId] = null }, expected));
        Assert.False(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonical, canonical, new Dictionary<string, string?> { [OlderId] = new string('b', 64) }, expected));
        Assert.False(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonical, canonical, new Dictionary<string, string?>(), expected));
        Assert.False(ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonical, canonical, new Dictionary<string, string?> { [OlderId] = source, [CanonicalId] = canonical }, expected));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    public void OnlyAnActiveFreshGroupingHidesItsRetainedSourceInTheDefaultWorkingList(bool active, bool fresh, bool includeHistory, bool hidden) =>
        Assert.Equal(hidden, ExcelPayslipSavedConsolidation.ShouldHideSource(active, fresh, includeHistory));

    [Fact]
    public void RepeatedActionsAreRecognizedOnlyForTheSameActionAndExactRequestPayload()
    {
        var requestHash = new string('a', 64);
        ExcelPayslipSavedConsolidation.ValidateReplay("batch-group-create", requestHash, "batch-group-create", requestHash);
        ExcelPayslipSavedConsolidation.ValidateReplay("batch-group-undo", requestHash, "batch-group-undo", requestHash);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipSavedConsolidation.ValidateReplay("batch-group-create", requestHash, "batch-group-undo", requestHash));
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipSavedConsolidation.ValidateReplay("discard-draft", requestHash, "batch-group-create", requestHash));
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipSavedConsolidation.ValidateReplay("batch-group-create", requestHash, "batch-group-create", new string('b', 64)));
    }

    [Fact]
    public void UndoRestoresOnlyItsExactGroupAndUnrelatedAuditEventsDoNotCancelIt()
    {
        const string group = "cccccccccccccccccccccccccccccccc";
        Assert.True(ExcelPayslipSavedConsolidation.GroupIsActive(group, [("batch-group-create", ""), ("batch-group-undo", OlderId)]));
        Assert.False(ExcelPayslipSavedConsolidation.GroupIsActive(group, [("batch-group-create", ""), ("batch-group-undo", group)]));
        Assert.False(ExcelPayslipSavedConsolidation.GroupIsActive(group, [("batch-group-undo", group), ("batch-group-undo", group)]));
    }

    [Fact]
    public void SourcesCannotOverlapTwoActiveGroupsOrBecomeChainedCanonicals()
    {
        IEnumerable<IEnumerable<string>> groups = [new[] { CanonicalId, OlderId }];
        Assert.True(ExcelPayslipSavedConsolidation.HasOverlap(new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", OlderId }, groups));
        Assert.True(ExcelPayslipSavedConsolidation.HasOverlap(new[] { CanonicalId, "dddddddddddddddddddddddddddddddd" }, groups));
        Assert.True(ExcelPayslipSavedConsolidation.HasOverlap(new[] { CanonicalId.ToUpperInvariant() }, groups));
        Assert.False(ExcelPayslipSavedConsolidation.HasOverlap(new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "dddddddddddddddddddddddddddddddd" }, groups));
    }

    [Fact]
    public void AnActiveGroupingProtectsCanonicalAndRetainedSnapshotsFromBothDeletionModes()
    {
        var preview = new ExcelPayslipDeletePreview { IsLegacy = true, CandidateCount = 1, ConsolidationReferences = 1 };
        preview.Blockers = ExcelPayslipDeletionPolicy.Blockers(preview, 1, false);
        preview.CanDiscardUnusedLegacy = ExcelPayslipDeletionPolicy.AllowsUnusedLegacyDiscard(preview, false);
        Assert.False(preview.CanDelete); Assert.False(preview.CanDiscardUnusedLegacy);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipDeletionPolicy.RequireDeletionIntent(preview, false));
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipDeletionPolicy.RequireDeletionIntent(preview, true));
    }

    [Fact]
    public async Task ConsolidationRoutesRetainExistingPermissionClientAndInactiveUserBoundariesWithoutDatabaseAccess()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ExcelPayslipRepository(builder.Configuration, null!, null!));
        builder.Services.AddSingleton(new ExcelPayslipPdfService());
        builder.Services.AddSingleton(new AttachmentRepository(builder.Configuration, null!, null!, null!, null!, NullLogger<AttachmentRepository>.Instance));
        builder.Services.AddSingleton(new AttachmentStorageService(null!, null!, builder.Configuration, null!));
        var user = new AuthUser { Id = 1, ClientId = 20, IsActive = true, Permissions = [] };
        var canRead = true; var canManage = false;
        await using var app = builder.Build();
        ExcelPayslipEndpoints.Map(app, _ => canRead && user.IsActive, _ => canManage && user.IsActive, (_, clientId) => user.ClientId == clientId, _ => user);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var historyEndpoint = endpoints.Single(item => item.RoutePattern.RawText == "/api/excel-payslips/batches/{id}/consolidation");
        Assert.Equal("GET", historyEndpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single());
        Assert.DoesNotContain(historyEndpoint.Metadata, item => item.GetType().Name is "DeleteBatchAccess" or "OriginalWorkbookAccess");
        var writePatterns = new[] { "/api/excel-payslips/consolidation-preview", "/api/excel-payslips/consolidations", "/api/excel-payslips/consolidations/{groupId}/undo" };
        foreach (var pattern in writePatterns) await Denied(pattern, 20);
        canManage = true;
        foreach (var pattern in writePatterns) await Denied(pattern, 20); // Payslip management alone does not grant consolidation authority.
        user.Permissions = ["settings.manage"];
        foreach (var pattern in writePatterns.Append("/api/excel-payslips/batches/{id}/consolidation")) await Denied(pattern, 21);
        canRead = false;
        foreach (var pattern in writePatterns.Append("/api/excel-payslips/batches/{id}/consolidation")) await Denied(pattern, 20);
        canRead = true; user.IsActive = false;
        foreach (var pattern in writePatterns.Append("/api/excel-payslips/batches/{id}/consolidation")) await Denied(pattern, 20);

        async Task Denied(string pattern, int clientId)
        {
            var endpoint = endpoints.Single(item => item.RoutePattern.RawText == pattern);
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.SetEndpoint(endpoint); context.Request.Method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
            context.Request.Path = pattern.Replace("{id}", CanonicalId).Replace("{groupId}", OlderId);
            context.Request.QueryString = new QueryString("?clientId=" + clientId);
            context.Request.RouteValues["id"] = CanonicalId; context.Request.RouteValues["groupId"] = OlderId;
            if (context.Request.Method == "POST")
            {
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { requestId = Guid.NewGuid().ToString(), token = new string('a', 64), canonicalBatchId = CanonicalId, sourceBatchIds = new[] { OlderId }, acknowledged = true }));
                context.Request.Body = new MemoryStream(body); context.Request.ContentLength = body.Length; context.Request.ContentType = "application/json";
            }
            context.Response.Body = new MemoryStream(); await endpoint.RequestDelegate!(context);
            Assert.Equal(403, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        }
    }
}
