using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private static readonly JsonSerializerOptions CurrencyJson = new(JsonSerializerDefaults.Web);
    internal const string NativeCurrencyFlag = "Payroll:NativeSourceCurrencyStagesEnabled";

    private static void AddNativeScalarCurrencyStage(List<PayrollSimulationLine>? trace, string code, string name,
        string category, decimal monthly, decimal earned, string origin)
    {
        if (trace is null) return;
        // These are native policy/approved-period scalar inputs at their actual
        // BuildEmployee origin, not rounded details reverse-engineered later.
        trace.Add(new(code, name, category, "", monthly.ToString(CultureInfo.InvariantCulture), monthly, monthly, earned,
            false, origin, "Existing native scalar amount", []) { UnroundedEarned = earned });
    }

    private sealed class CurrencyProcessingContext
    {
        public required NativeCurrencyProfile Profile { get; init; }
        public required string TransactionId { get; init; }
        public required ExcelPayslipBatch Batch { get; init; }
        public required ExcelPayslipCalculationSource Source { get; init; }
        public required string SetupJson { get; init; }
        public required IReadOnlyList<Employee> Identities { get; init; }
        public required Dictionary<int, CurrencySourceLink> Links { get; init; }
        public required Dictionary<int, NativePayrollAttendanceInput> NativeInputs { get; init; }
        public required Dictionary<int, PayRunAttendance> Attendance { get; init; }
        public Dictionary<int, CurrencyCapturedRow> Captured { get; } = [];
    }

    private sealed record CurrencySourceLink(ExcelPayrollSetupRequest Request, ExcelPayrollSetupResult Receipt,
        string RowId, string RequestHash, string ResultHash);
    private sealed record CurrencyCapturedRow(NativeCurrencySavedRow Saved, NativeCurrencyObservation Observation);
    private sealed class CurrencySourceReceiptJson
    {
        public string RequestJson { get; set; } = "";
        public string ResultJson { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ResultHash { get; set; } = "";
    }

    private sealed record CurrencyTraceContext(int Version, string ProcessingTransactionId, int NativeRowId,
        int EmployeeId, string SourceRowId, int SourceRow, string SourceIdentityHash, string InputSnapshotHash,
        string EffectiveTemplateHash, string TemplateId, decimal AnnualCtc, int PayrollDays,
        decimal? PresentDays, decimal PayableDays, string OrdinaryEvidenceHash, string IndependentSourceProofHash);
    private sealed record CurrencyTraceStage(int Version, string NativeRawStageHash, decimal MonthlyRaw,
        decimal UnroundedEarned, decimal AccountedMonthly, decimal AccountedEarned, string AccountingPath);
    private sealed record CurrencyProtectedValue(string Value);
    private sealed record CurrencyActivePointer(int Version, int ClientId, int RunId, string EpochHash, string ReceiptCode, string PayloadHash);

    private async Task<CurrencyProcessingContext?> BeginCurrencyProcessingAsync(MySqlConnection db, MySqlTransaction tx,
        int runId, CreatePayRunRequest request, string runType, string setupJson,
        IReadOnlyList<PayRunSourceEmployee> employees, Dictionary<int, PayRunAttendance> attendance, bool locked = true)
    {
        // No extra read, changed JSON or portable protector is required when off.
        if (!configuration.GetValue<bool>(NativeCurrencyFlag) || runType != "Regular") return null;
        var protector = nativeCurrencyProtector ?? throw new InvalidOperationException("Native currency-stage protection is not available.");
        var binding = await new PayslipDesignRepository(configuration, protector, new ExcelPayslipPdfService())
            .ReadNativeSourceDesignBindingAsync(db, tx, request.ClientId, locked, request.PayPeriod);
        if (binding is null || binding.Profile.AmountDecimalPlaces != 0 || binding.Batch.Month != request.PayPeriod) return null;
        if (employees.Count is < 1 or > 5000 || employees.Select(e => e.Id).Distinct().Count() != employees.Count)
            throw new InvalidOperationException("Native source currency capture requires the complete bounded regular run roster.");
        if (employees.Any(e => e.LocationParts is not null || e.NamedInputs is not null))
            throw new InvalidOperationException("Named/location salary requires its complete independently reviewed source-proof adapter before native currency capture.");
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(binding.RawBatchJson, CurrencyJson)
            ?? throw new InvalidOperationException("The original currency source batch is unavailable.");
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(binding.RawSourceJson, CurrencyJson)
            ?? throw new InvalidOperationException("The original currency source formulas are unavailable.");
        if (batch.ClientId != request.ClientId || batch.Month != request.PayPeriod || batch.Id != binding.Profile.SourceBatchId)
            throw new InvalidOperationException("The reviewed currency source does not match this native client/period.");
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, request.ClientId, batch.Id);
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        var profile = new NativeCurrencyProfile(true, 1, request.ClientId, request.PayPeriod, batch.Id, hash,
            NativeCurrencyPresentation.Hash(binding.Template,binding.Profile,request.PayPeriod), 0)
            {SourceDataHash=PayrollSourceDataProvenance.SnapshotHash(binding.RawBatchJson,binding.RawSourceJson)};
        var stored = (await db.QueryAsync<CurrencySourceReceiptJson>(@"SELECT CAST(request_json AS CHAR) RequestJson,
CAST(result_json AS CHAR) ResultJson,SHA2(CAST(request_json AS CHAR),256) RequestHash,SHA2(CAST(result_json AS CHAR),256) ResultHash
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND status='Applied'" + (locked ? " FOR UPDATE" : ""),
            new { request.ClientId, BatchId = batch.Id }, tx)).Select(r => new
            {
                Raw = r, Request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(r.RequestJson, CurrencyJson)!,
                Result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(r.ResultJson, CurrencyJson)!
            }).ToArray();
        var links = new Dictionary<int, CurrencySourceLink>();
        foreach (var employee in employees)
        {
            // Discover the original version-one link from the established
            // receipt predicate. ReviewAppliedSourceRow below still independently
            // proves the original explicit identity decision; none is invented.
            var candidates = stored.Where(r => r.Request.TransferReviewVersion == 1 && r.Result.Transfer is { Version: 1 } transfer
                && transfer.SourceHash == hash && transfer.SourceBatchId == batch.Id && transfer.SourceMonth == batch.Month
                && r.Result.EmployeeIds.Contains(employee.Id))
                .SelectMany(r => r.Result.TransferRows.Where(t => t.EmployeeId == employee.Id)
                .Select(t => new CurrencySourceLink(r.Request, r.Result, t.RowId, r.Raw.RequestHash, r.Raw.ResultHash))).ToArray();
            if (candidates.Length != 1 || batch.Rows.Count(row => row.Id == candidates[0].RowId) != 1)
                throw new InvalidOperationException("Every native currency row needs one unique original Applied employee/source receipt.");
            links.Add(employee.Id, candidates[0]);
        }
        var natives = new Dictionary<int, NativePayrollAttendanceInput>();
        foreach (var chunk in employees.Select(e => e.Id).Chunk(100))
            foreach (var pair in await ReadNativeAttendanceProofBatchAsync(db, tx, request.ClientId, request.PayPeriod, chunk, true))
                natives.Add(pair.Key, pair.Value);
        if (natives.Count != employees.Count || natives.Values.Any(n => n.Error.Length != 0))
            throw new InvalidOperationException("Every currency-stage employee requires complete same-period native cycle evidence.");
        return new() { Profile = profile, TransactionId = Guid.NewGuid().ToString("N"), Batch = batch, Source = source,
            SetupJson = setupJson, Identities = await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, tx, request.ClientId),
            Links = links, NativeInputs = natives, Attendance = attendance };
    }

    private async Task<CurrencyCapturedRow> ReviewCurrencyProcessingRowAsync(MySqlConnection db, MySqlTransaction tx,
        CurrencyProcessingContext context, PayRunSourceEmployee employee,
        PayRunEmployee row, int payrollDays, decimal? presentDays, decimal payableDays,
        IReadOnlyList<PayrollAdjustment> adjustments, IReadOnlyList<object> taxInputs, IReadOnlyList<PayrollSimulationLine> nativeLines,
        bool locked = true)
    {
        if (row.IsSkipped || context.Captured.ContainsKey(employee.Id) || row.Id <= 0)
            throw new InvalidOperationException("Currency capture requires every complete newly saved native processing row exactly once.");
        var stored = await db.QuerySingleAsync<CurrencyStoredRow>(CurrencyStoredRowsSql +
            " AND p.EmployeeId=@EmployeeId" + (locked ? " FOR UPDATE" : ""), new { ClientId = row.ClientId, RunId = row.PayRunId, EmployeeId = row.EmployeeId }, tx);
        if (stored.Id != row.Id || stored.Gross != row.GrossPay + row.OneTimeEarnings
            || stored.Deductions != row.StatutoryDeductions + row.OneTimeDeductions || stored.NetPay != row.NetPay
            || stored.MonthlyGross != row.MonthlyGross || stored.PresentDays != presentDays || stored.PayableDays != payableDays || stored.IsSkipped)
            throw new InvalidOperationException("Actual saved native processing row changed before currency proof.");
        var link = context.Links[employee.Id];
        var master = JsonSerializer.Deserialize<Employee>(JsonSerializer.Serialize(context.Identities.Single(e => e.Id == employee.Id), CurrencyJson), CurrencyJson)!;
        master.SalaryStructureId = employee.SalaryStructureId; master.AnnualCtc = employee.AnnualCtc;
        master.SalaryComponents = new(employee.SalaryComponents);
        var sourceReview = ExcelPayrollSetupRepository.ReviewAppliedSourceRow(row.ClientId, row.EmployeeId,
            context.Batch, context.Source, link.Request, link.Receipt, context.SetupJson, master, context.Identities,
            stored.DetailsJson, context.NativeInputs[employee.Id]);
        var sourceRow = context.Batch.Rows.Single(r => r.Id == link.RowId);
        var details = JsonNode.Parse(stored.DetailsJson)!.AsArray();
        var financial = details.Where(d => d!["Category"]!.GetValue<string>() != "Summary").ToArray();
        if (nativeLines.Count != financial.Length || sourceReview.Lines.Count != financial.Length
            || nativeLines.Any(line => sourceReview.Lines.Count(s => s.Code == line.Code && s.Category == line.Category
                && s.CoreAmount == line.Earned && line.UnroundedEarned is decimal raw
                && CurrencyWhole(raw) == CurrencyWhole(s.SourceRaw)) != 1))
            throw new InvalidOperationException("The complete actual native stage and independent source/formula/currency proof do not agree.");
        var gross = row.GrossPay + row.OneTimeEarnings; var deductions = row.StatutoryDeductions + row.OneTimeDeductions;
        var benefits = nativeLines.Where(l => l.Category == "Benefit").Sum(l => l.Earned);
        if (CurrencyWhole(gross) != CurrencyWhole(sourceRow.DeclaredGross ?? sourceRow.Earnings.Sum(l => l.Amount))
            || CurrencyWhole(deductions) != CurrencyWhole(sourceRow.DeclaredDeductions ?? sourceRow.Deductions.Sum(l => l.Amount))
            || CurrencyWhole(benefits) != CurrencyWhole(sourceRow.EmployerContributions.Sum(l => l.Amount))
            || CurrencyWhole(row.NetPay) != CurrencyWhole(sourceRow.NetPay))
            throw new InvalidOperationException("Native accounted totals do not reproduce all independently formatted whole source totals.");
        var result = new PayrollSimulationResult { Mode = "Employee", ClientId = row.ClientId, EmployeeId = row.EmployeeId,
            PayPeriod = context.Profile.PayPeriod, TemplateId = employee.SalaryStructureId, Lines = nativeLines.ToList(),
            Gross = gross, Deductions = deductions, Benefits = benefits, Net = row.NetPay,
            Inputs = [new("PAYROLL_DAYS", payrollDays, "Native processing attendance", "Days"),
                new("PRESENT_DAYS", presentDays, "Native processing attendance", "Days"),
                new("PAYABLE_DAYS", payableDays, "Native processing attendance", "Days"),
                new("ANNUAL_CTC", employee.AnnualCtc, "Saved effective salary", "Amount")] };
        var inputsHash = CurrencyHash(new { employee, SetupHash = CurrencyHash(context.SetupJson),
            Attendance = context.Attendance[employee.Id], Adjustments = adjustments, TaxInputs = taxInputs,
            employee.PfPolicyPeriod, employee.AttendancePolicySnapshot, employee.PayableOnlyAttendance });
        var templateHash = CurrencyHash(new { SetupHash = CurrencyHash(context.SetupJson), employee.SalaryStructureId,
            employee.AnnualCtc, employee.SalaryComponents });
        var proofHash = CurrencyHash(new { sourceReview.SourceHash, sourceReview.MappingFingerprint, sourceReview.IdentityFingerprint,
            sourceReview.FormulaFingerprint, sourceReview.Lines, sourceReview.Arithmetic, link.RequestHash, link.ResultHash });
        var saved = new NativeCurrencySavedRow(row.Id, row.EmployeeId, sourceRow.Id, sourceRow.SourceRow,
            sourceReview.IdentityFingerprint, inputsHash, templateHash, employee.SalaryStructureId, employee.AnnualCtc,
            payrollDays, presentDays, payableDays, stored.DetailsJson, row.MonthlyGross, new(gross, deductions, benefits, row.NetPay));
        var observed = new NativeCurrencyObservation(row.Id, NativeCurrencyCaptureOrigin.NativeProcessingTransaction,
            context.TransactionId, CurrencyHash(new { inputsHash, row.DetailsJson, result }), proofHash, inputsHash, templateHash,
            sourceReview.IdentityFingerprint, new() { Mode = "Employee", ClientId = row.ClientId, EmployeeId = row.EmployeeId,
                PayPeriod = context.Profile.PayPeriod }, result);
        var captured = new CurrencyCapturedRow(saved, observed); context.Captured.Add(employee.Id, captured); return captured;
    }

    private static string CurrencyHash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CurrencyJson))).ToLowerInvariant();
    private static decimal CurrencyWhole(decimal raw) => decimal.Round(ExcelPayrollSetupRepository.SourceDisplayPrecision(raw), 0, MidpointRounding.AwayFromZero);
}
