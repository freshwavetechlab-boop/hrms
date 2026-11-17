using System.Collections.Immutable;

namespace Payroll.API.Models;

// Internal observations only. There is no request DTO, endpoint or persistence
// path for these types. A future integration must read the complete run and its
// raw-stage trace envelope inside the native processing transaction.
internal sealed record NativeCurrencyProfile(bool Enabled, int Version, int ClientId, string PayPeriod,
    string SourceBatchId, string SourceHash, string ProfileHash, int AmountDecimalPlaces)
{
    public string SourceDataHash { get; init; } = "";
}

internal sealed record NativeCurrencyTotals(decimal Gross, decimal Deductions, decimal Benefits, decimal Net);

internal sealed record NativeCurrencySavedRow(int NativeRowId, int EmployeeId, string SourceRowId,
    int SourceRow, string SourceIdentityHash, string InputSnapshotHash, string EffectiveTemplateHash,
    string TemplateId, decimal AnnualCtc, int PayrollDays, decimal? PresentDays, decimal PayableDays,
    string DetailsJson, decimal MonthlyGross, NativeCurrencyTotals Totals);

internal sealed record NativeCurrencyTrace(long Id, int RunId, int EmployeeId, string ComponentCode,
    decimal CalculatedAmount, string InputHash, string OutputHash, string NativeRawStageHash = "");

internal sealed record NativeCurrencyRunState(int ClientId, string PayPeriod, int RunId,
    string ProcessingTransactionId, ImmutableArray<NativeCurrencySavedRow> Rows,
    ImmutableArray<NativeCurrencyTrace> Traces);

internal enum NativeCurrencyCaptureOrigin { Unspecified, NativeProcessingTransaction, ReconstructedSimulation }

internal sealed record NativeCurrencyObservation(int NativeRowId, NativeCurrencyCaptureOrigin Origin,
    string ProcessingTransactionId, string OrdinaryEvidenceHash, string IndependentSourceProofHash,
    string InputSnapshotHash, string EffectiveTemplateHash, string SourceIdentityHash,
    PayrollSimulationRequest Request, PayrollSimulationResult Result);

internal sealed record NativeCurrencyFrozenLine(string Code, string Category, decimal MonthlyRaw,
    decimal UnroundedEarned, decimal AccountedMonthly, decimal AccountedEarned, string AccountingPath,
    long CalculationTraceId, string NativeRawStageHash);

internal sealed record NativeCurrencyFrozenRow(int NativeRowId, int EmployeeId, string SourceRowId,
    int SourceRow, string SourceIdentityHash, string InputSnapshotHash, string EffectiveTemplateHash,
    string OrdinaryEvidenceHash, string IndependentSourceProofHash,
    ImmutableArray<NativeCurrencyFrozenLine> Lines);

internal sealed record NativeCurrencyStageReceipt(int Version, NativeCurrencyProfile Profile,
    int RunId, string ProcessingTransactionId, string SavedRunHash, string ProcessingEpochHash,
    ImmutableArray<NativeCurrencyFrozenRow> Rows, string PayloadHash);
