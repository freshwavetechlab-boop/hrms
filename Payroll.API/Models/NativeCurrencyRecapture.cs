namespace Payroll.API.Models;

// No amount, attendance, template or employee override is accepted by this
// transition. It attests one entire already-saved Draft, or changes nothing.
public sealed record NativeCurrencyRecapturePreview(int RunId, int ClientId, string PayPeriod,
    int EmployeeCount, int TraceCount, string PlanHash, string SourceHash, string ProfileHash,
    DateTime ExpiresAtUtc, string ReviewToken);

public sealed record NativeCurrencyRecaptureRequest(int ClientId, string PayPeriod, string ReviewToken,
    string RequestId, bool Confirmed);

public sealed record NativeCurrencyRecaptureResult(int RunId, int EmployeeCount, int TraceCount,
    string ProcessingEpochHash, string ArchiveId, bool Replayed, NativeCurrencyRecapturePreservation Preservation);

public sealed record NativeCurrencyRecapturePreservation(bool SalaryAndAccountingUnchanged,
    bool StatusAndPaymentUnchanged, bool TaxAndAdjustmentsUnchanged, bool SourceAndMailUnchanged,
    bool OriginalTraceOutputsAndIdsUnchanged);
