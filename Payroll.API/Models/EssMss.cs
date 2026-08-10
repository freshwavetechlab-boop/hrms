namespace Payroll.API.Models;

public class EssLeaveBalance
{
    public string LeaveCode { get; set; } = string.Empty;
    public string LeaveType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public DateTime BalanceDate { get; set; }
    public bool AllowHalfDay { get; set; } = true;
}

public class EssProfile
{
    public int ClientId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string DateOfBirth { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string PanNumber { get; set; } = string.Empty;
    public string AadhaarNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string CorrespondenceAddress { get; set; } = string.Empty;
    public string PermanentAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string BankAccountNo { get; set; } = string.Empty;
    public string IfscCode { get; set; } = string.Empty;
    public string PaymentMode { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string DateOfJoining { get; set; } = string.Empty;
    public string WorkLocation { get; set; } = string.Empty;
    public string AttendanceOffice { get; set; } = string.Empty;
    public string ReportingManager { get; set; } = string.Empty;
    public bool CanEdit { get; set; }
    public bool TravelExpenseEnabled { get; set; }
}

public class EssFeatureAccess
{
    public bool TravelExpenseEnabled { get; set; }
}

public class SaveEssProfileRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string DateOfBirth { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string PanNumber { get; set; } = string.Empty;
    public string AadhaarNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string CorrespondenceAddress { get; set; } = string.Empty;
    public string PermanentAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string BankAccountNo { get; set; } = string.Empty;
    public string IfscCode { get; set; } = string.Empty;
    public string PaymentMode { get; set; } = string.Empty;
}

public class EssClientSetting
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public bool AllowProfileEdit { get; set; }
    public string InitialPasswordMode { get; set; } = "App Default";
    public string FixedPassword { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class CreateEssLeaveRequest { public string LeaveCode { get; set; } = ""; public string FromDate { get; set; } = ""; public string ToDate { get; set; } = ""; public string DayType { get; set; } = "Full Day"; public string Reason { get; set; } = ""; }
public class EssLeaveRequest { public long Id { get; set; } public string LeaveCode { get; set; } = ""; public string LeaveType { get; set; } = ""; public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public string DayType { get; set; } = "Full Day"; public decimal Days { get; set; } public string Reason { get; set; } = ""; public string Status { get; set; } = ""; public DateTime CreatedAt { get; set; } }
public class EssTravelRequest
{
    public long Id { get; set; }
    public string RequestNumber { get; set; } = "";
    public DateTime RequestDate { get; set; }
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string Department { get; set; } = "";
    public string Designation { get; set; } = "";
    public string ReportingManager { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Project { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string TravelScope { get; set; } = "Domestic";
    public string TravelType { get; set; } = "Official";
    public string Priority { get; set; } = "Normal";
    public string FromLocation { get; set; } = "";
    public string ToLocation { get; set; } = "";
    public List<EssTravelCity> Legs { get; set; } = [];
    public List<EssTravelAccommodation> AccommodationDetails { get; set; } = [];
    public List<EssLocalTravelDetail> LocalTravelDetails { get; set; } = [];
    public DateTime StartDateTime { get; set; }
    public DateTime EndDateTime { get; set; }
    public decimal EstimatedCost { get; set; }
    public long? PolicyId { get; set; }
    public string PolicyName { get; set; } = "";
    public string TravelMode { get; set; } = "";
    public bool AccommodationRequired { get; set; }
    public bool LocalConveyanceRequired { get; set; }
    public bool AdvanceRequired { get; set; }
    public decimal AdvanceAmount { get; set; }
    public string Remarks { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public string PolicyValidationJson { get; set; } = "[]";
    public string CancellationReason { get; set; } = "";
    public DateTime? CancellationDate { get; set; }
    public string CancellationStatus { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
public class SaveEssTravelRequest
{
    public long Id { get; set; }
    public string Purpose { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Project { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string TravelScope { get; set; } = "Domestic";
    public string TravelType { get; set; } = "Official";
    public string Priority { get; set; } = "Normal";
    public string FromLocation { get; set; } = "";
    public string ToLocation { get; set; } = "";
    public List<EssTravelCity> Cities { get; set; } = [];
    public List<EssTravelAccommodation> AccommodationDetails { get; set; } = [];
    public List<EssLocalTravelDetail> LocalTravelDetails { get; set; } = [];
    public DateTime StartDateTime { get; set; }
    public DateTime EndDateTime { get; set; }
    public decimal EstimatedCost { get; set; }
    public string TravelMode { get; set; } = "";
    public bool AccommodationRequired { get; set; }
    public bool LocalConveyanceRequired { get; set; }
    public bool AdvanceRequired { get; set; }
    public decimal AdvanceAmount { get; set; }
    public string Remarks { get; set; } = "";
}
public class EssTravelCity { public string FromLocation { get; set; } = ""; public string ToLocation { get; set; } = ""; public string TravelMode { get; set; } = ""; public string TravelClass { get; set; } = ""; public string BookingAction { get; set; } = "Book by myself"; public string Remarks { get; set; } = ""; public DateTime? StartDateTime { get; set; } public DateTime? EndDateTime { get; set; } }
public class EssTravelAccommodation { public string City { get; set; } = ""; public DateTime? CheckInDateTime { get; set; } public DateTime? CheckOutDateTime { get; set; } public string Occupancy { get; set; } = ""; public string RoomPreference { get; set; } = ""; public string BookingAction { get; set; } = "Book by myself"; public string Remarks { get; set; } = ""; }
public class EssLocalTravelDetail { public string City { get; set; } = ""; public DateTime? TravelDateTime { get; set; } public string FromLocation { get; set; } = ""; public string ToLocation { get; set; } = ""; public string TravelMode { get; set; } = ""; public string BookingAction { get; set; } = "Book by myself"; public string Remarks { get; set; } = ""; }
public class EssTravelOptions { public long? PolicyId { get; set; } public string PolicyName { get; set; } = ""; public string ClientName { get; set; } = ""; public List<string> TravelModes { get; set; } = []; public List<string> LocalTravelModes { get; set; } = []; public List<string> TravelTypes { get; set; } = []; public List<string> Locations { get; set; } = []; public List<string> TravelClasses { get; set; } = []; public bool TravelDeskEnabled { get; set; } public bool ShowTripDetails { get; set; } public bool ShowAccommodationDetails { get; set; } public bool ShowLocalTravelDetails { get; set; } public List<string> ValidationMessages { get; set; } = []; }
public class EssTravelDashboard { public int DraftRequests { get; set; } public int PendingApproval { get; set; } public int Approved { get; set; } public int Rejected { get; set; } public int UpcomingTravel { get; set; } public int CancelledTrips { get; set; } }
public class EssTravelValidationResult { public string Severity { get; set; } = ""; public string Message { get; set; } = ""; public string RuleName { get; set; } = ""; public string Behavior { get; set; } = ""; }
public class EssExpenseClaim
{
    public long Id { get; set; }
    public string ClaimNumber { get; set; } = "";
    public DateTime ClaimDate { get; set; }
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string Department { get; set; } = "";
    public string Designation { get; set; } = "";
    public long? TravelRequestId { get; set; }
    public string TravelRequestNumber { get; set; } = "";
    public string ExpenseType { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Project { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string Currency { get; set; } = "INR";
    public decimal TotalClaimAmount { get; set; }
    public decimal TotalApprovedAmount { get; set; }
    public decimal TotalGstAmount { get; set; }
    public string PayrollStatus { get; set; } = "Not Ready";
    public int? PayrollRunId { get; set; }
    public string ReimbursementComponentCode { get; set; } = "REIMBURSEMENT";
    public string Status { get; set; } = "Draft";
    public string PolicyValidationJson { get; set; } = "[]";
    public string Remarks { get; set; } = "";
    public DateTime? SubmittedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<EssExpenseClaimLine> Lines { get; set; } = [];
}
public class SaveEssExpenseClaim
{
    public long Id { get; set; }
    public long? TravelRequestId { get; set; }
    public string ExpenseType { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Project { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string Currency { get; set; } = "INR";
    public string Remarks { get; set; } = "";
    public List<EssExpenseClaimLine> Lines { get; set; } = [];
}
public class EssExpenseClaimLine
{
    public long Id { get; set; }
    public long ClaimId { get; set; }
    public DateTime ExpenseDate { get; set; }
    public long CategoryId { get; set; }
    public string CategoryCode { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string SubCategory { get; set; } = "";
    public string VendorName { get; set; } = "";
    public string BillNumber { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public decimal ExchangeRate { get; set; } = 1;
    public decimal GstAmount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public string CostCenter { get; set; } = "";
    public string Project { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Location { get; set; } = "";
    public string PaymentMethod { get; set; } = "Employee Paid";
    public bool ReceiptAttached { get; set; }
    public string ReceiptFileName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public string ValidationJson { get; set; } = "[]";
    // Policy inputs are persisted inside ValidationJson so the entitlement engine can
    // evolve without adding client-specific columns to the expense line table.
    public string CityCategory { get; set; } = "";
    public decimal DistanceKm { get; set; }
    public decimal DutyHours { get; set; }
    public bool LodgingClaimed { get; set; }
    public bool LodgingIncludesFood { get; set; }
    public bool AlternativeStay { get; set; }
    public bool OvernightStay { get; set; }
    public string EntitlementLabel { get; set; } = "";
    public string EntitlementMessage { get; set; } = "";
}
public class EssExpenseCategoryOption { public long Id { get; set; } public int ClientId { get; set; } public long? ParentId { get; set; } public string ParentName { get; set; } = ""; public string ExpenseType { get; set; } = ""; public bool IsClaimHeader { get; set; } public string CategoryCode { get; set; } = ""; public string CategoryName { get; set; } = ""; public bool ReceiptMandatory { get; set; } public bool GstApplicable { get; set; } public decimal? DailyLimit { get; set; } public decimal? MaximumClaim { get; set; } public bool RequiresFinanceApproval { get; set; } public bool RequiresManagerApproval { get; set; } }
public class EssExpenseTravelOption { public long Id { get; set; } public string RequestNumber { get; set; } = ""; public string Purpose { get; set; } = ""; public string Customer { get; set; } = ""; public string Project { get; set; } = ""; public string CostCenter { get; set; } = ""; public DateTime StartDateTime { get; set; } public DateTime EndDateTime { get; set; } public string TravelMode { get; set; } = ""; public bool AccommodationRequired { get; set; } public bool LocalConveyanceRequired { get; set; } }
public class EssExpenseOptions { public string ClientName { get; set; } = ""; public long? PolicyId { get; set; } public string PolicyName { get; set; } = ""; public List<EssExpenseCategoryOption> Headers { get; set; } = []; public List<EssExpenseCategoryOption> Categories { get; set; } = []; public List<EssExpenseTravelOption> TravelRequests { get; set; } = []; public List<string> Currencies { get; set; } = []; public List<string> Locations { get; set; } = []; public List<string> PaymentMethods { get; set; } = []; public List<string> ValidationMessages { get; set; } = []; }
public class EssExpenseDashboard { public int DraftClaims { get; set; } public int PendingApproval { get; set; } public int Approved { get; set; } public int Rejected { get; set; } public int PendingPayroll { get; set; } public decimal ApprovedAmount { get; set; } }
public class EssAdminMaintenanceRequest { public string Reason { get; set; } = ""; }
public class EssAdminMaintenanceResult
{
    public bool Success { get; set; }
    public string Action { get; set; } = "";
    public string Message { get; set; } = "";
}
public class EssTravelAdvance
{
    public long Id { get; set; }
    public long TravelRequestId { get; set; }
    public string RequestNumber { get; set; } = "";
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public decimal RequestedAmount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal SettledAmount { get; set; }
    public decimal RecoverableAmount { get; set; }
    public string PaymentMode { get; set; } = "";
    public string PaymentReference { get; set; } = "";
    public DateTime? PaidDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = "Approved";
    public string Remarks { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
public class PayTravelAdvanceRequest { public decimal PaidAmount { get; set; } public string PaymentMode { get; set; } = ""; public string PaymentReference { get; set; } = ""; public DateTime? PaidDate { get; set; } public string Remarks { get; set; } = ""; }
public class SettleTravelAdvanceRequest { public decimal SettledAmount { get; set; } public string Remarks { get; set; } = ""; }
public class RecoverTravelAdvanceRequest { public decimal RecoverableAmount { get; set; } public string Remarks { get; set; } = ""; }
public class EssWorkflowTrail { public long? InstanceId { get; set; } public string WorkflowCode { get; set; } = ""; public string WorkflowName { get; set; } = ""; public string ResourceType { get; set; } = ""; public string MatchScope { get; set; } = ""; public string Status { get; set; } = ""; public DateTime? CreatedAt { get; set; } public DateTime? CompletedAt { get; set; } public List<EssWorkflowTrailItem> Events { get; set; } = []; }
public class EssWorkflowTrailItem { public string StageName { get; set; } = ""; public string Action { get; set; } = ""; public string Actor { get; set; } = ""; public string Comment { get; set; } = ""; public DateTime CreatedAt { get; set; } public bool IsPending { get; set; } }
public class EssPayslip { public int PayRunId { get; set; } public string PayPeriod { get; set; } = ""; public DateTime PayDate { get; set; } public string RunStatus { get; set; } = ""; public decimal GrossPay { get; set; } public decimal StatutoryDeductions { get; set; } public decimal OneTimeDeductions { get; set; } public decimal NetPay { get; set; } public string PaymentStatus { get; set; } = ""; public DateTime? PaymentDate { get; set; } }
public class EssPayslipDocument { public int PayRunId { get; set; } public string PayPeriod { get; set; } = ""; public string EmployeeCode { get; set; } = ""; public string FileName { get; set; } = ""; public string Html { get; set; } = ""; }
public class EssAttendanceSummary { public string Month { get; set; } = ""; public decimal PresentDays { get; set; } public decimal PayableDays { get; set; } public int TotalWorkingDays { get; set; } }
public class EssDailyAttendance { public DateTime AttendanceDate { get; set; } public string Status { get; set; } = ""; public decimal PayableValue { get; set; } public TimeSpan? CheckInTime { get; set; } public TimeSpan? CheckOutTime { get; set; } public decimal TotalHours { get; set; } public string Remarks { get; set; } = ""; }
public class EssAttendancePolicySummary { public int Id { get; set; } public string PolicyBatchId { get; set; } = ""; public string Name { get; set; } = ""; public int AttendanceCycleStartDay { get; set; } public int AttendanceCycleEndDay { get; set; } }
public class EssAttendancePeriodResponse { public string Scope { get; set; } = "calendar-month"; public string Month { get; set; } = ""; public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public bool CycleAvailable { get; set; } public EssAttendancePolicySummary? Policy { get; set; } public IEnumerable<EssDailyAttendance> Records { get; set; } = []; }
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


public class AttendanceFacialVerification
{
    public bool Passed { get; set; }
    public decimal? FaceMatchScore { get; set; }
    public decimal? LivenessScore { get; set; }
    public string Provider { get; set; } = "";
    public string ReferenceId { get; set; } = "";
}

public class ValidateAttendancePunchRequest
{
    public string ClientRequestId { get; set; } = "";
    public string Action { get; set; } = "CheckIn";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int AccuracyMeters { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string NetworkType { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public bool CameraCaptureConfirmed { get; set; }
    public bool BiometricConfirmed { get; set; }
    public string Reason { get; set; } = "";
    public AttendanceFacialVerification? Facial { get; set; }
}

public class AttendancePunchRuleSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ScopeType { get; set; } = "";
    public string Strictness { get; set; } = "";
}

public class AttendancePunchValidationResponse
{
    public bool Allowed { get; set; }
    public bool RequiresReason { get; set; }
    public bool RequiresApproval { get; set; }
    public bool PunchRecorded { get; set; }
    public long? PunchId { get; set; }
    public string Status { get; set; } = "";
    public string Decision { get; set; } = "";
    public string Message { get; set; } = "";
    public string NextAction { get; set; } = "";
    public string NextExpectedAction { get; set; } = "";
    public bool IdempotentReplay { get; set; }
    public DateTime? AttendanceDate { get; set; }
    public decimal? DistanceMeters { get; set; }
    public int? AllowedRadiusMeters { get; set; }
    public int? GpsToleranceMeters { get; set; }
    public int DeviceAccuracyMeters { get; set; }
    public int? EffectiveRadiusMeters { get; set; }
    public decimal? OutsideByMeters { get; set; }
    public bool FacialRequired { get; set; }
    public bool FacialPassed { get; set; }
    public AttendancePunchRuleSummary? Rule { get; set; }
}

public class EssAttendanceTodayState
{
    public DateTime AttendanceDate { get; set; }
    public string Status { get; set; } = "NotMarked";
    public TimeSpan? CheckInTime { get; set; }
    public TimeSpan? CheckOutTime { get; set; }
    public decimal TotalHours { get; set; }
    public decimal PayableValue { get; set; }
    public string NextExpectedAction { get; set; } = "CheckIn";
    public bool ApprovalPending { get; set; }
    public TimeSpan ShiftCheckInTime { get; set; } = new(9, 0, 0);
    public TimeSpan ShiftCheckOutTime { get; set; } = new(18, 0, 0);
    public decimal MinimumHoursForHalfDay { get; set; } = 4;
    public decimal MinimumHoursForFullDay { get; set; } = 8;
    public decimal MaximumHoursAllowedForFullDay { get; set; } = 12;
}

