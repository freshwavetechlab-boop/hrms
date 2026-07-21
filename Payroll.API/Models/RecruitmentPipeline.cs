namespace Payroll.API.Models;

public class RecruitmentJobDescriptionVersion
{
    public long Id { get; set; }
    public long RequisitionId { get; set; }
    public int ClientId { get; set; }
    public int VersionNumber { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public long? WorkflowInstanceId { get; set; }
    public int CreatedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<RecruitmentJdResponsibility> Responsibilities { get; set; } = [];
    public List<RecruitmentJdSkillRequirement> Skills { get; set; } = [];
    public List<RecruitmentJdQualificationRequirement> Qualifications { get; set; } = [];
    public List<RecruitmentJdCertificationRequirement> Certifications { get; set; } = [];
    public List<RecruitmentJdLanguageRequirement> Languages { get; set; } = [];
    public List<RecruitmentJdBenefit> Benefits { get; set; } = [];
}

public class SaveRecruitmentJobDescriptionVersion
{
    public long Id { get; set; }
    public long RequisitionId { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public List<RecruitmentJdResponsibility> Responsibilities { get; set; } = [];
    public List<RecruitmentJdSkillRequirement> Skills { get; set; } = [];
    public List<RecruitmentJdQualificationRequirement> Qualifications { get; set; } = [];
    public List<RecruitmentJdCertificationRequirement> Certifications { get; set; } = [];
    public List<RecruitmentJdLanguageRequirement> Languages { get; set; } = [];
    public List<RecruitmentJdBenefit> Benefits { get; set; } = [];
}

public class RecruitmentJdResponsibility
{
    public long Id { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public string ResponsibilityText { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentJdSkillRequirement
{
    public long Id { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public long? SkillId { get; set; }
    public string SkillName { get; set; } = "";
    public bool IsRequired { get; set; } = true;
    public decimal MinimumYears { get; set; }
    public string MinimumProficiency { get; set; } = "";
    public decimal WeightPercent { get; set; }
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentJdQualificationRequirement
{
    public long Id { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public string QualificationName { get; set; } = "";
    public string Specialization { get; set; } = "";
    public bool IsMandatory { get; set; } = true;
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentJdCertificationRequirement
{
    public long Id { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public string CertificationName { get; set; } = "";
    public bool IsMandatory { get; set; }
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentJdLanguageRequirement
{
    public long Id { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public string LanguageName { get; set; } = "";
    public string Proficiency { get; set; } = "";
    public bool IsMandatory { get; set; }
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentJdBenefit
{
    public long Id { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public string BenefitName { get; set; } = "";
    public string Description { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentJobPosting
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long PositionId { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public long? ApplicationFormVersionId { get; set; }
    public string PublicSlug { get; set; } = "";
    public string PublicTitle { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public DateTime? OpensAtUtc { get; set; }
    public DateTime? ClosesAtUtc { get; set; }
    public int? MaximumApplications { get; set; }
    public int ApplicationCount { get; set; }
    public bool SearchEngineVisible { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string ClientName { get; set; } = "";
}

public class SaveRecruitmentJobPosting
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public long JobDescriptionVersionId { get; set; }
    public long? ApplicationFormVersionId { get; set; }
    public string PublicTitle { get; set; } = "";
    public DateTime? OpensAtUtc { get; set; }
    public DateTime? ClosesAtUtc { get; set; }
    public int? MaximumApplications { get; set; }
    public bool SearchEngineVisible { get; set; }
}

public class RecruitmentPublicJobPosting
{
    public RecruitmentJobPosting Posting { get; set; } = new();
    public RecruitmentJobDescriptionVersion JobDescription { get; set; } = new();
}

public class RecruitmentPipelineDefinition
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string PipelineCode { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string Description { get; set; } = "";
    public long? CurrentPublishedVersionId { get; set; }
    public bool IsActive { get; set; } = true;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string ClientName { get; set; } = "";
}

public class SaveRecruitmentPipelineDefinition
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string PipelineCode { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class RecruitmentPipelineVersion
{
    public long Id { get; set; }
    public long PipelineDefinitionId { get; set; }
    public int VersionNumber { get; set; }
    public string Status { get; set; } = "Draft";
    public int CreatedByUserId { get; set; }
    public int? PublishedByUserId { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<RecruitmentPipelineStage> Stages { get; set; } = [];
    public List<RecruitmentPipelineTransition> Transitions { get; set; } = [];
}

public class SaveRecruitmentPipelineVersion
{
    public long Id { get; set; }
    public long PipelineDefinitionId { get; set; }
    public List<RecruitmentPipelineStage> Stages { get; set; } = [];
    public List<RecruitmentPipelineTransition> Transitions { get; set; } = [];
}

public class RecruitmentPipelineStage
{
    public long Id { get; set; }
    public long PipelineVersionId { get; set; }
    public string StageCode { get; set; } = "";
    public string StageName { get; set; } = "";
    public string StageType { get; set; } = "Screening";
    public int StageNumber { get; set; }
    public int DisplayOrder { get; set; }
    public int SlaDurationMinutes { get; set; }
    public int SlaWarningMinutes { get; set; }
    public long? ApprovalWorkflowId { get; set; }
    public bool RequiresApproval { get; set; }
    public bool CalendarEnabled { get; set; }
    public bool AllowSkip { get; set; }
    public bool IsInitial { get; set; }
    public bool IsTerminal { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RecruitmentPipelineStageAction> Actions { get; set; } = [];
    public RecruitmentStageAtsConfiguration? AtsConfiguration { get; set; }
    public RecruitmentStageExternalFormConfiguration? ExternalFormConfiguration { get; set; }
    public List<RecruitmentStageAttachmentRequirement> AttachmentRequirements { get; set; } = [];
    public RecruitmentStageOfferConfiguration? OfferConfiguration { get; set; }
    public RecruitmentInterviewStageConfiguration? InterviewConfiguration { get; set; }
}

public class RecruitmentPipelineStageAction
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public string TriggerEvent { get; set; } = "OnEntry";
    public string ActionCode { get; set; } = "";
    public int ExecutionOrder { get; set; } = 100;
    public bool IsBlocking { get; set; }
    public long? WorkflowId { get; set; }
    public long? TemplateId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RecruitmentStageActionExecution
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public long StageInstanceId { get; set; }
    public long StageActionId { get; set; }
    public string TriggerEvent { get; set; } = "";
    public string ActionCode { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public bool IsBlocking { get; set; }
    public long? WorkflowInstanceId { get; set; }
    public long? NotificationQueueId { get; set; }
    public long? CandidateActionSessionId { get; set; }
    public long? ApplicationScoreId { get; set; }
    public string ErrorMessage { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class RecruitmentStageActionExecutionResult
{
    public long ApplicationId { get; set; }
    public long StageInstanceId { get; set; }
    public string TriggerEvent { get; set; } = "";
    public bool HasBlockingFailure { get; set; }
    public List<RecruitmentStageActionExecution> Executions { get; set; } = [];
}

public class RecruitmentStageAtsConfiguration
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public long? ScoringProfileId { get; set; }
    public decimal MinimumAdvanceScore { get; set; } = 60;
    public decimal MaximumRejectScore { get; set; }
    public bool AutoScoreOnEntry { get; set; } = true;
    public bool AutoAdvance { get; set; }
    public bool AutoReject { get; set; }
    public bool RequireHumanConfirmation { get; set; } = true;
    public string AdvanceOutcomeCode { get; set; } = "SHORTLIST";
    public string RejectOutcomeCode { get; set; } = "REJECT";
}

public class RecruitmentStageExternalFormConfiguration
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public long FormVersionId { get; set; }
    public bool SubmissionRequired { get; set; } = true;
    public bool AllowSaveDraft { get; set; } = true;
    public int ActionTokenValidityMinutes { get; set; } = 10080;
    public int ActionTokenMaximumUses { get; set; } = 20;
}

public class RecruitmentStageAttachmentRequirement
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public long AttachmentFieldConfigurationId { get; set; }
    public bool IsRequired { get; set; } = true;
    public int MinimumFileCount { get; set; } = 1;
    public int MaximumFileCount { get; set; } = 1;
    public bool RequiresVerification { get; set; }
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentStageOfferConfiguration
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public long? OfferTemplateId { get; set; }
    public long? ApprovalWorkflowId { get; set; }
    public string BudgetBasis { get; set; } = "ApprovedMaximum";
    public decimal MaximumVariancePercent { get; set; }
    public bool RequireApprovalWhenVarianceExceeded { get; set; } = true;
    public long? VarianceApprovalWorkflowId { get; set; }
    public int CandidateResponseValidityDays { get; set; } = 7;
    public bool RequireAcceptedOfferToAdvance { get; set; } = true;
}

public class RecruitmentPipelineTransition
{
    public long Id { get; set; }
    public long PipelineVersionId { get; set; }
    public long FromStageId { get; set; }
    public long ToStageId { get; set; }
    public string FromStageCode { get; set; } = "";
    public string ToStageCode { get; set; } = "";
    public string OutcomeCode { get; set; } = "ADVANCE";
    public string ActionLabel { get; set; } = "Move";
    public long? ApprovalWorkflowId { get; set; }
    public bool RequiresReason { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; } = 100;
    public List<RecruitmentPipelineTransitionRule> Rules { get; set; } = [];
}

public class RecruitmentPipelineTransitionRule
{
    public long Id { get; set; }
    public long TransitionId { get; set; }
    public string RuleType { get; set; } = "";
    public string ComparisonOperator { get; set; } = "EQ";
    public string? TextValue { get; set; }
    public long? IntegerValue { get; set; }
    public decimal? DecimalValue { get; set; }
    public bool? BooleanValue { get; set; }
    public bool IsMandatory { get; set; } = true;
    public string ErrorMessage { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentInterviewStageConfiguration
{
    public long Id { get; set; }
    public long PipelineStageId { get; set; }
    public int RoundNumber { get; set; }
    public string InterviewType { get; set; } = "Technical";
    public int DefaultDurationMinutes { get; set; } = 60;
    public int MinimumPanelCount { get; set; } = 1;
    public decimal MinimumPassingScore { get; set; } = 60;
    public bool FeedbackRequired { get; set; } = true;
    public bool CalendarEnabled { get; set; } = true;
    public bool AllowReschedule { get; set; } = true;
    public List<RecruitmentInterviewStageCompetency> Competencies { get; set; } = [];
}

public class RecruitmentInterviewCompetencyDefinition
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string CompetencyCode { get; set; } = "";
    public string CompetencyName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class RecruitmentInterviewStageCompetency
{
    public long Id { get; set; }
    public long InterviewStageConfigurationId { get; set; }
    public long CompetencyId { get; set; }
    public string CompetencyName { get; set; } = "";
    public decimal WeightPercent { get; set; }
    public decimal MinimumScore { get; set; }
    public int DisplayOrder { get; set; } = 100;
}

public class RecruitmentPositionPipelineAssignment
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public long? JobPostingId { get; set; }
    public long PipelineVersionId { get; set; }
    public bool IsActive { get; set; } = true;
    public int AssignedByUserId { get; set; }
    public DateTime AssignedAtUtc { get; set; }
}

public class AssignRecruitmentPipelineRequest
{
    public long PositionId { get; set; }
    public long? JobPostingId { get; set; }
    public long PipelineVersionId { get; set; }
}

public class RecruitmentApplicationStageInstance
{
    public long Id { get; set; }
    public long ApplicationPipelineInstanceId { get; set; }
    public long ApplicationId { get; set; }
    public long PipelineStageId { get; set; }
    public string StageCode { get; set; } = "";
    public string StageName { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string OutcomeCode { get; set; } = "";
    public DateTime EnteredAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? ExitedAtUtc { get; set; }
    public long ActiveDurationSeconds { get; set; }
    public long PausedDurationSeconds { get; set; }
    public bool IsSlaBreached { get; set; }
}

public class RecruitmentPipelineBoard
{
    public long PositionId { get; set; }
    public long? JobPostingId { get; set; }
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public long PipelineVersionId { get; set; }
    public List<RecruitmentPipelineBoardLane> Lanes { get; set; } = [];
}

public class RecruitmentPipelineBoardLane
{
    public long StageId { get; set; }
    public string StageCode { get; set; } = "";
    public string StageName { get; set; } = "";
    public string StageType { get; set; } = "";
    public int DisplayOrder { get; set; }
    public int SlaDurationMinutes { get; set; }
    public int SlaWarningMinutes { get; set; }
    public List<RecruitmentPipelineBoardCard> Applications { get; set; } = [];
}

public class RecruitmentPipelineBoardCard
{
    public long ApplicationId { get; set; }
    public string ApplicationCode { get; set; } = "";
    public long CandidateId { get; set; }
    public string CandidateName { get; set; } = "";
    public string CandidateEmail { get; set; } = "";
    public decimal? AtsScore { get; set; }
    public DateTime EnteredAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public long ElapsedSeconds { get; set; }
    public long RemainingSeconds { get; set; }
    public long PausedDurationSeconds { get; set; }
    public bool IsSlaWarning { get; set; }
    public bool IsSlaBreached { get; set; }
    public string StageStatus { get; set; } = "Active";
    public int PendingBlockingActionCount { get; set; }
    public int FailedActionCount { get; set; }
}

public class RecruitmentPipelineTransitionRequest
{
    public long TransitionId { get; set; }
    public string Reason { get; set; } = "";
}

public class RecruitmentPipelineTransitionResult
{
    public long RequestId { get; set; }
    public long ApplicationId { get; set; }
    public string Status { get; set; } = "";
    public long? WorkflowInstanceId { get; set; }
    public long? CurrentStageInstanceId { get; set; }
    public string Message { get; set; } = "";
}

public class RecruitmentStagePauseRequest
{
    public string Reason { get; set; } = "";
}
