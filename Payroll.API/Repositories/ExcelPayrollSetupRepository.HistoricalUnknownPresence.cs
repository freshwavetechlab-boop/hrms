using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string HistoricalUnknownPresenceKind = "ReviewedPayableOnlyHistoricalSnapshotV1";
    internal const string HistoricalUnknownPresenceMatched = "Reviewed native payable-only inputs matched";
    private const string MissingNativeCycle = "Native monthly/daily totals have no dated cycle snapshot. Complete/review native dated attendance before claiming same-cycle proof, or use reviewed source-seed mode with native verification pending.";

    internal sealed class HistoricalUnknownMonthly
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public int EmployeeId { get; set; }
        public string Month { get; set; } = "";
        public decimal WorkingDays { get; set; }
        public decimal? PresentDays { get; set; }
        public decimal PayableDays { get; set; }
        public string SourceType { get; set; } = "";
        public string Remarks { get; set; } = "";
    }
    internal sealed class HistoricalUnknownGroup
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public int EmployeeId { get; set; }
        public int StartDay { get; set; }
        public int EndDay { get; set; }
        public string Mode { get; set; } = "";
        public string PolicyBatchId { get; set; } = "";
        public string WorkWeek { get; set; } = "";
    }
    internal sealed class HistoricalUnknownImport
    {
        public string Id { get; set; } = "";
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public string Status { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string RequestJson { get; set; } = "";
        public string ResultJson { get; set; } = "";
    }

    // Existing non-null proofs are returned byte-for-byte without extra reads.
    // This composes an already Applied ordinary source import; it never creates
    // attendance, changes salary targets, or treats a missing value as zero.
    internal async Task PopulateHistoricalUnknownPresenceProofsAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, HistoricalPeriodSalaryRequest request, ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source, Dictionary<int, NativePayrollAttendanceInput> native, bool locked)
    {
        var ids = request.Assignments.Select(a => a.EmployeeId)
            .Where(id => native.TryGetValue(id, out var proof) && !proof.PresentDays.HasValue).Distinct().ToArray();
        if (ids.Length == 0) return;
        void BlockAll(string message)
        {
            foreach (var id in ids) native[id] = native[id] with { Error = message };
        }
        if (!PayableOnlyRolloutEnabled(configuration))
        {
            BlockAll("Historical unknown-presence review requires the explicitly enabled compatible-reader payable-only capability; no presence value was inferred.");
            return;
        }
        if (!await HasNullablePresenceSchemaAsync(db, tx))
        {
            BlockAll("Historical unknown-presence review requires both reviewed nullable-presence columns; no schema is changed by this review.");
            return;
        }
        var parameters = new { ClientId = clientId, Month = batch.Month, Ids = ids };
        var monthly = (await db.QueryAsync<HistoricalUnknownMonthly>(@"SELECT id Id,client_id ClientId,employee_id EmployeeId,
attendance_month Month,working_days WorkingDays,present_days PresentDays,payable_days PayableDays,source_type SourceType,COALESCE(remarks,'') Remarks
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids" + (locked ? " FOR UPDATE" : ""), parameters, tx)).ToArray();
        var groups = (await db.QueryAsync<HistoricalUnknownGroup>(@"SELECT g.id Id,g.client_id ClientId,m.employee_id EmployeeId,
g.attendance_cycle_start_day StartDay,g.attendance_cycle_end_day EndDay,g.attendance_mode Mode,COALESCE(g.policy_batch_id,'') PolicyBatchId,COALESCE(g.work_week,'') WorkWeek
FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE g.client_id=@ClientId AND g.is_active=TRUE AND m.employee_id IN @Ids" + (locked ? " FOR UPDATE" : ""), parameters, tx)).ToArray();
        var cycle = PayRunRepository.NativeAttendanceCycle(batch.Month, request.Setup.AttendanceCycleStartDay, request.Setup.AttendanceCycleEndDay);
        var dated = (await db.QueryAsync<int>(@"SELECT DISTINCT employee_id FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End",
            new { ClientId = clientId, Ids = ids, Start = cycle.Start, End = cycle.End }, tx)).ToHashSet();
        var policies = await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId);
        var end = DateTime.ParseExact(cycle.End, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hasDailyPolicy = policies.WeeklyOffRuleVersions.Any(v => v.EffectiveFrom.Date <= end.Date);
        var pfVersions = await PfPolicyRepository.ReadVersionsAsync(db, tx, clientId);
        var evidence = new Dictionary<int, SourcePayableAttendanceEvidence>();
        foreach (var id in ids)
        {
            try
            {
                var rows = monthly.Where(r => r.EmployeeId == id).ToArray();
                if (rows.Length != 1 || rows[0].WorkingDays is < 1 or > 31 || decimal.Truncate(rows[0].WorkingDays) != rows[0].WorkingDays)
                    throw new InvalidOperationException("Exactly one valid saved payable-only monthly row is required.");
                evidence[id] = SourcePayableAttendance.Read(rows[0].Remarks, clientId, id, batch.Month, (int)rows[0].WorkingDays, rows[0].PayableDays);
            }
            catch (InvalidOperationException error) { native[id] = native[id] with { Error = error.Message }; }
        }
        var importIds = evidence.Values.Select(e => e.ImportId).Distinct(StringComparer.Ordinal).ToArray();
        var imports = importIds.Length == 0 ? [] : (await db.QueryAsync<HistoricalUnknownImport>(@"SELECT id Id,client_id ClientId,batch_id BatchId,status Status,
request_hash RequestHash,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND id IN @Ids" + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId, Ids = importIds }, tx)).ToArray();
        foreach (var id in ids.Where(evidence.ContainsKey))
        {
            try
            {
                var assignment = request.Assignments.Single(a => a.EmployeeId == id);
                var row = batch.Rows.Single(r => r.Id == assignment.RowId);
                var ownedGroups = groups.Where(g => g.EmployeeId == id).ToArray();
                var ownedImports = imports.Where(i => i.Id == evidence[id].ImportId).ToArray();
                if (ownedGroups.Length != 1 || ownedImports.Length != 1)
                    throw new InvalidOperationException("One active monthly group and one original Applied source import are required for unknown presence.");
                var splitPf = PfPolicyCalculator.ApplicableVersions(pfVersions, assignment.TemplateId,
                    DateOnly.ParseExact(cycle.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.FromDateTime(end)).Count > 1;
                native[id] = AuthenticateHistoricalUnknownPresence(request, batch, source, row, native[id],
                    monthly.Single(m => m.EmployeeId == id), ownedGroups[0], ownedImports[0], true, true, dated.Contains(id), hasDailyPolicy, splitPf);
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException)
            { native[id] = native[id] with { Error = "Unknown-presence provenance review: " + error.Message }; }
        }
    }

    internal static NativePayrollAttendanceInput AuthenticateHistoricalUnknownPresence(HistoricalPeriodSalaryRequest request,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayslipRow row, NativePayrollAttendanceInput native,
        HistoricalUnknownMonthly monthly, HistoricalUnknownGroup group, HistoricalUnknownImport receipt,
        bool rolloutEnabled, bool schemaReady, bool hasDatedAttendance = false, bool hasDailyPolicy = false, bool hasSplitPf = false)
    {
        var q = request.Setup;
        var sourceHash = Hash(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json));
        if (!rolloutEnabled || !schemaReady || q.ImportAttendance || q.ImportPayableOnlyAttendance || q.OverrideSourcePayableAttendance
            || q.LocationAllocations is not null || q.PresentDaysColumnIndex is not null
            || q.AttendanceReview is not { Mode: "VerifyExistingNative", Reviewed: true, PresentMeaning: "NotMapped", PayableMeaning: "AlreadyPayable" } review
            || review.SourceHash != sourceHash || review.SourceMonth != batch.Month || review.BoundSourceInputs != AttendanceBinding(q)
            || batch.Id != q.BatchId || monthly.Id <= 0 || group.Id <= 0 || monthly.ClientId != batch.ClientId || native.ClientId != batch.ClientId || group.ClientId != batch.ClientId
            || monthly.Month != batch.Month || native.Month != batch.Month || monthly.EmployeeId != native.EmployeeId || group.EmployeeId != native.EmployeeId
            || native.PresentDays.HasValue || monthly.PresentDays.HasValue || monthly.SourceType != SourcePayableAttendance.SourceType
            || monthly.WorkingDays is < 1 or > 31 || decimal.Truncate(monthly.WorkingDays) != monthly.WorkingDays
            || native.PayrollDays != monthly.WorkingDays || native.PayableDays != monthly.PayableDays
            || native.Error.Length != 0 && native.Error != MissingNativeCycle || group.Mode != "Monthly" || group.StartDay is < 1 or > 31 || group.EndDay is < 1 or > 31
            || !request.Assignments.Any(a => a.EmployeeId == native.EmployeeId && a.RowId == row.Id)
            || !batch.Rows.Any(r => r.Id == row.Id && r.SourceRow == row.SourceRow))
            throw new InvalidOperationException("Explicit same-source native payable-only review is required; unknown presence is not a nullable attendance waiver.");
        SourcePayableAttendance.EnsureIndependent(q.Components.SelectMany(c => new[] { c.Formula, c.BaseComponent }.Concat(c.RowFormulas.Values)), hasDailyPolicy, hasSplitPf, q.LocationAllocations is not null);
        if (hasDatedAttendance) throw new InvalidOperationException("Existing dated attendance cannot be ignored by a historical payable-only review.");
        var cycle = PayRunRepository.NativeAttendanceCycle(batch.Month, group.StartDay, group.EndDay);
        if (review.CycleStart != cycle.Start || review.CycleEnd != cycle.End
            || cycle != PayRunRepository.NativeAttendanceCycle(batch.Month, q.AttendanceCycleStartDay, q.AttendanceCycleEndDay))
            throw new InvalidOperationException("The retained monthly group and explicitly reviewed source cycle differ.");
        var original = SourcePayableAttendance.Read(monthly.Remarks, batch.ClientId, native.EmployeeId, batch.Month, (int)monthly.WorkingDays, monthly.PayableDays);
        if (original.BatchId != batch.Id || original.RowId != row.Id || original.SourceHash != sourceHash || original.ReviewedExtraDutyUnits
            || receipt.Id != original.ImportId || receipt.ClientId != batch.ClientId || receipt.BatchId != batch.Id || receipt.Status != "Applied")
            throw new InvalidOperationException("The saved monthly provenance does not belong to this exact original Applied source row.");
        var imported = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(receipt.RequestJson, Json)
            ?? throw new InvalidOperationException("Original payable-only request is missing.");
        var result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(receipt.ResultJson, Json)
            ?? throw new InvalidOperationException("Original payable-only result is missing.");
        if (Hash(JsonSerializer.Serialize(imported, Json)) != receipt.RequestHash || PayableOnlyRequestError(imported) is not null
            || imported.BatchId != batch.Id || !imported.RowIds.Contains(row.Id) || imported.AttendanceReview!.SourceHash != sourceHash
            || imported.AttendanceReview.SourceMonth != batch.Month || imported.AttendanceReview.BoundSourceInputs != AttendanceBinding(imported)
            || imported.AttendanceReview.CycleStart != cycle.Start || imported.AttendanceReview.CycleEnd != cycle.End || result.ImportId != original.ImportId
            || result.EmployeeIds.Count(id => id == native.EmployeeId) != 1
            || result.AttendanceAssignments.Count(a => a.EmployeeId == native.EmployeeId) != 1
            || result.AttendanceAssignments.Count(a => a.EmployeeId == native.EmployeeId && a.GroupId == group.Id) != 1
            || result.MasterLinks.Count(l => l.RowId == row.Id) != 1
            || result.MasterLinks.Count(l => l.RowId == row.Id && l.EmployeeId == native.EmployeeId && l.SourceRow == row.SourceRow
                && l.IdentityHash == ExcelPayslipMasterSynchronization.IdentityHash(row) && l.PayrollDays == monthly.WorkingDays
                && l.PresentDays is null && l.PayableDays == monthly.PayableDays && !l.ReviewedExtraDutyUnits) != 1
            || result.TransferRows.Count(r => r.RowId == row.Id) != 1
            || result.TransferRows.Count(r => r.RowId == row.Id && r.EmployeeId == native.EmployeeId && r.PayrollDays == monthly.WorkingDays
                && r.PresentDays is null && r.PayableDays == monthly.PayableDays) != 1
            || result.Transfer?.SourceHash != sourceHash || result.Transfer.AttendanceProofs.Count(p => p.RowId == row.Id) != 1
            || result.Transfer.AttendanceProofs.Count(p => p.RowId == row.Id && p.SourceRow == row.SourceRow
                && p.SourceHash == sourceHash && p.Status == PayableOnlyProofStatus && p.PresentDays is null
                && p.PayrollDays == monthly.WorkingDays && p.PayableDays == monthly.PayableDays) != 1)
            throw new InvalidOperationException("The original normal source-import request, identity, attendance assignment and payable-only proof do not match the retained snapshot.");
        var snapshot = Hash(JsonSerializer.Serialize(new { Version = 1, Kind = HistoricalUnknownPresenceKind, monthly, group,
            ImportId = receipt.Id, receipt.RequestHash, Request = Hash(receipt.RequestJson), Result = Hash(receipt.ResultJson),
            sourceHash, SourceRow = row.SourceRow, RowId = row.Id, SourceIdentity = ExcelPayslipMasterSynchronization.IdentityHash(row), original }, Json));
        return native with { CycleStart = cycle.Start, CycleEnd = cycle.End, SnapshotHash = snapshot, Error = "", ProofKind = HistoricalUnknownPresenceKind };
    }

    private static ExcelPayrollAttendanceProof ProveHistoricalAttendanceInput(Plan plan, PlannedRow row,
        ExcelPayrollSetupRequest request, int clientId, string sourceHash)
    {
        var proof = ProveAttendanceInput(plan, row, request, clientId, sourceHash);
        if (proof.Status != "Native attendance inputs matched" || proof.NativeInput?.ProofKind != HistoricalUnknownPresenceKind) return proof;
        return proof with { PresentDays = null, Status = HistoricalUnknownPresenceMatched,
            Reason = "Exact existing Applied source import, identity, monthly payable-only row and configured cycle match. Actual presence remains unknown; no dated presence, leave entitlement or lookup closure is inferred. Ordinary Employee/statutory and full financial parity remain separate checks." };
    }
    private static bool HistoricalAttendanceMatched(ExcelPayrollAttendanceProof proof) => proof.Status == "Native attendance inputs matched"
        || proof.Status == HistoricalUnknownPresenceMatched && proof.PresentDays is null && proof.NativeInput?.ProofKind == HistoricalUnknownPresenceKind;
}
