namespace Payroll.API.Models;

public class EssLeaveBalance
{
    public string LeaveCode { get; set; } = string.Empty;
    public string LeaveType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public DateTime BalanceDate { get; set; }
}

public class EssProfile
{
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string DateOfJoining { get; set; } = string.Empty;
    public string WorkLocation { get; set; } = string.Empty;
    public string ReportingManager { get; set; } = string.Empty;
}

public class CreateEssLeaveRequest { public string LeaveCode { get; set; } = ""; public string FromDate { get; set; } = ""; public string ToDate { get; set; } = ""; public string Reason { get; set; } = ""; }
public class EssLeaveRequest { public long Id { get; set; } public string LeaveCode { get; set; } = ""; public string LeaveType { get; set; } = ""; public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public decimal Days { get; set; } public string Reason { get; set; } = ""; public string Status { get; set; } = ""; public DateTime CreatedAt { get; set; } }
public class EssWorkflowTrail { public long? InstanceId { get; set; } public string WorkflowCode { get; set; } = ""; public string WorkflowName { get; set; } = ""; public string ResourceType { get; set; } = ""; public string MatchScope { get; set; } = ""; public string Status { get; set; } = ""; public DateTime? CreatedAt { get; set; } public DateTime? CompletedAt { get; set; } public List<EssWorkflowTrailItem> Events { get; set; } = []; }
public class EssWorkflowTrailItem { public string StageName { get; set; } = ""; public string Action { get; set; } = ""; public string Actor { get; set; } = ""; public string Comment { get; set; } = ""; public DateTime CreatedAt { get; set; } public bool IsPending { get; set; } }
public class EssPayslip { public int PayRunId { get; set; } public string PayPeriod { get; set; } = ""; public DateTime PayDate { get; set; } public string RunStatus { get; set; } = ""; public decimal GrossPay { get; set; } public decimal StatutoryDeductions { get; set; } public decimal OneTimeDeductions { get; set; } public decimal NetPay { get; set; } public string PaymentStatus { get; set; } = ""; public DateTime? PaymentDate { get; set; } }
public class EssAttendanceSummary { public string Month { get; set; } = ""; public decimal PresentDays { get; set; } public decimal PayableDays { get; set; } public int TotalWorkingDays { get; set; } }
public class EssDailyAttendance { public DateTime AttendanceDate { get; set; } public string Status { get; set; } = ""; public decimal PayableValue { get; set; } public string Remarks { get; set; } = ""; }
public class EssHoliday { public string Name { get; set; } = ""; public DateTime StartDate { get; set; } public DateTime EndDate { get; set; } }
public class EssBirthday { public string Name { get; set; } = ""; public string Department { get; set; } = ""; }
public class EssTaxPortal
{
    public string FinancialYear { get; set; } = "";
    public bool Enabled { get; set; }
    public string DefaultRegime { get; set; } = "New";
    public string? SelectedRegime { get; set; }
    public string RegimeStatus { get; set; } = "";
    public bool CanSelectRegime { get; set; }
    public bool CanDeclare { get; set; }
    public bool CanSubmitPlanned { get; set; }
    public bool CanSubmitActual { get; set; }
    public bool RegimeSelectionWindowOpen { get; set; }
    public bool PlannedDeclarationWindowOpen { get; set; }
    public bool ActualDeclarationWindowOpen { get; set; }
    public bool DeclarationRequired { get; set; }
    public string DeclarationPhase { get; set; } = "Closed";
    public bool RequiresApproval { get; set; }
    public DateTime? RegimeSelectionCutoff { get; set; }
    public DateTime? DeclarationWindowStart { get; set; }
    public DateTime? DeclarationWindowEnd { get; set; }
    public DateTime? PlannedDeclarationStart { get; set; }
    public DateTime? PlannedDeclarationEnd { get; set; }
    public DateTime? ActualDeclarationStart { get; set; }
    public DateTime? ActualDeclarationEnd { get; set; }
    public string PoiProcessingMonth { get; set; } = "";
    public string Message { get; set; } = "";
    public List<EssTaxDeclarationSection> Sections { get; set; } = [];
    public List<EssTaxFinalAdjustmentInfo> FinalAdjustments { get; set; } = [];
}
public class EssTaxDeclarationSection
{
    public long? DeclarationId { get; set; }
    public int SectionId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Regime { get; set; } = "";
    public decimal? LimitAmount { get; set; }
    public bool ProofRequired { get; set; }
    public bool RequiresApproval { get; set; }
    public decimal DeclaredAmount { get; set; }
    public decimal PlannedAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public string Status { get; set; } = "Draft";
    public string Remarks { get; set; } = "";
}
public class EssTaxFinalAdjustmentInfo { public string Label { get; set; } = ""; public string ValueType { get; set; } = ""; public decimal Value { get; set; } }
public class SaveEssTaxRegimeRequest { public string Regime { get; set; } = ""; }
public class SaveEssTaxDeclarationsRequest { public string Phase { get; set; } = "Planned"; public List<SaveEssTaxDeclarationLine> Lines { get; set; } = []; }
public class SaveEssTaxDeclarationLine { public int SectionId { get; set; } public decimal Amount { get; set; } public decimal DeclaredAmount { get; set; } public string Remarks { get; set; } = ""; }
