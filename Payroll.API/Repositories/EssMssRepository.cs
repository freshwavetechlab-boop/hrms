using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class EssMssRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task<IEnumerable<EssLeaveBalance>> GetLeaveBalancesAsync(int employeeId, int? clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        return await db.QueryAsync<EssLeaveBalance>(@"SELECT lt.Code AS LeaveCode, lt.Name AS LeaveType, b.balance_count AS Balance, b.balance_date AS BalanceDate
FROM employee_leave_balances b
JOIN leave_types lt ON lt.Id=b.leave_type_id
JOIN Employees e ON e.Id=b.employee_id
WHERE b.employee_id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)
ORDER BY lt.Name, b.BalanceDate DESC", new { EmployeeId = employeeId, ClientId = clientId });
    }

    public async Task<EssProfile?> GetProfileAsync(int employeeId, int? clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        return await db.QueryFirstOrDefaultAsync<EssProfile>(@"SELECT e.EmployeeCode, e.FirstName, e.LastName, e.WorkEmail, e.Department, e.Designation, e.DateOfJoining,
COALESCE(w.Name, '') AS WorkLocation, COALESCE(CONCAT(m.FirstName, ' ', m.LastName), '') AS ReportingManager
FROM Employees e LEFT JOIN WorkLocations w ON w.Id=e.WorkLocationId LEFT JOIN Employees m ON m.Id=e.ReportingManagerId
WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
    }

    public async Task<(EssLeaveRequest? Request, string? Error)> CreateLeaveRequestAsync(int employeeId, int? clientId, CreateEssLeaveRequest request)
    {
        if (!DateTime.TryParse(request.FromDate, out var from) || !DateTime.TryParse(request.ToDate, out var to) || to.Date < from.Date) return (null, "Select a valid leave date range.");
        var days = (decimal)(to.Date - from.Date).TotalDays + 1;
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var leave = await db.QueryFirstOrDefaultAsync<(int Id, string Name, decimal Balance)>(@"SELECT lt.Id,lt.Name,COALESCE(b.balance_count,0) Balance FROM leave_types lt LEFT JOIN employee_leave_balances b ON b.leave_type_id=lt.Id AND b.employee_id=@EmployeeId WHERE lt.client_id=@ClientId AND lt.code=@Code AND lt.is_active=TRUE ORDER BY b.balance_date DESC LIMIT 1", new { EmployeeId = employeeId, ClientId = clientId, Code = request.LeaveCode });
        if (leave.Id == 0) return (null, "Selected leave type is unavailable.");
        if (request.LeaveCode != "LWP" && days > leave.Balance) return (null, "Requested days exceed the uploaded leave balance.");
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO EssLeaveRequests (EmployeeId,ClientId,LeaveTypeId,FromDate,ToDate,Days,Reason,Status) VALUES (@EmployeeId,@ClientId,@LeaveTypeId,@FromDate,@ToDate,@Days,@Reason,'Pending Approval'); SELECT LAST_INSERT_ID();", new { EmployeeId = employeeId, ClientId = clientId, LeaveTypeId = leave.Id, FromDate = from.Date, ToDate = to.Date, Days = days, Reason = request.Reason.Trim() });
        return (new EssLeaveRequest { Id = id, LeaveCode = request.LeaveCode, LeaveType = leave.Name, FromDate = from.Date, ToDate = to.Date, Days = days, Reason = request.Reason, Status = "Pending Approval", CreatedAt = DateTime.UtcNow }, null);
    }

    public async Task<IEnumerable<EssLeaveRequest>> GetLeaveRequestsAsync(int employeeId, int? clientId)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssLeaveRequest>(@"SELECT r.Id,lt.Code LeaveCode,lt.Name LeaveType,r.FromDate,r.ToDate,r.Days,r.Reason,r.Status,r.CreatedAt FROM EssLeaveRequests r JOIN leave_types lt ON lt.Id=r.LeaveTypeId WHERE r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId) ORDER BY r.CreatedAt DESC",new{EmployeeId=employeeId,ClientId=clientId}); }
    public async Task<EssWorkflowTrail?> GetLeaveRequestTrailAsync(long requestId, int employeeId, int? clientId)
    {
        await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");
        var instance=await db.QueryFirstOrDefaultAsync<EssWorkflowTrail>(@"SELECT i.Id InstanceId,COALESCE(m.Code,'') WorkflowCode,COALESCE(m.Name,'') WorkflowName,COALESCE(i.ResourceType,'LeaveRequest') ResourceType,CASE WHEN m.ClientId IS NULL THEN 'Global fallback' ELSE 'Client specific' END MatchScope,i.Status,i.CreatedAt,i.CompletedAt
FROM EssLeaveRequests r LEFT JOIN WorkflowInstances i ON i.ResourceType='LeaveRequest' AND i.ResourceId=CAST(r.Id AS CHAR) LEFT JOIN WorkflowMasters m ON m.Id=i.WorkflowId
WHERE r.Id=@RequestId AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)",new{RequestId=requestId,EmployeeId=employeeId,ClientId=clientId});
        if(instance is null)return null;
        if(instance.InstanceId is null){instance.Events=[];return instance;}
        var events=(await db.QueryAsync<EssWorkflowTrailItem>(@"SELECT COALESCE(s.Name,'Request') StageName,h.Action,COALESCE(u.DisplayName,'System') Actor,COALESCE(h.Comment,'') Comment,h.CreatedAt,FALSE IsPending
FROM WorkflowHistory h LEFT JOIN WorkflowTasks t ON t.Id=h.TaskId LEFT JOIN WorkflowStages s ON s.Id=t.StageId LEFT JOIN AuthUsers u ON u.Id=h.ActorUserId
WHERE h.InstanceId=@InstanceId
UNION ALL
SELECT s.Name StageName,'Pending With' Action,COALESCE(u.DisplayName,'Unassigned') Actor,COALESCE(t.Comment,'') Comment,t.CreatedAt,TRUE IsPending
FROM WorkflowTasks t JOIN WorkflowStages s ON s.Id=t.StageId LEFT JOIN AuthUsers u ON u.Id=t.ApproverUserId
WHERE t.InstanceId=@InstanceId AND t.Status='Pending'
ORDER BY CreatedAt",new{instance.InstanceId})).ToList();
        instance.Events=events;return instance;
    }
    public async Task<IEnumerable<EssPayslip>> GetPayslipsAsync(int employeeId, int? clientId)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssPayslip>(@"SELECT p.PayRunId,r.PayPeriod,r.PayDate,r.Status RunStatus,p.GrossPay,p.StatutoryDeductions,p.OneTimeDeductions,p.NetPay,p.PaymentStatus,p.PaymentDate FROM PayRunEmployees p JOIN PayRuns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@EmployeeId AND p.IsSkipped=FALSE AND r.Status IN ('Approved','Partially Paid','Paid') AND (@ClientId IS NULL OR p.ClientId=@ClientId) ORDER BY r.PayPeriod DESC",new{EmployeeId=employeeId,ClientId=clientId}); }
    public async Task<EssTaxPortal> GetTaxPortalAsync(int employeeId, int? clientId)
    {
        var fy = CurrentFinancialYear();
        await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");
        var employeeClientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM Employees WHERE Id=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (employeeClientId is null) return new EssTaxPortal { FinancialYear = fy, Message = "Employee tax profile is unavailable. Contact HR." };
        var rule = await db.QueryFirstOrDefaultAsync<ClientTaxSetting>(@"SELECT s.id Id,s.client_id ClientId,s.enabled Enabled,s.financial_year FinancialYear,s.default_regime DefaultRegime,s.allow_employee_regime_selection AllowEmployeeRegimeSelection,COALESCE(reg.is_open,s.regime_selection_window_open) RegimeSelectionWindowOpen,COALESCE(reg.end_date,s.regime_selection_cutoff) RegimeSelectionCutoff,s.allow_declarations AllowDeclarations,COALESCE(it.is_open,s.planned_declaration_window_open) PlannedDeclarationWindowOpen,COALESCE(poi.is_open,s.actual_declaration_window_open) ActualDeclarationWindowOpen,s.declaration_window_start DeclarationWindowStart,s.declaration_window_end DeclarationWindowEnd,COALESCE(it.start_date,s.planned_declaration_start) PlannedDeclarationStart,COALESCE(it.end_date,s.planned_declaration_end) PlannedDeclarationEnd,COALESCE(poi.start_date,s.actual_declaration_start) ActualDeclarationStart,COALESCE(poi.end_date,s.actual_declaration_end) ActualDeclarationEnd,COALESCE(poi.processing_month,s.poi_processing_month) PoiProcessingMonth,s.require_proof_upload RequireProofUpload,s.require_approval RequireApproval,s.active Active
FROM tax_client_settings s
LEFT JOIN tax_activity_windows reg ON reg.client_id=s.client_id AND reg.financial_year=s.financial_year AND reg.activity_code='REGIME_SELECTION'
LEFT JOIN tax_activity_windows it ON it.client_id=s.client_id AND it.financial_year=s.financial_year AND it.activity_code='IT_DECLARATION'
LEFT JOIN tax_activity_windows poi ON poi.client_id=s.client_id AND poi.financial_year=s.financial_year AND poi.activity_code='POI'
WHERE s.client_id=@ClientId AND s.financial_year=@FinancialYear AND s.active=TRUE LIMIT 1", new { ClientId = employeeClientId, FinancialYear = fy });
        if (rule is null) return new EssTaxPortal { FinancialYear = fy, Message = "Tax settings are not configured for your client and financial year yet." };
        var today = DateTime.Today;
        var selected = await db.QueryFirstOrDefaultAsync<(string Regime,string Status)>(@"SELECT regime Regime,status Status FROM employee_tax_regime_selections WHERE employee_id=@EmployeeId AND financial_year=@FinancialYear", new { EmployeeId = employeeId, FinancialYear = fy });
        var selectedRegime = string.IsNullOrWhiteSpace(selected.Regime) ? rule.DefaultRegime : selected.Regime;
        var declarationRequired = selectedRegime == "Old";
        var selectionOpen = rule.Enabled && rule.AllowEmployeeRegimeSelection && rule.RegimeSelectionWindowOpen && (!rule.RegimeSelectionCutoff.HasValue || today <= rule.RegimeSelectionCutoff.Value.Date);
        var plannedStart = rule.PlannedDeclarationStart ?? rule.DeclarationWindowStart;
        var plannedEnd = rule.PlannedDeclarationEnd ?? rule.DeclarationWindowEnd;
        var plannedOpen = rule.Enabled && rule.AllowDeclarations && declarationRequired && rule.PlannedDeclarationWindowOpen && WindowOpen(plannedStart, plannedEnd, today);
        var actualOpen = rule.Enabled && rule.AllowDeclarations && declarationRequired && rule.ActualDeclarationWindowOpen && WindowOpen(rule.ActualDeclarationStart, rule.ActualDeclarationEnd, today);
        var phase = !declarationRequired ? "NotRequired" : actualOpen ? "Actual" : plannedOpen ? "Planned" : "Closed";
        var sections = declarationRequired ? (await db.QueryAsync<EssTaxDeclarationSection>(@"SELECT COALESCE(itl.id,poil.id,d.id) DeclarationId,s.id SectionId,s.code Code,s.name Name,s.regime Regime,s.limit_amount LimitAmount,s.proof_required ProofRequired,s.requires_approval RequiresApproval,COALESCE(d.declared_amount,0) DeclaredAmount,COALESCE(itl.amount,d.planned_amount,d.declared_amount,0) PlannedAmount,COALESCE(poil.amount,d.actual_amount,0) ActualAmount,COALESCE(poil.approved_amount,itl.approved_amount,d.approved_amount) ApprovedAmount,COALESCE(poih.status,ith.status,d.status,'Draft') Status,COALESCE(poil.remarks,itl.remarks,d.remarks,'') Remarks
FROM tax_declaration_sections s
LEFT JOIN employee_tax_declarations d ON d.section_id=s.id AND d.employee_id=@EmployeeId AND d.financial_year=s.financial_year
LEFT JOIN employee_tax_declaration_headers ith ON ith.employee_id=@EmployeeId AND ith.financial_year=s.financial_year AND ith.activity_code='IT_DECLARATION'
LEFT JOIN employee_tax_declaration_lines itl ON itl.header_id=ith.id AND itl.section_id=s.id
LEFT JOIN employee_tax_declaration_headers poih ON poih.employee_id=@EmployeeId AND poih.financial_year=s.financial_year AND poih.activity_code='POI'
LEFT JOIN employee_tax_declaration_lines poil ON poil.header_id=poih.id AND poil.section_id=s.id
WHERE s.financial_year=@FinancialYear AND s.active=TRUE AND s.regime IN ('Old','Both') ORDER BY s.code", new { EmployeeId = employeeId, FinancialYear = fy })).ToList() : [];
        var adjustments = (await db.QueryAsync<EssTaxFinalAdjustmentInfo>(@"SELECT label Label,value_type ValueType,value Value FROM tax_final_adjustments WHERE financial_year=@FinancialYear AND active=TRUE ORDER BY apply_order,label", new { FinancialYear = fy })).ToList();
        var message = BuildTaxMessage(rule, selectedRegime, selectionOpen, plannedOpen, actualOpen, today);
        return new EssTaxPortal { FinancialYear = fy, Enabled = rule.Enabled, DefaultRegime = rule.DefaultRegime, SelectedRegime = string.IsNullOrWhiteSpace(selected.Regime) ? null : selected.Regime, RegimeStatus = selected.Status ?? "", CanSelectRegime = selectionOpen, CanDeclare = plannedOpen || actualOpen, CanSubmitPlanned = plannedOpen, CanSubmitActual = actualOpen, RegimeSelectionWindowOpen = rule.RegimeSelectionWindowOpen, PlannedDeclarationWindowOpen = rule.PlannedDeclarationWindowOpen, ActualDeclarationWindowOpen = rule.ActualDeclarationWindowOpen, DeclarationRequired = declarationRequired, DeclarationPhase = phase, RequiresApproval = rule.RequireApproval, RegimeSelectionCutoff = rule.RegimeSelectionCutoff, DeclarationWindowStart = plannedStart, DeclarationWindowEnd = plannedEnd, PlannedDeclarationStart = plannedStart, PlannedDeclarationEnd = plannedEnd, ActualDeclarationStart = rule.ActualDeclarationStart, ActualDeclarationEnd = rule.ActualDeclarationEnd, PoiProcessingMonth = rule.PoiProcessingMonth, Message = message, Sections = sections, FinalAdjustments = adjustments };
    }
    public async Task<(bool Ok,string? Error)> SaveTaxRegimeAsync(int employeeId, int? clientId, SaveEssTaxRegimeRequest request)
    {
        if (request.Regime is not ("Old" or "New")) return (false, "Select a valid tax regime.");
        var portal = await GetTaxPortalAsync(employeeId, clientId);
        if (!portal.CanSelectRegime) return (false, portal.Message);
        await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");
        var resolvedClientId = clientId ?? await db.ExecuteScalarAsync<int>("SELECT ClientId FROM Employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId });
        await db.ExecuteAsync(@"INSERT INTO employee_tax_regime_selections (employee_id,client_id,financial_year,regime,status) VALUES (@EmployeeId,@ClientId,@FinancialYear,@Regime,'Submitted')
ON DUPLICATE KEY UPDATE regime=@Regime,status='Submitted',submitted_at=CURRENT_TIMESTAMP,approved_by_user_id=NULL,approved_at=NULL", new { EmployeeId = employeeId, ClientId = resolvedClientId, portal.FinancialYear, request.Regime });
        return (true, null);
    }
    public async Task<(bool Ok,string? Error)> SaveTaxDeclarationsAsync(int employeeId, int? clientId, SaveEssTaxDeclarationsRequest request)
    {
        var portal = await GetTaxPortalAsync(employeeId, clientId);
        var phase = request.Phase.Equals("Actual", StringComparison.OrdinalIgnoreCase) ? "Actual" : "Planned";
        if (phase == "Planned" && !portal.CanSubmitPlanned) return (false, portal.Message);
        if (phase == "Actual" && !portal.CanSubmitActual) return (false, portal.Message);
        await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");
        var resolvedClientId = clientId ?? await db.ExecuteScalarAsync<int>("SELECT ClientId FROM Employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId });
        var valid = portal.Sections.Select(s => s.SectionId).ToHashSet();
        var activityCode = phase == "Actual" ? "POI" : "IT_DECLARATION";
        var headerId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_tax_declaration_headers (employee_id,client_id,financial_year,activity_code,status,submitted_at)
VALUES (@EmployeeId,@ClientId,@FinancialYear,@ActivityCode,@Status,CURRENT_TIMESTAMP)
ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),status=@Status,submitted_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP;
SELECT LAST_INSERT_ID();", new { EmployeeId = employeeId, ClientId = resolvedClientId, portal.FinancialYear, ActivityCode = activityCode, Status = portal.RequiresApproval ? "Submitted" : "Approved" });
        foreach (var line in request.Lines.Where(l => valid.Contains(l.SectionId)))
        {
            var amount = line.Amount != 0 ? line.Amount : line.DeclaredAmount;
            if (amount < 0) return (false, "Declared amount cannot be negative.");
            await db.ExecuteAsync(@"INSERT INTO employee_tax_declaration_lines (header_id,section_id,amount,status,remarks) VALUES (@HeaderId,@SectionId,@Amount,@Status,@Remarks)
ON DUPLICATE KEY UPDATE amount=@Amount,status=@Status,remarks=@Remarks,updated_at=CURRENT_TIMESTAMP", new { HeaderId = headerId, line.SectionId, Amount = amount, Status = portal.RequiresApproval ? "Submitted" : "Approved", Remarks = line.Remarks ?? "" });
            await db.ExecuteAsync(@"INSERT INTO employee_tax_declarations (employee_id,client_id,financial_year,section_id,declared_amount,planned_amount,actual_amount,status,remarks) VALUES (@EmployeeId,@ClientId,@FinancialYear,@SectionId,@Amount,@PlannedAmount,@ActualAmount,@Status,@Remarks)
ON DUPLICATE KEY UPDATE declared_amount=@Amount,planned_amount=IF(@Phase='Planned',@Amount,planned_amount),actual_amount=IF(@Phase='Actual',@Amount,actual_amount),status=@Status,remarks=@Remarks,updated_at=CURRENT_TIMESTAMP", new { EmployeeId = employeeId, ClientId = resolvedClientId, portal.FinancialYear, line.SectionId, Amount = amount, PlannedAmount = phase == "Planned" ? amount : 0, ActualAmount = phase == "Actual" ? amount : 0, Phase = phase, Status = portal.RequiresApproval ? "Submitted" : "Approved", Remarks = line.Remarks ?? "" });
        }
        return (true, null);
    }
    public async Task<EssAttendanceSummary?> GetAttendanceSummaryAsync(int employeeId, int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryFirstOrDefaultAsync<EssAttendanceSummary>(@"SELECT r.PayPeriod Month,p.PresentDays,p.PayableDays,r.TotalWorkingDays FROM PayRunEmployees p JOIN PayRuns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR p.ClientId=@ClientId) AND r.PayPeriod=@Month ORDER BY r.Id DESC LIMIT 1",new{EmployeeId=employeeId,ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssDailyAttendance>> GetDailyAttendanceAsync(int employeeId, int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssDailyAttendance>(@"SELECT attendance_date AS AttendanceDate,status AS Status,payable_value AS PayableValue,COALESCE(remarks,'') AS Remarks FROM employee_daily_attendance WHERE employee_id=@EmployeeId AND (@ClientId IS NULL OR client_id=@ClientId) AND DATE_FORMAT(attendance_date,'%Y-%m')=@Month ORDER BY attendance_date",new{EmployeeId=employeeId,ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssHoliday>> GetHolidaysAsync(int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssHoliday>(@"SELECT name AS Name,start_date AS StartDate,end_date AS EndDate FROM holidays WHERE client_id=@ClientId AND start_date < DATE_ADD(STR_TO_DATE(CONCAT(@Month,'-01'),'%Y-%m-%d'),INTERVAL 1 MONTH) AND end_date >= STR_TO_DATE(CONCAT(@Month,'-01'),'%Y-%m-%d') ORDER BY start_date",new{ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssBirthday>> GetTodaysBirthdaysAsync(int? clientId)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssBirthday>(@"SELECT CONCAT(e.FirstName,' ',e.LastName) Name,e.Department FROM Employees e JOIN EmployeePersonalDetails p ON p.EmployeeId=e.Id WHERE e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId) AND p.DateOfBirth<>'' AND DATE_FORMAT(STR_TO_DATE(p.DateOfBirth,'%Y-%m-%d'),'%m-%d')=DATE_FORMAT(CURDATE(),'%m-%d') ORDER BY e.FirstName", new { ClientId = clientId }); }
    public async Task SyncLeaveWorkflowStatusAsync(string resourceId, string status)
    { if (!long.TryParse(resourceId, out var id) || status is not ("Approved" or "Rejected" or "Sent Back")) return; await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");await db.ExecuteAsync("UPDATE EssLeaveRequests SET Status=@Status WHERE Id=@Id",new{Id=id,Status=status}); }
    public async Task ReconcileLeaveWorkflowStatusesAsync()
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");await db.ExecuteAsync(@"UPDATE EssLeaveRequests r JOIN WorkflowInstances w ON w.ResourceType='LeaveRequest' AND w.ResourceId=CAST(r.Id AS CHAR) SET r.Status=w.Status WHERE w.Status IN ('Approved','Rejected','Sent Back') AND r.Status<>w.Status"); }
    private static string CurrentFinancialYear()
    {
        var today = DateTime.Today;
        var start = today.Month >= 4 ? today.Year : today.Year - 1;
        return $"{start}-{(start + 1).ToString()[2..]}";
    }
    private static bool WindowOpen(DateTime? start, DateTime? end, DateTime today) => (!start.HasValue || today >= start.Value.Date) && (!end.HasValue || today <= end.Value.Date);
    private static string BuildTaxMessage(ClientTaxSetting rule, string selectedRegime, bool selectionOpen, bool plannedOpen, bool actualOpen, DateTime today)
    {
        if (!rule.Enabled) return "Tax self-service is currently disabled for your client.";
        var notes = new List<string>();
        if (!rule.AllowEmployeeRegimeSelection) notes.Add("Regime selection is managed by payroll.");
        else if (!rule.RegimeSelectionWindowOpen) notes.Add("Regime selection window is not open.");
        else if (selectionOpen) notes.Add(rule.RegimeSelectionCutoff.HasValue ? $"Regime selection is open until {rule.RegimeSelectionCutoff.Value:dd MMM yyyy}." : "Regime selection is open.");
        else notes.Add("Regime selection is closed.");
        notes.Add($"Current effective regime is {selectedRegime}.");
        if (selectedRegime == "New") { notes.Add("Investment declaration is not required under New regime."); return string.Join(" ", notes); }
        if (!rule.AllowDeclarations) notes.Add("Tax declarations are not enabled for this financial year.");
        else if (actualOpen) notes.Add(rule.ActualDeclarationEnd.HasValue ? $"Actual investment declaration is open until {rule.ActualDeclarationEnd.Value:dd MMM yyyy}." : "Actual investment declaration is open.");
        else if (plannedOpen) notes.Add((rule.PlannedDeclarationEnd ?? rule.DeclarationWindowEnd).HasValue ? $"Planned investment declaration is open until {(rule.PlannedDeclarationEnd ?? rule.DeclarationWindowEnd)!.Value:dd MMM yyyy}." : "Planned investment declaration is open.");
        else notes.Add("Investment declaration is not open right now.");
        return string.Join(" ", notes);
    }
}
