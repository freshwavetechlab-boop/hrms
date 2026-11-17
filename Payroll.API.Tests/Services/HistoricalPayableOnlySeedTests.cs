using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

// Uses real v2 bound-input construction, not fabricated Applied receipts.
// These are pure seam tests; no native save/end-to-end result is claimed.
public sealed partial class ReportedPeriodInputV2Tests
{
    private static ExcelPayrollSetupRepository.HistoricalPayableSeedGroup SeedGroup() =>
        new(97962, 7, 97962, 26, 25, "Monthly", "existing-policy", "Sunday off");
    private static ExcelPayrollSetupRepository.HistoricalPayableSeedAbsence EmptySeedState() =>
        new(0, 0, 0, 0, new string('a', 64), new string('b', 64));
    private static HistoricalPeriodSalaryRequest SeedRequest()
    {
        var f = Fixture.Create();
        return new()
        {
            PayableOnlySeedReview = new() { Reviewed = true, Reason = "Reviewed salary period quantities; actual presence not supplied." },
            Setup = new()
            {
                ReportedPeriodInputs = f.Review, ReviewedFixedInputs = true, ReviewedPolicyScope = true,
                ImportAttendance = false, ImportPayableOnlyAttendance = false,
                AttendanceReview = new() { Mode = "SourceSeed", Reviewed = true, PresentMeaning = "NotMapped", PayableMeaning = "AlreadyPayable" }
            }
        };
    }
    private static SourcePayableAttendanceEvidence ClosedSeedEvidence(ReportedPeriodBoundRowV2 b) =>
        new(3, b.ClientId, 97962, b.PayPeriod, b.BatchId, b.RowId, new string('b', 32), b.SourceHash,
            b.PayrollDivisor, b.PayableDays, Mode: SourcePayableAttendance.ClosedReportedMode)
        { Origin = SourcePayableAttendance.ClosedReportedOrigin, ReportedBoundaryHash = b.ProofHash };
    private static ExcelPayrollSetupRepository.HistoricalPayableSeedMonthly SavedSeed(ReportedPeriodBoundRowV2 b) =>
        new(121, b.ClientId, 97962, b.PayPeriod, b.PayrollDivisor, null, b.PayableDays,
            b.PayrollDivisor - b.PayableDays, SourcePayableAttendance.SourceType, SourcePayableAttendance.Encode(ClosedSeedEvidence(b)));

    [Theory]
    [InlineData(0, 0)] [InlineData(5, 25)] [InlineData(4.5, 24)]
    public void ClosedSeedProposalIsExplicitlyPendingAndSavedMonthlyKeepsPresenceUnknown(decimal oldDays, decimal newDays)
    {
        var bound = Fixture.Create(oldDays, newDays).Bind();
        var plan = ExcelPayrollSetupRepository.CreateHistoricalPayableSeedPlan("12", bound, SeedGroup(), EmptySeedState(), new string('c', 64), true, true);
        Assert.Null(plan.PendingNativeInput.PresentDays);
        Assert.Equal(oldDays + newDays, plan.PendingNativeInput.PayableDays);
        Assert.Equal(ExcelPayrollSetupRepository.HistoricalPayableSeedPendingKind, plan.PendingNativeInput.ProofKind);
        var actual = ExcelPayrollSetupRepository.AuthenticClosedReportedSeedNative(SavedSeed(bound), SeedGroup(), bound, new string('c', 64), new string('b', 32));
        Assert.Null(actual.PresentDays);
        Assert.Equal(ExcelPayrollSetupRepository.HistoricalPayableSeedSavedKind, actual.ProofKind);
        Assert.NotEqual(plan.PendingNativeInput.SnapshotHash, actual.SnapshotHash);
        Assert.Equal("2026-09-26", actual.CycleStart); Assert.Equal("2026-10-25", actual.CycleEnd);
    }

    [Theory]
    [InlineData("monthly")] [InlineData("dated")] [InlineData("payroll")] [InlineData("location")]
    public void ClosedSeedNeverOverwritesExistingNativeOrPayrollCoverage(string field)
    {
        var state = EmptySeedState();
        state = field switch { "monthly" => state with { MonthlyRows = 1 }, "dated" => state with { DatedRows = 1 },
            "payroll" => state with { CoveredRunEmployees = 1 }, _ => state with { LocationParts = 1 } };
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.CreateHistoricalPayableSeedPlan("12", Fixture.Create().Bind(), SeedGroup(), state, new string('c', 64), true, true));
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public void ClosedSeedStillRequiresRolloutAndNullableSchema(bool rollout, bool schema) =>
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.CreateHistoricalPayableSeedPlan("12", Fixture.Create().Bind(), SeedGroup(), EmptySeedState(), new string('c', 64), rollout, schema));

    [Theory]
    [InlineData("mode")] [InlineData("client")] [InlineData("id")] [InlineData("cycle")] [InlineData("ownership")]
    public void ExistingMonthlyGroupAndVerifiedIdentityCannotBeInvented(string field)
    {
        var group = SeedGroup(); var ownership = new string('c', 64);
        switch (field) { case "mode": group = group with { Mode = "Legacy" }; break; case "client": group = group with { ClientId = 8 }; break;
            case "id": group = group with { Id = 0 }; break; case "cycle": group = group with { StartDay = 0 }; break; default: ownership = "unverified"; break; }
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.CreateHistoricalPayableSeedPlan("12", Fixture.Create().Bind(), group, EmptySeedState(), ownership, true, true));
    }

    [Theory]
    [InlineData("review")] [InlineData("version")] [InlineData("basis")] [InlineData("reason")]
    [InlineData("v1")] [InlineData("presence")] [InlineData("import")] [InlineData("mode")]
    public void SeedReviewCannotBeUsedAsAGeneralNullableWaiver(string field)
    {
        var request = SeedRequest();
        ExcelPayrollSetupRepository.ValidateHistoricalPayableOnlySeedReview(request);
        switch (field)
        {
            case "review": request.PayableOnlySeedReview!.Reviewed = false; break;
            case "version": request.PayableOnlySeedReview!.Version = 2; break;
            case "basis": request.PayableOnlySeedReview!.QuantityBasis = "SavedOutputs"; break;
            case "reason": request.PayableOnlySeedReview!.Reason = ""; break;
            case "v1": request.Setup.ReportedPeriodInputs = null; break;
            case "presence": request.Setup.PresentDaysColumnIndex = 15; break;
            case "import": request.Setup.ImportAttendance = true; break;
            default: request.Setup.AttendanceReview!.Mode = "VerifyExistingNative"; break;
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateHistoricalPayableOnlySeedReview(request));
    }

    [Theory]
    [InlineData("present")] [InlineData("payable")] [InlineData("divisor")] [InlineData("lop")]
    [InlineData("sourceType")] [InlineData("employee")] [InlineData("month")] [InlineData("request")]
    public void FactualSavedSnapshotRejectsChangedMonthlyValues(string field)
    {
        var b = Fixture.Create().Bind(); var monthly = SavedSeed(b); var requestId = new string('b', 32);
        switch (field)
        {
            case "present": monthly = monthly with { PresentDays = 0 }; break;
            case "payable": monthly = monthly with { PayableDays = 28 }; break;
            case "divisor": monthly = monthly with { WorkingDays = 31 }; break;
            case "lop": monthly = monthly with { LopDays = 0 }; break;
            case "sourceType": monthly = monthly with { SourceType = "Monthly" }; break;
            case "employee": monthly = monthly with { EmployeeId = 97963 }; break;
            case "month": monthly = monthly with { Month = "2026-09" }; break;
            default: requestId = new string('d', 32); break;
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.AuthenticClosedReportedSeedNative(monthly, SeedGroup(), b, new string('c', 64), requestId));
    }

    [Theory]
    [InlineData("boundary")] [InlineData("source")] [InlineData("row")] [InlineData("batch")]
    public void ClosedRemarksAreBoundToTheExactRealReportedBoundary(string field)
    {
        var b = Fixture.Create().Bind(); var evidence = ClosedSeedEvidence(b);
        evidence = field switch { "boundary" => evidence with { ReportedBoundaryHash = new string('d', 64) },
            "source" => evidence with { SourceHash = new string('d', 64) }, "row" => evidence with { RowId = "other" },
            _ => evidence with { BatchId = new string('d', 32) } };
        var row = SavedSeed(b) with { Remarks = SourcePayableAttendance.Encode(evidence) };
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.AuthenticClosedReportedSeedNative(row, SeedGroup(), b, new string('c', 64), new string('b', 32)));
    }

    [Fact]
    public void ChangedActualMonthlyIdGroupOrOwnershipChangesTheProtectedSnapshotHash()
    {
        var b = Fixture.Create().Bind(); var m = SavedSeed(b); var group = SeedGroup(); var identity = new string('c', 64); var request = new string('b', 32);
        string Hash(ExcelPayrollSetupRepository.HistoricalPayableSeedMonthly row, ExcelPayrollSetupRepository.HistoricalPayableSeedGroup g, string key) =>
            ExcelPayrollSetupRepository.AuthenticClosedReportedSeedNative(row, g, b, key, request).SnapshotHash;
        var original = Hash(m, group, identity);
        Assert.NotEqual(original, Hash(m with { Id = 122 }, group, identity));
        Assert.NotEqual(original, Hash(m, group with { WorkWeek = "Existing changed policy" }, identity));
        Assert.NotEqual(original, Hash(m, group, new string('d', 64)));
    }

    [Fact]
    public void ClosedOriginV3HasDistinctVersionModeAndBoundaryWithinExistingStorageCap()
    {
        var b = Fixture.Create().Bind(); var e = ClosedSeedEvidence(b); var text = SourcePayableAttendance.Encode(e);
        Assert.InRange(text.Length, 1, 600);
        var read = SourcePayableAttendance.Read(text, b.ClientId, 97962, b.PayPeriod, b.PayrollDivisor, b.PayableDays);
        Assert.Equal(SourcePayableAttendance.ClosedReportedOrigin, read.Origin); Assert.Equal(b.ProofHash, read.ReportedBoundaryHash);
        Assert.Null(read.ActualPresentDays);
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(e with { Version = 1 }));
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(e with { Origin = null }));
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(e with { Mode = "SourceSeed" }));
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(e with { ReviewedExtraDutyUnits = true }));
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(e with { ReportedBoundaryHash = null }));
    }

    [Fact]
    public void V1V2AttendanceBytesStayExactAndRejectClosedOriginFields()
    {
        var e = new SourcePayableAttendanceEvidence(1, 11, 7, "2026-09", new string('a', 32), "synthetic-row", new string('b', 32), new string('c', 64), 31, 19);
        var expected = "{\"version\":1,\"clientId\":11,\"employeeId\":7,\"payPeriod\":\"2026-09\",\"batchId\":\"" + new string('a',32) + "\",\"rowId\":\"synthetic-row\",\"importId\":\"" + new string('b',32) + "\",\"sourceHash\":\"" + new string('c',64) + "\",\"payrollDays\":31,\"payableDays\":19,\"actualPresentDays\":null,\"presentMeaning\":\"NotMapped\",\"payableMeaning\":\"AlreadyPayable\",\"mode\":\"SourceSeed\"}";
        Assert.Equal(expected, SourcePayableAttendance.Encode(e));
        var v2 = e with { Version = 2, ReviewedExtraDutyUnits = true };
        Assert.Equal(expected.Replace("\"version\":1", "\"version\":2")[..^1] + ",\"reviewedExtraDutyUnits\":true}", SourcePayableAttendance.Encode(v2));
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(e with { Origin = SourcePayableAttendance.ClosedReportedOrigin }));
        Assert.Throws<InvalidOperationException>(() => SourcePayableAttendance.Encode(v2 with { ReportedBoundaryHash = new string('c', 64) }));
    }

    [Fact]
    public void AbsentSeedReviewPreservesTheLegacyRequestWireContract()
    {
        var request = new HistoricalPeriodSalaryRequest();
        ExcelPayrollSetupRepository.ValidateHistoricalPayableOnlySeedReview(request);
        var json = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        Assert.False(json.ContainsKey("payableOnlySeedReview"));
        request.PayableOnlySeedReview = new() { Reviewed = true, Reason = "Explicit opt-in for a closed reported-period source." };
        Assert.NotNull(JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!["payableOnlySeedReview"]);
    }

    [Fact]
    public void SeedBindingUsesTheSameFullProfileIdentityAsV2IncludingCapturedGovernmentCells()
    {
        var f = Fixture.Create();
        f.Source.Columns[2].SourceHeader = "UAN"; f.Source.Columns[2].Label = "UAN";
        f.Source.Rows.Single(r => r.SourceRow == 8).Cells[2] = new() { DataType = "Text", Value = "123456789012" };
        f.RefreshHashes();
        var b = f.Bind() with { SourceIdentityHash = ExcelPayslipMasterSynchronization.ProfileIdentityHash(f.Batch, f.Row, f.Source) };
        b = b with { ProofHash = ReportedPeriodInputProof.BoundHash(b) };
        ExcelPayrollSetupRepository.ValidateHistoricalSeedSourceBinding(b, f.Batch, f.Source, f.Row, b.RawBatchHash, b.RawSourceHash);
        Assert.NotEqual(b.SourceIdentityHash, ExcelPayslipMasterSynchronization.IdentityHash(f.Row));
        var old = b with { SourceIdentityHash = ExcelPayslipMasterSynchronization.IdentityHash(f.Row) };
        old = old with { ProofHash = ReportedPeriodInputProof.BoundHash(old) };
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateHistoricalSeedSourceBinding(old, f.Batch, f.Source, f.Row, old.RawBatchHash, old.RawSourceHash));
        f.Source.Rows.Single(r => r.SourceRow == 8).Cells[2]!.Value = "123456789013";
        Assert.NotEqual(b.SourceIdentityHash, ExcelPayslipMasterSynchronization.ProfileIdentityHash(f.Batch, f.Row, f.Source));
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateHistoricalSeedSourceBinding(b, f.Batch, f.Source, f.Row, b.RawBatchHash, b.RawSourceHash));
    }

    [Theory]
    [InlineData("effective_from")] [InlineData("effective_to")] [InlineData("valid_from")]
    [InlineData("start_date")] [InlineData("end_date")] [InlineData("status")] [InlineData("is_active")]
    public void FutureDatedOrStatusScopedMembershipsNeverBecomeAllTimeLinks(string newColumn)
    {
        Assert.True(ExcelPayrollSetupRepository.HistoricalSeedMembershipSchemaSupported(["attendance_group_id", "employee_id"]));
        Assert.True(ExcelPayrollSetupRepository.HistoricalSeedMembershipSchemaSupported(["id", "attendance_group_id", "employee_id", "created_at", "updated_at_utc"]));
        Assert.False(ExcelPayrollSetupRepository.HistoricalSeedMembershipSchemaSupported(["attendance_group_id", "employee_id", newColumn]));
        Assert.False(ExcelPayrollSetupRepository.HistoricalSeedMembershipSchemaSupported(["employee_id"]));
    }
}
