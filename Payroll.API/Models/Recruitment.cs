namespace Payroll.API.Models;

public class RecruitmentRequisition
{
    public long Id { get; set; }
    public string RfrNumber { get; set; } = "";
    public DateTime RequestDate { get; set; } = DateTime.Today;
    public int RequestedByEmployeeId { get; set; }
    public int RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public int BranchId { get; set; }
    public string BranchName { get; set; } = "";
    public string BusinessUnit { get; set; } = "";
    public string Department { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string HiringType { get; set; } = "";
    public int NumberOfOpenings { get; set; } = 1;
    public bool IsReplacement { get; set; }
    public int? ReplacementEmployeeId { get; set; }
    public string ReplacementEmployeeName { get; set; } = "";
    public DateTime? TargetJoiningDate { get; set; }
    public string JobLocation { get; set; } = "";
    public string WorkMode { get; set; } = "Office";
    public string Project { get; set; } = "";
    public bool BudgetAvailable { get; set; }
    public decimal BudgetAmount { get; set; }
    public string HiringPriority { get; set; } = "Normal";
    public string BusinessJustification { get; set; } = "";
    public string ReasonForHiring { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string Qualification { get; set; } = "";
    public string RequiredSkills { get; set; } = "";
    public string PreferredSkills { get; set; } = "";
    public string Certifications { get; set; } = "";
    public string Languages { get; set; } = "";
    public decimal SalaryMin { get; set; }
    public decimal SalaryMax { get; set; }
    public string Currency { get; set; } = "INR";
    public string Benefits { get; set; } = "";
    public string ExternalPositionCode { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceReference { get; set; } = "";
    public string SourceDocumentName { get; set; } = "";
    public DateTime? SourceDocumentDate { get; set; }
    public string SourceAuthority { get; set; } = "";
    public string ExternalApprovalStatus { get; set; } = "";
    public decimal? CtcFlexibilityPercent { get; set; }
    public string SourceNotes { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public long? WorkflowInstanceId { get; set; }
    public long? OpenPositionId { get; set; }
    public long? WorkOrderId { get; set; }
    public int? WorkOrderLineNumber { get; set; }
    public long? LatestJobDescriptionVersionId { get; set; }
    public int? LatestJobDescriptionVersionNumber { get; set; }
    public string JobDescriptionStatus { get; set; } = "Not Started";
    public DateTime? SubmittedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SaveRecruitmentRequisition
{
    public long Id { get; set; }
    public DateTime? RequestDate { get; set; }
    public int? RequestedByEmployeeId { get; set; }
    public int BranchId { get; set; }
    public string BusinessUnit { get; set; } = "";
    public string Department { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string HiringType { get; set; } = "";
    public int NumberOfOpenings { get; set; } = 1;
    public bool IsReplacement { get; set; }
    public int? ReplacementEmployeeId { get; set; }
    public DateTime? TargetJoiningDate { get; set; }
    public string JobLocation { get; set; } = "";
    public string WorkMode { get; set; } = "Office";
    public int? ClientId { get; set; }
    public long? WorkOrderId { get; set; }
    public int? WorkOrderLineNumber { get; set; }
    public string Project { get; set; } = "";
    public bool BudgetAvailable { get; set; }
    public decimal BudgetAmount { get; set; }
    public string HiringPriority { get; set; } = "Normal";
    public string BusinessJustification { get; set; } = "";
    public string ReasonForHiring { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string Qualification { get; set; } = "";
    public string RequiredSkills { get; set; } = "";
    public string PreferredSkills { get; set; } = "";
    public string Certifications { get; set; } = "";
    public string Languages { get; set; } = "";
    public decimal SalaryMin { get; set; }
    public decimal SalaryMax { get; set; }
    public string Currency { get; set; } = "INR";
    public string Benefits { get; set; } = "";
    public string ExternalPositionCode { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceReference { get; set; } = "";
    public string SourceDocumentName { get; set; } = "";
    public DateTime? SourceDocumentDate { get; set; }
    public string SourceAuthority { get; set; } = "";
    public string ExternalApprovalStatus { get; set; } = "";
    public decimal? CtcFlexibilityPercent { get; set; }
    public string SourceNotes { get; set; } = "";
}

public class RecruitmentOptions
{
    public bool ModuleEnabled { get; set; }
    public bool Enabled { get; set; }
    public bool AllowReplacementHiring { get; set; }
    public bool EnableInternalHiring { get; set; }
    public bool EnableReferralHiring { get; set; }
    public string DisplayName { get; set; } = "Recruitment Requisition";
    public string ClientName { get; set; } = "";
    public List<string> PositionCategories { get; set; } = [];
    public List<string> HiringTypes { get; set; } = [];
    public List<string> EmploymentTypes { get; set; } = [];
    public List<string> Departments { get; set; } = [];
    public List<string> Designations { get; set; } = [];
    public List<string> Grades { get; set; } = [];
    public List<string> WorkLocations { get; set; } = [];
    public List<string> BusinessUnits { get; set; } = [];
    public List<string> CostCenters { get; set; } = [];
    public List<string> ExperienceRanges { get; set; } = [];
    public List<string> BudgetAmounts { get; set; } = [];
    public List<string> Priorities { get; set; } = ["Low", "Normal", "High", "Critical"];
    public List<EmployeeLookup> Employees { get; set; } = [];
    public List<string> ValidationMessages { get; set; } = [];
}

public class EmployeeLookup
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Department { get; set; } = "";
    public string Designation { get; set; } = "";
}

public class RecruitmentDashboard
{
    public int Drafts { get; set; }
    public int PendingApproval { get; set; }
    public int Approved { get; set; }
    public int Rejected { get; set; }
    public int Returned { get; set; }
    public int Withdrawn { get; set; }
    public int OpenPositions { get; set; }
    public int FilledPositions { get; set; }
    public int CancelledPositions { get; set; }
    public int OnHoldPositions { get; set; }
    public int RemainingPositions { get; set; }
    public decimal AverageApprovalHours { get; set; }
    public IEnumerable<RecruitmentMetric> DepartmentWiseHiring { get; set; } = [];
    public IEnumerable<RecruitmentMetric> CompanyWiseHiring { get; set; } = [];
    public IEnumerable<RecruitmentMetric> PriorityWiseHiring { get; set; } = [];
    public IEnumerable<RecruitmentMetric> UpcomingJoiningTargets { get; set; } = [];
}

public class RecruitmentMetric
{
    public string Label { get; set; } = "";
    public decimal Value { get; set; }
}

public class RecruitmentSearchRequest
{
    public int? ClientId { get; set; }
    public string Status { get; set; } = "";
    public string Query { get; set; } = "";
    public string Department { get; set; } = "";
    public string HiringType { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string Priority { get; set; } = "";
    public string BusinessUnit { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string Experience { get; set; } = "";
    public string Location { get; set; } = "";
    public string Project { get; set; } = "";
    public bool? ReplacementHiring { get; set; }
    public decimal? BudgetMin { get; set; }
    public decimal? BudgetMax { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? RecruiterUserId { get; set; }
}

public class RecruitmentOpenPosition
{
    public long Id { get; set; }
    public long RequisitionId { get; set; }
    public string RfrNumber { get; set; } = "";
    public string PositionCode { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public int BranchId { get; set; }
    public string BranchName { get; set; } = "";
    public string Department { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string HiringType { get; set; } = "";
    public int NumberOfPositions { get; set; }
    public int ApprovedPositions { get; set; }
    public int FilledPositions { get; set; }
    public int CancelledPositions { get; set; }
    public int OnHoldPositions { get; set; }
    public int RemainingPositions { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime? TargetJoiningDate { get; set; }
    public string JobLocation { get; set; } = "";
    public string BusinessUnit { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string Project { get; set; } = "";
    public bool BudgetAvailable { get; set; }
    public decimal BudgetAmount { get; set; }
    public decimal SalaryMin { get; set; }
    public decimal SalaryMax { get; set; }
    public string Currency { get; set; } = "INR";
    public string HiringPriority { get; set; } = "";
    public string RequiredSkills { get; set; } = "";
    public string PreferredSkills { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public int RecruiterUserId { get; set; }
    public string RecruiterName { get; set; } = "";
    public int CandidateCount { get; set; }
    public int InterviewCount { get; set; }
    public int OfferCount { get; set; }
    public int JoinedCount { get; set; }
    public long? LatestJobDescriptionVersionId { get; set; }
    public int? LatestJobDescriptionVersionNumber { get; set; }
    public string JobDescriptionStatus { get; set; } = "Not Started";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class RecruitmentPositionTimeline
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public string EventType { get; set; } = "";
    public string EventTitle { get; set; } = "";
    public string EventDetails { get; set; } = "";
    public int? ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class RecruitmentPositionNote
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public string NoteType { get; set; } = "General";
    public string NoteText { get; set; } = "";
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class RecruitmentPositionChecklistItem
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public string ChecklistName { get; set; } = "";
    public string Stage { get; set; } = "";
    public bool Mandatory { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RecruitmentPositionDetail
{
    public RecruitmentOpenPosition? Position { get; set; }
    public bool AllowMultipleRecruiters { get; set; }
    public bool EnableVendorHiring { get; set; }
    public bool EnableConsultantHiring { get; set; }
    public bool EnableInternalHiring { get; set; }
    public bool EnableReferralHiring { get; set; }
    public bool EnableDocumentVerification { get; set; }
    public IEnumerable<RecruitmentPositionTimeline> Timeline { get; set; } = [];
    public IEnumerable<RecruitmentPositionNote> Notes { get; set; } = [];
    public IEnumerable<RecruitmentPositionChecklistItem> Checklist { get; set; } = [];
    public IEnumerable<RecruitmentRecruiterAssignment> RecruiterAssignments { get; set; } = [];
    public IEnumerable<RecruitmentPartnerAssignment> VendorAssignments { get; set; } = [];
    public IEnumerable<RecruitmentPartnerAssignment> ConsultantAssignments { get; set; } = [];
    public IEnumerable<RecruitmentJobPublication> Publications { get; set; } = [];
    public IEnumerable<RecruitmentReferralCampaign> ReferralCampaigns { get; set; } = [];
}

public class SaveRecruitmentPositionNote
{
    public string NoteType { get; set; } = "General";
    public string NoteText { get; set; } = "";
}

public class UpdateRecruitmentPositionStatus
{
    public string Status { get; set; } = "";
    public string Comment { get; set; } = "";
}

public class RecruitmentOperationsOptions
{
    public bool AllowMultipleRecruiters { get; set; }
    public bool EnableVendorHiring { get; set; }
    public bool EnableConsultantHiring { get; set; }
    public bool EnableInternalHiring { get; set; }
    public bool EnableReferralHiring { get; set; }
    public bool EnableDocumentVerification { get; set; }
    public IEnumerable<AuthUser> Recruiters { get; set; } = [];
    public IEnumerable<RecruitmentPartner> Vendors { get; set; } = [];
    public IEnumerable<RecruitmentPartner> Consultants { get; set; } = [];
    public IEnumerable<string> PositionStatuses { get; set; } = [];
    public IEnumerable<string> PublishingChannels { get; set; } = [];
    public IEnumerable<string> AssignmentPriorities { get; set; } = [];
}

public class RecruitmentRecruiterAssignment
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public int PrimaryRecruiterUserId { get; set; }
    public string PrimaryRecruiterName { get; set; } = "";
    public int SecondaryRecruiterUserId { get; set; }
    public string SecondaryRecruiterName { get; set; } = "";
    public DateTime AssignmentDate { get; set; }
    public string AssignmentReason { get; set; } = "";
    public string AssignmentStatus { get; set; } = "Active";
    public int AssignedByUserId { get; set; }
    public string AssignedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class SaveRecruiterAssignment
{
    public int PrimaryRecruiterUserId { get; set; }
    public int SecondaryRecruiterUserId { get; set; }
    public string AssignmentReason { get; set; } = "";
}

public class RecruitmentPartnerAssignment
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public string PartnerType { get; set; } = "";
    public int PartnerId { get; set; }
    public string PartnerName { get; set; } = "";
    public DateTime AssignmentDate { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateTime? DueDate { get; set; }
    public int ExpectedProfiles { get; set; }
    public string Status { get; set; } = "Assigned";
    public string Remarks { get; set; } = "";
    public int AssignedByUserId { get; set; }
    public string AssignedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class SavePartnerAssignment
{
    public int PartnerId { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateTime? DueDate { get; set; }
    public int ExpectedProfiles { get; set; }
    public string Remarks { get; set; } = "";
}

public class RecruitmentJobPublication
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public string Channel { get; set; } = "";
    public DateTime PublishingDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string Status { get; set; } = "Published";
    public string Remarks { get; set; } = "";
    public int PublishedByUserId { get; set; }
    public string PublishedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class SaveJobPublication
{
    public string Channel { get; set; } = "";
    public DateTime? PublishingDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string Status { get; set; } = "Published";
    public string Remarks { get; set; } = "";
}

public class RecruitmentReferralCampaign
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public string CampaignName { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal ReferralReward { get; set; }
    public string VisibilityCompany { get; set; } = "";
    public string VisibilityDepartment { get; set; } = "";
    public string VisibilityBusinessUnit { get; set; } = "";
    public string VisibilityLocation { get; set; } = "";
    public string VisibilityEmploymentType { get; set; } = "";
    public string Status { get; set; } = "Open";
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class SaveReferralCampaign
{
    public string CampaignName { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal ReferralReward { get; set; }
    public string VisibilityDepartment { get; set; } = "";
    public string VisibilityBusinessUnit { get; set; } = "";
    public string VisibilityLocation { get; set; } = "";
    public string VisibilityEmploymentType { get; set; } = "";
    public string Status { get; set; } = "Open";
}

public class RecruitmentInternalOpening
{
    public long PositionId { get; set; }
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string Department { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string HiringType { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string RequiredSkills { get; set; } = "";
    public DateTime? TargetJoiningDate { get; set; }
    public string CampaignName { get; set; } = "";
    public decimal ReferralReward { get; set; }
    public DateTime EndDate { get; set; }
}

public class RecruitmentEmployeeReferral
{
    public long Id { get; set; }
    public long? CandidateId { get; set; }
    public long? ApplicationId { get; set; }
    public long PositionId { get; set; }
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public int ReferrerEmployeeId { get; set; }
    public string ReferrerName { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public string CandidateEmail { get; set; } = "";
    public string CandidatePhone { get; set; } = "";
    public string Relationship { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string Status { get; set; } = "Submitted";
    public DateTime CreatedAt { get; set; }
}

public class SaveEmployeeReferral
{
    public long PositionId { get; set; }
    public string CandidateName { get; set; } = "";
    public string CandidateEmail { get; set; } = "";
    public string CandidatePhone { get; set; } = "";
    public string Relationship { get; set; } = "";
    public string Remarks { get; set; } = "";
}
