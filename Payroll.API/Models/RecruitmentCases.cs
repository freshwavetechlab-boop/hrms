namespace Payroll.API.Models;

public class RecruitmentWorkOrder
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string WorkOrderNumber { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; }
    public string ReceivedFrom { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public int OverallSlaMinutes { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int LineCount { get; set; }
    public int OpenCaseCount { get; set; }
    public List<RecruitmentWorkOrderLine> Lines { get; set; } = [];
}

public class RecruitmentWorkOrderLine
{
    public long Id { get; set; }
    public long WorkOrderId { get; set; }
    public int LineNumber { get; set; }
    public string PositionName { get; set; } = "";
    public string PayBandLevelCode { get; set; } = "";
    public int NumberOfPositions { get; set; } = 1;
    public string Location { get; set; } = "";
    public string Division { get; set; } = "";
    public long? RequisitionId { get; set; }
    public long? PositionId { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class SaveRecruitmentWorkOrder
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string WorkOrderNumber { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; }
    public string ReceivedFrom { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public int OverallSlaMinutes { get; set; }
    public List<SaveRecruitmentWorkOrderLine> Lines { get; set; } = [];
}

public class SaveRecruitmentWorkOrderLine
{
    public long Id { get; set; }
    public int LineNumber { get; set; }
    public string PositionName { get; set; } = "";
    public string PayBandLevelCode { get; set; } = "";
    public int NumberOfPositions { get; set; } = 1;
    public string Location { get; set; } = "";
    public string Division { get; set; } = "";
    public long? RequisitionId { get; set; }
    public long? PositionId { get; set; }
    public string Status { get; set; } = "Open";
}

public class RecruitmentHiringCase
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public long WorkOrderId { get; set; }
    public string WorkOrderNumber { get; set; } = "";
    public long WorkOrderLineId { get; set; }
    public long? RequisitionId { get; set; }
    public long? PositionId { get; set; }
    public string PositionName { get; set; } = "";
    public string PayBandLevelCode { get; set; } = "";
    public string Division { get; set; } = "";
    public long PipelineVersionId { get; set; }
    public string PipelineName { get; set; } = "";
    public DateTime SlaAnchorAtUtc { get; set; }
    public DateTime? OverallDueAtUtc { get; set; }
    public long? CurrentStageInstanceId { get; set; }
    public string CurrentStageName { get; set; } = "";
    public string CurrentStakeholderCode { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string AdvanceStatus { get; set; } = "";
    public long? AdvanceRequestId { get; set; }
    public string AdvanceMessage { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<RecruitmentHiringCaseStage> Stages { get; set; } = [];
}

public class StartRecruitmentHiringCaseRequest
{
    public long WorkOrderLineId { get; set; }
    public long PipelineVersionId { get; set; }
}

public class RecruitmentHiringCaseStage
{
    public long Id { get; set; }
    public long HiringCaseId { get; set; }
    public long PipelineStageId { get; set; }
    public string StageCode { get; set; } = "";
    public string StageName { get; set; } = "";
    public int DisplayOrder { get; set; }
    public string StakeholderCode { get; set; } = "";
    public int? TargetOffsetMinutes { get; set; }
    public bool AllowPause { get; set; } = true;
    public string PauseBehavior { get; set; } = "ShiftStageAndOverall";
    public bool RequiresApproval { get; set; }
    public long? ApprovalWorkflowId { get; set; }
    public bool IsTerminal { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime? EnteredAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long PausedDurationSeconds { get; set; }
    public bool IsPaused { get; set; }
    public bool IsSlaBreached { get; set; }
    public List<RecruitmentHiringCasePausePeriod> PauseHistory { get; set; } = [];
    public List<RecruitmentStageProcessDocumentRequirement> ProcessDocumentRequirements { get; set; } = [];
}

public class RecruitmentHiringCasePausePeriod
{
    public long Id { get; set; }
    public long PositionStageInstanceId { get; set; }
    public string Reason { get; set; } = "";
    public int PausedByUserId { get; set; }
    public string PausedByName { get; set; } = "";
    public DateTime PausedAtUtc { get; set; }
    public int? ResumedByUserId { get; set; }
    public string ResumedByName { get; set; } = "";
    public DateTime? ResumedAtUtc { get; set; }
    public long DurationSeconds { get; set; }
}

public class MoveRecruitmentHiringCaseRequest
{
    public string OutcomeCode { get; set; } = "ADVANCE";
    public string Reason { get; set; } = "";
}

public class RecruitmentProcessDocument
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long? HiringCaseId { get; set; }
    public long? ApplicationId { get; set; }
    public long? InterviewId { get; set; }
    public long? PipelineStageId { get; set; }
    public string DocumentType { get; set; } = "";
    public int VersionNumber { get; set; }
    public long? TemplateId { get; set; }
    public Guid? AttachmentPublicId { get; set; }
    public bool HasFinalSignedAttachment { get; set; }
    public string Status { get; set; } = "Draft";
    public long? WorkflowInstanceId { get; set; }
    public int CreatedByUserId { get; set; }
    public int? SignedByUserId { get; set; }
    public DateTime? SignedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class SaveRecruitmentProcessDocument
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long? HiringCaseId { get; set; }
    public long? ApplicationId { get; set; }
    public long? InterviewId { get; set; }
    public long? PipelineStageId { get; set; }
    public string DocumentType { get; set; } = "";
    public long? TemplateId { get; set; }
    public Guid? AttachmentPublicId { get; set; }
    public string Status { get; set; } = "Draft";
    public long? WorkflowInstanceId { get; set; }
}

public class RecruitmentStageActionRecipient
{
    public long Id { get; set; }
    public long StageActionId { get; set; }
    public string RecipientType { get; set; } = "Candidate";
    public int? UserId { get; set; }
    public string RoleCode { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}

public class RecruitmentStageDefaultPanelMember
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public int PanelUserId { get; set; }
    public string PanelUserName { get; set; } = "";
    public string PanelRole { get; set; } = "Panelist";
    public bool IsRequired { get; set; } = true;
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentProfileSubmissionBatch
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long HiringCaseId { get; set; }
    public string BatchNumber { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public int CreatedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public int? ForwardedByUserId { get; set; }
    public DateTime? ForwardedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<RecruitmentProfileSubmissionBatchItem> Items { get; set; } = [];
    public List<RecruitmentProfileBatchNotificationDelivery> Deliveries { get; set; } = [];
}

public class RecruitmentProfileSubmissionBatchItem
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public string CandidateName { get; set; } = "";
    public decimal? AtsScore { get; set; }
    public string ReadinessStatus { get; set; } = "Pending";
    public string MissingFields { get; set; } = "";
}

public class SaveRecruitmentProfileSubmissionBatch
{
    public long HiringCaseId { get; set; }
    public List<long> ApplicationIds { get; set; } = [];
}

public class RecruitmentProfileBatchNotificationDelivery
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long StageActionId { get; set; }
    public string RecipientType { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public long NotificationQueueId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
