using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Pure validation only. PayloadHash is an integrity witness, NOT authentication.
// Verification requires a digest from a separately protected/trusted receipt.
// No persistence, route, native calculation, renderer or default flow uses this class.
internal static class NativePayslipCurrencyStage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const decimal Maximum = 1_000_000_000m;

    internal static NativeCurrencyStageReceipt Capture(NativeCurrencyProfile profile, NativeCurrencyRunState saved,
        IReadOnlyList<NativeCurrencyObservation> observations)
    {
        ValidateProfile(profile); ValidateState(saved);
        Require(profile.ClientId == saved.ClientId && profile.PayPeriod == saved.PayPeriod, "Foreign profile/run scope.");
        Require(observations is not null && observations.Count == saved.Rows.Length && observations.All(o => o is not null)
            && observations.Select(o => o.NativeRowId).Distinct().Count() == observations.Count
            && observations.Select(o => o.NativeRowId).ToHashSet().SetEquals(saved.Rows.Select(r => r.NativeRowId)), "The complete saved run roster is required.");
        var byRow = observations.ToDictionary(o => o.NativeRowId);
        var byTrace = saved.Traces.ToLookup(t => (t.EmployeeId, t.ComponentCode));
        var rows = ImmutableArray.CreateBuilder<NativeCurrencyFrozenRow>();
        foreach (var row in saved.Rows.OrderBy(r => r.NativeRowId))
        {
            var observed = byRow[row.NativeRowId];
            Require(observed.Origin == NativeCurrencyCaptureOrigin.NativeProcessingTransaction
                && observed.ProcessingTransactionId == saved.ProcessingTransactionId,
                "Frozen native stages must come from this processing transaction; a reconstructed simulation is not a receipt.");
            Require(HashValue(observed.OrdinaryEvidenceHash) && HashValue(observed.IndependentSourceProofHash)
                && observed.InputSnapshotHash == row.InputSnapshotHash && observed.EffectiveTemplateHash == row.EffectiveTemplateHash
                && observed.SourceIdentityHash == row.SourceIdentityHash, "Native/source/identity/input/template proof is missing or changed.");
            var request = observed.Request; var result = observed.Result;
            Require(request is not null && request.Mode == "Employee" && request.ClientId == saved.ClientId && request.EmployeeId == row.EmployeeId
                && request.PayPeriod == saved.PayPeriod && request.AnnualCtc is null && request.PayrollDays is null && request.PresentDays is null
                && request.PayableDays is null && request.AmountOverrides is { Count: 0 } && request.Snapshot is null
                && request.NamedInputs is null && !request.ReviewedExtraDutyUnits
                && (string.IsNullOrEmpty(request.TemplateId) || request.TemplateId == row.TemplateId), "What-if/request scope cannot become frozen native evidence.");
            Require(result is not null && result.Issues is not null && result.Inputs is not null && result.Lines is not null
                && result.Mode == "Employee" && result.Complete && result.ClientId == saved.ClientId
                && result.EmployeeId == row.EmployeeId && result.PayPeriod == saved.PayPeriod && result.TemplateId == row.TemplateId
                && result.LocationAllocations is null && result.Gross == row.Totals.Gross && result.Deductions == row.Totals.Deductions
                && result.Benefits == row.Totals.Benefits && result.Net == row.Totals.Net, "Ordinary native result differs from the complete saved financial row.");
            Require(result.Inputs.All(i => i is not null && i.Source is not null && !i.Source.Contains("what-if", StringComparison.OrdinalIgnoreCase)), "What-if input evidence is not supported.");
            CheckInput(result, "PAYROLL_DAYS", row.PayrollDays); CheckInput(result, "PRESENT_DAYS", row.PresentDays);
            CheckInput(result, "PAYABLE_DAYS", row.PayableDays); CheckInput(result, "ANNUAL_CTC", row.AnnualCtc);
            var amounts = ReadSavedLines(row);
            Require(result.Lines.All(l => l is not null && !string.IsNullOrWhiteSpace(l.Code) && l.Category is not null)
                && result.Lines.Count == amounts.Count && result.Lines.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Lines.Count,
                "Native raw-stage component coverage is incomplete or duplicated.");
            var lines = ImmutableArray.CreateBuilder<NativeCurrencyFrozenLine>();
            foreach (var line in result.Lines)
            {
                Require(amounts.TryGetValue(line.Code, out var amount) && amount.Category == line.Category
                    && amount.Earned == line.Earned && amount.Monthly == line.Monthly && line.UnroundedEarned is decimal,
                    "Native stage and saved component accounting differ.");
                var raw = line.UnroundedEarned!.Value;
                Require(Math.Abs(raw) <= Maximum && Math.Abs(line.Raw) <= Maximum, "Native raw stage is outside its supported bound.");
                var path = AccountingPath(line);
                var expectedRaw = path == "DailyRate" ? line.DailyRate!.Value * row.PayableDays
                    : path == "Prorated" ? line.Monthly * row.PayableDays / row.PayrollDays : line.Raw;
                Require(raw == expectedRaw, "The captured earned stage differs from its actual native monthly/proration/rate path.");
                Require(AccountedMonthly(line.Raw) == line.Monthly && AccountedEarned(raw, path) == line.Earned,
                    "Native raw-to-accounted currency boundary does not reproduce the saved amount.");
                var stageHash = NativeRawStageHash(saved, row, line);
                var traces = byTrace[(row.EmployeeId, line.Code)].ToArray();
                Require(traces.Length == 1 && traces[0].CalculatedAmount == line.Earned && traces[0].NativeRawStageHash == stageHash,
                    "Original processing trace lacks this exact frozen native raw stage; legacy traces cannot be reconstructed.");
                lines.Add(new(line.Code, line.Category, line.Raw, raw, line.Monthly, line.Earned, path, traces[0].Id, stageHash));
            }
            rows.Add(new(row.NativeRowId, row.EmployeeId, row.SourceRowId, row.SourceRow, row.SourceIdentityHash,
                row.InputSnapshotHash, row.EffectiveTemplateHash, observed.OrdinaryEvidenceHash, observed.IndependentSourceProofHash, lines.ToImmutable()));
        }
        var receipt = new NativeCurrencyStageReceipt(1, profile, saved.RunId, saved.ProcessingTransactionId,
            SavedRunHash(saved), ProcessingEpochHash(saved), rows.ToImmutable(), "");
        return receipt with { PayloadHash = PayloadHash(receipt) };
    }

    // A disabled profile preserves the existing path. An opted-in profile must
    // have a valid protected receipt; it must never silently fall back.
    internal static NativeCurrencyStageReceipt? Verify(NativeCurrencyProfile profile, NativeCurrencyRunState current,
        NativeCurrencyStageReceipt? receipt, string trustedProtectedPayloadHash)
    {
        Require(profile is not null, "Currency profile is missing.");
        if (!profile.Enabled) return null;
        ValidateProfile(profile); ValidateState(current);
        Require(receipt is not null && receipt.Version == 1 && HashValue(trustedProtectedPayloadHash), "A protected native currency-stage receipt is required.");
        Require(receipt.Profile == profile && receipt.RunId == current.RunId && receipt.ProcessingTransactionId == current.ProcessingTransactionId
            && current.ClientId == profile.ClientId && current.PayPeriod == profile.PayPeriod, "Currency receipt profile/source/run scope changed.");
        Require(EqualHash(receipt.PayloadHash, trustedProtectedPayloadHash) && EqualHash(receipt.PayloadHash, PayloadHash(receipt))
            && EqualHash(receipt.SavedRunHash, SavedRunHash(current)) && EqualHash(receipt.ProcessingEpochHash, ProcessingEpochHash(current)),
            "Saved native details, totals, inputs, template, identity or processing epoch changed; review the complete run again.");
        Require(!receipt.Rows.IsDefault && receipt.Rows.All(r => r is not null) && receipt.Rows.Length == current.Rows.Length
            && receipt.Rows.Select(r => r.NativeRowId).ToHashSet().SetEquals(current.Rows.Select(r => r.NativeRowId)), "Complete currency receipt roster changed.");
        return receipt;
    }

    internal static string NativeRawStageHash(NativeCurrencyRunState state, NativeCurrencySavedRow row, PayrollSimulationLine line) => Hash(new
    {
        Version = 1, state.ClientId, state.PayPeriod, state.RunId, state.ProcessingTransactionId,
        row.NativeRowId, row.EmployeeId, row.InputSnapshotHash, row.EffectiveTemplateHash,
        line.Code, line.Category, line.Raw, line.UnroundedEarned, line.Monthly, line.Earned, Path = AccountingPath(line)
    });

    // These are the observed existing CalculateStructureLines boundaries. The
    // regular engine clamps monthly amounts, then rounds prorated/daily earned
    // amounts separately. No accounting mode or total is changed by this helper.
    internal static decimal AccountedMonthly(decimal raw) => Math.Max(0, decimal.Round(raw, 2));
    internal static decimal AccountedEarned(decimal raw, string path) => path switch
    {
        "Monthly" => AccountedMonthly(raw),
        "Prorated" or "DailyRate" => decimal.Round(raw, 2),
        _ => throw Failure("Unknown native currency path.")
    };

    private static string AccountingPath(PayrollSimulationLine line) => line.DailyRate.HasValue ? "DailyRate" : line.ProRata ? "Prorated" : "Monthly";
    private static void CheckInput(PayrollSimulationResult result, string key, decimal? expected)
    {
        var values = result.Inputs.Where(i => i.Name == key).ToArray();
        Require(values.Length == 1 && values[0].Value == expected, "Frozen ordinary attendance/salary inputs differ from the saved row.");
    }

    private static Dictionary<string, SavedLine> ReadSavedLines(NativeCurrencySavedRow row)
    {
        try { return ReadSavedLinesCore(row); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or OverflowException
            || error is InvalidOperationException && !error.Message.StartsWith("Review native payslip currency evidence:", StringComparison.Ordinal))
        { throw Failure("Saved native component details are malformed."); }
    }

    private static Dictionary<string, SavedLine> ReadSavedLinesCore(NativeCurrencySavedRow row)
    {
        using var document = JsonDocument.Parse(row.DetailsJson);
        Require(document.RootElement.ValueKind == JsonValueKind.Array, "Saved native component details are invalid.");
        var lines = new Dictionary<string, SavedLine>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var category = item.GetProperty("Category").GetString() ?? "";
            if (category == "Summary") continue;
            var code = item.GetProperty("Id").GetString() ?? "";
            Require(code.Length is > 0 and <= 80 && category is "Earning" or "Reimbursement" or "Deduction" or "Benefit", "Unknown native financial component.");
            var earned = item.GetProperty("amount").GetDecimal(); var monthly = item.GetProperty("monthlyAmount").GetDecimal();
            Require(earned is >= 0 and <= Maximum && monthly is >= 0 and <= Maximum && lines.TryAdd(code, new(category, earned, monthly)), "Duplicate/invalid saved native money.");
        }
        Require(lines.Count > 0 && row.Totals.Gross == lines.Values.Where(l => l.Category is "Earning" or "Reimbursement").Sum(l => l.Earned)
            && row.Totals.Deductions == lines.Values.Where(l => l.Category == "Deduction").Sum(l => l.Earned)
            && row.Totals.Benefits == lines.Values.Where(l => l.Category == "Benefit").Sum(l => l.Earned)
            && row.Totals.Net == Math.Max(0, row.Totals.Gross - row.Totals.Deductions), "Saved totals differ from native accounted components.");
        return lines;
    }

    private static void ValidateProfile(NativeCurrencyProfile profile) => Require(profile is not null && profile.Enabled && profile.Version == 1
        && profile.ClientId > 0 && Period(profile.PayPeriod) && Guid.TryParseExact(profile.SourceBatchId, "N", out _)
        && HashValue(profile.SourceHash) && HashValue(profile.SourceDataHash) && HashValue(profile.ProfileHash) && profile.AmountDecimalPlaces == 0,
        "Only an explicitly reviewed source-bound whole-rupee profile supports a currency-stage receipt.");

    private static void ValidateState(NativeCurrencyRunState state)
    {
        Require(state is not null && state.ClientId > 0 && Period(state.PayPeriod) && state.RunId > 0
            && Guid.TryParseExact(state.ProcessingTransactionId, "N", out _) && !state.Rows.IsDefault && state.Rows.Length is >= 1 and <= 5_000
            && !state.Traces.IsDefaultOrEmpty && state.Traces.Length <= 100_000, "Native processing transaction, complete roster and traces are required.");
        Require(state.Rows.All(r => r is not null) && state.Rows.Select(r => r.NativeRowId).Distinct().Count() == state.Rows.Length
            && state.Rows.Select(r => r.EmployeeId).Distinct().Count() == state.Rows.Length, "Native run roster is ambiguous.");
        foreach (var row in state.Rows)
        {
            Require(row.NativeRowId > 0 && row.EmployeeId > 0 && row.SourceRow > 0 && Guid.TryParse(row.SourceRowId, out _)
                && HashValue(row.SourceIdentityHash) && HashValue(row.InputSnapshotHash) && HashValue(row.EffectiveTemplateHash)
                && !string.IsNullOrWhiteSpace(row.TemplateId) && row.AnnualCtc is >= 0 and <= Maximum && row.PayrollDays is >= 1 and <= 31
                && row.PresentDays is null or >= 0 && row.PayableDays >= 0 && row.MonthlyGross is >= 0 and <= Maximum
                && row.Totals is not null && !string.IsNullOrWhiteSpace(row.DetailsJson),
                "A saved native row/identity/input/template is missing or invalid.");
            ReadSavedLines(row);
        }
        var employeeIds = state.Rows.Select(r => r.EmployeeId).ToHashSet();
        Require(state.Traces.All(t => t is not null) && state.Traces.Select(t => t.Id).Distinct().Count() == state.Traces.Length && state.Traces.All(t => t.Id > 0 && t.RunId == state.RunId
            && employeeIds.Contains(t.EmployeeId) && !string.IsNullOrWhiteSpace(t.ComponentCode)
            && HashValue(t.InputHash) && HashValue(t.OutputHash) && (t.NativeRawStageHash == "" || HashValue(t.NativeRawStageHash))), "Processing traces are missing, duplicated or foreign.");
    }

    private static string SavedRunHash(NativeCurrencyRunState saved) => Hash(new
        { Version = 1, saved.ClientId, saved.PayPeriod, saved.RunId, saved.ProcessingTransactionId, Rows = saved.Rows.OrderBy(r => r.NativeRowId).ToArray() });
    private static string ProcessingEpochHash(NativeCurrencyRunState saved) => Hash(new
        { Version = 1, saved.ClientId, saved.PayPeriod, saved.RunId, saved.ProcessingTransactionId, Traces = saved.Traces.OrderBy(t => t.Id).ToArray() });
    private static string PayloadHash(NativeCurrencyStageReceipt receipt) => Hash(receipt with { PayloadHash = "" });
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static bool HashValue(string value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    private static bool EqualHash(string a, string b) => HashValue(a) && HashValue(b) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));
    private static bool Period(string value) => DateTime.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw Failure(message); }
    private static InvalidOperationException Failure(string message) => new("Review native payslip currency evidence: " + message);
    private sealed record SavedLine(string Category, decimal Earned, decimal Monthly);
}
