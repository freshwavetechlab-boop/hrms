using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string HistoricalPayableSeedPendingKind = "ReviewedClosedSourcePayableSeedV2.PendingNative";
    internal const string HistoricalPayableSeedSavedKind = "ReviewedClosedSourcePayableSeedV2.SavedNative";

    internal sealed record HistoricalPayableSeedGroup(int Id, int ClientId, int EmployeeId, int StartDay,
        int EndDay, string Mode, string PolicyBatchId, string WorkWeek);
    internal sealed record HistoricalPayableSeedAbsence(int MonthlyRows, int DatedRows,
        int CoveredRunEmployees, int LocationParts, string NativeSnapshotHash, string StateHash);
    internal sealed record HistoricalPayableOnlySeedPlan(int ClientId, int EmployeeId, string BatchId,
        string RowId, string PayPeriod, string TemplateId, string VerifiedIdentityOwnershipHash,
        ReportedPeriodBoundRowV2 BoundInputs, HistoricalPayableSeedGroup Group,
        HistoricalPayableSeedAbsence Before, NativePayrollAttendanceInput PendingNativeInput, string PlanHash);
    internal sealed record HistoricalPayableSeedMonthly(long Id, int ClientId, int EmployeeId, string Month,
        decimal WorkingDays, decimal? PresentDays, decimal PayableDays, decimal LopDays,
        string SourceType, string Remarks)
    {
        // Existing installations use both INT and BIGINT monthly row IDs.
        // Property materialization preserves the long witness without requiring
        // an exact database constructor signature or changing stored IDs.
        public HistoricalPayableSeedMonthly():this(0,0,0,"",0,null,0,0,"","") { }
    }
    internal sealed record HistoricalPayableSeedSaved(HistoricalPayableOnlySeedPlan Plan,
        HistoricalPayableSeedMonthly MonthlyWitness, NativePayrollAttendanceInput NativeInput, string ProvenanceHash);
    private sealed class HistoricalSeedCount
    {
        public int EmployeeId { get; set; }
        public int MonthlyRows { get; set; }
        public int DatedRows { get; set; }
        public int CoveredRunEmployees { get; set; }
        public int LocationParts { get; set; }
    }
    private sealed record HistoricalSeedState(HistoricalPayableSeedGroup[] Groups,
        HistoricalSeedCount[] Counts, string PolicyHash, bool DailyPolicy,
        IReadOnlyList<PfPolicyVersion> PfVersions, bool LocationSchema);

    internal static void ValidateHistoricalPayableOnlySeedReview(HistoricalPeriodSalaryRequest request)
    {
        if (request is null) throw new InvalidOperationException("A reviewed closed source request is required.");
        if (request.PayableOnlySeedReview is null) return;
        var q = request.Setup;
        if (request.PayableOnlySeedReview is not { Version: 1, Reviewed: true, QuantityBasis: "ReportedPeriodInputsV2" } seed
            || (seed.Reason?.Trim().Length ?? 0) is < 12 or > 1200
            || q?.ReportedPeriodInputs is not { Version: 2, Reviewed: true, Meaning: "ReportedSalaryPeriodFacts" }
            || q.ImportAttendance || q.ImportPayableOnlyAttendance || q.OverrideSourcePayableAttendance
            || q.LocationAllocations is not null || q.PresentDaysColumnIndex is not null || q.ReviewedExtraDutyUnits
            || q.NamedInputReview is not null || request.NamedInputReview is not null
            || !q.PreserveExistingAttendancePolicies || !q.ReviewedPolicyScope || !q.ReviewedFixedInputs
            || q.AttendanceReview is not { Mode: "SourceSeed", Reviewed: true, PresentMeaning: "NotMapped", PayableMeaning: "AlreadyPayable" })
            throw new InvalidOperationException("Closed payable-only creation requires an explicit version-two reported-period review, unchanged existing policy/cycle and unknown actual presence. It cannot reuse an Applied source import or replace attendance.");
    }

    internal async Task<Dictionary<int, HistoricalPayableOnlySeedPlan>> ReadHistoricalPayableSeedPlansAsync(
        MySqlConnection db, MySqlTransaction tx, int clientId, HistoricalPeriodSalaryRequest request,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        IReadOnlyDictionary<int, ReportedPeriodBoundRowV2> boundByEmployee,
        IReadOnlyDictionary<int, string> verifiedIdentityHashes, bool locked)
    {
        if (request.PayableOnlySeedReview is null) return [];
        ValidateHistoricalPayableOnlySeedReview(request);
        ReportedPeriodInputV2Codec.RequireWriterCompatibility(configuration);
        if (!PayableOnlyRolloutEnabled(configuration) || !await HasNullablePresenceSchemaAsync(db, tx))
            throw new InvalidOperationException("Closed payable-only creation is disabled until compatible API/workers and both reviewed nullable-presence columns are deployed and explicitly enabled. No schema is changed here.");
        var q = request.Setup;
        var ids = request.Assignments.Select(a => a.EmployeeId).ToArray();
        if (clientId <= 0 || batch.ClientId != clientId || batch.Id != q.BatchId || ids.Length is < 1 or > 100
            || ids.Distinct().Count() != ids.Length || ids.Any(id => id <= 0)
            || !ids.Order().SequenceEqual(boundByEmployee.Keys.Order())
            || !ids.Order().SequenceEqual(verifiedIdentityHashes.Keys.Order()))
            throw new InvalidOperationException("Exact existing-employee, bound source and independently verified identity selections are required.");
        _ = ClosedHistoricalPeriod(batch.Month, EmployeeEmploymentRules.Today);
        var sourceHash = ReportedPeriodInputProof.SourceHash(batch, source);
        var cycle = PayRunRepository.NativeAttendanceCycle(batch.Month, q.AttendanceCycleStartDay, q.AttendanceCycleEndDay);
        if (q.AttendanceReview!.SourceHash != sourceHash || q.AttendanceReview.SourceMonth != batch.Month
            || q.AttendanceReview.CycleStart != cycle.Start || q.AttendanceReview.CycleEnd != cycle.End
            || q.AttendanceReview.BoundSourceInputs != AttendanceBinding(q))
            throw new InvalidOperationException("The reviewed closed source, mapping and configured cycle changed.");
        var raw = await db.QuerySingleAsync<(string BatchHash, string SourceHash)>(@"SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@BatchId" + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId, BatchId = batch.Id }, tx);
        var state = await ReadHistoricalSeedStateAsync(db, tx, clientId, batch.Month, ids, cycle.Start, cycle.End, locked);
        var plans = new Dictionary<int, HistoricalPayableOnlySeedPlan>();
        foreach (var assignment in request.Assignments)
        {
            var row = batch.Rows.Single(r => r.Id == assignment.RowId);
            var bound = boundByEmployee[assignment.EmployeeId];
            ValidateHistoricalSeedSourceBinding(bound, batch, source, row, raw.BatchHash, raw.SourceHash);
            var owned = state.Groups.Where(g => g.EmployeeId == assignment.EmployeeId).ToArray();
            if (owned.Length != 1) throw new InvalidOperationException("Exactly one existing active Monthly attendance membership is required; this lane creates no policy group or membership.");
            var group = owned[0];
            if (group.Mode != "Monthly" || group.StartDay is < 1 or > 31 || group.EndDay is < 1 or > 31)
                throw new InvalidOperationException("An existing valid Monthly policy group is required; dated or malformed policies are not relabelled.");
            if (PayRunRepository.NativeAttendanceCycle(batch.Month, group.StartDay, group.EndDay) != cycle)
                throw new InvalidOperationException("The existing Monthly group's cycle differs from the explicitly reviewed source period.");
            var counts = state.Counts.Single(c => c.EmployeeId == assignment.EmployeeId);
            var before = HistoricalSeedAbsence(clientId, assignment.EmployeeId, batch.Month, group, counts, state.PolicyHash, state.LocationSchema);
            var splitPf = PfPolicyCalculator.ApplicableVersions(state.PfVersions, assignment.TemplateId,
                DateOnly.ParseExact(cycle.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(cycle.End, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Count > 1;
            SourcePayableAttendance.EnsureIndependent(q.Components.SelectMany(c => new[] { c.Formula, c.BaseComponent }.Concat(c.RowFormulas.Values)), state.DailyPolicy, splitPf, counts.LocationParts > 0);
            plans[assignment.EmployeeId] = CreateHistoricalPayableSeedPlan(assignment.TemplateId, bound, group, before,
                verifiedIdentityHashes[assignment.EmployeeId], true, true);
        }
        var eligible = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, clientId, batch.Month,
            plans.Values.Select(p => (p.EmployeeId, (decimal?)null, p.BoundInputs.PayableDays)), lockEmployees: locked);
        if (eligible is not null) throw new InvalidOperationException(eligible);
        return plans;
    }

    internal static HistoricalPayableOnlySeedPlan CreateHistoricalPayableSeedPlan(string templateId,
        ReportedPeriodBoundRowV2 bound, HistoricalPayableSeedGroup group, HistoricalPayableSeedAbsence before,
        string verifiedIdentityHash, bool rolloutEnabled, bool schemaReady)
    {
        ReportedPeriodInputProof.ValidateBound(bound);
        if (!rolloutEnabled || !schemaReady || string.IsNullOrEmpty(templateId) || !Regex.IsMatch(templateId, @"\A[0-9]{1,20}\z")
            || string.IsNullOrEmpty(verifiedIdentityHash) || !Regex.IsMatch(verifiedIdentityHash, @"\A[a-f0-9]{64}\z")
            || group.Id <= 0 || group.ClientId != bound.ClientId || group.EmployeeId <= 0 || group.Mode != "Monthly"
            || group.StartDay is < 1 or > 31 || group.EndDay is < 1 or > 31
            || before.MonthlyRows != 0 || before.DatedRows != 0 || before.CoveredRunEmployees != 0 || before.LocationParts != 0
            || !Regex.IsMatch(before.NativeSnapshotHash ?? "", @"\A[a-f0-9]{64}\z") || !Regex.IsMatch(before.StateHash ?? "", @"\A[a-f0-9]{64}\z")
            || bound.PayableDays < 0 || bound.PayableDays > bound.PayrollDivisor || decimal.Round(bound.PayableDays, 2) != bound.PayableDays)
            throw new InvalidOperationException("The insert-only closed source seed requires verified identity, one existing Monthly group, empty native/payroll/location coverage and bounded payable quantities; no actual presence is assumed.");
        var cycle = PayRunRepository.NativeAttendanceCycle(bound.PayPeriod, group.StartDay, group.EndDay);
        var hash = Hash(JsonSerializer.Serialize(new { Version = 1, Purpose = HistoricalPayableSeedPendingKind,
            templateId, bound, group, before, verifiedIdentityHash }, Json));
        var pending = new NativePayrollAttendanceInput(group.EmployeeId, bound.ClientId, bound.PayPeriod, bound.PayrollDivisor,
            null, bound.PayableDays, cycle.Start, cycle.End, hash, "") { ProofKind = HistoricalPayableSeedPendingKind };
        return new(bound.ClientId, group.EmployeeId, bound.BatchId, bound.RowId, bound.PayPeriod, templateId,
            verifiedIdentityHash, bound, group, before, pending, hash);
    }

    internal static void ValidateHistoricalSeedSourceBinding(ReportedPeriodBoundRowV2 bound, ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source, ExcelPayslipRow row, string rawBatchHash, string rawSourceHash)
    {
        ReportedPeriodInputProof.ValidateBound(bound);
        if (bound.ClientId != batch.ClientId || bound.BatchId != batch.Id || bound.PayPeriod != batch.Month
            || bound.RowId != row.Id || bound.SourceRow != row.SourceRow || bound.SourceHash != ReportedPeriodInputProof.SourceHash(batch, source)
            || bound.RawBatchHash != rawBatchHash || bound.RawSourceHash != rawSourceHash
            || bound.SourceIdentityHash != ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch, row, source))
            throw new InvalidOperationException("The reported payable quantities do not belong to this exact captured source and full profile identity row.");
    }

    internal async Task<Dictionary<int, HistoricalPayableSeedSaved>> InsertHistoricalPayableSeedsAsync(
        MySqlConnection db, MySqlTransaction tx, IReadOnlyDictionary<int, HistoricalPayableOnlySeedPlan> plans,
        string requestId, AuthUser actor)
    {
        if (plans.Count == 0) return [];
        if (tx.Connection != db || tx.IsolationLevel != System.Data.IsolationLevel.Serializable
            || !Guid.TryParseExact(requestId, "N", out _) || actor.Id <= 0 || plans.Count > 100)
            throw new InvalidOperationException("A real reviewed closed request, actor and Serializable parent transaction are required; this helper cannot commit independently.");
        ReportedPeriodInputV2Codec.RequireWriterCompatibility(configuration);
        if (!PayableOnlyRolloutEnabled(configuration) || !await HasNullablePresenceSchemaAsync(db, tx))
            throw new InvalidOperationException("The explicit compatible-reader/nullable-presence capability changed; the whole closed transaction must roll back.");
        var first = plans.Values.First();
        if (plans.Any(p => p.Key != p.Value.EmployeeId || p.Value.ClientId != first.ClientId || p.Value.PayPeriod != first.PayPeriod || p.Value.BatchId != first.BatchId))
            throw new InvalidOperationException("A seed transaction must contain one exact client/source/month cohort.");
        var cycle = PayRunRepository.NativeAttendanceCycle(first.PayPeriod, first.Group.StartDay, first.Group.EndDay);
        if (plans.Values.Any(p => PayRunRepository.NativeAttendanceCycle(p.PayPeriod, p.Group.StartDay, p.Group.EndDay) != cycle))
            throw new InvalidOperationException("Every selected seed must use the same explicitly reviewed configured cycle.");
        var raw = await db.QuerySingleAsync<(string BatchHash, string SourceHash)>(@"SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@BatchId FOR UPDATE", new { first.ClientId, first.BatchId }, tx);
        if (plans.Values.Any(p => p.BoundInputs.RawBatchHash != raw.BatchHash || p.BoundInputs.RawSourceHash != raw.SourceHash))
            throw new InvalidOperationException("The exact captured source changed before monthly insertion; the entire closed save must roll back.");
        var ids = plans.Keys.ToArray();
        var state = await ReadHistoricalSeedStateAsync(db, tx, first.ClientId, first.PayPeriod, ids, cycle.Start, cycle.End, true);
        foreach (var plan in plans.Values)
        {
            var rebuilt = CreateHistoricalPayableSeedPlan(plan.TemplateId, plan.BoundInputs, plan.Group, plan.Before, plan.VerifiedIdentityOwnershipHash, true, true);
            if (plan.PlanHash != rebuilt.PlanHash || plan.PendingNativeInput != rebuilt.PendingNativeInput)
                throw new InvalidOperationException("The internal seed proposal changed after the reviewed plan.");
            var groups = state.Groups.Where(g => g.EmployeeId == plan.EmployeeId).ToArray();
            var counts = state.Counts.Single(c => c.EmployeeId == plan.EmployeeId);
            if (groups.Length != 1 || groups[0] != plan.Group
                || HistoricalSeedAbsence(plan.ClientId, plan.EmployeeId, plan.PayPeriod, plan.Group, counts, state.PolicyHash, state.LocationSchema) != plan.Before)
                throw new InvalidOperationException("Native monthly/daily/payroll/location coverage, policy or group changed; no recorded attendance is replaced.");
        }
        var eligible = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, first.ClientId, first.PayPeriod,
            plans.Values.Select(p => (p.EmployeeId, (decimal?)null, p.BoundInputs.PayableDays)));
        if (eligible is not null) throw new InvalidOperationException(eligible);
        var saved = new Dictionary<int, HistoricalPayableSeedSaved>();
        foreach (var plan in plans.Values)
        {
            var e = new SourcePayableAttendanceEvidence(3, plan.ClientId, plan.EmployeeId, plan.PayPeriod, plan.BatchId,
                plan.RowId, requestId, plan.BoundInputs.SourceHash, plan.BoundInputs.PayrollDivisor, plan.BoundInputs.PayableDays,
                Mode: SourcePayableAttendance.ClosedReportedMode)
            { Origin = SourcePayableAttendance.ClosedReportedOrigin, ReportedBoundaryHash = plan.BoundInputs.ProofHash };
            var remarks = SourcePayableAttendance.Encode(e);
            var lop = Math.Max(0, e.PayrollDays - e.PayableDays);
            // Plain INSERT only. Unique-month conflicts fail and roll back the
            // caller's whole history/manifest/receipt transaction. Never upsert.
            await db.ExecuteAsync(@"INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks)
VALUES(@ClientId,@EmployeeId,@Period,@Days,NULL,@Payable,@Lop,@SourceType,@Remarks)",
                new { plan.ClientId, plan.EmployeeId, Period = plan.PayPeriod, Days = e.PayrollDays,
                    Payable = e.PayableDays, Lop = lop, SourceType = SourcePayableAttendance.SourceType, Remarks = remarks }, tx);
            var monthly = await db.QuerySingleAsync<HistoricalPayableSeedMonthly>(@"SELECT id Id,client_id ClientId,employee_id EmployeeId,attendance_month Month,
working_days WorkingDays,present_days PresentDays,payable_days PayableDays,lop_days LopDays,source_type SourceType,COALESCE(remarks,'') Remarks
FROM employee_monthly_attendance WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_month=@Period FOR UPDATE",
                new { plan.ClientId, plan.EmployeeId, Period = plan.PayPeriod }, tx);
            var native = AuthenticClosedReportedSeedNative(monthly, plan.Group, plan.BoundInputs, plan.VerifiedIdentityOwnershipHash, requestId);
            saved[plan.EmployeeId] = new(plan, monthly, native, Hash(remarks));
        }
        return saved;
    }

    // Formatting alone is not authorization: the ordinary loader MUST first
    // authenticate its protected v2 history/profile/closed receipt. This helper
    // then reconstructs the exact factual monthly/group snapshot for that scope.
    internal static NativePayrollAttendanceInput AuthenticClosedReportedSeedNative(HistoricalPayableSeedMonthly monthly,
        HistoricalPayableSeedGroup group, ReportedPeriodBoundRowV2 bound, string verifiedIdentityHash, string closedRequestId)
    {
        ReportedPeriodInputProof.ValidateBound(bound);
        if (!Guid.TryParseExact(closedRequestId, "N", out _) || !Regex.IsMatch(verifiedIdentityHash ?? "", @"\A[a-f0-9]{64}\z")
            || monthly.Id <= 0 || monthly.ClientId != bound.ClientId || monthly.EmployeeId != group.EmployeeId
            || monthly.Month != bound.PayPeriod || monthly.PresentDays.HasValue || monthly.WorkingDays != bound.PayrollDivisor
            || monthly.PayableDays != bound.PayableDays || monthly.LopDays != Math.Max(0, bound.PayrollDivisor - bound.PayableDays)
            || monthly.SourceType != SourcePayableAttendance.SourceType || group.Id <= 0 || group.ClientId != bound.ClientId
            || group.Mode != "Monthly" || group.StartDay is < 1 or > 31 || group.EndDay is < 1 or > 31)
            throw new InvalidOperationException("The saved closed-source monthly row, existing group and reported payable boundary differ. Unknown presence cannot be replaced with zero.");
        var e = SourcePayableAttendance.Read(monthly.Remarks, monthly.ClientId, monthly.EmployeeId, monthly.Month, bound.PayrollDivisor, bound.PayableDays);
        if (e.Version != 3 || e.Origin != SourcePayableAttendance.ClosedReportedOrigin || e.Mode != SourcePayableAttendance.ClosedReportedMode
            || e.ReportedBoundaryHash != bound.ProofHash || e.SourceHash != bound.SourceHash || e.BatchId != bound.BatchId
            || e.RowId != bound.RowId || e.ImportId != closedRequestId)
            throw new InvalidOperationException("Closed reported-period attendance requires its exact real closed request and protected reported boundary; a normal Applied receipt is not substituted.");
        var cycle = PayRunRepository.NativeAttendanceCycle(bound.PayPeriod, group.StartDay, group.EndDay);
        var hash = Hash(JsonSerializer.Serialize(new { Version = 3, Origin = SourcePayableAttendance.ClosedReportedOrigin,
            monthly, group, bound.ProofHash, verifiedIdentityHash, closedRequestId }, Json));
        return new(monthly.EmployeeId, monthly.ClientId, monthly.Month, bound.PayrollDivisor, null, bound.PayableDays,
            cycle.Start, cycle.End, hash, "") { ProofKind = HistoricalPayableSeedSavedKind };
    }

    private static HistoricalPayableSeedAbsence HistoricalSeedAbsence(int clientId, int employeeId, string period,
        HistoricalPayableSeedGroup group, HistoricalSeedCount c, string policyHash, bool locationSchema)
    {
        var nativeHash = Hash(JsonSerializer.Serialize(new { Kind = "NativeAttendanceAbsent", clientId, employeeId,
            period, group, c.MonthlyRows, c.DatedRows, c.CoveredRunEmployees, c.LocationParts }, Json));
        return new(c.MonthlyRows, c.DatedRows, c.CoveredRunEmployees, c.LocationParts, nativeHash,
            Hash(JsonSerializer.Serialize(new { nativeHash, policyHash, locationSchema }, Json)));
    }

    private static async Task<HistoricalSeedState> ReadHistoricalSeedStateAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, string month, int[] ids, string start, string end, bool locked)
    {
        await RequireHistoricalSeedMembershipSchemaAsync(db, tx);
        var p = new { ClientId = clientId, Month = month, Ids = ids, Start = start, End = end };
        if (locked) await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id FOR UPDATE", p, tx);
        var groups = (await db.QueryAsync<HistoricalPayableSeedGroup>(@"SELECT g.id Id,g.client_id ClientId,m.employee_id EmployeeId,
g.attendance_cycle_start_day StartDay,g.attendance_cycle_end_day EndDay,g.attendance_mode Mode,COALESCE(g.policy_batch_id,'') PolicyBatchId,COALESCE(g.work_week,'') WorkWeek
FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE g.client_id=@ClientId AND g.is_active=TRUE AND m.employee_id IN @Ids" + (locked ? " FOR UPDATE" : ""), p, tx)).ToArray();
        var counts = (await db.QueryAsync<HistoricalSeedCount>(@"SELECT e.Id EmployeeId,
(SELECT COUNT(*) FROM employee_monthly_attendance a WHERE a.client_id=@ClientId AND a.employee_id=e.Id AND a.attendance_month=@Month) MonthlyRows,
(SELECT COUNT(*) FROM employee_daily_attendance d WHERE d.client_id=@ClientId AND d.employee_id=e.Id AND d.attendance_date BETWEEN @Start AND @End) DatedRows,
(SELECT COUNT(*) FROM payrunemployees pe JOIN payruns r ON r.Id=pe.PayRunId WHERE r.ClientId=@ClientId AND r.PayPeriod=@Month AND pe.EmployeeId=e.Id) CoveredRunEmployees
FROM employees e WHERE e.ClientId=@ClientId AND e.Id IN @Ids", p, tx)).ToArray();
        if (counts.Length != ids.Length) throw new InvalidOperationException("An exact existing same-client employee is missing.");
        var tables = (await db.QueryAsync<string>(@"SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE()
AND table_name IN('employee_payroll_location_assignments','employee_payroll_location_allocations')", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tables.Count is not (0 or 2)) throw new InvalidOperationException("Incomplete location-attendance schema cannot establish safe empty native coverage.");
        if (tables.Count == 2)
        {
            var locations = (await db.QueryAsync<(int EmployeeId, int Count)>(@"SELECT e.Id EmployeeId,
(SELECT COUNT(*) FROM employee_payroll_location_assignments a WHERE a.client_id=@ClientId AND a.employee_id=e.Id AND a.status='Active' AND a.configuration_effective_from<=@End)
+(SELECT COUNT(*) FROM employee_payroll_location_allocations a WHERE a.client_id=@ClientId AND a.employee_id=e.Id AND a.pay_period=@Month) Count
FROM employees e WHERE e.ClientId=@ClientId AND e.Id IN @Ids", p, tx)).ToArray();
            foreach (var c in counts) c.LocationParts = locations.Single(l => l.EmployeeId == c.EmployeeId).Count;
        }
        var policy = await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId);
        var pf = await PfPolicyRepository.ReadVersionsAsync(db, tx, clientId);
        var until = DateTime.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new(groups, counts, Hash(JsonSerializer.Serialize(new { policy, pf }, Json)),
            policy.WeeklyOffRuleVersions.Any(v => v.EffectiveFrom.Date <= until.Date), pf, tables.Count == 2);
    }

    // Current membership rows are undated links. Never silently treat a new
    // effective-date/status schema as an all-time closed-month membership.
    internal static bool HistoricalSeedMembershipSchemaSupported(IEnumerable<string> columnNames)
    {
        var columns = columnNames.Select(c => Regex.Replace(c.ToLowerInvariant(), "[^a-z0-9]", "")).ToArray();
        string[] supported = ["id", "attendancegroupid", "employeeid", "createdat", "createdatutc", "updatedat", "updatedatutc"];
        return columns.Contains("attendancegroupid") && columns.Contains("employeeid")
            && columns.Distinct(StringComparer.Ordinal).Count() == columns.Length
            && columns.All(c => supported.Contains(c, StringComparer.Ordinal));
    }
    internal static async Task RequireHistoricalSeedMembershipSchemaAsync(MySqlConnection db, MySqlTransaction tx)
    {
        var columns = await db.QueryAsync<string>(@"SELECT column_name FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name='attendance_group_employees' ORDER BY ordinal_position", transaction: tx);
        if (!HistoricalSeedMembershipSchemaSupported(columns))
            throw new InvalidOperationException("Closed reported-period creation requires the supported undated membership schema. Effective-date/status or other new membership semantics require an explicit compatible reader; no all-time membership is inferred.");
    }
}
