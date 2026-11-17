using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalUnknownPresenceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Case
    {
        internal ExcelPayslipBatch Batch = new() { Id = "97962000000000000000000000000001", ClientId = 97962, Month = "2026-10",
            Rows = [new() { Id = "unknown-source-2", SourceRow = 2, EmployeeCode = "SYN-UNKNOWN", EmployeeName = "Synthetic unknown" }] };
        internal ExcelPayslipCalculationSource Source = new() { CaptureVersion = 0, DependencyReviewStatus = "Legacy" };
        internal HistoricalPeriodSalaryRequest Request = new();
        internal ExcelPayrollSetupRepository.HistoricalUnknownMonthly Monthly = new();
        internal ExcelPayrollSetupRepository.HistoricalUnknownGroup Group = new();
        internal ExcelPayrollSetupRepository.HistoricalUnknownImport Import = new();
        internal NativePayrollAttendanceInput Native = null!;
        internal Case()
        {
            var row = Batch.Rows[0];
            var hash = ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new { Batch, Source }, Json));
            Request = new() { Setup = new() { BatchId = Batch.Id, RowIds = [row.Id], ImportAttendance = false,
                PayrollDaysColumnIndex = 3, PayableDaysColumnIndex = 4, AttendanceCycleStartDay = 1, AttendanceCycleEndDay = 31,
                Components = [new() { Code = "WAGES", Formula = "WAGES_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS" }],
                AttendanceReview = new() { Mode = "VerifyExistingNative", Reviewed = true, PresentMeaning = "NotMapped", PayableMeaning = "AlreadyPayable",
                    SourceMonth = Batch.Month, SourceHash = hash, CycleStart = "2026-10-01", CycleEnd = "2026-10-31" } },
                Assignments = [new(row.Id, 9796201, "9796200")] };
            Request.Setup.AttendanceReview.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(Request.Setup);
            var importRequest = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(JsonSerializer.Serialize(Request.Setup, Json), Json)!;
            importRequest.TransferReviewVersion = 1;
            importRequest.ImportAttendance = true;
            importRequest.ImportPayableOnlyAttendance = true;
            importRequest.AttendanceReview!.Mode = "SourceSeed";
            importRequest.AttendanceReview.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(importRequest);
            var importId = new string('a', 32);
            Monthly = new() { Id = 9796201, ClientId = Batch.ClientId, EmployeeId = 9796201, Month = Batch.Month,
                WorkingDays = 31, PresentDays = null, PayableDays = 27.5m, SourceType = SourcePayableAttendance.SourceType,
                Remarks = SourcePayableAttendance.Encode(new(1, Batch.ClientId, 9796201, Batch.Month, Batch.Id, row.Id, importId, hash, 31, 27.5m)) };
            Group = new() { Id = 97962, ClientId = Batch.ClientId, EmployeeId = 9796201, StartDay = 1, EndDay = 31, Mode = "Monthly", WorkWeek = "Sunday off" };
            var result = new ExcelPayrollSetupResult { ImportId = importId, EmployeeIds = [9796201],
                AttendanceAssignments = [new(9796201, Group.Id, true)],
                MasterLinks = [new(row.Id, 9796201, row.SourceRow, ExcelPayslipMasterSynchronization.IdentityHash(row), new string('b', 64), 31, null, 27.5m, false)],
                TransferRows = [new(row.Id, 9796201, 31, null, 27.5m, 0, [])],
                Transfer = new() { SourceHash = hash, AttendanceProofs = [new(row.Id, row.SourceRow, "SourceSeed", ExcelPayrollSetupRepository.PayableOnlyProofStatus,
                    "Reviewed source, not actual presence", hash, new string('c', 64), 31, null, 27.5m, null, null, null)] } };
            Import = new() { Id = importId, ClientId = Batch.ClientId, BatchId = Batch.Id, Status = "Applied",
                RequestJson = JsonSerializer.Serialize(importRequest, Json), ResultJson = JsonSerializer.Serialize(result, Json) };
            Import.RequestHash = ExcelPayrollSetupRepository.Hash(Import.RequestJson);
            Native = new(9796201, Batch.ClientId, Batch.Month, 31, null, 27.5m, null, null, new string('d', 64),
                "Native monthly/daily totals have no dated cycle snapshot. Complete/review native dated attendance before claiming same-cycle proof, or use reviewed source-seed mode with native verification pending.");
        }
        internal NativePayrollAttendanceInput Authenticate(bool flag = true, bool schema = true, bool dated = false, bool policy = false, bool split = false) =>
            ExcelPayrollSetupRepository.AuthenticateHistoricalUnknownPresence(Request, Batch, Source, Batch.Rows[0], Native, Monthly, Group, Import, flag, schema, dated, policy, split);
    }

    [Fact]
    public void Applied_source_and_exact_native_snapshot_keep_unknown_and_fractional_payable()
    {
        var c = new Case(); var proof = c.Authenticate();
        Assert.Null(proof.PresentDays); Assert.Equal(27.5m, proof.PayableDays); Assert.Equal(31, proof.PayrollDays);
        Assert.Equal("2026-10-01", proof.CycleStart); Assert.Equal("2026-10-31", proof.CycleEnd);
        Assert.Equal("", proof.Error); Assert.Equal(ExcelPayrollSetupRepository.HistoricalUnknownPresenceKind, proof.ProofKind);
        Assert.NotEqual(c.Native.SnapshotHash, proof.SnapshotHash);
        var transport = JsonSerializer.SerializeToElement(proof, Json);
        Assert.Equal(JsonValueKind.Null, transport.GetProperty("presentDays").ValueKind);
    }

    [Theory]
    [InlineData("flag")][InlineData("schema")][InlineData("dated")][InlineData("policy")][InlineData("split")]
    public void Rollout_schema_and_dated_policy_guards_cannot_be_waived(string boundary)
    {
        var c = new Case();
        Assert.Throws<InvalidOperationException>(() => c.Authenticate(boundary != "flag", boundary != "schema", boundary == "dated", boundary == "policy", boundary == "split"));
    }

    [Theory]
    [InlineData("known zero")][InlineData("numeric native")][InlineData("foreign month")][InlineData("foreign client")]
    [InlineData("foreign employee")][InlineData("different days")][InlineData("native error")][InlineData("source type")]
    [InlineData("different cycle")][InlineData("group mode")][InlineData("source changed")][InlineData("source identity changed")]
    [InlineData("import not applied")][InlineData("import hash")][InlineData("present formula")][InlineData("review meaning")]
    public void Changed_or_unreviewed_presence_and_scope_fail_closed(string change)
    {
        var c = new Case();
        switch (change)
        {
            case "known zero": c.Monthly.PresentDays = 0; break;
            case "numeric native": c.Native = c.Native with { PresentDays = 0 }; break;
            case "foreign month": c.Monthly.Month = "2026-09"; break;
            case "foreign client": c.Monthly.ClientId++; break;
            case "foreign employee": c.Group.EmployeeId++; break;
            case "different days": c.Monthly.PayableDays--; break;
            case "native error": c.Native = c.Native with { Error = "Ambiguous active attendance policy" }; break;
            case "source type": c.Monthly.SourceType = "Monthly"; break;
            case "different cycle": c.Group.StartDay = 26; break;
            case "group mode": c.Group.Mode = "Legacy"; break;
            case "source changed": c.Source.SheetName = "Other"; break;
            case "source identity changed": c.Batch.Rows[0].EmployeeName = "Other"; break;
            case "import not applied": c.Import.Status = "Draft"; break;
            case "import hash": c.Import.RequestHash = new string('0', 64); break;
            case "present formula": c.Request.Setup.Components[0].Formula = "PRESENT_DAYS*100"; break;
            case "review meaning": c.Request.Setup.AttendanceReview!.PresentMeaning = "ActualWorked"; break;
        }
        Assert.Throws<InvalidOperationException>(() => c.Authenticate());
    }

    [Fact]
    public void Receipt_links_and_snapshot_hash_are_exact_and_not_just_matching_totals()
    {
        var c = new Case(); var first = c.Authenticate();
        c.Group.WorkWeek = "No fixed weekly off";
        Assert.NotEqual(first.SnapshotHash, c.Authenticate().SnapshotHash);
        var result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(c.Import.ResultJson, Json)!;
        result.MasterLinks.Add(result.MasterLinks[0]);
        c.Import.ResultJson = JsonSerializer.Serialize(result, Json);
        Assert.Throws<InvalidOperationException>(() => c.Authenticate());
    }

    [Fact]
    public async Task Known_presence_including_zero_keeps_existing_proof_bytes_without_database_or_capability_reads()
    {
        var c = new Case();
        var repository = new ExcelPayrollSetupRepository(null!, null!, null);
        foreach (var present in new[] { 0m, 26m, 26.5m })
        {
            var proofs = new Dictionary<int, NativePayrollAttendanceInput>
            {
                [9796201] = c.Native with { PresentDays = present, CycleStart = "2026-10-01", CycleEnd = "2026-10-31", Error = "", ProofKind = null }
            };
            var bytes = JsonSerializer.Serialize(proofs, Json);
            await repository.PopulateHistoricalUnknownPresenceProofsAsync(null!, null!, c.Batch.ClientId, c.Request, c.Batch, c.Source, proofs, false);
            Assert.Equal(bytes, JsonSerializer.Serialize(proofs, Json));
        }
    }
}
