using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    internal const string NativeRecaptureFlag = "Payroll:NativeDraftCurrencyRecaptureEnabled";
    private sealed record CurrencyRecaptureReview(int Version, string Kind, int ActorId, int ClientId, int RunId,
        string PayPeriod, string PlanHash, string RosterHash, string SavedStateHash, string NativeInputHash,
        string SourceHash, string ProfileHash, DateTime ExpiresAtUtc);
    private sealed record CurrencyRecapturePlan(PayRun Run, CurrencyProcessingContext Context,
        PayrollCalculationTrace[] Traces, string PlanHash, string RosterHash, string SavedStateHash, string NativeInputHash,
        CurrencyRecaptureBusinessWitness Preservation);
    private sealed record CurrencyRecaptureBusinessWitness(string SalaryAndAccountingHash, string StatusAndPaymentHash,
        string TaxAndAdjustmentsHash, string SourceAndMailHash, string OriginalTraceOutputsAndIdsHash);
    private sealed record CurrencyRecaptureEnvelope(int Version, string Kind, string Payload);
    private sealed record CurrencyRecaptureArchive(int Version, int ActorId, int ClientId, int RunId, string RequestId,
        string PlanHash, string OriginalStateHash, PayrollCalculationTrace[] Traces);
    private sealed record CurrencyArchiveReference(string Code, string Hash);
    private sealed record CurrencyRecaptureReceipt(int Version, int ActorId, int ClientId, int RunId,
        string Fingerprint, NativeCurrencyRecaptureResult Result, CurrencyArchiveReference[] Archives);

    private void RequireCurrencyRecapture(AuthUser actor, int clientId)
    {
        if (!CanManageReviewedProfessionalTax(actor, clientId))
            throw new UnauthorizedAccessException("Native Draft recapture requires an active global payroll administrator with settings and employee management for this client.");
        if (!configuration.GetValue<bool>(NativeRecaptureFlag) || !configuration.GetValue<bool>(NativeCurrencyFlag)
            || !SelectedRunRuntime.Read(configuration).Safe || nativeCurrencyProtector is null)
            throw new InvalidOperationException("Reviewed Draft currency recapture is disabled, or the isolated payroll runtime/protection is unavailable. No payroll data was changed.");
    }

    private static async Task RequireCurrencyRecaptureWorkflowAsync(MySqlConnection db, MySqlTransaction tx, int id)
    {
        // Missing workflow metadata also fails closed. Read in this transaction,
        // including on replay; a direct repository call cannot bypass the route.
        var rules = await db.QueryAsync<WorkflowActionRule>("SELECT HttpMethod,PathPattern FROM workflow_action_rules WHERE IsActive=TRUE", transaction: tx);
        var paths = new[] { $"/api/pay-runs/{id}/currency-recapture", $"/api/pay-runs/{id}/process-selected" };
        if (rules.Any(r => r.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && paths.Any(p => WorkflowActionMiddleware.Match(r.PathPattern, p) is not null))
            || await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ResourceStates WHERE ResourceType='PayRun' AND ResourceId=@Id", new { Id = id.ToString(CultureInfo.InvariantCulture) }, tx) != 0)
            throw new InvalidOperationException("A configured payroll workflow applies to this run. Currency recapture cannot bypass its reviewed transition.");
    }

    public async Task<NativeCurrencyRecapturePreview> PreviewNativeCurrencyRecaptureAsync(int runId, int clientId,
        string payPeriod, AuthUser actor)
    {
        RequireCurrencyRecapture(actor, clientId);
        await using var db = CreateConnection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        await RequireCurrencyRecaptureWorkflowAsync(db, tx, runId);
        var plan = await BuildCurrencyRecapturePlanAsync(db, tx, runId, clientId, payPeriod, actor, locked: false);
        var expiry = DateTime.UtcNow.AddMinutes(30);
        var review = new CurrencyRecaptureReview(1, "DraftCurrencyReview", actor.Id, clientId, runId, payPeriod,
            plan.PlanHash, plan.RosterHash, plan.SavedStateHash, plan.NativeInputHash,
            plan.Context.Profile.SourceHash, plan.Context.Profile.ProfileHash, expiry);
        var token = nativeCurrencyProtector!.ProtectNativeCurrencyRecapture(JsonSerializer.Serialize(review, CurrencyJson));
        await tx.RollbackAsync();
        return new(runId, clientId, payPeriod, plan.Context.Captured.Count, plan.Traces.Length,
            plan.PlanHash, review.SourceHash, review.ProfileHash, expiry, token);
    }

    public async Task<NativeCurrencyRecaptureResult> ApplyNativeCurrencyRecaptureAsync(int runId,
        NativeCurrencyRecaptureRequest command, AuthUser actor)
    {
        RequireCurrencyRecapture(actor, command.ClientId);
        if (!command.Confirmed || !Guid.TryParse(command.RequestId, out var requestGuid)
            || string.IsNullOrWhiteSpace(command.ReviewToken) || command.ReviewToken.Length > 16000)
            throw new InvalidOperationException("Preview and explicitly confirm the entire Draft using a stable request ID.");
        var review = ReadCurrencyRecaptureReview(command.ReviewToken);
        if (review.ActorId != actor.Id || review.ClientId != command.ClientId || review.RunId != runId || review.PayPeriod != command.PayPeriod)
            throw new InvalidOperationException("The protected Draft review belongs to a different actor, client, period or run.");
        var requestId = requestGuid.ToString("N");
        var code = "pcrr:" + requestId;
        var fingerprint = CurrencyHash(new { actor.Id, runId, command.ClientId, command.PayPeriod, review.PlanHash });
        await using var db = CreateConnection(); await db.OpenAsync();
        var lockName = await db.ExecuteScalarAsync<string>("SELECT CONCAT('payroll:',MD5(DATABASE()),':',@Id)", new { Id = runId });
        if (await db.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@Name,0)", new { Name = lockName }) != 1)
            throw new InvalidOperationException("This exact payroll run is locked. No second native recapture was started.");
        try
        {
            await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await RequireCurrencyRecaptureWorkflowAsync(db, tx, runId);
            var prior = await ReadRecaptureValueAsync<CurrencyRecaptureReceipt>(db, tx, command.ClientId, code, "RecaptureReceipt");
            if (prior is not null)
            {
                if (prior.Version != 1 || prior.ActorId != actor.Id || prior.RunId != runId || prior.ClientId != command.ClientId || prior.Fingerprint != fingerprint)
                    throw new InvalidOperationException("This request ID already belongs to a different reviewed recapture.");
                await RequireCurrencyRecaptureDraftAsync(db, tx, runId, command.ClientId, command.PayPeriod);
                var current = await ReadNativeCurrencyStageAsync(db, tx, command.ClientId, runId,
                    await db.ExecuteScalarAsync<int>("SELECT MIN(EmployeeId) FROM payrunemployees WHERE PayRunId=@Id AND ClientId=@ClientId", new { Id = runId, command.ClientId }, tx));
                if (current is null) throw new InvalidOperationException("The protected native recapture is no longer current.");
                await VerifyCurrencyRecaptureArchivesAsync(db, tx, prior, requestId);
                await tx.RollbackAsync(); return prior.Result with { Replayed = true };
            }
            if (review.ExpiresAtUtc <= DateTime.UtcNow || review.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(31))
                throw new InvalidOperationException("The Draft review expired. Preview again; no payroll data was changed.");
            var plan = await BuildCurrencyRecapturePlanAsync(db, tx, runId, command.ClientId, command.PayPeriod, actor);
            if (plan.PlanHash != review.PlanHash || plan.RosterHash != review.RosterHash || plan.SavedStateHash != review.SavedStateHash
                || plan.NativeInputHash != review.NativeInputHash || plan.Context.Profile.SourceHash != review.SourceHash
                || plan.Context.Profile.ProfileHash != review.ProfileHash)
                throw new InvalidOperationException("Saved payroll, trace epoch, roster, source, policy or native input changed. Refresh the full Draft preview; no payroll data was changed.");

            // Authenticate and retain every complete original trace before the
            // first annotation. No original output, amount, ID or history row is
            // deleted/reinserted. The archive and annotations commit atomically.
            var archives = new List<CurrencyArchiveReference>(); var chunkIndex = 0;
            foreach (var chunk in plan.Traces.Chunk(100))
            {
                var archiveCode = $"pcra:{requestId}:{chunkIndex++}";
                var archive = new CurrencyRecaptureArchive(1, actor.Id, command.ClientId, runId, requestId,
                    plan.PlanHash, plan.SavedStateHash, chunk);
                await WriteRecaptureValueAsync(db, tx, command.ClientId, archiveCode, "TraceArchive", archive);
                archives.Add(new(archiveCode, CurrencyHash(archive)));
            }
            foreach (var captured in plan.Context.Captured.Values)
                await AnnotateCurrencyRecaptureTracesAsync(db, tx, plan.Context, captured, plan.Traces);
            await FinishCurrencyProcessingAsync(db, tx, runId, plan.Context);
            var state = await ReadCurrencyRunStateAsync(db, tx, command.ClientId, runId, command.PayPeriod);
            var epoch = NativePayslipCurrencyStage.Capture(plan.Context.Profile, state,
                plan.Context.Captured.Values.Select(c => c.Observation).ToArray()).ProcessingEpochHash;
            var after = await ReadCurrencyRecaptureBusinessWitnessAsync(db, tx, plan.Run, plan.Context.Profile.SourceBatchId);
            if (after != plan.Preservation)
                throw new InvalidOperationException("Payroll, accounting, status/payment, tax/adjustment, original trace output or source/mail history changed. The entire recapture was rolled back.");
            var result = new NativeCurrencyRecaptureResult(runId, plan.Context.Captured.Count, plan.Traces.Length, epoch, requestId, false,
                new(true, true, true, true, true));
            var receipt = new CurrencyRecaptureReceipt(1, actor.Id, command.ClientId, runId, fingerprint, result, archives.ToArray());
            await WriteRecaptureValueAsync(db, tx, command.ClientId, code, "RecaptureReceipt", receipt);
            await VerifyCurrencyRecaptureArchivesAsync(db, tx, receipt, requestId);
            // Also authenticate the exact persisted pointer/receipt before commit.
            await ReadNativeCurrencyStageAsync(db, tx, command.ClientId, runId, plan.Context.Captured.Keys.First());
            await tx.CommitAsync(); return result;
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@Name)", new { Name = lockName }); }
    }

    private CurrencyRecaptureReview ReadCurrencyRecaptureReview(string token)
    {
        try
        {
            var plain = nativeCurrencyProtector!.UnprotectNativeCurrencyRecapture(token);
            var value = plain is null ? null : JsonSerializer.Deserialize<CurrencyRecaptureReview>(plain, CurrencyJson);
            return value is { Version: 1, Kind: "DraftCurrencyReview" } && value.ActorId > 0 && value.ClientId > 0 && value.RunId > 0
                && new[] { value.PlanHash, value.RosterHash, value.SavedStateHash, value.NativeInputHash, value.SourceHash, value.ProfileHash }
                    .All(h => h is { Length: 64 } && h.All(Uri.IsHexDigit)) ? value : throw new InvalidOperationException();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidOperationException("The Draft recapture review cannot be authenticated. No payroll data was changed.", e); }
    }

    private static async Task<PayRun> RequireCurrencyRecaptureDraftAsync(MySqlConnection db, MySqlTransaction tx,
        int id, int clientId, string period)
    {
        var run = await db.QuerySingleOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id AND ClientId=@ClientId AND PayPeriod=@Period", new { Id = id, ClientId = clientId, Period = period }, tx);
        if (run is null || run.Status != "Draft" || run.RunType != "Regular"
            || await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { ClientId = clientId }, tx) != 1
            || await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE PayRunId=@Id AND (ClientId<>@ClientId OR IsSkipped=TRUE OR PaymentStatus<>'Pending' OR PaymentDate IS NOT NULL)", new { Id = id, ClientId = clientId }, tx) != 0)
            throw new InvalidOperationException("Only one complete active-client regular Draft with all payments pending can be reviewed. Existing status, approval and payment history is retained.");
        return run;
    }

    private async Task<CurrencyRecapturePlan> BuildCurrencyRecapturePlanAsync(MySqlConnection db, MySqlTransaction tx,
        int id, int clientId, string period, AuthUser actor, bool locked = true)
    {
        var run = await RequireCurrencyRecaptureDraftAsync(db, tx, id, clientId, period);
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code", new { ClientId = clientId, Code = CurrencyPointerCode(id) }, tx) != 0)
            throw new InvalidOperationException("This Draft already has a currency epoch. Replay its original request; replacing an epoch requires a separately reviewed transition.");
        var request = JsonSerializer.Deserialize<CreatePayRunRequest>(run.RequestJson, CurrencyJson)
            ?? throw new InvalidOperationException("The original native payroll request is unavailable.");
        if (request.ClientId != clientId || request.PayPeriod != period || request.RunType != "Regular" || request.TotalWorkingDays != run.TotalWorkingDays
            || request.TotalWorkingDays is < 1 or > 31 || !DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new InvalidOperationException("The original regular payroll request/period is invalid. No payroll request is reconstructed.");
        var saved = (await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@Id ORDER BY Id", new { Id = id }, tx)).ToArray();
        var ids = saved.Select(r => r.EmployeeId).ToArray();
        if (saved.Length is < 1 or > 5000 || ids.Distinct().Count() != ids.Length || saved.Any(r => r.ClientId != clientId)
            || request.IncludedEmployeeIds is null || request.ExcludedEmployeeIds is null || request.AdjustmentIds is null
            || request.IncludedEmployeeIds.Count > 0 && !request.IncludedEmployeeIds.ToHashSet().SetEquals(ids)
            || request.ExcludedEmployeeIds.Intersect(ids).Any())
            throw new InvalidOperationException("Review the complete bounded saved Draft roster; no partial or duplicate employee recapture is allowed.");
        var traces = (await db.QueryAsync<PayrollCalculationTrace>("SELECT *,CAST(InputJson AS CHAR) InputJson,CAST(OutputJson AS CHAR) OutputJson FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY Id", new { Id = id }, tx)).ToArray();
        if (traces.Length is < 1 or > 100000 || traces.Any(t => !ids.Contains(t.EmployeeId)
            || JsonNode.Parse(t.InputJson)?["nativeCurrencyContext"] is not null || JsonNode.Parse(t.InputJson)?["nativeCurrencyStage"] is not null
            || JsonNode.Parse(t.OutputJson)?["nativeCurrencyStage"] is not null))
            throw new InvalidOperationException("Original complete native trace evidence is missing, foreign or already annotated.");
        var employees = (await db.QueryAsync<PayRunSourceEmployee>("SELECT e.*,c.Name ClientName,COALESCE(w.State,'') WorkState FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND "
            + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + " ORDER BY e.Id", new { ClientId = clientId, Ids = ids, PayPeriod = period }, tx)).ToList();
        if (employees.Count != ids.Length) throw new InvalidOperationException("Saved employees are missing, foreign or no longer eligible for this period.");
        var setup = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        await LoadEmployeeTablesAsync(db, tx, employees, period);
        await LoadPfPolicyPeriodsAsync(db, tx, employees, period, setup);
        var attendance = (await GetSelectedPayRunAttendanceAsync(db, tx, clientId, period, ids)).ToDictionary(r => r.EmployeeId);
        var adjustments = (await db.QueryAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayRunId=@Id AND Status='Applied' ORDER BY Id", new { ClientId = clientId, Id = id }, tx)).ToArray();
        if (adjustments.Any(a => !ids.Contains(a.EmployeeId))) throw new InvalidOperationException("An applied adjustment is outside the complete saved Draft roster.");
        var context = await BeginCurrencyProcessingAsync(db, tx, id, request, "Regular", setup, employees, attendance, locked)
            ?? throw new InvalidOperationException("Select an explicitly reviewed whole-rupee source design for this same payroll period before recapture.");
        var blocking = ValidatePayRunInputs(id, request, "Regular", employees, attendance, setup).Where(i => i.IsBlocking).ToArray();
        if (blocking.Length != 0) throw new InvalidOperationException("Native payroll validation failed: " + string.Join(" ", blocking.Select(i => i.Message)));
        foreach (var employee in employees)
        {
            var old = saved.Single(r => r.EmployeeId == employee.Id);
            var entry = attendance.GetValueOrDefault(employee.Id) ?? throw new InvalidOperationException("Native attendance is missing for a saved employee.");
            var days = AttendanceWorkingDays(entry, request.TotalWorkingDays);
            var present = ClampKnownPresence(entry.PresentDays, days); var payable = NativePayableUnits(entry, days);
            employee.AttendancePolicySnapshot = entry.PolicySnapshot; employee.PayableOnlyAttendance = entry.PayableOnlyEvidence;
            List<PayrollSimulationLine> raw = []; List<object> tax = [];
            var applied = adjustments.Where(a => a.EmployeeId == employee.Id).ToArray();
            var row = BuildEmployeeWithCurrencyTrace(id, employee, setup, period, days, present, payable, applied, 0, 0, -1, false, raw);
            var tds = await CalculateMonthlyTdsAsync(db, tx, employee, row, period, persistSnapshot: false, recordInputs: tax.Add, setupJson: setup);
            if (tds.Calculated)
            {
                raw.Clear();
                row = BuildEmployeeWithCurrencyTrace(id, employee, setup, period, days, present, payable, applied, 0, 0, tds.Amount, false, raw, tds.ComponentCode);
                row = WithSourceTdsEvidence(row, tds);
            }
            var changed = new List<string>();
            if (row.MonthlyGross != old.MonthlyGross) changed.Add("monthly gross");
            if (row.GrossPay != old.GrossPay) changed.Add("gross earnings");
            if (row.StatutoryDeductions != old.StatutoryDeductions) changed.Add("statutory deductions");
            if (row.OneTimeEarnings != old.OneTimeEarnings || row.OneTimeDeductions != old.OneTimeDeductions) changed.Add("adjustments");
            if (row.NetPay != old.NetPay || row.ManualTds != old.ManualTds) changed.Add("net/TDS");
            if (row.PresentDays != old.PresentDays || row.PayableDays != old.PayableDays) changed.Add("attendance");
            if (!NativeRecaptureJson.Equivalent(row.DetailsJson, old.DetailsJson))
                changed.Add("component details " + RecaptureDifferencePath(JsonNode.Parse(row.DetailsJson), JsonNode.Parse(old.DetailsJson), "$"));
            if (changed.Count != 0)
                throw new InvalidOperationException($"Employee {old.EmployeeCode}: actual native calculation differs from saved {string.Join(", ", changed)}. No saved salary is replaced.");
            row.Id = old.Id;
            await ReviewCurrencyProcessingRowAsync(db, tx, context, employee, row, days, present, payable, applied, tax, raw, locked);
            RequireRecaptureOriginalTraceCoverage(old, traces.Where(t => t.EmployeeId == employee.Id).ToArray());
        }
        var rosterHash = CurrencyHash(saved.Select(r => new { r.Id, r.EmployeeId, r.ClientId }).ToArray());
        var stateHash = CurrencyHash(new { run, saved, traces });
        var inputHash = CurrencyHash(context.Captured.Values.OrderBy(c => c.Saved.EmployeeId).Select(c => new
        { c.Saved, c.Observation.OrdinaryEvidenceHash, c.Observation.IndependentSourceProofHash, c.Observation.Result }).ToArray());
        var preservation = await ReadCurrencyRecaptureBusinessWitnessAsync(db, tx, run, context.Profile.SourceBatchId);
        var planHash = CurrencyHash(new { ActorId = actor.Id, RunId = id, clientId, period, rosterHash, stateHash, inputHash, context.Profile, preservation });
        return new(run, context, traces, planHash, rosterHash, stateHash, inputHash, preservation);
    }

    private static async Task<CurrencyRecaptureBusinessWitness> ReadCurrencyRecaptureBusinessWitnessAsync(
        MySqlConnection db, MySqlTransaction tx, PayRun run, string batchId)
    {
        var key = new { run.Id, run.ClientId, BatchId = batchId };
        var accounting = new List<object>();
        foreach (var table in new[] { "payrunemployees", "payrunemployeelines", "payrun_step_logs", "payroll_validation_issues", "payroll_reconciliation_results" })
            accounting.Add((await db.QueryAsync($"SELECT * FROM `{table}` WHERE PayRunId=@Id ORDER BY Id", key, tx)).ToArray());
        var header = (await db.QueryAsync("SELECT * FROM payruns WHERE Id=@Id AND ClientId=@ClientId", key, tx)).ToArray();
        var tax = (await db.QueryAsync("SELECT * FROM tax_computation_snapshots WHERE pay_run_id=@Id ORDER BY id", key, tx)).ToArray();
        var adjustments = (await db.QueryAsync("SELECT * FROM payrolladjustments WHERE PayRunId=@Id ORDER BY Id", key, tx)).ToArray();
        var source = (await db.QueryAsync("SELECT * FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId", key, tx)).ToArray();
        var formulas = (await db.QueryAsync("SELECT * FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@BatchId", key, tx)).ToArray();
        var imports = (await db.QueryAsync("SELECT * FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId ORDER BY id", key, tx)).ToArray();
        var outputs = (await db.QueryAsync(@"SELECT Id,PayRunId,EmployeeId,EmployeeCode,ComponentCode,ComponentName,ParentComponentCode,
TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,CAST(OutputJson AS CHAR) OutputJson,CreatedAt
FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY Id", key, tx)).ToArray();
        var externalMail = await ReadRecaptureMailWitnessAsync(db, tx, run.ClientId, batchId);
        return new(CurrencyHash(accounting), CurrencyHash(header), CurrencyHash(new { tax, adjustments }),
            CurrencyHash(new { source, formulas, imports, externalMail }), CurrencyHash(outputs));
    }

    private static void RequireRecaptureOriginalTraceCoverage(PayRunEmployee saved, PayrollCalculationTrace[] traces)
    {
        var details = JsonNode.Parse(saved.DetailsJson)!.AsArray();
        if (traces.Length != details.Count || traces.Select(t => t.ComponentCode).Distinct(StringComparer.Ordinal).Count() != traces.Length)
            throw new InvalidOperationException("Original native traces must cover every saved component and summary exactly once.");
        foreach (var detail in details)
        {
            var code = detail!["Id"]!.GetValue<string>(); var category = detail["Category"]!.GetValue<string>();
            var matches = traces.Where(t => t.ComponentCode == code).ToArray();
            if (matches.Length != 1 || matches[0].CalculatedAmount != detail["amount"]!.GetValue<decimal>()
                || matches[0].RuleUsed != category || !NativeRecaptureJson.Equivalent(JsonNode.Parse(matches[0].OutputJson), detail))
                throw new InvalidOperationException("Original trace output/amount/category differs from its saved native component; no old evidence is replaced.");
        }
    }

    private static string RecaptureDifferencePath(JsonNode? actual, JsonNode? saved, string path)
    {
        if (actual is JsonObject a && saved is JsonObject b)
        {
            foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Order(StringComparer.Ordinal))
                if (!NativeRecaptureJson.Equivalent(a[key], b[key])) return RecaptureDifferencePath(a[key], b[key], path + "." + key);
        }
        if (actual is JsonArray x && saved is JsonArray y)
        {
            if (x.Count != y.Count) return path + ".length";
            for (var index = 0; index < x.Count; index++)
                if (!NativeRecaptureJson.Equivalent(x[index], y[index])) return RecaptureDifferencePath(x[index], y[index], path + "[" + index + "]");
        }
        return path;
    }

    private static async Task AnnotateCurrencyRecaptureTracesAsync(MySqlConnection db, MySqlTransaction tx,
        CurrencyProcessingContext context, CurrencyCapturedRow captured, PayrollCalculationTrace[] originals)
    {
        var row = captured.Saved;
        var state = new NativeCurrencyRunState(context.Profile.ClientId, context.Profile.PayPeriod, originals[0].PayRunId,
            context.TransactionId, [row], []);
        var metadata = new CurrencyTraceContext(1, context.TransactionId, row.NativeRowId, row.EmployeeId, row.SourceRowId, row.SourceRow,
            row.SourceIdentityHash, row.InputSnapshotHash, row.EffectiveTemplateHash, row.TemplateId, row.AnnualCtc,
            row.PayrollDays, row.PresentDays, row.PayableDays, captured.Observation.OrdinaryEvidenceHash, captured.Observation.IndependentSourceProofHash);
        foreach (var trace in originals.Where(t => t.EmployeeId == row.EmployeeId))
        {
            var line = captured.Observation.Result.Lines.SingleOrDefault(l => l.Code == trace.ComponentCode);
            if (line is null) continue; // Saved summaries remain byte-for-byte unchanged.
            var input = JsonNode.Parse(trace.InputJson)!.AsObject();
            input["nativeCurrencyContext"] = JsonSerializer.SerializeToNode(metadata, CurrencyJson);
            input["nativeCurrencyStage"] = JsonSerializer.SerializeToNode(new CurrencyTraceStage(1,
                NativePayslipCurrencyStage.NativeRawStageHash(state, row, line), line.Raw, line.UnroundedEarned!.Value,
                line.Monthly, line.Earned, line.DailyRate.HasValue ? "DailyRate" : line.ProRata ? "Prorated" : "Monthly"), CurrencyJson);
            if (await db.ExecuteAsync("UPDATE payroll_calculation_traces SET InputJson=CAST(@Input AS JSON) WHERE Id=@Id AND PayRunId=@RunId AND EmployeeId=@EmployeeId AND SHA2(CAST(InputJson AS CHAR),256)=@InputHash AND SHA2(CAST(OutputJson AS CHAR),256)=@OutputHash", new
            { Input = input.ToJsonString(), trace.Id, RunId = trace.PayRunId, trace.EmployeeId,
                InputHash = RecaptureTextHash(trace.InputJson), OutputHash = RecaptureTextHash(trace.OutputJson) }, tx) != 1)
                throw new InvalidOperationException("An original trace changed before its reviewed input annotation; the complete recapture was rolled back.");
        }
    }

    private static string RecaptureTextHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task<T?> ReadRecaptureValueAsync<T>(MySqlConnection db, MySqlTransaction tx, int clientId, string code, string kind) where T : class
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE", new { ClientId = clientId, Code = code }, tx);
        if (json is null) return null;
        var envelope = JsonSerializer.Deserialize<CurrencyRecaptureEnvelope>(json, CurrencyJson);
        var plain = envelope is { Version: 1 } && envelope.Kind == kind ? nativeCurrencyProtector!.UnprotectNativeCurrencyRecapture(envelope.Payload) : null;
        return plain is null ? throw new InvalidOperationException("Native recapture history cannot be authenticated.")
            : JsonSerializer.Deserialize<T>(plain, CurrencyJson) ?? throw new InvalidOperationException("Native recapture history is malformed.");
    }

    private async Task WriteRecaptureValueAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string code, string kind, object value)
    {
        var plain = JsonSerializer.Serialize(value, CurrencyJson);
        if (code.Length > 80 || plain.Length > 8_000_000) throw new InvalidOperationException("Native recapture evidence exceeds its bounded storage contract.");
        var envelope = new CurrencyRecaptureEnvelope(1, kind, nativeCurrencyProtector!.ProtectNativeCurrencyRecapture(plain));
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new { ClientId = clientId, Code = code, Json = JsonSerializer.Serialize(envelope, CurrencyJson) }, tx);
    }

    private async Task VerifyCurrencyRecaptureArchivesAsync(MySqlConnection db, MySqlTransaction tx, CurrencyRecaptureReceipt receipt, string requestId)
    {
        var count = 0;
        foreach (var reference in receipt.Archives)
        {
            var archive = await ReadRecaptureValueAsync<CurrencyRecaptureArchive>(db, tx, receipt.ClientId, reference.Code, "TraceArchive");
            if (archive is null || archive.Version != 1 || archive.ActorId != receipt.ActorId || archive.ClientId != receipt.ClientId
                || archive.RunId != receipt.RunId || archive.RequestId != requestId || CurrencyHash(archive) != reference.Hash)
                throw new InvalidOperationException("The complete authenticated original trace archive is missing or changed.");
            count += archive.Traces.Length;
        }
        if (count != receipt.Result.TraceCount) throw new InvalidOperationException("Native recapture cannot omit any original trace history.");
    }
}
