using System.Collections.Immutable;
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
    private const string CurrencyStoredRowsSql = @"SELECT p.Id,p.EmployeeId,p.ClientId,p.GrossPay+p.OneTimeEarnings Gross,
p.StatutoryDeductions+p.OneTimeDeductions Deductions,p.NetPay,p.MonthlyGross,p.PresentDays,p.PayableDays,p.IsSkipped,
CAST(p.DetailsJson AS CHAR) DetailsJson FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
JOIN employees e ON e.Id=p.EmployeeId AND e.ClientId=p.ClientId WHERE p.ClientId=@ClientId AND p.PayRunId=@RunId";
    private sealed class CurrencyStoredRow
    {
        public int Id { get; set; } public int EmployeeId { get; set; } public int ClientId { get; set; }
        public decimal Gross { get; set; } public decimal Deductions { get; set; } public decimal NetPay { get; set; }
        public decimal MonthlyGross { get; set; } public decimal? PresentDays { get; set; } public decimal PayableDays { get; set; }
        public bool IsSkipped { get; set; } public string DetailsJson { get; set; } = "";
    }
    private sealed class CurrencyStoredTrace
    {
        public long Id { get; set; } public int RunId { get; set; } public int EmployeeId { get; set; }
        public string ComponentCode { get; set; } = ""; public decimal CalculatedAmount { get; set; }
        public string InputJson { get; set; } = ""; public string OutputJson { get; set; } = "";
        public string InputHash { get; set; } = ""; public string OutputHash { get; set; } = "";
    }

    private static async Task WriteCurrencyCalculationTracesAsync(MySqlConnection db, MySqlTransaction tx,
        PayRunEmployee row, int payrollDays, CurrencyProcessingContext context, CurrencyCapturedRow captured)
    {
        var traces = new List<PayrollCalculationTrace>();
        var factor = payrollDays <= 0 || row.IsSkipped ? 0 : row.PayableDays / payrollDays;
        using var document = JsonDocument.Parse(captured.Saved.DetailsJson);
        var order = 1;
        var state = new NativeCurrencyRunState(row.ClientId, context.Profile.PayPeriod, row.PayRunId,
            context.TransactionId, [captured.Saved], []);
        var inputs = new CurrencyTraceContext(1, context.TransactionId, row.Id, row.EmployeeId,
            captured.Saved.SourceRowId, captured.Saved.SourceRow, captured.Saved.SourceIdentityHash,
            captured.Saved.InputSnapshotHash, captured.Saved.EffectiveTemplateHash, captured.Saved.TemplateId,
            captured.Saved.AnnualCtc, payrollDays, row.PresentDays, row.PayableDays,
            captured.Observation.OrdinaryEvidenceHash, captured.Observation.IndependentSourceProofHash);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var code = Text(element, "Id"); if (string.IsNullOrWhiteSpace(code)) continue;
            var category = Text(element, "Category");
            var input = JsonSerializer.SerializeToNode(new { row.PayableDays, TotalWorkingDays = payrollDays, row.PresentDays })!.AsObject();
            var output = JsonNode.Parse(element.GetRawText())!.AsObject();
            if (category != "Summary")
            {
                var line = captured.Observation.Result.Lines.Single(l => l.Code == code && l.Category == category);
                input["nativeCurrencyContext"] = JsonSerializer.SerializeToNode(inputs, CurrencyJson);
                output["nativeCurrencyStage"] = JsonSerializer.SerializeToNode(new CurrencyTraceStage(1,
                    NativePayslipCurrencyStage.NativeRawStageHash(state, captured.Saved, line), line.Raw,
                    line.UnroundedEarned!.Value, line.Monthly, line.Earned,
                    line.DailyRate.HasValue ? "DailyRate" : line.ProRata ? "Prorated" : "Monthly"), CurrencyJson);
            }
            traces.Add(new()
            {
                PayRunId = row.PayRunId, EmployeeId = row.EmployeeId, EmployeeCode = row.EmployeeCode,
                ComponentCode = code, ComponentName = Text(element, "Name", code), ParentComponentCode = category == "Summary" ? "" : "GROSS",
                TraceOrder = order++, RuleUsed = category,
                FormulaUsed = element.TryGetProperty("rate", out _) ? "Daily rate * attendance payable days" : element.TryGetProperty("pfPolicy", out _)
                    ? "Sum of effective PF segments; round once after aggregation" : Bool(element, "ProRata", false) ? "MonthlyAmount * PayableDays / TotalWorkingDays" : "Configured amount",
                BaseAmount = NumberFrom(Text(element, "monthlyAmount")), Factor = Bool(element, "ProRata", false) ? factor : 1,
                CalculatedAmount = NumberFrom(Text(element, "amount")), InputJson = input.ToJsonString(), OutputJson = output.ToJsonString()
            });
        }
        if (traces.Count > 0)
            await db.ExecuteAsync(@"INSERT INTO payroll_calculation_traces
(PayRunId,EmployeeId,EmployeeCode,ComponentCode,ComponentName,ParentComponentCode,TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,InputJson,OutputJson)
VALUES(@PayRunId,@EmployeeId,@EmployeeCode,@ComponentCode,@ComponentName,@ParentComponentCode,@TraceOrder,@RuleUsed,@FormulaUsed,@BaseAmount,@Factor,@CalculatedAmount,@InputJson,@OutputJson)", traces, tx);
    }

    private async Task FinishCurrencyProcessingAsync(MySqlConnection db, MySqlTransaction tx, int runId,
        CurrencyProcessingContext? context)
    {
        if (context is null) return;
        if (context.Captured.Count != context.Links.Count || !context.Captured.Keys.ToHashSet().SetEquals(context.Links.Keys))
            throw new InvalidOperationException("A currency receipt cannot omit any employee in the complete native processing scope.");
        var state = await ReadCurrencyRunStateAsync(db, tx, context.Profile.ClientId, runId, context.Profile.PayPeriod);
        var receipt = NativePayslipCurrencyStage.Capture(context.Profile, state, context.Captured.Values.Select(c => c.Observation).ToArray());
        var code = "pcst:" + receipt.ProcessingEpochHash;
        var prior = await ReadProtectedCurrencyValueAsync<NativeCurrencyStageReceipt>(db, tx, context.Profile.ClientId, code);
        if (prior is not null && prior.PayloadHash != receipt.PayloadHash)
            throw new InvalidOperationException("This immutable native processing epoch already has different currency evidence.");
        if (prior is null) await WriteProtectedCurrencyValueAsync(db, tx, context.Profile.ClientId, code, receipt, immutable: true);
        await WriteProtectedCurrencyValueAsync(db, tx, context.Profile.ClientId, CurrencyPointerCode(runId),
            new CurrencyActivePointer(1, context.Profile.ClientId, runId, receipt.ProcessingEpochHash, code, receipt.PayloadHash), immutable: false);
    }

    // Internal read-only adapter for the renderer owner. No native salary/setup
    // calculation is performed. The frozen context is read from original trace
    // envelopes and authenticated by the separately protected receipt.
    internal async Task<NativeCurrencyFrozenRow?> ReadNativeCurrencyStageAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, int runId, int employeeId)
    {
        if (employeeId <= 0) throw new InvalidOperationException("Read one authorized native employee currency row.");
        var verified = await ReadNativeCurrencyRunStageAsync(db, tx, clientId, runId);
        return verified is null ? null : verified.Rows.SingleOrDefault(r => r.EmployeeId == employeeId)
            ?? throw new InvalidOperationException("This employee is outside the complete protected native currency run.");
    }

    // A register export can verify once in its current transaction and project
    // all employees from this immutable receipt, rather than re-scan N times.
    internal async Task<NativeCurrencyStageReceipt?> ReadNativeCurrencyRunStageAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, int runId)
    {
        if (!ReferenceEquals(tx.Connection, db) || clientId <= 0 || runId <= 0)
            throw new InvalidOperationException("Read native currency evidence in the authorized client/run transaction.");
        var protectedReceipt=await ReadExistingCurrencyReceiptAsync(db,tx,clientId,runId);
        if(protectedReceipt is null&&!configuration.GetValue<bool>(NativeCurrencyFlag))return null;
        var period = await db.ExecuteScalarAsync<string?>("SELECT PayPeriod FROM payruns WHERE Id=@RunId AND ClientId=@ClientId", new { RunId = runId, ClientId = clientId }, tx);
        if (period is null) throw new InvalidOperationException("The native currency run does not belong to this client.");
        var pointer=protectedReceipt?.Pointer;var receipt=protectedReceipt?.Receipt;
        var protector = nativeCurrencyProtector ?? throw new InvalidOperationException("Native currency-stage protection is not available.");
        var designs = new PayslipDesignRepository(configuration, protector, new ExcelPayslipPdfService());
        var selection = await designs.ReadNativeSourceDesignSelectionAsync(db, tx, clientId, payPeriod: period);
        if (selection?.Profile.SourceKind == "CorePayroll")
        {
            // A portable layout has no source-based currency stage. A layout
            // switch must never discard protected or observed processing proof.
            if (pointer is not null)
                throw new InvalidOperationException("The protected native currency source design changed; a portable layout cannot replace its original processing evidence.");
            var captured = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM payroll_calculation_traces WHERE PayRunId=@RunId AND JSON_EXTRACT(InputJson,'$.nativeCurrencyContext') IS NOT NULL)",new{RunId=runId},tx);
            if (captured)
                throw new InvalidOperationException("This captured payroll requires its original native processing currency receipt; a portable layout cannot replace it.");
            return null;
        }
        if (selection is null || selection.Profile.AmountDecimalPlaces != 0 || !selection.Applies)
        {
            if (pointer is not null) throw new InvalidOperationException("The protected native currency source design changed; review the complete run again.");
            return null;
        }
        if(pointer is null)
        {
            // Unmanifested legacy/global months retain their old accounting
            // display. A scoped month or an existing native stage cannot silently
            // lose its protected pointer after a future blueprint selection.
            var captured=await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM payroll_calculation_traces WHERE PayRunId=@RunId AND JSON_EXTRACT(InputJson,'$.nativeCurrencyContext') IS NOT NULL)",new{RunId=runId},tx);
            if(selection.Profile.PayPeriods is not null||captured)
                throw new InvalidOperationException("This source whole-rupee design requires its original native processing currency receipt.");
            var sourceMonth=await db.ExecuteScalarAsync<string?>("SELECT JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId",new{ClientId=clientId,BatchId=selection.Profile.SourceBatchId},tx);
            if(sourceMonth!=period)return null;
        }
        if (pointer is null || pointer.Version != 1 || pointer.ClientId != clientId || pointer.RunId != runId
            || pointer.ReceiptCode != "pcst:" + pointer.EpochHash)
            throw new InvalidOperationException("This source whole-rupee design requires its original native processing currency receipt.");
        if(receipt is null||receipt.Profile.ClientId!=clientId||receipt.Profile.PayPeriod!=period||receipt.RunId!=runId)
            throw new InvalidOperationException("The original protected native currency receipt is missing or belongs to another run.");
        var original=await db.QuerySingleOrDefaultAsync<CurrencyOriginalSource>(@"SELECT CAST(b.batch_json AS CHAR) BatchJson,CAST(s.source_json AS CHAR) SourceJson
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@BatchId",new{ClientId=clientId,BatchId=receipt.Profile.SourceBatchId},tx)
            ??throw new InvalidOperationException("The currency receipt's original source is unavailable.");
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(original.BatchJson, CurrencyJson)!;
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(original.SourceJson, CurrencyJson)!;
        if(batch.ClientId!=clientId||batch.Id!=receipt.Profile.SourceBatchId||batch.Month!=period)
            throw new InvalidOperationException("The original currency source ownership/period changed.");
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, clientId, batch.Id);
        var profile = new NativeCurrencyProfile(true, 1, clientId, period, batch.Id,
            ExcelPayslipRepository.EmploymentSourceHash(batch, source), NativeCurrencyPresentation.Hash(selection.Template,selection.Profile,period), 0)
            {SourceDataHash=PayrollSourceDataProvenance.SnapshotHash(original.BatchJson,original.SourceJson)};
        var state = await ReadCurrencyRunStateAsync(db, tx, clientId, runId, period);
        return NativePayslipCurrencyStage.Verify(profile, state, receipt, pointer.PayloadHash)!;
    }

    private sealed class CurrencyOriginalSource { public string BatchJson{get;set;}="";public string SourceJson{get;set;}=""; }

    private static async Task<NativeCurrencyRunState> ReadCurrencyRunStateAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, int runId, string period)
    {
        var header = await db.QuerySingleOrDefaultAsync<(string PayPeriod, decimal PayrollCost, decimal NetPay)>(
            "SELECT PayPeriod,PayrollCost,NetPay FROM payruns WHERE Id=@RunId AND ClientId=@ClientId",
            new { RunId = runId, ClientId = clientId }, tx);
        if (header.PayPeriod != period) throw new InvalidOperationException("Native currency period/header ownership changed.");
        var rows = (await db.QueryAsync<CurrencyStoredRow>(CurrencyStoredRowsSql + " ORDER BY p.Id", new { ClientId = clientId, RunId = runId }, tx)).ToArray();
        var traces = (await db.QueryAsync<CurrencyStoredTrace>(@"SELECT Id,PayRunId RunId,EmployeeId,ComponentCode,CalculatedAmount,
CAST(InputJson AS CHAR) InputJson,CAST(OutputJson AS CHAR) OutputJson,SHA2(CAST(InputJson AS CHAR),256) InputHash,SHA2(CAST(OutputJson AS CHAR),256) OutputHash
FROM payroll_calculation_traces WHERE PayRunId=@RunId ORDER BY Id", new { RunId = runId }, tx)).ToArray();
        if (rows.Length is < 1 or > 5000 || traces.Length is < 1 or > 100000 || rows.Any(r => r.ClientId != clientId || r.IsSkipped)
            || header.PayrollCost != rows.Sum(r => r.Gross) || header.NetPay != rows.Sum(r => r.NetPay))
            throw new InvalidOperationException("The complete native financial run/header is missing or changed.");
        var savedRows = ImmutableArray.CreateBuilder<NativeCurrencySavedRow>(); var nativeTraces = ImmutableArray.CreateBuilder<NativeCurrencyTrace>();
        var contexts = new Dictionary<int, CurrencyTraceContext>();
        foreach (var trace in traces)
        {
            var output = JsonNode.Parse(trace.OutputJson)!.AsObject();
            var input = JsonNode.Parse(trace.InputJson)!.AsObject();
            var outputStage = output["nativeCurrencyStage"];
            var inputStage = input["nativeCurrencyStage"];
            if (outputStage is not null && inputStage is not null)
                throw new InvalidOperationException("A native trace cannot contain competing raw-stage envelopes.");
            var stage = (outputStage ?? inputStage)?.Deserialize<CurrencyTraceStage>(CurrencyJson);
            var context = input["nativeCurrencyContext"]?.Deserialize<CurrencyTraceContext>(CurrencyJson);
            if (output["Category"]?.GetValue<string>() != "Summary")
            {
                if (stage is not { Version: 1 } || context is not { Version: 1 } || context.EmployeeId != trace.EmployeeId
                    || trace.CalculatedAmount != stage.AccountedEarned || stage.NativeRawStageHash.Length != 64)
                    throw new InvalidOperationException("An original native financial raw-stage trace envelope is missing or invalid.");
                if (contexts.TryGetValue(trace.EmployeeId, out var prior) && prior != context)
                    throw new InvalidOperationException("The native processing row has inconsistent input/identity/source trace context.");
                contexts[trace.EmployeeId] = context;
            }
            else if (stage is not null || context is not null) throw new InvalidOperationException("A summary cannot supply native financial stage evidence.");
            nativeTraces.Add(new(trace.Id, trace.RunId, trace.EmployeeId, trace.ComponentCode, trace.CalculatedAmount,
                trace.InputHash, trace.OutputHash, stage?.NativeRawStageHash ?? ""));
        }
        foreach (var row in rows)
        {
            if (!contexts.TryGetValue(row.EmployeeId, out var context) || context.NativeRowId != row.Id
                || context.PresentDays != row.PresentDays || context.PayableDays != row.PayableDays)
                throw new InvalidOperationException("Saved native attendance/identity differs from its original processing envelope.");
            using var details = JsonDocument.Parse(row.DetailsJson);
            var benefits = details.RootElement.EnumerateArray().Where(e => Text(e, "Category") == "Benefit").Sum(e => e.GetProperty("amount").GetDecimal());
            savedRows.Add(new(row.Id, row.EmployeeId, context.SourceRowId, context.SourceRow, context.SourceIdentityHash,
                context.InputSnapshotHash, context.EffectiveTemplateHash, context.TemplateId, context.AnnualCtc,
                context.PayrollDays, row.PresentDays, row.PayableDays, row.DetailsJson, row.MonthlyGross,
                new(row.Gross, row.Deductions, benefits, row.NetPay)));
        }
        var transactions = contexts.Values.Select(c => c.ProcessingTransactionId).Distinct(StringComparer.Ordinal).ToArray();
        if (transactions.Length != 1 || contexts.Count != rows.Length)
            throw new InvalidOperationException("The complete native run does not share one original processing transaction.");
        return new(clientId, period, runId, transactions[0], savedRows.ToImmutable(), nativeTraces.ToImmutable());
    }

    private async Task<T?> ReadProtectedCurrencyValueAsync<T>(MySqlConnection db, MySqlTransaction tx, int clientId, string code) where T : class
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE", new { ClientId = clientId, Code = code }, tx);
        if (json is null) return null;
        var protectedValue = JsonSerializer.Deserialize<CurrencyProtectedValue>(json, CurrencyJson);
        var plain = nativeCurrencyProtector?.UnprotectNativeCurrencyStage(protectedValue?.Value ?? "");
        return plain is null ? throw new InvalidOperationException("Native currency evidence cannot be authenticated.")
            : JsonSerializer.Deserialize<T>(plain, CurrencyJson) ?? throw new InvalidOperationException("Native currency evidence is malformed.");
    }

    private async Task WriteProtectedCurrencyValueAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string code, object value, bool immutable)
    {
        var plain = JsonSerializer.Serialize(value, CurrencyJson);
        if (code.Length > 80 || plain.Length > 8_000_000) throw new InvalidOperationException("Native currency evidence exceeds its bounded storage contract.");
        var protector = nativeCurrencyProtector ?? throw new InvalidOperationException("Native currency protection is unavailable.");
        var json = JsonSerializer.Serialize(new CurrencyProtectedValue(protector.ProtectNativeCurrencyStage(plain)), CurrencyJson);
        var sql = @"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))";
        if (!immutable) sql += " ON DUPLICATE KEY UPDATE IsEnabled=TRUE,SettingsJson=VALUES(SettingsJson)";
        await db.ExecuteAsync(sql, new { ClientId = clientId, Code = code, Json = json }, tx);
    }
    private static string CurrencyPointerCode(int runId) => "pcst.active:" + runId.ToString(CultureInfo.InvariantCulture);
}
