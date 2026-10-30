namespace Payroll.API.Models;

public sealed record ExcelPayslipOriginal(string Id, string FileName, long SizeBytes, string Sha256, DateTime UploadedAtUtc);
public sealed class ExcelPayslipDeletePreview
{
    public string BatchId { get; set; } = "";
    public string Token { get; set; } = "";
    public int PayslipRows { get; set; }
    public int CalculationSources { get; set; }
    public int DeliveryRecords { get; set; }
    public int MailJobs { get; set; }
    public int RequestReceipts { get; set; }
    public int Notifications { get; set; }
    public int NotificationLogs { get; set; }
    public int MasterSetupReceipts { get; set; }
    public int DerivedBatches { get; set; }
    public int ConsolidationReferences { get; set; }
    public int OriginalLinks { get; set; }
    public int OtherBatchOriginalLinks { get; set; }
    public int LegacyStorageRecords { get; set; }
    public int CleanupAuditRecords { get; set; }
    public int InvalidParentReferences { get; set; }
    public bool IsLegacy { get; set; }
    public bool LegacyReviewValid { get; set; }
    public string LegacyReviewStatus { get; set; } = "Not required";
    public string RecordedParentId { get; set; } = "";
    public List<ExcelPayslipCleanupObject> Objects { get; set; } = [];
    public List<ExcelPayslipLegacyCandidate> Candidates { get; set; } = [];
    public int CandidateCount { get; set; }
    public List<string> Blockers { get; set; } = [];
    public bool CanDelete => Blockers.Count == 0;
    public bool CanDiscardUnusedLegacy { get; set; }
}
public sealed record ExcelPayslipDeleteRequest(string Token, string ConfirmBatchId, bool DiscardUnusedLegacy = false);
public sealed record ExcelPayslipDeleteResult(bool Deleted, bool AlreadyDeleted);

public sealed record ExcelPayslipOriginalReference(string Id, string FileName);

public sealed record ExcelPayslipCleanupObject(string Kind, string Id, string Status, string Reason, string Action = "", int Count = 1, string Label = "");
public sealed class ExcelPayslipLegacyCandidate
{
    public string Id { get; set; } = "";
    public string Month { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int RowCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
public sealed class ExcelPayslipSupersessionPreview
{
    public string BatchId { get; set; } = "";
    public string KeeperBatchId { get; set; } = "";
    public string Token { get; set; } = "";
    public string Evidence { get; set; } = "";
    public int OldRows { get; set; }
    public int KeeperRows { get; set; }
    public int ChangedRows { get; set; }
    public bool ExactSnapshot { get; set; }
    public bool SameCalculationSource { get; set; }
    public List<string> Blockers { get; set; } = [];
    public bool Eligible => Blockers.Count == 0;
}
public sealed record ExcelPayslipSupersessionRequest(string KeeperBatchId);
public sealed record ExcelPayslipCleanupActionRequest(string RequestId, string Action, string Token, string ConfirmBatchId,
    string ObjectId, string ConfirmObjectId, string KeeperBatchId, bool Acknowledged);
public sealed record ExcelPayslipCleanupActionResult(bool Applied, bool AlreadyApplied, ExcelPayslipDeletePreview? Preview);
