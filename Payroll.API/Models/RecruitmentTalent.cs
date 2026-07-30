namespace Payroll.API.Models;

public class RecruitmentCandidate
{
    public long Id { get; set; }
    public string CandidateCode { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public int? EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string CurrentCompany { get; set; } = "";
    public string CurrentTitle { get; set; } = "";
    public int TotalExperienceMonths { get; set; }
    public string CurrentLocation { get; set; } = "";
    public string PreferredLocationsJson { get; set; } = "[]";
    public int? NoticePeriodDays { get; set; }
    public decimal? CurrentCtc { get; set; }
    public decimal? ExpectedCtc { get; set; }
    public string HighestQualification { get; set; } = "";
    public string SourceType { get; set; } = "Direct";
    public long? SourceReferenceId { get; set; }
    public string ProfileStatus { get; set; } = "Active";
    public string ConsentStatus { get; set; } = "Pending";
    public DateTime? ConsentCapturedAt { get; set; }
    public DateTime? RetentionUntil { get; set; }
    public long? DuplicateOfCandidateId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int ApplicationCount { get; set; }
    public decimal? LatestScore { get; set; }
}

public class SaveRecruitmentCandidate
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string CurrentCompany { get; set; } = "";
    public string CurrentTitle { get; set; } = "";
    public int TotalExperienceMonths { get; set; }
    public string CurrentLocation { get; set; } = "";
    public string PreferredLocationsJson { get; set; } = "[]";
    public int? NoticePeriodDays { get; set; }
    public decimal? CurrentCtc { get; set; }
    public decimal? ExpectedCtc { get; set; }
    public string HighestQualification { get; set; } = "";
    public string SourceType { get; set; } = "Direct";
    public long? SourceReferenceId { get; set; }
    public string ProfileStatus { get; set; } = "Active";
    public string ConsentStatus { get; set; } = "Pending";
    public DateTime? ConsentCapturedAt { get; set; }
    public DateTime? RetentionUntil { get; set; }
}

public class RecruitmentCandidateResume
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public Guid AttachmentPublicId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public int VersionNumber { get; set; }
    public bool IsPrimary { get; set; }
    public string ParsingStatus { get; set; } = "Pending";
    public string ParsedText { get; set; } = "";
    public string ParsedJson { get; set; } = "{}";
    public string ParserName { get; set; } = "";
    public string ParserVersion { get; set; } = "";
    public DateTime? ParsedAt { get; set; }
    public string ParsingError { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public RecruitmentResumeParseFacts? ParseFacts { get; set; }
    public List<RecruitmentResumeParserRun> ParserRuns { get; set; } = [];
    public List<RecruitmentResumeSection> Sections { get; set; } = [];
    public List<RecruitmentResumeParsedSkill> ParsedSkills { get; set; } = [];
}

public class RecruitmentResumeParserRun
{
    public long Id { get; set; }
    public long ResumeId { get; set; }
    public string ParserName { get; set; } = "";
    public string ParserVersion { get; set; } = "";
    public string ParseStatus { get; set; } = "Pending";
    public int ExtractedCharacterCount { get; set; }
    public int ExtractedLineCount { get; set; }
    public string ErrorMessage { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class RecruitmentResumeParseFacts
{
    public long Id { get; set; }
    public long ResumeId { get; set; }
    public string ExtractedEmail { get; set; } = "";
    public string ExtractedPhone { get; set; } = "";
    public int CharacterCount { get; set; }
    public int LineCount { get; set; }
    public string LanguageCode { get; set; } = "und";
    public string SummaryText { get; set; } = "";
    public int? TotalExperienceMonths { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class RecruitmentResumeSection
{
    public long Id { get; set; }
    public long ResumeId { get; set; }
    public string SectionCode { get; set; } = "GENERAL";
    public string Heading { get; set; } = "";
    public string Content { get; set; } = "";
    public int DisplayOrder { get; set; }
    public decimal Confidence { get; set; }
}

public class RecruitmentResumeParsedSkill
{
    public long Id { get; set; }
    public long ResumeId { get; set; }
    public long? SkillId { get; set; }
    public string SkillName { get; set; } = "";
    public string MatchedTerm { get; set; } = "";
    public string EvidenceExcerpt { get; set; } = "";
    public decimal Confidence { get; set; }
}

public class RecruitmentCandidateApplication
{
    public long Id { get; set; }
    public string ApplicationCode { get; set; } = "";
    public long CandidateId { get; set; }
    public string CandidateCode { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public string CandidateEmail { get; set; } = "";
    public string CandidatePhone { get; set; } = "";
    public long PositionId { get; set; }
    public long? JobPostingId { get; set; }
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string SourceType { get; set; } = "Direct";
    public long? SourceReferenceId { get; set; }
    public long? ResumeId { get; set; }
    public string CurrentStatus { get; set; } = "New";
    public string CurrentStage { get; set; } = "New";
    public int? RecruiterUserId { get; set; }
    public string RecruiterName { get; set; } = "";
    public DateTime AppliedAt { get; set; }
    public DateTime LastStageChangedAt { get; set; }
    public string DispositionReason { get; set; } = "";
    public DateTime? RejectedAt { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public int? JoinedEmployeeId { get; set; }
    public decimal? AtsScore { get; set; }
    public string ScoreStatus { get; set; } = "Not Scored";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SaveCandidateApplication
{
    public long CandidateId { get; set; }
    public long PositionId { get; set; }
    public long? JobPostingId { get; set; }
    public string SourceType { get; set; } = "Direct";
    public long? SourceReferenceId { get; set; }
    public long? ResumeId { get; set; }
    public int? RecruiterUserId { get; set; }
}

public class ChangeCandidateStageRequest
{
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class RecruitmentApplicationStageHistory
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string FromStage { get; set; } = "";
    public string ToStage { get; set; } = "";
    public string Reason { get; set; } = "";
    public int ChangedByUserId { get; set; }
    public string ChangedByName { get; set; } = "";
    public DateTime ChangedAt { get; set; }
}

public class RecruitmentSkill
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string SkillCode { get; set; } = "";
    public string SkillName { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

public class RecruitmentCandidateSkill
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public long? SkillId { get; set; }
    public string SkillName { get; set; } = "";
    public decimal YearsExperience { get; set; }
    public string Proficiency { get; set; } = "";
    public string Source { get; set; } = "Resume";
    public decimal Confidence { get; set; }
}

public class RecruitmentCandidateExperience
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public string Employer { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCurrent { get; set; }
    public string Description { get; set; } = "";
    public int DisplayOrder { get; set; }
}

public class RecruitmentCandidateEducation
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public string Qualification { get; set; } = "";
    public string Institution { get; set; } = "";
    public string Specialization { get; set; } = "";
    public int? CompletionYear { get; set; }
    public string Score { get; set; } = "";
    public int DisplayOrder { get; set; }
}

public class RecruitmentCandidateCertification
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public string CertificationName { get; set; } = "";
    public string Issuer { get; set; } = "";
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string CredentialId { get; set; } = "";
}

public class SaveCandidateProfileSections
{
    public List<RecruitmentCandidateExperience> Experience { get; set; } = [];
    public List<RecruitmentCandidateEducation> Education { get; set; } = [];
    public List<RecruitmentCandidateCertification> Certifications { get; set; } = [];
}

public class RecruitmentAtsScoringProfile
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string ProfileName { get; set; } = "Default ATS profile";
    public string PositionCategory { get; set; } = "";
    public string ScoringMethod { get; set; } = "RuleBased";
    public decimal MinimumShortlistScore { get; set; } = 60;
    public bool AutoScoreOnResumeUpload { get; set; } = true;
    public bool AllowManualOverride { get; set; } = true;
    public string ParserProvider { get; set; } = "BuiltIn";
    public string ScoringProvider { get; set; } = "BuiltIn";
    public string ModelName { get; set; } = "Deterministic-v1";
    public int VersionNumber { get; set; } = 1;
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<RecruitmentAtsScoringCriterion> Criteria { get; set; } = [];
}

public class RecruitmentAtsScoringCriterion
{
    public long Id { get; set; }
    public long ScoringProfileId { get; set; }
    public string CriterionCode { get; set; } = "";
    public string CriterionLabel { get; set; } = "";
    public string EvaluationType { get; set; } = "TextMatch";
    public decimal Weight { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RecruitmentApplicationScore
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public long ResumeId { get; set; }
    public long? ScoringProfileId { get; set; }
    public decimal TotalScore { get; set; }
    public string ComponentScoresJson { get; set; } = "{}";
    public string MatchedSkillsJson { get; set; } = "[]";
    public string MissingSkillsJson { get; set; } = "[]";
    public string ExplanationJson { get; set; } = "{}";
    public string ScoringMethod { get; set; } = "RuleBased";
    public string ModelName { get; set; } = "Deterministic-v1";
    public string ModelVersion { get; set; } = "1";
    public string ScoreStatus { get; set; } = "Completed";
    public bool IsCurrent { get; set; } = true;
    public decimal? OverrideScore { get; set; }
    public string OverrideReason { get; set; } = "";
    public int? OverriddenByUserId { get; set; }
    public DateTime? OverriddenAt { get; set; }
    public DateTime ScoredAt { get; set; }
    public decimal ShortlistThreshold { get; set; }
    public string Recommendation { get; set; } = "";
    public string ExplanationText { get; set; } = "";
    public int ProfileVersionNumber { get; set; }
    public bool HumanReviewRequired { get; set; } = true;
    public List<RecruitmentApplicationScoreComponent> Components { get; set; } = [];
    public List<RecruitmentApplicationScoreSkillMatch> SkillMatches { get; set; } = [];
    public List<RecruitmentApplicationScoreEvidence> Evidence { get; set; } = [];
    public RecruitmentApplicationScorePositionSnapshot? PositionSnapshot { get; set; }
}

public class RecruitmentApplicationScoreComponent
{
    public long Id { get; set; }
    public long ApplicationScoreId { get; set; }
    public string CriterionCode { get; set; } = "";
    public string CriterionLabel { get; set; } = "";
    public decimal Weight { get; set; }
    public decimal RawRatio { get; set; }
    public decimal AwardedScore { get; set; }
    public decimal MaximumScore { get; set; }
    public string EvidenceSummary { get; set; } = "";
    public int DisplayOrder { get; set; }
}

public class RecruitmentApplicationScoreSkillMatch
{
    public long Id { get; set; }
    public long ApplicationScoreId { get; set; }
    public string SkillType { get; set; } = "Required";
    public string SkillName { get; set; } = "";
    public string MatchStatus { get; set; } = "Missing";
    public string MatchedTerm { get; set; } = "";
    public string EvidenceExcerpt { get; set; } = "";
    public decimal RequirementWeight { get; set; }
    public decimal MinimumYears { get; set; }
    public string MinimumProficiency { get; set; } = "";
    public decimal Confidence { get; set; }
}

public class RecruitmentApplicationScoreEvidence
{
    public long Id { get; set; }
    public long ApplicationScoreId { get; set; }
    public string CriterionCode { get; set; } = "";
    public string EvidenceType { get; set; } = "";
    public string ExpectedValue { get; set; } = "";
    public string ActualValue { get; set; } = "";
    public string MatchStatus { get; set; } = "NotMatched";
    public decimal Confidence { get; set; }
    public long? ResumeSectionId { get; set; }
}

public class RecruitmentApplicationScorePositionSnapshot
{
    public long Id { get; set; }
    public long ApplicationScoreId { get; set; }
    public long PositionId { get; set; }
    public long? JobDescriptionVersionId { get; set; }
    public int JobDescriptionVersionNumber { get; set; }
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string RequiredSkills { get; set; } = "";
    public string PreferredSkills { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string Qualification { get; set; } = "";
    public string Certifications { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public DateTime CapturedAt { get; set; }
}

public class OverrideApplicationScoreRequest
{
    public decimal Score { get; set; }
    public string Reason { get; set; } = "";
}

public class RecruitmentInterview
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string CandidateName { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string RoundCode { get; set; } = "Round 1";
    public string InterviewType { get; set; } = "Technical";
    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }
    public string Mode { get; set; } = "Virtual";
    public string LocationOrLink { get; set; } = "";
    public string Status { get; set; } = "Scheduled";
    public string Result { get; set; } = "Pending";
    public string OverallFeedback { get; set; } = "";
    public decimal OverallScore { get; set; }
    public string PanelUserIdsJson { get; set; } = "[]";
    public List<int> PanelUserIds { get; set; } = [];
    public long? PipelineStageInstanceId { get; set; }
    public long? RoundConfigurationId { get; set; }
    public string PipelineStageName { get; set; } = "";
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
    public int AttemptNumber { get; set; } = 1;
    public int RescheduleCount { get; set; }
    public bool IsPipelineManaged { get; set; }
    public int DefaultDurationMinutes { get; set; }
    public int MinimumPanelCount { get; set; }
    public decimal MinimumPassingScore { get; set; }
    public string ScoreInputMode { get; set; } = "PercentageWeighted";
    public string PanelAggregationMethod { get; set; } = "Average";
    public bool FeedbackRequired { get; set; }
    public bool CalendarEnabled { get; set; }
    public bool AllowReschedule { get; set; } = true;
    public List<RecruitmentInterviewStageCompetency> Competencies { get; set; } = [];
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SaveRecruitmentInterview
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string RoundCode { get; set; } = "Round 1";
    public string InterviewType { get; set; } = "Technical";
    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }
    public string Mode { get; set; } = "Virtual";
    public string LocationOrLink { get; set; } = "";
    public string Status { get; set; } = "Scheduled";
    public string Result { get; set; } = "Pending";
    public string OverallFeedback { get; set; } = "";
    public decimal OverallScore { get; set; }
    public List<int> PanelUserIds { get; set; } = [];
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
}

public class RecruitmentInterviewSchedulingContext
{
    public long ApplicationId { get; set; }
    public bool IsPipelineManaged { get; set; }
    public long? PipelineStageInstanceId { get; set; }
    public long? RoundConfigurationId { get; set; }
    public string PipelineStageName { get; set; } = "";
    public string RoundCode { get; set; } = "Round 1";
    public string InterviewType { get; set; } = "Technical";
    public int DefaultDurationMinutes { get; set; } = 60;
    public int MinimumPanelCount { get; set; } = 1;
    public decimal MinimumPassingScore { get; set; } = 60;
    public string ScoreInputMode { get; set; } = "PercentageWeighted";
    public string PanelAggregationMethod { get; set; } = "Average";
    public bool FeedbackRequired { get; set; }
    public bool CalendarEnabled { get; set; } = true;
    public bool AllowReschedule { get; set; } = true;
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
    public int NextAttemptNumber { get; set; } = 1;
    public List<RecruitmentInterviewStageCompetency> Competencies { get; set; } = [];
    public List<int> DefaultPanelUserIds { get; set; } = [];
}

public class RecruitmentInterviewFeedback
{
    public long Id { get; set; }
    public long InterviewId { get; set; }
    public int PanelUserId { get; set; }
    public string PanelUserName { get; set; } = "";
    public decimal OverallScore { get; set; }
    public string Recommendation { get; set; } = "";
    public string CompetencyScoresJson { get; set; } = "{}";
    public decimal WeightedScore { get; set; }
    public string ScoreSource { get; set; } = "LegacyOverall";
    public string Comments { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public List<RecruitmentInterviewFeedbackCompetencyScore> CompetencyScores { get; set; } = [];
}

public class SaveRecruitmentInterviewFeedback
{
    public int PanelUserId { get; set; }
    public decimal OverallScore { get; set; }
    public string Recommendation { get; set; } = "";
    public string CompetencyScoresJson { get; set; } = "{}";
    public string Comments { get; set; } = "";
    public List<SaveRecruitmentInterviewFeedbackCompetencyScore> CompetencyScores { get; set; } = [];
}

public class RecruitmentInterviewFeedbackCompetencyScore
{
    public long Id { get; set; }
    public long InterviewFeedbackId { get; set; }
    public long InterviewStageCompetencyId { get; set; }
    public long CompetencyId { get; set; }
    public string CompetencyCode { get; set; } = "";
    public string CompetencyName { get; set; } = "";
    public decimal WeightPercent { get; set; }
    public decimal MinimumScore { get; set; }
    public decimal Score { get; set; }
    public decimal WeightedScore { get; set; }
    public string Comments { get; set; } = "";
    public bool MeetsMinimum { get; set; }
}

public class SaveRecruitmentInterviewFeedbackCompetencyScore
{
    public long InterviewStageCompetencyId { get; set; }
    public decimal Score { get; set; }
    public string Comments { get; set; } = "";
}

public class RecruitmentOffer
{
    public long Id { get; set; }
    public string OfferNumber { get; set; } = "";
    public long ApplicationId { get; set; }
    public string CandidateName { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public int ClientId { get; set; }
    public decimal OfferedCtc { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTime ProposedJoiningDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string Status { get; set; } = "Draft";
    public long? WorkflowInstanceId { get; set; }
    public long? PipelineStageInstanceId { get; set; }
    public long? StageOfferConfigurationId { get; set; }
    public long? OfferTemplateId { get; set; }
    public string OfferTemplateName { get; set; } = "";
    public string BudgetBasis { get; set; } = "";
    public decimal ApprovedBudgetAmount { get; set; }
    public decimal BudgetExposureAmount { get; set; }
    public decimal MaximumVariancePercent { get; set; }
    public decimal VariancePercent { get; set; }
    public bool VarianceExceeded { get; set; }
    public long? AppliedApprovalWorkflowId { get; set; }
    public string ApprovalPolicy { get; set; } = "";
    public int CandidateResponseValidityDays { get; set; }
    public Guid? OfferLetterAttachmentPublicId { get; set; }
    public string Remarks { get; set; } = "";
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SaveRecruitmentOffer
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public decimal OfferedCtc { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTime ProposedJoiningDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string Status { get; set; } = "Draft";
    public Guid? OfferLetterAttachmentPublicId { get; set; }
    public string Remarks { get; set; } = "";
}

public class RecruitmentCandidateChecklistItem
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public int ChecklistConfigurationId { get; set; }
    public string ChecklistName { get; set; } = "";
    public string Stage { get; set; } = "Pre-Onboarding";
    public bool Mandatory { get; set; }
    public long? AttachmentAttributeId { get; set; }
    public bool RequiresVerification { get; set; }
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid? AttachmentPublicId { get; set; }
    public int? CompletedByUserId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ConvertCandidateToEmployeeRequest
{
    public string EmployeeCode { get; set; } = "";
    public string DateOfJoining { get; set; } = "";
    public string WorkEmail { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Department { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Grade { get; set; } = "";
    public int WorkLocationId { get; set; }
    public int ReportingManagerId { get; set; }
    public int? ReportingManagerUserId { get; set; }
    public bool PortalAccess { get; set; } = true;
    public string SalaryStructureId { get; set; } = "";
    public decimal AnnualCtc { get; set; }
}

public class PersonActivityEvent
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long? CandidateId { get; set; }
    public int? EmployeeId { get; set; }
    public string ModuleCode { get; set; } = "";
    public string EventType { get; set; } = "";
    public string EventTitle { get; set; } = "";
    public string EventSummary { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public string Visibility { get; set; } = "HR";
    public bool IsSensitive { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RecruitmentCandidateDetail
{
    public RecruitmentCandidate? Candidate { get; set; }
    public IEnumerable<RecruitmentCandidateResume> Resumes { get; set; } = [];
    public IEnumerable<RecruitmentCandidateApplication> Applications { get; set; } = [];
    public IEnumerable<RecruitmentCandidateSkill> Skills { get; set; } = [];
    public IEnumerable<RecruitmentCandidateExperience> Experience { get; set; } = [];
    public IEnumerable<RecruitmentCandidateEducation> Education { get; set; } = [];
    public IEnumerable<RecruitmentCandidateCertification> Certifications { get; set; } = [];
    public IEnumerable<RecruitmentApplicationScore> Scores { get; set; } = [];
    public IEnumerable<RecruitmentInterview> Interviews { get; set; } = [];
    public IEnumerable<RecruitmentOffer> Offers { get; set; } = [];
    public IEnumerable<RecruitmentCandidateChecklistItem> Checklist { get; set; } = [];
    public IEnumerable<PersonActivityEvent> Activity { get; set; } = [];
}

public class CandidateResumeUploadRequest
{
    public long FieldConfigurationId { get; set; }
    public string DocumentNumber { get; set; } = "";
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public IFormFile? File { get; set; }
}

public class RecruitmentResumeIntakeRequest
{
    public int ClientId { get; set; }
    public long PositionId { get; set; }
    public long? JobPostingId { get; set; }
    public long? FieldConfigurationId { get; set; }
    public string SourceType { get; set; } = "Direct Sourcing";
    public List<IFormFile> Files { get; set; } = [];
}

public class RecruitmentResumeIntakeItem
{
    public string FileName { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public string ParsingStatus { get; set; } = "";
    public string DetectedName { get; set; } = "";
    public string DetectedEmail { get; set; } = "";
    public string DetectedPhone { get; set; } = "";
    public string DetectedAddress { get; set; } = "";
    public RecruitmentCandidate? Candidate { get; set; }
    public RecruitmentCandidateResume? Resume { get; set; }
    public RecruitmentCandidateApplication? Application { get; set; }
}

public class RecruitmentResumeIntakeResult
{
    public int TotalFiles { get; set; }
    public int Imported { get; set; }
    public int NeedsReview { get; set; }
    public List<RecruitmentResumeIntakeItem> Items { get; set; } = [];
}

public class RecruitmentTalentDashboard
{
    public int TalentProfiles { get; set; }
    public int ActiveApplications { get; set; }
    public int InterviewsScheduled { get; set; }
    public int OffersPending { get; set; }
    public int PreOnboardingPending { get; set; }
    public int Joined { get; set; }
}
