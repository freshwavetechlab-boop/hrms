using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string LocationCorrectionReceiptPrefix = "excel_location_correction:";
    private PortableIntegrationCredentialProtector? locationCorrectionProtector;
    private PortableIntegrationCredentialProtector LocationCorrectionProtector => locationCorrectionProtector ??= historicalReviewProtector
        ?? new(configuration, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private PayRunRepository LocationCorrectionPayRuns => historicalPayRuns ?? new(configuration, new TaxEngineRepository(configuration));
    private sealed record CorrectionEnvelope(int Version, string Payload);
    private sealed class CorrectionRawWitness { public string BatchHash { get; set; }=""; public string SourceHash { get; set; }=""; }
    private sealed record LocationCorrectionReceipt(int ClientId, string RequestHash, string PlanHash,
        string Kind, ExcelPayrollLocationCorrectionRequest ReviewedRequest, IReadOnlyList<ExcelPayrollLocationRecoveryEvidence> RecoveryEvidence,
        ExcelPayrollLocationCorrectionResult Result, object Previous, object Changes, int ActorId, DateTime AppliedAtUtc);
    private sealed class CorrectionMonthly
    {
        public long Id { get; set; }
        public int EmployeeId { get; set; }
        public decimal WorkingDays { get; set; }
        public decimal? PresentDays { get; set; }
        public decimal PayableDays { get; set; }
        public decimal LopDays { get; set; }
        public string SourceType { get; set; } = "";
    }
    private sealed class LocationCorrectionPlan
    {
        public ExcelPayrollLocationCorrectionPreview Preview { get; } = new();
        public required PayRun Run { get; init; }
        public required string SetupJson { get; init; }
        public required LocationAllocationStorageState Previous { get; init; }
        public List<LocationAssignmentStorageRow> Assignments { get; } = [];
        public List<LocationAllocationStorageRow> Allocations { get; } = [];
        public List<PayRunEmployee> Rows { get; } = [];
        public List<CorrectionMonthly> Monthly { get; } = [];
        public List<PayrollLocationPartPeriod> Periods { get; } = [];
        public List<NamedFormulaInputManifest> NamedInputs { get; } = [];
        public object Archive { get; set; } = new { };
    }

    internal static bool CanManageLocationCorrection(AuthUser actor, int clientId) => SelectedPayRunAuthorization.CanAccess(actor, clientId)
        && !actor.MustChangePassword && SelectedClientAccess.Allows(actor, clientId)
        && new[] { "settings.manage", "employees.manage", "payroll.run" }.All(p => actor.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase));

    internal static void ValidateLocationCorrectionScope(ExcelPayrollLocationCorrectionRequest request)
    {
        if (request is null || request.PayRunId <= 0 || !Guid.TryParseExact(request.BatchId, "N", out _)
            || request.EmployeeIds is null || request.EmployeeIds.Count is < 1 or > 10 || request.EmployeeIds.Any(id => id <= 0)
            || request.EmployeeIds.Distinct().Count() != request.EmployeeIds.Count)
            throw new InvalidOperationException("Select one saved source batch, an existing Draft and 1–10 unique existing employees.");
        if (request.RecoverOrphanedLineage)
        {
            if (request.SourceReview is null || request.SourceReview.BatchId != request.BatchId || !Regex.IsMatch(request.RecoverySourceHash ?? "", "\\A[a-f0-9]{64}\\z")
                || request.SourceReview.EmployeeDecisions is null || request.SourceReview.EmployeeDecisions.Any(decision => decision is null || decision.AllowCreate) || request.SourceReview.CreateMissingWorkLocations
                || request.SourceReview.CreateMissingDropdownValues || request.SourceReview.ImportPayableOnlyAttendance || request.SourceReview.OverrideSourcePayableAttendance)
                throw new InvalidOperationException("Orphan recovery requires an explicit current source hash and complete fresh existing-employee source review; no identities, locations or policy are created.");
            ValidateRequest(request.SourceReview);
        }
        else if (request.SourceReview is not null || !string.IsNullOrEmpty(request.RecoverySourceHash))
            throw new InvalidOperationException("A fresh recovery mapping is accepted only with explicit orphan-lineage recovery review.");
        ValidateLocationRowBindingReview(request);
    }

    public async Task<ExcelPayrollLocationCorrectionPreview> PreviewLocationCorrectionAsync(int clientId,
        ExcelPayrollLocationCorrectionRequest request, AuthUser actor)
    {
        if (!CanManageLocationCorrection(actor, clientId)) throw new UnauthorizedAccessException();
        ValidateLocationCorrectionScope(request);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var plan = await BuildLocationCorrectionAsync(db, tx, clientId, request, false);
        await tx.RollbackAsync(); return plan.Preview;
    }

    public async Task<ExcelPayrollLocationCorrectionResult> ApplyLocationCorrectionAsync(int clientId,
        ExcelPayrollLocationCorrectionApplyRequest command, AuthUser actor)
    {
        if (!CanManageLocationCorrection(actor, clientId)) throw new UnauthorizedAccessException();
        ValidateLocationCorrectionScope(command.Request);
        if (!SelectedRunRuntime.Read(configuration).Safe || !command.Acknowledged || !Guid.TryParseExact(command.RequestId, "N", out _)
            || !Regex.IsMatch(command.PlanHash ?? "", "\\A[a-f0-9]{64}\\z"))
            throw new InvalidOperationException("Apply requires explicit acknowledgment, stable request ID, current preview hash and isolated local runtime with migrations/workers disabled and outbound suppressed.");
        var scope = command.Request;
        var requestHash = Hash(JsonSerializer.Serialize(new { clientId, scope, command.PlanHash }, Json));
        var moduleCode = LocationCorrectionReceiptPrefix + command.RequestId;
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx) is null)
            throw new InvalidOperationException("The client is unavailable.");
        var prior = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@ModuleCode FOR UPDATE", new { ClientId = clientId, ModuleCode = moduleCode }, tx);
        if (prior is not null)
        {
            var receipt = ReadLocationCorrectionReceipt(prior);
            if (receipt.ClientId != clientId || receipt.RequestHash != requestHash) throw new InvalidOperationException("This request reference already belongs to a different correction; its original result is retained.");
            await tx.RollbackAsync(); return receipt.Result with { Replayed = true };
        }
        var plan = await BuildLocationCorrectionAsync(db, tx, clientId, scope, true);
        if (!plan.Preview.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Preview.Blockers.Take(5)));
        RequireMatchingSetupPlanHash(plan.Preview.PlanHash, command.PlanHash!);
        var result = new ExcelPayrollLocationCorrectionResult(command.RequestId, scope.PayRunId, scope.EmployeeIds.Order().ToArray(), command.PlanHash!, false);
        var namedManifests=plan.NamedInputs.Select(m=>m with{RequestId=command.RequestId,ReviewerId=actor.Id,ReviewedAtUtc=DateTime.UtcNow}).ToArray();
        object changes=namedManifests.Length==0?new { plan.Assignments, plan.Allocations, plan.Preview.Employees }:new {plan.Assignments,plan.Allocations,plan.Preview.Employees,NamedInputs=namedManifests};
        var receiptValue = new LocationCorrectionReceipt(clientId, requestHash, command.PlanHash!,
            scope.RecoverOrphanedLineage ? "OrphanedLocationRecovery" : "AppliedLocationCorrection", scope, plan.Preview.RecoveryEvidence, result, plan.Archive,
            changes, actor.Id, DateTime.UtcNow);
        var receiptJson = JsonSerializer.Serialize(new CorrectionEnvelope(1, LocationCorrectionProtector.ProtectLocationCorrection(JsonSerializer.Serialize(receiptValue, Json))), Json);
        if (System.Text.Encoding.UTF8.GetByteCount(receiptJson) > 8_000_000) throw new InvalidOperationException("The complete previous Draft audit exceeds the correction limit; split the employee selection and repreview.");
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@ModuleCode,TRUE,CAST(@Json AS JSON))",
            new { ClientId = clientId, ModuleCode = moduleCode, Json = receiptJson }, tx);
        // Ordinary assignment/allocation writers remain insert-only. This separate
        // guarded lane changes only explicit existing period keys after archiving.
        await InsertLocationAllocationStorageAsync(db, tx, plan.Assignments, []);
        foreach(var manifest in namedManifests)await NamedFormulaInputStore.InsertManifestAsync(db,tx,configuration,manifest);
        foreach (var assignment in plan.Assignments)
        {
            var allocation = plan.Allocations.Single(row => row.AssignmentId == assignment.Id);
            var previous = plan.Previous.Allocations.Single(row => row.EmployeeId == allocation.EmployeeId && row.SourcePartKey == allocation.SourcePartKey);
            await ReviewedLocationCorrectionOrigins.SaveAsync(db, tx, configuration, new(clientId, assignment.EmployeeId, plan.Run.PayPeriod,
                assignment.Id, previous.AssignmentId, allocation.SourcePartKey, command.RequestId, allocation.SourceHash));
        }
        foreach (var allocation in plan.Allocations)
        {
            var changed = await db.ExecuteAsync(@"UPDATE employee_payroll_location_allocations SET assignment_id=@AssignmentId,
reference_assignment_id=@ReferenceAssignmentId,payroll_days=@PayrollDays,present_days=@PresentDays,payable_days=@PayableDays,
source_hash=@SourceHash"+(scope.RowRebindReview is null?"":",source_row_id=@SourceRowId")+@" WHERE client_id=@ClientId AND employee_id=@EmployeeId AND pay_period=@PayPeriod AND source_part_key=@SourcePartKey", allocation, tx);
            if (changed != 1) throw new InvalidOperationException("An exact source-period allocation disappeared; no correction was saved.");
        }
        foreach (var period in plan.Periods)
        {
            var monthly = plan.Monthly.Single(row => row.EmployeeId == period.EmployeeId);
            var present = period.Parts.Sum(part => part.PresentDays); var payable = period.Parts.Sum(part => part.PayableDays);
            if (monthly.PresentDays != present || monthly.PayableDays != payable)
                if (await db.ExecuteAsync(@"UPDATE employee_monthly_attendance SET present_days=@Present,payable_days=@Payable,lop_days=GREATEST(0,working_days-@Payable)
WHERE id=@Id AND client_id=@ClientId AND employee_id=@EmployeeId AND attendance_month=@Month AND present_days IS NOT NULL",
                    new { monthly.Id, ClientId = clientId, period.EmployeeId, Month = plan.Run.PayPeriod, Present = present, Payable = payable }, tx) != 1)
                    throw new InvalidOperationException("The exact known monthly attendance row disappeared; no correction was saved.");
        }
        // Reload through native storage, not the preview's temporary object. All
        // source amounts are proved again before replacing current projections.
        await LocationCorrectionPayRuns.SaveLocationCorrectionRowsAsync(db, tx, plan.Run, plan.Periods, plan.SetupJson,
            plan.Preview.Employees.SelectMany(employee => employee.SourceParts).ToArray());
        await tx.CommitAsync(); return result;
    }

    private LocationCorrectionReceipt ReadLocationCorrectionReceipt(string json)
    {
        var envelope = JsonSerializer.Deserialize<CorrectionEnvelope>(json, Json);
        var plaintext = envelope is { Version: 1 } ? LocationCorrectionProtector.UnprotectLocationCorrection(envelope.Payload) : null;
        return plaintext is null ? throw new InvalidOperationException("The saved correction receipt cannot be authenticated.")
            : JsonSerializer.Deserialize<LocationCorrectionReceipt>(plaintext, Json) ?? throw new InvalidOperationException("The saved correction receipt is incomplete.");
    }

    private async Task<LocationCorrectionPlan> BuildLocationCorrectionAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, ExcelPayrollLocationCorrectionRequest request, bool lockRows)
    {
        var ids = request.EmployeeIds.Order().ToArray(); var suffix = lockRows ? " FOR UPDATE" : "";
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE" + suffix, new { ClientId = clientId }, tx) is null)
            throw new InvalidOperationException("Select an active client.");
        var run = await db.QuerySingleOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id AND ClientId=@ClientId" + suffix,
            new { Id = request.PayRunId, ClientId = clientId }, tx) ?? throw new InvalidOperationException("The selected Draft does not belong to this client.");
        if (run.Status != "Draft" || run.RunType != "Regular" || run.TotalWorkingDays is < 1 or > 31)
            throw new InvalidOperationException("Only an existing Regular Draft can be corrected; submitted, approved, running and paid runs are retained.");
        if (!await HasLocationAllocationSchemaAsync(db, tx) || !await HasReceiptTableAsync(db, tx))
            throw new InvalidOperationException("Existing location/source receipt storage is required. This endpoint never migrates the database.");
        if (lockRows)
        {
            await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId ORDER BY Id FOR UPDATE", new { ClientId = clientId }, tx);
            await db.QueryAsync<long>("SELECT Id FROM salarystructures ORDER BY Id FOR UPDATE", transaction: tx);
            await db.QueryAsync<long>("SELECT Id FROM salarystructurelines ORDER BY Id FOR UPDATE", transaction: tx);
            await db.QueryAsync<long>("SELECT Id FROM salarycomponents ORDER BY Id FOR UPDATE", transaction: tx);
            await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id=@ClientId ORDER BY ModuleCode FOR UPDATE", new { ClientId = clientId }, tx);
            await db.QueryAsync<string>("SELECT id FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId FOR UPDATE", new { ClientId = clientId, request.BatchId }, tx);
        }
        var (batch, source) = await ReadSourceAsync(db, tx, clientId, request.BatchId);
        var rawWitness=await db.QuerySingleAsync<CorrectionRawWitness>(@"SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@ClientId AND b.id=@BatchId",
            new{ClientId=clientId,request.BatchId},tx);
        if (batch.Month != run.PayPeriod) throw new InvalidOperationException("The source batch month must equal the existing Draft month.");
        if (ExcelPayslipCalculationService.ValidateSource(source, batch) is { } invalidSource)
            throw new InvalidOperationException("Saved source validation failed: " + invalidSource);
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, clientId, batch.Id);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        var namedProfile=await ReadCorrectionNamedProfileAsync(db,tx,clientId,request,setupJson,lockRows);
        if(namedProfile is not null&&(!request.RecoverOrphanedLineage||request.SourceReview is null))throw new InvalidOperationException("This opt-in named-operand correction needs the explicit complete fresh orphan source review.");
        var storage = await ReadLocationAllocationStateAsync(db, tx, clientId, ids, batch.Month, lockRows);
        var plan = new LocationCorrectionPlan { Run = run, SetupJson = setupJson, Previous = storage };
        var preview = plan.Preview; preview.ClientId = clientId; preview.PayRunId = run.Id; preview.BatchId = batch.Id; preview.Month = batch.Month;
        preview.SourceHash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        preview.RawBatchSha256=rawWitness.BatchHash;preview.RawSourceSha256=rawWitness.SourceHash;
        if (request.RecoverOrphanedLineage && request.RecoverySourceHash != preview.SourceHash)
            preview.Blockers.Add("The fresh recovery source hash changed; review the current saved batch and source before applying.");
        if (!SelectedRunRuntime.Read(configuration).Safe) preview.Blockers.Add("Apply is available only on the isolated reviewed runtime; preview remains read-only.");
        var selectedRows = (await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@PayRunId AND ClientId=@ClientId AND EmployeeId IN @Ids ORDER BY EmployeeId" + suffix,
            new { PayRunId = run.Id, ClientId = clientId, Ids = ids }, tx)).ToArray();
        if (selectedRows.Length != ids.Length || selectedRows.Any(row => !PayRunRepository.CanCorrectLocationDraftRow(row)))
            preview.Blockers.Add("Each selected employee needs one unpaid, non-skipped Draft row without manual amounts or frozen PF/attendance/unknown-presence snapshots.");
        var allRunRows = (await db.QueryAsync("SELECT * FROM payrunemployees WHERE PayRunId=@PayRunId ORDER BY EmployeeId" + suffix, new { PayRunId = run.Id }, tx)).ToArray();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE PayRunId=@Id AND (PaymentStatus NOT IN ('','Pending') OR PaymentDate IS NOT NULL)", new { run.Id }, tx) > 0)
            preview.Blockers.Add("This Draft already has payment evidence.");
        var adjustments = (await db.QueryAsync("SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@Month AND EmployeeId IN @Ids ORDER BY Id" + suffix,
            new { ClientId = clientId, Month = batch.Month, Ids = ids }, tx)).ToArray();
        if (adjustments.Length != 0) preview.Blockers.Add("Review existing period adjustments separately; this correction never recounts or detaches them.");
        var overlaps = (await db.QueryAsync<int>(@"SELECT p.EmployeeId FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId
WHERE r.Id<>@Id AND r.ClientId=@ClientId AND r.PayPeriod=@Month AND p.EmployeeId IN @Ids AND p.IsSkipped=FALSE
AND r.Status IN ('Draft','AwaitingProcessing','Queued','Processing','Pending Approval','Approved','Partially Paid','Paid')" + suffix,
            new { run.Id, ClientId = clientId, Month = batch.Month, Ids = ids }, tx)).ToArray();
        if (overlaps.Length != 0) preview.Blockers.Add("Another active payroll covers a selected employee in this month.");
        var workflow = await ReadLocationCorrectionOptionalRowsAsync(db, tx, "workflowinstances",
            "SELECT * FROM workflowinstances WHERE LOWER(ResourceType) IN ('payrun','payroll','payrollrun') AND ResourceId=@ResourceId ORDER BY Id" + suffix,
            new { ResourceId = run.Id.ToString(CultureInfo.InvariantCulture) });
        if (workflow.Length != 0) preview.Blockers.Add("Existing payroll workflow history is retained; use its reviewed correction process.");
        var monthly = (await db.QueryAsync<CorrectionMonthly>(@"SELECT id Id,employee_id EmployeeId,working_days WorkingDays,
present_days PresentDays,payable_days PayableDays,lop_days LopDays,source_type SourceType FROM employee_monthly_attendance
WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids ORDER BY employee_id" + suffix,
            new { ClientId = clientId, Month = batch.Month, Ids = ids }, tx)).ToArray();
        plan.Monthly.AddRange(monthly);
        if (monthly.Length != ids.Length || monthly.Any(row => row.PresentDays is null || !row.SourceType.Equals("Monthly", StringComparison.OrdinalIgnoreCase)))
            preview.Blockers.Add("Known existing Monthly attendance is required; actual presence, leave and daily dates are never invented.");
        var policies = (await db.QueryAsync("SELECT * FROM modulesettings WHERE client_id=@ClientId AND ModuleCode IN ('payroll','leave_attendance') ORDER BY ModuleCode" + suffix, new { ClientId = clientId }, tx)).ToArray();
        var groups = (await db.QueryAsync(@"SELECT g.*,m.employee_id FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE g.client_id=@ClientId AND m.employee_id IN @Ids ORDER BY g.id,m.employee_id" + suffix, new { ClientId = clientId, Ids = ids }, tx)).ToArray();
        var traces = await ReadLocationCorrectionOptionalRowsAsync(db, tx, "payroll_calculation_traces", "SELECT * FROM payroll_calculation_traces WHERE PayRunId=@PayRunId AND EmployeeId IN @Ids ORDER BY Id" + suffix, new { PayRunId = run.Id, Ids = ids });
        var taxes = await ReadLocationCorrectionOptionalRowsAsync(db, tx, "tax_computation_snapshots", "SELECT * FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId AND employee_id IN @Ids ORDER BY id" + suffix, new { PayRunId = run.Id, Ids = ids });
        var reconciliation = await ReadLocationCorrectionOptionalRowsAsync(db, tx, "payroll_reconciliation_results", "SELECT * FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId ORDER BY Id" + suffix, new { PayRunId = run.Id });
        var identities = await ReadIdentitiesAsync(db, tx, clientId, batch.Month);
        var templateScopes=await ReadTemplateScopesAsync(db,tx,clientId);
        var nativeInputs=await PayRunRepository.ReadNativeAttendanceProofAsync(db,tx,clientId,batch.Month,ids,allowRecordedMonthlyForSalaryCorrection:true);
        var applied = new List<object>(); var nativeHashes = new List<string>();var rowRebindProofs=new List<object>();
        foreach (var employeeId in ids)
        {
            try
            {
                var saved = selectedRows.Single(row => row.EmployeeId == employeeId);
                if (!PayRunRepository.CanCorrectLocationDraftRow(saved)) throw new InvalidOperationException("The existing Draft row contains protected or manual state.");
                var allocations = storage.Allocations.Where(row => row.EmployeeId == employeeId).OrderBy(row => row.SourcePartKey, StringComparer.Ordinal).ToArray();
                var storedAllocations=allocations;
                IReadOnlyList<LocationAssignmentStorageRow> workingAssignments=storage.Assignments;
                ReboundLocationRows? rowRebinding=null;
                if (allocations.Length == 0 || allocations.Any(row => row.SourceBatchId != batch.Id) || allocations.Select(row => row.SetupImportId).Distinct().Count() != 1)
                    throw new InvalidOperationException("Select all source parts for this employee from one exact Applied location receipt; cross-batch corrections require a separate review.");
                var importId = allocations[0].SetupImportId;
                var parent = await db.QuerySingleOrDefaultAsync<SavedSetup>(@"SELECT id ImportId,request_id RequestId,status Status,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE id=@Id AND client_id=@ClientId AND batch_id=@BatchId AND status='Applied'" + suffix,
                    new { Id = importId, ClientId = clientId, BatchId = batch.Id }, tx);
                ExcelPayrollSetupRequest original; ExcelPayrollSetupResult? result;
                if (parent is null)
                {
                    if (!request.RecoverOrphanedLineage || request.SourceReview is null)
                        throw new InvalidOperationException("The original Applied location source receipt is unavailable. An explicit fresh orphan-lineage recovery review is required; no parent history is fabricated.");
                    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payroll_setup_imports WHERE id=@Id", new { Id = importId }, tx) != 0)
                        throw new InvalidOperationException("The parent import exists with a different client, batch or non-Applied state. Orphan recovery cannot waive that history.");
                    original = request.SourceReview; result = null;
                    if(request.RowRebindReview is not null)
                    {
                        rowRebinding=await ReviewLocationRowBindingsAsync(db,tx,request,batch,source,saved,allocations,storage.Assignments,identities);
                        var oldReplay=await LocationCorrectionPayRuns.CalculateLocationCorrectionAsync(db,tx,run,rowRebinding.OriginalPeriod,setupJson,requirePersistedParts:true);
                        var oldFinancialHash=RequireOldLocationFinancialReplay(saved,oldReplay.Row);
                        nativeHashes.Add("original-rebind:"+oldReplay.EvidenceHash);
                        rowRebindProofs.Add(new{employeeId,rowRebinding.Bindings,rowRebinding.NumericRowWitness,OldFinancialProofHash=oldFinancialHash,OldCoreEvidenceHash=oldReplay.EvidenceHash});
                        preview.RowRebindings??=[];
                        preview.RowRebindings.AddRange(rowRebinding.Bindings.Select(binding=>new ExcelPayrollLocationRowRebindEvidence(employeeId,binding.AssignmentId,binding.SourcePartKey,
                            binding.StoredSourceRowId,binding.CurrentRowId,binding.SourceRow,binding.SourceIdentityHash,oldFinancialHash,saved.PresentDays!.Value,saved.PayableDays)));
                        allocations=rowRebinding.Allocations.ToArray();workingAssignments=rowRebinding.Assignments;
                    }
                    foreach (var allocation in allocations)
                    {
                        RequireDurableLocationRecoveryLink(batch, source, allocation);
                        preview.RecoveryEvidence.Add(new(employeeId, importId, "Unavailable — no parent row exists", allocation.SourceHash,
                            preview.SourceHash, allocation.SourceHash != preview.SourceHash, true));
                    }
                }
                else
                {
                    if (request.RecoverOrphanedLineage) throw new InvalidOperationException("This source has an Applied parent; use the ordinary immutable-receipt correction review.");
                    original = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(parent.RequestJson, Json)!;
                    result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(parent.ResultJson, Json)!;
                }
                var employee = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, employeeId, tx)
                    ?? throw new InvalidOperationException("The exact existing employee is unavailable.");
                if(!nativeInputs.TryGetValue(employeeId,out var nativeInput)||nativeInput.Error.Length!=0||nativeInput.PresentDays is null)
                    throw new InvalidOperationException("The current native known monthly inputs and exact configured policy cycle must be reviewed before correction.");
                var resolved = BuildLocationCorrectionParts(clientId, batch, source, original, result, setupJson, employee,
                    identities, allocations, workingAssignments, saved.DetailsJson, orphanRecovery: parent is null,nativeInput:nativeInput,templateScopes:templateScopes,
                    reboundParts:rowRebinding?.Bindings.Select(binding=>binding.SourcePartKey).ToHashSet(StringComparer.Ordinal),namedProfile:namedProfile,namedReview:request.NamedInputReview);
                plan.Periods.Add(resolved.Period); plan.Allocations.AddRange(resolved.Allocations); plan.Assignments.AddRange(resolved.Assignments);
                plan.NamedInputs.AddRange(PrepareNamedCorrectionManifests(resolved.Period,batch,source,request,namedProfile,rawWitness.BatchHash,rawWitness.SourceHash,
                    resolved.Assignments.ToDictionary(assignment=>assignment.Id,assignment=>assignment.SourceRowId,StringComparer.Ordinal)));
                preview.Aggregates.AddRange(resolved.Aggregates);
                var native = await LocationCorrectionPayRuns.CalculateLocationCorrectionAsync(db, tx, run, resolved.Period, setupJson);
                RequireLocationCorrectionParity(native.Row, resolved.Rows);
                native.Row.Id = saved.Id;
                plan.Rows.Add(native.Row); nativeHashes.Add(native.EvidenceHash);
                var employer = ReadCorrectionFinancial(native.Row).Where(line => line.Category == "Benefit").Sum(line => line.Amount);
                preview.Employees.Add(new(employeeId, saved.Id, saved.PresentDays!.Value, saved.PayableDays,
                    native.Row.PresentDays!.Value, native.Row.PayableDays, saved.GrossPay, saved.StatutoryDeductions, saved.NetPay,
                    native.Row.GrossPay, native.Row.StatutoryDeductions, employer, native.Row.NetPay, resolved.Rows));
                foreach (var next in resolved.Allocations)
                {
                    var old = storedAllocations.Single(a => a.SourcePartKey == next.SourcePartKey);
                    void Changed(string field, object before, object after) { var left=Convert.ToString(before,CultureInfo.InvariantCulture)??"";var right=Convert.ToString(after,CultureInfo.InvariantCulture)??"";if(left!=right)preview.Changes.Add(new(employeeId,old.SourceRowId,field,left,right)); }
                    Changed("Assignment",old.AssignmentId,next.AssignmentId);Changed("Present days",old.PresentDays,next.PresentDays);Changed("Payable days",old.PayableDays,next.PayableDays);
                    Changed("Source proof hash",old.SourceHash,next.SourceHash);
                    Changed("Source row",old.SourceRowId,next.SourceRowId);
                    if (next.AssignmentId != old.AssignmentId)
                    {
                        var previousAssignment=storage.Assignments.Single(a=>a.Id==old.AssignmentId);var nextAssignment=resolved.Assignments.Single(a=>a.Id==next.AssignmentId);
                        Changed("Settings template",previousAssignment.SalaryStructureId,nextAssignment.SalaryStructureId);
                        Changed("Rules hash",previousAssignment.RulesHash,nextAssignment.RulesHash);
                    }
                }
                applied.Add(new { ImportId=importId, ParentStatus=parent?.Status??"Unavailable", ParentFingerprint=parent is null?null:Hash(parent.RequestJson+"\n"+parent.ResultJson), MappingHash=Hash(JsonSerializer.Serialize(original,Json)) });
            }
            catch (Exception failure) when (failure is InvalidOperationException or FormatException or OverflowException or JsonException)
            { preview.Blockers.Add($"Employee {employeeId}: {failure.Message}"); }
        }
        plan.Archive = rowRebindProofs.Count==0?new { storage, monthly, SelectedDraftRows = selectedRows, traces, taxes, reconciliation, SourceHash = preview.SourceHash,rawWitness }
            :new {storage,monthly,SelectedDraftRows=selectedRows,traces,taxes,reconciliation,SourceHash=preview.SourceHash,rawWitness,RowRebindings=rowRebindProofs};
        var hashWitness=new { clientId, request, run, batch, source, setupJson, storage,
            identities, allRunRows, monthly, policies, groups, adjustments, overlaps, workflow, traces, taxes, reconciliation, applied, nativeHashes,nativeInputs,templateScopes,
            proposed = preview.Employees,rawWitness };
        var legacyWitness=rowRebindProofs.Count==0?(object)hashWitness:new{Witness=hashWitness,RowRebindings=rowRebindProofs};
        preview.PlanHash = Hash(JsonSerializer.Serialize(plan.NamedInputs.Count==0?legacyWitness:new{Witness=legacyWitness,NamedInputs=plan.NamedInputs,Profile=namedProfile},Json));
        return plan;
    }

    private static async Task<object[]> ReadLocationCorrectionOptionalRowsAsync(MySqlConnection db, MySqlTransaction tx, string table, string sql, object args)
    {
        var exists = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@Table)", new { Table = table }, tx);
        return exists ? (await db.QueryAsync(sql, args, tx)).Cast<object>().ToArray() : [];
    }

    internal sealed record CorrectionParts(PayrollLocationPartPeriod Period, List<ExcelPayrollSetupPreviewRow> Rows,
        List<LocationAssignmentStorageRow> Assignments, List<LocationAllocationStorageRow> Allocations,List<ExcelPayrollLocationCorrectionAggregate> Aggregates);

    internal static CorrectionParts BuildLocationCorrectionParts(int clientId, ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source, ExcelPayrollSetupRequest original, ExcelPayrollSetupResult? receipt,
        string setupJson, Employee employee, IReadOnlyList<Identity> identities,
        IReadOnlyList<LocationAllocationStorageRow> allocations, IReadOnlyList<LocationAssignmentStorageRow> assignments, string nativeDetails, bool orphanRecovery=false,NativePayrollAttendanceInput? nativeInput=null,IReadOnlyList<ExcelPayrollRecordedTemplateScope>? templateScopes=null,
        IReadOnlySet<string>? reboundParts=null,NamedFormulaInputProfile? namedProfile=null,NamedFormulaInputReview? namedReview=null)
    {
        if (original.BatchId != batch.Id || original.LocationAllocations is not { } review
            || !orphanRecovery && (receipt is null || receipt.ImportId.Length != 32 || receipt.LocationAllocations is null || !receipt.EmployeeIds.Contains(employee.Id))
            || employee.ClientId != clientId
            || original.AttendanceReview is not { Reviewed: true }
            || source.CaptureVersion > 0 && source.DependencyReviewStatus != "Reviewed")
            throw new InvalidOperationException("The source review must prove this existing employee and complete source-period parts; known actual presence is retained separately.");
        var group = review.Groups.SingleOrDefault(group => group.EmployeeId == employee.Id)
            ?? throw new InvalidOperationException("The prior source receipt has no unique employee location group.");
        if (!group.Parts.Select(part => part.RowId).Order(StringComparer.Ordinal).SequenceEqual(allocations.Select(row => row.SourceRowId).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Every existing source location part must be selected together.");
        if (allocations.Select(row => row.AssignmentId).Distinct(StringComparer.Ordinal).Count() != allocations.Count)
            throw new InvalidOperationException("Shared-assignment source parts require a separate complete assignment revision review; no ambiguous period reference is rewritten.");
        var sourceCodes = group.Parts.Select(part => batch.Rows.Single(row => row.Id == part.RowId).EmployeeCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceCodes.Length != 1 || sourceCodes[0].Length == 0 || !batch.Rows.Where(row => row.EmployeeCode.Trim().Equals(sourceCodes[0], StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Id).Order(StringComparer.Ordinal).SequenceEqual(allocations.Select(row => row.SourceRowId).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Every saved source part for this exact employee code must be reviewed together.");
        if (original.AttendanceReview.BoundSourceInputs != AttendanceBinding(original))
            throw new InvalidOperationException("The original actual-worked/payable source column bindings are incomplete or changed.");
        if(nativeInput is null||nativeInput.EmployeeId!=employee.Id||nativeInput.ClientId!=clientId||nativeInput.Month!=batch.Month||nativeInput.PresentDays is null
            ||nativeInput.Error.Length!=0||nativeInput.PayrollDays is null||nativeInput.PresentDays!=allocations.Sum(a=>a.PresentDays)
            ||nativeInput.PayableDays!=allocations.Sum(a=>a.PayableDays))
            throw new InvalidOperationException("The complete existing location parts must agree with current known native monthly inputs before correction.");
        if(nativeInput.CycleStart!=original.AttendanceReview.CycleStart||nativeInput.CycleEnd!=original.AttendanceReview.CycleEnd)
            throw new InvalidOperationException("The current native configured cycle differs from the explicit saved-source review.");
        var setup = JsonNode.Parse(setupJson)!.AsObject();
        var masters = setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();
        var templates = setup["salaryStructures"]!.AsArray().OfType<JsonObject>().Where(t => Text(t, "active") == "true"
            && Text(t, "clientId").Split([';', ',', '|']).Any(value => value.Split(':')[0].Trim() == clientId.ToString(CultureInfo.InvariantCulture))).ToArray();
        var period = new PayrollLocationPartPeriod { ClientId = clientId, EmployeeId = employee.Id, PayPeriod = batch.Month };
        var rows = new List<ExcelPayrollSetupPreviewRow>(); var newAssignments = new List<LocationAssignmentStorageRow>(); var newAllocations = new List<LocationAllocationStorageRow>();
        var aggregates=new List<ExcelPayrollLocationCorrectionAggregate>();
        var newSourceHash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        foreach (var previous in allocations.OrderBy(row => row.SourcePartKey, StringComparer.Ordinal))
        {
            var old = assignments.Single(a => a.Id == previous.AssignmentId && a.ClientId == clientId && a.EmployeeId == employee.Id);
            if (old.SourceBatchId != batch.Id || old.SourceRowId != previous.SourceRowId || old.SetupImportId != previous.SetupImportId
                || previous.ClientId != clientId || previous.EmployeeId != employee.Id || previous.PayPeriod != batch.Month || old.SourceHash != previous.SourceHash)
                throw new InvalidOperationException("The exact immutable assignment/allocation source identity or legacy provenance is inconsistent.");
            var originalPart = group.Parts.Single(part => part.RowId == previous.SourceRowId);
            if (originalPart.WorkLocationId != old.WorkLocationId || originalPart.TemplateId != old.SalaryStructureId
                || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(originalPart.ComponentScopes, Json), JsonNode.Parse(old.ComponentScopesJson)))
                throw new InvalidOperationException("The immutable assignment does not match its original reviewed location/template/component scope.");
            if (old.Status != "Active") throw new InvalidOperationException("This initial correction lane does not overwrite a prior period correction; retain its receipt and review a new complete revision.");
            if (!orphanRecovery && !receipt!.LocationAssignmentReceipts.Any(r => r.RowId == previous.SourceRowId && r.EmployeeId == employee.Id && r.AssignmentId == old.Id
                && r.PartKey == previous.SourcePartKey && r.WorkLocationId == old.WorkLocationId && r.TemplateId == old.SalaryStructureId
                && r.PayPeriod == batch.Month && r.RulesHash == old.RulesHash && r.SourceHash == old.SourceHash))
                throw new InvalidOperationException("The original Applied receipt does not prove this immutable assignment and source row.");
            var row = batch.Rows.Single(r => r.Id == previous.SourceRowId);
            var errors = new List<ExcelPayrollSetupIssue>();
            if (!orphanRecovery && (MatchIdentity(row, SourceInformation(row, source, original.SourceWorkLocationColumnIndex), identities,
                new() { RowId = row.Id, MatchedEmployeeId = employee.Id, AllowCreate = false }, errors)?.Id != employee.Id
                || errors.Any(error => error.Severity == "Error"))) throw new InvalidOperationException("Current exact source identity is missing or conflicting; name-only matching is unavailable.");
            if (orphanRecovery)
            {
                RequireDurableLocationRecoveryLink(batch,source,previous);
                var decisions=original.EmployeeDecisions.Where(d=>d.RowId==row.Id).ToArray();
                if(decisions.Length!=1||decisions[0].AllowCreate||decisions[0].MatchedEmployeeId!=employee.Id)
                    throw new InvalidOperationException("The fresh review must explicitly select this exact durable-linked existing employee for every source part.");
                var scoped=JsonSerializer.Deserialize<Employee>(JsonSerializer.Serialize(employee,Json),Json)!;scoped.WorkLocationId=old.WorkLocationId;
                if(TemplateScopeIssues(original,[new(){Employee=scoped}]).Count!=0)
                    throw new InvalidOperationException("Review the fresh source cohort's exact location/category/day/policy applicability before recovering orphaned lineage.");
            }
            var definitions = JsonSerializer.Deserialize<List<ExcelPayrollSetupComponent>>(JsonSerializer.Serialize(original.Components, Json), Json)!;
            NamedFormulaInputContext? namedContext=null;Dictionary<int,NamedRecurringSourceBoundary>? namedLeaves=null;
            if(namedProfile is not null)
            {
                if(namedReview is null||namedReview.TemplateId!=namedProfile.TemplateId||namedReview.DeclarationHash!=namedProfile.DeclarationHash)throw new InvalidOperationException("The reviewed source operand declaration changed.");
                var bound=ReviewedNamedFormulaInputs.Bind(namedProfile,namedReview.Bindings,source,row,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));
                RequireReviewedNamedSourceRoles(original,source,namedReview.Bindings);
                namedContext=bound.Context;namedLeaves=namedReview.Bindings.ToDictionary(b=>b.ColumnIndex,b=>new NamedRecurringSourceBoundary(b.Key,namedContext.Values[b.Key]));
                if(namedLeaves.ContainsKey(original.PayrollDaysColumnIndex)||namedLeaves.ContainsKey(original.PayableDaysColumnIndex)||namedLeaves.ContainsKey(original.PresentDaysColumnIndex??-1))throw new InvalidOperationException("A named operand cannot replace attendance.");
            }
            var resolved = new List<ResolvedComponent>();
            foreach (var definition in definitions)
            {
                var matches = masters.Where(master => definition.ExistingComponentId is { Length: > 0 }
                    ? Text(master, "id") == definition.ExistingComponentId : Text(master, "code").Equals(definition.Code, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || definition.IsPeriodAdjustment || definition.ExemptWhenSourceZero || definition.ExpectedColumnIndex is not int column)
                    throw new InvalidOperationException("Every source output needs one existing recurring Settings component; period transactions and cache exemptions are not corrected here.");
                var master = matches[0];
                if (ReusedComponentDifferences(master, definition).Any(difference => difference is not ("Priority" or "PayType")))
                    throw new InvalidOperationException("Shared component semantics differ from the reviewed source; no global master is changed.");
                definition.Priority = int.Parse(Text(master, "priority"), CultureInfo.InvariantCulture); definition.PayType = Text(master, "payType");
                var cell = source.Rows.Single(cells => cells.SourceRow == row.SourceRow).Cells.ElementAtOrDefault(column);
                definition.RowFormulas.Clear();
                if (!string.IsNullOrWhiteSpace(cell?.FormulaText))
                {
                    if (definition.CalculationType != "Formula" || definition.ProRata || definition.SourceColumnIndex.HasValue)
                    {
                        // Proven Fixed/Daily recurring mappings retain their original rule;
                        // InspectTransferFormula verifies exact supported source equivalence.
                    }
                    else definition.Formula = namedLeaves is null?TranslateTransferFormula(cell.FormulaText, original, source, row):TranslateNamedFormulaWithEvidence(cell.FormulaText,original,source,row,namedLeaves).Formula;
                }
                var formula = namedLeaves is null?InspectTransferFormula(original, source, row, definition):InspectNamedTransferFormula(original,source,row,definition,namedLeaves);
                if (formula.Status == "Blocked") throw new InvalidOperationException(definition.Code + ": " + formula.Reason);
                resolved.Add(new(definition, Text(master, "id"), false));
            }
            foreach (var column in source.Columns.Where(column => original.SourceColumnRoles.GetValueOrDefault(column.ColumnIndex, column.Kind) is "earning" or "deduction" or "employer"))
                if (resolved.Count(c => c.Definition.ExpectedColumnIndex == column.ColumnIndex) != 1)
                    throw new InvalidOperationException("Every classified source financial output requires one exact comparison mapping.");
            aggregates.AddRange(ReviewCorrectionAggregates(source,row,original,resolved));
            var compatible = templates.Where(template => (namedProfile is null||Text(template,"id")==namedProfile.TemplateId)&&LocationTemplateDifferences(template, resolved).Count == 0)
                .OrderBy(template => Text(template, "id") == old.SalaryStructureId ? 0 : 1).ThenBy(template => Text(template, "id"), StringComparer.Ordinal).ToArray();
            if (compatible.Length == 0) throw new InvalidOperationException("No existing Settings template exactly preserves the current source formulas. Review assignment-local rules or an explicitly approved template; no duplicate global template is created.");
            var template = compatible[0];
            if(Text(template,"id")!=old.SalaryStructureId)
            {
                var decision=original.TemplateDecisions.SingleOrDefault(d=>d.ExistingTemplateId==Text(template,"id"));
                var scope=CompareTemplateScope(Text(template,"id"),ReusedTemplateRulesSignature(template,resolved),original,clientId,templateScopes??[],decision);
                if(original.TemplateScopeReviewVersion!=1||!scope.Compatible)
                    throw new InvalidOperationException("The alternate existing template needs explicit compatible location/category/day/policy scope review: "+scope.Reason);
            }
            var cells = source.Rows.Single(cells => cells.SourceRow == row.SourceRow);
            var arithmetic = CalculateRow(clientId, original, resolved, cells, row, source.Columns.Where(c => c.BlankAsZero).Select(c => c.ColumnIndex).ToHashSet(), retainedAnnualCtc: old.AnnualCtc,namedInputs:namedContext);
            arithmetic.Preview.EmployeeId = employee.Id; arithmetic.Preview.RowId = row.Id;
            if(original.PresentDaysColumnIndex.HasValue&&arithmetic.Preview.PresentDays!=previous.PresentDays)
                throw new InvalidOperationException("Source worked-day meaning differs from known native actual presence. This lane retains actual presence; review payable separately instead of replacing it.");
            arithmetic.Preview.PresentDays=previous.PresentDays;
            var proofPlan=new Plan{Batch=batch,Source=source};
            var proofRow=new PlannedRow{RowId=row.Id,SourceRow=row.SourceRow,Employee=employee,Preview=arithmetic.Preview,Components=resolved};
            proofPlan.Rows.Add(proofRow);proofPlan.NativeAttendanceInputs[employee.Id]=nativeInput;
            var dayProof=ProveAttendanceInput(proofPlan,proofRow,original,clientId,newSourceHash,reviewedLocationAllocation:true);
            if(dayProof.Status=="Blocked")throw new InvalidOperationException(dayProof.Reason);
            if (!TransferAmountsMatch(arithmetic.Preview, row, resolved.Count) || arithmetic.Issues.Any(issue => issue.Severity is "Error" or "Review"))
                throw new InvalidOperationException("Current source formula arithmetic does not exactly match all saved financial lines and supplied totals.");
            var scopes = JsonSerializer.Deserialize<Dictionary<string, string>>(old.ComponentScopesJson, Json)!;
            if (!scopes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(resolved.Select(c => c.Id))) throw new InvalidOperationException("The immutable assignment component scopes do not cover the exact source rules.");
            var rulesHash = LocationSettingsRulesHash(template, masters);
            if(namedProfile is not null)rulesHash=ReviewedNamedFormulaInputs.RulesHash(rulesHash,namedProfile.DeclarationHash);
            var same = Text(template, "id") == old.SalaryStructureId && arithmetic.Preview.AnnualCtc == old.AnnualCtc
                && JsonNode.DeepEquals(JsonNode.Parse(old.SalaryInputsJson), JsonSerializer.SerializeToNode(arithmetic.Inputs, Json)) && rulesHash == old.RulesHash
                && reboundParts?.Contains(previous.SourcePartKey)!=true;
            var assignmentWitness=new { kind = "ReviewedPeriodCorrection", clientId, employee.Id, batch.Month,
                previous = old.Id, source = newSourceHash, rulesHash, inputs = arithmetic.Inputs.OrderBy(pair => pair.Key), annual = arithmetic.Preview.AnnualCtc };
            var assignmentId = same ? old.Id : Hash(JsonSerializer.Serialize(namedContext is not null?(object)new{Witness=assignmentWitness,NamedSignature=ReviewedNamedFormulaInputs.Signature("",namedContext)}:reboundParts?.Contains(previous.SourcePartKey)==true?
                (object)new{Witness=assignmentWitness,ReboundCurrentRowId=row.Id}:assignmentWitness, Json))[..32];
            if (!same) newAssignments.Add(old with { Id = assignmentId, SalaryStructureId = Text(template, "id"), AnnualCtc = arithmetic.Preview.AnnualCtc,
                SalaryInputsJson = JsonSerializer.Serialize(arithmetic.Inputs, Json), RulesHash = rulesHash, SourceHash = newSourceHash,
                ConfigurationEffectiveFrom = DateTime.UtcNow.Date, CreatedBy = "Reviewed source-period correction", Status = ReviewedLocationCorrectionOrigins.Status, CreatedAt = default });
            var part = new PayrollLocationPart { PartKey = previous.SourcePartKey, AssignmentId = assignmentId, WorkLocationId = old.WorkLocationId,
                WorkState = old.WorkState, SalaryStructureId = Text(template, "id"), AnnualCtc = arithmetic.Preview.AnnualCtc, SalaryInputs = arithmetic.Inputs,
                ComponentScopes = scopes, PayrollDays = arithmetic.Preview.PayrollDays, PresentDays = arithmetic.Preview.PresentDays, PayableDays = arithmetic.Preview.PayableDays,NamedInputs=namedContext };
            period.Parts.Add(part); rows.Add(arithmetic.Preview);
            newAllocations.Add(previous with { AssignmentId = assignmentId, PayrollDays = part.PayrollDays, PresentDays = part.PresentDays, PayableDays = part.PayableDays, SourceHash = newSourceHash });
        }
        var references = allocations.Select(a => a.ReferenceAssignmentId).Distinct(StringComparer.Ordinal).ToArray();
        if (references.Length != 1 || references[0] is null) throw new InvalidOperationException("The existing complete location group needs one explicit monthly reference assignment.");
        var oldReference = references[0];
        period.ReferenceAssignmentId = newAllocations.Single(a => allocations.Single(old => old.SourcePartKey == a.SourcePartKey).AssignmentId == oldReference).AssignmentId;
        for (var i = 0; i < newAllocations.Count; i++) newAllocations[i] = newAllocations[i] with { ReferenceAssignmentId = period.ReferenceAssignmentId };
        return new(period, rows, newAssignments, newAllocations,aggregates);
    }

    internal static IReadOnlyList<ExcelPayrollLocationCorrectionAggregate> ReviewCorrectionAggregates(ExcelPayslipCalculationSource source,
        ExcelPayslipRow row,ExcelPayrollSetupRequest request,IReadOnlyList<ResolvedComponent> components)
    {
        var proofs=new List<ExcelPayrollLocationCorrectionAggregate>();
        var cells=source.Rows.Single(captured=>captured.SourceRow==row.SourceRow);
        foreach(var aggregate in source.Columns.Where(column=>column.Kind is "earning" or "deduction" or "employer"
            &&request.SourceColumnRoles.GetValueOrDefault(column.ColumnIndex,column.Kind)!=column.Kind))
        {
            if(request.SourceColumnRoles.GetValueOrDefault(aggregate.ColumnIndex)!="ignore")
                throw new InvalidOperationException("A saved financial output cannot be relabelled without an exact captured aggregate proof.");
            var cell=cells.Cells.ElementAtOrDefault(aggregate.ColumnIndex);
            var formula=cell?.FormulaText??"";
            var direct=Regex.Replace(formula.ToUpperInvariant(),@"[\s$()=]","").TrimStart('+');
            if(!Regex.IsMatch(direct,@"\A[A-Z]{1,3}[1-9][0-9]*(?:\+[A-Z]{1,3}[1-9][0-9]*)+\z"))
                throw new InvalidOperationException("An ignored salary output must be a captured direct sum of its separately mapped original source components; cached equality is insufficient.");
            var references=Regex.Matches(direct,@"([A-Z]{1,3})([1-9][0-9]*)").Select(match=>(Column:TransferColumn(match.Groups[1].Value),Row:int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture))).ToArray();
            if(references.Any(reference=>reference.Row!=row.SourceRow)||references.Select(reference=>reference.Column).Distinct().Count()!=references.Length)
                throw new InvalidOperationException("A source aggregate must reference distinct original outputs from this exact employee row.");
            foreach(var reference in references)
            {
                var mapped=components.Where(component=>component.Definition.ExpectedColumnIndex==reference.Column).ToArray();
                var category=aggregate.Kind=="earning"?"Earning":aggregate.Kind=="deduction"?"Deduction":"Benefit";
                if(mapped.Length!=1||mapped[0].Definition.Category!=category
                    ||request.SourceColumnRoles.GetValueOrDefault(reference.Column,source.Columns.Single(column=>column.ColumnIndex==reference.Column).Kind)!=aggregate.Kind)
                    throw new InvalidOperationException("Every original aggregate dependency needs one separately reviewed component of the same salary classification.");
            }
            var evaluated=ExcelPayslipCalculationService.EvaluateCapturedCellAtInputBoundary(source,TransferLetter(aggregate.ColumnIndex)+row.SourceRow,null,requireExplicitCells:true);
            var amount=ReadNumber(cells,aggregate.ColumnIndex);
            if(evaluated.Value!=amount||references.Sum(reference=>ReadNumber(cells,reference.Column))!=amount||evaluated.Traces.Count!=0)
                throw new InvalidOperationException("The captured aggregate formula, original output values and saved amount differ; no source output is ignored.");
            proofs.Add(new(row.Id,aggregate.ColumnIndex,formula,references.Select(reference=>reference.Column).ToArray(),amount,
                Hash(JsonSerializer.Serialize(new{row.Id,row.SourceRow,aggregate.ColumnIndex,formula,amount,references},Json))));
        }
        return proofs;
    }

    internal static void RequireDurableLocationRecoveryLink(ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,LocationAllocationStorageRow allocation)
    {
        var rows=batch.Rows.Where(row=>row.Id==allocation.SourceRowId).ToArray();
        if(rows.Length!=1||allocation.SourceBatchId!=batch.Id||allocation.ClientId!=batch.ClientId||allocation.PayPeriod!=batch.Month)
            throw new InvalidOperationException("The recovery allocation does not identify one exact current source row/client/month.");
        var row=rows[0];var links=(batch.ProfileLinks??[]).Where(link=>link.RowId==row.Id).ToArray();
        if(links.Length!=1||links[0].EmployeeId!=allocation.EmployeeId||links[0].SourceRow!=row.SourceRow
            ||links[0].SourceIdentityHash!=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source))
            throw new InvalidOperationException("A current strong durable ProfileLink is required independently of legacy allocation IDs; code/name matching cannot recover orphaned lineage.");
    }

    internal sealed record CorrectionFinancialLine(string Code, string Category, decimal Amount);
    internal static IReadOnlyList<CorrectionFinancialLine> ReadCorrectionFinancial(PayRunEmployee row) => JsonNode.Parse(row.DetailsJson)!.AsArray()
        .Where(node => node is not null && node["Category"]?.ToString() != "Summary")
        .Select(node => new CorrectionFinancialLine(node!["Id"]!.ToString(), node["Category"]!.ToString(), decimal.Parse(node["amount"]!.ToString(), CultureInfo.InvariantCulture))).ToArray();

    internal static void RequireLocationCorrectionParity(PayRunEmployee native, IReadOnlyList<ExcelPayrollSetupPreviewRow> source)
    {
        var expected = source.SelectMany(row => row.Lines).GroupBy(line => line.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (Amount: group.Sum(line => line.SourceAmount ?? throw new InvalidOperationException("A source output comparison is missing.")), Category: group.First().Category), StringComparer.OrdinalIgnoreCase);
        var actual = ReadCorrectionFinancial(native).ToDictionary(line => line.Code, StringComparer.OrdinalIgnoreCase);
        if (!expected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual.Keys)
            || expected.Any(pair => actual[pair.Key].Amount != pair.Value.Amount || actual[pair.Key].Category != pair.Value.Category)
            || native.GrossPay + native.OneTimeEarnings != source.Sum(row => row.Gross ?? throw new InvalidOperationException())
            || native.StatutoryDeductions + native.OneTimeDeductions != source.Sum(row => row.Deductions ?? throw new InvalidOperationException())
            || actual.Values.Where(line => line.Category == "Benefit").Sum(line => line.Amount) != source.Sum(row => row.Benefits ?? throw new InvalidOperationException())
            || native.NetPay != source.Sum(row => row.Net ?? throw new InvalidOperationException())
            || native.PresentDays != source.Sum(row => row.PresentDays) || native.PayableDays != source.Sum(row => row.PayableDays))
            throw new InvalidOperationException("Actual Employee payroll does not exactly preserve every source earning/deduction/employer line, supplied total, net and known attendance input.");
    }
}
