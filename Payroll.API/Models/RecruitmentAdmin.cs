namespace Payroll.API.Models;

public class RecruitmentAdminSetup
{
    public IEnumerable<RecruitmentSetting> Settings { get; set; } = [];
    public IEnumerable<RecruitmentMasterValue> Masters { get; set; } = [];
    public IEnumerable<RecruitmentPartner> Consultants { get; set; } = [];
    public IEnumerable<RecruitmentPartner> Vendors { get; set; } = [];
    public IEnumerable<RecruitmentAssignmentRule> AssignmentRules { get; set; } = [];
    public IEnumerable<RecruitmentSlaRule> SlaRules { get; set; } = [];
    public IEnumerable<RecruitmentDocumentChecklist> DocumentChecklist { get; set; } = [];
    public IEnumerable<RecruitmentApprovalMapping> ApprovalMappings { get; set; } = [];
    public IEnumerable<RecruitmentTemplate> Templates { get; set; } = [];
}

public class RecruitmentSetting
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public bool RecruitmentEnabled { get; set; }
    public bool AllowEmployeeRfrCreation { get; set; }
    public bool AllowReplacementHiring { get; set; }
    public bool AllowMultipleHiringManagers { get; set; }
    public bool AllowMultipleRecruiters { get; set; }
    public bool AutoGeneratePositionCode { get; set; } = true;
    public bool AutoGenerateRfrNumber { get; set; } = true;
    public bool EnableVendorHiring { get; set; }
    public bool EnableConsultantHiring { get; set; }
    public bool EnableInternalHiring { get; set; } = true;
    public bool EnableReferralHiring { get; set; } = true;
    public bool EnableCampusHiring { get; set; }
    public bool EnableWalkInHiring { get; set; }
    public bool EnableOfferApproval { get; set; } = true;
    public bool EnablePreOfferProcess { get; set; }
    public bool EnableBackgroundVerification { get; set; }
    public bool EnableDocumentVerification { get; set; }
    public bool EnableCandidatePortal { get; set; }
    public bool EnableVendorPortal { get; set; }
    public bool EnableJobPortalIntegration { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RecruitmentMasterValue
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string MasterType { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int SortOrder { get; set; } = 100;
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RecruitmentPartner
{
    public int Id { get; set; }
    public string PartnerType { get; set; } = "Consultant";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string ContactPerson { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string Gstin { get; set; } = "";
    public string Pan { get; set; } = "";
    public DateTime? AgreementStartDate { get; set; }
    public DateTime? AgreementEndDate { get; set; }
    public string CommissionType { get; set; } = "Percentage";
    public decimal CommissionValue { get; set; }
    public string Status { get; set; } = "Active";
    public decimal PerformanceRating { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RecruitmentAssignmentRule
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string BusinessUnit { get; set; } = "";
    public string Department { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string SkillCategory { get; set; } = "";
    public string Project { get; set; } = "";
    public string Location { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string JobLevel { get; set; } = "";
    public string RecruitmentSource { get; set; } = "";
    public string Priority { get; set; } = "";
    public int RecruiterUserId { get; set; }
    public string RecruiterName { get; set; } = "";
    public int MaximumOpenPositions { get; set; }
    public bool WorkloadBased { get; set; }
    public bool ManualOverrideAllowed { get; set; } = true;
    public int SortOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}

public class RecruitmentSlaRule
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int DurationDays { get; set; }
    public bool ReminderEnabled { get; set; } = true;
    public int ReminderBeforeDays { get; set; } = 1;
    public bool EscalationEnabled { get; set; }
    public int EscalationAfterDays { get; set; }
    public int NotificationRuleId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RecruitmentDocumentChecklist
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string HiringType { get; set; } = "";
    public string DocumentName { get; set; } = "";
    public bool Mandatory { get; set; } = true;
    public string Stage { get; set; } = "Pre-Onboarding";
    public bool IsActive { get; set; } = true;
}

public class RecruitmentApprovalMapping
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string ProcessCode { get; set; } = "";
    public long WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class RecruitmentTemplate
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string TemplateType { get; set; } = "";
    public string TemplateCode { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string SubjectTemplate { get; set; } = "";
    public string BodyTemplate { get; set; } = "";
    public bool IsHtml { get; set; } = true;
    public bool IsActive { get; set; } = true;
}
