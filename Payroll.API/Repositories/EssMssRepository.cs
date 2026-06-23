using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class EssMssRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS employee_attendance_punches (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    action VARCHAR(20) NOT NULL,
    captured_at DATETIME NOT NULL,
    latitude DECIMAL(10,7) NOT NULL,
    longitude DECIMAL(10,7) NOT NULL,
    accuracy_meters INT NOT NULL DEFAULT 0,
    geo_fence_rule_id INT NULL,
    distance_meters DECIMAL(10,2) NULL,
    effective_radius_meters INT NULL,
    outside_by_meters DECIMAL(10,2) NULL,
    validation_status VARCHAR(60) NOT NULL,
    decision VARCHAR(30) NOT NULL,
    reason VARCHAR(600),
    face_verified BOOLEAN NOT NULL DEFAULT FALSE,
    face_match_score DECIMAL(6,3) NULL,
    liveness_score DECIMAL(6,3) NULL,
    face_provider VARCHAR(80),
    face_reference_id VARCHAR(180),
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_attendance_punch_employee_date (client_id, employee_id, captured_at),
    INDEX IX_attendance_punch_rule (geo_fence_rule_id)
);");
    }

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
    public async Task<IEnumerable<EssPayslip>> GetPayslipsAsync(int employeeId, int? clientId)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssPayslip>(@"SELECT p.PayRunId,r.PayPeriod,r.PayDate,r.Status RunStatus,p.GrossPay,p.StatutoryDeductions,p.OneTimeDeductions,p.NetPay,p.PaymentStatus,p.PaymentDate FROM PayRunEmployees p JOIN PayRuns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@EmployeeId AND p.IsSkipped=FALSE AND r.Status IN ('Approved','Partially Paid','Paid') AND (@ClientId IS NULL OR p.ClientId=@ClientId) ORDER BY r.PayPeriod DESC",new{EmployeeId=employeeId,ClientId=clientId}); }
    public async Task<EssAttendanceSummary?> GetAttendanceSummaryAsync(int employeeId, int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryFirstOrDefaultAsync<EssAttendanceSummary>(@"SELECT r.PayPeriod Month,p.PresentDays,p.PayableDays,r.TotalWorkingDays FROM PayRunEmployees p JOIN PayRuns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR p.ClientId=@ClientId) AND r.PayPeriod=@Month ORDER BY r.Id DESC LIMIT 1",new{EmployeeId=employeeId,ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssHoliday>> GetHolidaysAsync(int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssHoliday>(@"SELECT name AS Name,start_date AS StartDate,end_date AS EndDate FROM holidays WHERE client_id=@ClientId AND start_date < DATE_ADD(STR_TO_DATE(CONCAT(@Month,'-01'),'%Y-%m-%d'),INTERVAL 1 MONTH) AND end_date >= STR_TO_DATE(CONCAT(@Month,'-01'),'%Y-%m-%d') ORDER BY start_date",new{ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssBirthday>> GetTodaysBirthdaysAsync(int? clientId)
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");return await db.QueryAsync<EssBirthday>(@"SELECT CONCAT(FirstName,' ',LastName) Name,Department FROM Employees WHERE IsActive=TRUE AND (@ClientId IS NULL OR ClientId=@ClientId) AND DATE_FORMAT(STR_TO_DATE(JSON_UNQUOTE(JSON_EXTRACT(PersonalJson,'$.dob')),'%Y-%m-%d'),'%m-%d')=DATE_FORMAT(CURDATE(),'%m-%d') ORDER BY FirstName"); }
    public async Task SyncLeaveWorkflowStatusAsync(string resourceId, string status)
    { if (!long.TryParse(resourceId, out var id) || status is not ("Approved" or "Rejected" or "Sent Back")) return; await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");await db.ExecuteAsync("UPDATE EssLeaveRequests SET Status=@Status WHERE Id=@Id",new{Id=id,Status=status}); }
    public async Task ReconcileLeaveWorkflowStatusesAsync()
    { await using var db=Connection();await db.OpenAsync();await db.ExecuteAsync("USE payroll;");await db.ExecuteAsync(@"UPDATE EssLeaveRequests r JOIN WorkflowInstances w ON w.ResourceType='LeaveRequest' AND w.ResourceId=CAST(r.Id AS CHAR) SET r.Status=w.Status WHERE w.Status IN ('Approved','Rejected','Sent Back') AND r.Status<>w.Status"); }

    public async Task<AttendancePunchValidationResponse> ValidateAttendancePunchAsync(int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        return await ValidateAttendancePunchAsync(db, employeeId, clientId, request);
    }

    public async Task<AttendancePunchValidationResponse> RecordAttendancePunchAsync(int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        var validation = await ValidateAttendancePunchAsync(db, employeeId, clientId, request);
        if (!validation.Allowed || (validation.RequiresReason && string.IsNullOrWhiteSpace(request.Reason)))
        {
            if (validation.RequiresReason && string.IsNullOrWhiteSpace(request.Reason))
            {
                validation.Allowed = false;
                validation.Status = "ReasonRequired";
                validation.Message = "Reason is required to submit this outside-fence punch.";
                validation.NextAction = "CaptureReason";
            }
            return validation;
        }

        var capturedAt = request.CapturedAt ?? DateTime.Now;
        var decision = validation.RequiresApproval ? "PendingApproval" : validation.RequiresReason ? "SubmittedWithReason" : "Accepted";
        var punchId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_attendance_punches (client_id, employee_id, action, captured_at, latitude, longitude, accuracy_meters, geo_fence_rule_id, distance_meters, effective_radius_meters, outside_by_meters, validation_status, decision, reason, face_verified, face_match_score, liveness_score, face_provider, face_reference_id)
VALUES (@ClientId, @EmployeeId, @Action, @CapturedAt, @Latitude, @Longitude, @AccuracyMeters, @RuleId, @DistanceMeters, @EffectiveRadiusMeters, @OutsideByMeters, @Status, @Decision, @Reason, @FaceVerified, @FaceMatchScore, @LivenessScore, @FaceProvider, @FaceReferenceId);
SELECT LAST_INSERT_ID();", new
        {
            ClientId = clientId ?? 0,
            EmployeeId = employeeId,
            Action = CleanPunchAction(request.Action),
            CapturedAt = capturedAt,
            request.Latitude,
            request.Longitude,
            AccuracyMeters = Math.Max(0, request.AccuracyMeters),
            RuleId = validation.Rule?.Id,
            validation.DistanceMeters,
            validation.EffectiveRadiusMeters,
            validation.OutsideByMeters,
            validation.Status,
            Decision = decision,
            Reason = request.Reason.Trim(),
            FaceVerified = validation.FacialPassed,
            request.Facial?.FaceMatchScore,
            request.Facial?.LivenessScore,
            FaceProvider = request.Facial?.Provider ?? "",
            FaceReferenceId = request.Facial?.ReferenceId ?? ""
        });
        validation.PunchRecorded = true;
        validation.PunchId = punchId;
        validation.NextAction = decision == "PendingApproval" ? "WaitForApproval" : "ShowSuccess";
        return validation;
    }

    private static async Task<AttendancePunchValidationResponse> ValidateAttendancePunchAsync(MySqlConnection db, int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        var action = CleanPunchAction(request.Action);
        if (action == "")
            return Block("InvalidAction", "Attendance action must be CheckIn or CheckOut.", "Retry");
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return Block("InvalidLocation", "Valid latitude and longitude are required.", "Retry");
        if (request.Facial is null)
            return Block("FacialVerificationRequired", "Facial verification is required before marking attendance.", "CaptureFace");
        if (!request.Facial.Passed)
            return Block("FacialVerificationFailed", "Facial verification failed. Try again.", "CaptureFace");

        var rule = await GetApplicableGeoFenceRuleAsync(db, employeeId, clientId, request.CapturedAt ?? DateTime.Today);
        if (rule is null)
            return new AttendancePunchValidationResponse { Allowed = true, Status = "NoGeoFenceConfigured", Message = "No geo-fence rule is configured for this employee.", NextAction = "SubmitPunch", FacialPassed = true, DeviceAccuracyMeters = Math.Max(0, request.AccuracyMeters) };
        if (action == "CheckIn" && !rule.AllowCheckIn)
            return WithRule(Block("ActionNotAllowed", "Check-in is not allowed under the applicable geo-fence rule.", "Retry"), rule, request);
        if (action == "CheckOut" && !rule.AllowCheckOut)
            return WithRule(Block("ActionNotAllowed", "Check-out is not allowed under the applicable geo-fence rule.", "Retry"), rule, request);

        var distance = DistanceMeters((double)request.Latitude, (double)request.Longitude, (double)rule.Latitude, (double)rule.Longitude);
        var deviceAccuracy = Math.Max(0, request.AccuracyMeters);
        var effectiveRadius = rule.RadiusMeters + rule.GpsToleranceMeters + deviceAccuracy;
        var outsideBy = Math.Max(0, distance - effectiveRadius);
        var response = new AttendancePunchValidationResponse
        {
            Allowed = outsideBy <= 0,
            Status = outsideBy <= 0 ? "InsideFence" : "OutsideFence",
            Message = outsideBy <= 0 ? "Attendance punch allowed." : $"You are {Math.Ceiling(outsideBy)} meters outside the allowed attendance range.",
            NextAction = outsideBy <= 0 ? "SubmitPunch" : "MoveInsideFence",
            DistanceMeters = Math.Round((decimal)distance, 2),
            AllowedRadiusMeters = rule.RadiusMeters,
            GpsToleranceMeters = rule.GpsToleranceMeters,
            DeviceAccuracyMeters = deviceAccuracy,
            EffectiveRadiusMeters = effectiveRadius,
            OutsideByMeters = Math.Round((decimal)outsideBy, 2),
            FacialPassed = true,
            Rule = new AttendancePunchRuleSummary { Id = rule.Id, Name = rule.Name, ScopeType = rule.ScopeType, Strictness = rule.Strictness }
        };
        if (outsideBy <= 0) return response;
        if (rule.Strictness == "Allow with reason")
        {
            response.Allowed = true;
            response.RequiresReason = true;
            response.Status = "OutsideFenceReasonRequired";
            response.NextAction = "CaptureReason";
            return response;
        }
        if (rule.Strictness == "Allow with approval")
        {
            response.Allowed = true;
            response.RequiresApproval = true;
            response.Status = "OutsideFenceApprovalRequired";
            response.NextAction = "SubmitForApproval";
            return response;
        }
        return response;
    }

    private static AttendancePunchValidationResponse Block(string status, string message, string nextAction) =>
        new() { Allowed = false, Status = status, Message = message, NextAction = nextAction };

    private static AttendancePunchValidationResponse WithRule(AttendancePunchValidationResponse response, GeoFenceRule rule, ValidateAttendancePunchRequest request)
    {
        response.Rule = new AttendancePunchRuleSummary { Id = rule.Id, Name = rule.Name, ScopeType = rule.ScopeType, Strictness = rule.Strictness };
        response.DeviceAccuracyMeters = Math.Max(0, request.AccuracyMeters);
        response.FacialPassed = request.Facial?.Passed == true;
        return response;
    }

    private static async Task<GeoFenceRule?> GetApplicableGeoFenceRuleAsync(MySqlConnection db, int employeeId, int? clientId, DateTime onDate)
    {
        var rows = (await db.QueryAsync<GeoFenceRule>(@"SELECT r.id AS Id, r.client_id AS ClientId, r.name AS Name, r.scope_type AS ScopeType, r.work_location_id AS WorkLocationId,
r.latitude AS Latitude, r.longitude AS Longitude, r.radius_meters AS RadiusMeters, r.gps_tolerance_meters AS GpsToleranceMeters,
r.strictness AS Strictness, r.allow_check_in AS AllowCheckIn, r.allow_check_out AS AllowCheckOut, r.effective_from AS EffectiveFrom, r.effective_to AS EffectiveTo,
r.is_active AS IsActive, r.priority AS Priority
FROM attendance_geo_fence_rules r
LEFT JOIN attendance_geo_fence_rule_employees ge ON ge.geo_fence_rule_id = r.id
LEFT JOIN Employees e ON e.Id=@EmployeeId AND e.ClientId=r.client_id
WHERE (@ClientId IS NULL OR r.client_id=@ClientId) AND r.is_active=TRUE AND r.effective_from <= @Date AND (r.effective_to IS NULL OR r.effective_to >= @Date)
AND (
    (r.scope_type='Employee' AND ge.employee_id=@EmployeeId)
    OR (r.scope_type='Work Location' AND r.work_location_id=e.WorkLocationId)
    OR r.scope_type='Client Default'
)
GROUP BY r.id
ORDER BY CASE r.scope_type WHEN 'Employee' THEN 1 WHEN 'Work Location' THEN 2 ELSE 3 END, r.priority
LIMIT 1;", new { EmployeeId = employeeId, ClientId = clientId, Date = onDate.Date })).ToList();
        return rows.FirstOrDefault();
    }

    private static string CleanPunchAction(string action) =>
        action.Equals("CheckIn", StringComparison.OrdinalIgnoreCase) ? "CheckIn" :
        action.Equals("CheckOut", StringComparison.OrdinalIgnoreCase) ? "CheckOut" : "";

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371000;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
