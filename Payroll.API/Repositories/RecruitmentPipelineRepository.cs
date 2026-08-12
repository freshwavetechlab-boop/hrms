using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class RecruitmentPipelineRepository(IConfiguration configuration, WorkflowRepository workflows)
{
    private static readonly HashSet<string> StageTypes = new(StringComparer.OrdinalIgnoreCase) { "Screening", "ATS", "ExternalForm", "Documents", "Interview", "HR", "Approval", "PreOnboarding", "Offer", "Joining", "Rejected", "Withdrawn", "Completed" };
    private static readonly JsonSerializerOptions ApprovalSnapshotJson = new(JsonSerializerDefaults.Web);
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS recruitment_job_description_versions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    RequisitionId BIGINT NOT NULL,
    ClientId INT NOT NULL,
    VersionNumber INT NOT NULL,
    Title VARCHAR(180) NOT NULL,
    Summary TEXT NOT NULL,
    RolePurpose TEXT NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
    WorkflowInstanceId BIGINT NULL,
    CreatedByUserId INT NOT NULL,
    ApprovedByUserId INT NULL,
    ApprovedAtUtc DATETIME NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_recruitment_jd_version (RequisitionId,VersionNumber),
    INDEX IX_recruitment_jd_client_status (ClientId,Status)
);
CREATE TABLE IF NOT EXISTS recruitment_jd_responsibilities (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobDescriptionVersionId BIGINT NOT NULL,
    ResponsibilityText TEXT NOT NULL,
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_jd_responsibility (JobDescriptionVersionId,DisplayOrder),
    CONSTRAINT FK_recruitment_jd_resp_version FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_jd_skill_requirements (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobDescriptionVersionId BIGINT NOT NULL,
    SkillId BIGINT NULL,
    SkillName VARCHAR(180) NOT NULL,
    IsRequired BOOLEAN NOT NULL DEFAULT TRUE,
    MinimumYears DECIMAL(5,2) NOT NULL DEFAULT 0,
    MinimumProficiency VARCHAR(80) NOT NULL DEFAULT '',
    WeightPercent DECIMAL(5,2) NOT NULL DEFAULT 0,
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_jd_skill (JobDescriptionVersionId,IsRequired,DisplayOrder),
    CONSTRAINT FK_recruitment_jd_skill_version FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_jd_qualification_requirements (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobDescriptionVersionId BIGINT NOT NULL,
    QualificationName VARCHAR(180) NOT NULL,
    Specialization VARCHAR(180) NOT NULL DEFAULT '',
    IsMandatory BOOLEAN NOT NULL DEFAULT TRUE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_jd_qualification (JobDescriptionVersionId,DisplayOrder),
    CONSTRAINT FK_recruitment_jd_qualification_version FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_jd_certification_requirements (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobDescriptionVersionId BIGINT NOT NULL,
    CertificationName VARCHAR(180) NOT NULL,
    IsMandatory BOOLEAN NOT NULL DEFAULT FALSE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_jd_certification (JobDescriptionVersionId,DisplayOrder),
    CONSTRAINT FK_recruitment_jd_certification_version FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_jd_language_requirements (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobDescriptionVersionId BIGINT NOT NULL,
    LanguageName VARCHAR(120) NOT NULL,
    Proficiency VARCHAR(80) NOT NULL DEFAULT '',
    IsMandatory BOOLEAN NOT NULL DEFAULT FALSE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_jd_language (JobDescriptionVersionId,DisplayOrder),
    CONSTRAINT FK_recruitment_jd_language_version FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_jd_benefits (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobDescriptionVersionId BIGINT NOT NULL,
    BenefitName VARCHAR(180) NOT NULL,
    Description VARCHAR(1000) NOT NULL DEFAULT '',
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_jd_benefit (JobDescriptionVersionId,DisplayOrder),
    CONSTRAINT FK_recruitment_jd_benefit_version FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_job_postings (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    PositionId BIGINT NOT NULL,
    JobDescriptionVersionId BIGINT NOT NULL,
    ApplicationFormVersionId BIGINT NULL,
    PublicSlug CHAR(32) NOT NULL,
    PublicTitle VARCHAR(180) NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
    OpensAtUtc DATETIME NULL,
    ClosesAtUtc DATETIME NULL,
    MaximumApplications INT NULL,
    ApplicationCount INT NOT NULL DEFAULT 0,
    SearchEngineVisible BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedByUserId INT NOT NULL,
    PublishedAtUtc DATETIME NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_recruitment_job_posting_slug (PublicSlug),
    INDEX IX_recruitment_job_posting_position (PositionId,Status),
    INDEX IX_recruitment_job_posting_client (ClientId,Status,OpensAtUtc,ClosesAtUtc),
    CONSTRAINT FK_recruitment_job_posting_jd FOREIGN KEY (JobDescriptionVersionId) REFERENCES recruitment_job_description_versions(Id)
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_definitions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    PipelineCode VARCHAR(80) NOT NULL,
    PipelineName VARCHAR(180) NOT NULL,
    Description VARCHAR(1000) NOT NULL DEFAULT '',
    CurrentPublishedVersionId BIGINT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_recruitment_pipeline_code (ClientId,PipelineCode),
    INDEX IX_recruitment_pipeline_client (ClientId,IsActive)
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_versions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineDefinitionId BIGINT NOT NULL,
    VersionNumber INT NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
    CreatedByUserId INT NOT NULL,
    PublishedByUserId INT NULL,
    PublishedAtUtc DATETIME NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_recruitment_pipeline_version (PipelineDefinitionId,VersionNumber),
    INDEX IX_recruitment_pipeline_version_status (PipelineDefinitionId,Status),
    CONSTRAINT FK_recruitment_pipeline_version_definition FOREIGN KEY (PipelineDefinitionId) REFERENCES recruitment_pipeline_definitions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_stages (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineVersionId BIGINT NOT NULL,
    StageCode VARCHAR(80) NOT NULL,
    StageName VARCHAR(180) NOT NULL,
    StageType VARCHAR(40) NOT NULL DEFAULT 'Screening',
    CardScope VARCHAR(40) NOT NULL DEFAULT 'Application',
    StageNumber INT NOT NULL,
    DisplayOrder INT NOT NULL,
    SlaDurationMinutes INT NOT NULL DEFAULT 0,
    SlaWarningMinutes INT NOT NULL DEFAULT 0,
    ApprovalWorkflowId BIGINT NULL,
    RequiresApproval BOOLEAN NOT NULL DEFAULT FALSE,
    CalendarEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    AllowSkip BOOLEAN NOT NULL DEFAULT FALSE,
    IsInitial BOOLEAN NOT NULL DEFAULT FALSE,
    IsTerminal BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_recruitment_pipeline_stage_code (PipelineVersionId,StageCode),
    UNIQUE KEY UX_recruitment_pipeline_stage_order (PipelineVersionId,DisplayOrder),
    INDEX IX_recruitment_pipeline_stage_number (PipelineVersionId,StageNumber),
    CONSTRAINT FK_recruitment_pipeline_stage_version FOREIGN KEY (PipelineVersionId) REFERENCES recruitment_pipeline_versions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_stage_actions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    TriggerEvent VARCHAR(40) NOT NULL DEFAULT 'OnEntry',
    ActionCode VARCHAR(80) NOT NULL,
    ExecutionOrder INT NOT NULL DEFAULT 100,
    IsBlocking BOOLEAN NOT NULL DEFAULT FALSE,
    WorkflowId BIGINT NULL,
    TemplateId BIGINT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_recruitment_stage_action (PipelineStageId,TriggerEvent,ActionCode,ExecutionOrder),
    INDEX IX_recruitment_stage_action_trigger (PipelineStageId,TriggerEvent,IsActive),
    CONSTRAINT FK_recruitment_stage_action_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_stage_ats_configurations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    ScoringProfileId BIGINT NULL,
    MinimumAdvanceScore DECIMAL(5,2) NOT NULL DEFAULT 60,
    MaximumRejectScore DECIMAL(5,2) NOT NULL DEFAULT 0,
    AutoScoreOnEntry BOOLEAN NOT NULL DEFAULT TRUE,
    AutoAdvance BOOLEAN NOT NULL DEFAULT FALSE,
    AutoReject BOOLEAN NOT NULL DEFAULT FALSE,
    RequireHumanConfirmation BOOLEAN NOT NULL DEFAULT TRUE,
    AdvanceOutcomeCode VARCHAR(80) NOT NULL DEFAULT 'SHORTLIST',
    RejectOutcomeCode VARCHAR(80) NOT NULL DEFAULT 'REJECT',
    UNIQUE KEY UX_recruitment_stage_ats_config (PipelineStageId),
    CONSTRAINT FK_recruitment_stage_ats_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_stage_external_form_configurations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    FormVersionId BIGINT NOT NULL,
    SubmissionRequired BOOLEAN NOT NULL DEFAULT TRUE,
    AllowSaveDraft BOOLEAN NOT NULL DEFAULT TRUE,
    ActionTokenValidityMinutes INT NOT NULL DEFAULT 10080,
    ActionTokenMaximumUses INT NOT NULL DEFAULT 20,
    UNIQUE KEY UX_recruitment_stage_external_form (PipelineStageId),
    INDEX IX_recruitment_stage_external_form_version (FormVersionId),
    CONSTRAINT FK_recruitment_stage_external_form_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_stage_attachment_requirements (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    AttachmentFieldConfigurationId BIGINT NOT NULL,
    IsRequired BOOLEAN NOT NULL DEFAULT TRUE,
    MinimumFileCount INT NOT NULL DEFAULT 1,
    MaximumFileCount INT NOT NULL DEFAULT 1,
    RequiresVerification BOOLEAN NOT NULL DEFAULT FALSE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    UNIQUE KEY UX_recruitment_stage_attachment (PipelineStageId,AttachmentFieldConfigurationId),
    INDEX IX_recruitment_stage_attachment_order (PipelineStageId,DisplayOrder),
    CONSTRAINT FK_recruitment_stage_attachment_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_stage_offer_configurations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    OfferTemplateId BIGINT NULL,
    ApprovalWorkflowId BIGINT NULL,
    BudgetBasis VARCHAR(40) NOT NULL DEFAULT 'ApprovedMaximum',
    MaximumVariancePercent DECIMAL(7,2) NOT NULL DEFAULT 0,
    RequireApprovalWhenVarianceExceeded BOOLEAN NOT NULL DEFAULT TRUE,
    VarianceApprovalWorkflowId BIGINT NULL,
    CandidateResponseValidityDays INT NOT NULL DEFAULT 7,
    RequireAcceptedOfferToAdvance BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_recruitment_stage_offer_config (PipelineStageId),
    CONSTRAINT FK_recruitment_stage_offer_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_transitions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineVersionId BIGINT NOT NULL,
    FromStageId BIGINT NOT NULL,
    ToStageId BIGINT NOT NULL,
    OutcomeCode VARCHAR(80) NOT NULL DEFAULT 'ADVANCE',
    ActionLabel VARCHAR(120) NOT NULL DEFAULT 'Move',
    ApprovalWorkflowId BIGINT NULL,
    RequiresReason BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    UNIQUE KEY UX_recruitment_pipeline_transition (PipelineVersionId,FromStageId,ToStageId,OutcomeCode),
    INDEX IX_recruitment_pipeline_transition_from (FromStageId,IsActive,DisplayOrder),
    CONSTRAINT FK_recruitment_pipeline_transition_version FOREIGN KEY (PipelineVersionId) REFERENCES recruitment_pipeline_versions(Id) ON DELETE CASCADE,
    CONSTRAINT FK_recruitment_pipeline_transition_from FOREIGN KEY (FromStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE,
    CONSTRAINT FK_recruitment_pipeline_transition_to FOREIGN KEY (ToStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_transition_rules (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    TransitionId BIGINT NOT NULL,
    RuleType VARCHAR(80) NOT NULL,
    ComparisonOperator VARCHAR(20) NOT NULL DEFAULT 'EQ',
    TextValue VARCHAR(500) NULL,
    IntegerValue BIGINT NULL,
    DecimalValue DECIMAL(18,4) NULL,
    BooleanValue BOOLEAN NULL,
    IsMandatory BOOLEAN NOT NULL DEFAULT TRUE,
    ErrorMessage VARCHAR(500) NOT NULL DEFAULT '',
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_recruitment_pipeline_transition_rule (TransitionId,DisplayOrder),
    CONSTRAINT FK_recruitment_transition_rule_transition FOREIGN KEY (TransitionId) REFERENCES recruitment_pipeline_transitions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_interview_competency_definitions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    CompetencyCode VARCHAR(80) NOT NULL,
    CompetencyName VARCHAR(180) NOT NULL,
    Description VARCHAR(1000) NOT NULL DEFAULT '',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_recruitment_interview_competency (ClientId,CompetencyCode)
);
CREATE TABLE IF NOT EXISTS recruitment_interview_stage_configurations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    RoundNumber INT NOT NULL,
    InterviewType VARCHAR(80) NOT NULL DEFAULT 'Technical',
    DefaultDurationMinutes INT NOT NULL DEFAULT 60,
    MinimumPanelCount INT NOT NULL DEFAULT 1,
    MinimumPassingScore DECIMAL(5,2) NOT NULL DEFAULT 60,
    ScoreInputMode VARCHAR(40) NOT NULL DEFAULT 'PercentageWeighted',
    PanelAggregationMethod VARCHAR(40) NOT NULL DEFAULT 'Average',
    FeedbackRequired BOOLEAN NOT NULL DEFAULT TRUE,
    CalendarEnabled BOOLEAN NOT NULL DEFAULT TRUE,
    AllowReschedule BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_recruitment_interview_stage_config (PipelineStageId),
    CONSTRAINT FK_recruitment_interview_config_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_interview_stage_competencies (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    InterviewStageConfigurationId BIGINT NOT NULL,
    CompetencyId BIGINT NOT NULL,
    WeightPercent DECIMAL(5,2) NOT NULL DEFAULT 0,
    MinimumScore DECIMAL(5,2) NOT NULL DEFAULT 0,
    DisplayOrder INT NOT NULL DEFAULT 100,
    UNIQUE KEY UX_recruitment_interview_stage_competency (InterviewStageConfigurationId,CompetencyId),
    CONSTRAINT FK_recruitment_interview_stage_comp_config FOREIGN KEY (InterviewStageConfigurationId) REFERENCES recruitment_interview_stage_configurations(Id) ON DELETE CASCADE,
    CONSTRAINT FK_recruitment_interview_stage_comp_master FOREIGN KEY (CompetencyId) REFERENCES recruitment_interview_competency_definitions(Id)
);
CREATE TABLE IF NOT EXISTS recruitment_position_pipeline_assignments (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PositionId BIGINT NOT NULL,
    JobPostingId BIGINT NULL,
    PipelineVersionId BIGINT NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    AssignedByUserId INT NOT NULL,
    AssignedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_recruitment_position_pipeline (PositionId,IsActive,AssignedAtUtc),
    INDEX IX_recruitment_posting_pipeline (JobPostingId,IsActive),
    CONSTRAINT FK_recruitment_assignment_pipeline_version FOREIGN KEY (PipelineVersionId) REFERENCES recruitment_pipeline_versions(Id),
    CONSTRAINT FK_recruitment_assignment_job_posting FOREIGN KEY (JobPostingId) REFERENCES recruitment_job_postings(Id)
);
CREATE TABLE IF NOT EXISTS recruitment_application_pipeline_instances (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ApplicationId BIGINT NOT NULL,
    PipelineVersionId BIGINT NOT NULL,
    PositionPipelineAssignmentId BIGINT NOT NULL,
    CurrentStageInstanceId BIGINT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Active',
    StartedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CompletedAtUtc DATETIME NULL,
    UNIQUE KEY UX_recruitment_application_pipeline (ApplicationId),
    INDEX IX_recruitment_application_pipeline_status (PipelineVersionId,Status),
    CONSTRAINT FK_recruitment_application_pipeline_version FOREIGN KEY (PipelineVersionId) REFERENCES recruitment_pipeline_versions(Id),
    CONSTRAINT FK_recruitment_application_pipeline_assignment FOREIGN KEY (PositionPipelineAssignmentId) REFERENCES recruitment_position_pipeline_assignments(Id)
);
CREATE TABLE IF NOT EXISTS recruitment_application_stage_instances (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ApplicationPipelineInstanceId BIGINT NOT NULL,
    ApplicationId BIGINT NOT NULL,
    PipelineStageId BIGINT NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Active',
    OutcomeCode VARCHAR(80) NOT NULL DEFAULT '',
    EnteredAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    DueAtUtc DATETIME NULL,
    ExitedAtUtc DATETIME NULL,
    ActiveDurationSeconds BIGINT NOT NULL DEFAULT 0,
    PausedDurationSeconds BIGINT NOT NULL DEFAULT 0,
    EnteredByUserId INT NOT NULL,
    ExitedByUserId INT NULL,
    INDEX IX_recruitment_application_stage_active (ApplicationId,Status,EnteredAtUtc),
    INDEX IX_recruitment_application_stage_sla (Status,DueAtUtc),
    CONSTRAINT FK_recruitment_app_stage_pipeline FOREIGN KEY (ApplicationPipelineInstanceId) REFERENCES recruitment_application_pipeline_instances(Id) ON DELETE CASCADE,
    CONSTRAINT FK_recruitment_app_stage_definition FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id)
);
CREATE TABLE IF NOT EXISTS recruitment_stage_action_executions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ApplicationId BIGINT NOT NULL,
    StageInstanceId BIGINT NOT NULL,
    StageActionId BIGINT NOT NULL,
    TriggerEvent VARCHAR(40) NOT NULL,
    ActionCode VARCHAR(80) NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Pending',
    IsBlocking BOOLEAN NOT NULL DEFAULT FALSE,
    WorkflowInstanceId BIGINT NULL,
    NotificationQueueId BIGINT NULL,
    CandidateActionSessionId BIGINT NULL,
    ApplicationScoreId BIGINT NULL,
    ErrorMessage VARCHAR(1000) NOT NULL DEFAULT '',
    StartedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CompletedAtUtc DATETIME(6) NULL,
    UNIQUE KEY UX_recruitment_stage_action_execution (StageInstanceId,StageActionId,TriggerEvent),
    INDEX IX_recruitment_stage_action_execution_application (ApplicationId,Status,StartedAtUtc),
    INDEX IX_recruitment_stage_action_execution_workflow (WorkflowInstanceId),
    CONSTRAINT FK_recruitment_stage_action_execution_stage FOREIGN KEY (StageInstanceId) REFERENCES recruitment_application_stage_instances(Id) ON DELETE CASCADE,
    CONSTRAINT FK_recruitment_stage_action_execution_action FOREIGN KEY (StageActionId) REFERENCES recruitment_pipeline_stage_actions(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_stage_pause_periods (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    StageInstanceId BIGINT NOT NULL,
    Reason VARCHAR(500) NOT NULL,
    PausedByUserId INT NOT NULL,
    PausedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ResumedByUserId INT NULL,
    ResumedAtUtc DATETIME NULL,
    DurationSeconds BIGINT NOT NULL DEFAULT 0,
    INDEX IX_recruitment_stage_pause_open (StageInstanceId,ResumedAtUtc),
    CONSTRAINT FK_recruitment_stage_pause_instance FOREIGN KEY (StageInstanceId) REFERENCES recruitment_application_stage_instances(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_stage_events (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    StageInstanceId BIGINT NOT NULL,
    EventType VARCHAR(80) NOT NULL,
    EventTitle VARCHAR(180) NOT NULL,
    EventDetails VARCHAR(1000) NOT NULL DEFAULT '',
    ActorUserId INT NULL,
    OccurredAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_recruitment_stage_event (StageInstanceId,OccurredAtUtc),
    CONSTRAINT FK_recruitment_stage_event_instance FOREIGN KEY (StageInstanceId) REFERENCES recruitment_application_stage_instances(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_pipeline_transition_requests (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ApplicationId BIGINT NOT NULL,
    StageInstanceId BIGINT NOT NULL,
    TransitionId BIGINT NOT NULL,
    Reason VARCHAR(1000) NOT NULL DEFAULT '',
    Status VARCHAR(40) NOT NULL DEFAULT 'Requested',
    WorkflowInstanceId BIGINT NULL,
    RequestedByUserId INT NOT NULL,
    RequestedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    DecidedByUserId INT NULL,
    DecidedAtUtc DATETIME NULL,
    AppliedAtUtc DATETIME NULL,
    INDEX IX_recruitment_transition_request_application (ApplicationId,Status,RequestedAtUtc),
    INDEX IX_recruitment_transition_request_workflow (WorkflowInstanceId),
    CONSTRAINT FK_recruitment_transition_request_stage FOREIGN KEY (StageInstanceId) REFERENCES recruitment_application_stage_instances(Id),
    CONSTRAINT FK_recruitment_transition_request_transition FOREIGN KEY (TransitionId) REFERENCES recruitment_pipeline_transitions(Id)
);");

        await EnsureColumnAsync(db, "recruitment_pipeline_versions", "ScopeType", "VARCHAR(40) NOT NULL DEFAULT 'Application'");
        await EnsureColumnAsync(db, "recruitment_pipeline_versions", "SlaMode", "VARCHAR(40) NOT NULL DEFAULT 'StageEntry'");
        await EnsureColumnAsync(db, "recruitment_pipeline_versions", "OverallSlaMinutes", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_pipeline_stages", "CardScope", "VARCHAR(40) NOT NULL DEFAULT 'Application' AFTER StageType");
        await BackfillStageCardScopesAsync(db);
        await db.ExecuteAsync("UPDATE recruitment_pipeline_stages SET CardScope='Application' WHERE CardScope IS NULL OR CardScope NOT IN ('Position','Application')");

        await EnsureColumnAsync(db, "recruitment_open_positions", "ApprovedJobDescriptionVersionId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_candidate_applications", "PipelineInstanceId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_candidate_applications", "CurrentPipelineStageInstanceId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_candidate_applications", "JobPostingId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_interviews", "PipelineStageInstanceId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_interviews", "RoundConfigurationId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_interviews", "TimeZoneId", "VARCHAR(80) NOT NULL DEFAULT 'Asia/Kolkata'");
        await EnsureColumnAsync(db, "recruitment_interviews", "AttemptNumber", "INT NOT NULL DEFAULT 1");
        await EnsureColumnAsync(db, "recruitment_interviews", "RescheduleCount", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_interview_stage_configurations", "ScoreInputMode", "VARCHAR(40) NOT NULL DEFAULT 'PercentageWeighted'");
        await EnsureColumnAsync(db, "recruitment_interview_stage_configurations", "PanelAggregationMethod", "VARCHAR(40) NOT NULL DEFAULT 'Average'");
        await EnsureColumnAsync(db, "recruitment_stage_action_executions", "NotificationQueueId", "BIGINT NULL AFTER WorkflowInstanceId");
        await EnsureColumnAsync(db, "recruitment_stage_action_executions", "CandidateActionSessionId", "BIGINT NULL AFTER NotificationQueueId");
        await EnsureColumnAsync(db, "recruitment_stage_action_executions", "ApplicationScoreId", "BIGINT NULL AFTER CandidateActionSessionId");
        await DropColumnIfExistsAsync(db, "recruitment_stage_external_form_configurations", "RequireEmailVerification");
        await DropColumnIfExistsAsync(db, "recruitment_pipeline_stage_actions", "TargetOutcomeCode");
    }

    public async Task<IEnumerable<RecruitmentJobDescriptionVersion>> GetJobDescriptionVersionsAsync(long requisitionId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentJobDescriptionVersion>(@"SELECT j.* FROM recruitment_job_description_versions j
JOIN recruitment_requisitions r ON r.Id=j.RequisitionId
WHERE j.RequisitionId=@RequisitionId AND (@ClientId IS NULL OR r.ClientId=@ClientId)
ORDER BY j.VersionNumber DESC", new { RequisitionId = requisitionId, user.ClientId });
    }

    public async Task<RecruitmentJobDescriptionVersion?> GetJobDescriptionVersionAsync(long id, AuthUser? user = null)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await LoadJobDescriptionAsync(db, id, user?.ClientId);
    }

    public async Task<(RecruitmentJobDescriptionVersion? Row, string Error)> SaveJobDescriptionVersionAsync(SaveRecruitmentJobDescriptionVersion request, AuthUser user)
    {
        if (request.RequisitionId <= 0) return (null, "Recruitment requisition is required.");
        if (string.IsNullOrWhiteSpace(request.Title)) return (null, "Job-description title is required.");
        if (string.IsNullOrWhiteSpace(request.Summary)) return (null, "Job-description summary is required.");
        if (request.Responsibilities.Count == 0 || request.Responsibilities.All(x => string.IsNullOrWhiteSpace(x.ResponsibilityText))) return (null, "Add at least one responsibility.");
        if (request.Skills.Count == 0 || request.Skills.Any(skill => string.IsNullOrWhiteSpace(skill.SkillName)))
            return (null, "Add at least one skill and complete every skill name.");
        if (!request.Skills.Any(skill => skill.IsRequired))
            return (null, "Mark at least one skill as must-have. Preferred skills are optional scoring evidence.");
        if (request.Skills.Any(skill => skill.MinimumYears < 0 || skill.WeightPercent is < 0 or > 100))
            return (null, "Skill experience and relative weights must be valid non-negative values; weights cannot exceed 100.");
        foreach (var bucket in request.Skills.GroupBy(skill => skill.IsRequired))
        {
            var usesCustomWeights = bucket.Any(skill => skill.WeightPercent > 0);
            if (usesCustomWeights && bucket.Any(skill => skill.WeightPercent <= 0))
                return (null, $"Give every {(bucket.Key ? "must-have" : "preferred")} skill a relative weight, or leave the whole group at zero for equal weighting.");
        }

        await using var db = Db();
        await db.OpenAsync();
        var clientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_requisitions WHERE Id=@Id", new { Id = request.RequisitionId });
        if (clientId is null || (user.ClientId is not null && user.ClientId != clientId)) return (null, "Recruitment requisition was not found.");

        await using var tx = await db.BeginTransactionAsync();
        long id = request.Id;
        if (id == 0)
        {
            var version = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(VersionNumber),0)+1 FROM recruitment_job_description_versions WHERE RequisitionId=@RequisitionId", request, tx);
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_job_description_versions
(RequisitionId,ClientId,VersionNumber,Title,Summary,RolePurpose,Status,CreatedByUserId)
VALUES (@RequisitionId,@ClientId,@VersionNumber,@Title,@Summary,@RolePurpose,'Draft',@UserId);SELECT LAST_INSERT_ID();",
                new { request.RequisitionId, ClientId = clientId.Value, VersionNumber = version, Title = request.Title.Trim(), Summary = request.Summary.Trim(), RolePurpose = request.RolePurpose.Trim(), UserId = user.Id }, tx);
        }
        else
        {
            var editable = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_job_description_versions
WHERE Id=@Id AND RequisitionId=@RequisitionId AND ClientId=@ClientId AND Status IN ('Draft','Sent Back')", new { Id = id, request.RequisitionId, ClientId = clientId.Value }, tx);
            if (editable == 0) return (null, "Only a draft or sent-back job-description version can be edited.");
            await db.ExecuteAsync(@"UPDATE recruitment_job_description_versions SET Title=@Title,Summary=@Summary,RolePurpose=@RolePurpose,
Status='Draft',WorkflowInstanceId=NULL,ApprovedByUserId=NULL,ApprovedAtUtc=NULL,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id",
                new { Id = id, Title = request.Title.Trim(), Summary = request.Summary.Trim(), RolePurpose = request.RolePurpose.Trim() }, tx);
            await DeleteJobDescriptionChildrenAsync(db, tx, id);
        }

        await InsertJobDescriptionChildrenAsync(db, tx, id, request);
        await tx.CommitAsync();
        return (await LoadJobDescriptionAsync(db, id, user.ClientId), "");
    }

    public async Task<(RecruitmentJobDescriptionVersion? Row, string Error)> SubmitJobDescriptionForApprovalAsync(long id, long workflowId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await LoadJobDescriptionAsync(db, id, user.ClientId);
        if (row is null || (user.ClientId is not null && user.ClientId != row.ClientId)) return (null, "Job description was not found.");
        if (!row.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase) && !row.Status.Equals("Sent Back", StringComparison.OrdinalIgnoreCase)) return (null, "Only a draft or sent-back job description can be submitted.");
        var completenessError = ValidateJobDescriptionCompleteness(row);
        if (completenessError.Length > 0) return (null, completenessError);
        var workflow = await db.QueryFirstOrDefaultAsync<WorkflowMaster>(@"SELECT * FROM workflowmasters
WHERE Id=@WorkflowId AND IsActive=TRUE AND (ClientId=@ClientId OR ClientId IS NULL)", new { WorkflowId = workflowId, row.ClientId });
        if (workflow is null) return (null, "Select an active approval workflow belonging to this client.");
        if (!workflow.ResourceType.Equals("RecruitmentJobDescription", StringComparison.OrdinalIgnoreCase))
            return (null, "The selected workflow is not configured for job-description approval.");
        var payloadJson = await BuildJobDescriptionApprovalSnapshotAsync(db, row, user);
        var instance = await workflows.StartAsync(new StartWorkflowRequest { WorkflowId = checked((int)workflowId), ResourceType = "RecruitmentJobDescription", ResourceId = id.ToString(), PayloadJson = payloadJson }, user.Id);
        if (instance is null) return (null, "Approval workflow could not start. Check workflow stages and approvers.");
        await db.ExecuteAsync("UPDATE recruitment_job_description_versions SET Status='Pending Approval',WorkflowInstanceId=@WorkflowInstanceId,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id, WorkflowInstanceId = instance.Id });
        return (await LoadJobDescriptionAsync(db, id, user.ClientId), "");
    }

    public async Task<(RecruitmentJobDescriptionVersion? Row, string Error)> ApproveJobDescriptionDirectlyAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await LoadJobDescriptionAsync(db, id, user.ClientId);
        if (row is null) return (null, "Job description was not found in your permitted client scope.");
        if (!row.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase) && !row.Status.Equals("Sent Back", StringComparison.OrdinalIgnoreCase))
            return (null, "Only a draft or sent-back job description can be approved directly.");
        var completenessError = ValidateJobDescriptionCompleteness(row);
        if (completenessError.Length > 0) return (null, completenessError);

        var snapshotJson = await BuildJobDescriptionApprovalSnapshotAsync(db, row, user);
        await using var transaction = await db.BeginTransactionAsync();
        var updated = await db.ExecuteAsync(@"UPDATE recruitment_job_description_versions SET Status='Approved',WorkflowInstanceId=NULL,
ApprovedByUserId=@UserId,ApprovedAtUtc=UTC_TIMESTAMP(),UpdatedAtUtc=UTC_TIMESTAMP()
WHERE Id=@Id AND Status IN ('Draft','Sent Back')", new { Id = id, UserId = user.Id }, transaction);
        if (updated == 0) return (null, "The job description changed before it could be approved. Refresh and try again.");
        await BindApprovedJobDescriptionAsync(db, id, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId)
VALUES ('RecruitmentJobDescription',@Id,'Direct Approval',@Json,@UserId)", new { Id = id, Json = snapshotJson, UserId = user.Id }, transaction);
        await transaction.CommitAsync();
        return (await LoadJobDescriptionAsync(db, id, user.ClientId), "");
    }

    public async Task<(RecruitmentJobDescriptionVersion? Row, string Error)> SyncJobDescriptionWorkflowStatusAsync(long id, string workflowStatus, AuthUser user)
    {
        var status = NormalizeWorkflowDecision(workflowStatus);
        if (status.Length == 0) return (null, "Unsupported job-description workflow status.");
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentJobDescriptionVersion>("SELECT * FROM recruitment_job_description_versions WHERE Id=@Id", new { Id = id });
        if (row is null || (user.ClientId is not null && user.ClientId != row.ClientId)) return (null, "Job description was not found.");
        await db.ExecuteAsync(@"UPDATE recruitment_job_description_versions SET Status=@Status,
ApprovedByUserId=CASE WHEN @Status='Approved' THEN @UserId ELSE NULL END,
ApprovedAtUtc=CASE WHEN @Status='Approved' THEN UTC_TIMESTAMP() ELSE NULL END,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id",
            new { Id = id, Status = status, UserId = user.Id });
        if (status == "Approved")
            await BindApprovedJobDescriptionAsync(db, id);
        return (await LoadJobDescriptionAsync(db, id, user.ClientId), "");
    }

    public async Task<IEnumerable<RecruitmentJobPosting>> GetJobPostingsAsync(int? clientId, AuthUser user)
    {
        var scope = user.ClientId ?? (clientId is > 0 ? clientId : null);
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentJobPosting>(JobPostingSelect + " WHERE (@ClientId IS NULL OR p.ClientId=@ClientId) ORDER BY p.CreatedAtUtc DESC", new { ClientId = scope });
    }

    public async Task<RecruitmentJobPosting?> GetJobPostingAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await GetJobPostingAsync(db, id, user.ClientId);
    }

    public async Task<(RecruitmentJobPosting? Row, string Error)> SaveJobPostingAsync(SaveRecruitmentJobPosting request, AuthUser user)
    {
        if (request.PositionId <= 0 || request.JobDescriptionVersionId <= 0) return (null, "Position and approved job description are required.");
        if (request.ClosesAtUtc is not null && request.OpensAtUtc is not null && request.ClosesAtUtc <= request.OpensAtUtc) return (null, "Posting close time must be after its open time.");
        if (request.MaximumApplications is <= 0) return (null, "Maximum applications must be greater than zero when specified.");
        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<PostingSourceRow>(@"SELECT p.ClientId,p.RequisitionId,p.PositionTitle,j.Status JobDescriptionStatus,j.RequisitionId JobDescriptionRequisitionId
FROM recruitment_open_positions p JOIN recruitment_job_description_versions j ON j.Id=@JobDescriptionVersionId WHERE p.Id=@PositionId",
            new { request.PositionId, request.JobDescriptionVersionId });
        if (source is null || source.RequisitionId != source.JobDescriptionRequisitionId || !source.JobDescriptionStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase)) return (null, "Select an approved job-description version for this position.");
        if (user.ClientId is not null && user.ClientId != source.ClientId) return (null, "Open position was not found.");
        if (request.ApplicationFormVersionId is > 0)
        {
            var validForm = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_versions v
JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE v.Id=@Id AND v.Status IN ('Published','Retired') AND d.ClientId IN (0,@ClientId)",
                new { Id = request.ApplicationFormVersionId.Value, source.ClientId });
            if (validForm == 0) return (null, "Select a published application-form version belonging to this client.");
        }
        long id = request.Id;
        if (id == 0)
        {
            var slug = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_job_postings
(ClientId,PositionId,JobDescriptionVersionId,ApplicationFormVersionId,PublicSlug,PublicTitle,Status,OpensAtUtc,ClosesAtUtc,MaximumApplications,SearchEngineVisible,CreatedByUserId)
VALUES (@ClientId,@PositionId,@JobDescriptionVersionId,@ApplicationFormVersionId,@PublicSlug,@PublicTitle,'Draft',@OpensAtUtc,@ClosesAtUtc,@MaximumApplications,@SearchEngineVisible,@UserId);SELECT LAST_INSERT_ID();",
                new { source.ClientId, request.PositionId, request.JobDescriptionVersionId, request.ApplicationFormVersionId, PublicSlug = slug, PublicTitle = string.IsNullOrWhiteSpace(request.PublicTitle) ? source.PositionTitle : request.PublicTitle.Trim(), request.OpensAtUtc, request.ClosesAtUtc, request.MaximumApplications, request.SearchEngineVisible, UserId = user.Id });
        }
        else
        {
            var updated = await db.ExecuteAsync(@"UPDATE recruitment_job_postings SET JobDescriptionVersionId=@JobDescriptionVersionId,
ApplicationFormVersionId=@ApplicationFormVersionId,PublicTitle=@PublicTitle,OpensAtUtc=@OpensAtUtc,ClosesAtUtc=@ClosesAtUtc,
MaximumApplications=@MaximumApplications,SearchEngineVisible=@SearchEngineVisible,UpdatedAtUtc=UTC_TIMESTAMP()
WHERE Id=@Id AND ClientId=@ClientId AND PositionId=@PositionId AND Status='Draft'", new { Id = id, source.ClientId, request.PositionId, request.JobDescriptionVersionId, request.ApplicationFormVersionId, PublicTitle = string.IsNullOrWhiteSpace(request.PublicTitle) ? source.PositionTitle : request.PublicTitle.Trim(), request.OpensAtUtc, request.ClosesAtUtc, request.MaximumApplications, request.SearchEngineVisible });
            if (updated == 0) return (null, "Only a draft job posting can be edited.");
        }
        return (await GetJobPostingAsync(db, id, user.ClientId), "");
    }

    public async Task<(RecruitmentJobPosting? Row, string Error)> PublishJobPostingAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var posting = await GetJobPostingAsync(db, id, user.ClientId);
        if (posting is null) return (null, "Job posting was not found.");
        if (posting.Status is not ("Draft" or "Closed")) return (null, "Only a draft or closed job posting can be published.");
        if (posting.ClosesAtUtc is not null && posting.ClosesAtUtc <= DateTime.UtcNow) return (null, "Posting close time must be in the future.");
        if (posting.ApplicationFormVersionId is null) return (null, "Select a published application form before publishing this job.");
        var validForm = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_versions v
JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE v.Id=@Id AND v.Status IN ('Published','Retired') AND d.ClientId IN (0,@ClientId)",
            new { Id = posting.ApplicationFormVersionId.Value, posting.ClientId });
        if (validForm == 0) return (null, "The selected application-form version is unavailable for this client.");
        var pipeline = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_position_pipeline_assignments
WHERE PositionId=@PositionId AND (JobPostingId IS NULL OR JobPostingId=@Id) AND IsActive=TRUE", new { posting.PositionId, Id = id });
        if (pipeline == 0) return (null, "Assign a published hiring pipeline before publishing this job.");
        var publicPortalBaseUrl = await db.ExecuteScalarAsync<string?>(@"SELECT PublicPortalBaseUrl FROM recruitment_settings
WHERE ClientId=@ClientId AND RecruitmentEnabled=TRUE AND EnableCandidatePortal=TRUE AND IsActive=TRUE LIMIT 1", new { ClientId = posting.ClientId });
        if (RecruitmentPublicUrls.BuildCareerUrl(publicPortalBaseUrl ?? "", posting.PublicSlug).Length == 0)
            return (null, "Enable the candidate portal and configure a valid public HTTP or HTTPS base URL before publishing this job.");
        await db.ExecuteAsync("UPDATE recruitment_job_postings SET Status='Published',PublishedAtUtc=UTC_TIMESTAMP(),UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id });
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET Status='Published',PublishedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@PositionId", posting);
        return (await GetJobPostingAsync(db, id, user.ClientId), "");
    }

    public async Task<bool> CloseJobPostingAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.ExecuteAsync("UPDATE recruitment_job_postings SET Status='Closed',UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, user.ClientId }) > 0;
    }

    public async Task<(bool Ok, string Error)> DeleteJobDescriptionVersionAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentJobDescriptionVersion>(
            "SELECT * FROM recruitment_job_description_versions WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = id, user.ClientId });
        if (row is null) return (false, "Job-description version was not found in your permitted client scope.");
        var postings = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_job_postings WHERE JobDescriptionVersionId=@Id", new { Id = id });
        if (postings > 0) return (false, $"Delete the {postings} linked job posting(s) first.");
        var scoreSnapshots = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_application_score_position_snapshots WHERE JobDescriptionVersionId=@Id", new { Id = id });
        if (scoreSnapshots > 0) return (false, "This job description has ATS scoring evidence and must be retained for audit.");

        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET ApprovedJobDescriptionVersionId=NULL WHERE ApprovedJobDescriptionVersionId=@Id", new { Id = id }, transaction);
        await DeleteJobDescriptionChildrenAsync(db, transaction, id);
        await db.ExecuteAsync("DELETE FROM recruitment_job_description_versions WHERE Id=@Id", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return (true, "");
    }

    public async Task<(bool Ok, string Error)> DeleteJobPostingAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentJobPosting>(
            "SELECT * FROM recruitment_job_postings WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = id, user.ClientId });
        if (row is null) return (false, "Job posting was not found in your permitted client scope.");
        var applications = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications WHERE JobPostingId=@Id", new { Id = id });
        if (applications > 0) return (false, $"Delete the {applications} linked application(s) first.");

        await using var transaction = await db.BeginTransactionAsync();
        // Public application sessions are short-lived access tokens, not business records.
        // Once a posting has no candidate applications, revoke and remove its sessions so
        // the restrictive FK cannot turn an admin delete into an unhandled server error.
        await db.ExecuteAsync("UPDATE form_public_sessions SET RevokedAtUtc=COALESCE(RevokedAtUtc,UTC_TIMESTAMP(6)) WHERE PostingId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM form_public_sessions WHERE PostingId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_position_pipeline_assignments WHERE JobPostingId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_job_postings WHERE Id=@Id", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return (true, "");
    }

    public async Task<RecruitmentPublicJobPosting?> GetPublicJobPostingAsync(string publicSlug)
    {
        if (!Regex.IsMatch(publicSlug ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) return null;
        await using var db = Db();
        await db.OpenAsync();
        var posting = await db.QueryFirstOrDefaultAsync<RecruitmentJobPosting>(JobPostingSelect + @" JOIN recruitment_settings settings ON settings.ClientId=p.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE WHERE p.PublicSlug=@PublicSlug AND p.Status='Published'
AND (p.OpensAtUtc IS NULL OR p.OpensAtUtc<=UTC_TIMESTAMP()) AND (p.ClosesAtUtc IS NULL OR p.ClosesAtUtc>UTC_TIMESTAMP())
AND (p.MaximumApplications IS NULL OR p.ApplicationCount<p.MaximumApplications)", new { PublicSlug = publicSlug });
        if (posting is null) return null;
        var jd = await LoadJobDescriptionAsync(db, posting.JobDescriptionVersionId, null);
        return jd is null ? null : new RecruitmentPublicJobPosting { Posting = posting, JobDescription = jd };
    }

    public async Task<IEnumerable<RecruitmentPipelineDefinition>> GetPipelinesAsync(int? clientId, AuthUser user)
    {
        var scope = user.ClientId ?? clientId;
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentPipelineDefinition>(@"SELECT d.*,COALESCE(c.Name,'') ClientName
FROM recruitment_pipeline_definitions d LEFT JOIN clients c ON c.Id=d.ClientId
WHERE (@ClientId IS NULL OR d.ClientId=@ClientId) ORDER BY d.IsActive DESC,d.PipelineName", new { ClientId = scope });
    }

    public async Task<(RecruitmentPipelineDefinition? Row, string Error)> SavePipelineAsync(SaveRecruitmentPipelineDefinition request, AuthUser user)
    {
        var code = Regex.Replace((request.PipelineCode ?? "").Trim().ToUpperInvariant(), "\\s+", "_");
        if (!Regex.IsMatch(code, "^[A-Z0-9_-]{2,80}$", RegexOptions.CultureInvariant)) return (null, "Pipeline code must contain only letters, numbers, underscore or hyphen.");
        if (string.IsNullOrWhiteSpace(request.PipelineName)) return (null, "Pipeline name is required.");
        var clientId = user.ClientId ?? request.ClientId;
        if (clientId <= 0 || (user.ClientId is not null && request.ClientId > 0 && request.ClientId != user.ClientId)) return (null, "A valid client is required.");
        await using var db = Db();
        await db.OpenAsync();
        long id = request.Id;
        try
        {
            if (id == 0)
                id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_definitions
(ClientId,PipelineCode,PipelineName,Description,IsActive,CreatedByUserId)
VALUES (@ClientId,@PipelineCode,@PipelineName,@Description,@IsActive,@UserId);SELECT LAST_INSERT_ID();",
                    new { ClientId = clientId, PipelineCode = code, PipelineName = request.PipelineName.Trim(), Description = request.Description.Trim(), request.IsActive, UserId = user.Id });
            else
            {
                var updated = await db.ExecuteAsync(@"UPDATE recruitment_pipeline_definitions SET PipelineCode=@PipelineCode,PipelineName=@PipelineName,
Description=@Description,IsActive=@IsActive,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id AND ClientId=@ClientId",
                    new { Id = id, ClientId = clientId, PipelineCode = code, PipelineName = request.PipelineName.Trim(), Description = request.Description.Trim(), request.IsActive });
                if (updated == 0) return (null, "Pipeline was not found.");
            }
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            return (null, "Pipeline code already exists for this client.");
        }
        return (await db.QueryFirstOrDefaultAsync<RecruitmentPipelineDefinition>("SELECT * FROM recruitment_pipeline_definitions WHERE Id=@Id", new { Id = id }), "");
    }

    public async Task<RecruitmentPipelineVersion?> GetPipelineVersionAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var version = await db.QueryFirstOrDefaultAsync<RecruitmentPipelineVersion>(@"SELECT v.* FROM recruitment_pipeline_versions v
JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId WHERE v.Id=@Id AND (@ClientId IS NULL OR d.ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (version is null) return null;
        version.Stages = (await db.QueryAsync<RecruitmentPipelineStage>("SELECT * FROM recruitment_pipeline_stages WHERE PipelineVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id })).ToList();
        version.Transitions = (await db.QueryAsync<RecruitmentPipelineTransition>(@"SELECT t.*,f.StageCode FromStageCode,n.StageCode ToStageCode
FROM recruitment_pipeline_transitions t JOIN recruitment_pipeline_stages f ON f.Id=t.FromStageId JOIN recruitment_pipeline_stages n ON n.Id=t.ToStageId
WHERE t.PipelineVersionId=@Id ORDER BY t.DisplayOrder,t.Id", new { Id = id })).ToList();
        foreach (var transition in version.Transitions)
            transition.Rules = (await db.QueryAsync<RecruitmentPipelineTransitionRule>("SELECT * FROM recruitment_pipeline_transition_rules WHERE TransitionId=@Id ORDER BY DisplayOrder,Id", new { transition.Id })).ToList();
        foreach (var stage in version.Stages)
        {
            stage.Actions = (await db.QueryAsync<RecruitmentPipelineStageAction>("SELECT * FROM recruitment_pipeline_stage_actions WHERE PipelineStageId=@Id ORDER BY ExecutionOrder,Id", new { stage.Id })).ToList();
            foreach (var action in stage.Actions)
                action.Recipients = (await db.QueryAsync<RecruitmentStageActionRecipient>("SELECT * FROM recruitment_stage_action_recipients WHERE StageActionId=@Id ORDER BY DisplayOrder,Id", new { action.Id })).ToList();
            stage.DefaultPanelMembers = (await db.QueryAsync<RecruitmentStageDefaultPanelMember>(@"SELECT panel.*,COALESCE(userRow.DisplayName,userRow.Email,'') PanelUserName
FROM recruitment_stage_default_panel_members panel LEFT JOIN authusers userRow ON userRow.Id=panel.PanelUserId
WHERE panel.PipelineStageId=@Id ORDER BY panel.DisplayOrder,panel.Id", new { stage.Id })).ToList();
            stage.AtsConfiguration = await db.QueryFirstOrDefaultAsync<RecruitmentStageAtsConfiguration>("SELECT * FROM recruitment_stage_ats_configurations WHERE PipelineStageId=@Id", new { stage.Id });
            stage.ExternalFormConfiguration = await db.QueryFirstOrDefaultAsync<RecruitmentStageExternalFormConfiguration>("SELECT * FROM recruitment_stage_external_form_configurations WHERE PipelineStageId=@Id", new { stage.Id });
            stage.AttachmentRequirements = (await db.QueryAsync<RecruitmentStageAttachmentRequirement>("SELECT * FROM recruitment_stage_attachment_requirements WHERE PipelineStageId=@Id ORDER BY DisplayOrder,Id", new { stage.Id })).ToList();
            stage.ProcessDocumentRequirements = (await db.QueryAsync<RecruitmentStageProcessDocumentRequirement>("SELECT * FROM recruitment_stage_process_document_requirements WHERE PipelineStageId=@Id ORDER BY DisplayOrder,Id", new { stage.Id })).ToList();
            stage.OfferConfiguration = await db.QueryFirstOrDefaultAsync<RecruitmentStageOfferConfiguration>("SELECT * FROM recruitment_stage_offer_configurations WHERE PipelineStageId=@Id", new { stage.Id });
        }
        foreach (var stage in version.Stages.Where(x => x.StageType.Equals("Interview", StringComparison.OrdinalIgnoreCase)))
        {
            stage.InterviewConfiguration = await db.QueryFirstOrDefaultAsync<RecruitmentInterviewStageConfiguration>("SELECT * FROM recruitment_interview_stage_configurations WHERE PipelineStageId=@Id", new { stage.Id });
            if (stage.InterviewConfiguration is not null)
                stage.InterviewConfiguration.Competencies = (await db.QueryAsync<RecruitmentInterviewStageCompetency>(@"SELECT m.*,c.CompetencyName FROM recruitment_interview_stage_competencies m
JOIN recruitment_interview_competency_definitions c ON c.Id=m.CompetencyId WHERE m.InterviewStageConfigurationId=@Id ORDER BY m.DisplayOrder,m.Id", new { Id = stage.InterviewConfiguration.Id })).ToList();
        }
        return version;
    }

    public async Task<IEnumerable<RecruitmentPipelineVersion>> GetPipelineVersionsAsync(long pipelineDefinitionId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentPipelineVersion>(@"SELECT v.* FROM recruitment_pipeline_versions v
JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId
WHERE v.PipelineDefinitionId=@Id AND (@ClientId IS NULL OR d.ClientId=@ClientId)
ORDER BY v.VersionNumber DESC", new { Id = pipelineDefinitionId, user.ClientId });
    }

    public async Task<(bool Ok, string Error)> DeletePipelineDefinitionAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var definition = await db.QueryFirstOrDefaultAsync<(long Id, int ClientId, string PipelineName)>(@"SELECT Id,ClientId,PipelineName
FROM recruitment_pipeline_definitions WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (definition.Id <= 0) return (false, "Pipeline definition was not found in your client scope.");

        var versions = (await db.QueryAsync<long>("SELECT Id FROM recruitment_pipeline_versions WHERE PipelineDefinitionId=@Id", new { Id = id })).ToArray();
        if (versions.Length == 0)
        {
            await db.ExecuteAsync("DELETE FROM recruitment_pipeline_definitions WHERE Id=@Id", new { Id = id });
            return (true, "");
        }

        var assignments = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM recruitment_position_pipeline_assignments WHERE PipelineVersionId IN @Ids", new { Ids = versions });
        if (assignments > 0)
            return (false, $"This pipeline is assigned to {assignments} position/posting record(s). Delete those hiring records or assignments first.");

        var applicationInstances = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM recruitment_application_pipeline_instances WHERE PipelineVersionId IN @Ids", new { Ids = versions });
        if (applicationInstances > 0)
            return (false, $"This pipeline contains {applicationInstances} candidate journey instance(s). Delete their applications first.");

        var positionInstances = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM recruitment_position_pipeline_instances WHERE PipelineVersionId IN @Ids", new { Ids = versions });
        if (positionInstances > 0)
            return (false, $"This pipeline contains {positionInstances} live cumulative hiring case(s). Delete those cases first.");

        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var stageIds = (await db.QueryAsync<long>(
                "SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId IN @Ids", new { Ids = versions }, tx)).ToArray();
            if (stageIds.Length > 0)
            {
                var actionIds = (await db.QueryAsync<long>(
                    "SELECT Id FROM recruitment_pipeline_stage_actions WHERE PipelineStageId IN @Ids", new { Ids = stageIds }, tx)).ToArray();
                if (actionIds.Length > 0)
                    await db.ExecuteAsync("DELETE FROM recruitment_stage_action_recipients WHERE StageActionId IN @Ids", new { Ids = actionIds }, tx);
                await db.ExecuteAsync("DELETE FROM recruitment_stage_default_panel_members WHERE PipelineStageId IN @Ids", new { Ids = stageIds }, tx);
            }
            await db.ExecuteAsync("UPDATE recruitment_pipeline_definitions SET CurrentPublishedVersionId=NULL WHERE Id=@Id", new { Id = id }, tx);
            await db.ExecuteAsync("DELETE FROM recruitment_pipeline_versions WHERE PipelineDefinitionId=@Id", new { Id = id }, tx);
            await db.ExecuteAsync("DELETE FROM recruitment_pipeline_definitions WHERE Id=@Id", new { Id = id }, tx);
            await tx.CommitAsync();
            return (true, "");
        }
        catch (MySqlException)
        {
            await tx.RollbackAsync();
            return (false, "This pipeline still has linked configuration or transaction data. Remove its dependent records first, then retry.");
        }
    }

    public async Task<(RecruitmentPipelineVersion? Row, string Error)> SavePipelineVersionAsync(SaveRecruitmentPipelineVersion request, AuthUser user)
    {
        request.ScopeType = Canonical(new[] { "Application", "Position", "Hybrid" }, request.ScopeType, "Application");
        request.SlaMode = Canonical(new[] { "StageEntry", "CumulativeFromAnchor" }, request.SlaMode, "StageEntry");
        request.OverallSlaMinutes = Math.Max(0, request.OverallSlaMinutes);
        NormalizeStageCardScopes(request);
        var validation = ValidatePipelineDraft(request);
        if (validation.Length > 0) return (null, validation);
        await using var db = Db();
        await db.OpenAsync();
        var definition = await db.QueryFirstOrDefaultAsync<RecruitmentPipelineDefinition>("SELECT * FROM recruitment_pipeline_definitions WHERE Id=@Id", new { Id = request.PipelineDefinitionId });
        if (definition is null || (user.ClientId is not null && user.ClientId != definition.ClientId)) return (null, "Pipeline definition was not found.");
        var competencyIds = request.Stages.SelectMany(x => x.InterviewConfiguration?.Competencies ?? []).Select(x => x.CompetencyId).Distinct().ToArray();
        if (competencyIds.Length > 0)
        {
            var validCompetencies = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_interview_competency_definitions
WHERE Id IN @Ids AND ClientId=@ClientId AND IsActive=TRUE", new { Ids = competencyIds, definition.ClientId });
            if (validCompetencies != competencyIds.Length) return (null, "One or more interview competencies are inactive or belong to another client.");
        }
        var panelUserIds = request.Stages.SelectMany(stage => stage.DefaultPanelMembers).Select(panel => panel.PanelUserId).Where(id => id > 0).Distinct().ToArray();
        if (panelUserIds.Length > 0)
        {
            var validPanelUsers = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers
WHERE Id IN @Ids AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Ids = panelUserIds, definition.ClientId });
            if (validPanelUsers != panelUserIds.Length) return (null, "One or more default panel members are inactive or outside this client.");
        }
        var recipientUserIds = request.Stages.SelectMany(stage => stage.Actions).SelectMany(action => action.Recipients)
            .Where(recipient => recipient.RecipientType.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase) && recipient.UserId is > 0)
            .Select(recipient => recipient.UserId!.Value).Distinct().ToArray();
        if (recipientUserIds.Length > 0)
        {
            var validRecipientUsers = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers
WHERE Id IN @Ids AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Ids = recipientUserIds, definition.ClientId });
            if (validRecipientUsers != recipientUserIds.Length) return (null, "One or more notification recipients are inactive or outside this client.");
        }
        var attachmentConfigurationIds = request.Stages.SelectMany(x => x.AttachmentRequirements).Select(x => x.AttachmentFieldConfigurationId).Distinct().ToArray();
        if (attachmentConfigurationIds.Length > 0)
        {
            var validAttachments = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attachment_field_configurations
WHERE id IN @Ids AND client_id IN (0,@ClientId) AND is_active=TRUE", new { Ids = attachmentConfigurationIds, ClientId = definition.ClientId });
            if (validAttachments != attachmentConfigurationIds.Length) return (null, "One or more stage attachment configurations are inactive or belong to another client.");
        }
        var scoringProfileIds = request.Stages.Select(x => x.AtsConfiguration?.ScoringProfileId).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
        if (scoringProfileIds.Length > 0)
        {
            var validProfiles = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_ats_scoring_profiles
WHERE Id IN @Ids AND ClientId IN (0,@ClientId) AND IsActive=TRUE", new { Ids = scoringProfileIds, ClientId = definition.ClientId });
            if (validProfiles != scoringProfileIds.Length) return (null, "One or more ATS scoring profiles are inactive or belong to another client.");
        }
        var formVersionIds = request.Stages.Select(x => x.ExternalFormConfiguration?.FormVersionId).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
        if (formVersionIds.Length > 0)
        {
            var validForms = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_versions v
JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE v.Id IN @Ids AND v.Status IN ('Published','Retired') AND d.ClientId IN (0,@ClientId)", new { Ids = formVersionIds, ClientId = definition.ClientId });
            if (validForms != formVersionIds.Length) return (null, "One or more external-form versions are unavailable or belong to another client.");
        }
        foreach (var stage in request.Stages.Where(x => x.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase)
            || x.StageType.Equals("PreOnboarding", StringComparison.OrdinalIgnoreCase)))
        {
            if (stage.ExternalFormConfiguration is null || stage.ExternalFormConfiguration.FormVersionId <= 0)
                return (null, $"Select a published candidate form for {stage.StageName}.");
            var requiredConfigurationIds = stage.AttachmentRequirements.Select(x => x.AttachmentFieldConfigurationId).Distinct().ToArray();
            if (requiredConfigurationIds.Length == 0) continue;
            var mappedConfigurationCount = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(DISTINCT f.AttachmentFieldConfigurationId)
FROM form_fields f
JOIN form_field_types t ON t.Id=f.FieldTypeId
WHERE f.FormVersionId=@FormVersionId AND f.IsActive=TRUE AND t.TypeCode='UPLOAD'
  AND f.AttachmentFieldConfigurationId IN @ConfigurationIds", new
            {
                stage.ExternalFormConfiguration.FormVersionId,
                ConfigurationIds = requiredConfigurationIds
            });
            if (mappedConfigurationCount != requiredConfigurationIds.Length)
                return (null, $"The published form selected for {stage.StageName} does not contain every requested global document field.");
        }
        var templateIds = request.Stages.SelectMany(x => x.Actions.Select(a => a.TemplateId)
            .Append(x.OfferConfiguration?.OfferTemplateId)
            .Concat(x.ProcessDocumentRequirements.Select(document => document.TemplateId)))
            .Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
        if (templateIds.Length > 0)
        {
            var validTemplates = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_templates
WHERE Id IN @Ids AND ClientId IN (0,@ClientId) AND IsActive=TRUE", new { Ids = templateIds, ClientId = definition.ClientId });
            if (validTemplates != templateIds.Length) return (null, "One or more stage templates are inactive or belong to another client.");
        }
        var workflowValidation = await ValidateWorkflowBindingsAsync(db, request.Stages, request.Transitions, definition.ClientId);
        if (workflowValidation.Length > 0) return (null, workflowValidation);

        await using var tx = await db.BeginTransactionAsync();
        long versionId = request.Id;
        if (versionId == 0)
        {
            var number = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(VersionNumber),0)+1 FROM recruitment_pipeline_versions WHERE PipelineDefinitionId=@Id", new { Id = definition.Id }, tx);
            versionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_versions
(PipelineDefinitionId,VersionNumber,Status,ScopeType,SlaMode,OverallSlaMinutes,CreatedByUserId)
VALUES (@PipelineDefinitionId,@VersionNumber,'Draft',@ScopeType,@SlaMode,@OverallSlaMinutes,@UserId);SELECT LAST_INSERT_ID();",
                new { request.PipelineDefinitionId, VersionNumber = number, request.ScopeType, request.SlaMode, request.OverallSlaMinutes, UserId = user.Id }, tx);
        }
        else
        {
            var editable = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_pipeline_versions v JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId
WHERE v.Id=@Id AND v.PipelineDefinitionId=@DefinitionId AND v.Status='Draft' AND d.ClientId=@ClientId",
                new { Id = versionId, DefinitionId = definition.Id, definition.ClientId }, tx);
            if (editable == 0) return (null, "Only a draft pipeline version can be edited.");
            await db.ExecuteAsync(@"UPDATE recruitment_pipeline_versions SET ScopeType=@ScopeType,SlaMode=@SlaMode,
OverallSlaMinutes=@OverallSlaMinutes WHERE Id=@Id", new { Id = versionId, request.ScopeType, request.SlaMode, request.OverallSlaMinutes }, tx);
            await db.ExecuteAsync(@"DELETE recipient FROM recruitment_stage_action_recipients recipient
JOIN recruitment_pipeline_stage_actions actionRow ON actionRow.Id=recipient.StageActionId
JOIN recruitment_pipeline_stages stageRow ON stageRow.Id=actionRow.PipelineStageId
WHERE stageRow.PipelineVersionId=@Id;
DELETE panel FROM recruitment_stage_default_panel_members panel
JOIN recruitment_pipeline_stages stageRow ON stageRow.Id=panel.PipelineStageId
WHERE stageRow.PipelineVersionId=@Id", new { Id = versionId }, tx);
            await db.ExecuteAsync("DELETE FROM recruitment_pipeline_stages WHERE PipelineVersionId=@Id", new { Id = versionId }, tx);
        }

        var stageIdsByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var stageIdsByRequestId = new Dictionary<long, long>();
        foreach (var stage in request.Stages.OrderBy(x => x.DisplayOrder))
        {
            var stageCode = stage.StageCode.Trim().ToUpperInvariant();
            var stageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_stages
(PipelineVersionId,StageCode,StageName,StageType,CardScope,StageNumber,DisplayOrder,SlaDurationMinutes,SlaWarningMinutes,TargetOffsetMinutes,StakeholderCode,AllowPause,PauseBehavior,ApprovalWorkflowId,RequiresApproval,CalendarEnabled,AllowSkip,IsInitial,IsTerminal,IsActive)
VALUES (@PipelineVersionId,@StageCode,@StageName,@StageType,@CardScope,@StageNumber,@DisplayOrder,@SlaDurationMinutes,@SlaWarningMinutes,@TargetOffsetMinutes,@StakeholderCode,@AllowPause,@PauseBehavior,@ApprovalWorkflowId,@RequiresApproval,@CalendarEnabled,@AllowSkip,@IsInitial,@IsTerminal,@IsActive);SELECT LAST_INSERT_ID();",
                new { PipelineVersionId = versionId, StageCode = stageCode, StageName = stage.StageName.Trim(), stage.StageType, stage.CardScope, stage.StageNumber, stage.DisplayOrder, SlaDurationMinutes = Math.Max(0, stage.SlaDurationMinutes), SlaWarningMinutes = Math.Max(0, stage.SlaWarningMinutes), TargetOffsetMinutes = stage.TargetOffsetMinutes is < 0 ? null : stage.TargetOffsetMinutes, StakeholderCode = (stage.StakeholderCode ?? "").Trim().ToUpperInvariant(), stage.AllowPause, PauseBehavior = Canonical(new[] { "ShiftStageAndOverall", "ShiftStageOnly", "NoShift" }, stage.PauseBehavior, "ShiftStageAndOverall"), stage.ApprovalWorkflowId, stage.RequiresApproval, stage.CalendarEnabled, stage.AllowSkip, stage.IsInitial, stage.IsTerminal, stage.IsActive }, tx);
            stageIdsByCode[stageCode] = stageId;
            if (stage.Id != 0) stageIdsByRequestId[stage.Id] = stageId;
            await InsertStageBehaviorAsync(db, tx, stageId, stage);
            if (stage.StageType.Equals("Interview", StringComparison.OrdinalIgnoreCase))
                await InsertInterviewStageConfigurationAsync(db, tx, stageId, stage.InterviewConfiguration, definition.ClientId);
        }

        foreach (var transition in request.Transitions.OrderBy(x => x.DisplayOrder))
        {
            var fromId = ResolveStageId(transition.FromStageId, transition.FromStageCode, stageIdsByRequestId, stageIdsByCode);
            var toId = ResolveStageId(transition.ToStageId, transition.ToStageCode, stageIdsByRequestId, stageIdsByCode);
            if (fromId == 0 || toId == 0 || fromId == toId) return (null, "Every transition must reference two different stages in this pipeline version.");
            var transitionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_transitions
(PipelineVersionId,FromStageId,ToStageId,OutcomeCode,ActionLabel,ApprovalWorkflowId,RequiresReason,IsActive,DisplayOrder)
VALUES (@PipelineVersionId,@FromStageId,@ToStageId,@OutcomeCode,@ActionLabel,@ApprovalWorkflowId,@RequiresReason,@IsActive,@DisplayOrder);SELECT LAST_INSERT_ID();",
                new { PipelineVersionId = versionId, FromStageId = fromId, ToStageId = toId, OutcomeCode = transition.OutcomeCode.Trim().ToUpperInvariant(), ActionLabel = transition.ActionLabel.Trim(), transition.ApprovalWorkflowId, transition.RequiresReason, transition.IsActive, transition.DisplayOrder }, tx);
            foreach (var rule in transition.Rules.OrderBy(x => x.DisplayOrder))
                await db.ExecuteAsync(@"INSERT INTO recruitment_pipeline_transition_rules
(TransitionId,RuleType,ComparisonOperator,TextValue,IntegerValue,DecimalValue,BooleanValue,IsMandatory,ErrorMessage,DisplayOrder)
VALUES (@TransitionId,@RuleType,@ComparisonOperator,@TextValue,@IntegerValue,@DecimalValue,@BooleanValue,@IsMandatory,@ErrorMessage,@DisplayOrder)",
                    new { TransitionId = transitionId, RuleType = rule.RuleType.Trim().ToUpperInvariant(), ComparisonOperator = rule.ComparisonOperator.Trim().ToUpperInvariant(), rule.TextValue, rule.IntegerValue, rule.DecimalValue, rule.BooleanValue, rule.IsMandatory, rule.ErrorMessage, rule.DisplayOrder }, tx);
        }
        await tx.CommitAsync();
        return (await GetPipelineVersionAsync(versionId, user), "");
    }

    public async Task<(RecruitmentPipelineVersion? Row, string Error)> PublishPipelineVersionAsync(long id, AuthUser user)
    {
        var version = await GetPipelineVersionAsync(id, user);
        if (version is null) return (null, "Pipeline version was not found.");
        if (!version.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) return (null, "Only a draft pipeline version can be published.");
        var validation = ValidatePipelineDraft(new SaveRecruitmentPipelineVersion { Id = version.Id, PipelineDefinitionId = version.PipelineDefinitionId, ScopeType = version.ScopeType, SlaMode = version.SlaMode, OverallSlaMinutes = version.OverallSlaMinutes, Stages = version.Stages, Transitions = version.Transitions });
        if (validation.Length > 0) return (null, validation);
        await using var db = Db();
        await db.OpenAsync();
        var clientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_pipeline_definitions WHERE Id=@Id", new { Id = version.PipelineDefinitionId });
        if (!clientId.HasValue) return (null, "Pipeline definition was not found.");
        var workflowValidation = await ValidateWorkflowBindingsAsync(db, version.Stages, version.Transitions, clientId.Value);
        if (workflowValidation.Length > 0) return (null, workflowValidation);
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE recruitment_pipeline_versions SET Status='Retired' WHERE PipelineDefinitionId=@DefinitionId AND Status='Published' AND Id<>@Id", new { DefinitionId = version.PipelineDefinitionId, Id = id }, tx);
        await db.ExecuteAsync("UPDATE recruitment_pipeline_versions SET Status='Published',PublishedByUserId=@UserId,PublishedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id, UserId = user.Id }, tx);
        await db.ExecuteAsync("UPDATE recruitment_pipeline_definitions SET CurrentPublishedVersionId=@Id,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@DefinitionId", new { Id = id, DefinitionId = version.PipelineDefinitionId }, tx);
        await tx.CommitAsync();
        return (await GetPipelineVersionAsync(id, user), "");
    }

    public async Task<(RecruitmentPositionPipelineAssignment? Row, string Error)> AssignPipelineAsync(AssignRecruitmentPipelineRequest request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<AssignmentSourceRow>(@"SELECT p.ClientId,v.Status,d.ClientId PipelineClientId
FROM recruitment_open_positions p CROSS JOIN recruitment_pipeline_versions v JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId
WHERE p.Id=@PositionId AND v.Id=@PipelineVersionId", request);
        if (source is null || source.ClientId != source.PipelineClientId || !source.Status.Equals("Published", StringComparison.OrdinalIgnoreCase)) return (null, "Select a published pipeline belonging to the position's client.");
        if (user.ClientId is not null && user.ClientId != source.ClientId) return (null, "Open position was not found.");
        if (request.JobPostingId is not null)
        {
            var validPosting = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_job_postings WHERE Id=@Id AND PositionId=@PositionId AND ClientId=@ClientId", new { Id = request.JobPostingId, request.PositionId, source.ClientId });
            if (validPosting == 0) return (null, "Job posting does not belong to this open position.");
        }
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync(@"UPDATE recruitment_position_pipeline_assignments SET IsActive=FALSE
WHERE PositionId=@PositionId AND IsActive=TRUE
AND ((@JobPostingId IS NULL AND JobPostingId IS NULL) OR JobPostingId=@JobPostingId)", request, tx);
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_position_pipeline_assignments
(PositionId,JobPostingId,PipelineVersionId,IsActive,AssignedByUserId) VALUES (@PositionId,@JobPostingId,@PipelineVersionId,TRUE,@UserId);SELECT LAST_INSERT_ID();",
            new { request.PositionId, request.JobPostingId, request.PipelineVersionId, UserId = user.Id }, tx);
        await tx.CommitAsync();
        return (await db.QueryFirstAsync<RecruitmentPositionPipelineAssignment>("SELECT * FROM recruitment_position_pipeline_assignments WHERE Id=@Id", new { Id = id }), "");
    }

    public async Task<RecruitmentPositionPipelineAssignment?> GetPositionPipelineAssignmentAsync(long positionId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryFirstOrDefaultAsync<RecruitmentPositionPipelineAssignment>(@"SELECT a.* FROM recruitment_position_pipeline_assignments a
JOIN recruitment_open_positions p ON p.Id=a.PositionId
WHERE a.PositionId=@PositionId AND a.JobPostingId IS NULL AND a.IsActive=TRUE AND (@ClientId IS NULL OR p.ClientId=@ClientId)
ORDER BY a.AssignedAtUtc DESC,a.Id DESC LIMIT 1", new { PositionId = positionId, user.ClientId });
    }

    public async Task<IEnumerable<RecruitmentInterviewCompetencyDefinition>> GetInterviewCompetenciesAsync(int? clientId, AuthUser user)
    {
        var scope = user.ClientId ?? clientId;
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentInterviewCompetencyDefinition>("SELECT * FROM recruitment_interview_competency_definitions WHERE (@ClientId IS NULL OR ClientId=@ClientId) ORDER BY IsActive DESC,CompetencyName", new { ClientId = scope });
    }

    public async Task<IEnumerable<RecruitmentPipelineStageAction>> GetStageActionsAsync(long stageId, string triggerEvent, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentPipelineStageAction>(@"SELECT a.* FROM recruitment_pipeline_stage_actions a
JOIN recruitment_pipeline_stages s ON s.Id=a.PipelineStageId
JOIN recruitment_pipeline_versions v ON v.Id=s.PipelineVersionId
JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId
WHERE a.PipelineStageId=@StageId AND a.TriggerEvent=@TriggerEvent AND a.IsActive=TRUE
AND (@ClientId IS NULL OR d.ClientId=@ClientId) ORDER BY a.ExecutionOrder,a.Id",
            new { StageId = stageId, TriggerEvent = triggerEvent, user.ClientId });
    }

    public async Task<(RecruitmentInterviewCompetencyDefinition? Row, string Error)> SaveInterviewCompetencyAsync(RecruitmentInterviewCompetencyDefinition request, AuthUser user)
    {
        var clientId = user.ClientId ?? request.ClientId;
        var code = Regex.Replace((request.CompetencyCode ?? "").Trim().ToUpperInvariant(), "\\s+", "_");
        if (clientId <= 0 || !Regex.IsMatch(code, "^[A-Z0-9_-]{2,80}$") || string.IsNullOrWhiteSpace(request.CompetencyName)) return (null, "Client, competency code and name are required.");
        await using var db = Db();
        await db.OpenAsync();
        try
        {
            if (request.Id == 0)
                request.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_interview_competency_definitions
(ClientId,CompetencyCode,CompetencyName,Description,IsActive) VALUES (@ClientId,@Code,@Name,@Description,@IsActive);SELECT LAST_INSERT_ID();",
                    new { ClientId = clientId, Code = code, Name = request.CompetencyName.Trim(), Description = request.Description.Trim(), request.IsActive });
            else
            {
                var updated = await db.ExecuteAsync(@"UPDATE recruitment_interview_competency_definitions SET CompetencyCode=@Code,CompetencyName=@Name,
Description=@Description,IsActive=@IsActive WHERE Id=@Id AND ClientId=@ClientId", new { request.Id, ClientId = clientId, Code = code, Name = request.CompetencyName.Trim(), Description = request.Description.Trim(), request.IsActive });
                if (updated == 0) return (null, "Interview competency was not found.");
            }
        }
        catch (MySqlException ex) when (ex.Number == 1062) { return (null, "Competency code already exists for this client."); }
        return (await db.QueryFirstAsync<RecruitmentInterviewCompetencyDefinition>("SELECT * FROM recruitment_interview_competency_definitions WHERE Id=@Id", new { request.Id }), "");
    }

    public async Task<(long? PipelineInstanceId, string Error)> EnsureApplicationPipelineAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var application = await db.QueryFirstOrDefaultAsync<ApplicationSourceRow>("SELECT Id,PositionId,ClientId,JobPostingId FROM recruitment_candidate_applications WHERE Id=@Id", new { Id = applicationId });
        if (application is null || (user.ClientId is not null && user.ClientId != application.ClientId)) return (null, "Candidate application was not found.");
        var existing = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_application_pipeline_instances WHERE ApplicationId=@Id", new { Id = applicationId });
        if (existing is not null) return (existing, "");
        var assignment = await db.QueryFirstOrDefaultAsync<RecruitmentPositionPipelineAssignment>(@"SELECT * FROM recruitment_position_pipeline_assignments
WHERE PositionId=@PositionId AND IsActive=TRUE AND (JobPostingId=@JobPostingId OR JobPostingId IS NULL)
ORDER BY (JobPostingId=@JobPostingId) DESC,AssignedAtUtc DESC,Id DESC LIMIT 1", application);
        if (assignment is null) return (null, "No active pipeline is assigned to this open position.");
        var initial = await db.QueryFirstOrDefaultAsync<RecruitmentPipelineStage>(@"SELECT * FROM recruitment_pipeline_stages
WHERE PipelineVersionId=@PipelineVersionId AND CardScope='Application' AND IsActive=TRUE
ORDER BY IsInitial DESC,DisplayOrder,Id LIMIT 1", assignment);
        if (initial is null) return (null, "Assigned pipeline does not have an active application stage.");
        await using var tx = await db.BeginTransactionAsync();
        var pipelineId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_application_pipeline_instances
(ApplicationId,PipelineVersionId,PositionPipelineAssignmentId,Status) VALUES (@ApplicationId,@PipelineVersionId,@AssignmentId,'Active');SELECT LAST_INSERT_ID();",
            new { ApplicationId = applicationId, assignment.PipelineVersionId, AssignmentId = assignment.Id }, tx);
        var stageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_application_stage_instances
(ApplicationPipelineInstanceId,ApplicationId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc,EnteredByUserId)
VALUES (@PipelineId,@ApplicationId,@StageId,'Active',UTC_TIMESTAMP(),CASE WHEN @SlaMinutes>0 THEN TIMESTAMPADD(MINUTE,@SlaMinutes,UTC_TIMESTAMP()) ELSE NULL END,@UserId);SELECT LAST_INSERT_ID();",
            new { PipelineId = pipelineId, ApplicationId = applicationId, StageId = initial.Id, SlaMinutes = initial.SlaDurationMinutes, UserId = user.Id }, tx);
        await db.ExecuteAsync("UPDATE recruitment_application_pipeline_instances SET CurrentStageInstanceId=@StageId WHERE Id=@PipelineId", new { StageId = stageId, PipelineId = pipelineId }, tx);
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET PipelineInstanceId=@PipelineId,CurrentPipelineStageInstanceId=@StageId,
CurrentStage=@StageName,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@ApplicationId",
            new { PipelineId = pipelineId, StageId = stageId, StageName = initial.StageName, ApplicationId = applicationId }, tx);
        await AddStageEventAsync(db, tx, stageId, "Entered", $"Entered {initial.StageName}", "Pipeline started", user.Id);
        await tx.CommitAsync();
        return (pipelineId, "");
    }

    public async Task<RecruitmentPipelineBoard?> GetPipelineBoardAsync(long positionId, AuthUser user, long? jobPostingId = null)
    {
        await using var db = Db();
        await db.OpenAsync();
        var position = await db.QueryFirstOrDefaultAsync<PositionBoardRow>("SELECT Id PositionId,PositionCode,PositionTitle,ClientId FROM recruitment_open_positions WHERE Id=@Id", new { Id = positionId });
        if (position is null || (user.ClientId is not null && user.ClientId != position.ClientId)) return null;
        var assignment = await db.QueryFirstOrDefaultAsync<RecruitmentPositionPipelineAssignment>(@"SELECT * FROM recruitment_position_pipeline_assignments
WHERE PositionId=@Id AND IsActive=TRUE
AND ((@JobPostingId IS NULL AND JobPostingId IS NULL)
 OR (@JobPostingId IS NOT NULL AND (JobPostingId=@JobPostingId OR JobPostingId IS NULL)))
ORDER BY CASE WHEN JobPostingId=@JobPostingId THEN 0 ELSE 1 END,AssignedAtUtc DESC,Id DESC LIMIT 1", new { Id = positionId, JobPostingId = jobPostingId });
        if (assignment is null) return new RecruitmentPipelineBoard { ClientId = position.ClientId, PositionId = positionId, JobPostingId = jobPostingId, PositionCode = position.PositionCode, PositionTitle = position.PositionTitle };
        var board = new RecruitmentPipelineBoard { ClientId = position.ClientId, PositionId = positionId, JobPostingId = jobPostingId, PositionCode = position.PositionCode, PositionTitle = position.PositionTitle, PipelineVersionId = assignment.PipelineVersionId };
        board.Lanes = (await db.QueryAsync<RecruitmentPipelineBoardLane>(@"SELECT Id StageId,StageCode,StageName,StageType,CardScope,DisplayOrder,SlaDurationMinutes,SlaWarningMinutes
FROM recruitment_pipeline_stages WHERE PipelineVersionId=@PipelineVersionId AND CardScope='Application' AND IsActive=TRUE ORDER BY DisplayOrder,Id", assignment)).ToList();
        if (board.Lanes.Count > 0)
        {
            var processRequirements = (await db.QueryAsync<RecruitmentStageProcessDocumentRequirement>(@"SELECT * FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId IN @Ids ORDER BY DisplayOrder,Id", new { Ids = board.Lanes.Select(lane => lane.StageId).ToArray() })).ToLookup(requirement => requirement.PipelineStageId);
            foreach (var lane in board.Lanes) lane.ProcessDocumentRequirements = processRequirements[lane.StageId].ToList();
        }
        var cards = (await db.QueryAsync<BoardCardRow>(@"SELECT s.PipelineStageId StageId,a.Id ApplicationId,a.ApplicationCode,a.CandidateId,
CONCAT(c.FirstName,' ',c.LastName) CandidateName,c.Email CandidateEmail,
(SELECT COALESCE(sc.OverrideScore,sc.TotalScore) FROM recruitment_application_scores sc WHERE sc.ApplicationId=a.Id AND sc.IsCurrent=TRUE ORDER BY sc.ScoredAt DESC LIMIT 1) AtsScore,
s.EnteredAtUtc,
CASE WHEN s.DueAtUtc IS NULL THEN NULL ELSE TIMESTAMPADD(SECOND,COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0),s.DueAtUtc) END DueAtUtc,
GREATEST(0,TIMESTAMPDIFF(SECOND,s.EnteredAtUtc,UTC_TIMESTAMP())-s.PausedDurationSeconds-COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0)) ElapsedSeconds,
CASE WHEN s.DueAtUtc IS NULL THEN 0 ELSE GREATEST(0,TIMESTAMPDIFF(SECOND,UTC_TIMESTAMP(),TIMESTAMPADD(SECOND,COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0),s.DueAtUtc))) END RemainingSeconds,
s.PausedDurationSeconds+COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0) PausedDurationSeconds,
CASE WHEN ps.SlaWarningMinutes>0 AND s.DueAtUtc IS NOT NULL AND TIMESTAMPDIFF(SECOND,UTC_TIMESTAMP(),TIMESTAMPADD(SECOND,COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0),s.DueAtUtc)) BETWEEN 0 AND ps.SlaWarningMinutes*60 THEN TRUE ELSE FALSE END IsSlaWarning,
CASE WHEN s.DueAtUtc IS NOT NULL AND TIMESTAMPADD(SECOND,COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0),s.DueAtUtc)<UTC_TIMESTAMP() THEN TRUE ELSE FALSE END IsSlaBreached,s.Status StageStatus,
(SELECT COUNT(*) FROM recruitment_pipeline_stage_actions stageAction
 WHERE stageAction.PipelineStageId=s.PipelineStageId AND stageAction.IsActive=TRUE AND stageAction.IsBlocking=TRUE
 AND stageAction.TriggerEvent IN ('OnEntry','OnSubmission')
 AND NOT EXISTS (SELECT 1 FROM recruitment_stage_action_executions execution
  WHERE execution.StageInstanceId=s.Id AND execution.StageActionId=stageAction.Id
  AND execution.TriggerEvent=stageAction.TriggerEvent AND execution.Status='Completed')) PendingBlockingActionCount,
(SELECT COUNT(*) FROM recruitment_stage_action_executions execution
 WHERE execution.StageInstanceId=s.Id AND execution.Status='Failed') FailedActionCount
FROM recruitment_application_stage_instances s
JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId
JOIN recruitment_candidates c ON c.Id=a.CandidateId
JOIN recruitment_pipeline_stages ps ON ps.Id=s.PipelineStageId
JOIN recruitment_application_pipeline_instances pi ON pi.Id=s.ApplicationPipelineInstanceId AND pi.CurrentStageInstanceId=s.Id
WHERE a.PositionId=@PositionId AND pi.PipelineVersionId=@PipelineVersionId AND s.Status IN ('Active','Paused')
AND (@JobPostingId IS NULL OR a.JobPostingId=@JobPostingId)
ORDER BY s.EnteredAtUtc", new { PositionId = positionId, assignment.PipelineVersionId, JobPostingId = jobPostingId })).ToList();
        foreach (var lane in board.Lanes)
            lane.Applications = cards.Where(x => x.StageId == lane.StageId).Select(x => x.Card).ToList();
        return board;
    }

    public async Task<RecruitmentPipelineWorkspace?> GetPipelineWorkspaceAsync(int? clientId, long? positionId, long? jobPostingId, AuthUser user)
    {
        if (user.ClientId.HasValue && clientId.HasValue && user.ClientId.Value != clientId.Value) return null;
        var effectiveClientId = user.ClientId ?? (clientId is > 0 ? clientId : null);
        await using var db = Db();
        await db.OpenAsync();

        var demandCards = (await db.QueryAsync<WorkspaceDemandRow>(@"SELECT
workOrder.ClientId,line.Id WorkOrderLineId,line.WorkOrderId,workOrder.WorkOrderNumber,workOrder.Status WorkOrderStatus,
line.PositionName,line.PayBandLevelCode,line.Division,
hiringCase.Id HiringCaseId,hiringCase.PipelineVersionId,hiringCase.CurrentStageInstanceId,
COALESCE(hiringCase.Status,line.Status,'Not Started') Status,stage.Id CurrentStageId,stage.StageName CurrentStageName,
stageInstance.EnteredAtUtc,stageInstance.DueAtUtc,hiringCase.OverallDueAtUtc,
CASE WHEN hiringCase.Status IN ('Active','Candidate Flow') AND (stageInstance.DueAtUtc<UTC_TIMESTAMP(6) OR hiringCase.OverallDueAtUtc<UTC_TIMESTAMP(6)) THEN TRUE ELSE FALSE END IsSlaBreached,
requisition.Id RequisitionId,COALESCE(requisition.RfrNumber,'') RequisitionNumber,
COALESCE(requisition.Status,'Not Started') RequisitionStatus,
positionRow.Id PositionId,COALESCE(positionRow.PositionCode,'') PositionCode,
COALESCE(positionRow.Status,'Not Started') PositionStatus,
jobDescription.Id JobDescriptionId,COALESCE(jobDescription.Status,'Not Started') JobDescriptionStatus,
posting.Id JobPostingId,COALESCE(posting.Status,'Not Started') JobPostingStatus,
(SELECT assignment.PipelineVersionId FROM recruitment_position_pipeline_assignments assignment
 WHERE assignment.PositionId=positionRow.Id AND assignment.IsActive=TRUE
   AND (@JobPostingId IS NULL OR assignment.JobPostingId=@JobPostingId OR assignment.JobPostingId IS NULL)
 ORDER BY CASE WHEN assignment.JobPostingId=@JobPostingId THEN 0 ELSE 1 END,assignment.AssignedAtUtc DESC,assignment.Id DESC LIMIT 1) AssignedPipelineVersionId
FROM recruitment_work_order_lines line
JOIN recruitment_work_orders workOrder ON workOrder.Id=line.WorkOrderId
LEFT JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.WorkOrderLineId=line.Id
LEFT JOIN recruitment_position_stage_instances stageInstance ON stageInstance.Id=hiringCase.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages stage ON stage.Id=stageInstance.PipelineStageId
LEFT JOIN recruitment_requisitions requisition ON requisition.Id=COALESCE(line.RequisitionId,hiringCase.RequisitionId)
LEFT JOIN recruitment_open_positions positionRow ON positionRow.Id=COALESCE(line.PositionId,hiringCase.PositionId,requisition.OpenPositionId)
LEFT JOIN recruitment_job_description_versions jobDescription ON jobDescription.Id=(
 SELECT jd.Id FROM recruitment_job_description_versions jd WHERE jd.RequisitionId=requisition.Id ORDER BY jd.VersionNumber DESC,jd.Id DESC LIMIT 1)
LEFT JOIN recruitment_job_postings posting ON posting.Id=(
 SELECT jp.Id FROM recruitment_job_postings jp WHERE jp.PositionId=positionRow.Id
   AND (@JobPostingId IS NULL OR jp.Id=@JobPostingId) ORDER BY jp.Id DESC LIMIT 1)
WHERE (@ClientId IS NULL OR workOrder.ClientId=@ClientId)
  AND (@PositionId IS NULL OR positionRow.Id=@PositionId)
  AND (@JobPostingId IS NULL OR posting.Id=@JobPostingId)
ORDER BY workOrder.ReceivedAtUtc DESC,workOrder.Id DESC,line.LineNumber,line.Id",
            new { ClientId = effectiveClientId, PositionId = positionId is > 0 ? positionId : null, JobPostingId = jobPostingId is > 0 ? jobPostingId : null })).ToList();

        var assignedTargets = (await db.QueryAsync<WorkspaceAssignmentRow>(@"SELECT DISTINCT positionRow.Id PositionId,positionRow.ClientId,assignment.PipelineVersionId
FROM recruitment_open_positions positionRow
JOIN recruitment_position_pipeline_assignments assignment ON assignment.PositionId=positionRow.Id AND assignment.IsActive=TRUE
WHERE (@ClientId IS NULL OR positionRow.ClientId=@ClientId)
  AND (@PositionId IS NULL OR positionRow.Id=@PositionId)
  AND (@JobPostingId IS NULL OR assignment.JobPostingId=@JobPostingId OR assignment.JobPostingId IS NULL)",
            new { ClientId = effectiveClientId, PositionId = positionId is > 0 ? positionId : null, JobPostingId = jobPostingId is > 0 ? jobPostingId : null })).ToList();

        var applicationVersions = (await db.QueryAsync<long>(@"SELECT DISTINCT pipelineInstance.PipelineVersionId
FROM recruitment_application_pipeline_instances pipelineInstance
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=pipelineInstance.ApplicationId
WHERE (@ClientId IS NULL OR applicationRow.ClientId=@ClientId)
  AND (@PositionId IS NULL OR applicationRow.PositionId=@PositionId)
  AND (@JobPostingId IS NULL OR applicationRow.JobPostingId=@JobPostingId)",
            new { ClientId = effectiveClientId, PositionId = positionId is > 0 ? positionId : null, JobPostingId = jobPostingId is > 0 ? jobPostingId : null })).ToList();

        var publishedPositionVersions = (await db.QueryAsync<WorkspacePublishedPipelineRow>(@"SELECT definition.ClientId,versionRow.Id PipelineVersionId
FROM recruitment_pipeline_versions versionRow
JOIN recruitment_pipeline_definitions definition ON definition.Id=versionRow.PipelineDefinitionId
WHERE versionRow.Status='Published' AND versionRow.ScopeType IN ('Position','Hybrid')
  AND (@ClientId IS NULL OR definition.ClientId=@ClientId)", new { ClientId = effectiveClientId })).ToList();
        var uniquePublishedByClient = publishedPositionVersions.GroupBy(row => row.ClientId)
            .Where(group => group.Select(row => row.PipelineVersionId).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().PipelineVersionId);

        foreach (var card in demandCards.Where(card => !card.PipelineVersionId.HasValue))
        {
            if (card.AssignedPipelineVersionId.HasValue)
            {
                card.PipelineVersionId = card.AssignedPipelineVersionId;
                card.NeedsPipelineSelection = false;
            }
            else
            {
                card.PipelineVersionId = uniquePublishedByClient.TryGetValue(card.ClientId, out var onlyVersion) ? onlyVersion : null;
                card.NeedsPipelineSelection = true;
            }
        }

        var versionIds = demandCards.Where(card => card.PipelineVersionId.HasValue).Select(card => card.PipelineVersionId!.Value)
            .Concat(assignedTargets.Select(row => row.PipelineVersionId))
            .Concat(applicationVersions)
            .Distinct().ToArray();
        var workspace = new RecruitmentPipelineWorkspace { ClientId = effectiveClientId };
        if (versionIds.Length == 0)
        {
            workspace.UnassignedDemandCards = demandCards.Cast<RecruitmentPipelineDemandCard>().ToList();
            return workspace;
        }

        workspace.Lanes = (await db.QueryAsync<RecruitmentPipelineWorkspaceLane>(@"SELECT PipelineVersionId,Id StageId,StageCode,StageName,StageType,CardScope,
DisplayOrder,SlaDurationMinutes,SlaWarningMinutes
FROM recruitment_pipeline_stages
WHERE PipelineVersionId IN @VersionIds AND IsActive=TRUE
ORDER BY PipelineVersionId,DisplayOrder,Id", new { VersionIds = versionIds })).ToList();

        foreach (var card in demandCards)
        {
            if (!card.PipelineVersionId.HasValue)
            {
                workspace.UnassignedDemandCards.Add(card);
                continue;
            }
            if (!card.HiringCaseId.HasValue)
            {
                var initialLane = workspace.Lanes.FirstOrDefault(lane => lane.PipelineVersionId == card.PipelineVersionId
                    && lane.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase));
                if (initialLane is not null)
                {
                    card.CurrentStageId = initialLane.StageId;
                    card.CurrentStageName = initialLane.StageName;
                    card.Status = string.IsNullOrWhiteSpace(card.Status) ? "Not Started" : card.Status;
                }
            }
            var lane = workspace.Lanes.FirstOrDefault(row => row.PipelineVersionId == card.PipelineVersionId && row.StageId == card.CurrentStageId);
            if (lane is null) workspace.UnassignedDemandCards.Add(card);
            else lane.DemandCards.Add(card);
        }

        var candidateCards = (await db.QueryAsync<WorkspaceBoardCardRow>(@"SELECT pipelineInstance.PipelineVersionId,stageInstance.PipelineStageId StageId,
applicationRow.Id ApplicationId,applicationRow.ApplicationCode,applicationRow.CandidateId,
CONCAT(candidate.FirstName,' ',candidate.LastName) CandidateName,candidate.Email CandidateEmail,
(SELECT COALESCE(score.OverrideScore,score.TotalScore) FROM recruitment_application_scores score WHERE score.ApplicationId=applicationRow.Id AND score.IsCurrent=TRUE ORDER BY score.ScoredAt DESC LIMIT 1) AtsScore,
stageInstance.EnteredAtUtc,stageInstance.DueAtUtc,
GREATEST(0,TIMESTAMPDIFF(SECOND,stageInstance.EnteredAtUtc,UTC_TIMESTAMP())-stageInstance.PausedDurationSeconds) ElapsedSeconds,
CASE WHEN stageInstance.DueAtUtc IS NULL THEN 0 ELSE GREATEST(0,TIMESTAMPDIFF(SECOND,UTC_TIMESTAMP(),stageInstance.DueAtUtc)) END RemainingSeconds,
stageInstance.PausedDurationSeconds,
CASE WHEN stageDefinition.SlaWarningMinutes>0 AND stageInstance.DueAtUtc IS NOT NULL AND TIMESTAMPDIFF(SECOND,UTC_TIMESTAMP(),stageInstance.DueAtUtc) BETWEEN 0 AND stageDefinition.SlaWarningMinutes*60 THEN TRUE ELSE FALSE END IsSlaWarning,
CASE WHEN stageInstance.DueAtUtc IS NOT NULL AND stageInstance.DueAtUtc<UTC_TIMESTAMP() THEN TRUE ELSE FALSE END IsSlaBreached,
stageInstance.Status StageStatus,
(SELECT COUNT(*) FROM recruitment_pipeline_stage_actions stageAction
 WHERE stageAction.PipelineStageId=stageInstance.PipelineStageId AND stageAction.IsActive=TRUE AND stageAction.IsBlocking=TRUE
 AND stageAction.TriggerEvent IN ('OnEntry','OnSubmission')
 AND NOT EXISTS (SELECT 1 FROM recruitment_stage_action_executions execution
  WHERE execution.StageInstanceId=stageInstance.Id AND execution.StageActionId=stageAction.Id
  AND execution.TriggerEvent=stageAction.TriggerEvent AND execution.Status='Completed')) PendingBlockingActionCount,
(SELECT COUNT(*) FROM recruitment_stage_action_executions execution
 WHERE execution.StageInstanceId=stageInstance.Id AND execution.Status='Failed') FailedActionCount
FROM recruitment_application_stage_instances stageInstance
JOIN recruitment_application_pipeline_instances pipelineInstance ON pipelineInstance.Id=stageInstance.ApplicationPipelineInstanceId AND pipelineInstance.CurrentStageInstanceId=stageInstance.Id
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=stageInstance.ApplicationId
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
JOIN recruitment_pipeline_stages stageDefinition ON stageDefinition.Id=stageInstance.PipelineStageId AND stageDefinition.CardScope='Application'
WHERE stageInstance.Status IN ('Active','Paused')
  AND (@ClientId IS NULL OR applicationRow.ClientId=@ClientId)
  AND (@PositionId IS NULL OR applicationRow.PositionId=@PositionId)
  AND (@JobPostingId IS NULL OR applicationRow.JobPostingId=@JobPostingId)
ORDER BY stageInstance.EnteredAtUtc",
            new { ClientId = effectiveClientId, PositionId = positionId is > 0 ? positionId : null, JobPostingId = jobPostingId is > 0 ? jobPostingId : null })).ToList();
        foreach (var candidate in candidateCards)
        {
            var lane = workspace.Lanes.FirstOrDefault(row => row.PipelineVersionId == candidate.PipelineVersionId && row.StageId == candidate.StageId);
            if (lane is not null) lane.Applications.Add(candidate);
        }
        return workspace;
    }

    public async Task<(RecruitmentPipelineTransitionResult? Result, string Error)> RequestTransitionAsync(long applicationId, RecruitmentPipelineTransitionRequest request, AuthUser user)
    {
        var (_, ensureError) = await EnsureApplicationPipelineAsync(applicationId, user);
        if (ensureError.Length > 0) return (null, ensureError);
        await using var db = Db();
        await db.OpenAsync();
        var context = await db.QueryFirstOrDefaultAsync<TransitionContextRow>(@"SELECT a.ClientId,pi.Id PipelineInstanceId,pi.CurrentStageInstanceId,
s.PipelineStageId CurrentStageId,t.Id TransitionId,t.ToStageId,t.RequiresReason,
COALESCE(t.ApprovalWorkflowId,CASE WHEN fs.RequiresApproval THEN fs.ApprovalWorkflowId ELSE NULL END) ApprovalWorkflowId
FROM recruitment_candidate_applications a
JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
JOIN recruitment_application_stage_instances s ON s.Id=pi.CurrentStageInstanceId AND s.Status IN ('Active','Paused')
JOIN recruitment_pipeline_transitions t ON t.Id=@TransitionId AND t.FromStageId=s.PipelineStageId AND t.PipelineVersionId=pi.PipelineVersionId AND t.IsActive=TRUE
JOIN recruitment_pipeline_stages fs ON fs.Id=t.FromStageId AND fs.CardScope='Application' AND fs.IsActive=TRUE
JOIN recruitment_pipeline_stages ts ON ts.Id=t.ToStageId AND ts.CardScope='Application' AND ts.IsActive=TRUE
WHERE a.Id=@ApplicationId", new { ApplicationId = applicationId, request.TransitionId });
        if (context is null || (user.ClientId is not null && user.ClientId != context.ClientId)) return (null, "Transition is not available from the application's current stage.");
        if (context.RequiresReason && string.IsNullOrWhiteSpace(request.Reason)) return (null, "A transition reason is required.");
        var stageRequirementError = await ValidateStageExitRequirementsAsync(db, applicationId, context.CurrentStageId, request.TransitionId);
        if (stageRequirementError.Length > 0) return (null, stageRequirementError);
        var ruleError = await ValidateTransitionRulesAsync(db, applicationId, request.TransitionId);
        if (ruleError.Length > 0) return (null, ruleError);
        var duplicate = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_pipeline_transition_requests
WHERE ApplicationId=@ApplicationId AND StageInstanceId=@StageId AND TransitionId=@TransitionId AND Status IN ('Requested','Pending Approval') LIMIT 1",
            new { ApplicationId = applicationId, StageId = context.CurrentStageInstanceId, TransitionId = request.TransitionId });
        if (duplicate is not null) return (null, "This transition is already pending.");
        var pendingApproval = context.ApprovalWorkflowId is > 0;
        var requestId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_transition_requests
(ApplicationId,StageInstanceId,TransitionId,Reason,Status,RequestedByUserId)
VALUES (@ApplicationId,@StageInstanceId,@TransitionId,@Reason,@Status,@UserId);SELECT LAST_INSERT_ID();",
            new { ApplicationId = applicationId, StageInstanceId = context.CurrentStageInstanceId, TransitionId = request.TransitionId, Reason = request.Reason.Trim(), Status = pendingApproval ? "Pending Approval" : "Approved", UserId = user.Id });
        if (pendingApproval)
        {
            var workflowRequestorId = await ResolveWorkflowRequestorAsync(db, applicationId, user.Id);
            if (workflowRequestorId <= 0)
            {
                await db.ExecuteAsync("UPDATE recruitment_pipeline_transition_requests SET Status='Workflow Failed',DecidedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = requestId });
                return (null, "A valid hiring requester or recruiter is required before starting transition approval.");
            }
            var workflow = await workflows.StartAsync(new StartWorkflowRequest { WorkflowId = checked((int)context.ApprovalWorkflowId!.Value), ResourceType = "RecruitmentPipelineTransition", ResourceId = requestId.ToString(), PayloadJson = "{}" }, workflowRequestorId);
            if (workflow is null)
            {
                await db.ExecuteAsync("UPDATE recruitment_pipeline_transition_requests SET Status='Workflow Failed',DecidedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = requestId });
                return (null, "Transition approval workflow could not start. Check stages and approvers.");
            }
            await db.ExecuteAsync("UPDATE recruitment_pipeline_transition_requests SET WorkflowInstanceId=@WorkflowId WHERE Id=@Id", new { Id = requestId, WorkflowId = workflow.Id });
            return (new RecruitmentPipelineTransitionResult { RequestId = requestId, ApplicationId = applicationId, Status = "Pending Approval", WorkflowInstanceId = workflow.Id, CurrentStageInstanceId = context.CurrentStageInstanceId, Message = "Transition sent for approval." }, "");
        }
        var applied = await ApplyTransitionAsync(requestId, user.Id);
        return applied.Result is null ? (null, applied.Error) : (applied.Result, "");
    }

    public async Task<IEnumerable<RecruitmentPipelineTransition>> GetAvailableTransitionsAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var rows = (await db.QueryAsync<RecruitmentPipelineTransition>(@"SELECT t.*,f.StageCode FromStageCode,n.StageCode ToStageCode
FROM recruitment_candidate_applications a
JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId AND si.Status IN ('Active','Paused')
JOIN recruitment_pipeline_transitions t ON t.FromStageId=si.PipelineStageId AND t.PipelineVersionId=pi.PipelineVersionId AND t.IsActive=TRUE
JOIN recruitment_pipeline_stages f ON f.Id=t.FromStageId AND f.CardScope='Application' AND f.IsActive=TRUE
JOIN recruitment_pipeline_stages n ON n.Id=t.ToStageId AND n.CardScope='Application' AND n.IsActive=TRUE
WHERE a.Id=@ApplicationId AND (@ClientId IS NULL OR a.ClientId=@ClientId) ORDER BY t.DisplayOrder,t.Id",
            new { ApplicationId = applicationId, user.ClientId })).ToList();
        foreach (var row in rows)
            row.Rules = (await db.QueryAsync<RecruitmentPipelineTransitionRule>("SELECT * FROM recruitment_pipeline_transition_rules WHERE TransitionId=@Id ORDER BY DisplayOrder,Id", new { row.Id })).ToList();
        return rows;
    }

    public async Task<(RecruitmentPipelineTransitionResult? Result, string Error)> EvaluateAtsStageAutomationAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<AtsAutomationRow>(@"SELECT a.ClientId,si.PipelineStageId,c.MinimumAdvanceScore,c.MaximumRejectScore,c.AutoAdvance,c.AutoReject,
c.RequireHumanConfirmation,c.AdvanceOutcomeCode,c.RejectOutcomeCode,
(SELECT COALESCE(s.OverrideScore,s.TotalScore) FROM recruitment_application_scores s WHERE s.ApplicationId=a.Id AND s.IsCurrent=TRUE ORDER BY s.ScoredAt DESC,s.Id DESC LIMIT 1) CurrentScore,
(SELECT COALESCE(s.ScoreStatus,'') FROM recruitment_application_scores s WHERE s.ApplicationId=a.Id AND s.IsCurrent=TRUE ORDER BY s.ScoredAt DESC,s.Id DESC LIMIT 1) CurrentScoreStatus,
(SELECT COALESCE(s.HumanReviewRequired,TRUE) FROM recruitment_application_scores s WHERE s.ApplicationId=a.Id AND s.IsCurrent=TRUE ORDER BY s.ScoredAt DESC,s.Id DESC LIMIT 1) CurrentScoreRequiresReview
FROM recruitment_candidate_applications a JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId AND si.Status IN ('Active','Paused')
JOIN recruitment_stage_ats_configurations c ON c.PipelineStageId=si.PipelineStageId WHERE a.Id=@Id", new { Id = applicationId });
        if (row is null || (user.ClientId is not null && user.ClientId != row.ClientId)) return (null, "The application's current stage has no ATS automation configuration.");
        if (row.CurrentScore is null) return (null, "ATS score is not available yet.");
        var scoreStatus = row.CurrentScoreStatus.Trim();
        if (scoreStatus.Equals(RecruitmentAtsDomainRules.NeedsReview, StringComparison.OrdinalIgnoreCase)
            || row.RequireHumanConfirmation
            || row.CurrentScoreRequiresReview)
            return (new RecruitmentPipelineTransitionResult
            {
                ApplicationId = applicationId,
                Status = "Manual Review",
                Message = scoreStatus.Equals(RecruitmentAtsDomainRules.NeedsReview, StringComparison.OrdinalIgnoreCase)
                    ? "ATS must-have evidence is incomplete. A recruiter must verify the candidate before any pipeline movement."
                    : $"ATS score {row.CurrentScore:0.##} is ready for human review."
            }, "");

        var ineligible = scoreStatus.Equals(RecruitmentAtsDomainRules.Ineligible, StringComparison.OrdinalIgnoreCase);
        var outcome = ineligible && row.AutoReject ? row.RejectOutcomeCode
            : ineligible ? ""
            : row.AutoReject && row.CurrentScore <= row.MaximumRejectScore ? row.RejectOutcomeCode
            : row.AutoAdvance && row.CurrentScore >= row.MinimumAdvanceScore ? row.AdvanceOutcomeCode : "";
        if (outcome.Length == 0) return (new RecruitmentPipelineTransitionResult
        {
            ApplicationId = applicationId,
            Status = ineligible ? "Manual Review" : "No Action",
            Message = ineligible
                ? "Candidate does not meet a must-have requirement. Configure auto-reject or review the evidence manually; auto-advance is blocked."
                : $"ATS score {row.CurrentScore:0.##} is inside the manual-review band."
        }, "");
        var transitionId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_pipeline_transitions
WHERE FromStageId=@StageId AND OutcomeCode=@Outcome AND IsActive=TRUE ORDER BY DisplayOrder,Id LIMIT 1", new { StageId = row.PipelineStageId, Outcome = outcome });
        if (transitionId is null) return (null, $"No active {outcome} transition is configured for this ATS stage.");
        return await RequestTransitionAsync(applicationId, new RecruitmentPipelineTransitionRequest { TransitionId = transitionId.Value, Reason = $"ATS automation: score {row.CurrentScore:0.##}." }, user);
    }

    public async Task<(RecruitmentPipelineTransitionResult? Result, string Error)> SyncTransitionWorkflowStatusAsync(long transitionRequestId, string workflowStatus, AuthUser user)
    {
        var normalized = NormalizeWorkflowDecision(workflowStatus);
        if (normalized.Length == 0) return (null, "Unsupported transition workflow status.");
        await using var db = Db();
        await db.OpenAsync();
        var request = await db.QueryFirstOrDefaultAsync<TransitionRequestRow>(@"SELECT r.*,a.ClientId FROM recruitment_pipeline_transition_requests r
JOIN recruitment_candidate_applications a ON a.Id=r.ApplicationId WHERE r.Id=@Id", new { Id = transitionRequestId });
        if (request is null || (user.ClientId is not null && user.ClientId != request.ClientId)) return (null, "Transition request was not found.");
        if (!request.Status.Equals("Pending Approval", StringComparison.OrdinalIgnoreCase)) return (null, "Transition request is no longer pending approval.");
        if (normalized == "Approved") return await ApplyTransitionAsync(transitionRequestId, user.Id);
        await db.ExecuteAsync("UPDATE recruitment_pipeline_transition_requests SET Status=@Status,DecidedByUserId=@UserId,DecidedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = transitionRequestId, Status = normalized, UserId = user.Id });
        return (new RecruitmentPipelineTransitionResult { RequestId = transitionRequestId, ApplicationId = request.ApplicationId, Status = normalized, CurrentStageInstanceId = request.StageInstanceId, Message = $"Transition {normalized.ToLowerInvariant()}." }, "");
    }

    public async Task<(RecruitmentApplicationStageInstance? Row, string Error)> PauseStageAsync(long applicationId, RecruitmentStagePauseRequest request, AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return (null, "Pause reason is required.");
        await using var db = Db();
        await db.OpenAsync();
        var stage = await CurrentStageAsync(db, applicationId, user.ClientId);
        if (stage is null) return (null, "Active application stage was not found.");
        if (stage.Status.Equals("Paused", StringComparison.OrdinalIgnoreCase)) return (null, "Application stage is already paused.");
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("INSERT INTO recruitment_stage_pause_periods (StageInstanceId,Reason,PausedByUserId) VALUES (@Id,@Reason,@UserId)", new { stage.Id, Reason = request.Reason.Trim(), UserId = user.Id }, tx);
        await db.ExecuteAsync("UPDATE recruitment_application_stage_instances SET Status='Paused' WHERE Id=@Id", stage, tx);
        await AddStageEventAsync(db, tx, stage.Id, "Paused", "Stage timer paused", request.Reason.Trim(), user.Id);
        await tx.CommitAsync();
        return (await CurrentStageAsync(db, applicationId, user.ClientId), "");
    }

    public async Task<(RecruitmentApplicationStageInstance? Row, string Error)> ResumeStageAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var stage = await CurrentStageAsync(db, applicationId, user.ClientId);
        if (stage is null) return (null, "Active application stage was not found.");
        if (!stage.Status.Equals("Paused", StringComparison.OrdinalIgnoreCase)) return (null, "Application stage is not paused.");
        await using var tx = await db.BeginTransactionAsync();
        var pause = await db.QueryFirstOrDefaultAsync<PauseRow>("SELECT Id,TIMESTAMPDIFF(SECOND,PausedAtUtc,UTC_TIMESTAMP()) DurationSeconds FROM recruitment_stage_pause_periods WHERE StageInstanceId=@Id AND ResumedAtUtc IS NULL ORDER BY Id DESC LIMIT 1 FOR UPDATE", new { stage.Id }, tx);
        if (pause is null) return (null, "Open pause period was not found.");
        await db.ExecuteAsync("UPDATE recruitment_stage_pause_periods SET ResumedByUserId=@UserId,ResumedAtUtc=UTC_TIMESTAMP(),DurationSeconds=@Seconds WHERE Id=@Id", new { Id = pause.Id, UserId = user.Id, Seconds = pause.DurationSeconds }, tx);
        await db.ExecuteAsync(@"UPDATE recruitment_application_stage_instances SET Status='Active',PausedDurationSeconds=PausedDurationSeconds+@Seconds,
DueAtUtc=CASE WHEN DueAtUtc IS NULL THEN NULL ELSE TIMESTAMPADD(SECOND,@Seconds,DueAtUtc) END WHERE Id=@Id", new { Id = stage.Id, Seconds = pause.DurationSeconds }, tx);
        await AddStageEventAsync(db, tx, stage.Id, "Resumed", "Stage timer resumed", $"Paused for {pause.DurationSeconds} seconds", user.Id);
        await tx.CommitAsync();
        return (await CurrentStageAsync(db, applicationId, user.ClientId), "");
    }

    private async Task<(RecruitmentPipelineTransitionResult? Result, string Error)> ApplyTransitionAsync(long requestId, int actorUserId)
    {
        await using var db = Db();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var request = await db.QueryFirstOrDefaultAsync<ApplyTransitionRow>(@"SELECT r.*,t.FromStageId,t.ToStageId,t.OutcomeCode,
f.StageName FromStageName,n.StageName ToStageName,n.SlaDurationMinutes,n.IsTerminal,n.StageType ToStageType
FROM recruitment_pipeline_transition_requests r
JOIN recruitment_pipeline_transitions t ON t.Id=r.TransitionId AND t.IsActive=TRUE
JOIN recruitment_pipeline_stages f ON f.Id=t.FromStageId AND f.CardScope='Application' AND f.IsActive=TRUE
JOIN recruitment_pipeline_stages n ON n.Id=t.ToStageId AND n.CardScope='Application' AND n.IsActive=TRUE
WHERE r.Id=@Id FOR UPDATE", new { Id = requestId }, tx);
        if (request is null) return (null, "Transition request was not found.");
        if (request.AppliedAtUtc is not null)
            return (new RecruitmentPipelineTransitionResult { RequestId = request.Id, ApplicationId = request.ApplicationId, Status = "Applied", CurrentStageInstanceId = request.StageInstanceId, Message = "Transition was already applied." }, "");
        if (request.Status is not ("Approved" or "Pending Approval")) return (null, "Transition request is not approved.");
        var current = await db.QueryFirstOrDefaultAsync<StageLockRow>(@"SELECT s.*,p.CurrentStageInstanceId,p.Id PipelineInstanceId
FROM recruitment_application_stage_instances s JOIN recruitment_application_pipeline_instances p ON p.Id=s.ApplicationPipelineInstanceId
WHERE s.Id=@Id AND p.CurrentStageInstanceId=s.Id AND s.Status IN ('Active','Paused') FOR UPDATE", new { Id = request.StageInstanceId }, tx);
        if (current is null || current.PipelineStageId != request.FromStageId) return (null, "Application has already left the requested stage.");

        if (current.Status.Equals("Paused", StringComparison.OrdinalIgnoreCase))
        {
            var pause = await db.QueryFirstOrDefaultAsync<PauseRow>("SELECT Id,TIMESTAMPDIFF(SECOND,PausedAtUtc,UTC_TIMESTAMP()) DurationSeconds FROM recruitment_stage_pause_periods WHERE StageInstanceId=@Id AND ResumedAtUtc IS NULL ORDER BY Id DESC LIMIT 1 FOR UPDATE", new { current.Id }, tx);
            if (pause is not null)
            {
                await db.ExecuteAsync("UPDATE recruitment_stage_pause_periods SET ResumedByUserId=@UserId,ResumedAtUtc=UTC_TIMESTAMP(),DurationSeconds=@Seconds WHERE Id=@Id", new { Id = pause.Id, UserId = actorUserId, Seconds = pause.DurationSeconds }, tx);
                current.PausedDurationSeconds += pause.DurationSeconds;
            }
        }

        var activeSeconds = await db.ExecuteScalarAsync<long>("SELECT GREATEST(0,TIMESTAMPDIFF(SECOND,EnteredAtUtc,UTC_TIMESTAMP())-@Paused) FROM recruitment_application_stage_instances WHERE Id=@Id", new { Id = current.Id, Paused = current.PausedDurationSeconds }, tx);
        await db.ExecuteAsync(@"UPDATE recruitment_application_stage_instances SET Status='Completed',OutcomeCode=@OutcomeCode,ExitedAtUtc=UTC_TIMESTAMP(),
ActiveDurationSeconds=@ActiveSeconds,PausedDurationSeconds=@PausedSeconds,ExitedByUserId=@UserId WHERE Id=@Id",
            new { Id = current.Id, request.OutcomeCode, ActiveSeconds = activeSeconds, PausedSeconds = current.PausedDurationSeconds, UserId = actorUserId }, tx);
        await AddStageEventAsync(db, tx, current.Id, "Exited", $"Exited {request.FromStageName}", request.Reason, actorUserId);

        var nextStageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_application_stage_instances
(ApplicationPipelineInstanceId,ApplicationId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc,EnteredByUserId)
VALUES (@PipelineInstanceId,@ApplicationId,@ToStageId,'Active',UTC_TIMESTAMP(),CASE WHEN @SlaMinutes>0 THEN TIMESTAMPADD(MINUTE,@SlaMinutes,UTC_TIMESTAMP()) ELSE NULL END,@UserId);SELECT LAST_INSERT_ID();",
            new { current.PipelineInstanceId, request.ApplicationId, request.ToStageId, SlaMinutes = request.SlaDurationMinutes, UserId = actorUserId }, tx);
        await AddStageEventAsync(db, tx, nextStageId, "Entered", $"Entered {request.ToStageName}", request.Reason, actorUserId);
        await db.ExecuteAsync(@"UPDATE recruitment_application_pipeline_instances SET CurrentStageInstanceId=@StageId,
Status=CASE WHEN @Terminal THEN 'Completed' ELSE 'Active' END,CompletedAtUtc=CASE WHEN @Terminal THEN UTC_TIMESTAMP() ELSE NULL END WHERE Id=@Id",
            new { StageId = nextStageId, Terminal = request.IsTerminal, Id = current.PipelineInstanceId }, tx);
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET CurrentPipelineStageInstanceId=@StageId,CurrentStage=@StageName,
CurrentStatus=CASE WHEN @Terminal THEN @StageName ELSE CurrentStatus END,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@ApplicationId",
            new { StageId = nextStageId, StageName = request.ToStageName, Terminal = request.IsTerminal, request.ApplicationId }, tx);
        await db.ExecuteAsync(@"INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId,ChangedAt)
VALUES (@ApplicationId,@FromStage,@ToStage,@Reason,@UserId,UTC_TIMESTAMP())",
            new { request.ApplicationId, FromStage = request.FromStageName, ToStage = request.ToStageName, request.Reason, UserId = actorUserId }, tx);
        await db.ExecuteAsync(@"UPDATE recruitment_pipeline_transition_requests SET Status='Applied',DecidedByUserId=@UserId,
DecidedAtUtc=COALESCE(DecidedAtUtc,UTC_TIMESTAMP()),AppliedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id",
            new { Id = requestId, UserId = actorUserId }, tx);
        await tx.CommitAsync();
        return (new RecruitmentPipelineTransitionResult { RequestId = requestId, ApplicationId = request.ApplicationId, Status = "Applied", CurrentStageInstanceId = nextStageId, Message = $"Moved to {request.ToStageName}." }, "");
    }

    private static async Task<string> ValidateTransitionRulesAsync(MySqlConnection db, long applicationId, long transitionId)
    {
        var rules = await db.QueryAsync<RecruitmentPipelineTransitionRule>("SELECT * FROM recruitment_pipeline_transition_rules WHERE TransitionId=@Id ORDER BY DisplayOrder,Id", new { Id = transitionId });
        foreach (var rule in rules)
        {
            bool passed;
            switch (rule.RuleType.ToUpperInvariant())
            {
                case "ATS_SCORE":
                    var ats = await db.ExecuteScalarAsync<decimal?>(@"SELECT COALESCE(OverrideScore,TotalScore) FROM recruitment_application_scores
WHERE ApplicationId=@Id AND IsCurrent=TRUE ORDER BY ScoredAt DESC,Id DESC LIMIT 1", new { Id = applicationId });
                    passed = ats is not null && Compare(ats.Value, rule.DecimalValue ?? (decimal)(rule.IntegerValue ?? 0), rule.ComparisonOperator);
                    break;
                case "MANDATORY_DOCUMENTS_COMPLETE":
                    var incomplete = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_checklist_items WHERE ApplicationId=@Id AND Mandatory=TRUE AND Status<>'Completed'", new { Id = applicationId });
                    passed = Compare(incomplete == 0, rule.BooleanValue ?? true, rule.ComparisonOperator);
                    break;
                case "RESUME_REQUIRED":
                    var hasResume = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications WHERE Id=@Id AND ResumeId IS NOT NULL", new { Id = applicationId });
                    passed = Compare(hasResume > 0, rule.BooleanValue ?? true, rule.ComparisonOperator);
                    break;
                case "OFFER_ACCEPTED":
                    var acceptedOffer = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_offers WHERE ApplicationId=@Id AND Status='Accepted'", new { Id = applicationId });
                    passed = Compare(acceptedOffer > 0, rule.BooleanValue ?? true, rule.ComparisonOperator);
                    break;
                case "INTERVIEW_RESULT":
                    var result = await db.ExecuteScalarAsync<string?>(@"SELECT Result FROM recruitment_interviews WHERE ApplicationId=@Id
AND Status='Completed' ORDER BY ScheduledStart DESC,Id DESC LIMIT 1", new { Id = applicationId });
                    passed = result is not null && Compare(result, rule.TextValue ?? "Passed", rule.ComparisonOperator);
                    break;
                case "INTERVIEW_SCORE":
                    var score = await db.ExecuteScalarAsync<decimal?>(@"SELECT OverallScore FROM recruitment_interviews WHERE ApplicationId=@Id
AND Status='Completed' ORDER BY ScheduledStart DESC,Id DESC LIMIT 1", new { Id = applicationId });
                    passed = score is not null && Compare(score.Value, rule.DecimalValue ?? (decimal)(rule.IntegerValue ?? 0), rule.ComparisonOperator);
                    break;
                case "PANEL_FEEDBACK_COMPLETE":
                    var missingFeedback = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_interview_panel_members pm
JOIN recruitment_interviews i ON i.Id=pm.InterviewId
LEFT JOIN recruitment_interview_feedback f ON f.InterviewId=pm.InterviewId AND f.PanelUserId=pm.PanelUserId
WHERE i.ApplicationId=@Id AND i.Status<>'Cancelled' AND f.Id IS NULL", new { Id = applicationId });
                    passed = Compare(missingFeedback == 0, rule.BooleanValue ?? true, rule.ComparisonOperator);
                    break;
                default:
                    passed = false;
                    break;
            }
            if (!passed && rule.IsMandatory) return string.IsNullOrWhiteSpace(rule.ErrorMessage) ? $"Transition rule {rule.RuleType} is not satisfied." : rule.ErrorMessage;
        }
        return "";
    }

    private static async Task<string> ValidateStageExitRequirementsAsync(MySqlConnection db, long applicationId, long stageId, long transitionId)
    {
        var candidateId = await db.ExecuteScalarAsync<long?>("SELECT CandidateId FROM recruitment_candidate_applications WHERE Id=@Id", new { Id = applicationId });
        if (candidateId is null) return "Candidate application was not found.";
        var stageInstanceId = await db.ExecuteScalarAsync<long?>(@"SELECT CurrentPipelineStageInstanceId
FROM recruitment_candidate_applications WHERE Id=@Id", new { Id = applicationId });
        if (stageInstanceId.HasValue)
        {
            var pendingBlockingActions = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_pipeline_stage_actions action
WHERE action.PipelineStageId=@StageId AND action.IsActive=TRUE AND action.IsBlocking=TRUE
AND action.TriggerEvent IN ('OnEntry','OnSubmission')
AND NOT EXISTS (SELECT 1 FROM recruitment_stage_action_executions execution
    WHERE execution.StageInstanceId=@StageInstanceId AND execution.StageActionId=action.Id
      AND execution.TriggerEvent=action.TriggerEvent AND execution.Status='Completed')",
                new { StageId = stageId, StageInstanceId = stageInstanceId.Value });
            if (pendingBlockingActions > 0) return "Complete all blocking stage actions and approvals before moving this candidate.";
        }
        var requirements = await db.QueryAsync<RecruitmentStageAttachmentRequirement>("SELECT * FROM recruitment_stage_attachment_requirements WHERE PipelineStageId=@Id AND IsRequired=TRUE ORDER BY DisplayOrder,Id", new { Id = stageId });
        foreach (var requirement in requirements)
        {
            var count = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM entity_attachments WHERE entity_type='CANDIDATE' AND entity_id=@CandidateId
AND field_configuration_id=@ConfigurationId AND is_current=TRUE AND is_deleted=FALSE
AND (@Verify=FALSE OR verification_status='Verified')", new { CandidateId = candidateId.Value, ConfigurationId = requirement.AttachmentFieldConfigurationId, Verify = requirement.RequiresVerification });
            if (count < requirement.MinimumFileCount) return requirement.RequiresVerification ? "A required candidate document is missing or not verified." : "A required candidate document is missing.";
            if (count > requirement.MaximumFileCount) return "The candidate has more files than this stage permits for a required document.";
        }
        var missingProcessDocuments = (await db.QueryAsync<string>(@"SELECT requirement.DocumentType
FROM recruitment_stage_process_document_requirements requirement
WHERE requirement.PipelineStageId=@StageId AND requirement.IsRequired=TRUE
AND NOT EXISTS (SELECT 1 FROM recruitment_process_documents document
  WHERE document.ApplicationId=@ApplicationId AND document.PipelineStageId=@StageId
    AND document.DocumentType=requirement.DocumentType
    AND (requirement.RequiresSignature=FALSE OR (document.Status='Signed' AND EXISTS (
      SELECT 1 FROM entity_attachments attachment WHERE attachment.entity_type='RECRUITMENT_PROCESS_DOCUMENT'
        AND attachment.entity_id=document.Id AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE))))
ORDER BY requirement.DisplayOrder,requirement.Id", new { StageId = stageId, ApplicationId = applicationId })).ToArray();
        if (missingProcessDocuments.Length > 0) return $"Complete required process documents before moving this candidate: {string.Join(", ", missingProcessDocuments)}.";
        var form = await db.QueryFirstOrDefaultAsync<RecruitmentStageExternalFormConfiguration>("SELECT * FROM recruitment_stage_external_form_configurations WHERE PipelineStageId=@Id AND SubmissionRequired=TRUE", new { Id = stageId });
        if (form is not null)
        {
            var tableExists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='form_submissions'");
            if (tableExists == 0) return "Required external-form submission storage is not initialized.";
            var submitted = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_submissions
WHERE ApplicationId=@ApplicationId AND FormVersionId=@FormVersionId AND Status='Submitted'", new { ApplicationId = applicationId, form.FormVersionId });
            if (submitted == 0) return "The required external form has not been submitted.";
        }
        var offer = await db.QueryFirstOrDefaultAsync<RecruitmentStageOfferConfiguration>("SELECT * FROM recruitment_stage_offer_configurations WHERE PipelineStageId=@Id", new { Id = stageId });
        if (offer?.RequireAcceptedOfferToAdvance == true)
        {
            var accepted = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_offers WHERE ApplicationId=@Id AND Status='Accepted'", new { Id = applicationId });
            if (accepted == 0) return "An accepted offer is required before leaving this stage.";
        }
        var ats = await db.QueryFirstOrDefaultAsync<RecruitmentStageAtsConfiguration>("SELECT * FROM recruitment_stage_ats_configurations WHERE PipelineStageId=@Id", new { Id = stageId });
        if (ats is not null)
        {
            var outcome = await db.ExecuteScalarAsync<string>("SELECT OutcomeCode FROM recruitment_pipeline_transitions WHERE Id=@Id", new { Id = transitionId }) ?? "";
            var score = await db.ExecuteScalarAsync<decimal?>("SELECT COALESCE(OverrideScore,TotalScore) FROM recruitment_application_scores WHERE ApplicationId=@Id AND IsCurrent=TRUE ORDER BY ScoredAt DESC,Id DESC LIMIT 1", new { Id = applicationId });
            if (outcome.Equals(ats.AdvanceOutcomeCode, StringComparison.OrdinalIgnoreCase) && (score is null || score < ats.MinimumAdvanceScore)) return $"ATS score must be at least {ats.MinimumAdvanceScore:0.##} for this transition.";
            if (outcome.Equals(ats.RejectOutcomeCode, StringComparison.OrdinalIgnoreCase) && (score is null || score > ats.MaximumRejectScore)) return $"ATS score must be at most {ats.MaximumRejectScore:0.##} for this rejection transition.";
        }
        return "";
    }

    private static bool Compare(decimal actual, decimal expected, string operation) => operation.ToUpperInvariant() switch
    {
        "EQ" => actual == expected,
        "NE" => actual != expected,
        "GT" => actual > expected,
        "GTE" => actual >= expected,
        "LT" => actual < expected,
        "LTE" => actual <= expected,
        _ => false
    };

    private static bool Compare(bool actual, bool expected, string operation) => operation.ToUpperInvariant() switch
    {
        "EQ" => actual == expected,
        "NE" => actual != expected,
        _ => false
    };

    private static bool Compare(string actual, string expected, string operation) => operation.ToUpperInvariant() switch
    {
        "EQ" => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
        "NE" => !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
        "CONTAINS" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
        "NOT_CONTAINS" => !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static async Task<string> ValidateWorkflowBindingsAsync(
        MySqlConnection db,
        IEnumerable<RecruitmentPipelineStage> stages,
        IEnumerable<RecruitmentPipelineTransition> transitions,
        int clientId)
    {
        var stageRows = stages.ToList();
        var transitionRows = transitions.ToList();
        var workflowIds = stageRows.SelectMany(stage => stage.Actions.Select(action => action.WorkflowId)
                .Append(stage.ApprovalWorkflowId)
                .Append(stage.OfferConfiguration?.ApprovalWorkflowId)
                .Append(stage.OfferConfiguration?.VarianceApprovalWorkflowId))
            .Concat(transitionRows.Select(transition => transition.ApprovalWorkflowId))
            .Where(id => id is > 0).Select(id => id!.Value).Distinct().ToArray();
        if (workflowIds.Length == 0) return "";

        var workflowRows = (await db.QueryAsync<WorkflowBindingRow>(@"SELECT Id,ResourceType FROM workflowmasters
WHERE Id IN @Ids AND (ClientId=@ClientId OR ClientId IS NULL) AND IsActive=TRUE", new { Ids = workflowIds, ClientId = clientId })).ToDictionary(row => row.Id);
        if (workflowRows.Count != workflowIds.Length) return "One or more stage workflows are inactive or belong to another client.";

        bool UsesOnly(IEnumerable<long?> ids, string resourceType) => ids.Where(id => id is > 0)
            .All(id => workflowRows.TryGetValue(id!.Value, out var row) && row.ResourceType.Equals(resourceType, StringComparison.OrdinalIgnoreCase));
        if (!UsesOnly(stageRows.Select(stage => stage.ApprovalWorkflowId).Concat(transitionRows.Select(transition => transition.ApprovalWorkflowId)), "RecruitmentPipelineTransition"))
            return "Stage and transition approvals must use a Recruitment Pipeline Transition workflow from global Workflow Setup.";
        if (!UsesOnly(stageRows.SelectMany(stage => stage.Actions.Where(action => action.ActionCode.Equals("START_WORKFLOW", StringComparison.OrdinalIgnoreCase)).Select(action => action.WorkflowId)), "RecruitmentPipelineStageAction"))
            return "START WORKFLOW actions must use a Recruitment Pipeline Stage Action workflow from global Workflow Setup.";
        if (!UsesOnly(stageRows.SelectMany(stage => new[] { stage.OfferConfiguration?.ApprovalWorkflowId, stage.OfferConfiguration?.VarianceApprovalWorkflowId }), "RecruitmentOffer"))
            return "Offer approvals must use a Recruitment Offer workflow from global Workflow Setup.";
        return "";
    }

    private static void NormalizeStageCardScopes(SaveRecruitmentPipelineVersion request)
    {
        foreach (var stage in request.Stages)
        {
            stage.CardScope = request.ScopeType.Equals("Position", StringComparison.OrdinalIgnoreCase)
                ? "Position"
                : request.ScopeType.Equals("Application", StringComparison.OrdinalIgnoreCase)
                    ? "Application"
                    : Canonical(new[] { "Position", "Application" }, stage.CardScope, "Application");
        }
    }

    private static string ValidatePipelineDraft(SaveRecruitmentPipelineVersion request)
    {
        if (request.PipelineDefinitionId <= 0) return "Pipeline definition is required.";
        if (request.Stages.Count < 2) return "A pipeline requires at least two stages.";
        if (request.Stages.Count(x => x.IsInitial && x.IsActive) != 1 || request.Stages.Any(x => x.IsInitial && !x.IsActive)) return "A pipeline must have exactly one active initial stage.";
        if (request.Stages.All(x => !x.IsTerminal || !x.IsActive)) return "A pipeline requires at least one active terminal stage.";
        if (request.Stages.Any(x => string.IsNullOrWhiteSpace(x.StageName) || !Regex.IsMatch(x.StageCode ?? "", "^[A-Za-z0-9_-]{2,80}$"))) return "Every stage needs a valid code and name.";
        if (request.Stages.GroupBy(x => x.StageCode.Trim(), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) return "Stage codes must be unique within a pipeline version.";
        if (request.Stages.GroupBy(x => x.DisplayOrder).Any(x => x.Count() > 1)) return "Stage display order must be unique.";
        if (request.Stages.Any(x => x.StageNumber <= 0) || request.Stages.GroupBy(x => x.StageNumber).Any(x => x.Count() > 1)) return "Stage numbers must be positive and unique.";
        if (request.Stages.Any(x => !StageTypes.Contains(x.StageType))) return "One or more pipeline stage types are unsupported.";
        if (request.Stages.Any(stage => !new[] { "Position", "Application" }.Contains(stage.CardScope, StringComparer.OrdinalIgnoreCase)))
            return "Every stage needs a valid Position or Application card scope.";
        var orderedActiveStages = request.Stages.Where(stage => stage.IsActive).OrderBy(stage => stage.DisplayOrder).ToList();
        if (request.ScopeType.Equals("Application", StringComparison.OrdinalIgnoreCase) && orderedActiveStages.Any(stage => !stage.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase)))
            return "Application pipelines can contain only Application stages.";
        if (request.ScopeType.Equals("Position", StringComparison.OrdinalIgnoreCase) && orderedActiveStages.Any(stage => !stage.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase)))
            return "Position pipelines can contain only Position stages.";
        if (request.ScopeType.Equals("Hybrid", StringComparison.OrdinalIgnoreCase))
        {
            if (!orderedActiveStages.Any(stage => stage.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase))
                || !orderedActiveStages.Any(stage => stage.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase)))
                return "Hybrid pipelines require at least one Position stage and one Application stage.";
            var firstApplicationOrder = orderedActiveStages.First(stage => stage.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase)).DisplayOrder;
            if (orderedActiveStages.Any(stage => stage.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase) && stage.DisplayOrder > firstApplicationOrder))
                return "All Position stages must appear before Application stages in a Hybrid pipeline.";
            if (orderedActiveStages.Single(stage => stage.IsInitial).CardScope != "Position")
                return "The initial stage of a Hybrid pipeline must be a Position stage.";
        }
        else if (orderedActiveStages.Single(stage => stage.IsInitial).CardScope != request.ScopeType)
            return $"The initial stage must use the {request.ScopeType} card scope.";
        if (request.Stages.Any(x => x.SlaDurationMinutes < 0 || x.SlaWarningMinutes < 0 || (x.SlaDurationMinutes > 0 && x.SlaWarningMinutes > x.SlaDurationMinutes))) return "Stage SLA and warning durations are invalid.";
        if (request.SlaMode.Equals("CumulativeFromAnchor", StringComparison.OrdinalIgnoreCase))
        {
            if (request.OverallSlaMinutes <= 0) return "Cumulative pipelines require an overall SLA.";
            var orderedTargets = request.Stages.Where(stage => stage.IsActive).OrderBy(stage => stage.DisplayOrder).Select(stage => stage.TargetOffsetMinutes).ToList();
            if (orderedTargets.Any(value => !value.HasValue || value.Value < 0)) return "Every active cumulative-SLA stage needs a target offset; zero is valid for a same-day stage.";
            if (orderedTargets.Zip(orderedTargets.Skip(1), (left, right) => right!.Value < left!.Value).Any(value => value)) return "Cumulative stage targets must increase with the configured stage order.";
            if (orderedTargets.Max()!.Value > request.OverallSlaMinutes) return "A stage target cannot exceed the pipeline overall SLA.";
        }
        foreach (var stage in request.Stages.Where(x => x.StageType.Equals("Interview", StringComparison.OrdinalIgnoreCase)))
        {
            var config = stage.InterviewConfiguration;
            if (config is null || config.RoundNumber <= 0 || config.DefaultDurationMinutes <= 0 || config.MinimumPanelCount <= 0 || config.MinimumPassingScore is < 0 or > 100) return $"Interview configuration for {stage.StageName} is incomplete.";
            if (!new[] { "PercentageWeighted", "Points" }.Contains(config.ScoreInputMode, StringComparer.OrdinalIgnoreCase)) return $"Interview score input mode for {stage.StageName} is invalid.";
            if (!new[] { "Average", "Median", "Chairperson" }.Contains(config.PanelAggregationMethod, StringComparer.OrdinalIgnoreCase)) return $"Panel aggregation for {stage.StageName} is invalid.";
            if (config.Competencies.Any(x => x.CompetencyId <= 0 || x.WeightPercent <= 0 || x.MinimumScore < 0 || (config.ScoreInputMode.Equals("Points", StringComparison.OrdinalIgnoreCase) ? x.MinimumScore > x.WeightPercent : x.MinimumScore > 100))) return $"Interview competencies for {stage.StageName} are invalid.";
            if (config.Competencies.Count > 0 && Math.Abs(config.Competencies.Sum(x => x.WeightPercent) - 100m) > 0.01m) return $"Interview competency weights for {stage.StageName} must total 100%.";
            if (stage.DefaultPanelMembers.Count > 0 && stage.DefaultPanelMembers.Count < config.MinimumPanelCount) return $"Default panel for {stage.StageName} needs at least {config.MinimumPanelCount} member(s).";
            if (stage.DefaultPanelMembers.Any(panel => panel.PanelUserId <= 0) || stage.DefaultPanelMembers.GroupBy(panel => panel.PanelUserId).Any(group => group.Count() > 1)) return $"Default panel members for {stage.StageName} are invalid.";
        }
        foreach (var stage in request.Stages)
        {
            var validTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OnEntry", "OnExit", "OnSlaWarning", "OnSlaBreach", "OnApproval", "OnSubmission", "OnProfileBatchForward" };
            var validActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SEND_NOTIFICATION", "START_WORKFLOW", "GENERATE_ACTION_LINK", "RUN_ATS_SCORE" };
            if (stage.Actions.Any(x => string.IsNullOrWhiteSpace(x.ActionCode) || x.ExecutionOrder < 0 || !validTriggers.Contains(x.TriggerEvent) || !validActions.Contains(x.ActionCode))) return $"Stage actions for {stage.StageName} are invalid.";
            if (stage.Actions.Any(x => x.IsBlocking && !x.TriggerEvent.Equals("OnEntry", StringComparison.OrdinalIgnoreCase) && !x.TriggerEvent.Equals("OnSubmission", StringComparison.OrdinalIgnoreCase))) return $"Blocking actions in {stage.StageName} must run on entry or candidate submission.";
            if (stage.Actions.Any(x => x.ActionCode.Equals("START_WORKFLOW", StringComparison.OrdinalIgnoreCase) && (x.WorkflowId is null or <= 0))) return $"Select a workflow for every workflow action in {stage.StageName}.";
            if (stage.Actions.Any(x => x.ActionCode.Equals("SEND_NOTIFICATION", StringComparison.OrdinalIgnoreCase) && (x.TemplateId is null or <= 0))) return $"Select a notification template for every notification action in {stage.StageName}.";
            var recipientTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Candidate", "InterviewPanelMembers", "StageDefaultPanelMembers", "SpecificUser", "UserRole", "StaticEmail", "HiringRequester", "PositionRecruiter" };
            foreach (var action in stage.Actions.Where(action => action.ActionCode.Equals("SEND_NOTIFICATION", StringComparison.OrdinalIgnoreCase)))
            {
                if (action.Recipients.Any(recipient => !recipientTypes.Contains(recipient.RecipientType))) return $"Notification recipients for {stage.StageName} contain an unsupported type.";
                if (action.Recipients.Any(recipient => recipient.RecipientType.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase) && recipient.UserId is null or <= 0)) return $"Choose a user for every specific-user recipient in {stage.StageName}.";
                if (action.Recipients.Any(recipient => recipient.RecipientType.Equals("UserRole", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(recipient.RoleCode))) return $"Choose a role for every role recipient in {stage.StageName}.";
                if (action.Recipients.Any(recipient => recipient.RecipientType.Equals("StaticEmail", StringComparison.OrdinalIgnoreCase) && (!System.Net.Mail.MailAddress.TryCreate(recipient.EmailAddress, out _)))) return $"Enter a valid address for every static email recipient in {stage.StageName}.";
                if (action.TriggerEvent.Equals("OnProfileBatchForward", StringComparison.OrdinalIgnoreCase) && action.Recipients.Any(recipient => recipient.RecipientType.Equals("Candidate", StringComparison.OrdinalIgnoreCase))) return $"Candidate cannot receive the client shortlist batch from {stage.StageName}. Choose client users, roles or panels.";
            }
            if (stage.Actions.GroupBy(x => x.TriggerEvent, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.GroupBy(x => x.ExecutionOrder).Any(order => order.Count() > 1)))
                return $"Action order must be unique within each trigger in {stage.StageName}.";
            foreach (var group in stage.Actions.GroupBy(x => x.TriggerEvent, StringComparer.OrdinalIgnoreCase))
            {
                var linkOrder = group.Where(x => x.ActionCode.Equals("GENERATE_ACTION_LINK", StringComparison.OrdinalIgnoreCase))
                    .Select(x => (int?)x.ExecutionOrder).Min();
                var notificationOrder = group.Where(x => x.ActionCode.Equals("SEND_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
                    .Select(x => (int?)x.ExecutionOrder).Min();
                if (linkOrder.HasValue && notificationOrder.HasValue && notificationOrder.Value <= linkOrder.Value)
                    return $"Generate the candidate link before sending its notification in {stage.StageName}.";
            }
            if (stage.Actions.Any(x => x.ActionCode.Equals("GENERATE_ACTION_LINK", StringComparison.OrdinalIgnoreCase)
                && (!x.TriggerEvent.Equals("OnEntry", StringComparison.OrdinalIgnoreCase)
                    || !(stage.StageType.Equals("ExternalForm", StringComparison.OrdinalIgnoreCase)
                        || stage.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase)
                        || stage.StageType.Equals("PreOnboarding", StringComparison.OrdinalIgnoreCase)
                        || stage.StageType.Equals("Offer", StringComparison.OrdinalIgnoreCase)))))
                return $"Candidate action links in {stage.StageName} require a candidate-facing stage and the OnEntry trigger.";
            if (stage.Actions.Any(x => x.ActionCode.Equals("RUN_ATS_SCORE", StringComparison.OrdinalIgnoreCase)
                && !stage.StageType.Equals("ATS", StringComparison.OrdinalIgnoreCase))) return $"ATS scoring actions can be used only in ATS stages.";
            if (stage.Actions.Any(x => x.TriggerEvent.Equals("OnSubmission", StringComparison.OrdinalIgnoreCase)
                && !(stage.StageType.Equals("ExternalForm", StringComparison.OrdinalIgnoreCase)
                    || stage.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase)
                    || stage.StageType.Equals("PreOnboarding", StringComparison.OrdinalIgnoreCase)
                    || stage.StageType.Equals("Offer", StringComparison.OrdinalIgnoreCase)))) return $"Submission actions in {stage.StageName} require a candidate-facing stage.";
            var ats = stage.AtsConfiguration;
            if (stage.StageType.Equals("ATS", StringComparison.OrdinalIgnoreCase) && ats is null) return $"ATS configuration for {stage.StageName} is required.";
            if (ats is not null && (ats.MinimumAdvanceScore is < 0 or > 100 || ats.MaximumRejectScore is < 0 or > 100 || ats.MaximumRejectScore > ats.MinimumAdvanceScore || (ats.AutoAdvance && string.IsNullOrWhiteSpace(ats.AdvanceOutcomeCode)) || (ats.AutoReject && string.IsNullOrWhiteSpace(ats.RejectOutcomeCode)))) return $"ATS behavior for {stage.StageName} is invalid.";
            var form = stage.ExternalFormConfiguration;
            if ((stage.StageType.Equals("ExternalForm", StringComparison.OrdinalIgnoreCase)
                || stage.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase)
                || stage.StageType.Equals("PreOnboarding", StringComparison.OrdinalIgnoreCase)) && form is null)
                return $"Candidate-form configuration for {stage.StageName} is required.";
            if (form is not null && (form.FormVersionId <= 0 || form.ActionTokenValidityMinutes is < 5 or > 525600 || form.ActionTokenMaximumUses is < 1 or > 1000)) return $"External-form behavior for {stage.StageName} is invalid.";
            if (stage.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase) && stage.AttachmentRequirements.Count == 0 && stage.ProcessDocumentRequirements.Count == 0) return $"Add at least one candidate attachment or process-document requirement to {stage.StageName}.";
            if (stage.AttachmentRequirements.Any(x => x.AttachmentFieldConfigurationId <= 0 || x.MinimumFileCount < 0 || x.MaximumFileCount < Math.Max(1, x.MinimumFileCount)) || stage.AttachmentRequirements.GroupBy(x => x.AttachmentFieldConfigurationId).Any(x => x.Count() > 1)) return $"Attachment requirements for {stage.StageName} are invalid.";
            var processDocumentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WORK_ORDER", "JD_ANNEXURE", "MOM", "SIGNED_MOM", "SCORE_ANNEXURE", "HR_PROPOSAL", "JOINING_INTIMATION", "CANDIDATE_PACK" };
            if (stage.ProcessDocumentRequirements.Any(x => !processDocumentTypes.Contains(x.DocumentType)) || stage.ProcessDocumentRequirements.GroupBy(x => x.DocumentType, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) return $"Process document requirements for {stage.StageName} are invalid.";
            var offer = stage.OfferConfiguration;
            if (stage.StageType.Equals("Offer", StringComparison.OrdinalIgnoreCase) && offer is null) return $"Offer configuration for {stage.StageName} is required.";
            var validBudgetBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ApprovedMaximum", "ApprovedTotal", "SalaryRangeMaximum" };
            if (offer is not null && (offer.MaximumVariancePercent < 0 || offer.CandidateResponseValidityDays is < 1 or > 365 || !validBudgetBases.Contains(offer.BudgetBasis) || (offer.RequireApprovalWhenVarianceExceeded && (offer.VarianceApprovalWorkflowId is null or <= 0)))) return $"Offer behavior for {stage.StageName} is invalid.";
            if (stage.RequiresApproval && (stage.ApprovalWorkflowId is null or <= 0)) return $"Approval workflow for {stage.StageName} is required.";
        }
        var stageById = request.Stages.Where(x => x.Id != 0).ToDictionary(x => x.Id);
        var stageByCode = request.Stages.ToDictionary(x => x.StageCode.Trim(), StringComparer.OrdinalIgnoreCase);
        RecruitmentPipelineStage? ResolveStage(long id, string code)
        {
            if (id != 0 && stageById.TryGetValue(id, out var byId)) return byId;
            return !string.IsNullOrWhiteSpace(code) && stageByCode.TryGetValue(code.Trim(), out var byCode) ? byCode : null;
        }
        if (request.Transitions.Count == 0 || request.Transitions.Any(x => ResolveStage(x.FromStageId, x.FromStageCode) is null || ResolveStage(x.ToStageId, x.ToStageCode) is null)) return "Every transition must reference stages from this pipeline version.";
        if (request.Transitions.Any(x => x.FromStageId != 0 && x.FromStageId == x.ToStageId) || request.Transitions.Any(x => !string.IsNullOrWhiteSpace(x.FromStageCode) && x.FromStageCode.Equals(x.ToStageCode, StringComparison.OrdinalIgnoreCase))) return "A transition cannot move to the same stage.";
        var numericRuleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ATS_SCORE", "INTERVIEW_SCORE" };
        var booleanRuleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RESUME_REQUIRED", "MANDATORY_DOCUMENTS_COMPLETE", "OFFER_ACCEPTED", "PANEL_FEEDBACK_COMPLETE" };
        var textRuleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "INTERVIEW_RESULT" };
        var numericOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EQ", "NE", "GT", "GTE", "LT", "LTE" };
        var textOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EQ", "NE", "CONTAINS", "NOT_CONTAINS" };
        foreach (var transition in request.Transitions)
        {
            if (transition.Rules.GroupBy(x => x.DisplayOrder).Any(x => x.Count() > 1)) return $"Rule order must be unique for transition {transition.ActionLabel}.";
            foreach (var rule in transition.Rules)
            {
                if (numericRuleTypes.Contains(rule.RuleType) && ((!rule.DecimalValue.HasValue && !rule.IntegerValue.HasValue) || !numericOperators.Contains(rule.ComparisonOperator))) return $"Enter a valid numeric rule for transition {transition.ActionLabel}.";
                if (booleanRuleTypes.Contains(rule.RuleType) && (!rule.BooleanValue.HasValue || !new[] { "EQ", "NE" }.Contains(rule.ComparisonOperator, StringComparer.OrdinalIgnoreCase))) return $"Enter a valid yes/no rule for transition {transition.ActionLabel}.";
                if (textRuleTypes.Contains(rule.RuleType) && (string.IsNullOrWhiteSpace(rule.TextValue) || !textOperators.Contains(rule.ComparisonOperator))) return $"Enter a valid text rule for transition {transition.ActionLabel}.";
                if (!numericRuleTypes.Contains(rule.RuleType) && !booleanRuleTypes.Contains(rule.RuleType) && !textRuleTypes.Contains(rule.RuleType)) return $"Transition {transition.ActionLabel} contains an unsupported rule type.";
            }
        }
        var activeTransitions = request.Transitions.Where(x => x.IsActive).ToList();
        if (activeTransitions.Any(x => ResolveStage(x.FromStageId, x.FromStageCode)?.IsActive != true || ResolveStage(x.ToStageId, x.ToStageCode)?.IsActive != true)) return "Active transitions can reference only active stages.";
        if (activeTransitions.Any(x => ResolveStage(x.FromStageId, x.FromStageCode)?.IsTerminal == true)) return "A terminal stage cannot have an outgoing active transition.";
        if (request.Stages.Any(x => (x.IsInitial || x.IsTerminal) && !x.IsActive)) return "Initial and terminal stages must remain active.";
        foreach (var transition in activeTransitions)
        {
            var from = ResolveStage(transition.FromStageId, transition.FromStageCode)!;
            var to = ResolveStage(transition.ToStageId, transition.ToStageCode)!;
            if (!to.IsTerminal && to.StageNumber > from.StageNumber + 1 && !from.AllowSkip)
                return $"Enable stage skipping on {from.StageName} before jumping directly to {to.StageName}.";
        }
        foreach (var stage in request.Stages.Where(x => x.IsActive && !x.IsTerminal))
        {
            var hasOutgoing = activeTransitions.Any(t => ResolveStage(t.FromStageId, t.FromStageCode) == stage);
            if (!hasOutgoing) return $"Stage {stage.StageName} requires at least one outgoing transition.";
        }
        var activeStages = request.Stages.Where(x => x.IsActive).ToList();
        var initial = activeStages.Single(x => x.IsInitial);
        var reachable = new HashSet<RecruitmentPipelineStage> { initial };
        var pending = new Queue<RecruitmentPipelineStage>();
        pending.Enqueue(initial);
        while (pending.TryDequeue(out var current))
            foreach (var next in activeTransitions.Where(t => ResolveStage(t.FromStageId, t.FromStageCode) == current).Select(t => ResolveStage(t.ToStageId, t.ToStageCode)!).Where(reachable.Add)) pending.Enqueue(next);
        var unreachable = activeStages.FirstOrDefault(x => !reachable.Contains(x));
        if (unreachable is not null) return $"Stage {unreachable.StageName} is not reachable from the initial stage.";
        var canReachTerminal = new HashSet<RecruitmentPipelineStage>(activeStages.Where(x => x.IsTerminal));
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var transition in activeTransitions)
            {
                var from = ResolveStage(transition.FromStageId, transition.FromStageCode)!;
                var to = ResolveStage(transition.ToStageId, transition.ToStageCode)!;
                if (canReachTerminal.Contains(to) && canReachTerminal.Add(from)) changed = true;
            }
        }
        var deadEnd = activeStages.FirstOrDefault(x => !canReachTerminal.Contains(x));
        if (deadEnd is not null) return $"Stage {deadEnd.StageName} has no route to a terminal stage.";
        return "";
    }

    private static long ResolveStageId(long requestId, string code, IReadOnlyDictionary<long, long> ids, IReadOnlyDictionary<string, long> codes)
    {
        if (requestId != 0 && ids.TryGetValue(requestId, out var mapped)) return mapped;
        return !string.IsNullOrWhiteSpace(code) && codes.TryGetValue(code.Trim(), out var byCode) ? byCode : 0;
    }

    private static async Task InsertStageBehaviorAsync(MySqlConnection db, MySqlTransaction tx, long stageId, RecruitmentPipelineStage stage)
    {
        foreach (var action in stage.Actions.OrderBy(x => x.ExecutionOrder))
        {
            var actionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_stage_actions
(PipelineStageId,TriggerEvent,ActionCode,ExecutionOrder,IsBlocking,WorkflowId,TemplateId,IsActive)
VALUES (@StageId,@TriggerEvent,@ActionCode,@ExecutionOrder,@IsBlocking,@WorkflowId,@TemplateId,@IsActive);SELECT LAST_INSERT_ID();",
                new { StageId = stageId, TriggerEvent = NormalizeTriggerEvent(action.TriggerEvent), ActionCode = action.ActionCode.Trim().ToUpperInvariant(), action.ExecutionOrder, action.IsBlocking, action.WorkflowId, action.TemplateId, action.IsActive }, tx);
            foreach (var recipient in action.Recipients.OrderBy(row => row.DisplayOrder))
                await db.ExecuteAsync(@"INSERT INTO recruitment_stage_action_recipients
(StageActionId,RecipientType,UserId,RoleCode,EmailAddress,DisplayOrder,IsActive)
VALUES (@StageActionId,@RecipientType,@UserId,@RoleCode,@EmailAddress,@DisplayOrder,@IsActive)", new
                {
                    StageActionId = actionId,
                    RecipientType = Canonical(new[] { "Candidate", "InterviewPanelMembers", "StageDefaultPanelMembers", "SpecificUser", "UserRole", "StaticEmail", "HiringRequester", "PositionRecruiter" }, recipient.RecipientType, "Candidate"),
                    recipient.UserId,
                    RoleCode = (recipient.RoleCode ?? "").Trim(),
                    EmailAddress = (recipient.EmailAddress ?? "").Trim(),
                    recipient.DisplayOrder,
                    recipient.IsActive
                }, tx);
        }
        foreach (var panel in stage.DefaultPanelMembers.OrderBy(row => row.DisplayOrder))
            await db.ExecuteAsync(@"INSERT INTO recruitment_stage_default_panel_members
(PipelineStageId,PanelUserId,PanelRole,IsRequired,DisplayOrder)
VALUES (@PipelineStageId,@PanelUserId,@PanelRole,@IsRequired,@DisplayOrder)", new
            {
                PipelineStageId = stageId,
                panel.PanelUserId,
                PanelRole = string.IsNullOrWhiteSpace(panel.PanelRole) ? "Panelist" : panel.PanelRole.Trim(),
                panel.IsRequired,
                panel.DisplayOrder
            }, tx);
        if (stage.AtsConfiguration?.AutoScoreOnEntry == true
            && !stage.Actions.Any(action => action.IsActive && action.ActionCode.Equals("RUN_ATS_SCORE", StringComparison.OrdinalIgnoreCase)))
            await db.ExecuteAsync(@"INSERT INTO recruitment_pipeline_stage_actions
(PipelineStageId,TriggerEvent,ActionCode,ExecutionOrder,IsBlocking,IsActive)
VALUES (@StageId,'OnEntry','RUN_ATS_SCORE',@ExecutionOrder,FALSE,TRUE)",
                new { StageId = stageId, ExecutionOrder = stage.Actions.Count == 0 ? 1 : stage.Actions.Max(action => action.ExecutionOrder) + 1 }, tx);
        if (stage.AtsConfiguration is not null)
        {
            var row = stage.AtsConfiguration;
            await db.ExecuteAsync(@"INSERT INTO recruitment_stage_ats_configurations
(PipelineStageId,ScoringProfileId,MinimumAdvanceScore,MaximumRejectScore,AutoScoreOnEntry,AutoAdvance,AutoReject,RequireHumanConfirmation,AdvanceOutcomeCode,RejectOutcomeCode)
VALUES (@StageId,@ScoringProfileId,@MinimumAdvanceScore,@MaximumRejectScore,@AutoScoreOnEntry,@AutoAdvance,@AutoReject,@RequireHumanConfirmation,@AdvanceOutcomeCode,@RejectOutcomeCode)",
                new { StageId = stageId, row.ScoringProfileId, row.MinimumAdvanceScore, row.MaximumRejectScore, row.AutoScoreOnEntry, row.AutoAdvance, row.AutoReject, row.RequireHumanConfirmation, AdvanceOutcomeCode = row.AdvanceOutcomeCode.Trim().ToUpperInvariant(), RejectOutcomeCode = row.RejectOutcomeCode.Trim().ToUpperInvariant() }, tx);
        }
        if (stage.ExternalFormConfiguration is not null)
        {
            var row = stage.ExternalFormConfiguration;
            await db.ExecuteAsync(@"INSERT INTO recruitment_stage_external_form_configurations
(PipelineStageId,FormVersionId,SubmissionRequired,AllowSaveDraft,ActionTokenValidityMinutes,ActionTokenMaximumUses)
VALUES (@StageId,@FormVersionId,@SubmissionRequired,@AllowSaveDraft,@ActionTokenValidityMinutes,@ActionTokenMaximumUses)",
                new { StageId = stageId, row.FormVersionId, row.SubmissionRequired, row.AllowSaveDraft, row.ActionTokenValidityMinutes, row.ActionTokenMaximumUses }, tx);
        }
        foreach (var row in stage.AttachmentRequirements.OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync(@"INSERT INTO recruitment_stage_attachment_requirements
(PipelineStageId,AttachmentFieldConfigurationId,IsRequired,MinimumFileCount,MaximumFileCount,RequiresVerification,DisplayOrder)
VALUES (@StageId,@AttachmentFieldConfigurationId,@IsRequired,@MinimumFileCount,@MaximumFileCount,@RequiresVerification,@DisplayOrder)",
                new { StageId = stageId, row.AttachmentFieldConfigurationId, row.IsRequired, row.MinimumFileCount, row.MaximumFileCount, row.RequiresVerification, row.DisplayOrder }, tx);
        foreach (var row in stage.ProcessDocumentRequirements.OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync(@"INSERT INTO recruitment_stage_process_document_requirements
(PipelineStageId,DocumentType,TemplateId,IsRequired,RequiresSignature,DisplayOrder)
VALUES (@StageId,@DocumentType,@TemplateId,@IsRequired,@RequiresSignature,@DisplayOrder)",
                new { StageId = stageId, DocumentType = row.DocumentType.Trim().ToUpperInvariant(), row.TemplateId, row.IsRequired, row.RequiresSignature, row.DisplayOrder }, tx);
        if (stage.OfferConfiguration is not null)
        {
            var row = stage.OfferConfiguration;
            await db.ExecuteAsync(@"INSERT INTO recruitment_stage_offer_configurations
(PipelineStageId,OfferTemplateId,ApprovalWorkflowId,BudgetBasis,MaximumVariancePercent,RequireApprovalWhenVarianceExceeded,VarianceApprovalWorkflowId,CandidateResponseValidityDays,RequireAcceptedOfferToAdvance)
VALUES (@StageId,@OfferTemplateId,@ApprovalWorkflowId,@BudgetBasis,@MaximumVariancePercent,@RequireApprovalWhenVarianceExceeded,@VarianceApprovalWorkflowId,@CandidateResponseValidityDays,@RequireAcceptedOfferToAdvance)",
                new { StageId = stageId, row.OfferTemplateId, row.ApprovalWorkflowId, BudgetBasis = NormalizeBudgetBasis(row.BudgetBasis), row.MaximumVariancePercent, row.RequireApprovalWhenVarianceExceeded, row.VarianceApprovalWorkflowId, row.CandidateResponseValidityDays, row.RequireAcceptedOfferToAdvance }, tx);
        }
    }

    private static string Canonical(IEnumerable<string> allowed, string? input, string fallback) =>
        allowed.FirstOrDefault(value => value.Equals((input ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static async Task InsertInterviewStageConfigurationAsync(MySqlConnection db, MySqlTransaction tx, long stageId, RecruitmentInterviewStageConfiguration? config, int clientId)
    {
        config ??= new RecruitmentInterviewStageConfiguration();
        var configId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_interview_stage_configurations
(PipelineStageId,RoundNumber,InterviewType,DefaultDurationMinutes,MinimumPanelCount,MinimumPassingScore,ScoreInputMode,PanelAggregationMethod,FeedbackRequired,CalendarEnabled,AllowReschedule)
VALUES (@StageId,@RoundNumber,@InterviewType,@Duration,@PanelCount,@PassingScore,@ScoreInputMode,@PanelAggregationMethod,@FeedbackRequired,@CalendarEnabled,@AllowReschedule);SELECT LAST_INSERT_ID();",
            new { StageId = stageId, RoundNumber = Math.Max(1, config.RoundNumber), InterviewType = config.InterviewType.Trim(), Duration = Math.Max(1, config.DefaultDurationMinutes), PanelCount = Math.Max(1, config.MinimumPanelCount), PassingScore = Math.Clamp(config.MinimumPassingScore, 0, 100), ScoreInputMode = Canonical(new[] { "PercentageWeighted", "Points" }, config.ScoreInputMode, "PercentageWeighted"), PanelAggregationMethod = Canonical(new[] { "Average", "Median", "Chairperson" }, config.PanelAggregationMethod, "Average"), config.FeedbackRequired, config.CalendarEnabled, config.AllowReschedule }, tx);
        foreach (var competency in config.Competencies.OrderBy(x => x.DisplayOrder))
        {
            var valid = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_competency_definitions WHERE Id=@Id AND ClientId=@ClientId AND IsActive=TRUE", new { Id = competency.CompetencyId, ClientId = clientId }, tx);
            if (valid == 0) throw new InvalidOperationException("Interview competency does not belong to this client or is inactive.");
            await db.ExecuteAsync(@"INSERT INTO recruitment_interview_stage_competencies
(InterviewStageConfigurationId,CompetencyId,WeightPercent,MinimumScore,DisplayOrder)
VALUES (@ConfigId,@CompetencyId,@WeightPercent,@MinimumScore,@DisplayOrder)", new { ConfigId = configId, competency.CompetencyId, competency.WeightPercent, competency.MinimumScore, competency.DisplayOrder }, tx);
        }
    }

    private static async Task<RecruitmentJobDescriptionVersion?> LoadJobDescriptionAsync(MySqlConnection db, long id, int? clientId)
    {
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentJobDescriptionVersion>("SELECT * FROM recruitment_job_description_versions WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, ClientId = clientId });
        if (row is null) return null;
        row.Responsibilities = (await db.QueryAsync<RecruitmentJdResponsibility>("SELECT * FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id })).ToList();
        row.Skills = (await db.QueryAsync<RecruitmentJdSkillRequirement>("SELECT * FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@Id ORDER BY IsRequired DESC,DisplayOrder,Id", new { Id = id })).ToList();
        row.Qualifications = (await db.QueryAsync<RecruitmentJdQualificationRequirement>("SELECT * FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id })).ToList();
        row.Certifications = (await db.QueryAsync<RecruitmentJdCertificationRequirement>("SELECT * FROM recruitment_jd_certification_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id })).ToList();
        row.Languages = (await db.QueryAsync<RecruitmentJdLanguageRequirement>("SELECT * FROM recruitment_jd_language_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id })).ToList();
        row.Benefits = (await db.QueryAsync<RecruitmentJdBenefit>("SELECT * FROM recruitment_jd_benefits WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = id })).ToList();
        return row;
    }

    private static string ValidateJobDescriptionCompleteness(RecruitmentJobDescriptionVersion row)
    {
        if (string.IsNullOrWhiteSpace(row.Title)) return "Job-description title is required before approval.";
        if (string.IsNullOrWhiteSpace(row.Summary)) return "Job-description summary is required before approval.";
        if (row.Responsibilities.Count == 0 || row.Responsibilities.All(item => string.IsNullOrWhiteSpace(item.ResponsibilityText)))
            return "Add at least one responsibility before approval.";
        if (row.Skills.Count == 0 || row.Skills.Any(item => string.IsNullOrWhiteSpace(item.SkillName)))
            return "Add at least one skill and complete every skill name before approval.";
        if (!row.Skills.Any(item => item.IsRequired)) return "Mark at least one skill as must-have before approval.";
        if (row.Skills.Any(item => item.MinimumYears < 0 || item.WeightPercent is < 0 or > 100))
            return "Skill experience and relative weights must be valid before approval.";
        foreach (var bucket in row.Skills.GroupBy(item => item.IsRequired))
            if (bucket.Any(item => item.WeightPercent > 0) && bucket.Any(item => item.WeightPercent <= 0))
                return $"Complete every {(bucket.Key ? "must-have" : "preferred")} relative weight, or leave the group at zero for equal weighting.";
        return "";
    }

    private static async Task<string> BuildJobDescriptionApprovalSnapshotAsync(MySqlConnection db, RecruitmentJobDescriptionVersion source, AuthUser submittedBy)
    {
        var description = await LoadJobDescriptionAsync(db, source.Id, source.ClientId) ?? source;
        var context = await db.QueryFirstOrDefaultAsync<JobDescriptionApprovalContext>(@"SELECT COALESCE(c.Name,'') ClientName,
r.RfrNumber,r.PositionTitle,r.Department,r.BusinessUnit,r.EmploymentType,r.HiringType,r.NumberOfOpenings,r.JobLocation,r.WorkMode,
r.ExperienceRange,r.Qualification,r.SourceType,r.SourceReference,r.SourceDocumentName,p.Id PositionId,COALESCE(p.PositionCode,'') PositionCode
FROM recruitment_requisitions r
LEFT JOIN clients c ON c.Id=r.ClientId
LEFT JOIN recruitment_open_positions p ON p.RequisitionId=r.Id
WHERE r.Id=@RequisitionId
ORDER BY p.Id LIMIT 1", new { description.RequisitionId }) ?? new JobDescriptionApprovalContext();
        var attachments = (await db.QueryAsync<JobDescriptionApprovalAttachment>(@"SELECT a.public_id PublicId,at.attribute_name AttachmentType,
f.field_label FieldLabel,a.original_file_name FileName,a.file_size_bytes FileSizeBytes,a.version_number VersionNumber,
a.verification_status VerificationStatus,a.uploaded_at_utc UploadedAtUtc
FROM entity_attachments a
JOIN attachment_attributes at ON at.id=a.attachment_attribute_id
JOIN attachment_field_configurations f ON f.id=a.field_configuration_id
WHERE a.client_id=@ClientId AND a.is_current=TRUE AND a.is_deleted=FALSE AND (
    (a.entity_type='RECRUITMENT_JOB_DESCRIPTION' AND a.entity_id=@JobDescriptionId)
    OR (a.entity_type='RECRUITMENT_REQUISITION' AND a.entity_id=@RequisitionId)
    OR (a.entity_type='RECRUITMENT_WORK_ORDER' AND EXISTS (
        SELECT 1 FROM recruitment_work_order_lines line
        WHERE line.WorkOrderId=a.entity_id AND line.RequisitionId=@RequisitionId
    ))
)
ORDER BY a.uploaded_at_utc,a.id", new { description.ClientId, JobDescriptionId = description.Id, description.RequisitionId })).ToList();

        var snapshot = new
        {
            SnapshotType = "RecruitmentJobDescriptionApproval",
            CapturedAtUtc = DateTime.UtcNow,
            Client = new { Id = description.ClientId, Name = context.ClientName },
            Requisition = new
            {
                Id = description.RequisitionId,
                context.RfrNumber,
                context.PositionTitle,
                context.Department,
                context.BusinessUnit,
                context.EmploymentType,
                context.HiringType,
                context.NumberOfOpenings,
                context.JobLocation,
                context.WorkMode,
                context.ExperienceRange,
                context.Qualification,
                context.SourceType,
                context.SourceReference,
                context.SourceDocumentName
            },
            Position = new { Id = context.PositionId, context.PositionCode, Title = context.PositionTitle },
            JobDescription = new
            {
                description.Id,
                description.VersionNumber,
                description.Title,
                description.Summary,
                description.RolePurpose,
                description.Status,
                Responsibilities = description.Responsibilities.Select(item => new { Text = item.ResponsibilityText, item.DisplayOrder }),
                Skills = description.Skills.Select(item => new { Name = item.SkillName, Required = item.IsRequired, item.MinimumYears, item.MinimumProficiency, item.WeightPercent, item.DisplayOrder }),
                Qualifications = description.Qualifications.Select(item => new { Name = item.QualificationName, item.Specialization, Mandatory = item.IsMandatory, item.DisplayOrder }),
                Certifications = description.Certifications.Select(item => new { Name = item.CertificationName, Mandatory = item.IsMandatory, item.DisplayOrder }),
                Languages = description.Languages.Select(item => new { Name = item.LanguageName, item.Proficiency, Mandatory = item.IsMandatory, item.DisplayOrder }),
                Benefits = description.Benefits.Select(item => new { Name = item.BenefitName, item.Description, item.DisplayOrder }),
                MustHaveRelativeWeightTotal = description.Skills.Where(item => item.IsRequired).Sum(item => item.WeightPercent),
                PreferredRelativeWeightTotal = description.Skills.Where(item => !item.IsRequired).Sum(item => item.WeightPercent)
            },
            Attachments = attachments,
            SubmittedBy = new { submittedBy.Id, submittedBy.DisplayName, submittedBy.Email }
        };
        return JsonSerializer.Serialize(snapshot, ApprovalSnapshotJson);
    }

    private static Task BindApprovedJobDescriptionAsync(MySqlConnection db, long id, MySqlTransaction? transaction = null) =>
        db.ExecuteAsync(@"UPDATE recruitment_open_positions p JOIN recruitment_job_description_versions j ON j.RequisitionId=p.RequisitionId
SET p.ApprovedJobDescriptionVersionId=j.Id,p.JobDescriptionText=j.Summary,p.JobDescriptionVersion=j.VersionNumber WHERE j.Id=@Id", new { Id = id }, transaction);

    private static async Task DeleteJobDescriptionChildrenAsync(MySqlConnection db, MySqlTransaction tx, long id)
    {
        foreach (var table in new[] { "recruitment_jd_responsibilities", "recruitment_jd_skill_requirements", "recruitment_jd_qualification_requirements", "recruitment_jd_certification_requirements", "recruitment_jd_language_requirements", "recruitment_jd_benefits" })
            await db.ExecuteAsync($"DELETE FROM `{table}` WHERE JobDescriptionVersionId=@Id", new { Id = id }, tx);
    }

    private static async Task InsertJobDescriptionChildrenAsync(MySqlConnection db, MySqlTransaction tx, long id, SaveRecruitmentJobDescriptionVersion request)
    {
        foreach (var row in request.Responsibilities.Where(x => !string.IsNullOrWhiteSpace(x.ResponsibilityText)).OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync("INSERT INTO recruitment_jd_responsibilities (JobDescriptionVersionId,ResponsibilityText,DisplayOrder) VALUES (@Id,@Text,@Order)", new { Id = id, Text = row.ResponsibilityText.Trim(), Order = row.DisplayOrder }, tx);
        foreach (var row in request.Skills.Where(x => !string.IsNullOrWhiteSpace(x.SkillName)).OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync(@"INSERT INTO recruitment_jd_skill_requirements (JobDescriptionVersionId,SkillId,SkillName,IsRequired,MinimumYears,MinimumProficiency,WeightPercent,DisplayOrder)
VALUES (@Id,@SkillId,@Name,@Required,@Years,@Proficiency,@Weight,@Order)", new { Id = id, row.SkillId, Name = row.SkillName.Trim(), Required = row.IsRequired, Years = Math.Max(0, row.MinimumYears), Proficiency = row.MinimumProficiency.Trim(), Weight = Math.Clamp(row.WeightPercent, 0, 100), Order = row.DisplayOrder }, tx);
        foreach (var row in request.Qualifications.Where(x => !string.IsNullOrWhiteSpace(x.QualificationName)).OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync("INSERT INTO recruitment_jd_qualification_requirements (JobDescriptionVersionId,QualificationName,Specialization,IsMandatory,DisplayOrder) VALUES (@Id,@Name,@Specialization,@Mandatory,@Order)", new { Id = id, Name = row.QualificationName.Trim(), Specialization = row.Specialization.Trim(), Mandatory = row.IsMandatory, Order = row.DisplayOrder }, tx);
        foreach (var row in request.Certifications.Where(x => !string.IsNullOrWhiteSpace(x.CertificationName)).OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync("INSERT INTO recruitment_jd_certification_requirements (JobDescriptionVersionId,CertificationName,IsMandatory,DisplayOrder) VALUES (@Id,@Name,@Mandatory,@Order)", new { Id = id, Name = row.CertificationName.Trim(), Mandatory = row.IsMandatory, Order = row.DisplayOrder }, tx);
        foreach (var row in request.Languages.Where(x => !string.IsNullOrWhiteSpace(x.LanguageName)).OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync("INSERT INTO recruitment_jd_language_requirements (JobDescriptionVersionId,LanguageName,Proficiency,IsMandatory,DisplayOrder) VALUES (@Id,@Name,@Proficiency,@Mandatory,@Order)", new { Id = id, Name = row.LanguageName.Trim(), Proficiency = row.Proficiency.Trim(), Mandatory = row.IsMandatory, Order = row.DisplayOrder }, tx);
        foreach (var row in request.Benefits.Where(x => !string.IsNullOrWhiteSpace(x.BenefitName)).OrderBy(x => x.DisplayOrder))
            await db.ExecuteAsync("INSERT INTO recruitment_jd_benefits (JobDescriptionVersionId,BenefitName,Description,DisplayOrder) VALUES (@Id,@Name,@Description,@Order)", new { Id = id, Name = row.BenefitName.Trim(), Description = row.Description.Trim(), Order = row.DisplayOrder }, tx);
    }

    private static async Task<RecruitmentJobPosting?> GetJobPostingAsync(MySqlConnection db, long id, int? clientId) =>
        await db.QueryFirstOrDefaultAsync<RecruitmentJobPosting>(JobPostingSelect + " WHERE p.Id=@Id AND (@ClientId IS NULL OR p.ClientId=@ClientId)", new { Id = id, ClientId = clientId });

    private static async Task<RecruitmentApplicationStageInstance?> CurrentStageAsync(MySqlConnection db, long applicationId, int? clientId) =>
        await db.QueryFirstOrDefaultAsync<RecruitmentApplicationStageInstance>(@"SELECT s.Id,s.ApplicationPipelineInstanceId,s.ApplicationId,s.PipelineStageId,
d.StageCode,d.StageName,s.Status,s.OutcomeCode,s.EnteredAtUtc,
CASE WHEN s.DueAtUtc IS NULL THEN NULL ELSE TIMESTAMPADD(SECOND,COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0),s.DueAtUtc) END DueAtUtc,s.ExitedAtUtc,
GREATEST(0,TIMESTAMPDIFF(SECOND,s.EnteredAtUtc,UTC_TIMESTAMP())-s.PausedDurationSeconds-COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0)) ActiveDurationSeconds,
s.PausedDurationSeconds+COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0) PausedDurationSeconds,
CASE WHEN s.DueAtUtc IS NOT NULL AND TIMESTAMPADD(SECOND,COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pp.PausedAtUtc,UTC_TIMESTAMP())) FROM recruitment_stage_pause_periods pp WHERE pp.StageInstanceId=s.Id AND pp.ResumedAtUtc IS NULL),0),s.DueAtUtc)<UTC_TIMESTAMP() THEN TRUE ELSE FALSE END IsSlaBreached
FROM recruitment_application_stage_instances s JOIN recruitment_pipeline_stages d ON d.Id=s.PipelineStageId
JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId
JOIN recruitment_application_pipeline_instances p ON p.Id=s.ApplicationPipelineInstanceId AND p.CurrentStageInstanceId=s.Id
WHERE s.ApplicationId=@ApplicationId AND s.Status IN ('Active','Paused') AND (@ClientId IS NULL OR a.ClientId=@ClientId)", new { ApplicationId = applicationId, ClientId = clientId });

    private static async Task<int> ResolveWorkflowRequestorAsync(MySqlConnection db, long applicationId, int fallbackUserId) =>
        await db.ExecuteScalarAsync<int?>(@"SELECT COALESCE(requesterUser.Id,applicationRecruiter.Id,positionRecruiter.Id,fallbackUser.Id)
FROM recruitment_candidate_applications applicationRow
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_requisitions requisition ON requisition.Id=positionRow.RequisitionId
LEFT JOIN authusers requesterUser ON requesterUser.Id=requisition.RequestedByUserId AND requesterUser.IsActive=TRUE
LEFT JOIN authusers applicationRecruiter ON applicationRecruiter.Id=applicationRow.RecruiterUserId AND applicationRecruiter.IsActive=TRUE
LEFT JOIN authusers positionRecruiter ON positionRecruiter.Id=positionRow.RecruiterUserId AND positionRecruiter.IsActive=TRUE
LEFT JOIN authusers fallbackUser ON fallbackUser.Id=@FallbackUserId AND fallbackUser.IsActive=TRUE
WHERE applicationRow.Id=@ApplicationId LIMIT 1", new { ApplicationId = applicationId, FallbackUserId = fallbackUserId }) ?? 0;

    private static Task AddStageEventAsync(MySqlConnection db, MySqlTransaction tx, long stageInstanceId, string type, string title, string details, int? actorUserId) =>
        db.ExecuteAsync("INSERT INTO recruitment_stage_events (StageInstanceId,EventType,EventTitle,EventDetails,ActorUserId) VALUES (@Id,@Type,@Title,@Details,@Actor)", new { Id = stageInstanceId, Type = type, Title = title, Details = details ?? "", Actor = actorUserId }, tx);

    private static string NormalizeWorkflowDecision(string value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "APPROVED" => "Approved",
        "REJECTED" => "Rejected",
        "SENT BACK" or "SENT_BACK" or "SENTBACK" => "Sent Back",
        _ => ""
    };

    private static string NormalizeTriggerEvent(string value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "ONENTRY" => "OnEntry",
        "ONEXIT" => "OnExit",
        "ONSLAWARNING" => "OnSlaWarning",
        "ONSLABREACH" => "OnSlaBreach",
        "ONAPPROVAL" => "OnApproval",
        "ONSUBMISSION" => "OnSubmission",
        "ONPROFILEBATCHFORWARD" => "OnProfileBatchForward",
        _ => (value ?? "").Trim()
    };

    private static string NormalizeBudgetBasis(string value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "APPROVEDMAXIMUM" => "ApprovedMaximum",
        "APPROVEDTOTAL" => "ApprovedTotal",
        "SALARYRANGEMAXIMUM" => "SalaryRangeMaximum",
        _ => (value ?? "").Trim()
    };

    private static async Task EnsureColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        var tableExists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@Table", new { Table = table });
        if (tableExists == 0) return;
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@Table AND LOWER(column_name)=LOWER(@Column)", new { Table = table, Column = column });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }

    private static Task<bool> ColumnExistsAsync(MySqlConnection db, string table, string column) =>
        db.ExecuteScalarAsync<bool>(@"SELECT COUNT(*)>0 FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name=@Table AND LOWER(column_name)=LOWER(@Column)", new { Table = table, Column = column });

    private static async Task BackfillStageCardScopesAsync(MySqlConnection db)
    {
        var rows = (await db.QueryAsync<StageScopeBackfillRow>(@"SELECT stageRow.Id,stageRow.PipelineVersionId,
COALESCE(versionRow.ScopeType,'Application') ScopeType,stageRow.StageCode,stageRow.StageName,stageRow.CardScope,
stageRow.DisplayOrder,stageRow.IsActive
FROM recruitment_pipeline_stages stageRow
JOIN recruitment_pipeline_versions versionRow ON versionRow.Id=stageRow.PipelineVersionId
ORDER BY stageRow.PipelineVersionId,stageRow.DisplayOrder,stageRow.Id")).ToList();

        await using var transaction = await db.BeginTransactionAsync();
        foreach (var version in rows.GroupBy(row => row.PipelineVersionId))
        {
            var ordered = version.OrderBy(row => row.DisplayOrder).ThenBy(row => row.Id).ToList();
            var scopeType = ordered.FirstOrDefault()?.ScopeType ?? "Application";
            var currentScopesAreValid = scopeType.Equals("Position", StringComparison.OrdinalIgnoreCase)
                ? ordered.All(row => row.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase))
                : scopeType.Equals("Application", StringComparison.OrdinalIgnoreCase)
                    ? ordered.All(row => row.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase))
                    : HybridStageScopesAreValid(ordered);
            if (currentScopesAreValid) continue;
            var positionBoundary = -1;
            if (scopeType.Equals("Hybrid", StringComparison.OrdinalIgnoreCase) && ordered.Count > 0)
            {
                positionBoundary = ordered.FindIndex(row =>
                    (row.StageCode ?? "").Contains("WORK_ORDER_INTAKE", StringComparison.OrdinalIgnoreCase)
                    || (row.StageName ?? "").Contains("Work Order Intake", StringComparison.OrdinalIgnoreCase));
                if (positionBoundary < 0)
                {
                    positionBoundary = ordered.FindIndex(row => row.IsActive);
                    if (positionBoundary < 0) positionBoundary = 0;
                }
            }

            for (var index = 0; index < ordered.Count; index++)
            {
                var cardScope = scopeType.Equals("Position", StringComparison.OrdinalIgnoreCase)
                    ? "Position"
                    : scopeType.Equals("Hybrid", StringComparison.OrdinalIgnoreCase) && index <= positionBoundary
                        ? "Position"
                        : "Application";
                await db.ExecuteAsync("UPDATE recruitment_pipeline_stages SET CardScope=@CardScope WHERE Id=@Id",
                    new { ordered[index].Id, CardScope = cardScope }, transaction);
            }
        }
        await transaction.CommitAsync();
    }

    private static bool HybridStageScopesAreValid(IReadOnlyList<StageScopeBackfillRow> stages)
    {
        var active = stages.Where(row => row.IsActive).ToList();
        if (!active.Any(row => row.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase))
            || !active.Any(row => row.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase))) return false;
        var firstApplication = active.FindIndex(row => row.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase));
        return active.Take(firstApplication).All(row => row.CardScope.Equals("Position", StringComparison.OrdinalIgnoreCase))
            && active.Skip(firstApplication).All(row => row.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task DropColumnIfExistsAsync(MySqlConnection db, string table, string column)
    {
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@Table AND LOWER(column_name)=LOWER(@Column)", new { Table = table, Column = column });
        if (exists > 0) await db.ExecuteAsync($"ALTER TABLE `{table}` DROP COLUMN `{column}`");
    }

    private const string JobPostingSelect = @"SELECT p.*,o.PositionCode,o.PositionTitle,COALESCE(c.Name,'') ClientName,
COALESCE(rs.PublicPortalBaseUrl,'') PublicPortalBaseUrl,
CASE WHEN rs.RecruitmentEnabled=TRUE AND rs.EnableCandidatePortal=TRUE AND rs.IsActive=TRUE AND TRIM(rs.PublicPortalBaseUrl)<>'' THEN TRUE ELSE FALSE END CandidatePortalReady
FROM recruitment_job_postings p
JOIN recruitment_open_positions o ON o.Id=p.PositionId
LEFT JOIN clients c ON c.Id=p.ClientId
LEFT JOIN recruitment_settings rs ON rs.ClientId=p.ClientId";

    private sealed class JobDescriptionApprovalContext { public string ClientName { get; set; } = ""; public string RfrNumber { get; set; } = ""; public string PositionTitle { get; set; } = ""; public string Department { get; set; } = ""; public string BusinessUnit { get; set; } = ""; public string EmploymentType { get; set; } = ""; public string HiringType { get; set; } = ""; public int NumberOfOpenings { get; set; } public string JobLocation { get; set; } = ""; public string WorkMode { get; set; } = ""; public string ExperienceRange { get; set; } = ""; public string Qualification { get; set; } = ""; public string SourceType { get; set; } = ""; public string SourceReference { get; set; } = ""; public string SourceDocumentName { get; set; } = ""; public long? PositionId { get; set; } public string PositionCode { get; set; } = ""; }
    private sealed class JobDescriptionApprovalAttachment { public string PublicId { get; set; } = ""; public string AttachmentType { get; set; } = ""; public string FieldLabel { get; set; } = ""; public string FileName { get; set; } = ""; public long FileSizeBytes { get; set; } public int VersionNumber { get; set; } public string VerificationStatus { get; set; } = ""; public DateTime UploadedAtUtc { get; set; } }
    private sealed class PostingSourceRow { public int ClientId { get; set; } public long RequisitionId { get; set; } public string PositionTitle { get; set; } = ""; public string JobDescriptionStatus { get; set; } = ""; public long JobDescriptionRequisitionId { get; set; } }
    private sealed class AssignmentSourceRow { public int ClientId { get; set; } public string Status { get; set; } = ""; public int PipelineClientId { get; set; } }
    private sealed class ApplicationSourceRow { public long Id { get; set; } public long PositionId { get; set; } public int ClientId { get; set; } public long? JobPostingId { get; set; } }
    private sealed class PositionBoardRow { public long PositionId { get; set; } public string PositionCode { get; set; } = ""; public string PositionTitle { get; set; } = ""; public int ClientId { get; set; } }
    private sealed class StageScopeBackfillRow { public long Id { get; set; } public long PipelineVersionId { get; set; } public string ScopeType { get; set; } = "Application"; public string StageCode { get; set; } = ""; public string StageName { get; set; } = ""; public string CardScope { get; set; } = "Application"; public int DisplayOrder { get; set; } public bool IsActive { get; set; } }
    private sealed class WorkspaceDemandRow : RecruitmentPipelineDemandCard { public long? AssignedPipelineVersionId { get; set; } }
    private sealed class WorkspaceAssignmentRow { public long PositionId { get; set; } public int ClientId { get; set; } public long PipelineVersionId { get; set; } }
    private sealed class WorkspacePublishedPipelineRow { public int ClientId { get; set; } public long PipelineVersionId { get; set; } }
    private sealed class WorkspaceBoardCardRow : RecruitmentPipelineBoardCard { public long PipelineVersionId { get; set; } public long StageId { get; set; } }
    private sealed class BoardCardRow : RecruitmentPipelineBoardCard { public long StageId { get; set; } public RecruitmentPipelineBoardCard Card => this; }
    private sealed class TransitionContextRow { public int ClientId { get; set; } public long PipelineInstanceId { get; set; } public long CurrentStageInstanceId { get; set; } public long CurrentStageId { get; set; } public long TransitionId { get; set; } public long ToStageId { get; set; } public bool RequiresReason { get; set; } public long? ApprovalWorkflowId { get; set; } }
    private sealed class AtsAutomationRow { public int ClientId { get; set; } public long PipelineStageId { get; set; } public decimal MinimumAdvanceScore { get; set; } public decimal MaximumRejectScore { get; set; } public bool AutoAdvance { get; set; } public bool AutoReject { get; set; } public bool RequireHumanConfirmation { get; set; } public string AdvanceOutcomeCode { get; set; } = ""; public string RejectOutcomeCode { get; set; } = ""; public decimal? CurrentScore { get; set; } public string CurrentScoreStatus { get; set; } = ""; public bool CurrentScoreRequiresReview { get; set; } }
    private class TransitionRequestRow { public long Id { get; set; } public long ApplicationId { get; set; } public long StageInstanceId { get; set; } public long TransitionId { get; set; } public string Reason { get; set; } = ""; public string Status { get; set; } = ""; public long? WorkflowInstanceId { get; set; } public DateTime? AppliedAtUtc { get; set; } public int ClientId { get; set; } }
    private sealed class ApplyTransitionRow : TransitionRequestRow { public long FromStageId { get; set; } public long ToStageId { get; set; } public string OutcomeCode { get; set; } = ""; public string FromStageName { get; set; } = ""; public string ToStageName { get; set; } = ""; public int SlaDurationMinutes { get; set; } public bool IsTerminal { get; set; } public string ToStageType { get; set; } = ""; }
    private sealed class StageLockRow { public long Id { get; set; } public long ApplicationPipelineInstanceId { get; set; } public long ApplicationId { get; set; } public long PipelineStageId { get; set; } public string Status { get; set; } = ""; public DateTime EnteredAtUtc { get; set; } public long PausedDurationSeconds { get; set; } public long CurrentStageInstanceId { get; set; } public long PipelineInstanceId { get; set; } }
    private sealed class PauseRow { public long Id { get; set; } public long DurationSeconds { get; set; } }
    private sealed class WorkflowBindingRow { public long Id { get; set; } public string ResourceType { get; set; } = ""; }
}
