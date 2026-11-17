namespace Payroll.API.Models;

// Configuration review only: callers never provide employee amounts, histories,
// identities, subsets or native financial snapshots.
public sealed record PayrollReferenceInformationReviewRequest(int ClientId,int RunId,string PayPeriod,long TemplateId,
    string SourceBatchId,string HistoricalReceiptRequestId,int SourceInfoColumn,bool RefreshExisting=false);
public sealed record PayrollReferenceInformationApplyRequest(string ReviewToken,string RequestId,bool Confirmed);
public sealed record PayrollReferenceInformationReviewRow(int EmployeeId,long NativeRowId,string SourceRowId,decimal MonthlyBudget);
public sealed record PayrollReferenceInformationReview(int ClientId,int RunId,string PayPeriod,long TemplateId,int SourceInfoColumn,
    string Label,string PlanHash,string StateWitnessHash,string ReviewToken,DateTime ExpiresAtUtc,
    IReadOnlyList<PayrollReferenceInformationReviewRow> Rows,bool RefreshExisting=false,string? PreviousManifestHash=null);
public sealed record PayrollReferenceInformationReceipt(int Version,int ClientId,int RunId,string PayPeriod,long TemplateId,
    int SourceInfoColumn,int EmployeeCount,string RequestId,string PlanHash,string ManifestHash,DateTime AppliedAtUtc,
    bool Refreshed=false,string? PreviousManifestHash=null,string? PreviousManifestArchiveCode=null);
internal sealed record PayrollReferenceInformationOverride(PayrollReferenceStatus Status,int? SourceColumn,string? Label,
    PayrollReferenceUnit? Unit,decimal? Value,string ManifestHash);
