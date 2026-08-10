using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Net;
using System.Text.Json;

namespace Payroll.API.Repositories;

public class EssMssRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS employee_attendance_punches (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    client_request_id VARCHAR(100) NULL,
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
    device_id VARCHAR(180) NOT NULL DEFAULT '',
    network_type VARCHAR(40) NOT NULL DEFAULT '',
    app_version VARCHAR(80) NOT NULL DEFAULT '',
    camera_capture_confirmed BOOLEAN NOT NULL DEFAULT FALSE,
    biometric_confirmed BOOLEAN NOT NULL DEFAULT FALSE,
    battery_percent DECIMAL(5,2) NULL,
    device_model VARCHAR(180) NOT NULL DEFAULT '',
    os_version VARCHAR(80) NOT NULL DEFAULT '',
    selfie_path VARCHAR(500) NOT NULL DEFAULT '',
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_attendance_punch_request (client_id, employee_id, client_request_id),
    INDEX IX_attendance_punch_employee_date (client_id, employee_id, captured_at),
    INDEX IX_attendance_punch_rule (geo_fence_rule_id)
);");
        await EnsureColumnAsync(db, "employee_attendance_punches", "client_request_id", "VARCHAR(100) NULL AFTER employee_id");
        await EnsureColumnAsync(db, "employee_attendance_punches", "device_id", "VARCHAR(180) NOT NULL DEFAULT '' AFTER face_reference_id");
        await EnsureColumnAsync(db, "employee_attendance_punches", "network_type", "VARCHAR(40) NOT NULL DEFAULT '' AFTER device_id");
        await EnsureColumnAsync(db, "employee_attendance_punches", "app_version", "VARCHAR(80) NOT NULL DEFAULT '' AFTER network_type");
        await EnsureColumnAsync(db, "employee_attendance_punches", "camera_capture_confirmed", "BOOLEAN NOT NULL DEFAULT FALSE AFTER app_version");
        await EnsureColumnAsync(db, "employee_attendance_punches", "biometric_confirmed", "BOOLEAN NOT NULL DEFAULT FALSE AFTER camera_capture_confirmed");
        await EnsureColumnAsync(db, "employee_attendance_punches", "battery_percent", "DECIMAL(5,2) NULL AFTER biometric_confirmed");
        await EnsureColumnAsync(db, "employee_attendance_punches", "device_model", "VARCHAR(180) NOT NULL DEFAULT '' AFTER battery_percent");
        await EnsureColumnAsync(db, "employee_attendance_punches", "os_version", "VARCHAR(80) NOT NULL DEFAULT '' AFTER device_model");
        await EnsureColumnAsync(db, "employee_attendance_punches", "selfie_path", "VARCHAR(500) NOT NULL DEFAULT '' AFTER os_version");
        await EnsureIndexAsync(db, "employee_attendance_punches", "UX_attendance_punch_request", "CREATE UNIQUE INDEX UX_attendance_punch_request ON employee_attendance_punches (client_id, employee_id, client_request_id)");
        await EnsureTravelTablesAsync(db);
        await EnsureExpenseClaimTablesAsync(db);
        await EnsureColumnAsync(db, "essleaverequests", "DayType", "VARCHAR(30) NOT NULL DEFAULT 'Full Day' AFTER ToDate");
        await EnsureProfileTablesAsync(db);
    }

    public async Task<IEnumerable<EssLeaveBalance>> GetLeaveBalancesAsync(int employeeId, int? clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryAsync<EssLeaveBalance>(@"SELECT lt.Code AS LeaveCode, lt.Name AS LeaveType, COALESCE(b.balance_count,0) AS Balance, COALESCE(b.balance_date,CURDATE()) AS BalanceDate, COALESCE(p.allow_half_day, TRUE) AS AllowHalfDay
FROM employees e
JOIN leave_types lt ON lt.client_id=e.ClientId AND lt.is_active=TRUE
LEFT JOIN leave_type_policies p ON p.leave_type_id=lt.Id
LEFT JOIN (
    SELECT b1.employee_id,b1.leave_type_id,b1.balance_count,b1.balance_date
    FROM employee_leave_balances b1
    JOIN (
        SELECT employee_id,leave_type_id,MAX(balance_date) AS balance_date
        FROM employee_leave_balances
        WHERE employee_id=@EmployeeId
        GROUP BY employee_id,leave_type_id
    ) latest ON latest.employee_id=b1.employee_id AND latest.leave_type_id=b1.leave_type_id AND latest.balance_date=b1.balance_date
) b ON b.employee_id=e.Id AND b.leave_type_id=lt.Id
WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)
ORDER BY lt.Name", new { EmployeeId = employeeId, ClientId = clientId });
    }

    public async Task<EssProfile?> GetProfileAsync(int employeeId, int? clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureProfileTablesAsync(db);
        var profile = await db.QueryFirstOrDefaultAsync<EssProfile>(@"SELECT e.ClientId,e.EmployeeCode,e.FirstName,e.LastName,e.WorkEmail,e.Department,e.Designation,e.DateOfJoining,
COALESCE(p.DateOfBirth,'') DateOfBirth,COALESCE(p.Mobile,'') Mobile,COALESCE(p.PanNumber,'') PanNumber,COALESCE(p.AadhaarNumber,'') AadhaarNumber,
COALESCE(p.Address,'') Address,COALESCE(p.CorrespondenceAddress,'') CorrespondenceAddress,COALESCE(p.PermanentAddress,'') PermanentAddress,
COALESCE(p.City,'') City,COALESCE(p.District,'') District,COALESCE(p.State,'') State,
COALESCE(pay.BankName,'') BankName,COALESCE(pay.BankAccountNo,'') BankAccountNo,COALESCE(pay.IfscCode,'') IfscCode,COALESCE(pay.PaymentMode,'') PaymentMode,
COALESCE(w.Name, '') AS WorkLocation, COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName, ' ', m.LastName), '') AS ReportingManager,
COALESCE(s.AllowProfileEdit,FALSE) CanEdit,
COALESCE(te.IsEnabled,FALSE) TravelExpenseEnabled
FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId LEFT JOIN employees m ON m.Id=e.ReportingManagerId
LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId=e.Id
LEFT JOIN ess_client_settings s ON s.ClientId=e.ClientId AND s.IsActive=TRUE
LEFT JOIN travel_expense_client_settings te ON te.ClientId=e.ClientId
  AND te.IsEnabled=TRUE
  AND (te.EffectiveFrom IS NULL OR te.EffectiveFrom<=CURRENT_DATE)
  AND (te.EffectiveTo IS NULL OR te.EffectiveTo>=CURRENT_DATE)
WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (profile is null) return null;
        var applicableOffices = await GetApplicableGeoFenceRulesAsync(db, null, employeeId, profile.ClientId, AttendanceNow().Date);
        profile.AttendanceOffice = string.Join(", ", applicableOffices
            .Select(rule => rule.Name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        return profile;
    }

    public async Task<EssFeatureAccess> GetFeatureAccessAsync(int employeeId, int? clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return new EssFeatureAccess { TravelExpenseEnabled = await IsTravelExpenseEnabledAsync(db, employeeId, clientId) };
    }

    public async Task<(EssProfile? Profile, string? Error)> SaveProfileAsync(int employeeId, int? clientId, SaveEssProfileRequest request, string changedBy)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureProfileTablesAsync(db);
        var employee = await db.QueryFirstOrDefaultAsync<(int Id, int ClientId)>("SELECT Id,ClientId FROM employees WHERE Id=@EmployeeId AND IsActive=TRUE AND (@ClientId IS NULL OR ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (employee.Id == 0) return (null, "Employee profile was not found.");
        var allowed = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ess_client_settings WHERE ClientId=@ClientId AND AllowProfileEdit=TRUE AND IsActive=TRUE", new { employee.ClientId });
        if (allowed == 0) return (null, "Profile self-update is not enabled for your client.");
        var pan = Clean(request.PanNumber);
        var ifsc = Clean(request.IfscCode);
        var email = request.WorkEmail.Trim();
        if (!string.IsNullOrWhiteSpace(email) && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return (null, "Enter a valid email address.");
        var before = await GetProfileAsync(employeeId, clientId);
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync(@"UPDATE employees
SET FirstName=@FirstName,LastName=@LastName,WorkEmail=@WorkEmail
WHERE Id=@EmployeeId", new { EmployeeId = employeeId, FirstName = Clean(request.FirstName), LastName = Clean(request.LastName), WorkEmail = email }, tx);
        await db.ExecuteAsync(@"INSERT INTO employeepersonaldetails (EmployeeId,DateOfBirth,Mobile,PanNumber,AadhaarNumber,UanNumber,EsicNumber,Address,CorrespondenceAddress,PermanentAddress,Source,SourceLocation,City,District,State,RawDesignation,OriginalEmployeeCode,DuplicateResolution,ExcelRow,EsicEmployee,PtLwfWorkmenComp,Tds,Recovery)
VALUES (@EmployeeId,@DateOfBirth,@Mobile,@PanNumber,@AadhaarNumber,'','',@Address,@CorrespondenceAddress,@PermanentAddress,'','',@City,@District,@State,'','', '',0,0,0,0,0)
ON DUPLICATE KEY UPDATE DateOfBirth=@DateOfBirth,Mobile=@Mobile,PanNumber=@PanNumber,AadhaarNumber=@AadhaarNumber,Address=@Address,CorrespondenceAddress=@CorrespondenceAddress,PermanentAddress=@PermanentAddress,City=@City,District=@District,State=@State", new { EmployeeId = employeeId, DateOfBirth = Clean(request.DateOfBirth), Mobile = Clean(request.Mobile), PanNumber = pan, AadhaarNumber = Clean(request.AadhaarNumber), Address = Clean(request.Address), CorrespondenceAddress = Clean(request.CorrespondenceAddress), PermanentAddress = Clean(request.PermanentAddress), City = Clean(request.City), District = Clean(request.District), State = Clean(request.State) }, tx);
        await db.ExecuteAsync(@"INSERT INTO employeepaymentdetails (EmployeeId,BankName,BankAccountNo,IfscCode,PaymentMode)
VALUES (@EmployeeId,@BankName,@BankAccountNo,@IfscCode,@PaymentMode)
ON DUPLICATE KEY UPDATE BankName=@BankName,BankAccountNo=@BankAccountNo,IfscCode=@IfscCode,PaymentMode=@PaymentMode", new { EmployeeId = employeeId, BankName = Clean(request.BankName), BankAccountNo = Clean(request.BankAccountNo), IfscCode = ifsc, PaymentMode = Clean(request.PaymentMode) }, tx);
        await db.ExecuteAsync(@"INSERT INTO ess_profile_update_audit (EmployeeId,ClientId,ChangedBy,OldValueJson,NewValueJson)
VALUES (@EmployeeId,@ClientId,@ChangedBy,@OldValue,@NewValue)", new { EmployeeId = employeeId, employee.ClientId, ChangedBy = changedBy, OldValue = JsonSerializer.Serialize(before), NewValue = JsonSerializer.Serialize(request) }, tx);
        await tx.CommitAsync();
        return (await GetProfileAsync(employeeId, clientId), null);
    }

    public async Task<IEnumerable<EssClientSetting>> GetEssClientSettingsAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureProfileTablesAsync(db);
        await db.ExecuteAsync(@"INSERT INTO ess_client_settings (ClientId,AllowProfileEdit,InitialPasswordMode,FixedPassword,IsActive)
SELECT Id,FALSE,'App Default','',TRUE FROM clients WHERE IsActive=TRUE
ON DUPLICATE KEY UPDATE ClientId=ClientId");
        return await db.QueryAsync<EssClientSetting>(@"SELECT s.*,COALESCE(c.Name,'') ClientName
FROM ess_client_settings s JOIN clients c ON c.Id=s.ClientId ORDER BY c.Name");
    }

    public async Task<EssClientSetting> SaveEssClientSettingAsync(EssClientSetting setting)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureProfileTablesAsync(db);
        var id = await db.ExecuteScalarAsync<int>(@"INSERT INTO ess_client_settings (Id,ClientId,AllowProfileEdit,InitialPasswordMode,FixedPassword,IsActive)
VALUES (@Id,@ClientId,@AllowProfileEdit,@InitialPasswordMode,@FixedPassword,@IsActive)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),AllowProfileEdit=VALUES(AllowProfileEdit),InitialPasswordMode=VALUES(InitialPasswordMode),FixedPassword=VALUES(FixedPassword),IsActive=VALUES(IsActive),UpdatedAt=CURRENT_TIMESTAMP;
SELECT LAST_INSERT_ID();", setting);
        return await db.QueryFirstAsync<EssClientSetting>(@"SELECT s.*,COALESCE(c.Name,'') ClientName FROM ess_client_settings s JOIN clients c ON c.Id=s.ClientId WHERE s.Id=@Id", new { Id = id });
    }

    public async Task<(EssLeaveRequest? Request, string? Error)> CreateLeaveRequestAsync(int employeeId, int? clientId, CreateEssLeaveRequest request)
    {
        if (!DateTime.TryParse(request.FromDate, out var from) || !DateTime.TryParse(request.ToDate, out var to) || to.Date < from.Date) return (null, "Select a valid leave date range.");
        var dayType = NormalizeDayType(request.DayType);
        if (dayType != "Full Day" && from.Date != to.Date) return (null, "Half-day leave can be applied for one date only.");
        var days = dayType == "Full Day" ? (decimal)(to.Date - from.Date).TotalDays + 1 : 0.5m;
        await using var db = Connection(); await db.OpenAsync();
        var leave = await db.QueryFirstOrDefaultAsync<EssLeaveSelection>(@"SELECT lt.Id,e.ClientId,lt.Name,lt.Code,lt.Type,COALESCE(b.balance_count,0) Balance,COALESCE(p.allow_negative_leave_balance,FALSE) AllowNegativeLeaveBalance,COALESCE(p.allow_half_day,TRUE) AllowHalfDay,COALESCE(p.attendance_action,'Mark as leave') AttendanceAction
FROM employees e
JOIN leave_types lt ON lt.client_id=e.ClientId AND lt.code=@Code AND lt.is_active=TRUE
LEFT JOIN leave_type_policies p ON p.leave_type_id=lt.Id
LEFT JOIN (
    SELECT b1.employee_id,b1.leave_type_id,b1.balance_count,b1.balance_date
    FROM employee_leave_balances b1
    JOIN (
        SELECT employee_id,leave_type_id,MAX(balance_date) AS balance_date
        FROM employee_leave_balances
        WHERE employee_id=@EmployeeId
        GROUP BY employee_id,leave_type_id
    ) latest ON latest.employee_id=b1.employee_id AND latest.leave_type_id=b1.leave_type_id AND latest.balance_date=b1.balance_date
) b ON b.employee_id=e.Id AND b.leave_type_id=lt.Id
WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)
LIMIT 1", new { EmployeeId = employeeId, ClientId = clientId, Code = request.LeaveCode });
        if (leave is null || leave.Id == 0) return (null, "Selected leave type is unavailable.");
        if (dayType != "Full Day" && !leave.AllowHalfDay) return (null, "Selected leave type does not allow half-day leave.");
        var marksPresent = leave.AttendanceAction.Equals("Mark as present", StringComparison.OrdinalIgnoreCase);
        if (marksPresent && dayType != "Full Day") return (null, "Attendance regularization can only be requested for full days.");
        var isPaidLeave = !marksPresent && leave.Type.Equals("Paid", StringComparison.OrdinalIgnoreCase) && !leave.Code.Equals("LWP", StringComparison.OrdinalIgnoreCase);
        if (isPaidLeave && !leave.AllowNegativeLeaveBalance && days > leave.Balance) return (null, "Requested days exceed the available leave balance.");
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO essleaverequests (EmployeeId,ClientId,LeaveTypeId,FromDate,ToDate,DayType,Days,Reason,Status) VALUES (@EmployeeId,@ClientId,@LeaveTypeId,@FromDate,@ToDate,@DayType,@Days,@Reason,'Pending Approval'); SELECT LAST_INSERT_ID();", new { EmployeeId = employeeId, ClientId = clientId ?? leave.ClientId, LeaveTypeId = leave.Id, FromDate = from.Date, ToDate = to.Date, DayType = dayType, Days = days, Reason = request.Reason.Trim() });
        return (new EssLeaveRequest { Id = id, LeaveCode = request.LeaveCode, LeaveType = leave.Name, FromDate = from.Date, ToDate = to.Date, DayType = dayType, Days = days, Reason = request.Reason, Status = "Pending Approval", CreatedAt = DateTime.UtcNow }, null);
    }

    public async Task<IEnumerable<EssLeaveRequest>> GetLeaveRequestsAsync(int employeeId, int? clientId)
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssLeaveRequest>(@"SELECT r.Id,lt.Code LeaveCode,lt.Name LeaveType,r.FromDate,r.ToDate,COALESCE(r.DayType,'Full Day') DayType,r.Days,r.Reason,r.Status,r.CreatedAt FROM essleaverequests r JOIN leave_types lt ON lt.Id=r.LeaveTypeId WHERE r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId) ORDER BY r.CreatedAt DESC",new{EmployeeId=employeeId,ClientId=clientId}); }
    public async Task<EssTravelOptions> GetTravelOptionsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId))
            return new EssTravelOptions { ValidationMessages = ["Travel & Expense is not enabled for your client."] };
        var employee = await db.QueryFirstOrDefaultAsync<EssTravelEmployee>(@"SELECT e.Id EmployeeId,e.ClientId,COALESCE(c.Name,'') ClientName
FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId
WHERE e.Id=@EmployeeId AND e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (employee is null) return new EssTravelOptions { ValidationMessages = ["Employee profile is unavailable."] };
        var resolvedClientId = employee.ClientId;
        var desk = await db.QueryFirstOrDefaultAsync<TravelExpenseClientSetting>("SELECT * FROM travel_expense_client_settings WHERE ClientId=@ClientId LIMIT 1", new { ClientId = resolvedClientId });
        var policy = await ResolveTravelPolicyAsync(db, employeeId, clientId);
        var modes = policy is null ? [] : (await db.QueryAsync<string>(@"SELECT AppliesTo FROM travel_policy_rules WHERE PolicyId=@PolicyId AND RuleType='Travel Mode' AND IsAllowed=TRUE AND IsActive=TRUE ORDER BY AppliesTo", new { PolicyId = policy.Value.Id })).ToList();
        var localModes = policy is null ? [] : (await db.QueryAsync<string>(@"SELECT AppliesTo FROM travel_policy_rules WHERE PolicyId=@PolicyId AND RuleType='Local Conveyance' AND IsAllowed=TRUE AND IsActive=TRUE ORDER BY AppliesTo", new { PolicyId = policy.Value.Id })).ToList();
        var travelTypes = await ActiveDropdownValuesAsync(db, "Travel Type", resolvedClientId);
        var locations = await ActiveDropdownValuesAsync(db, "Travel Location", resolvedClientId);
        if (locations.Count == 0)
        {
            var workLocations = await db.QueryAsync<string>(@"SELECT DISTINCT Name FROM worklocations WHERE IsActive=TRUE AND (@ClientId IS NULL OR ClientId=@ClientId)
UNION
SELECT DISTINCT City FROM worklocations WHERE IsActive=TRUE AND City<>'' AND (@ClientId IS NULL OR ClientId=@ClientId)", new { ClientId = resolvedClientId });
            var cityMasters = await db.QueryAsync<string>("SELECT Value FROM dropdownmasters WHERE IsActive=TRUE AND Type LIKE 'City:%' ORDER BY Value");
            locations = workLocations.Concat(cityMasters).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
        }
        var classes = await ActiveDropdownValuesAsync(db, "Travel Class", resolvedClientId);
        var messages = policy is null ? new List<string> { "No active travel policy is assigned to your profile. Contact HR." } : [];
        if (travelTypes.Count == 0) messages.Add("No Travel Type is configured for your client in Dropdown Masters.");
        return new EssTravelOptions
        {
            PolicyId = policy?.Id,
            PolicyName = policy?.Name ?? "",
            ClientName = employee.ClientName,
            TravelModes = modes,
            LocalTravelModes = localModes.Count == 0 ? ["Cab Aggregator", "Taxi", "Auto", "Metro", "Rental Car", "Own Vehicle"] : localModes,
            TravelTypes = travelTypes,
            Locations = locations,
            TravelClasses = classes.Count == 0 ? ["Economy", "Premium Economy", "Business", "Sleeper", "3AC", "2AC", "1AC", "Standard"] : classes,
            TravelDeskEnabled = desk?.IsTravelDeskEnabled == true,
            ShowTripDetails = desk?.ShowTripDetails == true,
            ShowAccommodationDetails = desk?.ShowAccommodationDetails == true,
            ShowLocalTravelDetails = desk?.ShowLocalTravelDetails == true,
            ValidationMessages = messages
        };
    }
    public async Task<IEnumerable<EssTravelRequest>> GetTravelRequestsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return [];
        var rows = (await db.QueryAsync<EssTravelRequest>(TravelRequestSelect("r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)") + " ORDER BY r.UpdatedAt DESC, r.Id DESC", new { EmployeeId = employeeId, ClientId = clientId })).ToList();
        await AttachTravelSectionsAsync(db, rows);
        return rows;
    }
    public async Task<IEnumerable<EssTravelRequest>> GetTravelRequestsForAdminAsync(int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var rows = (await db.QueryAsync<EssTravelRequest>(TravelRequestSelect("(@ClientId IS NULL OR r.ClientId=@ClientId)") + " ORDER BY r.UpdatedAt DESC, r.Id DESC LIMIT 1000", new { ClientId = clientId })).ToList();
        await AttachTravelSectionsAsync(db, rows);
        return rows;
    }
    public async Task<EssTravelRequest?> GetTravelRequestAsync(long id, int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return null;
        var row = await db.QueryFirstOrDefaultAsync<EssTravelRequest>(TravelRequestSelect("r.Id=@Id AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)") + " LIMIT 1", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (row is not null) await AttachTravelSectionsAsync(db, [row]);
        return row;
    }
    public async Task<(EssTravelRequest? Request, string? Error)> SaveTravelDraftAsync(int employeeId, int? clientId, SaveEssTravelRequest request)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return (null, "Travel & Expense is not enabled for your client.");
        var employee = await db.QueryFirstOrDefaultAsync<EssTravelEmployee>(@"SELECT e.Id EmployeeId,e.ClientId,COALESCE(c.Name,'') ClientName,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,e.Department,e.Designation,COALESCE(e.ReportingManagerId,0) ReportingManagerId,COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName,' ',m.LastName), '') ReportingManager
FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId LEFT JOIN employees m ON m.Id=e.ReportingManagerId
WHERE e.Id=@EmployeeId AND e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (employee is null) return (null, "Employee profile is unavailable.");
        if (string.IsNullOrWhiteSpace(request.TravelType) || await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM dropdownmasters
WHERE IsActive=TRUE AND Type='Travel Type' AND UPPER(Value)=UPPER(@TravelType) AND (ClientId=0 OR ClientId=@ClientId)", new { request.TravelType, employee.ClientId }) == 0)
            return (null, "Select a Travel Type configured for your client in Dropdown Masters.");
        await ApplyTravelDeskScopeAsync(db, employee.ClientId, request);
        var validation = ValidateTravelRequest(request, false);
        if (validation.Count > 0) return (null, validation[0].Message);
        var policy = await ResolveTravelPolicyAsync(db, employeeId, employee.ClientId);
        var policyMessages = await ValidateTravelPolicyAsync(db, policy?.Id, request, false);
        var policyJson = JsonSerializer.Serialize(policyMessages);
        var requestNumber = request.Id > 0 ? await db.ExecuteScalarAsync<string>("SELECT RequestNumber FROM ess_travel_requests WHERE Id=@Id AND EmployeeId=@EmployeeId", new { request.Id, EmployeeId = employeeId }) ?? "" : "";
        if (request.Id <= 0)
        {
            requestNumber = await NextTravelRequestNumberAsync(db);
            await using var tx = await db.BeginTransactionAsync();
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO ess_travel_requests (RequestNumber,RequestDate,EmployeeId,ClientId,Department,Designation,ReportingManagerId,Purpose,Customer,Project,CostCenter,TravelScope,TravelType,Priority,FromLocation,ToLocation,StartDateTime,EndDateTime,EstimatedCost,PolicyId,TravelMode,AccommodationRequired,LocalConveyanceRequired,AdvanceRequired,AdvanceAmount,Remarks,Status,PolicyValidationJson)
VALUES (@RequestNumber,CURRENT_DATE,@EmployeeId,@ClientId,@Department,@Designation,@ReportingManagerId,@Purpose,@Customer,@Project,@CostCenter,@TravelScope,@TravelType,@Priority,@FromLocation,@ToLocation,@StartDateTime,@EndDateTime,@EstimatedCost,@PolicyId,@TravelMode,@AccommodationRequired,@LocalConveyanceRequired,@AdvanceRequired,@AdvanceAmount,@Remarks,'Draft',@PolicyValidationJson); SELECT LAST_INSERT_ID();", ToTravelArgs(request, employee, policy?.Id, policyJson, requestNumber), tx);
            await ReplaceTravelLegsAsync(db, tx, id, NormalizeTravelCities(request));
            await ReplaceTravelAccommodationAsync(db, tx, id, NormalizeAccommodation(request));
            await ReplaceLocalTravelAsync(db, tx, id, NormalizeLocalTravel(request));
            await tx.CommitAsync();
            await AuditTravelAsync(db, id, "Created", "Draft saved");
            return (await GetTravelRequestAsync(id, employeeId, employee.ClientId), null);
        }
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM ess_travel_requests WHERE Id=@Id AND EmployeeId=@EmployeeId", new { request.Id, EmployeeId = employeeId });
        if (status != "Draft" && status != "Sent Back") return (null, "Only draft or sent back travel requests can be edited.");
        await using (var tx = await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync(@"UPDATE ess_travel_requests SET Purpose=@Purpose,Customer=@Customer,Project=@Project,CostCenter=@CostCenter,TravelScope=@TravelScope,TravelType=@TravelType,Priority=@Priority,FromLocation=@FromLocation,ToLocation=@ToLocation,StartDateTime=@StartDateTime,EndDateTime=@EndDateTime,EstimatedCost=@EstimatedCost,PolicyId=@PolicyId,TravelMode=@TravelMode,AccommodationRequired=@AccommodationRequired,LocalConveyanceRequired=@LocalConveyanceRequired,AdvanceRequired=@AdvanceRequired,AdvanceAmount=@AdvanceAmount,Remarks=@Remarks,PolicyValidationJson=@PolicyValidationJson,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id AND EmployeeId=@EmployeeId", ToTravelArgs(request, employee, policy?.Id, policyJson, requestNumber), tx);
            await ReplaceTravelLegsAsync(db, tx, request.Id, NormalizeTravelCities(request));
            await ReplaceTravelAccommodationAsync(db, tx, request.Id, NormalizeAccommodation(request));
            await ReplaceLocalTravelAsync(db, tx, request.Id, NormalizeLocalTravel(request));
            await tx.CommitAsync();
        }
        await AuditTravelAsync(db, request.Id, "Updated", "Draft updated");
        return (await GetTravelRequestAsync(request.Id, employeeId, employee.ClientId), null);
    }
    public async Task<(EssTravelRequest? Request, string? Error)> SubmitTravelRequestAsync(int employeeId, int? clientId, long id)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return (null, "Travel & Expense is not enabled for your client.");
        var request = await db.QueryFirstOrDefaultAsync<SaveEssTravelRequest>(@"SELECT Id,Purpose,Customer,Project,CostCenter,TravelScope,TravelType,Priority,FromLocation,ToLocation,StartDateTime,EndDateTime,EstimatedCost,TravelMode,AccommodationRequired,LocalConveyanceRequired,AdvanceRequired,AdvanceAmount,Remarks FROM ess_travel_requests WHERE Id=@Id AND EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (request is null) return (null, "Travel request was not found.");
        request.Cities = await GetTravelLegsAsync(db, id);
        request.AccommodationDetails = await GetTravelAccommodationAsync(db, id);
        request.LocalTravelDetails = await GetLocalTravelAsync(db, id);
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM ess_travel_requests WHERE Id=@Id", new { Id = id });
        if (status != "Draft" && status != "Sent Back") return (null, "Only draft or sent back travel requests can be submitted.");
        var desk = await GetTravelDeskSettingAsync(db, clientId ?? await db.ExecuteScalarAsync<int>("SELECT ClientId FROM employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId }));
        var errors = ValidateTravelRequest(request, true, desk?.ShowTripDetails == true);
        if (errors.Count > 0) return (null, errors[0].Message);
        var policy = await ResolveTravelPolicyAsync(db, employeeId, clientId);
        var policyMessages = await ValidateTravelPolicyAsync(db, policy?.Id, request, true);
        if (policy is null) policyMessages.Add(new EssTravelValidationResult { Severity = "Block", Message = "No active travel policy is assigned to your profile.", Behavior = "Block" });
        var block = policyMessages.FirstOrDefault(item => item.Severity == "Block" || item.Behavior == "Block");
        if (block is not null) return (null, block.Message);
        await db.ExecuteAsync(@"UPDATE ess_travel_requests SET Status='Pending Approval',SubmittedAt=CURRENT_TIMESTAMP,PolicyId=@PolicyId,PolicyValidationJson=@PolicyValidationJson,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id, PolicyId = policy?.Id, PolicyValidationJson = JsonSerializer.Serialize(policyMessages) });
        await AuditTravelAsync(db, id, "Submitted", "Submitted for approval");
        return (await GetTravelRequestAsync(id, employeeId, clientId), null);
    }
    public async Task<(bool Ok, string? Error)> WithdrawTravelRequestAsync(int employeeId, int? clientId, long id)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM ess_travel_requests WHERE Id=@Id AND EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (status is null) return (false, "Travel request was not found.");
        if (status != "Pending Approval") return (false, "Only pending travel requests can be withdrawn.");
        await db.ExecuteAsync("UPDATE ess_travel_requests SET Status='Withdrawn',UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id });
        await AuditTravelAsync(db, id, "Withdrawn", "Withdrawn by employee");
        return (true, null);
    }
    public async Task<(bool Ok, string? Error)> CancelTravelRequestAsync(int employeeId, int? clientId, long id, string reason)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var row = await db.QueryFirstOrDefaultAsync<(string Status, DateTime StartDateTime)>("SELECT Status,StartDateTime FROM ess_travel_requests WHERE Id=@Id AND EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (string.IsNullOrWhiteSpace(row.Status)) return (false, "Travel request was not found.");
        if (row.Status != "Approved") return (false, "Only approved travel requests can be cancelled.");
        if (row.StartDateTime <= DateTime.Now) return (false, "Travel can be cancelled only before travel start.");
        await db.ExecuteAsync("UPDATE ess_travel_requests SET Status='Cancellation Requested',CancellationReason=@Reason,CancellationDate=CURRENT_TIMESTAMP,CancellationStatus='Pending Approval',UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id, Reason = reason.Trim() });
        await AuditTravelAsync(db, id, "Cancellation Requested", reason.Trim());
        return (true, null);
    }
    public async Task<EssTravelDashboard> GetTravelDashboardAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return new EssTravelDashboard();
        return await db.QueryFirstAsync<EssTravelDashboard>(@"SELECT
COALESCE(SUM(Status='Draft'),0) DraftRequests,
COALESCE(SUM(Status='Pending Approval'),0) PendingApproval,
COALESCE(SUM(Status='Approved'),0) Approved,
COALESCE(SUM(Status='Rejected'),0) Rejected,
COALESCE(SUM(Status='Approved' AND StartDateTime>=NOW()),0) UpcomingTravel,
COALESCE(SUM(Status IN ('Cancelled','Cancellation Requested')),0) CancelledTrips
FROM ess_travel_requests WHERE EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
    }
    public async Task<IEnumerable<EssTravelRequest>> GetTravelCalendarAsync(int employeeId, int? clientId, string from, string to)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return [];
        return await db.QueryAsync<EssTravelRequest>(TravelRequestSelect("r.Status='Approved' AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId) AND r.StartDateTime<=@ToDate AND r.EndDateTime>=@FromDate") + " ORDER BY r.StartDateTime", new { EmployeeId = employeeId, ClientId = clientId, FromDate = from, ToDate = to });
    }
    public async Task<EssExpenseOptions> GetExpenseOptionsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId))
            return new EssExpenseOptions { ValidationMessages = ["Travel & Expense is not enabled for your client."] };
        var policy = await ResolveTravelPolicyAsync(db, employeeId, clientId);
        var employee = await db.QueryFirstOrDefaultAsync<EssTravelEmployee>(@"SELECT e.Id EmployeeId,e.ClientId,COALESCE(c.Name,'') ClientName,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,e.Department,e.Designation,COALESCE(e.ReportingManagerId,0) ReportingManagerId,'' ReportingManager
FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        var resolvedClientId = employee?.ClientId ?? clientId ?? 0;
        var clientName = employee?.ClientName ?? "";
        var headers = (await db.QueryAsync<EssExpenseCategoryOption>(@"SELECT h.Id,hs.ClientId,NULL ParentId,'' ParentName,h.HeaderName ExpenseType,TRUE IsClaimHeader,h.HeaderCode CategoryCode,h.HeaderName CategoryName,
FALSE ReceiptMandatory,FALSE GstApplicable,NULL DailyLimit,NULL MaximumClaim,FALSE RequiresFinanceApproval,FALSE RequiresManagerApproval
FROM expense_headers h
JOIN client_expense_header_settings hs ON hs.HeaderId=h.Id
WHERE hs.ClientId=@ClientId AND h.IsActive=TRUE AND hs.IsEnabled=TRUE AND (hs.EffectiveFrom IS NULL OR hs.EffectiveFrom<=CURRENT_DATE) AND (hs.EffectiveTo IS NULL OR hs.EffectiveTo>=CURRENT_DATE)
ORDER BY h.HeaderName", new { ClientId = resolvedClientId })).ToList();
        var categories = (await db.QueryAsync<EssExpenseCategoryOption>(@"SELECT c.Id,cs.ClientId,h.Id ParentId,h.HeaderName ParentName,h.HeaderName ExpenseType,FALSE IsClaimHeader,c.CategoryCode,c.CategoryName,
cs.ReceiptMandatory,cs.GstApplicable,cs.DailyLimit,cs.MaximumClaim,cs.RequiresFinanceApproval,cs.RequiresManagerApproval
FROM expense_categories c
JOIN expense_headers h ON h.Id=c.HeaderId
JOIN client_expense_header_settings hs ON hs.HeaderId=h.Id AND hs.ClientId=@ClientId
JOIN client_expense_category_settings cs ON cs.CategoryId=c.Id AND cs.ClientId=@ClientId
WHERE h.IsActive=TRUE AND c.IsActive=TRUE AND hs.IsEnabled=TRUE AND cs.IsEnabled=TRUE
  AND (hs.EffectiveFrom IS NULL OR hs.EffectiveFrom<=CURRENT_DATE) AND (hs.EffectiveTo IS NULL OR hs.EffectiveTo>=CURRENT_DATE)
  AND (cs.EffectiveFrom IS NULL OR cs.EffectiveFrom<=CURRENT_DATE) AND (cs.EffectiveTo IS NULL OR cs.EffectiveTo>=CURRENT_DATE)
ORDER BY h.HeaderName,c.CategoryName", new { ClientId = resolvedClientId })).ToList();
        var travelRequests = (await db.QueryAsync<EssExpenseTravelOption>(@"SELECT Id,RequestNumber,Purpose,Customer,Project,CostCenter,StartDateTime,EndDateTime,TravelMode,AccommodationRequired,LocalConveyanceRequired
FROM ess_travel_requests WHERE EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId) AND Status IN ('Approved','Pending Approval') ORDER BY StartDateTime DESC LIMIT 50", new { EmployeeId = employeeId, ClientId = clientId })).ToList();
        var locations = await ActiveDropdownValuesAsync(db, "Travel Location", resolvedClientId);
        return new EssExpenseOptions
        {
            ClientName = clientName,
            PolicyId = policy?.Id,
            PolicyName = policy?.Name ?? "",
            Headers = headers,
            Categories = categories,
            TravelRequests = travelRequests,
            Currencies = ["INR", "USD", "EUR", "GBP", "AED", "SGD"],
            Locations = locations,
            PaymentMethods = ["Employee Paid", "Company Card", "Cash Advance", "Direct Vendor Payment"],
            ValidationMessages = policy is null ? ["No active travel and expense policy is assigned to your profile. Contact HR."] : []
        };
    }
    public async Task<IEnumerable<EssExpenseClaim>> GetExpenseClaimsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return [];
        var rows = (await db.QueryAsync<EssExpenseClaim>(ExpenseClaimSelect("c.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR c.ClientId=@ClientId)") + " ORDER BY c.UpdatedAt DESC,c.Id DESC", new { EmployeeId = employeeId, ClientId = clientId })).ToList();
        await AttachExpenseLinesAsync(db, rows);
        return rows;
    }
    public async Task<IEnumerable<EssExpenseClaim>> GetExpenseClaimsForAdminAsync(int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        var rows = (await db.QueryAsync<EssExpenseClaim>(ExpenseClaimSelect("(@ClientId IS NULL OR c.ClientId=@ClientId)") + " ORDER BY c.UpdatedAt DESC,c.Id DESC LIMIT 1000", new { ClientId = clientId })).ToList();
        await AttachExpenseLinesAsync(db, rows);
        return rows;
    }
    public async Task<EssExpenseClaim?> GetExpenseClaimAsync(long id, int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return null;
        var row = await db.QueryFirstOrDefaultAsync<EssExpenseClaim>(ExpenseClaimSelect("c.Id=@Id AND c.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR c.ClientId=@ClientId)") + " LIMIT 1", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (row is not null) await AttachExpenseLinesAsync(db, [row]);
        return row;
    }
    public async Task<EssAdminMaintenanceResult> AdminRevertExpenseClaimAsync(long id, int? clientId, int actorUserId, string reason)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var claim = await db.QueryFirstOrDefaultAsync<AdminExpenseClaimRow>(@"SELECT Id,ClaimNumber,ClientId,EmployeeId,TravelRequestId,Status,PayrollStatus,PayrollRunId
FROM ess_expense_claims WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId) FOR UPDATE", new { Id = id, ClientId = clientId }, tx);
        if (claim is null) return MaintenanceFailure("Revert", "Expense claim was not found in your client scope.");
        var dependencyError = await ExpenseFinancialDependencyAsync(db, tx, claim);
        if (!string.IsNullOrWhiteSpace(dependencyError)) return MaintenanceFailure("Revert", dependencyError);
        await RemovePendingExpensePayrollAsync(db, tx, claim);
        await MarkWorkflowAdminRevertedAsync(db, tx, "ExpenseClaim", id, actorUserId, reason);
        await db.ExecuteAsync(@"UPDATE ess_expense_claims SET Status='Draft',PayrollStatus='Not Ready',PayrollRunId=NULL,SubmittedAt=NULL,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id;
UPDATE ess_expense_claim_lines SET Status='Draft',UpdatedAt=CURRENT_TIMESTAMP WHERE ClaimId=@Id;
INSERT INTO ess_expense_claim_audit (ClaimId,Action,Comment) VALUES (@Id,'Admin Reverted',@Reason);", new { Id = id, Reason = MaintenanceReason(reason) }, tx);
        await tx.CommitAsync();
        return MaintenanceSuccess("Revert", $"Expense claim {claim.ClaimNumber} was reverted to Draft. Pending workflow and payroll reimbursement records were cleared.");
    }
    public async Task<EssAdminMaintenanceResult> AdminDeleteExpenseClaimAsync(long id, int? clientId, int actorUserId, string reason)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var claim = await db.QueryFirstOrDefaultAsync<AdminExpenseClaimRow>(@"SELECT Id,ClaimNumber,ClientId,EmployeeId,TravelRequestId,Status,PayrollStatus,PayrollRunId
FROM ess_expense_claims WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId) FOR UPDATE", new { Id = id, ClientId = clientId }, tx);
        if (claim is null) return MaintenanceFailure("Delete", "Expense claim was not found in your client scope.");
        var dependencyError = await ExpenseFinancialDependencyAsync(db, tx, claim);
        if (!string.IsNullOrWhiteSpace(dependencyError)) return MaintenanceFailure("Delete", dependencyError);
        await RemovePendingExpensePayrollAsync(db, tx, claim);
        await db.ExecuteAsync("INSERT INTO ess_expense_claim_audit (ClaimId,Action,Comment) VALUES (@Id,'Admin Deleted',@Reason)", new { Id = id, Reason = MaintenanceReason(reason) }, tx);
        await DeleteWorkflowAsync(db, tx, "ExpenseClaim", id);
        await db.ExecuteAsync(@"DELETE FROM ess_expense_claim_attachments WHERE ClaimId=@Id;
DELETE FROM ess_expense_claim_lines WHERE ClaimId=@Id;
DELETE FROM ess_expense_claims WHERE Id=@Id;", new { Id = id }, tx);
        await tx.CommitAsync();
        return MaintenanceSuccess("Delete", $"Expense claim {claim.ClaimNumber} and its unconsumed dependencies were deleted.");
    }
    public async Task<EssAdminMaintenanceResult> AdminRevertTravelRequestAsync(long id, int? clientId, int actorUserId, string reason)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var request = await db.QueryFirstOrDefaultAsync<AdminTravelRequestRow>(@"SELECT Id,RequestNumber,ClientId,EmployeeId,Status FROM ess_travel_requests
WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId) FOR UPDATE", new { Id = id, ClientId = clientId }, tx);
        if (request is null) return MaintenanceFailure("Revert", "Travel request was not found in your client scope.");
        var dependencyError = await TravelFinancialDependencyAsync(db, tx, id);
        if (!string.IsNullOrWhiteSpace(dependencyError)) return MaintenanceFailure("Revert", dependencyError);
        await RemoveUnpaidTravelAdvanceAsync(db, tx, id);
        await MarkWorkflowAdminRevertedAsync(db, tx, "TravelRequest", id, actorUserId, reason);
        await db.ExecuteAsync(@"UPDATE ess_travel_requests SET Status='Draft',SubmittedAt=NULL,CancellationReason='',CancellationDate=NULL,CancellationStatus='',UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id;
INSERT INTO ess_travel_request_audit (RequestId,Action,Comment) VALUES (@Id,'Admin Reverted',@Reason);", new { Id = id, Reason = MaintenanceReason(reason) }, tx);
        await tx.CommitAsync();
        return MaintenanceSuccess("Revert", $"Travel request {request.RequestNumber} was reverted to Draft. Its pending workflow and unpaid advance were cleared.");
    }
    public async Task<EssAdminMaintenanceResult> AdminDeleteTravelRequestAsync(long id, int? clientId, int actorUserId, string reason)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var request = await db.QueryFirstOrDefaultAsync<AdminTravelRequestRow>(@"SELECT Id,RequestNumber,ClientId,EmployeeId,Status FROM ess_travel_requests
WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId) FOR UPDATE", new { Id = id, ClientId = clientId }, tx);
        if (request is null) return MaintenanceFailure("Delete", "Travel request was not found in your client scope.");
        var dependencyError = await TravelFinancialDependencyAsync(db, tx, id);
        if (!string.IsNullOrWhiteSpace(dependencyError)) return MaintenanceFailure("Delete", dependencyError);
        await RemoveUnpaidTravelAdvanceAsync(db, tx, id);
        await db.ExecuteAsync("INSERT INTO ess_travel_request_audit (RequestId,Action,Comment) VALUES (@Id,'Admin Deleted',@Reason)", new { Id = id, Reason = MaintenanceReason(reason) }, tx);
        await DeleteWorkflowAsync(db, tx, "TravelRequest", id);
        await db.ExecuteAsync(@"DELETE FROM ess_travel_request_local_travel WHERE RequestId=@Id;
DELETE FROM ess_travel_request_accommodation WHERE RequestId=@Id;
DELETE FROM ess_travel_request_legs WHERE RequestId=@Id;
DELETE FROM ess_travel_requests WHERE Id=@Id;", new { Id = id }, tx);
        await tx.CommitAsync();
        return MaintenanceSuccess("Delete", $"Travel request {request.RequestNumber} and its unconsumed dependencies were deleted.");
    }
    public async Task<(EssExpenseClaim? Claim, string? Error)> SaveExpenseDraftAsync(int employeeId, int? clientId, SaveEssExpenseClaim request)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return (null, "Travel & Expense is not enabled for your client.");
        var employee = await db.QueryFirstOrDefaultAsync<EssTravelEmployee>(@"SELECT e.Id EmployeeId,e.ClientId,COALESCE(c.Name,'') ClientName,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,e.Department,e.Designation,COALESCE(e.ReportingManagerId,0) ReportingManagerId,COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName,' ',m.LastName), '') ReportingManager
FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId LEFT JOIN employees m ON m.Id=e.ReportingManagerId
WHERE e.Id=@EmployeeId AND e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (employee is null) return (null, "Employee profile is unavailable.");
        request.Customer = employee.ClientName;
        request.Project = "";
        foreach (var line in request.Lines ?? [])
        {
            line.Customer = employee.ClientName;
            line.Project = "";
        }
        var lines = NormalizeExpenseLines(request);
        var validation = await ValidateExpenseClaimAsync(db, employeeId, employee.ClientId, request, false, lines);
        var claimNumber = request.Id > 0 ? await db.ExecuteScalarAsync<string>("SELECT ClaimNumber FROM ess_expense_claims WHERE Id=@Id AND EmployeeId=@EmployeeId", new { request.Id, EmployeeId = employeeId }) ?? "" : "";
        if (request.Id <= 0)
        {
            claimNumber = await NextExpenseClaimNumberAsync(db);
            await using var tx = await db.BeginTransactionAsync();
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO ess_expense_claims (ClaimNumber,ClaimDate,EmployeeId,ClientId,Department,Designation,TravelRequestId,ExpenseType,Purpose,Customer,Project,CostCenter,Currency,TotalClaimAmount,TotalApprovedAmount,TotalGstAmount,Remarks,Status,PolicyValidationJson)
VALUES (@ClaimNumber,CURRENT_DATE,@EmployeeId,@ClientId,@Department,@Designation,@TravelRequestId,@ExpenseType,@Purpose,@Customer,@Project,@CostCenter,@Currency,@TotalClaimAmount,@TotalApprovedAmount,@TotalGstAmount,@Remarks,'Draft',@PolicyValidationJson); SELECT LAST_INSERT_ID();", ToExpenseArgs(request, employee, claimNumber, validation, lines), tx);
            await ReplaceExpenseLinesAsync(db, tx, id, lines);
            await tx.CommitAsync();
            await AuditExpenseAsync(db, id, "Created", "Draft saved");
            return (await GetExpenseClaimAsync(id, employeeId, employee.ClientId), null);
        }
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM ess_expense_claims WHERE Id=@Id AND EmployeeId=@EmployeeId", new { request.Id, EmployeeId = employeeId });
        if (status != "Draft" && status != "Sent Back") return (null, "Only draft or sent back expense claims can be edited.");
        await using (var tx = await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync(@"UPDATE ess_expense_claims SET TravelRequestId=@TravelRequestId,ExpenseType=@ExpenseType,Purpose=@Purpose,Customer=@Customer,Project=@Project,CostCenter=@CostCenter,Currency=@Currency,TotalClaimAmount=@TotalClaimAmount,TotalApprovedAmount=@TotalApprovedAmount,TotalGstAmount=@TotalGstAmount,Remarks=@Remarks,PolicyValidationJson=@PolicyValidationJson,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id AND EmployeeId=@EmployeeId", ToExpenseArgs(request, employee, claimNumber, validation, lines), tx);
            await ReplaceExpenseLinesAsync(db, tx, request.Id, lines);
            await tx.CommitAsync();
        }
        await AuditExpenseAsync(db, request.Id, "Updated", "Draft updated");
        return (await GetExpenseClaimAsync(request.Id, employeeId, employee.ClientId), null);
    }
    public async Task<(EssExpenseClaim? Claim, string? Error)> SubmitExpenseClaimAsync(int employeeId, int? clientId, long id)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return (null, "Travel & Expense is not enabled for your client.");
        var request = await db.QueryFirstOrDefaultAsync<SaveEssExpenseClaim>(@"SELECT Id,TravelRequestId,ExpenseType,Purpose,Customer,Project,CostCenter,Currency,Remarks FROM ess_expense_claims WHERE Id=@Id AND EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (request is null) return (null, "Expense claim was not found.");
        request.Lines = (await db.QueryAsync<EssExpenseClaimLine>(@"SELECT Id,ClaimId,ExpenseDate,CategoryId,CategoryCode,CategoryName,SubCategory,VendorName,BillNumber,InvoiceNumber,Amount,Currency,ExchangeRate,GstAmount,ApprovedAmount,CostCenter,Project,Customer,Location,PaymentMethod,ReceiptAttached,ReceiptFileName,Description,Status,ValidationJson FROM ess_expense_claim_lines WHERE ClaimId=@Id ORDER BY ExpenseDate,Id", new { Id = id })).ToList();
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM ess_expense_claims WHERE Id=@Id", new { Id = id });
        if (status != "Draft" && status != "Sent Back") return (null, "Only draft or sent back expense claims can be submitted.");
        foreach (var line in request.Lines) HydrateExpenseEntitlement(line);
        var validation = await ValidateExpenseClaimAsync(db, employeeId, clientId, request, true, request.Lines);
        var block = validation.FirstOrDefault(item => item.Severity == "Block" || item.Behavior == "Block");
        if (block is not null) return (null, block.Message);
        await using (var tx = await db.BeginTransactionAsync())
        {
            foreach (var line in request.Lines)
                await db.ExecuteAsync(@"UPDATE ess_expense_claim_lines SET CategoryCode=@CategoryCode,CategoryName=@CategoryName,ApprovedAmount=@ApprovedAmount,ValidationJson=@ValidationJson WHERE Id=@Id AND ClaimId=@ClaimId", new { line.Id, ClaimId = id, line.CategoryCode, line.CategoryName, line.ApprovedAmount, line.ValidationJson }, tx);
            await db.ExecuteAsync("UPDATE ess_expense_claims SET Status='Pending Approval',TotalApprovedAmount=@TotalApprovedAmount,SubmittedAt=CURRENT_TIMESTAMP,PolicyValidationJson=@PolicyValidationJson,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id, TotalApprovedAmount = request.Lines.Sum(line => line.ApprovedAmount), PolicyValidationJson = JsonSerializer.Serialize(validation) }, tx);
            await tx.CommitAsync();
        }
        await AuditExpenseAsync(db, id, "Submitted", "Submitted for approval");
        return (await GetExpenseClaimAsync(id, employeeId, clientId), null);
    }
    public async Task<EssExpenseDashboard> GetExpenseDashboardAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        if (!await IsTravelExpenseEnabledAsync(db, employeeId, clientId)) return new EssExpenseDashboard();
        return await db.QueryFirstAsync<EssExpenseDashboard>(@"SELECT
COALESCE(SUM(Status='Draft'),0) DraftClaims,
COALESCE(SUM(Status='Pending Approval'),0) PendingApproval,
COALESCE(SUM(Status='Approved'),0) Approved,
COALESCE(SUM(Status='Rejected'),0) Rejected,
COALESCE(SUM(PayrollStatus='Pending Payroll'),0) PendingPayroll,
COALESCE(SUM(CASE WHEN Status='Approved' THEN TotalApprovedAmount ELSE 0 END),0) ApprovedAmount
FROM ess_expense_claims WHERE EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
    }

    public async Task<IEnumerable<EssTravelAdvance>> GetTravelAdvancesAsync(int? clientId, string status)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        return await db.QueryAsync<EssTravelAdvance>(TravelAdvanceSelect(@"
(@ClientId IS NULL OR a.ClientId=@ClientId)
AND (@Status='' OR a.Status=@Status)") + " ORDER BY a.UpdatedAt DESC,a.Id DESC", new { ClientId = clientId, Status = status?.Trim() ?? "" });
    }

    public async Task<(EssTravelAdvance? Advance, string? Error)> PayTravelAdvanceAsync(long id, PayTravelAdvanceRequest request, int userId)
    {
        if (request.PaidAmount <= 0) return (null, "Paid amount must be greater than zero.");
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var advance = await db.QueryFirstOrDefaultAsync<EssTravelAdvance>("SELECT * FROM ess_travel_advances WHERE Id=@Id FOR UPDATE", new { Id = id }, tx);
        if (advance is null) return (null, "Travel advance was not found.");
        if (advance.Status is "Cancelled" or "Recovered" or "Settled") return (null, $"Cannot pay an advance in {advance.Status} status.");
        var paid = Math.Min(request.PaidAmount, Math.Max(advance.ApprovedAmount - advance.PaidAmount, 0));
        if (paid <= 0) return (null, "Approved advance amount is already paid.");
        var nextPaid = advance.PaidAmount + paid;
        var nextStatus = nextPaid >= advance.ApprovedAmount ? "Paid" : "Partially Paid";
        await db.ExecuteAsync(@"UPDATE ess_travel_advances SET PaidAmount=@PaidAmount,PaymentMode=@PaymentMode,PaymentReference=@PaymentReference,PaidDate=@PaidDate,Status=@Status,Remarks=@Remarks,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id",
            new { Id = id, PaidAmount = nextPaid, PaymentMode = CleanText(request.PaymentMode, ""), PaymentReference = CleanText(request.PaymentReference, ""), PaidDate = (request.PaidDate ?? DateTime.Today).Date, Status = nextStatus, Remarks = CleanText(request.Remarks, "") }, tx);
        await AuditTravelAdvanceAsync(db, tx, id, "Paid", paid, request.Remarks, userId);
        await tx.CommitAsync();
        return (await GetTravelAdvanceAsync(id), null);
    }

    public async Task<(EssTravelAdvance? Advance, string? Error)> SettleTravelAdvanceAsync(long id, SettleTravelAdvanceRequest request, int userId)
    {
        if (request.SettledAmount <= 0) return (null, "Settlement amount must be greater than zero.");
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var advance = await db.QueryFirstOrDefaultAsync<EssTravelAdvance>("SELECT * FROM ess_travel_advances WHERE Id=@Id FOR UPDATE", new { Id = id }, tx);
        if (advance is null) return (null, "Travel advance was not found.");
        var openPaid = Math.Max(advance.PaidAmount - advance.SettledAmount, 0);
        var settled = Math.Min(request.SettledAmount, openPaid);
        if (settled <= 0) return (null, "No paid advance amount is available for settlement.");
        await SettleTravelAdvanceLockedAsync(db, tx, advance, settled, "Manual settlement", request.Remarks, userId);
        await tx.CommitAsync();
        return (await GetTravelAdvanceAsync(id), null);
    }

    public async Task<(EssTravelAdvance? Advance, string? Error)> MarkTravelAdvanceRecoverableAsync(long id, RecoverTravelAdvanceRequest request, int userId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var advance = await db.QueryFirstOrDefaultAsync<EssTravelAdvance>("SELECT * FROM ess_travel_advances WHERE Id=@Id FOR UPDATE", new { Id = id }, tx);
        if (advance is null) return (null, "Travel advance was not found.");
        var openPaid = Math.Max(advance.PaidAmount - advance.SettledAmount - advance.RecoverableAmount, 0);
        var recoverable = request.RecoverableAmount > 0 ? Math.Min(request.RecoverableAmount, openPaid) : openPaid;
        if (recoverable <= 0) return (null, "No unsettled paid advance amount is available for recovery.");
        var nextRecoverable = advance.RecoverableAmount + recoverable;
        await db.ExecuteAsync("UPDATE ess_travel_advances SET RecoverableAmount=@RecoverableAmount,Status='Recoverable',Remarks=@Remarks,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id, RecoverableAmount = nextRecoverable, Remarks = CleanText(request.Remarks, "") }, tx);
        await AuditTravelAdvanceAsync(db, tx, id, "Marked Recoverable", recoverable, request.Remarks, userId);
        await tx.CommitAsync();
        return (await GetTravelAdvanceAsync(id), null);
    }
    public async Task<EssWorkflowTrail?> GetExpenseClaimTrailAsync(long claimId, int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureExpenseClaimTablesAsync(db);
        var instance = await db.QueryFirstOrDefaultAsync<EssWorkflowTrail>(@"SELECT i.Id InstanceId,COALESCE(m.Code,'') WorkflowCode,COALESCE(m.Name,'') WorkflowName,COALESCE(i.ResourceType,'ExpenseClaim') ResourceType,CASE WHEN m.ClientId IS NULL THEN 'Global fallback' ELSE 'Client specific' END MatchScope,i.Status,i.CreatedAt,i.CompletedAt
FROM ess_expense_claims c LEFT JOIN workflowinstances i ON i.ResourceType='ExpenseClaim' AND i.ResourceId=CAST(c.Id AS CHAR) LEFT JOIN workflowmasters m ON m.Id=i.WorkflowId
WHERE c.Id=@ClaimId AND c.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR c.ClientId=@ClientId)", new { ClaimId = claimId, EmployeeId = employeeId, ClientId = clientId });
        if (instance is null) return null;
        if (instance.InstanceId is null) { instance.Events = []; return instance; }
        instance.Events = (await db.QueryAsync<EssWorkflowTrailItem>(@"SELECT COALESCE(s.Name,'Request') StageName,h.Action,COALESCE(u.DisplayName,'System') Actor,COALESCE(h.Comment,'') Comment,h.CreatedAt,FALSE IsPending
FROM workflowhistory h LEFT JOIN workflowtasks t ON t.Id=h.TaskId LEFT JOIN workflowstages s ON s.Id=t.StageId LEFT JOIN authusers u ON u.Id=h.ActorUserId
WHERE h.InstanceId=@InstanceId
UNION ALL
SELECT s.Name StageName,'Pending With' Action,COALESCE(u.DisplayName,'Unassigned') Actor,COALESCE(t.Comment,'') Comment,t.CreatedAt,TRUE IsPending
FROM workflowtasks t JOIN workflowstages s ON s.Id=t.StageId LEFT JOIN authusers u ON u.Id=t.ApproverUserId
WHERE t.InstanceId=@InstanceId AND t.Status='Pending'
ORDER BY CreatedAt", new { instance.InstanceId })).ToList();
        return instance;
    }
    public async Task<EssWorkflowTrail?> GetTravelRequestTrailAsync(long requestId, int employeeId, int? clientId)
    {
        await using var db=Connection();await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var instance=await db.QueryFirstOrDefaultAsync<EssWorkflowTrail>(@"SELECT i.Id InstanceId,COALESCE(m.Code,'') WorkflowCode,COALESCE(m.Name,'') WorkflowName,COALESCE(i.ResourceType,'TravelRequest') ResourceType,CASE WHEN m.ClientId IS NULL THEN 'Global fallback' ELSE 'Client specific' END MatchScope,i.Status,i.CreatedAt,i.CompletedAt
FROM ess_travel_requests r LEFT JOIN workflowinstances i ON i.ResourceType='TravelRequest' AND i.ResourceId=CAST(r.Id AS CHAR) LEFT JOIN workflowmasters m ON m.Id=i.WorkflowId
WHERE r.Id=@RequestId AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)",new{RequestId=requestId,EmployeeId=employeeId,ClientId=clientId});
        if(instance is null)return null;
        if(instance.InstanceId is null){instance.Events=[];return instance;}
        instance.Events=(await db.QueryAsync<EssWorkflowTrailItem>(@"SELECT COALESCE(s.Name,'Request') StageName,h.Action,COALESCE(u.DisplayName,'System') Actor,COALESCE(h.Comment,'') Comment,h.CreatedAt,FALSE IsPending
FROM workflowhistory h LEFT JOIN workflowtasks t ON t.Id=h.TaskId LEFT JOIN workflowstages s ON s.Id=t.StageId LEFT JOIN authusers u ON u.Id=h.ActorUserId
WHERE h.InstanceId=@InstanceId
UNION ALL
SELECT s.Name StageName,'Pending With' Action,COALESCE(u.DisplayName,'Unassigned') Actor,COALESCE(t.Comment,'') Comment,t.CreatedAt,TRUE IsPending
FROM workflowtasks t JOIN workflowstages s ON s.Id=t.StageId LEFT JOIN authusers u ON u.Id=t.ApproverUserId
WHERE t.InstanceId=@InstanceId AND t.Status='Pending'
ORDER BY CreatedAt",new{instance.InstanceId})).ToList();
        return instance;
    }
    public async Task<EssWorkflowTrail?> GetLeaveRequestTrailAsync(long requestId, int employeeId, int? clientId)
    {
        await using var db=Connection();await db.OpenAsync();
        var instance=await db.QueryFirstOrDefaultAsync<EssWorkflowTrail>(@"SELECT i.Id InstanceId,COALESCE(m.Code,'') WorkflowCode,COALESCE(m.Name,'') WorkflowName,COALESCE(i.ResourceType,'LeaveRequest') ResourceType,CASE WHEN m.ClientId IS NULL THEN 'Global fallback' ELSE 'Client specific' END MatchScope,i.Status,i.CreatedAt,i.CompletedAt
FROM essleaverequests r LEFT JOIN workflowinstances i ON i.ResourceType='LeaveRequest' AND i.ResourceId=CAST(r.Id AS CHAR) LEFT JOIN workflowmasters m ON m.Id=i.WorkflowId
WHERE r.Id=@RequestId AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)",new{RequestId=requestId,EmployeeId=employeeId,ClientId=clientId});
        if(instance is null)return null;
        if(instance.InstanceId is null){instance.Events=[];return instance;}
        var events=(await db.QueryAsync<EssWorkflowTrailItem>(@"SELECT COALESCE(s.Name,'Request') StageName,h.Action,COALESCE(u.DisplayName,'System') Actor,COALESCE(h.Comment,'') Comment,h.CreatedAt,FALSE IsPending
FROM workflowhistory h LEFT JOIN workflowtasks t ON t.Id=h.TaskId LEFT JOIN workflowstages s ON s.Id=t.StageId LEFT JOIN authusers u ON u.Id=h.ActorUserId
WHERE h.InstanceId=@InstanceId
UNION ALL
SELECT s.Name StageName,'Pending With' Action,COALESCE(u.DisplayName,'Unassigned') Actor,COALESCE(t.Comment,'') Comment,t.CreatedAt,TRUE IsPending
FROM workflowtasks t JOIN workflowstages s ON s.Id=t.StageId LEFT JOIN authusers u ON u.Id=t.ApproverUserId
WHERE t.InstanceId=@InstanceId AND t.Status='Pending'
ORDER BY CreatedAt",new{instance.InstanceId})).ToList();
        instance.Events=events;return instance;
    }
    public async Task<IEnumerable<EssPayslip>> GetPayslipsAsync(int employeeId, int? clientId)
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssPayslip>(@"SELECT p.PayRunId,r.PayPeriod,r.PayDate,r.Status RunStatus,p.GrossPay,p.StatutoryDeductions,p.OneTimeDeductions,p.NetPay,p.PaymentStatus,p.PaymentDate FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@EmployeeId AND p.IsSkipped=FALSE AND r.Status IN ('Approved','Partially Paid','Paid') AND (@ClientId IS NULL OR p.ClientId=@ClientId) ORDER BY r.PayPeriod DESC",new{EmployeeId=employeeId,ClientId=clientId}); }

    public async Task<EssPayslipDocument?> GetPayslipDocumentAsync(int employeeId, int? clientId, int payRunId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<EssPayslipRow>(@"SELECT p.Id PayRunEmployeeId,p.PayRunId,p.EmployeeId,p.ClientId,p.EmployeeCode,p.EmployeeName,p.Department,p.PresentDays,p.PayableDays,p.GrossPay,p.StatutoryDeductions,p.OneTimeEarnings,p.OneTimeDeductions,p.NetPay,p.PaymentStatus,p.PaymentDate,p.DetailsJson,
r.PayPeriod,r.PayDate,r.Status RunStatus,r.TotalWorkingDays,COALESCE(c.Name,'') ClientName,
e.WorkEmail,e.Designation,e.DateOfJoining,COALESCE(w.Name,'') WorkLocation,
COALESCE(pd.Address,'') Address,COALESCE(pd.PanNumber,'') PanNumber,COALESCE(pd.UanNumber,'') UanNumber,
COALESCE(pay.BankName,'') BankName,COALESCE(pay.BankAccountNo,'') BankAccountNo,COALESCE(pay.IfscCode,'') IfscCode
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
LEFT JOIN clients c ON c.Id=p.ClientId
LEFT JOIN employees e ON e.Id=p.EmployeeId
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=p.EmployeeId
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId=p.EmployeeId
WHERE p.EmployeeId=@EmployeeId AND p.PayRunId=@PayRunId AND p.IsSkipped=FALSE AND r.Status IN ('Approved','Partially Paid','Paid') AND (@ClientId IS NULL OR p.ClientId=@ClientId)
LIMIT 1", new { EmployeeId = employeeId, ClientId = clientId, PayRunId = payRunId });
        if (row is null) return null;

        var organization = await db.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1") ?? new Organization();
        var template = await db.QueryFirstOrDefaultAsync<EssPayslipTemplate>(@"SELECT Id,ClientId,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active
FROM paysliptemplates
WHERE Active=TRUE AND (ClientId=@ClientId OR ClientId=0)
ORDER BY CASE WHEN ClientId=@ClientId THEN 0 ELSE 1 END, Id DESC
LIMIT 1", new { row.ClientId }) ?? new EssPayslipTemplate();
        var ytd = template.ShowYtd ? await db.QueryFirstOrDefaultAsync<EssPayslipYtd>(@"SELECT COALESCE(SUM(p.GrossPay+p.OneTimeEarnings),0) Gross,COALESCE(SUM(p.StatutoryDeductions+p.OneTimeDeductions),0) Deductions,COALESCE(SUM(p.NetPay),0) NetPay
FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId
WHERE p.EmployeeId=@EmployeeId AND p.ClientId=@ClientId AND p.IsSkipped=FALSE AND r.PayPeriod<=@PayPeriod AND LEFT(r.PayPeriod,4)=LEFT(@PayPeriod,4) AND r.Status IN ('Approved','Partially Paid','Paid')", new { row.EmployeeId, row.ClientId, row.PayPeriod }) ?? new EssPayslipYtd() : new EssPayslipYtd();

        var html = BuildPayslipHtml(organization, template, row, ytd);
        return new EssPayslipDocument { PayRunId = row.PayRunId, PayPeriod = row.PayPeriod, EmployeeCode = row.EmployeeCode, FileName = $"payslip-{SafeFile(row.EmployeeCode)}-{SafeFile(row.PayPeriod)}.html", Html = html };
    }
    public async Task<EssTaxPortal> GetTaxPortalAsync(int employeeId, int? clientId)
    {
        var fy = CurrentFinancialYear();
        await using var db=Connection();await db.OpenAsync();
        var employeeClientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
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
        await using var db=Connection();await db.OpenAsync();
        var resolvedClientId = clientId ?? await db.ExecuteScalarAsync<int>("SELECT ClientId FROM employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId });
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
        await using var db=Connection();await db.OpenAsync();
        var resolvedClientId = clientId ?? await db.ExecuteScalarAsync<int>("SELECT ClientId FROM employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId });
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
    { await using var db=Connection();await db.OpenAsync();return await db.QueryFirstOrDefaultAsync<EssAttendanceSummary>(@"SELECT r.PayPeriod Month,p.PresentDays,p.PayableDays,r.TotalWorkingDays FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR p.ClientId=@ClientId) AND r.PayPeriod=@Month ORDER BY r.Id DESC LIMIT 1",new{EmployeeId=employeeId,ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssDailyAttendance>> GetDailyAttendanceAsync(int employeeId, int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssDailyAttendance>(@"SELECT attendance_date AS AttendanceDate,status AS Status,payable_value AS PayableValue,check_in_time AS CheckInTime,check_out_time AS CheckOutTime,total_hours AS TotalHours,COALESCE(remarks,'') AS Remarks FROM employee_daily_attendance WHERE employee_id=@EmployeeId AND (@ClientId IS NULL OR client_id=@ClientId) AND DATE_FORMAT(attendance_date,'%Y-%m')=@Month ORDER BY attendance_date",new{EmployeeId=employeeId,ClientId=clientId,Month=month}); }

    public async Task<EssAttendancePeriodResponse?> GetAttendanceHistoryAsync(int employeeId, int? clientId, string month, string scope)
    {
        if (!DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var monthStart))
            return null;
        await using var db = Connection();
        await db.OpenAsync();
        var identity = await GetPunchIdentityAsync(db, null, employeeId, clientId, false);
        if (identity is null) return null;
        var policy = await db.QueryFirstOrDefaultAsync<EssAttendancePolicySummary>(@"SELECT g.id AS Id, COALESCE(g.policy_batch_id,'') AS PolicyBatchId, g.name AS Name,
g.attendance_cycle_start_day AS AttendanceCycleStartDay, g.attendance_cycle_end_day AS AttendanceCycleEndDay
FROM attendance_group_employees age
JOIN attendance_groups g ON g.id=age.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
WHERE age.employee_id=@EmployeeId
ORDER BY g.id LIMIT 1;", new { identity.ClientId, EmployeeId = employeeId });
        var normalizedScope = string.Equals(scope, "attendance-cycle", StringComparison.OrdinalIgnoreCase)
            ? "attendance-cycle"
            : "calendar-month";
        DateTime fromDate;
        DateTime toDate;
        if (normalizedScope == "attendance-cycle" && policy is not null)
        {
            var anchor = DateWithClampedDay(monthStart.Year, monthStart.Month, policy.AttendanceCycleEndDay);
            var cycle = ResolveAttendanceCycle(anchor, policy.AttendanceCycleStartDay, policy.AttendanceCycleEndDay);
            fromDate = cycle.CycleStart;
            toDate = cycle.CycleEnd;
        }
        else
        {
            fromDate = monthStart.Date;
            toDate = monthStart.AddMonths(1).AddDays(-1).Date;
        }
        var records = await db.QueryAsync<EssDailyAttendance>(@"SELECT attendance_date AS AttendanceDate,status AS Status,payable_value AS PayableValue,
check_in_time AS CheckInTime,check_out_time AS CheckOutTime,total_hours AS TotalHours,COALESCE(remarks,'') AS Remarks
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_date BETWEEN @FromDate AND @ToDate
ORDER BY attendance_date;", new { identity.ClientId, EmployeeId = employeeId, FromDate = fromDate, ToDate = toDate });
        return new EssAttendancePeriodResponse
        {
            Scope = normalizedScope,
            Month = month,
            FromDate = fromDate,
            ToDate = toDate,
            CycleAvailable = policy is not null,
            Policy = policy,
            Records = records
        };
    }
    public async Task<IEnumerable<EssHoliday>> GetHolidaysAsync(int? clientId, string month)
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssHoliday>(@"SELECT name AS Name,start_date AS StartDate,end_date AS EndDate FROM holidays WHERE client_id=@ClientId AND start_date < DATE_ADD(STR_TO_DATE(CONCAT(@Month,'-01'),'%Y-%m-%d'),INTERVAL 1 MONTH) AND end_date >= STR_TO_DATE(CONCAT(@Month,'-01'),'%Y-%m-%d') ORDER BY start_date",new{ClientId=clientId,Month=month}); }
    public async Task<IEnumerable<EssBirthday>> GetTodaysBirthdaysAsync(int? clientId)
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssBirthday>(@"SELECT CONCAT(e.FirstName,' ',e.LastName) Name,e.Department FROM employees e JOIN employeepersonaldetails p ON p.EmployeeId=e.Id WHERE e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId) AND p.DateOfBirth<>'' AND DATE_FORMAT(STR_TO_DATE(p.DateOfBirth,'%Y-%m-%d'),'%m-%d')=DATE_FORMAT(CURDATE(),'%m-%d') ORDER BY e.FirstName", new { ClientId = clientId }); }
    public async Task SyncLeaveWorkflowStatusAsync(string resourceId, string status)
    {
        if (!long.TryParse(resourceId, out var id) || status is not ("Approved" or "Rejected" or "Sent Back")) return;
        await using var db=Connection();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE essleaverequests SET Status=@Status WHERE Id=@Id",new{Id=id,Status=status}, tx);
        if (status == "Approved") await ApplyApprovedLeaveEffectsAsync(db, tx, id);
        await tx.CommitAsync();
    }
    public async Task SyncTravelWorkflowStatusAsync(string resourceId, string status)
    {
        if (!long.TryParse(resourceId, out var id) || status is not ("Approved" or "Rejected" or "Sent Back")) return;
        await using var db=Connection();
        await db.OpenAsync();
        await EnsureTravelTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE ess_travel_requests SET Status=@Status,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id, Status = status }, tx);
        if (status == "Approved") await CreateTravelAdvanceIfRequiredAsync(db, tx, id);
        if (status is "Rejected" or "Sent Back")
            await db.ExecuteAsync("UPDATE ess_travel_advances SET Status='Cancelled',UpdatedAt=CURRENT_TIMESTAMP WHERE TravelRequestId=@Id AND Status IN ('Approved','Pending Payment')", new { Id = id }, tx);
        await tx.CommitAsync();
        await AuditTravelAsync(db, id, status, $"Workflow {status}");
    }
    public async Task SyncExpenseWorkflowStatusAsync(string resourceId, string status)
    {
        if (!long.TryParse(resourceId, out var id) || status is not ("Approved" or "Rejected" or "Sent Back")) return;
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureExpenseClaimTablesAsync(db);
        await using var tx = await db.BeginTransactionAsync();
        var payrollStatus = status == "Approved" ? "Pending Payroll" : status == "Rejected" ? "Not Ready" : "Not Ready";
        await db.ExecuteAsync(@"UPDATE ess_expense_claims c
SET c.Status=@Status,
    c.PayrollStatus=@PayrollStatus,
    c.TotalApprovedAmount=CASE WHEN @Status='Approved' THEN
        (SELECT COALESCE(SUM(l.ApprovedAmount),0) FROM ess_expense_claim_lines l WHERE l.ClaimId=c.Id AND l.Status<>'Rejected')
        ELSE c.TotalApprovedAmount END,
    c.UpdatedAt=CURRENT_TIMESTAMP
WHERE c.Id=@Id", new { Id = id, Status = status, PayrollStatus = payrollStatus }, tx);
        if (status == "Approved")
        {
            await QueueApprovedExpenseReimbursementAsync(db, tx, id);
        }
        await tx.CommitAsync();
        await AuditExpenseAsync(db, id, status, $"Workflow {status}");
    }
    public async Task ReconcileLeaveWorkflowStatusesAsync()
    {
        await using var db=Connection();
        await db.OpenAsync();
        var rows = (await db.QueryAsync<LeaveWorkflowReconciliationRow>(@"SELECT r.Id,w.Status
FROM essleaverequests r
JOIN workflowinstances w ON w.ResourceType='LeaveRequest' AND w.ResourceId=CAST(r.Id AS CHAR)
WHERE w.Status IN ('Approved','Rejected','Sent Back') AND r.Status<>w.Status
ORDER BY r.Id;")).ToList();
        foreach (var row in rows)
            await SyncLeaveWorkflowStatusAsync(row.Id.ToString(), row.Status);
    }

    private static async Task ApplyApprovedLeaveEffectsAsync(MySqlConnection db, MySqlTransaction tx, long requestId)
    {
        var row = await db.QueryFirstOrDefaultAsync<ApprovedLeaveRequestRow>(@"SELECT r.Id,r.EmployeeId,r.ClientId,r.LeaveTypeId,r.FromDate,r.ToDate,COALESCE(r.DayType,'Full Day') DayType,r.Days,COALESCE(r.Reason,'') Reason,lt.Code LeaveCode,lt.Type LeaveTypeKind,COALESCE(p.attendance_action,'Mark as leave') AttendanceAction
FROM essleaverequests r
JOIN leave_types lt ON lt.Id=r.LeaveTypeId
LEFT JOIN leave_type_policies p ON p.leave_type_id=lt.Id
WHERE r.Id=@RequestId", new { RequestId = requestId }, tx);
        if (row is null) return;

        var marksPresent = row.AttendanceAction.Equals("Mark as present", StringComparison.OrdinalIgnoreCase);
        var deductsBalance = !marksPresent && row.LeaveTypeKind.Equals("Paid", StringComparison.OrdinalIgnoreCase) && !row.LeaveCode.Equals("LWP", StringComparison.OrdinalIgnoreCase);
        if (deductsBalance)
        {
            var current = await db.ExecuteScalarAsync<decimal?>(@"SELECT balance_count FROM employee_leave_balances
WHERE employee_id=@EmployeeId AND leave_type_id=@LeaveTypeId
ORDER BY balance_date DESC,id DESC LIMIT 1", new { row.EmployeeId, row.LeaveTypeId }, tx) ?? 0;
            var next = current - row.Days;
            var dedupKey = $"LEAVE_REQUEST:{row.Id}";
            var inserted = await db.ExecuteAsync(@"INSERT IGNORE INTO employee_leave_ledger (ClientId,EmployeeId,LeaveTypeId,LeaveCode,TransactionDate,PeriodKey,TransactionType,Quantity,BalanceAfter,ReferenceType,ReferenceId,DedupKey,Remarks)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@LeaveCode,@TransactionDate,@PeriodKey,'Leave Availment',@Quantity,@BalanceAfter,'ESSLeaveRequest',@ReferenceId,@DedupKey,@Remarks);", new { row.ClientId, row.EmployeeId, row.LeaveTypeId, row.LeaveCode, TransactionDate = row.ToDate.Date, PeriodKey = row.ToDate.ToString("yyyy-MM"), Quantity = -row.Days, BalanceAfter = next, ReferenceId = row.Id.ToString(), DedupKey = dedupKey, Remarks = row.DayType }, tx);
            if (inserted > 0)
            {
                await db.ExecuteAsync(@"INSERT INTO employee_leave_balances (client_id,employee_id,leave_type_id,balance_date,balance_count)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@BalanceDate,@Balance)
ON DUPLICATE KEY UPDATE balance_count=VALUES(balance_count);", new { row.ClientId, row.EmployeeId, row.LeaveTypeId, BalanceDate = row.ToDate.Date, Balance = next }, tx);
            }
        }

        var status = marksPresent ? "Present" : row.LeaveCode;
        var payableValue = marksPresent || row.LeaveTypeKind.Equals("Paid", StringComparison.OrdinalIgnoreCase)
            ? 1m
            : row.DayType.Equals("Full Day", StringComparison.OrdinalIgnoreCase) ? 0m : 0.5m;
        var remarks = marksPresent
            ? $"Approved attendance regularization #{row.Id}: {row.Reason}".TrimEnd(' ', ':')
            : $"Approved leave request #{row.Id}: {row.Reason}".TrimEnd(' ', ':');
        var policy = await db.QueryFirstOrDefaultAsync<AttendanceCycleRow>(@"SELECT g.attendance_cycle_start_day AS StartDay,g.attendance_cycle_end_day AS EndDay
FROM attendance_group_employees age
JOIN attendance_groups g ON g.id=age.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
WHERE age.employee_id=@EmployeeId
ORDER BY g.id LIMIT 1;", new { row.ClientId, row.EmployeeId }, tx);
        var rollupCycles = new Dictionary<string, AttendanceCycleRange>(StringComparer.OrdinalIgnoreCase);
        for (var date = row.FromDate.Date; date <= row.ToDate.Date; date = date.AddDays(1))
        {
            await db.ExecuteAsync(@"INSERT INTO employee_daily_attendance
(client_id,employee_id,attendance_date,status,payable_value,check_in_time,check_out_time,total_hours,remarks)
VALUES (@ClientId,@EmployeeId,@AttendanceDate,@Status,@PayableValue,NULL,NULL,0,@Remarks)
ON DUPLICATE KEY UPDATE
status=VALUES(status),
payable_value=VALUES(payable_value),
check_in_time=IF(@MarksPresent,check_in_time,NULL),
check_out_time=IF(@MarksPresent,check_out_time,NULL),
total_hours=IF(@MarksPresent,total_hours,0),
remarks=VALUES(remarks);", new
            {
                row.ClientId,
                row.EmployeeId,
                AttendanceDate = date,
                Status = status,
                PayableValue = payableValue,
                Remarks = remarks,
                MarksPresent = marksPresent
            }, tx);
            var cycle = ResolveAttendanceCycle(date, policy?.StartDay ?? 1, policy?.EndDay ?? DateTime.DaysInMonth(date.Year, date.Month));
            rollupCycles.TryAdd(cycle.Month, cycle);
        }
        foreach (var cycle in rollupCycles.Values)
            await RollupApprovedLeaveAttendanceAsync(db, tx, row.ClientId, row.EmployeeId, cycle);
    }

    private async Task<EssTravelAdvance?> GetTravelAdvanceAsync(long id)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        return await db.QueryFirstOrDefaultAsync<EssTravelAdvance>(TravelAdvanceSelect("a.Id=@Id") + " LIMIT 1", new { Id = id });
    }

    private static string TravelAdvanceSelect(string where) => $@"SELECT a.*,r.RequestNumber,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,COALESCE(c.Name,'') ClientName
FROM ess_travel_advances a
JOIN ess_travel_requests r ON r.Id=a.TravelRequestId
JOIN employees e ON e.Id=a.EmployeeId
LEFT JOIN clients c ON c.Id=a.ClientId
WHERE {where}";

    private static async Task CreateTravelAdvanceIfRequiredAsync(MySqlConnection db, System.Data.IDbTransaction tx, long travelRequestId)
    {
        var row = await db.QueryFirstOrDefaultAsync<TravelAdvanceSeed>(@"SELECT Id TravelRequestId,EmployeeId,ClientId,AdvanceAmount,EndDateTime
FROM ess_travel_requests
WHERE Id=@Id AND AdvanceRequired=TRUE AND AdvanceAmount>0", new { Id = travelRequestId }, tx);
        if (row is null) return;
        var settlementDays = await ResolveTravelAdvanceSettlementDaysAsync(db, tx, row.TravelRequestId);
        var dueDate = row.EndDateTime.Date.AddDays(settlementDays);
        await db.ExecuteAsync(@"INSERT INTO ess_travel_advances (TravelRequestId,EmployeeId,ClientId,RequestedAmount,ApprovedAmount,DueDate,Status)
VALUES (@TravelRequestId,@EmployeeId,@ClientId,@AdvanceAmount,@AdvanceAmount,@DueDate,'Approved')
ON DUPLICATE KEY UPDATE RequestedAmount=VALUES(RequestedAmount),ApprovedAmount=VALUES(ApprovedAmount),DueDate=VALUES(DueDate),Status=IF(Status='Cancelled','Approved',Status),UpdatedAt=CURRENT_TIMESTAMP",
            new { row.TravelRequestId, row.EmployeeId, row.ClientId, row.AdvanceAmount, DueDate = dueDate }, tx);
    }

    private static async Task<int> ResolveTravelAdvanceSettlementDaysAsync(MySqlConnection db, System.Data.IDbTransaction tx, long travelRequestId)
    {
        var config = await db.ExecuteScalarAsync<string>(@"SELECT pr.ConfigJson
FROM ess_travel_requests r
JOIN travel_policy_rules pr ON pr.PolicyId=r.PolicyId AND pr.RuleType='Travel Advance' AND pr.IsActive=TRUE
WHERE r.Id=@TravelRequestId
ORDER BY pr.Id DESC LIMIT 1", new { TravelRequestId = travelRequestId }, tx) ?? "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(config) ? "{}" : config);
            return doc.RootElement.TryGetProperty("settlementDays", out var value) && value.TryGetInt32(out var days) && days > 0 ? days : 7;
        }
        catch { return 7; }
    }

    private static async Task QueueApprovedExpenseReimbursementAsync(MySqlConnection db, System.Data.IDbTransaction tx, long claimId)
    {
        var claim = await db.QueryFirstOrDefaultAsync<ExpenseClaimQueueHeader>(@"SELECT c.Id,c.ClientId,c.EmployeeId,c.TravelRequestId,c.ClaimNumber,c.ReimbursementComponentCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,e.EmployeeCode,
COALESCE(sc.Id,0) ComponentId,COALESCE(NULLIF(c.ReimbursementComponentCode,''),NULLIF(sc.Code,''),'REIMBURSEMENT') ComponentCode,COALESCE(NULLIF(sc.Name,''),'Expense Reimbursement') ComponentName
FROM ess_expense_claims c
JOIN employees e ON e.Id=c.EmployeeId
LEFT JOIN salarycomponents sc ON sc.Id=(SELECT sc2.Id FROM salarycomponents sc2 WHERE sc2.Active=TRUE AND (sc2.Code=c.ReimbursementComponentCode OR sc2.ComponentRole='Reimbursement') ORDER BY CASE WHEN sc2.Code=c.ReimbursementComponentCode THEN 0 ELSE 1 END, sc2.Priority, sc2.Id LIMIT 1)
WHERE c.Id=@Id", new { Id = claimId }, tx);
        if (claim is null) return;
        var lines = (await db.QueryAsync<ExpenseClaimQueueLine>(@"SELECT Id,
CASE
  WHEN JSON_VALID(ValidationJson) AND JSON_UNQUOTE(JSON_EXTRACT(ValidationJson,'$.calculated'))='true' THEN ApprovedAmount
  ELSE COALESCE(NULLIF(ApprovedAmount,0),Amount)
END Amount
FROM ess_expense_claim_lines WHERE ClaimId=@Id AND Status<>'Rejected' ORDER BY ExpenseDate,Id", new { Id = claimId }, tx)).ToList();
        var advanceOffset = claim.TravelRequestId.HasValue ? await ApplyTravelAdvanceSettlementForClaimAsync(db, tx, claim.TravelRequestId.Value, lines.Sum(l => l.Amount)) : 0;
        foreach (var line in lines)
        {
            var payable = line.Amount;
            if (advanceOffset > 0)
            {
                var used = Math.Min(payable, advanceOffset);
                payable -= used;
                advanceOffset -= used;
            }
            if (payable <= 0) continue;
            await db.ExecuteAsync(@"INSERT INTO payroll_reimbursement_queue (ClientId,EmployeeId,ExpenseClaimId,ExpenseLineId,ComponentCode,Amount,Taxable,Status,CreatedAt)
VALUES (@ClientId,@EmployeeId,@ExpenseClaimId,@ExpenseLineId,@ComponentCode,@Amount,FALSE,'Pending',CURRENT_TIMESTAMP)
ON DUPLICATE KEY UPDATE Amount=VALUES(Amount),Status='Pending',UpdatedAt=CURRENT_TIMESTAMP", new { claim.ClientId, claim.EmployeeId, ExpenseClaimId = claim.Id, ExpenseLineId = line.Id, claim.ComponentCode, Amount = payable }, tx);
            await db.ExecuteAsync(@"INSERT INTO payrolladjustments (ClientId,EmployeeId,EmployeeName,EmployeeCode,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,ReasonCode,Notes,Taxable,Status)
SELECT @ClientId,@EmployeeId,@EmployeeName,@EmployeeCode,@ComponentId,@ComponentCode,@ComponentName,'Reimbursement',@Amount,DATE_FORMAT(CURDATE(),'%Y-%m'),'Regular','EXPENSE_CLAIM',@Notes,FALSE,'Approved'
WHERE NOT EXISTS (SELECT 1 FROM payrolladjustments a WHERE a.ClientId=@ClientId AND a.EmployeeId=@EmployeeId AND a.ReasonCode='EXPENSE_CLAIM' AND a.Notes=@Notes)",
                new { claim.ClientId, claim.EmployeeId, claim.EmployeeName, claim.EmployeeCode, claim.ComponentId, claim.ComponentCode, claim.ComponentName, Amount = payable, Notes = $"Expense claim {claim.ClaimNumber} line {line.Id}" }, tx);
        }
    }

    private static async Task<decimal> ApplyTravelAdvanceSettlementForClaimAsync(MySqlConnection db, System.Data.IDbTransaction tx, long travelRequestId, decimal claimAmount)
    {
        if (claimAmount <= 0) return 0;
        var advances = (await db.QueryAsync<EssTravelAdvance>("SELECT * FROM ess_travel_advances WHERE TravelRequestId=@TravelRequestId AND Status NOT IN ('Cancelled','Recovered') ORDER BY Id FOR UPDATE", new { TravelRequestId = travelRequestId }, tx)).ToList();
        var remaining = claimAmount;
        var settledTotal = 0m;
        foreach (var advance in advances)
        {
            var openPaid = Math.Max(advance.PaidAmount - advance.SettledAmount - advance.RecoverableAmount, 0);
            if (openPaid <= 0 || remaining <= 0) continue;
            var settle = Math.Min(openPaid, remaining);
            await SettleTravelAdvanceLockedAsync(db, tx, advance, settle, "Expense claim settlement", $"Auto-settled from approved expense claim for travel request {travelRequestId}", null);
            remaining -= settle;
            settledTotal += settle;
        }
        return settledTotal;
    }

    private static async Task SettleTravelAdvanceLockedAsync(MySqlConnection db, System.Data.IDbTransaction tx, EssTravelAdvance advance, decimal settled, string action, string comment, int? userId)
    {
        var nextSettled = advance.SettledAmount + settled;
        var openAfter = Math.Max(advance.PaidAmount - nextSettled - advance.RecoverableAmount, 0);
        var status = nextSettled >= advance.PaidAmount ? "Settled" : openAfter > 0 ? "Partially Settled" : advance.Status;
        await db.ExecuteAsync("UPDATE ess_travel_advances SET SettledAmount=@SettledAmount,Status=@Status,Remarks=@Remarks,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { advance.Id, SettledAmount = nextSettled, Status = status, Remarks = CleanText(comment, "") }, tx);
        await AuditTravelAdvanceAsync(db, tx, advance.Id, action, settled, comment, userId);
    }

    private static async Task BackfillTravelAdvancesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"INSERT INTO ess_travel_advances (TravelRequestId,EmployeeId,ClientId,RequestedAmount,ApprovedAmount,DueDate,Status)
SELECT r.Id,r.EmployeeId,r.ClientId,r.AdvanceAmount,r.AdvanceAmount,DATE_ADD(DATE(r.EndDateTime), INTERVAL 7 DAY),'Approved'
FROM ess_travel_requests r
WHERE r.Status='Approved' AND r.AdvanceRequired=TRUE AND r.AdvanceAmount>0
ON DUPLICATE KEY UPDATE TravelRequestId=VALUES(TravelRequestId)");
    }

    private static Task AuditTravelAdvanceAsync(MySqlConnection db, System.Data.IDbTransaction tx, long id, string action, decimal amount, string comment, int? userId) =>
        db.ExecuteAsync("INSERT INTO ess_travel_advance_audit (AdvanceId,Action,Amount,Comment,CreatedBy) VALUES (@Id,@Action,@Amount,@Comment,@UserId)", new { Id = id, Action = action, Amount = amount, Comment = CleanText(comment, ""), UserId = userId }, tx);

    private static async Task EnsureColumnAsync(MySqlConnection connection, string table, string column, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table AND COLUMN_NAME = @Column", new { Table = table, Column = column });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }

    private static async Task EnsureIndexAsync(MySqlConnection connection, string table, string index, string createSql)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table AND INDEX_NAME = @Index", new { Table = table, Index = index });
        if (exists == 0) await connection.ExecuteAsync(createSql);
    }

    private static string NormalizeDayType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "first half" or "firsthalf" or "fh" => "First Half",
        "second half" or "secondhalf" or "sh" => "Second Half",
        _ => "Full Day"
    };
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
    public async Task<EssAttendanceTodayState?> GetAttendanceTodayAsync(int employeeId, int? clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var identity = await GetPunchIdentityAsync(db, null, employeeId, clientId, false);
        return identity is null
            ? null
            : await GetAttendanceStateAsync(db, null, identity.ClientId, employeeId, AttendanceNow().Date);
    }

    public async Task<AttendancePunchValidationResponse> ValidateAttendancePunchAsync(int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var identity = await GetPunchIdentityAsync(db, null, employeeId, clientId, false);
        if (identity is null)
            return Block("EmployeeOrClientInactive", "The active employee and client mapping was not found.", "ContactHR");
        var capturedAt = ResolveAttendanceDateTime(request.CapturedAt);
        return await ValidateAttendancePunchAsync(db, null, identity.ClientId, employeeId, request, capturedAt);
    }

    public async Task<AttendancePunchValidationResponse> RecordAttendancePunchAsync(int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var identity = await GetPunchIdentityAsync(db, transaction, employeeId, clientId, true);
        if (identity is null)
            return Block("EmployeeOrClientInactive", "The active employee and client mapping was not found.", "ContactHR");

        var action = CleanPunchAction(request.Action);
        var clientRequestId = CleanText(request.ClientRequestId, "");
        if (clientRequestId.Length > 0)
        {
            var existing = await db.QueryFirstOrDefaultAsync<ExistingAttendancePunch>(@"SELECT id AS Id, action AS Action, captured_at AS CapturedAt, decision AS Decision
FROM employee_attendance_punches
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND client_request_id=@ClientRequestId
LIMIT 1;", new { identity.ClientId, EmployeeId = employeeId, ClientRequestId = clientRequestId }, transaction);
            if (existing is not null)
            {
                if (!string.Equals(existing.Action, action, StringComparison.OrdinalIgnoreCase))
                    return Block("IdempotencyKeyConflict", "This attendance request id was already used for a different action.", "RefreshAttendance");
                var replayState = await GetAttendanceStateAsync(db, transaction, identity.ClientId, employeeId, existing.CapturedAt.Date);
                await transaction.CommitAsync();
                return new AttendancePunchValidationResponse
                {
                    Allowed = true,
                    PunchRecorded = true,
                    PunchId = existing.Id,
                    Status = "IdempotentReplay",
                    Decision = existing.Decision,
                    Message = "Attendance was already recorded for this request.",
                    NextAction = "ShowSuccess",
                    NextExpectedAction = replayState.NextExpectedAction,
                    IdempotentReplay = true,
                    AttendanceDate = replayState.AttendanceDate
                };
            }
        }

        var capturedAt = ResolveAttendanceDateTime(request.CapturedAt);
        var validation = await ValidateAttendancePunchAsync(db, transaction, identity.ClientId, employeeId, request, capturedAt);
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

        var decision = validation.RequiresReason ? "AcceptedWithReason" : "Accepted";
        var punchId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_attendance_punches
 (client_id, employee_id, client_request_id, action, captured_at, latitude, longitude, accuracy_meters, geo_fence_rule_id, distance_meters, effective_radius_meters, outside_by_meters, validation_status, decision, reason, face_verified, face_match_score, liveness_score, face_provider, face_reference_id, device_id, network_type, app_version, camera_capture_confirmed, biometric_confirmed, device_model, os_version)
 VALUES
 (@ClientId, @EmployeeId, @ClientRequestId, @Action, @CapturedAt, @Latitude, @Longitude, @AccuracyMeters, @RuleId, @DistanceMeters, @EffectiveRadiusMeters, @OutsideByMeters, @Status, @Decision, @Reason, FALSE, NULL, NULL, '', '', @DeviceId, @NetworkType, @AppVersion, @CameraCaptureConfirmed, @BiometricConfirmed, @DeviceModel, @OsVersion);
SELECT LAST_INSERT_ID();", new
        {
            ClientId = identity.ClientId,
            EmployeeId = employeeId,
            ClientRequestId = clientRequestId,
            Action = action,
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
            Reason = CleanText(request.Reason, ""),
            DeviceId = CleanText(request.DeviceId, ""),
            NetworkType = CleanText(request.NetworkType, ""),
            AppVersion = CleanText(request.AppVersion, ""),
            request.CameraCaptureConfirmed,
            request.BiometricConfirmed,
            DeviceModel = CleanText(request.DeviceModel, ""),
            OsVersion = CleanText(request.OsVersion, "")
        }, transaction);

        var state = await ProjectAcceptedPunchAsync(db, transaction, identity.ClientId, employeeId, action, capturedAt);
        await transaction.CommitAsync();
        validation.PunchRecorded = true;
        validation.PunchId = punchId;
        validation.Decision = decision;
        validation.AttendanceDate = state.AttendanceDate;
        validation.NextAction = "ShowSuccess";
        validation.NextExpectedAction = state.NextExpectedAction;
        return validation;
    }

    private static async Task<AttendancePunchValidationResponse> ValidateAttendancePunchAsync(MySqlConnection db, System.Data.IDbTransaction? transaction, int clientId, int employeeId, ValidateAttendancePunchRequest request, DateTime capturedAt)
    {
        var action = CleanPunchAction(request.Action);
        if (action == "")
            return Block("InvalidAction", "Attendance action must be CheckIn or CheckOut.", "Retry");
        if (string.IsNullOrWhiteSpace(request.ClientRequestId))
            return Block("ClientRequestIdRequired", "A unique attendance request id is required.", "Retry");
        if (request.ClientRequestId.Trim().Length > 100)
            return Block("ClientRequestIdRequired", "The attendance request id is too long.", "Retry");
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Block("DeviceIdRequired", "Device id is required for attendance.", "Retry");
        if (string.IsNullOrWhiteSpace(request.NetworkType))
            return Block("NetworkTypeRequired", "Network type is required for attendance.", "Retry");
        if (string.IsNullOrWhiteSpace(request.AppVersion))
            return Block("AppVersionRequired", "App version is required for attendance.", "Retry");
        if (!request.CameraCaptureConfirmed)
            return Block("CameraConfirmationRequired", "Open the front camera before marking attendance.", "OpenCamera");
        if (!request.BiometricConfirmed)
            return Block("BiometricConfirmationRequired", "Device biometric confirmation is required for attendance.", "AuthenticateBiometric");
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180 || (request.Latitude == 0 && request.Longitude == 0))
            return Block("InvalidLocation", "Valid latitude and longitude are required.", "Retry");
        if (request.AccuracyMeters is < 0 or > 100)
            return Block("LocationAccuracyTooLow", "A precise GPS reading within 100 meters is required.", "RetryLocation");
        if (capturedAt > AttendanceNow().AddMinutes(10))
            return Block("InvalidCapturedAt", "The device attendance time is ahead of the server time.", "CorrectDeviceTime");

        var state = await GetAttendanceStateAsync(db, transaction, clientId, employeeId, capturedAt.Date);
        if (state.ApprovalPending)
            return Block("ApprovalPending", "An attendance request is already waiting for approval.", "WaitForApproval");
        if (state.NextExpectedAction == "Unavailable")
            return Block("AttendanceLocked", $"Attendance is already marked as {state.Status} for this date.", "ContactHR");
        if (action == "CheckIn" && state.NextExpectedAction == "CheckOut")
            return Block("AlreadyCheckedIn", "Check-in is already complete. Use Punch Out.", "CheckOut");
        if (action == "CheckIn" && state.NextExpectedAction == "Completed")
            return Block("AlreadyCheckedOut", "Attendance is already complete for this date.", "RefreshAttendance");
        if (action == "CheckOut" && state.NextExpectedAction == "CheckIn")
            return Block("CheckInRequired", "Complete Punch In before Punch Out.", "CheckIn");
        if (action == "CheckOut" && state.NextExpectedAction == "Completed")
            return Block("AlreadyCheckedOut", "Punch Out is already complete for this date.", "RefreshAttendance");

        var rules = await GetApplicableGeoFenceRulesAsync(db, transaction, employeeId, clientId, capturedAt);
        if (rules.Count == 0)
            return new AttendancePunchValidationResponse
            {
                Allowed = true,
                Status = "NoGeoFenceConfigured",
                Message = "No geo-fence rule is configured for this employee.",
                NextAction = "SubmitPunch",
                NextExpectedAction = state.NextExpectedAction,
                AttendanceDate = capturedAt.Date,
                DeviceAccuracyMeters = Math.Max(0, request.AccuracyMeters)
            };
        var actionRules = rules.Where(rule => action == "CheckIn" ? rule.AllowCheckIn : rule.AllowCheckOut).ToList();
        if (actionRules.Count == 0)
        {
            var blockedRule = rules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id).First();
            var actionLabel = action == "CheckIn" ? "Check-in" : "Check-out";
            return WithRule(Block("ActionNotAllowed", $"{actionLabel} is not allowed under the applicable geo-fence rules.", "Retry"), blockedRule, request);
        }

        var deviceAccuracy = Math.Max(0, request.AccuracyMeters);
        var evaluations = actionRules.Select(rule =>
        {
            var distance = DistanceMeters((double)request.Latitude, (double)request.Longitude, (double)rule.Latitude, (double)rule.Longitude);
            var effectiveRadius = rule.RadiusMeters + rule.GpsToleranceMeters;
            return new GeoFenceEvaluation
            {
                Rule = rule,
                DistanceMeters = distance,
                EffectiveRadiusMeters = effectiveRadius,
                OutsideByMeters = Math.Max(0, distance - effectiveRadius)
            };
        }).ToList();
        var evaluation = evaluations
            .Where(item => item.OutsideByMeters <= 0)
            .OrderBy(item => item.DistanceMeters)
            .ThenBy(item => item.Rule.Priority)
            .FirstOrDefault()
            ?? evaluations.OrderBy(item => item.OutsideByMeters).ThenBy(item => item.DistanceMeters).ThenBy(item => item.Rule.Priority).First();
        var rule = evaluation.Rule;
        var distance = evaluation.DistanceMeters;
        var effectiveRadius = evaluation.EffectiveRadiusMeters;
        var outsideBy = evaluation.OutsideByMeters;
        var response = new AttendancePunchValidationResponse
        {
            Allowed = outsideBy <= 0,
            Status = outsideBy <= 0 ? "InsideFence" : "OutsideFence",
            Message = outsideBy <= 0 ? "Attendance punch allowed." : $"You are {Math.Ceiling(outsideBy)} meters outside the allowed attendance range.",
            NextAction = outsideBy <= 0 ? "SubmitPunch" : "MoveInsideFence",
            NextExpectedAction = state.NextExpectedAction,
            AttendanceDate = capturedAt.Date,
            DistanceMeters = Math.Round((decimal)distance, 2),
            AllowedRadiusMeters = rule.RadiusMeters,
            GpsToleranceMeters = rule.GpsToleranceMeters,
            DeviceAccuracyMeters = deviceAccuracy,
            EffectiveRadiusMeters = effectiveRadius,
            OutsideByMeters = Math.Round((decimal)outsideBy, 2),
            Rule = new AttendancePunchRuleSummary { Id = rule.Id, Name = rule.Name, ScopeType = rule.ScopeType, Strictness = rule.Strictness }
        };
        if (outsideBy <= 0) return response;
        if (rule.Strictness.Equals("Allow with reason", StringComparison.OrdinalIgnoreCase))
        {
            response.Allowed = true;
            response.RequiresReason = true;
            response.Status = "OutsideFenceReasonRequired";
            response.NextAction = "CaptureReason";
            return response;
        }
        if (rule.Strictness.Equals("Allow with approval", StringComparison.OrdinalIgnoreCase))
            return WithRule(Block("ApprovalWorkflowUnavailable", "Outside-fence attendance approval is not available yet. Move inside the configured office range.", "MoveInsideFence"), rule, request);
        return response;
    }

    private static AttendancePunchValidationResponse Block(string status, string message, string nextAction) =>
        new() { Allowed = false, Status = status, Message = message, NextAction = nextAction };

    private static AttendancePunchValidationResponse WithRule(AttendancePunchValidationResponse response, GeoFenceRule rule, ValidateAttendancePunchRequest request)
    {
        response.Rule = new AttendancePunchRuleSummary { Id = rule.Id, Name = rule.Name, ScopeType = rule.ScopeType, Strictness = rule.Strictness };
        response.DeviceAccuracyMeters = Math.Max(0, request.AccuracyMeters);
        return response;
    }

    private static async Task<PunchIdentity?> GetPunchIdentityAsync(MySqlConnection db, System.Data.IDbTransaction? transaction, int employeeId, int? clientId, bool forUpdate)
    {
        var lockClause = forUpdate ? " FOR UPDATE" : "";
        return await db.QueryFirstOrDefaultAsync<PunchIdentity>(@"SELECT e.Id AS EmployeeId, e.ClientId AS ClientId
FROM employees e
JOIN clients c ON c.Id=e.ClientId AND c.IsActive=TRUE
WHERE e.Id=@EmployeeId AND e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId)" + lockClause,
            new { EmployeeId = employeeId, ClientId = clientId }, transaction);
    }

    private static async Task<EssAttendanceTodayState> GetAttendanceStateAsync(MySqlConnection db, System.Data.IDbTransaction? transaction, int clientId, int employeeId, DateTime attendanceDate)
    {
        var daily = await db.QueryFirstOrDefaultAsync<AttendanceDailyStateRow>(@"SELECT status AS Status, payable_value AS PayableValue, check_in_time AS CheckInTime,
check_out_time AS CheckOutTime, total_hours AS TotalHours
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_date=@AttendanceDate
LIMIT 1;", new { ClientId = clientId, EmployeeId = employeeId, AttendanceDate = attendanceDate.Date }, transaction);
        var punches = await db.QuerySingleAsync<AttendancePunchAggregate>(@"SELECT
MIN(CASE WHEN action='CheckIn' AND decision IN ('Accepted','AcceptedWithReason','SubmittedWithReason') THEN captured_at END) AS CheckInAt,
MAX(CASE WHEN action='CheckOut' AND decision IN ('Accepted','AcceptedWithReason','SubmittedWithReason') THEN captured_at END) AS CheckOutAt,
SUM(CASE WHEN decision='PendingApproval' THEN 1 ELSE 0 END) AS ApprovalPendingCount
FROM employee_attendance_punches
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND captured_at>=@DayStart AND captured_at<@DayEnd;",
            new { ClientId = clientId, EmployeeId = employeeId, DayStart = attendanceDate.Date, DayEnd = attendanceDate.Date.AddDays(1) }, transaction);
        var settings = await GetAttendanceSettingsAsync(db, transaction, clientId);
        var checkIn = daily?.CheckInTime ?? punches.CheckInAt?.TimeOfDay;
        var checkOut = daily?.CheckOutTime ?? punches.CheckOutAt?.TimeOfDay;
        var lockedStatus = daily is not null &&
            !string.Equals(daily.Status, "Present", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(daily.Status, "A", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(daily.Status, "Absent", StringComparison.OrdinalIgnoreCase);
        var approvalPending = punches.ApprovalPendingCount > 0;
        var nextExpectedAction = approvalPending
            ? "WaitForApproval"
            : lockedStatus
                ? "Unavailable"
                : checkOut.HasValue
                    ? "Completed"
                    : checkIn.HasValue
                        ? "CheckOut"
                        : "CheckIn";
        var totalHours = daily?.TotalHours ?? 0;
        if (totalHours <= 0 && checkIn.HasValue && checkOut.HasValue)
            totalHours = CalculatePunchHours(checkIn.Value, checkOut.Value);
        return new EssAttendanceTodayState
        {
            AttendanceDate = attendanceDate.Date,
            Status = daily?.Status ?? (checkIn.HasValue ? "Present" : "NotMarked"),
            CheckInTime = checkIn,
            CheckOutTime = checkOut,
            TotalHours = totalHours,
            PayableValue = daily?.PayableValue ?? (checkIn.HasValue ? 1 : 0),
            NextExpectedAction = nextExpectedAction,
            ApprovalPending = approvalPending,
            ShiftCheckInTime = settings.CheckInTime,
            ShiftCheckOutTime = settings.CheckOutTime,
            MinimumHoursForHalfDay = settings.MinimumHoursForHalfDay,
            MinimumHoursForFullDay = settings.MinimumHoursForFullDay,
            MaximumHoursAllowedForFullDay = settings.MaximumHoursAllowedForFullDay
        };
    }

    private static async Task<AttendanceSettings> GetAttendanceSettingsAsync(MySqlConnection db, System.Data.IDbTransaction? transaction, int clientId) =>
        await db.QueryFirstOrDefaultAsync<AttendanceSettings>(@"SELECT id AS Id, client_id AS ClientId, check_in_time AS CheckInTime,
check_out_time AS CheckOutTime, working_hours_calculation AS WorkingHoursCalculation,
minimum_hours_for_half_day AS MinimumHoursForHalfDay, minimum_hours_for_full_day AS MinimumHoursForFullDay,
maximum_hours_allowed_for_full_day AS MaximumHoursAllowedForFullDay
FROM attendance_settings WHERE client_id=@ClientId LIMIT 1;", new { ClientId = clientId }, transaction)
        ?? new AttendanceSettings { ClientId = clientId };

    private static async Task<EssAttendanceTodayState> ProjectAcceptedPunchAsync(MySqlConnection db, MySqlTransaction transaction, int clientId, int employeeId, string action, DateTime capturedAt)
    {
        var attendanceDate = capturedAt.Date;
        if (action == "CheckIn")
        {
            await db.ExecuteAsync(@"INSERT INTO employee_daily_attendance
(client_id, employee_id, attendance_date, status, payable_value, check_in_time, check_out_time, total_hours, remarks)
VALUES (@ClientId, @EmployeeId, @AttendanceDate, 'Present', 1, @CheckInTime, NULL, 0, 'Mobile Punch In')
ON DUPLICATE KEY UPDATE status='Present', payable_value=1,
check_in_time=COALESCE(check_in_time, VALUES(check_in_time)), remarks='Mobile Punch In';",
                new { ClientId = clientId, EmployeeId = employeeId, AttendanceDate = attendanceDate, CheckInTime = capturedAt.TimeOfDay }, transaction);
        }
        else
        {
            var beforeCheckout = await GetAttendanceStateAsync(db, transaction, clientId, employeeId, attendanceDate);
            var checkIn = beforeCheckout.CheckInTime ?? capturedAt.TimeOfDay;
            var totalHours = CalculatePunchHours(checkIn, capturedAt.TimeOfDay);
            var settings = await GetAttendanceSettingsAsync(db, transaction, clientId);
            var maximum = settings.MaximumHoursAllowedForFullDay > 0 ? settings.MaximumHoursAllowedForFullDay : 24;
            var evaluatedHours = Math.Min(totalHours, maximum);
            var payableValue = evaluatedHours >= settings.MinimumHoursForFullDay
                ? 1m
                : evaluatedHours >= settings.MinimumHoursForHalfDay
                    ? 0.5m
                    : 0m;
            var status = payableValue > 0 ? "Present" : "A";
            await db.ExecuteAsync(@"INSERT INTO employee_daily_attendance
(client_id, employee_id, attendance_date, status, payable_value, check_in_time, check_out_time, total_hours, remarks)
VALUES (@ClientId, @EmployeeId, @AttendanceDate, @Status, @PayableValue, @CheckInTime, @CheckOutTime, @TotalHours, 'Mobile Punch Out')
ON DUPLICATE KEY UPDATE status=VALUES(status), payable_value=VALUES(payable_value),
check_in_time=COALESCE(check_in_time, VALUES(check_in_time)), check_out_time=VALUES(check_out_time),
total_hours=VALUES(total_hours), remarks='Mobile Punch Out';",
                new
                {
                    ClientId = clientId,
                    EmployeeId = employeeId,
                    AttendanceDate = attendanceDate,
                    Status = status,
                    PayableValue = payableValue,
                    CheckInTime = checkIn,
                    CheckOutTime = capturedAt.TimeOfDay,
                    TotalHours = totalHours
                }, transaction);
        }
        await RollupMobileAttendanceAsync(db, transaction, clientId, employeeId, attendanceDate);
        return await GetAttendanceStateAsync(db, transaction, clientId, employeeId, attendanceDate);
    }

    private static async Task RollupMobileAttendanceAsync(MySqlConnection db, MySqlTransaction transaction, int clientId, int employeeId, DateTime attendanceDate)
    {
        var policy = await db.QueryFirstOrDefaultAsync<AttendanceCycleRow>(@"SELECT g.attendance_cycle_start_day AS StartDay, g.attendance_cycle_end_day AS EndDay
FROM attendance_group_employees age
JOIN attendance_groups g ON g.id=age.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
WHERE age.employee_id=@EmployeeId
ORDER BY g.id LIMIT 1;", new { ClientId = clientId, EmployeeId = employeeId }, transaction);
        var cycle = ResolveAttendanceCycle(attendanceDate, policy?.StartDay ?? 1, policy?.EndDay ?? DateTime.DaysInMonth(attendanceDate.Year, attendanceDate.Month));
        var summary = await db.QuerySingleAsync<AttendanceMonthlySummary>(@"SELECT COUNT(*) AS WorkingDays,
COALESCE(SUM(CASE WHEN status='Present' THEN payable_value ELSE 0 END),0) AS PresentDays,
COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END),0) AS PayableDays
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_date BETWEEN @CycleStart AND @CycleEnd;",
            new { ClientId = clientId, EmployeeId = employeeId, cycle.CycleStart, cycle.CycleEnd }, transaction);
        var lopDays = Math.Max(0, summary.WorkingDays - summary.PayableDays);
        await db.ExecuteAsync(@"INSERT INTO employee_monthly_attendance
(client_id, employee_id, attendance_month, working_days, present_days, payable_days, lop_days, source_type, remarks)
VALUES (@ClientId, @EmployeeId, @Month, @WorkingDays, @PresentDays, @PayableDays, @LopDays, 'Date-wise', 'Rolled up from mobile attendance')
ON DUPLICATE KEY UPDATE
working_days=IF(source_type='Date-wise',VALUES(working_days),working_days),
present_days=IF(source_type='Date-wise',VALUES(present_days),present_days),
payable_days=IF(source_type='Date-wise',VALUES(payable_days),payable_days),
lop_days=IF(source_type='Date-wise',VALUES(lop_days),lop_days),
remarks=IF(source_type='Date-wise',VALUES(remarks),remarks);",
            new
            {
                ClientId = clientId,
                EmployeeId = employeeId,
                cycle.Month,
                summary.WorkingDays,
                summary.PresentDays,
                summary.PayableDays,
                LopDays = lopDays
            }, transaction);
    }

    private static async Task RollupApprovedLeaveAttendanceAsync(MySqlConnection db, MySqlTransaction transaction, int clientId, int employeeId, AttendanceCycleRange cycle)
    {
        var summary = await db.QuerySingleAsync<AttendanceMonthlySummary>(@"SELECT COUNT(*) AS WorkingDays,
COALESCE(SUM(CASE WHEN status='Present' THEN payable_value ELSE 0 END),0) AS PresentDays,
COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END),0) AS PayableDays
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_date BETWEEN @CycleStart AND @CycleEnd;",
            new { ClientId = clientId, EmployeeId = employeeId, cycle.CycleStart, cycle.CycleEnd }, transaction);
        var lopDays = Math.Max(0, summary.WorkingDays - summary.PayableDays);
        await db.ExecuteAsync(@"INSERT INTO employee_monthly_attendance
(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks)
VALUES (@ClientId,@EmployeeId,@Month,@WorkingDays,@PresentDays,@PayableDays,@LopDays,'Date-wise','Rolled up from approved leave and regularization')
ON DUPLICATE KEY UPDATE
working_days=VALUES(working_days),present_days=VALUES(present_days),payable_days=VALUES(payable_days),
lop_days=VALUES(lop_days),source_type='Date-wise',remarks=VALUES(remarks);",
            new
            {
                ClientId = clientId,
                EmployeeId = employeeId,
                cycle.Month,
                summary.WorkingDays,
                summary.PresentDays,
                summary.PayableDays,
                LopDays = lopDays
            }, transaction);
    }

    private static AttendanceCycleRange ResolveAttendanceCycle(DateTime attendanceDate, int startDay, int endDay)
    {
        startDay = Math.Clamp(startDay, 1, 31);
        endDay = Math.Clamp(endDay, 1, 31);
        DateTime start;
        DateTime end;
        if (startDay <= endDay)
        {
            start = DateWithClampedDay(attendanceDate.Year, attendanceDate.Month, startDay);
            end = DateWithClampedDay(attendanceDate.Year, attendanceDate.Month, endDay);
        }
        else if (attendanceDate.Day <= endDay)
        {
            var previous = attendanceDate.AddMonths(-1);
            start = DateWithClampedDay(previous.Year, previous.Month, startDay);
            end = DateWithClampedDay(attendanceDate.Year, attendanceDate.Month, endDay);
        }
        else
        {
            var next = attendanceDate.AddMonths(1);
            start = DateWithClampedDay(attendanceDate.Year, attendanceDate.Month, startDay);
            end = DateWithClampedDay(next.Year, next.Month, endDay);
        }
        return new AttendanceCycleRange(start, end, end.ToString("yyyy-MM"));
    }

    private static DateTime DateWithClampedDay(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    private static decimal CalculatePunchHours(TimeSpan checkIn, TimeSpan checkOut)
    {
        var minutes = (decimal)(checkOut - checkIn).TotalMinutes;
        if (minutes < 0) minutes += 24 * 60;
        return Math.Clamp(Math.Round(minutes / 60, 2), 0, 24);
    }

    private static DateTime ResolveAttendanceDateTime(DateTime? capturedAt)
    {
        var value = capturedAt ?? DateTime.UtcNow;
        if (value.Kind == DateTimeKind.Unspecified) return value;
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(utc, AttendanceTimeZone);
    }

    private static DateTime AttendanceNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AttendanceTimeZone);

    private static TimeZoneInfo ResolveAttendanceTimeZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.Utc;
    }

    private static async Task<List<GeoFenceRule>> GetApplicableGeoFenceRulesAsync(MySqlConnection db, System.Data.IDbTransaction? transaction, int employeeId, int clientId, DateTime onDate)
    {
        var rows = (await db.QueryAsync<GeoFenceRule>(@"SELECT r.id AS Id, r.client_id AS ClientId, r.name AS Name, r.scope_type AS ScopeType, r.work_location_id AS WorkLocationId,
r.latitude AS Latitude, r.longitude AS Longitude, r.radius_meters AS RadiusMeters, r.gps_tolerance_meters AS GpsToleranceMeters,
r.strictness AS Strictness, r.allow_check_in AS AllowCheckIn, r.allow_check_out AS AllowCheckOut, r.effective_from AS EffectiveFrom, r.effective_to AS EffectiveTo,
r.is_active AS IsActive, r.priority AS Priority
FROM attendance_geo_fence_rules r
LEFT JOIN employees target_employee ON target_employee.Id=@EmployeeId AND target_employee.ClientId=r.client_id
WHERE r.client_id=@ClientId AND r.is_active=TRUE AND r.effective_from <= @Date AND (r.effective_to IS NULL OR r.effective_to >= @Date)
AND (
    (r.scope_type='Employee' AND EXISTS (
        SELECT 1 FROM attendance_geo_fence_rule_employees employee_rule
        WHERE employee_rule.geo_fence_rule_id=r.id AND employee_rule.employee_id=@EmployeeId
    ))
    OR (r.scope_type='Work Location' AND r.work_location_id=target_employee.WorkLocationId AND (
        NOT EXISTS (
            SELECT 1 FROM attendance_geo_fence_rule_employees location_rule
            WHERE location_rule.geo_fence_rule_id=r.id
        )
        OR EXISTS (
            SELECT 1 FROM attendance_geo_fence_rule_employees location_employee_rule
            WHERE location_employee_rule.geo_fence_rule_id=r.id AND location_employee_rule.employee_id=@EmployeeId
        )
    ))
    OR r.scope_type='Client Default'
)
GROUP BY r.id
ORDER BY CASE r.scope_type WHEN 'Employee' THEN 1 WHEN 'Work Location' THEN 2 ELSE 3 END, r.priority, r.id;", new { EmployeeId = employeeId, ClientId = clientId, Date = onDate.Date }, transaction)).ToList();
        if (rows.Count == 0) return [];
        var highestScopeRank = GeoFenceScopeRank(rows[0].ScopeType);
        return rows.Where(rule => GeoFenceScopeRank(rule.ScopeType) == highestScopeRank).ToList();
    }

    private static int GeoFenceScopeRank(string scopeType) =>
        scopeType.Equals("Employee", StringComparison.OrdinalIgnoreCase) ? 1 :
        scopeType.Equals("Work Location", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    private static string CleanPunchAction(string? action) =>
        string.Equals(action, "CheckIn", StringComparison.OrdinalIgnoreCase) ? "CheckIn" :
        string.Equals(action, "CheckOut", StringComparison.OrdinalIgnoreCase) ? "CheckOut" : "";

    private static readonly TimeZoneInfo AttendanceTimeZone = ResolveAttendanceTimeZone();
    private sealed class PunchIdentity { public int EmployeeId { get; set; } public int ClientId { get; set; } }
    private sealed class ExistingAttendancePunch { public long Id { get; set; } public string Action { get; set; } = ""; public DateTime CapturedAt { get; set; } public string Decision { get; set; } = "Accepted"; }
    private sealed class AttendanceDailyStateRow { public string Status { get; set; } = ""; public decimal PayableValue { get; set; } public TimeSpan? CheckInTime { get; set; } public TimeSpan? CheckOutTime { get; set; } public decimal TotalHours { get; set; } }
    private sealed class AttendancePunchAggregate { public DateTime? CheckInAt { get; set; } public DateTime? CheckOutAt { get; set; } public int ApprovalPendingCount { get; set; } }
    private sealed class GeoFenceEvaluation { public GeoFenceRule Rule { get; set; } = new(); public double DistanceMeters { get; set; } public int EffectiveRadiusMeters { get; set; } public double OutsideByMeters { get; set; } }
    private sealed class AttendanceCycleRow { public int StartDay { get; set; } public int EndDay { get; set; } }
    private sealed class AttendanceMonthlySummary { public decimal WorkingDays { get; set; } public decimal PresentDays { get; set; } public decimal PayableDays { get; set; } }
    private sealed record AttendanceCycleRange(DateTime CycleStart, DateTime CycleEnd, string Month);

    private static async Task EnsureTravelTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS ess_travel_requests (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
RequestNumber VARCHAR(40) NOT NULL,
RequestDate DATE NOT NULL,
EmployeeId INT NOT NULL,
ClientId INT NOT NULL,
Department VARCHAR(120) NOT NULL DEFAULT '',
Designation VARCHAR(120) NOT NULL DEFAULT '',
ReportingManagerId INT NULL,
Purpose VARCHAR(500) NOT NULL DEFAULT '',
Customer VARCHAR(180) NOT NULL DEFAULT '',
Project VARCHAR(180) NOT NULL DEFAULT '',
CostCenter VARCHAR(120) NOT NULL DEFAULT '',
TravelScope VARCHAR(40) NOT NULL DEFAULT 'Domestic',
TravelType VARCHAR(80) NOT NULL DEFAULT 'Official',
Priority VARCHAR(40) NOT NULL DEFAULT 'Normal',
FromLocation VARCHAR(180) NOT NULL DEFAULT '',
ToLocation VARCHAR(180) NOT NULL DEFAULT '',
StartDateTime DATETIME NOT NULL,
EndDateTime DATETIME NOT NULL,
EstimatedCost DECIMAL(18,2) NOT NULL DEFAULT 0,
PolicyId BIGINT NULL,
TravelMode VARCHAR(80) NOT NULL DEFAULT '',
AccommodationRequired BOOLEAN NOT NULL DEFAULT FALSE,
LocalConveyanceRequired BOOLEAN NOT NULL DEFAULT FALSE,
AdvanceRequired BOOLEAN NOT NULL DEFAULT FALSE,
AdvanceAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
Remarks TEXT NULL,
Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
PolicyValidationJson JSON NULL,
SubmittedAt DATETIME NULL,
CancellationReason TEXT NULL,
CancellationDate DATETIME NULL,
CancellationStatus VARCHAR(40) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_travel_request_number (RequestNumber),
INDEX IX_ess_travel_employee_status (EmployeeId, Status, StartDateTime),
INDEX IX_ess_travel_client_status (ClientId, Status, StartDateTime)
);
CREATE TABLE IF NOT EXISTS ess_travel_request_audit (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
RequestId BIGINT NOT NULL,
Action VARCHAR(80) NOT NULL,
Comment VARCHAR(1000) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
INDEX IX_ess_travel_audit_request (RequestId, CreatedAt)
);
CREATE TABLE IF NOT EXISTS ess_travel_request_legs (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
RequestId BIGINT NOT NULL,
SequenceNo INT NOT NULL,
FromLocation VARCHAR(180) NOT NULL DEFAULT '',
ToLocation VARCHAR(180) NOT NULL DEFAULT '',
StartDateTime DATETIME NULL,
EndDateTime DATETIME NULL,
TravelMode VARCHAR(80) NOT NULL DEFAULT '',
TravelClass VARCHAR(80) NOT NULL DEFAULT '',
BookingAction VARCHAR(40) NOT NULL DEFAULT 'Book by myself',
Remarks VARCHAR(500) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_travel_leg_sequence (RequestId, SequenceNo),
INDEX IX_ess_travel_leg_request (RequestId),
INDEX IX_ess_travel_leg_route (FromLocation, ToLocation),
INDEX IX_ess_travel_leg_mode (TravelMode)
);
CREATE TABLE IF NOT EXISTS ess_travel_request_accommodation (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
RequestId BIGINT NOT NULL,
SequenceNo INT NOT NULL,
City VARCHAR(180) NOT NULL DEFAULT '',
CheckInDateTime DATETIME NULL,
CheckOutDateTime DATETIME NULL,
Occupancy VARCHAR(80) NOT NULL DEFAULT '',
RoomPreference VARCHAR(120) NOT NULL DEFAULT '',
BookingAction VARCHAR(40) NOT NULL DEFAULT 'Book by myself',
Remarks VARCHAR(500) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_travel_accommodation_sequence (RequestId, SequenceNo),
INDEX IX_ess_travel_accommodation_request (RequestId),
INDEX IX_ess_travel_accommodation_city (City)
);
CREATE TABLE IF NOT EXISTS ess_travel_request_local_travel (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
RequestId BIGINT NOT NULL,
SequenceNo INT NOT NULL,
City VARCHAR(180) NOT NULL DEFAULT '',
TravelDateTime DATETIME NULL,
FromLocation VARCHAR(180) NOT NULL DEFAULT '',
ToLocation VARCHAR(180) NOT NULL DEFAULT '',
TravelMode VARCHAR(80) NOT NULL DEFAULT '',
BookingAction VARCHAR(40) NOT NULL DEFAULT 'Book by myself',
Remarks VARCHAR(500) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_travel_local_sequence (RequestId, SequenceNo),
INDEX IX_ess_travel_local_request (RequestId),
INDEX IX_ess_travel_local_city (City),
INDEX IX_ess_travel_local_mode (TravelMode)
);
CREATE TABLE IF NOT EXISTS ess_travel_advances (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
TravelRequestId BIGINT NOT NULL,
EmployeeId INT NOT NULL,
ClientId INT NOT NULL,
RequestedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
ApprovedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
PaidAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
SettledAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
RecoverableAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
PaymentMode VARCHAR(80) NOT NULL DEFAULT '',
PaymentReference VARCHAR(160) NOT NULL DEFAULT '',
PaidDate DATE NULL,
DueDate DATE NULL,
Status VARCHAR(40) NOT NULL DEFAULT 'Approved',
Remarks VARCHAR(1000) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_travel_advance_request (TravelRequestId),
INDEX IX_ess_travel_advance_status (ClientId, Status, DueDate),
INDEX IX_ess_travel_advance_employee (EmployeeId, Status)
);
CREATE TABLE IF NOT EXISTS ess_travel_advance_audit (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
AdvanceId BIGINT NOT NULL,
Action VARCHAR(80) NOT NULL,
Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
Comment VARCHAR(1000) NOT NULL DEFAULT '',
CreatedBy INT NULL,
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
INDEX IX_ess_travel_advance_audit (AdvanceId, CreatedAt)
);");
        await EnsureColumnAsync(db, "ess_travel_request_legs", "BookingAction", "VARCHAR(40) NOT NULL DEFAULT 'Book by myself' AFTER TravelClass");
        await EnsureColumnAsync(db, "ess_travel_request_accommodation", "BookingAction", "VARCHAR(40) NOT NULL DEFAULT 'Book by myself' AFTER RoomPreference");
        await EnsureColumnAsync(db, "ess_travel_request_local_travel", "BookingAction", "VARCHAR(40) NOT NULL DEFAULT 'Book by myself' AFTER TravelMode");
        await MigrateLegacyTravelLegsAsync(db);
        await BackfillTravelAdvancesAsync(db);
    }

    private static async Task EnsureExpenseClaimTablesAsync(MySqlConnection db)
    {
        await EnsureTravelTablesAsync(db);
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS ess_expense_claims (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
ClaimNumber VARCHAR(40) NOT NULL,
ClaimDate DATE NOT NULL,
EmployeeId INT NOT NULL,
ClientId INT NOT NULL,
Department VARCHAR(120) NOT NULL DEFAULT '',
Designation VARCHAR(120) NOT NULL DEFAULT '',
TravelRequestId BIGINT NULL,
ExpenseType VARCHAR(120) NOT NULL DEFAULT '',
Purpose VARCHAR(500) NOT NULL DEFAULT '',
Customer VARCHAR(180) NOT NULL DEFAULT '',
Project VARCHAR(180) NOT NULL DEFAULT '',
CostCenter VARCHAR(120) NOT NULL DEFAULT '',
Currency VARCHAR(12) NOT NULL DEFAULT 'INR',
TotalClaimAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
TotalApprovedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
TotalGstAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
PayrollStatus VARCHAR(40) NOT NULL DEFAULT 'Not Ready',
PayrollRunId INT NULL,
ReimbursementComponentCode VARCHAR(80) NOT NULL DEFAULT 'REIMBURSEMENT',
Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
PolicyValidationJson JSON NULL,
Remarks TEXT NULL,
SubmittedAt DATETIME NULL,
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_expense_claim_number (ClaimNumber),
INDEX IX_ess_expense_employee_status (EmployeeId,Status,ClaimDate),
INDEX IX_ess_expense_client_status (ClientId,Status,ClaimDate),
INDEX IX_ess_expense_travel_request (TravelRequestId)
);
CREATE TABLE IF NOT EXISTS ess_expense_claim_lines (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
ClaimId BIGINT NOT NULL,
ExpenseDate DATE NOT NULL,
CategoryId BIGINT NOT NULL,
CategoryCode VARCHAR(80) NOT NULL DEFAULT '',
CategoryName VARCHAR(180) NOT NULL DEFAULT '',
SubCategory VARCHAR(180) NOT NULL DEFAULT '',
VendorName VARCHAR(180) NOT NULL DEFAULT '',
BillNumber VARCHAR(120) NOT NULL DEFAULT '',
InvoiceNumber VARCHAR(120) NOT NULL DEFAULT '',
Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
Currency VARCHAR(12) NOT NULL DEFAULT 'INR',
ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
GstAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
ApprovedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
CostCenter VARCHAR(120) NOT NULL DEFAULT '',
Project VARCHAR(180) NOT NULL DEFAULT '',
Customer VARCHAR(180) NOT NULL DEFAULT '',
Location VARCHAR(180) NOT NULL DEFAULT '',
PaymentMethod VARCHAR(80) NOT NULL DEFAULT 'Employee Paid',
ReceiptAttached BOOLEAN NOT NULL DEFAULT FALSE,
ReceiptFileName VARCHAR(260) NOT NULL DEFAULT '',
Description VARCHAR(1000) NOT NULL DEFAULT '',
Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
ValidationJson JSON NULL,
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
INDEX IX_ess_expense_line_claim (ClaimId),
INDEX IX_ess_expense_line_category (CategoryId,ExpenseDate)
);
CREATE TABLE IF NOT EXISTS ess_expense_claim_audit (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
ClaimId BIGINT NOT NULL,
Action VARCHAR(80) NOT NULL,
Comment VARCHAR(1000) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
INDEX IX_ess_expense_audit_claim (ClaimId,CreatedAt)
);
CREATE TABLE IF NOT EXISTS ess_expense_claim_attachments (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
ClaimId BIGINT NOT NULL,
LineId BIGINT NULL,
FileName VARCHAR(260) NOT NULL DEFAULT '',
ContentType VARCHAR(120) NOT NULL DEFAULT '',
StoragePath VARCHAR(500) NOT NULL DEFAULT '',
UploadedBy INT NULL,
UploadedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
INDEX IX_ess_expense_attachment_claim (ClaimId,LineId)
);
CREATE TABLE IF NOT EXISTS payroll_reimbursement_queue (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
ClientId INT NOT NULL,
EmployeeId INT NOT NULL,
ExpenseClaimId BIGINT NOT NULL,
ExpenseLineId BIGINT NOT NULL,
ComponentCode VARCHAR(80) NOT NULL,
Amount DECIMAL(18,2) NOT NULL,
Taxable BOOLEAN NOT NULL DEFAULT FALSE,
Status VARCHAR(40) NOT NULL DEFAULT 'Pending',
PayrollRunId INT NULL,
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_payroll_reimb_expense_line (ExpenseLineId),
INDEX IX_payroll_reimb_employee_status (ClientId,EmployeeId,Status)
);");
        await EnsureColumnAsync(db, "ess_expense_claims", "ExpenseType", "VARCHAR(120) NOT NULL DEFAULT '' AFTER TravelRequestId");
    }

    private static string TravelRequestSelect(string where) => $@"SELECT r.*, CONCAT(e.FirstName,' ',e.LastName) EmployeeName, COALESCE(p.PolicyName,'') PolicyName, COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName,' ',m.LastName), '') ReportingManager
FROM ess_travel_requests r
JOIN employees e ON e.Id=r.EmployeeId
LEFT JOIN travel_policies p ON p.Id=r.PolicyId
LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId
LEFT JOIN employees m ON m.Id=e.ReportingManagerId
WHERE {where}";

    private static string ExpenseClaimSelect(string where) => $@"SELECT c.*,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,COALESCE(t.RequestNumber,'') TravelRequestNumber
FROM ess_expense_claims c
JOIN employees e ON e.Id=c.EmployeeId
LEFT JOIN ess_travel_requests t ON t.Id=c.TravelRequestId
WHERE {where}";

    private static async Task AttachExpenseLinesAsync(MySqlConnection db, List<EssExpenseClaim> claims)
    {
        if (claims.Count == 0) return;
        var ids = claims.Select(item => item.Id).ToArray();
        var rows = (await db.QueryAsync<EssExpenseClaimLine>(@"SELECT Id,ClaimId,ExpenseDate,CategoryId,CategoryCode,CategoryName,SubCategory,VendorName,BillNumber,InvoiceNumber,Amount,Currency,ExchangeRate,GstAmount,ApprovedAmount,CostCenter,Project,Customer,Location,PaymentMethod,ReceiptAttached,ReceiptFileName,Description,Status,ValidationJson
FROM ess_expense_claim_lines WHERE ClaimId IN @Ids ORDER BY ClaimId,ExpenseDate,Id", new { Ids = ids })).ToList()
            .GroupBy(item => item.ClaimId)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var claim in claims)
        {
            claim.Lines = rows.TryGetValue(claim.Id, out var lines) ? lines : [];
            foreach (var line in claim.Lines) HydrateExpenseEntitlement(line);
        }
    }

    private static List<EssExpenseClaimLine> NormalizeExpenseLines(SaveEssExpenseClaim claim) =>
        (claim.Lines ?? [])
            .Select(line => new EssExpenseClaimLine
            {
                Id = line.Id,
                ExpenseDate = line.ExpenseDate == default ? DateTime.Today : line.ExpenseDate.Date,
                CategoryId = line.CategoryId,
                CategoryCode = line.CategoryCode?.Trim() ?? "",
                CategoryName = line.CategoryName?.Trim() ?? "",
                SubCategory = line.SubCategory?.Trim() ?? "",
                VendorName = line.VendorName?.Trim() ?? "",
                BillNumber = line.BillNumber?.Trim() ?? "",
                InvoiceNumber = line.InvoiceNumber?.Trim() ?? "",
                Amount = Math.Max(0, line.Amount),
                Currency = CleanText(line.Currency, claim.Currency, "INR").Trim().ToUpperInvariant(),
                ExchangeRate = line.ExchangeRate <= 0 ? 1 : line.ExchangeRate,
                GstAmount = Math.Max(0, line.GstAmount),
                ApprovedAmount = Math.Max(0, line.ApprovedAmount),
                CostCenter = CleanText(line.CostCenter, claim.CostCenter).Trim(),
                Project = CleanText(line.Project, claim.Project).Trim(),
                Customer = CleanText(line.Customer, claim.Customer).Trim(),
                Location = line.Location?.Trim() ?? "",
                PaymentMethod = CleanText(line.PaymentMethod, "Employee Paid").Trim(),
                ReceiptAttached = line.ReceiptAttached || !string.IsNullOrWhiteSpace(line.ReceiptFileName),
                ReceiptFileName = line.ReceiptFileName?.Trim() ?? "",
                Description = line.Description?.Trim() ?? "",
                Status = string.IsNullOrWhiteSpace(line.Status) ? "Draft" : line.Status.Trim(),
                ValidationJson = string.IsNullOrWhiteSpace(line.ValidationJson) ? "[]" : line.ValidationJson,
                CityCategory = line.CityCategory?.Trim() ?? "",
                DistanceKm = Math.Max(0, line.DistanceKm),
                DutyHours = Math.Max(0, line.DutyHours),
                LodgingClaimed = line.LodgingClaimed,
                LodgingIncludesFood = line.LodgingIncludesFood,
                AlternativeStay = line.AlternativeStay,
                OvernightStay = line.OvernightStay,
                EntitlementLabel = line.EntitlementLabel?.Trim() ?? "",
                EntitlementMessage = line.EntitlementMessage?.Trim() ?? ""
            })
            .Where(line => line.CategoryId > 0 || line.Amount > 0 || !string.IsNullOrWhiteSpace(line.Description) || !string.IsNullOrWhiteSpace(line.VendorName))
            .ToList();

    private static async Task<List<EssTravelValidationResult>> ValidateExpenseClaimAsync(MySqlConnection db, int employeeId, int? clientId, SaveEssExpenseClaim claim, bool submit, List<EssExpenseClaimLine>? normalizedLines = null)
    {
        var messages = new List<EssTravelValidationResult>();
        void Block(string message, string rule = "Mandatory") => messages.Add(new EssTravelValidationResult { Severity = "Block", Behavior = "Block", RuleName = rule, Message = message });
        void Warn(string message, string rule = "Policy") => messages.Add(new EssTravelValidationResult { Severity = "Warning", Behavior = "Approval Override", RuleName = rule, Message = message });
        var resolvedClientId = clientId ?? await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId }) ?? 0;
        var lines = normalizedLines ?? NormalizeExpenseLines(claim);
        if (submit && lines.Count == 0) Block("At least one expense line is required.");
        if (submit && string.IsNullOrWhiteSpace(claim.Purpose)) Block("Purpose is required.");
        if (submit && string.IsNullOrWhiteSpace(claim.ExpenseType)) Block("Expense type is required.");
        var policy = await ResolveTravelPolicyAsync(db, employeeId, clientId);
        if (submit && policy is null) Block("No active travel and expense policy is assigned to your profile.", "Policy");
        var expenseType = (claim.ExpenseType ?? "").Trim();
        if (submit && expenseType.Contains("Travel", StringComparison.OrdinalIgnoreCase) && claim.TravelRequestId is null) Block("Travel Expense must be linked with an approved or pending travel request.", "Travel Link");
        if (claim.TravelRequestId is > 0)
        {
            var travel = await db.QueryFirstOrDefaultAsync<EssExpenseTravelOption>(@"SELECT Id,RequestNumber,Purpose,Customer,Project,CostCenter,StartDateTime,EndDateTime,TravelMode,AccommodationRequired,LocalConveyanceRequired
FROM ess_travel_requests WHERE Id=@TravelRequestId AND EmployeeId=@EmployeeId AND ClientId=@ClientId AND Status IN ('Approved','Pending Approval')", new { claim.TravelRequestId, EmployeeId = employeeId, ClientId = resolvedClientId });
            if (travel is null) Block("Linked travel request is not valid for this employee.", "Travel Link");
            else
            {
                foreach (var pair in lines.Select((line, index) => new { line, index }))
                    if (pair.line.ExpenseDate.Date < travel.StartDateTime.Date || pair.line.ExpenseDate.Date > travel.EndDateTime.Date)
                        Block($"Line {pair.index + 1}: expense date must be within the linked travel period.", "Travel Date");
            }
        }
        var categoryIds = lines.Where(line => line.CategoryId > 0).Select(line => line.CategoryId).Distinct().ToArray();
        var categories = categoryIds.Length == 0 ? new Dictionary<long, EssExpenseCategoryOption>() : (await db.QueryAsync<EssExpenseCategoryOption>(@"SELECT c.Id,cs.ClientId,h.Id ParentId,h.HeaderName ParentName,h.HeaderName ExpenseType,FALSE IsClaimHeader,c.CategoryCode,c.CategoryName,
cs.ReceiptMandatory,cs.GstApplicable,cs.DailyLimit,cs.MaximumClaim,cs.RequiresFinanceApproval,cs.RequiresManagerApproval
FROM expense_categories c
JOIN expense_headers h ON h.Id=c.HeaderId
JOIN client_expense_header_settings hs ON hs.HeaderId=h.Id AND hs.ClientId=@ClientId
JOIN client_expense_category_settings cs ON cs.CategoryId=c.Id AND cs.ClientId=@ClientId
WHERE c.Id IN @Ids AND h.IsActive=TRUE AND c.IsActive=TRUE AND hs.IsEnabled=TRUE AND cs.IsEnabled=TRUE
  AND (hs.EffectiveFrom IS NULL OR hs.EffectiveFrom<=CURRENT_DATE) AND (hs.EffectiveTo IS NULL OR hs.EffectiveTo>=CURRENT_DATE)
  AND (cs.EffectiveFrom IS NULL OR cs.EffectiveFrom<=CURRENT_DATE) AND (cs.EffectiveTo IS NULL OR cs.EffectiveTo>=CURRENT_DATE)", new { Ids = categoryIds, ClientId = resolvedClientId })).ToDictionary(item => item.Id);
        var policyRules = policy is null
            ? []
            : (await db.QueryAsync<TravelPolicyRule>("SELECT * FROM travel_policy_rules WHERE PolicyId=@PolicyId AND IsActive=TRUE ORDER BY Id", new { PolicyId = policy.Value.Id })).ToList();
        foreach (var pair in lines.Select((line, index) => new { line, index }))
        {
            var line = pair.line;
            if (submit && line.CategoryId <= 0) Block($"Line {pair.index + 1}: category is required.");
            if (submit && line.Amount <= 0) Block($"Line {pair.index + 1}: amount must be greater than zero.");
            if (line.ExpenseDate.Date > DateTime.Today) Block($"Line {pair.index + 1}: future expense date is not allowed.");
            if (line.CategoryId > 0 && !categories.ContainsKey(line.CategoryId)) Block($"Line {pair.index + 1}: selected category is not active.");
            else if (line.CategoryId > 0)
            {
                var category = categories[line.CategoryId];
                if (!string.IsNullOrWhiteSpace(expenseType) && !category.ExpenseType.Equals(expenseType, StringComparison.OrdinalIgnoreCase)) Block($"Line {pair.index + 1}: {category.CategoryName} is not allowed under {expenseType}.", category.CategoryName);
                if (claim.TravelRequestId is > 0 && expenseType.Contains("Travel", StringComparison.OrdinalIgnoreCase))
                {
                    var allowedCodes = await AllowedTravelExpenseCodesAsync(db, claim.TravelRequestId.Value);
                    if (allowedCodes.Count > 0 && !allowedCodes.Contains(category.CategoryCode, StringComparer.OrdinalIgnoreCase))
                        Block($"Line {pair.index + 1}: {category.CategoryName} is not allowed for the linked travel request.", "Travel Category");
                }
                if (category.ReceiptMandatory && !line.ReceiptAttached) Block($"Line {pair.index + 1}: receipt is mandatory for {category.CategoryName}.", category.CategoryName);
                if (category.MaximumClaim.HasValue && line.Amount > category.MaximumClaim.Value) Warn($"Line {pair.index + 1}: amount exceeds maximum claim limit {category.MaximumClaim.Value:N2} for {category.CategoryName}.", category.CategoryName);
                if (category.DailyLimit.HasValue)
                {
                    var dayTotal = lines.Where(item => item.CategoryId == line.CategoryId && item.ExpenseDate.Date == line.ExpenseDate.Date).Sum(item => item.Amount);
                    if (dayTotal > category.DailyLimit.Value) Warn($"Line {pair.index + 1}: daily total exceeds limit {category.DailyLimit.Value:N2} for {category.CategoryName}.", category.CategoryName);
                }
                line.CategoryCode = category.CategoryCode;
                line.CategoryName = category.CategoryName;
                ApplyExpenseEntitlement(line, category, policyRules, pair.index, submit, Block, Warn);
            }
        }
        return messages;
    }

    private static void ApplyExpenseEntitlement(
        EssExpenseClaimLine line,
        EssExpenseCategoryOption category,
        List<TravelPolicyRule> rules,
        int lineIndex,
        bool submit,
        Action<string, string> block,
        Action<string, string> warn)
    {
        var identity = $"{category.CategoryCode} {category.CategoryName}".ToUpperInvariant();
        var cityCategory = ResolveExpenseCityCategory(line);
        var approved = Math.Max(0, line.Amount);
        var label = "Actual claim amount";
        var message = "Eligible at actual claimed amount, subject to approval.";

        if (category.MaximumClaim.HasValue) approved = Math.Min(approved, category.MaximumClaim.Value);

        if (IsHaltingAllowance(identity))
        {
            var rule = FindCityRule(rules, "Per Diem", cityCategory);
            if (rule is null)
            {
                (submit ? block : warn)($"Line {lineIndex + 1}: {cityCategory} halting allowance is not configured in the applicable policy.", "Halting Allowance");
                approved = 0;
                label = $"{cityCategory} HA not configured";
                message = "Configure a Per Diem rule before submitting this claim.";
            }
            else
            {
                var minimumDistance = JsonDecimal(rule.ConfigJson, "minimumDistanceKm") ?? 50m;
                var minimumHours = JsonDecimal(rule.ConfigJson, "minimumDutyHours") ?? 8m;
                var baseLimit = rule.LimitAmount ?? JsonDecimal(rule.ConfigJson, "fullDay") ?? 0;
                var lodgingPercent = JsonDecimal(rule.ConfigJson, "withLodgingClaimPercent") ?? 75m;
                var foodPercent = JsonDecimal(rule.ConfigJson, "lodgingIncludesFoodPercent") ?? 25m;
                var alternativeStayExtra = JsonDecimal(rule.ConfigJson, "alternativeStayExtraAmount") ?? 200m;
                var eligible = baseLimit;

                if (line.DistanceKm <= minimumDistance || line.DutyHours <= minimumHours)
                {
                    (submit ? block : warn)($"Line {lineIndex + 1}: full-day halting allowance requires travel beyond {minimumDistance:N0} km and duty over {minimumHours:N0} hours.", rule.RuleName);
                    eligible = 0;
                }
                else if (line.LodgingIncludesFood) eligible = baseLimit * foodPercent / 100m;
                else if (line.LodgingClaimed) eligible = baseLimit * lodgingPercent / 100m;

                if (line.AlternativeStay)
                {
                    if (line.OvernightStay) eligible += alternativeStayExtra;
                    else warn($"Line {lineIndex + 1}: alternative-stay allowance requires an overnight stay.", rule.RuleName);
                }

                approved = Math.Min(approved, Math.Max(0, eligible));
                label = $"{cityCategory} HA limit Rs {eligible:N2}";
                message = line.LodgingIncludesFood
                    ? $"25% of base HA applied because lodging includes food{(line.AlternativeStay && line.OvernightStay ? ", plus alternative-stay allowance" : "")}."
                    : line.LodgingClaimed
                        ? $"75% of base HA applied with lodging claim{(line.AlternativeStay && line.OvernightStay ? ", plus alternative-stay allowance" : "")}."
                        : $"Full HA applied{(line.AlternativeStay && line.OvernightStay ? " plus alternative-stay allowance" : "")}.";
            }
        }
        else if (IsLodging(identity))
        {
            var rule = FindCityRule(rules, "Hotel", cityCategory);
            if (rule is null)
            {
                (submit ? block : warn)($"Line {lineIndex + 1}: {cityCategory} lodging limit is not configured in the applicable policy.", "Lodging");
                approved = 0;
                label = $"{cityCategory} lodging not configured";
                message = "Configure a Hotel rule before submitting this claim.";
            }
            else
            {
                var limit = rule.LimitAmount ?? 0;
                approved = Math.Min(approved, limit);
                label = $"{cityCategory} lodging cap Rs {limit:N2}";
                message = "Actual lodging expense excluding taxes is eligible up to the configured cap.";
                if ((rule.ReceiptMandatory || category.ReceiptMandatory) && !line.ReceiptAttached)
                    block($"Line {lineIndex + 1}: lodging bill/receipt is required.", rule.RuleName);
            }
        }
        else if (IsLocalConveyance(identity))
        {
            var localRules = rules.Where(rule => rule.RuleType.Equals("Local Conveyance", StringComparison.OrdinalIgnoreCase)).ToList();
            var rule = line.ReceiptAttached
                ? localRules.FirstOrDefault(item => item.ReceiptMandatory || item.RuleName.Contains("actual", StringComparison.OrdinalIgnoreCase) || item.RuleName.Contains("receipt", StringComparison.OrdinalIgnoreCase))
                : localRules.FirstOrDefault(item => !item.ReceiptMandatory && (item.RuleName.Contains("without", StringComparison.OrdinalIgnoreCase) || JsonDecimal(item.ConfigJson, "ratePerKm").HasValue || JsonDecimal(item.ConfigJson, "mileageRate").HasValue));
            if (!line.ReceiptAttached)
            {
                var rate = rule is null ? null : JsonDecimal(rule.ConfigJson, "ratePerKm") ?? JsonDecimal(rule.ConfigJson, "mileageRate") ?? JsonDecimal(rule.ConfigJson, "mileageRatePerKm");
                if (rule is null || !rate.HasValue)
                {
                    (submit ? block : warn)($"Line {lineIndex + 1}: local travel without receipt has no per-kilometre policy configured.", "Local Conveyance");
                    approved = 0;
                    label = "Mileage rate not configured";
                    message = "Configure a Local Conveyance rate before submitting this claim.";
                }
                else if (line.DistanceKm <= 0)
                {
                    (submit ? block : warn)($"Line {lineIndex + 1}: distance is required for local travel without a receipt.", rule.RuleName);
                    approved = 0;
                    label = $"Rs {rate.Value:N2} per km";
                    message = "Enter the travelled distance to calculate eligibility.";
                }
                else
                {
                    var eligible = line.DistanceKm * rate.Value;
                    approved = Math.Min(approved, eligible);
                    label = $"{line.DistanceKm:N2} km x Rs {rate.Value:N2}";
                    message = $"Mileage entitlement is Rs {eligible:N2}.";
                }
            }
            else
            {
                if (rule?.LimitAmount is > 0) approved = Math.Min(approved, rule.LimitAmount.Value);
                label = "Actual local travel with receipt";
                message = "Receipt-backed local travel is eligible at actual cost, subject to approval.";
            }
        }

        approved = Math.Round(Math.Max(0, approved), 2, MidpointRounding.AwayFromZero);
        line.ApprovedAmount = approved;
        line.CityCategory = cityCategory;
        line.EntitlementLabel = label;
        line.EntitlementMessage = message;
        line.ValidationJson = JsonSerializer.Serialize(new
        {
            calculated = true,
            inputs = new
            {
                cityCategory,
                line.DistanceKm,
                line.DutyHours,
                line.LodgingClaimed,
                line.LodgingIncludesFood,
                line.AlternativeStay,
                line.OvernightStay
            },
            result = new { eligibleAmount = approved, label, message }
        });
    }

    private static void HydrateExpenseEntitlement(EssExpenseClaimLine line)
    {
        if (string.IsNullOrWhiteSpace(line.ValidationJson)) return;
        try
        {
            using var document = JsonDocument.Parse(line.ValidationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            if (document.RootElement.TryGetProperty("inputs", out var inputs))
            {
                line.CityCategory = JsonString(inputs, "cityCategory") ?? line.CityCategory;
                line.DistanceKm = JsonNumber(inputs, "DistanceKm") ?? JsonNumber(inputs, "distanceKm") ?? line.DistanceKm;
                line.DutyHours = JsonNumber(inputs, "DutyHours") ?? JsonNumber(inputs, "dutyHours") ?? line.DutyHours;
                line.LodgingClaimed = JsonBoolean(inputs, "LodgingClaimed") ?? JsonBoolean(inputs, "lodgingClaimed") ?? line.LodgingClaimed;
                line.LodgingIncludesFood = JsonBoolean(inputs, "LodgingIncludesFood") ?? JsonBoolean(inputs, "lodgingIncludesFood") ?? line.LodgingIncludesFood;
                line.AlternativeStay = JsonBoolean(inputs, "AlternativeStay") ?? JsonBoolean(inputs, "alternativeStay") ?? line.AlternativeStay;
                line.OvernightStay = JsonBoolean(inputs, "OvernightStay") ?? JsonBoolean(inputs, "overnightStay") ?? line.OvernightStay;
            }
            if (document.RootElement.TryGetProperty("result", out var result))
            {
                line.EntitlementLabel = JsonString(result, "label") ?? line.EntitlementLabel;
                line.EntitlementMessage = JsonString(result, "message") ?? line.EntitlementMessage;
            }
        }
        catch { }
    }

    private static string ResolveExpenseCityCategory(EssExpenseClaimLine line)
    {
        if (line.CityCategory.Equals("Metro", StringComparison.OrdinalIgnoreCase)) return "Metro";
        if (line.CityCategory.Replace("-", " ").Equals("Non Metro", StringComparison.OrdinalIgnoreCase)) return "Non-Metro";
        var location = line.Location ?? "";
        return new[] { "Delhi", "Mumbai", "Chennai", "Kolkata", "Hyderabad", "Bengaluru", "Bangalore" }
            .Any(city => location.Contains(city, StringComparison.OrdinalIgnoreCase)) ? "Metro" : "Non-Metro";
    }

    private static TravelPolicyRule? FindCityRule(IEnumerable<TravelPolicyRule> rules, string ruleType, string cityCategory) =>
        rules.Where(rule => rule.RuleType.Equals(ruleType, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(rule =>
                rule.AppliesTo.Contains(cityCategory, StringComparison.OrdinalIgnoreCase)
                || rule.RuleName.Contains(cityCategory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(JsonText(rule.EligibilityJson, "cityCategory"), cityCategory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(JsonText(rule.ConfigJson, "cityCategory"), cityCategory, StringComparison.OrdinalIgnoreCase));

    private static bool IsHaltingAllowance(string identity) => identity.Contains("HALTING") || identity.Contains("PER DIEM") || identity.Contains("PER_DIEM") || identity is "HA";
    private static bool IsLodging(string identity) => identity.Contains("HOTEL") || identity.Contains("LODGING");
    private static bool IsLocalConveyance(string identity) => new[] { "LOCAL", "CAB", "TAXI", "MILEAGE", "FUEL", "METRO" }.Any(token => identity.Contains(token));

    private static decimal? JsonDecimal(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return JsonNumber(document.RootElement, property);
        }
        catch { return null; }
    }

    private static string? JsonText(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return JsonString(document.RootElement, property);
        }
        catch { return null; }
    }

    private static string? JsonString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal? JsonNumber(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetDecimal(out var number) ? number : null;
    private static bool? JsonBoolean(JsonElement element, string property) => element.TryGetProperty(property, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? value.GetBoolean() : null;

    private static async Task<HashSet<string>> AllowedTravelExpenseCodesAsync(MySqlConnection db, long travelRequestId)
    {
        var travel = await db.QueryFirstOrDefaultAsync<EssExpenseTravelOption>("SELECT Id,TravelMode,AccommodationRequired,LocalConveyanceRequired FROM ess_travel_requests WHERE Id=@TravelRequestId", new { TravelRequestId = travelRequestId });
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MEALS", "HALTING_ALLOWANCE", "PER_DIEM", "HA" };
        if (travel is null) return codes;
        var mode = (travel.TravelMode ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(mode) || mode.Contains("air") || mode.Contains("flight")) codes.Add("AIR_FARE");
        if (string.IsNullOrWhiteSpace(mode) || mode.Contains("train") || mode.Contains("rail")) codes.Add("TRAIN_FARE");
        if (string.IsNullOrWhiteSpace(mode) || mode.Contains("bus")) codes.Add("BUS_FARE");
        if (mode.Contains("cab") || mode.Contains("taxi")) codes.Add("CAB_TAXI");
        if (mode.Contains("own") || mode.Contains("car")) foreach (var code in new[] { "FUEL", "PARKING", "TOLL" }) codes.Add(code);
        if (travel.AccommodationRequired) foreach (var code in new[] { "HOTEL_STAY", "LODGING" }) codes.Add(code);
        if (travel.LocalConveyanceRequired) foreach (var code in new[] { "CAB_TAXI", "FUEL", "PARKING", "TOLL", "METRO", "LOCAL_CONVEYANCE", "MILEAGE" }) codes.Add(code);
        return codes;
    }

    private static object ToExpenseArgs(SaveEssExpenseClaim request, EssTravelEmployee employee, string claimNumber, List<EssTravelValidationResult> validation, List<EssExpenseClaimLine> lines) => new
    {
        request.Id,
        ClaimNumber = claimNumber,
        employee.EmployeeId,
        employee.ClientId,
        employee.Department,
        employee.Designation,
        request.TravelRequestId,
        ExpenseType = request.ExpenseType.Trim(),
        Purpose = request.Purpose.Trim(),
        Customer = employee.ClientName.Trim(),
        Project = "",
        CostCenter = request.CostCenter.Trim(),
        Currency = CleanText(request.Currency, "INR").Trim().ToUpperInvariant(),
        TotalClaimAmount = lines.Sum(line => line.Amount),
        TotalApprovedAmount = lines.Sum(line => line.ApprovedAmount),
        TotalGstAmount = lines.Sum(line => line.GstAmount),
        Remarks = request.Remarks.Trim(),
        PolicyValidationJson = JsonSerializer.Serialize(validation)
    };

    private static async Task ReplaceExpenseLinesAsync(MySqlConnection db, MySqlTransaction tx, long claimId, List<EssExpenseClaimLine> lines)
    {
        await db.ExecuteAsync("DELETE FROM ess_expense_claim_lines WHERE ClaimId=@ClaimId", new { ClaimId = claimId }, tx);
        foreach (var line in lines)
        {
            await db.ExecuteAsync(@"INSERT INTO ess_expense_claim_lines (ClaimId,ExpenseDate,CategoryId,CategoryCode,CategoryName,SubCategory,VendorName,BillNumber,InvoiceNumber,Amount,Currency,ExchangeRate,GstAmount,ApprovedAmount,CostCenter,Project,Customer,Location,PaymentMethod,ReceiptAttached,ReceiptFileName,Description,Status,ValidationJson)
VALUES (@ClaimId,@ExpenseDate,@CategoryId,@CategoryCode,@CategoryName,@SubCategory,@VendorName,@BillNumber,@InvoiceNumber,@Amount,@Currency,@ExchangeRate,@GstAmount,@ApprovedAmount,@CostCenter,@Project,@Customer,@Location,@PaymentMethod,@ReceiptAttached,@ReceiptFileName,@Description,@Status,@ValidationJson)", new { ClaimId = claimId, line.ExpenseDate, line.CategoryId, line.CategoryCode, line.CategoryName, line.SubCategory, line.VendorName, line.BillNumber, line.InvoiceNumber, line.Amount, line.Currency, line.ExchangeRate, line.GstAmount, line.ApprovedAmount, line.CostCenter, line.Project, line.Customer, line.Location, line.PaymentMethod, line.ReceiptAttached, line.ReceiptFileName, line.Description, line.Status, line.ValidationJson }, tx);
        }
    }

    private static async Task<string> NextExpenseClaimNumberAsync(MySqlConnection db)
    {
        var prefix = $"EXP-{DateTime.Today:yyyyMM}-";
        var next = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(CAST(SUBSTRING(ClaimNumber, 12) AS UNSIGNED)),0)+1 FROM ess_expense_claims WHERE ClaimNumber LIKE @Pattern", new { Pattern = $"{prefix}%" });
        return $"{prefix}{next:0000}";
    }

    private static async Task AuditExpenseAsync(MySqlConnection db, long claimId, string action, string comment) =>
        await db.ExecuteAsync("INSERT INTO ess_expense_claim_audit (ClaimId,Action,Comment) VALUES (@ClaimId,@Action,@Comment)", new { ClaimId = claimId, Action = action, Comment = comment });

    private static EssAdminMaintenanceResult MaintenanceSuccess(string action, string message) => new() { Success = true, Action = action, Message = message };
    private static EssAdminMaintenanceResult MaintenanceFailure(string action, string message) => new() { Success = false, Action = action, Message = message };
    private static string MaintenanceReason(string reason) => string.IsNullOrWhiteSpace(reason) ? "Administrative data maintenance" : reason.Trim();

    private static async Task<string?> ExpenseFinancialDependencyAsync(MySqlConnection db, MySqlTransaction tx, AdminExpenseClaimRow claim)
    {
        if (claim.PayrollRunId.HasValue)
            return $"This claim is already linked to payroll run #{claim.PayrollRunId}. Reverse or delete that pay run before cleaning this claim.";
        var consumedQueue = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payroll_reimbursement_queue
WHERE ExpenseClaimId=@Id AND (PayrollRunId IS NOT NULL OR Status IN ('Applied','Paid'))", new { claim.Id }, tx);
        var consumedAdjustment = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payrolladjustments
WHERE ClientId=@ClientId AND EmployeeId=@EmployeeId AND ReasonCode='EXPENSE_CLAIM'
AND Notes LIKE CONCAT('Expense claim ',@ClaimNumber,' line %') AND PayRunId IS NOT NULL", claim, tx);
        if (consumedQueue > 0 || consumedAdjustment > 0)
            return "This expense has already been consumed by payroll. Reverse or delete the linked pay run before reverting or deleting the claim.";
        if (claim.TravelRequestId.HasValue)
        {
            var settledAdvance = await db.ExecuteScalarAsync<decimal>(@"SELECT COALESCE(SUM(SettledAmount+RecoverableAmount),0)
FROM ess_travel_advances WHERE TravelRequestId=@TravelRequestId", new { claim.TravelRequestId }, tx);
            if (settledAdvance > 0)
                return "This claim has settled a paid travel advance. Reverse the advance settlement before reverting or deleting the claim.";
        }
        return null;
    }

    private static async Task RemovePendingExpensePayrollAsync(MySqlConnection db, MySqlTransaction tx, AdminExpenseClaimRow claim)
    {
        await db.ExecuteAsync("DELETE FROM payroll_reimbursement_queue WHERE ExpenseClaimId=@Id AND PayrollRunId IS NULL", new { claim.Id }, tx);
        await db.ExecuteAsync(@"DELETE FROM payrolladjustments
WHERE ClientId=@ClientId AND EmployeeId=@EmployeeId AND ReasonCode='EXPENSE_CLAIM'
AND Notes LIKE CONCAT('Expense claim ',@ClaimNumber,' line %') AND PayRunId IS NULL", claim, tx);
    }

    private static async Task<string?> TravelFinancialDependencyAsync(MySqlConnection db, MySqlTransaction tx, long requestId)
    {
        var linkedClaims = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ess_expense_claims WHERE TravelRequestId=@RequestId", new { RequestId = requestId }, tx);
        if (linkedClaims > 0)
            return $"This travel request has {linkedClaims} linked expense claim(s). Revert or delete those claims first.";
        var financialAdvance = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ess_travel_advances
WHERE TravelRequestId=@RequestId AND (PaidAmount>0 OR SettledAmount>0 OR RecoverableAmount>0)", new { RequestId = requestId }, tx);
        return financialAdvance > 0
            ? "This travel request has a paid, settled, or recoverable advance. Reverse the financial transaction before reverting or deleting it."
            : null;
    }

    private static async Task RemoveUnpaidTravelAdvanceAsync(MySqlConnection db, MySqlTransaction tx, long requestId)
    {
        await db.ExecuteAsync(@"DELETE a FROM ess_travel_advance_audit a
JOIN ess_travel_advances v ON v.Id=a.AdvanceId
WHERE v.TravelRequestId=@RequestId AND v.PaidAmount=0 AND v.SettledAmount=0 AND v.RecoverableAmount=0", new { RequestId = requestId }, tx);
        await db.ExecuteAsync(@"DELETE FROM ess_travel_advances
WHERE TravelRequestId=@RequestId AND PaidAmount=0 AND SettledAmount=0 AND RecoverableAmount=0", new { RequestId = requestId }, tx);
    }

    private static async Task MarkWorkflowAdminRevertedAsync(MySqlConnection db, MySqlTransaction tx, string resourceType, long resourceId, int actorUserId, string reason)
    {
        var args = new { ResourceType = resourceType, ResourceId = resourceId.ToString(), Actor = actorUserId, Reason = MaintenanceReason(reason) };
        await db.ExecuteAsync(@"INSERT INTO workflowhistory (InstanceId,TaskId,Action,ActorUserId,Comment)
SELECT i.Id,NULL,'Admin Reverted',@Actor,@Reason FROM workflowinstances i
WHERE i.ResourceType=@ResourceType AND i.ResourceId=@ResourceId;
UPDATE workflowtasks t JOIN workflowinstances i ON i.Id=t.InstanceId
SET t.Status='Admin Reverted',t.ActionedAt=UTC_TIMESTAMP(),t.Comment=@Reason
WHERE i.ResourceType=@ResourceType AND i.ResourceId=@ResourceId AND t.Status='Pending';
UPDATE workflowinstances SET Status='Admin Reverted',CompletedAt=UTC_TIMESTAMP()
WHERE ResourceType=@ResourceType AND ResourceId=@ResourceId;
UPDATE ResourceStates SET CurrentState='Draft',WorkflowInstanceId=NULL,ModifiedBy=@Actor,ModifiedOn=CURRENT_TIMESTAMP
WHERE ResourceType=@ResourceType AND ResourceId=@ResourceId;", args, tx);
    }

    private static async Task DeleteWorkflowAsync(MySqlConnection db, MySqlTransaction tx, string resourceType, long resourceId)
    {
        var args = new { ResourceType = resourceType, ResourceId = resourceId.ToString() };
        await db.ExecuteAsync(@"DELETE h FROM workflowhistory h JOIN workflowinstances i ON i.Id=h.InstanceId
WHERE i.ResourceType=@ResourceType AND i.ResourceId=@ResourceId;
DELETE t FROM workflowtasks t JOIN workflowinstances i ON i.Id=t.InstanceId
WHERE i.ResourceType=@ResourceType AND i.ResourceId=@ResourceId;
DELETE FROM workflowinstances WHERE ResourceType=@ResourceType AND ResourceId=@ResourceId;
DELETE FROM ResourceStates WHERE ResourceType=@ResourceType AND ResourceId=@ResourceId;", args, tx);
    }

    private static async Task<(long Id, string Name)?> ResolveTravelPolicyAsync(MySqlConnection db, int employeeId, int? clientId)
    {
        return await db.QueryFirstOrDefaultAsync<(long Id, string Name)?>(@"SELECT p.Id, p.PolicyName Name
FROM employees e
JOIN travel_policy_assignments a ON a.CompanyId=e.ClientId AND a.IsActive=TRUE AND a.EffectiveFrom<=CURRENT_DATE AND (a.EffectiveTo IS NULL OR a.EffectiveTo>=CURRENT_DATE)
JOIN travel_policies p ON p.Id=a.PolicyId AND p.Status='Active' AND p.IsActive=TRUE AND p.EffectiveFrom<=CURRENT_DATE AND (p.EffectiveTo IS NULL OR p.EffectiveTo>=CURRENT_DATE)
WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)
AND (a.BranchId IS NULL OR a.BranchId=e.WorkLocationId)
AND (a.Department='' OR a.Department=e.Department)
AND (a.Grade='' OR a.Grade=e.Grade)
AND (a.Designation='' OR a.Designation=e.Designation)
AND (a.EmployeeId IS NULL OR a.EmployeeId=e.Id)
ORDER BY a.Priority ASC, a.Id DESC
LIMIT 1", new { EmployeeId = employeeId, ClientId = clientId });
    }

    private static async Task<bool> IsTravelExpenseEnabledAsync(MySqlConnection db, int employeeId, int? clientId)
    {
        await EnsureTravelExpenseClientSettingsAsync(db);
        return await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM employees e
JOIN travel_expense_client_settings s ON s.ClientId=e.ClientId
WHERE e.Id=@EmployeeId
  AND (@ClientId IS NULL OR e.ClientId=@ClientId)
  AND e.IsActive=TRUE
  AND s.IsEnabled=TRUE
  AND (s.EffectiveFrom IS NULL OR s.EffectiveFrom<=CURRENT_DATE)
  AND (s.EffectiveTo IS NULL OR s.EffectiveTo>=CURRENT_DATE)", new { EmployeeId = employeeId, ClientId = clientId }) > 0;
    }

    private static async Task<TravelExpenseClientSetting?> GetTravelDeskSettingAsync(MySqlConnection db, int clientId)
    {
        await EnsureTravelExpenseClientSettingsAsync(db);
        return await db.QueryFirstOrDefaultAsync<TravelExpenseClientSetting>(
            "SELECT * FROM travel_expense_client_settings WHERE ClientId=@ClientId LIMIT 1",
            new { ClientId = clientId });
    }

    private static async Task ApplyTravelDeskScopeAsync(MySqlConnection db, int clientId, SaveEssTravelRequest request)
    {
        var setting = await GetTravelDeskSettingAsync(db, clientId);
        if (setting?.ShowTripDetails != true) request.Cities = [];
        if (setting?.ShowAccommodationDetails != true)
        {
            request.AccommodationDetails = [];
            request.AccommodationRequired = false;
        }
        if (setting?.ShowLocalTravelDetails != true)
        {
            request.LocalTravelDetails = [];
            request.LocalConveyanceRequired = false;
        }
        if (setting?.IsTravelDeskEnabled != true)
        {
            foreach (var leg in request.Cities) leg.BookingAction = "";
            foreach (var stay in request.AccommodationDetails) stay.BookingAction = "";
            foreach (var ride in request.LocalTravelDetails) ride.BookingAction = "";
        }
    }

    private static async Task EnsureTravelExpenseClientSettingsAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS travel_expense_client_settings (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
ClientId INT NOT NULL,
IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
IsTravelDeskEnabled BOOLEAN NOT NULL DEFAULT FALSE,
ShowTripDetails BOOLEAN NOT NULL DEFAULT FALSE,
ShowAccommodationDetails BOOLEAN NOT NULL DEFAULT FALSE,
ShowLocalTravelDetails BOOLEAN NOT NULL DEFAULT FALSE,
EffectiveFrom DATE NULL,
EffectiveTo DATE NULL,
Remarks VARCHAR(500) NOT NULL DEFAULT '',
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_TravelExpenseClientSettings_Client (ClientId),
KEY IX_TravelExpenseClientSettings_Enabled (ClientId, IsEnabled)
);
INSERT INTO travel_expense_client_settings (ClientId,IsEnabled)
SELECT Id,FALSE FROM clients WHERE IsActive=TRUE
ON DUPLICATE KEY UPDATE ClientId=ClientId;");
        await EnsureColumnAsync(db, "travel_expense_client_settings", "IsTravelDeskEnabled", "BOOLEAN NOT NULL DEFAULT FALSE AFTER IsEnabled");
        await EnsureColumnAsync(db, "travel_expense_client_settings", "ShowTripDetails", "BOOLEAN NOT NULL DEFAULT FALSE AFTER IsTravelDeskEnabled");
        await EnsureColumnAsync(db, "travel_expense_client_settings", "ShowAccommodationDetails", "BOOLEAN NOT NULL DEFAULT FALSE AFTER ShowTripDetails");
        await EnsureColumnAsync(db, "travel_expense_client_settings", "ShowLocalTravelDetails", "BOOLEAN NOT NULL DEFAULT FALSE AFTER ShowAccommodationDetails");
    }

    private static List<EssTravelValidationResult> ValidateTravelRequest(SaveEssTravelRequest request, bool submit, bool requireTripDetails = true)
    {
        var result = new List<EssTravelValidationResult>();
        void Block(string message) => result.Add(new EssTravelValidationResult { Severity = "Block", Behavior = "Block", Message = message, RuleName = "Mandatory" });
        var legs = NormalizeTravelCities(request);
        if (submit && string.IsNullOrWhiteSpace(request.Purpose)) Block("Purpose of travel is required.");
        if (submit && string.IsNullOrWhiteSpace(request.TravelType)) Block("Travel type is required.");
        if (submit && requireTripDetails && legs.Count == 0) Block("At least one trip detail row is required when Travel Desk is enabled.");
        foreach (var leg in legs.Select((value, index) => new { value, index }))
        {
            if (submit && string.IsNullOrWhiteSpace(leg.value.FromLocation)) Block($"Trip row {leg.index + 1}: From location is required.");
            if (submit && string.IsNullOrWhiteSpace(leg.value.ToLocation)) Block($"Trip row {leg.index + 1}: To location is required.");
            if (submit && string.IsNullOrWhiteSpace(leg.value.TravelMode)) Block($"Trip row {leg.index + 1}: Travel mode is required.");
            if (leg.value.EndDateTime.HasValue && leg.value.StartDateTime.HasValue && leg.value.EndDateTime.Value <= leg.value.StartDateTime.Value) Block($"Trip row {leg.index + 1}: End date/time must be after start date/time.");
        }
        if (request.EndDateTime != default && request.StartDateTime != default && request.EndDateTime <= request.StartDateTime) Block("End date/time must be after start date/time.");
        foreach (var stay in NormalizeAccommodation(request).Select((value, index) => new { value, index }))
        {
            if (submit && request.AccommodationRequired && string.IsNullOrWhiteSpace(stay.value.City)) Block($"Accommodation row {stay.index + 1}: City is required.");
            if (stay.value.CheckOutDateTime.HasValue && stay.value.CheckInDateTime.HasValue && stay.value.CheckOutDateTime.Value <= stay.value.CheckInDateTime.Value) Block($"Accommodation row {stay.index + 1}: Check-out must be after check-in.");
        }
        foreach (var ride in NormalizeLocalTravel(request).Select((value, index) => new { value, index }))
        {
            if (submit && request.LocalConveyanceRequired && string.IsNullOrWhiteSpace(ride.value.TravelMode)) Block($"Local travel row {ride.index + 1}: Mode is required.");
        }
        if (request.AdvanceAmount < 0) Block("Advance amount cannot be negative.");
        return result;
    }

    private static async Task<List<EssTravelValidationResult>> ValidateTravelPolicyAsync(MySqlConnection db, long? policyId, SaveEssTravelRequest request, bool submit)
    {
        var messages = new List<EssTravelValidationResult>();
        if (policyId is null) return messages;
        foreach (var leg in NormalizeTravelCities(request).Where(item => !string.IsNullOrWhiteSpace(item.TravelMode)))
        {
            var configured = (await db.QueryAsync<TravelPolicyRule>(@"SELECT * FROM travel_policy_rules
WHERE PolicyId=@PolicyId AND RuleType='Travel Mode' AND UPPER(AppliesTo)=UPPER(@TravelMode) AND IsActive=TRUE
ORDER BY ApprovalRequired,Id", new { PolicyId = policyId.Value, TravelMode = leg.TravelMode })).ToList();
            var requestedClass = NormalizeTravelClass(leg.TravelClass);
            var mode = configured.FirstOrDefault(rule =>
                string.IsNullOrWhiteSpace(requestedClass)
                    ? !rule.ApprovalRequired
                    : NormalizeTravelClass(JsonText(rule.ConfigJson, "travelClass") ?? "") == requestedClass)
                ?? configured.FirstOrDefault(rule => string.IsNullOrWhiteSpace(JsonText(rule.ConfigJson, "travelClass")));
            if (mode is null)
            {
                var display = string.IsNullOrWhiteSpace(leg.TravelClass) ? leg.TravelMode : $"{leg.TravelMode} ({leg.TravelClass})";
                messages.Add(new EssTravelValidationResult { Severity = submit ? "Block" : "Warning", Behavior = "Block", RuleName = "Travel Mode", Message = $"Travel mode {display} is not configured in applicable policy." });
            }
            else if (!mode.IsAllowed)
            {
                messages.Add(new EssTravelValidationResult { Severity = mode.ExceptionHandling == "Block" ? "Block" : "Warning", Behavior = mode.ExceptionHandling, RuleName = mode.RuleName, Message = $"Travel mode {leg.TravelMode} {leg.TravelClass} is not allowed by policy.".Trim() });
            }
            else if (mode.ApprovalRequired)
            {
                messages.Add(new EssTravelValidationResult { Severity = "Warning", Behavior = "Approval Override", RuleName = mode.RuleName, Message = $"{leg.TravelMode} {leg.TravelClass} requires prior approval under the applicable policy.".Trim() });
            }
        }
        if (request.AdvanceRequired)
        {
            var advance = await db.QueryFirstOrDefaultAsync<(bool IsAllowed, decimal? LimitAmount, string RuleName, string ExceptionHandling)>(@"SELECT IsAllowed,LimitAmount,RuleName,ExceptionHandling FROM travel_policy_rules WHERE PolicyId=@PolicyId AND RuleType='Travel Advance' AND IsActive=TRUE ORDER BY Id DESC LIMIT 1", new { PolicyId = policyId.Value });
            if (string.IsNullOrWhiteSpace(advance.RuleName))
                messages.Add(new EssTravelValidationResult { Severity = "Block", Behavior = "Block", RuleName = "Travel Advance", Message = "Travel advance is not configured in applicable policy." });
            else if (!advance.IsAllowed)
                messages.Add(new EssTravelValidationResult { Severity = advance.ExceptionHandling == "Block" ? "Block" : "Warning", Behavior = advance.ExceptionHandling, RuleName = advance.RuleName, Message = "Travel advance is not allowed by policy." });
            else if (advance.LimitAmount.HasValue && request.AdvanceAmount > advance.LimitAmount.Value)
                messages.Add(new EssTravelValidationResult { Severity = advance.ExceptionHandling == "Block" ? "Block" : "Warning", Behavior = advance.ExceptionHandling, RuleName = advance.RuleName, Message = $"Advance amount exceeds policy limit {advance.LimitAmount.Value:N2}." });
        }
        if (request.TravelScope == "International")
        {
            var international = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM travel_policy_rules WHERE PolicyId=@PolicyId AND RuleType='Per Diem' AND AppliesTo LIKE 'International%' AND IsActive=TRUE", new { PolicyId = policyId.Value });
            if (international == 0) messages.Add(new EssTravelValidationResult { Severity = "Warning", Behavior = "Approval Override", RuleName = "International Travel", Message = "International travel policy is not fully configured; approval override will be required." });
        }
          return messages;
      }

      private static string NormalizeTravelClass(string value) => (value ?? "")
          .Trim().ToUpperInvariant()
          .Replace("THIRD", "3RD")
          .Replace("SECOND", "2ND")
          .Replace("3 AC", "3RD AC")
          .Replace("3AC", "3RD AC")
          .Replace("AC 3 TIER", "3RD AC")
          .Replace("2 AC", "2ND AC")
          .Replace("2AC", "2ND AC")
          .Replace("AC 2 TIER", "2ND AC");

      private static async Task<List<string>> ActiveDropdownValuesAsync(MySqlConnection db, string type, int clientId)
      {
          var rows = await db.QueryAsync<string>(@"SELECT Value FROM dropdownmasters
WHERE IsActive=TRUE AND Type=@Type AND (ClientId=0 OR ClientId=@ClientId)
ORDER BY CASE WHEN ClientId=@ClientId THEN 0 ELSE 1 END, Value", new { Type = type, ClientId = clientId });
          return rows.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      }

      private static async Task AttachTravelSectionsAsync(MySqlConnection db, List<EssTravelRequest> requests)
      {
          if (requests.Count == 0) return;
          var ids = requests.Select(item => item.Id).ToArray();
          var legs = (await db.QueryAsync<EssTravelLegRow>(@"SELECT RequestId,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode,TravelClass,BookingAction,Remarks
FROM ess_travel_request_legs WHERE RequestId IN @Ids ORDER BY RequestId,SequenceNo", new { Ids = ids })).ToList();
          var grouped = legs.GroupBy(item => item.RequestId).ToDictionary(group => group.Key, group => group.Select(ToTravelCity).ToList());
          var accommodation = (await db.QueryAsync<EssTravelAccommodationRow>(@"SELECT RequestId,City,CheckInDateTime,CheckOutDateTime,Occupancy,RoomPreference,BookingAction,Remarks
FROM ess_travel_request_accommodation WHERE RequestId IN @Ids ORDER BY RequestId,SequenceNo", new { Ids = ids })).ToList().GroupBy(item => item.RequestId).ToDictionary(group => group.Key, group => group.Select(ToAccommodation).ToList());
          var localTravel = (await db.QueryAsync<EssLocalTravelRow>(@"SELECT RequestId,City,TravelDateTime,FromLocation,ToLocation,TravelMode,BookingAction,Remarks
FROM ess_travel_request_local_travel WHERE RequestId IN @Ids ORDER BY RequestId,SequenceNo", new { Ids = ids })).ToList().GroupBy(item => item.RequestId).ToDictionary(group => group.Key, group => group.Select(ToLocalTravel).ToList());
          foreach (var request in requests)
          {
              request.Legs = grouped.TryGetValue(request.Id, out var rows)
                  ? rows
                  : string.IsNullOrWhiteSpace(request.FromLocation) && string.IsNullOrWhiteSpace(request.ToLocation) && string.IsNullOrWhiteSpace(request.TravelMode)
                      ? []
                      : [new EssTravelCity { FromLocation = request.FromLocation, ToLocation = request.ToLocation, StartDateTime = request.StartDateTime, EndDateTime = request.EndDateTime, TravelMode = request.TravelMode, BookingAction = "Book by myself" }];
              request.AccommodationDetails = accommodation.TryGetValue(request.Id, out var stays) ? stays : [];
              request.LocalTravelDetails = localTravel.TryGetValue(request.Id, out var rides) ? rides : [];
          }
      }

      private static async Task<List<EssTravelCity>> GetTravelLegsAsync(MySqlConnection db, long requestId)
      {
          var rows = await db.QueryAsync<EssTravelLegRow>(@"SELECT RequestId,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode,TravelClass,BookingAction,Remarks
FROM ess_travel_request_legs WHERE RequestId=@RequestId ORDER BY SequenceNo", new { RequestId = requestId });
          return rows.Select(ToTravelCity).ToList();
      }

      private static async Task<List<EssTravelAccommodation>> GetTravelAccommodationAsync(MySqlConnection db, long requestId)
      {
          var rows = await db.QueryAsync<EssTravelAccommodationRow>(@"SELECT RequestId,City,CheckInDateTime,CheckOutDateTime,Occupancy,RoomPreference,BookingAction,Remarks
FROM ess_travel_request_accommodation WHERE RequestId=@RequestId ORDER BY SequenceNo", new { RequestId = requestId });
          return rows.Select(ToAccommodation).ToList();
      }

      private static async Task<List<EssLocalTravelDetail>> GetLocalTravelAsync(MySqlConnection db, long requestId)
      {
          var rows = await db.QueryAsync<EssLocalTravelRow>(@"SELECT RequestId,City,TravelDateTime,FromLocation,ToLocation,TravelMode,BookingAction,Remarks
FROM ess_travel_request_local_travel WHERE RequestId=@RequestId ORDER BY SequenceNo", new { RequestId = requestId });
          return rows.Select(ToLocalTravel).ToList();
      }

      private static async Task ReplaceTravelLegsAsync(MySqlConnection db, MySqlTransaction tx, long requestId, List<EssTravelCity> legs)
      {
          await db.ExecuteAsync("DELETE FROM ess_travel_request_legs WHERE RequestId=@RequestId", new { RequestId = requestId }, tx);
          var sequence = 1;
          foreach (var leg in legs)
          {
              await db.ExecuteAsync(@"INSERT INTO ess_travel_request_legs (RequestId,SequenceNo,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode,TravelClass,BookingAction,Remarks)
VALUES (@RequestId,@SequenceNo,@FromLocation,@ToLocation,@StartDateTime,@EndDateTime,@TravelMode,@TravelClass,@BookingAction,@Remarks)", new { RequestId = requestId, SequenceNo = sequence++, leg.FromLocation, leg.ToLocation, leg.StartDateTime, leg.EndDateTime, leg.TravelMode, leg.TravelClass, leg.BookingAction, leg.Remarks }, tx);
          }
      }

      private static async Task ReplaceTravelAccommodationAsync(MySqlConnection db, MySqlTransaction tx, long requestId, List<EssTravelAccommodation> rows)
      {
          await db.ExecuteAsync("DELETE FROM ess_travel_request_accommodation WHERE RequestId=@RequestId", new { RequestId = requestId }, tx);
          var sequence = 1;
          foreach (var row in rows)
          {
              await db.ExecuteAsync(@"INSERT INTO ess_travel_request_accommodation (RequestId,SequenceNo,City,CheckInDateTime,CheckOutDateTime,Occupancy,RoomPreference,BookingAction,Remarks)
VALUES (@RequestId,@SequenceNo,@City,@CheckInDateTime,@CheckOutDateTime,@Occupancy,@RoomPreference,@BookingAction,@Remarks)", new { RequestId = requestId, SequenceNo = sequence++, row.City, row.CheckInDateTime, row.CheckOutDateTime, row.Occupancy, row.RoomPreference, row.BookingAction, row.Remarks }, tx);
          }
      }

      private static async Task ReplaceLocalTravelAsync(MySqlConnection db, MySqlTransaction tx, long requestId, List<EssLocalTravelDetail> rows)
      {
          await db.ExecuteAsync("DELETE FROM ess_travel_request_local_travel WHERE RequestId=@RequestId", new { RequestId = requestId }, tx);
          var sequence = 1;
          foreach (var row in rows)
          {
              await db.ExecuteAsync(@"INSERT INTO ess_travel_request_local_travel (RequestId,SequenceNo,City,TravelDateTime,FromLocation,ToLocation,TravelMode,BookingAction,Remarks)
VALUES (@RequestId,@SequenceNo,@City,@TravelDateTime,@FromLocation,@ToLocation,@TravelMode,@BookingAction,@Remarks)", new { RequestId = requestId, SequenceNo = sequence++, row.City, row.TravelDateTime, row.FromLocation, row.ToLocation, row.TravelMode, row.BookingAction, row.Remarks }, tx);
          }
      }

      private static EssTravelCity ToTravelCity(EssTravelLegRow row) => new()
      {
          FromLocation = row.FromLocation,
          ToLocation = row.ToLocation,
          TravelMode = row.TravelMode,
          TravelClass = row.TravelClass,
          BookingAction = row.BookingAction,
          Remarks = row.Remarks,
          StartDateTime = row.StartDateTime,
          EndDateTime = row.EndDateTime
      };

      private static EssTravelAccommodation ToAccommodation(EssTravelAccommodationRow row) => new()
      {
          City = row.City,
          CheckInDateTime = row.CheckInDateTime,
          CheckOutDateTime = row.CheckOutDateTime,
          Occupancy = row.Occupancy,
          RoomPreference = row.RoomPreference,
          BookingAction = row.BookingAction,
          Remarks = row.Remarks
      };

      private static EssLocalTravelDetail ToLocalTravel(EssLocalTravelRow row) => new()
      {
          City = row.City,
          TravelDateTime = row.TravelDateTime,
          FromLocation = row.FromLocation,
          ToLocation = row.ToLocation,
          TravelMode = row.TravelMode,
          BookingAction = row.BookingAction,
          Remarks = row.Remarks
      };

      private static async Task MigrateLegacyTravelLegsAsync(MySqlConnection db)
      {
          if (!await ColumnExistsAsync(db, "ess_travel_requests", "MultiCityJson")) return;
          var rows = await db.QueryAsync<(long Id, string MultiCityJson, string FromLocation, string ToLocation, DateTime StartDateTime, DateTime EndDateTime, string TravelMode)>(@"SELECT Id,COALESCE(MultiCityJson,'[]') MultiCityJson,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode
FROM ess_travel_requests r
WHERE NOT EXISTS (SELECT 1 FROM ess_travel_request_legs l WHERE l.RequestId=r.Id)
AND COALESCE(JSON_LENGTH(r.MultiCityJson),0)>0");
          foreach (var row in rows)
          {
              List<EssTravelCity> legs;
              try { legs = JsonSerializer.Deserialize<List<EssTravelCity>>(row.MultiCityJson) ?? []; }
              catch { legs = []; }
              if (legs.Count == 0) legs = [new EssTravelCity { FromLocation = row.FromLocation, ToLocation = row.ToLocation, StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime, TravelMode = row.TravelMode }];
              await using var tx = await db.BeginTransactionAsync();
              await ReplaceTravelLegsAsync(db, tx, row.Id, NormalizeTravelCities(new SaveEssTravelRequest { FromLocation = row.FromLocation, ToLocation = row.ToLocation, StartDateTime = row.StartDateTime, EndDateTime = row.EndDateTime, TravelMode = row.TravelMode, Cities = legs }));
              await tx.CommitAsync();
          }
      }

      private static async Task<bool> ColumnExistsAsync(MySqlConnection db, string table, string column)
      {
          var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table AND COLUMN_NAME = @Column", new { Table = table, Column = column });
          return exists > 0;
      }

      private static List<EssTravelCity> NormalizeTravelCities(SaveEssTravelRequest request)
      {
          var rows = (request.Cities ?? [])
              .Select(item => new EssTravelCity
              {
                  FromLocation = item.FromLocation?.Trim() ?? "",
                  ToLocation = item.ToLocation?.Trim() ?? "",
                  TravelMode = item.TravelMode?.Trim() ?? "",
                  TravelClass = item.TravelClass?.Trim() ?? "",
                  BookingAction = NormalizeBookingAction(item.BookingAction),
                  Remarks = item.Remarks?.Trim() ?? "",
                  StartDateTime = item.StartDateTime,
                  EndDateTime = item.EndDateTime
              })
              .Where(item => !string.IsNullOrWhiteSpace(item.FromLocation) || !string.IsNullOrWhiteSpace(item.ToLocation) || !string.IsNullOrWhiteSpace(item.TravelMode) || item.StartDateTime.HasValue || item.EndDateTime.HasValue)
              .ToList();
          if (rows.Count == 0 && (!string.IsNullOrWhiteSpace(request.FromLocation) || !string.IsNullOrWhiteSpace(request.ToLocation) || !string.IsNullOrWhiteSpace(request.TravelMode)))
          {
              rows.Add(new EssTravelCity
              {
                  FromLocation = request.FromLocation.Trim(),
                  ToLocation = request.ToLocation.Trim(),
                  TravelMode = request.TravelMode.Trim(),
                  StartDateTime = request.StartDateTime == default ? null : request.StartDateTime,
                  EndDateTime = request.EndDateTime == default ? null : request.EndDateTime
              });
          }
          return rows;
      }

      private static List<EssTravelAccommodation> NormalizeAccommodation(SaveEssTravelRequest request) =>
          (request.AccommodationDetails ?? [])
              .Select(item => new EssTravelAccommodation
              {
                  City = item.City?.Trim() ?? "",
                  CheckInDateTime = item.CheckInDateTime,
                  CheckOutDateTime = item.CheckOutDateTime,
                  Occupancy = item.Occupancy?.Trim() ?? "",
                  RoomPreference = item.RoomPreference?.Trim() ?? "",
                  BookingAction = NormalizeBookingAction(item.BookingAction),
                  Remarks = item.Remarks?.Trim() ?? ""
              })
              .Where(item => !string.IsNullOrWhiteSpace(item.City) || item.CheckInDateTime.HasValue || item.CheckOutDateTime.HasValue || !string.IsNullOrWhiteSpace(item.Occupancy) || !string.IsNullOrWhiteSpace(item.RoomPreference) || !string.IsNullOrWhiteSpace(item.Remarks))
              .ToList();

      private static List<EssLocalTravelDetail> NormalizeLocalTravel(SaveEssTravelRequest request) =>
          (request.LocalTravelDetails ?? [])
              .Select(item => new EssLocalTravelDetail
              {
                  City = item.City?.Trim() ?? "",
                  TravelDateTime = item.TravelDateTime,
                  FromLocation = item.FromLocation?.Trim() ?? "",
                  ToLocation = item.ToLocation?.Trim() ?? "",
                  TravelMode = item.TravelMode?.Trim() ?? "",
                  BookingAction = NormalizeBookingAction(item.BookingAction),
                  Remarks = item.Remarks?.Trim() ?? ""
              })
              .Where(item => !string.IsNullOrWhiteSpace(item.City) || item.TravelDateTime.HasValue || !string.IsNullOrWhiteSpace(item.FromLocation) || !string.IsNullOrWhiteSpace(item.ToLocation) || !string.IsNullOrWhiteSpace(item.TravelMode) || !string.IsNullOrWhiteSpace(item.Remarks))
              .ToList();

      private static string NormalizeBookingAction(string? value) =>
          string.Equals(value, "Require Booking", StringComparison.OrdinalIgnoreCase)
              ? "Require Booking"
              : "Book by myself";

      private static object ToTravelArgs(SaveEssTravelRequest request, EssTravelEmployee employee, long? policyId, string policyJson, string requestNumber)
    {
        var cities = NormalizeTravelCities(request);
        var first = cities.FirstOrDefault();
        var last = cities.LastOrDefault();
        var start = cities.Select(item => item.StartDateTime).Where(item => item.HasValue).Min() ?? request.StartDateTime;
        var end = cities.Select(item => item.EndDateTime).Where(item => item.HasValue).Max() ?? request.EndDateTime;
        return new
      {
          request.Id,
          RequestNumber = requestNumber,
        employee.EmployeeId,
        employee.ClientId,
        employee.Department,
        employee.Designation,
        employee.ReportingManagerId,
        Purpose = request.Purpose.Trim(),
          Customer = employee.ClientName.Trim(),
        Project = "",
        CostCenter = request.CostCenter.Trim(),
        TravelScope = "Domestic",
          request.TravelType,
            Priority = "Normal",
            FromLocation = CleanText(first?.FromLocation, request.FromLocation).Trim(),
            ToLocation = CleanText(last?.ToLocation, request.ToLocation).Trim(),
            StartDateTime = start,
            EndDateTime = end,
          EstimatedCost = 0,
          PolicyId = policyId,
          TravelMode = CleanText(first?.TravelMode, request.TravelMode),
          AccommodationRequired = request.AccommodationRequired || NormalizeAccommodation(request).Count > 0,
          LocalConveyanceRequired = request.LocalConveyanceRequired || NormalizeLocalTravel(request).Count > 0,
          request.AdvanceRequired,
        request.AdvanceAmount,
          Remarks = request.Remarks.Trim(),
          PolicyValidationJson = policyJson
      };
    }

    private static async Task<string> NextTravelRequestNumberAsync(MySqlConnection db)
    {
        var prefix = $"TRV-{DateTime.Today:yyyyMM}-";
        var next = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(CAST(SUBSTRING(RequestNumber, 12) AS UNSIGNED)),0)+1 FROM ess_travel_requests WHERE RequestNumber LIKE @Pattern", new { Pattern = $"{prefix}%" });
        return $"{prefix}{next:0000}";
    }

    private static async Task AuditTravelAsync(MySqlConnection db, long requestId, string action, string comment) =>
        await db.ExecuteAsync("INSERT INTO ess_travel_request_audit (RequestId,Action,Comment) VALUES (@RequestId,@Action,@Comment)", new { RequestId = requestId, Action = action, Comment = comment });

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

    private static string BuildPayslipHtml(Organization org, EssPayslipTemplate template, EssPayslipRow row, EssPayslipYtd ytd)
    {
        var lines = ParsePayslipLines(row.DetailsJson);
        var earnings = lines.Where(line => line.Amount > 0 && (line.Category.Equals("Earning", StringComparison.OrdinalIgnoreCase) || line.Category.Equals("Reimbursement", StringComparison.OrdinalIgnoreCase))).ToList();
        var deductions = lines.Where(line => line.Amount > 0 && line.Category.Contains("Deduction", StringComparison.OrdinalIgnoreCase)).ToList();
        if (row.OneTimeEarnings > 0 && !earnings.Any(line => line.Id == "ONE_TIME_EARNINGS")) earnings.Add(new("ONE_TIME_EARNINGS", "One Time Earnings", "Earning", row.OneTimeEarnings, row.OneTimeEarnings));
        if (row.OneTimeDeductions > 0 && !deductions.Any(line => line.Id == "ONE_TIME_DEDUCTIONS")) deductions.Add(new("ONE_TIME_DEDUCTIONS", "One Time Deductions", "Deduction", row.OneTimeDeductions, row.OneTimeDeductions));
        var companyName = CleanText(org.LegalName, org.Name, row.ClientName, "Organization");
        var companyAddress = string.Join(", ", new[] { org.AddressLine1, org.AddressLine2, org.City, org.State, org.PostalCode }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var logo = template.ShowLogo && !string.IsNullOrWhiteSpace(org.LogoDataUrl) ? $@"<img src=""{Attr(org.LogoDataUrl)}"" alt=""Logo"" />" : "<b>LOGO</b>";
        var theme = (template.Theme ?? "Classic").Trim().ToLowerInvariant();
        var showBank = template.ShowBank;
        var showClient = template.ShowClient;
        return $@"<!doctype html><html><head><meta charset=""utf-8""><title>Payslip {Html(row.PayPeriod)} - {Html(row.EmployeeCode)}</title><style>{PayslipCss(theme)}</style></head><body><main class=""slip {Attr(theme)}""><header class=""slip-head""><div class=""logo"">{logo}</div><div><h1>{Html(companyName)}</h1><p>{Html(companyAddress)}</p>{(showClient ? $"<p>Client: {Html(row.ClientName)}</p>" : "")}</div></header><h2>Payslip - {Html(PeriodTitle(row.PayPeriod))}</h2><table class=""info""><thead><tr><th>Employee Details</th><th>Salary Details</th></tr></thead><tbody><tr><td>{Detail("Name", row.EmployeeName)}{Detail("Email Id", row.WorkEmail)}{Detail("Emp Code", row.EmployeeCode)}{Detail("Designation", row.Designation)}{Detail("Date of Joining", DateText(row.DateOfJoining))}{Detail("Address", row.Address)}{Detail("Location", row.WorkLocation)}{Detail("PAN", row.PanNumber)}{Detail("UAN", row.UanNumber)}{(showBank ? Detail("Bank", row.BankName) + Detail("Account #", row.BankAccountNo) + Detail("IFSC", row.IfscCode) : "")}</td><td>{Detail("Salary Period", PeriodRange(row.PayPeriod))}{Detail("Payable Days", Amount(row.PayableDays))}{Detail("Present Days", Amount(row.PresentDays))}{Detail("Working Days", row.TotalWorkingDays)}{Detail("Payment Status", row.PaymentStatus)}{Detail("Payment Date", DateText(row.PaymentDate))}</td></tr></tbody></table><table class=""salary""><thead><tr><th>Earnings</th><th>Rate</th><th>Actual</th><th>Deductions</th><th>Amount</th></tr></thead><tbody>{RenderRows(earnings, deductions)}<tr class=""total""><td>Earning Total</td><td class=""num"">{Amount(earnings.Sum(item => item.MonthlyAmount))}</td><td class=""num"">{Amount(row.GrossPay + row.OneTimeEarnings)}</td><td>Deduction Total</td><td class=""num"">{Amount(row.StatutoryDeductions + row.OneTimeDeductions)}</td></tr></tbody></table><table class=""net""><tbody><tr><td>Net Pay (INR) :</td><td class=""num"">{Amount(row.NetPay)}</td></tr></tbody></table>{(template.ShowYtd ? $@"<p class=""ytd"">YTD Gross: Rs {Amount(ytd.Gross)} | YTD Deductions: Rs {Amount(ytd.Deductions)} | YTD Net: Rs {Amount(ytd.NetPay)}</p>" : "")}<p class=""words"">{Html(AmountInWords(row.NetPay))}</p>{(!string.IsNullOrWhiteSpace(template.Note) ? $"<footer>{Html(template.Note)}</footer>" : "")}</main></body></html>";
    }

    private static List<EssPayslipLine> ParsePayslipLines(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var rows = new List<EssPayslipLine>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = JsonText(item, "id", "Id");
                if (string.IsNullOrWhiteSpace(id) || new[] { "GROSS_EARNED", "NET_PAY", "EMPLOYER_COST" }.Contains(id.ToUpperInvariant())) continue;
                rows.Add(new EssPayslipLine(id, CleanText(JsonText(item, "name", "Name"), "Salary component"), JsonText(item, "category", "Category"), JsonNumber(item, "monthlyAmount", "MonthlyAmount"), JsonNumber(item, "amount", "Amount")));
            }
            return rows;
        }
        catch { return []; }
    }

    private static string PayslipCss(string theme) => $@"@page{{size:A4;margin:12mm}}*{{box-sizing:border-box}}body{{margin:0;background:#fff;color:#000;font-family:Arial,Helvetica,sans-serif;font-size:11px}}.slip{{width:720px;margin:18px auto 0}}.slip-head{{display:grid;grid-template-columns:150px 1fr 90px;align-items:center;min-height:118px}}.logo{{width:120px;height:86px;display:grid;place-items:center}}.logo img{{max-width:120px;max-height:86px;object-fit:contain}}.logo b{{color:#2f80bd}}.slip-head h1{{margin:0;text-align:center;font-size:14px;font-weight:800;text-transform:uppercase}}.slip-head p{{margin:2px 0;text-align:center;font-size:12px}}.slip h2{{margin:8px 0 14px;text-align:center;color:#337fb6;font-size:15px}}.info,.salary,.net{{width:100%;border-collapse:collapse}}.info th,.salary th{{padding:7px 8px;border:1px solid #5c9bd3;background:#eaf4fb;color:#2f80bd;text-align:left;font-weight:800}}.info td{{width:50%;vertical-align:top;padding:9px 10px;border:1px solid #5c9bd3}}.detail{{display:grid;grid-template-columns:78px 1fr;gap:7px;margin:0 0 5px;line-height:1.12}}.detail strong{{font-weight:800}}.salary{{margin-top:14px}}.salary th:nth-child(1),.salary th:nth-child(4){{width:20%}}.salary th:nth-child(2),.salary th:nth-child(3),.salary th:nth-child(5){{width:20%}}.salary td{{height:23px;padding:4px 8px;border-left:1px solid #5c9bd3;border-right:1px solid #5c9bd3;font-weight:700}}.salary tbody tr:first-child td{{border-top:1px solid #5c9bd3}}.salary tbody tr:last-child td{{border-bottom:1px solid #5c9bd3}}.salary .total td{{border-top:1px solid #5c9bd3;border-bottom:1px solid #5c9bd3;font-weight:800}}.num{{text-align:right}}.net{{width:255px;margin-top:14px}}.net td{{padding:4px 8px;border-top:1px solid #5c9bd3;border-bottom:1px solid #5c9bd3;font-size:12px;font-weight:800}}.words,.ytd,footer{{margin:8px 0 0;font-weight:800}}footer{{text-align:center;color:#555;font-weight:400}}.modern .info th,.modern .salary th{{background:#f3f0ff;color:#5133cc;border-color:#6546e8}}.modern .info td,.modern .salary td,.modern .net td{{border-color:#6546e8}}.compact{{font-size:10px}}.compact .slip-head{{min-height:88px}}.compact .info td{{padding:6px}}.compact .salary td{{height:20px;padding:3px 6px}}@media print{{body{{print-color-adjust:exact;-webkit-print-color-adjust:exact}}.slip{{margin:0 auto}}}}";
    private static string RenderRows(List<EssPayslipLine> earnings, List<EssPayslipLine> deductions) => string.Join("", Enumerable.Range(0, Math.Max(1, Math.Max(earnings.Count, deductions.Count))).Select(index => { var earning = index < earnings.Count ? earnings[index] : null; var deduction = index < deductions.Count ? deductions[index] : null; return $@"<tr><td>{Html(earning?.Name ?? "")}</td><td class=""num"">{(earning is null ? "" : Amount(earning.MonthlyAmount))}</td><td class=""num"">{(earning is null ? "" : Amount(earning.Amount))}</td><td>{Html(deduction?.Name ?? "")}</td><td class=""num"">{(deduction is null ? "" : Amount(deduction.Amount))}</td></tr>"; }));
    private static string Detail(string label, object? value) => $@"<div class=""detail""><span>{Html(label)}</span><strong>{Html(CleanText(Convert.ToString(value), "-"))}</strong></div>";
    private static string Html(object? value) => WebUtility.HtmlEncode(Convert.ToString(value) ?? "");
    private static string Attr(object? value) => Html(value).Replace("\"", "&quot;");
    private static string CleanText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    private static string Clean(string? value) => (value ?? "").Trim();
    private static async Task EnsureProfileTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS ess_client_settings (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    AllowProfileEdit BOOLEAN NOT NULL DEFAULT FALSE,
    InitialPasswordMode VARCHAR(30) NOT NULL DEFAULT 'App Default',
    FixedPassword VARCHAR(200) NOT NULL DEFAULT '',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ess_client_settings_client (ClientId)
);
CREATE TABLE IF NOT EXISTS employeepersonaldetails (
    EmployeeId INT PRIMARY KEY,
    DateOfBirth VARCHAR(30) NOT NULL DEFAULT '',
    Mobile VARCHAR(50) NOT NULL DEFAULT '',
    PanNumber VARCHAR(50) NOT NULL DEFAULT '',
    AadhaarNumber VARCHAR(50) NOT NULL DEFAULT '',
    UanNumber VARCHAR(50) NOT NULL DEFAULT '',
    EsicNumber VARCHAR(50) NOT NULL DEFAULT '',
    Address VARCHAR(800) NOT NULL DEFAULT '',
    CorrespondenceAddress VARCHAR(800) NOT NULL DEFAULT '',
    PermanentAddress VARCHAR(800) NOT NULL DEFAULT '',
    Source VARCHAR(120) NOT NULL DEFAULT '',
    SourceLocation VARCHAR(200) NOT NULL DEFAULT '',
    City VARCHAR(100) NOT NULL DEFAULT '',
    District VARCHAR(100) NOT NULL DEFAULT '',
    State VARCHAR(100) NOT NULL DEFAULT '',
    RawDesignation VARCHAR(160) NOT NULL DEFAULT '',
    OriginalEmployeeCode VARCHAR(80) NOT NULL DEFAULT '',
    DuplicateResolution VARCHAR(500) NOT NULL DEFAULT '',
    ExcelRow INT NOT NULL DEFAULT 0,
    EsicEmployee DECIMAL(18,4) NOT NULL DEFAULT 0,
    PtLwfWorkmenComp DECIMAL(18,4) NOT NULL DEFAULT 0,
    Tds DECIMAL(18,4) NOT NULL DEFAULT 0,
    Recovery DECIMAL(18,4) NOT NULL DEFAULT 0,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS employeepaymentdetails (
    EmployeeId INT PRIMARY KEY,
    BankName VARCHAR(160) NOT NULL DEFAULT '',
    BankAccountNo VARCHAR(100) NOT NULL DEFAULT '',
    IfscCode VARCHAR(40) NOT NULL DEFAULT '',
    PaymentMode VARCHAR(60) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS ess_profile_update_audit (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    ChangedBy VARCHAR(180) NOT NULL DEFAULT '',
    OldValueJson JSON NULL,
    NewValueJson JSON NULL,
    ChangedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_ess_profile_update_audit_employee (EmployeeId, ChangedAt)
);");
        await EnsureColumnAsync(db, "ess_client_settings", "InitialPasswordMode", "VARCHAR(30) NOT NULL DEFAULT 'App Default' AFTER AllowProfileEdit");
        await EnsureColumnAsync(db, "ess_client_settings", "FixedPassword", "VARCHAR(200) NOT NULL DEFAULT '' AFTER InitialPasswordMode");
    }
    private static string SafeFile(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
    private static string Amount(decimal value) => value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
    private static string DateText(DateTime? value) => value.HasValue ? value.Value.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture) : "-";
    private static string PeriodRange(string payPeriod) => DateTime.TryParse($"{payPeriod}-01", out var start) ? $"{DateText(start)} - {DateText(start.AddMonths(1).AddDays(-1))}" : payPeriod;
    private static string PeriodTitle(string payPeriod) => DateTime.TryParse($"{payPeriod}-01", out var start) ? start.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : payPeriod;
    private static string DateText(DateTime value) => value.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
    private static string JsonText(JsonElement element, params string[] names) => names.Select(name => element.TryGetProperty(name, out var property) ? property.ToString() : "").FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static decimal JsonNumber(JsonElement element, params string[] names) => decimal.TryParse(JsonText(element, names), out var value) ? value : 0;
    private static string AmountInWords(decimal value) => $"{Amount(value)} only";

      private sealed record EssPayslipLine(string Id, string Name, string Category, decimal MonthlyAmount, decimal Amount);
      private sealed class EssTravelLegRow { public long RequestId { get; set; } public string FromLocation { get; set; } = ""; public string ToLocation { get; set; } = ""; public DateTime? StartDateTime { get; set; } public DateTime? EndDateTime { get; set; } public string TravelMode { get; set; } = ""; public string TravelClass { get; set; } = ""; public string BookingAction { get; set; } = "Book by myself"; public string Remarks { get; set; } = ""; }
      private sealed class EssTravelAccommodationRow { public long RequestId { get; set; } public string City { get; set; } = ""; public DateTime? CheckInDateTime { get; set; } public DateTime? CheckOutDateTime { get; set; } public string Occupancy { get; set; } = ""; public string RoomPreference { get; set; } = ""; public string BookingAction { get; set; } = "Book by myself"; public string Remarks { get; set; } = ""; }
      private sealed class EssLocalTravelRow { public long RequestId { get; set; } public string City { get; set; } = ""; public DateTime? TravelDateTime { get; set; } public string FromLocation { get; set; } = ""; public string ToLocation { get; set; } = ""; public string TravelMode { get; set; } = ""; public string BookingAction { get; set; } = "Book by myself"; public string Remarks { get; set; } = ""; }
      private sealed class EssTravelEmployee { public int EmployeeId { get; set; } public int ClientId { get; set; } public string ClientName { get; set; } = ""; public string EmployeeName { get; set; } = ""; public string Department { get; set; } = ""; public string Designation { get; set; } = ""; public int ReportingManagerId { get; set; } public string ReportingManager { get; set; } = ""; }
    private sealed class TravelAdvanceSeed { public long TravelRequestId { get; set; } public int EmployeeId { get; set; } public int ClientId { get; set; } public decimal AdvanceAmount { get; set; } public DateTime EndDateTime { get; set; } }
    private sealed class ExpenseClaimQueueHeader { public long Id { get; set; } public int ClientId { get; set; } public int EmployeeId { get; set; } public long? TravelRequestId { get; set; } public string ClaimNumber { get; set; } = ""; public string ReimbursementComponentCode { get; set; } = ""; public string EmployeeName { get; set; } = ""; public string EmployeeCode { get; set; } = ""; public long ComponentId { get; set; } public string ComponentCode { get; set; } = ""; public string ComponentName { get; set; } = ""; }
    private sealed class ExpenseClaimQueueLine { public long Id { get; set; } public decimal Amount { get; set; } }
    private sealed class AdminExpenseClaimRow { public long Id { get; set; } public string ClaimNumber { get; set; } = ""; public int ClientId { get; set; } public int EmployeeId { get; set; } public long? TravelRequestId { get; set; } public string Status { get; set; } = ""; public string PayrollStatus { get; set; } = ""; public int? PayrollRunId { get; set; } }
    private sealed class AdminTravelRequestRow { public long Id { get; set; } public string RequestNumber { get; set; } = ""; public int ClientId { get; set; } public int EmployeeId { get; set; } public string Status { get; set; } = ""; }
    private sealed class EssLeaveSelection { public int Id { get; set; } public int ClientId { get; set; } public string Name { get; set; } = ""; public string Code { get; set; } = ""; public string Type { get; set; } = "Paid"; public decimal Balance { get; set; } public bool AllowNegativeLeaveBalance { get; set; } public bool AllowHalfDay { get; set; } = true; public string AttendanceAction { get; set; } = "Mark as leave"; }
    private sealed class ApprovedLeaveRequestRow { public long Id { get; set; } public int EmployeeId { get; set; } public int ClientId { get; set; } public int LeaveTypeId { get; set; } public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public string DayType { get; set; } = "Full Day"; public decimal Days { get; set; } public string Reason { get; set; } = ""; public string LeaveCode { get; set; } = ""; public string LeaveTypeKind { get; set; } = "Paid"; public string AttendanceAction { get; set; } = "Mark as leave"; }
    private sealed class LeaveWorkflowReconciliationRow { public long Id { get; set; } public string Status { get; set; } = ""; }
    private sealed class EssPayslipTemplate { public long Id { get; set; } public int ClientId { get; set; } public string Name { get; set; } = "Standard Payslip"; public string Theme { get; set; } = "Classic"; public bool ShowLogo { get; set; } = true; public bool ShowClient { get; set; } = true; public bool ShowYtd { get; set; } = true; public bool ShowBank { get; set; } = true; public string Note { get; set; } = "This is a system generated payslip."; public bool Active { get; set; } = true; }
    private sealed class EssPayslipYtd { public decimal Gross { get; set; } public decimal Deductions { get; set; } public decimal NetPay { get; set; } }
    private sealed class EssPayslipRow { public int PayRunEmployeeId { get; set; } public int PayRunId { get; set; } public int EmployeeId { get; set; } public int ClientId { get; set; } public string EmployeeCode { get; set; } = ""; public string EmployeeName { get; set; } = ""; public string Department { get; set; } = ""; public decimal PresentDays { get; set; } public decimal PayableDays { get; set; } public decimal GrossPay { get; set; } public decimal StatutoryDeductions { get; set; } public decimal OneTimeEarnings { get; set; } public decimal OneTimeDeductions { get; set; } public decimal NetPay { get; set; } public string PaymentStatus { get; set; } = ""; public DateTime? PaymentDate { get; set; } public string DetailsJson { get; set; } = ""; public string PayPeriod { get; set; } = ""; public DateTime PayDate { get; set; } public string RunStatus { get; set; } = ""; public int TotalWorkingDays { get; set; } public string ClientName { get; set; } = ""; public string WorkEmail { get; set; } = ""; public string Designation { get; set; } = ""; public DateTime? DateOfJoining { get; set; } public string WorkLocation { get; set; } = ""; public string Address { get; set; } = ""; public string PanNumber { get; set; } = ""; public string UanNumber { get; set; } = ""; public string BankName { get; set; } = ""; public string BankAccountNo { get; set; } = ""; public string IfscCode { get; set; } = ""; }
}
