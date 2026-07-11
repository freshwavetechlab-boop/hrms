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
        await EnsureTravelTablesAsync(db);
        await EnsureColumnAsync(db, "essleaverequests", "DayType", "VARCHAR(30) NOT NULL DEFAULT 'Full Day' AFTER ToDate");
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
        return await db.QueryFirstOrDefaultAsync<EssProfile>(@"SELECT e.EmployeeCode, e.FirstName, e.LastName, e.WorkEmail, e.Department, e.Designation, e.DateOfJoining,
COALESCE(w.Name, '') AS WorkLocation, COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName, ' ', m.LastName), '') AS ReportingManager
FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId LEFT JOIN employees m ON m.Id=e.ReportingManagerId
WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
    }

    public async Task<(EssLeaveRequest? Request, string? Error)> CreateLeaveRequestAsync(int employeeId, int? clientId, CreateEssLeaveRequest request)
    {
        if (!DateTime.TryParse(request.FromDate, out var from) || !DateTime.TryParse(request.ToDate, out var to) || to.Date < from.Date) return (null, "Select a valid leave date range.");
        var dayType = NormalizeDayType(request.DayType);
        if (dayType != "Full Day" && from.Date != to.Date) return (null, "Half-day leave can be applied for one date only.");
        var days = dayType == "Full Day" ? (decimal)(to.Date - from.Date).TotalDays + 1 : 0.5m;
        await using var db = Connection(); await db.OpenAsync();
        var leave = await db.QueryFirstOrDefaultAsync<EssLeaveSelection>(@"SELECT lt.Id,e.ClientId,lt.Name,lt.Code,lt.Type,COALESCE(b.balance_count,0) Balance,COALESCE(p.allow_negative_leave_balance,FALSE) AllowNegativeLeaveBalance,COALESCE(p.allow_half_day,TRUE) AllowHalfDay
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
        var isPaidLeave = leave.Type.Equals("Paid", StringComparison.OrdinalIgnoreCase) && !leave.Code.Equals("LWP", StringComparison.OrdinalIgnoreCase);
        if (isPaidLeave && !leave.AllowNegativeLeaveBalance && days > leave.Balance) return (null, "Requested days exceed the available leave balance.");
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO essleaverequests (EmployeeId,ClientId,LeaveTypeId,FromDate,ToDate,DayType,Days,Reason,Status) VALUES (@EmployeeId,@ClientId,@LeaveTypeId,@FromDate,@ToDate,@DayType,@Days,@Reason,'Pending Approval'); SELECT LAST_INSERT_ID();", new { EmployeeId = employeeId, ClientId = clientId ?? leave.ClientId, LeaveTypeId = leave.Id, FromDate = from.Date, ToDate = to.Date, DayType = dayType, Days = days, Reason = request.Reason.Trim() });
        return (new EssLeaveRequest { Id = id, LeaveCode = request.LeaveCode, LeaveType = leave.Name, FromDate = from.Date, ToDate = to.Date, DayType = dayType, Days = days, Reason = request.Reason, Status = "Pending Approval", CreatedAt = DateTime.UtcNow }, null);
    }

    public async Task<IEnumerable<EssLeaveRequest>> GetLeaveRequestsAsync(int employeeId, int? clientId)
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssLeaveRequest>(@"SELECT r.Id,lt.Code LeaveCode,lt.Name LeaveType,r.FromDate,r.ToDate,COALESCE(r.DayType,'Full Day') DayType,r.Days,r.Reason,r.Status,r.CreatedAt FROM essleaverequests r JOIN leave_types lt ON lt.Id=r.LeaveTypeId WHERE r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId) ORDER BY r.CreatedAt DESC",new{EmployeeId=employeeId,ClientId=clientId}); }
    public async Task<EssTravelOptions> GetTravelOptionsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var policy = await ResolveTravelPolicyAsync(db, employeeId, clientId);
        var modes = policy is null ? [] : (await db.QueryAsync<string>(@"SELECT AppliesTo FROM travel_policy_rules WHERE PolicyId=@PolicyId AND RuleType='Travel Mode' AND IsAllowed=TRUE AND IsActive=TRUE ORDER BY AppliesTo", new { PolicyId = policy.Value.Id })).ToList();
        var clientName = await db.ExecuteScalarAsync<string>(@"SELECT COALESCE(c.Name,'') FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId WHERE e.Id=@EmployeeId AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId }) ?? "";
        var travelTypes = await ActiveDropdownValuesAsync(db, "Travel Type");
        var locations = await ActiveDropdownValuesAsync(db, "Travel Location");
        if (locations.Count == 0)
        {
            var workLocations = await db.QueryAsync<string>(@"SELECT DISTINCT Name FROM worklocations WHERE IsActive=TRUE AND (@ClientId IS NULL OR ClientId=@ClientId)
UNION
SELECT DISTINCT City FROM worklocations WHERE IsActive=TRUE AND City<>'' AND (@ClientId IS NULL OR ClientId=@ClientId)", new { ClientId = clientId });
            var cityMasters = await db.QueryAsync<string>("SELECT Value FROM dropdownmasters WHERE IsActive=TRUE AND Type LIKE 'City:%' ORDER BY Value");
            locations = workLocations.Concat(cityMasters).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
        }
        var classes = await ActiveDropdownValuesAsync(db, "Travel Class");
        return new EssTravelOptions
        {
            PolicyId = policy?.Id,
            PolicyName = policy?.Name ?? "",
            ClientName = clientName,
            TravelModes = modes,
            TravelTypes = travelTypes.Count == 0 ? ["Official", "Client Visit", "Training", "Conference", "Site Visit"] : travelTypes,
            Priorities = ["Normal", "Urgent", "Emergency"],
            Locations = locations,
            TravelClasses = classes.Count == 0 ? ["Economy", "Premium Economy", "Business", "Sleeper", "3AC", "2AC", "1AC", "Standard"] : classes,
            ValidationMessages = policy is null ? ["No active travel policy is assigned to your profile. Contact HR."] : []
        };
    }
    public async Task<IEnumerable<EssTravelRequest>> GetTravelRequestsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var rows = (await db.QueryAsync<EssTravelRequest>(TravelRequestSelect("r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)") + " ORDER BY r.UpdatedAt DESC, r.Id DESC", new { EmployeeId = employeeId, ClientId = clientId })).ToList();
        await AttachTravelLegsAsync(db, rows);
        return rows;
    }
    public async Task<EssTravelRequest?> GetTravelRequestAsync(long id, int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var row = await db.QueryFirstOrDefaultAsync<EssTravelRequest>(TravelRequestSelect("r.Id=@Id AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId)") + " LIMIT 1", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (row is not null) await AttachTravelLegsAsync(db, [row]);
        return row;
    }
    public async Task<(EssTravelRequest? Request, string? Error)> SaveTravelDraftAsync(int employeeId, int? clientId, SaveEssTravelRequest request)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var employee = await db.QueryFirstOrDefaultAsync<EssTravelEmployee>(@"SELECT e.Id EmployeeId,e.ClientId,COALESCE(c.Name,'') ClientName,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,e.Department,e.Designation,COALESCE(e.ReportingManagerId,0) ReportingManagerId,COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName,' ',m.LastName), '') ReportingManager
FROM employees e LEFT JOIN clients c ON c.Id=e.ClientId LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId LEFT JOIN employees m ON m.Id=e.ReportingManagerId
WHERE e.Id=@EmployeeId AND e.IsActive=TRUE AND (@ClientId IS NULL OR e.ClientId=@ClientId)", new { EmployeeId = employeeId, ClientId = clientId });
        if (employee is null) return (null, "Employee profile is unavailable.");
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
            await tx.CommitAsync();
        }
        await AuditTravelAsync(db, request.Id, "Updated", "Draft updated");
        return (await GetTravelRequestAsync(request.Id, employeeId, employee.ClientId), null);
    }
    public async Task<(EssTravelRequest? Request, string? Error)> SubmitTravelRequestAsync(int employeeId, int? clientId, long id)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTravelTablesAsync(db);
        var request = await db.QueryFirstOrDefaultAsync<SaveEssTravelRequest>(@"SELECT Id,Purpose,Customer,Project,CostCenter,TravelScope,TravelType,Priority,FromLocation,ToLocation,StartDateTime,EndDateTime,EstimatedCost,TravelMode,AccommodationRequired,LocalConveyanceRequired,AdvanceRequired,AdvanceAmount,Remarks FROM ess_travel_requests WHERE Id=@Id AND EmployeeId=@EmployeeId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, EmployeeId = employeeId, ClientId = clientId });
        if (request is null) return (null, "Travel request was not found.");
        request.Cities = await GetTravelLegsAsync(db, id);
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM ess_travel_requests WHERE Id=@Id", new { Id = id });
        if (status != "Draft" && status != "Sent Back") return (null, "Only draft or sent back travel requests can be submitted.");
        var errors = ValidateTravelRequest(request, true);
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
        return await db.QueryAsync<EssTravelRequest>(TravelRequestSelect("r.Status='Approved' AND r.EmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId) AND r.StartDateTime<=@ToDate AND r.EndDateTime>=@FromDate") + " ORDER BY r.StartDateTime", new { EmployeeId = employeeId, ClientId = clientId, FromDate = from, ToDate = to });
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
    { await using var db=Connection();await db.OpenAsync();return await db.QueryAsync<EssDailyAttendance>(@"SELECT attendance_date AS AttendanceDate,status AS Status,payable_value AS PayableValue,COALESCE(remarks,'') AS Remarks FROM employee_daily_attendance WHERE employee_id=@EmployeeId AND (@ClientId IS NULL OR client_id=@ClientId) AND DATE_FORMAT(attendance_date,'%Y-%m')=@Month ORDER BY attendance_date",new{EmployeeId=employeeId,ClientId=clientId,Month=month}); }
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
        if (status == "Approved") await ApplyApprovedLeaveBalanceAsync(db, tx, id);
        await tx.CommitAsync();
    }
    public async Task SyncTravelWorkflowStatusAsync(string resourceId, string status)
    {
        if (!long.TryParse(resourceId, out var id) || status is not ("Approved" or "Rejected" or "Sent Back")) return;
        await using var db=Connection();
        await db.OpenAsync();
        await EnsureTravelTablesAsync(db);
        await db.ExecuteAsync("UPDATE ess_travel_requests SET Status=@Status,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", new { Id = id, Status = status });
        await AuditTravelAsync(db, id, status, $"Workflow {status}");
    }
    public async Task ReconcileLeaveWorkflowStatusesAsync()
    {
        await using var db=Connection();
        await db.OpenAsync();
        await db.ExecuteAsync(@"UPDATE essleaverequests r JOIN workflowinstances w ON w.ResourceType='LeaveRequest' AND w.ResourceId=CAST(r.Id AS CHAR) SET r.Status=w.Status WHERE w.Status IN ('Approved','Rejected','Sent Back') AND r.Status<>w.Status");
    }

    private static async Task ApplyApprovedLeaveBalanceAsync(MySqlConnection db, System.Data.IDbTransaction tx, long requestId)
    {
        var row = await db.QueryFirstOrDefaultAsync<ApprovedLeaveRequestRow>(@"SELECT r.Id,r.EmployeeId,r.ClientId,r.LeaveTypeId,COALESCE(r.DayType,'Full Day') DayType,r.Days,r.ToDate,lt.Code LeaveCode,lt.Type LeaveTypeKind
FROM essleaverequests r
JOIN leave_types lt ON lt.Id=r.LeaveTypeId
WHERE r.Id=@RequestId", new { RequestId = requestId }, tx);
        if (row is null || !row.LeaveTypeKind.Equals("Paid", StringComparison.OrdinalIgnoreCase) || row.LeaveCode.Equals("LWP", StringComparison.OrdinalIgnoreCase)) return;

        var current = await db.ExecuteScalarAsync<decimal?>(@"SELECT balance_count FROM employee_leave_balances
WHERE employee_id=@EmployeeId AND leave_type_id=@LeaveTypeId
ORDER BY balance_date DESC,id DESC LIMIT 1", new { row.EmployeeId, row.LeaveTypeId }, tx) ?? 0;
        var next = current - row.Days;
        var dedupKey = $"LEAVE_REQUEST:{row.Id}";
        var inserted = await db.ExecuteAsync(@"INSERT IGNORE INTO employee_leave_ledger (ClientId,EmployeeId,LeaveTypeId,LeaveCode,TransactionDate,PeriodKey,TransactionType,Quantity,BalanceAfter,ReferenceType,ReferenceId,DedupKey,Remarks)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@LeaveCode,@TransactionDate,@PeriodKey,'Leave Availment',@Quantity,@BalanceAfter,'ESSLeaveRequest',@ReferenceId,@DedupKey,@Remarks);", new { row.ClientId, row.EmployeeId, row.LeaveTypeId, row.LeaveCode, TransactionDate = row.ToDate.Date, PeriodKey = row.ToDate.ToString("yyyy-MM"), Quantity = -row.Days, BalanceAfter = next, ReferenceId = row.Id.ToString(), DedupKey = dedupKey, Remarks = row.DayType }, tx);
        if (inserted == 0) return;
        await db.ExecuteAsync(@"INSERT INTO employee_leave_balances (client_id,employee_id,leave_type_id,balance_date,balance_count)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@BalanceDate,@Balance)
ON DUPLICATE KEY UPDATE balance_count=VALUES(balance_count);", new { row.ClientId, row.EmployeeId, row.LeaveTypeId, BalanceDate = row.ToDate.Date, Balance = next }, tx);
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string table, string column, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @Table AND COLUMN_NAME = @Column", new { Table = table, Column = column });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
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
    public async Task<AttendancePunchValidationResponse> ValidateAttendancePunchAsync(int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await ValidateAttendancePunchAsync(db, employeeId, clientId, request);
    }

    public async Task<AttendancePunchValidationResponse> RecordAttendancePunchAsync(int employeeId, int? clientId, ValidateAttendancePunchRequest request)
    {
        await using var db = Connection();
        await db.OpenAsync();
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
Remarks VARCHAR(500) NOT NULL DEFAULT '',
CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ess_travel_leg_sequence (RequestId, SequenceNo),
INDEX IX_ess_travel_leg_request (RequestId),
INDEX IX_ess_travel_leg_route (FromLocation, ToLocation),
INDEX IX_ess_travel_leg_mode (TravelMode)
);");
        await MigrateLegacyTravelLegsAsync(db);
    }

    private static string TravelRequestSelect(string where) => $@"SELECT r.*, CONCAT(e.FirstName,' ',e.LastName) EmployeeName, COALESCE(p.PolicyName,'') PolicyName, COALESCE(NULLIF(mu.DisplayName,''), CONCAT(m.FirstName,' ',m.LastName), '') ReportingManager
FROM ess_travel_requests r
JOIN employees e ON e.Id=r.EmployeeId
LEFT JOIN travel_policies p ON p.Id=r.PolicyId
LEFT JOIN authusers mu ON mu.Id=e.ReportingManagerUserId
LEFT JOIN employees m ON m.Id=e.ReportingManagerId
WHERE {where}";

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

    private static List<EssTravelValidationResult> ValidateTravelRequest(SaveEssTravelRequest request, bool submit)
    {
        var result = new List<EssTravelValidationResult>();
        void Block(string message) => result.Add(new EssTravelValidationResult { Severity = "Block", Behavior = "Block", Message = message, RuleName = "Mandatory" });
        var legs = NormalizeTravelCities(request);
        if (submit && string.IsNullOrWhiteSpace(request.Purpose)) Block("Purpose of travel is required.");
        if (submit && legs.Count == 0) Block("At least one trip detail row is required.");
        foreach (var leg in legs.Select((value, index) => new { value, index }))
        {
            if (submit && string.IsNullOrWhiteSpace(leg.value.FromLocation)) Block($"Trip row {leg.index + 1}: From location is required.");
            if (submit && string.IsNullOrWhiteSpace(leg.value.ToLocation)) Block($"Trip row {leg.index + 1}: To location is required.");
            if (submit && string.IsNullOrWhiteSpace(leg.value.TravelMode)) Block($"Trip row {leg.index + 1}: Travel mode is required.");
            if (leg.value.EndDateTime.HasValue && leg.value.StartDateTime.HasValue && leg.value.EndDateTime.Value <= leg.value.StartDateTime.Value) Block($"Trip row {leg.index + 1}: End date/time must be after start date/time.");
        }
        if (request.EndDateTime != default && request.StartDateTime != default && request.EndDateTime <= request.StartDateTime) Block("End date/time must be after start date/time.");
        if (request.AdvanceAmount < 0) Block("Advance amount cannot be negative.");
        return result;
    }

    private static async Task<List<EssTravelValidationResult>> ValidateTravelPolicyAsync(MySqlConnection db, long? policyId, SaveEssTravelRequest request, bool submit)
    {
        var messages = new List<EssTravelValidationResult>();
        if (policyId is null) return messages;
        foreach (var travelMode in NormalizeTravelCities(request).Select(item => item.TravelMode).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mode = await db.QueryFirstOrDefaultAsync<(bool IsAllowed, string RuleName, string ExceptionHandling)>(@"SELECT IsAllowed,RuleName,ExceptionHandling FROM travel_policy_rules WHERE PolicyId=@PolicyId AND RuleType='Travel Mode' AND AppliesTo=@TravelMode AND IsActive=TRUE ORDER BY Id DESC LIMIT 1", new { PolicyId = policyId.Value, TravelMode = travelMode });
            if (string.IsNullOrWhiteSpace(mode.RuleName))
            {
                messages.Add(new EssTravelValidationResult { Severity = submit ? "Block" : "Warning", Behavior = "Block", RuleName = "Travel Mode", Message = $"Travel mode {travelMode} is not configured in applicable policy." });
            }
            else if (!mode.IsAllowed)
            {
                messages.Add(new EssTravelValidationResult { Severity = mode.ExceptionHandling == "Block" ? "Block" : "Warning", Behavior = mode.ExceptionHandling, RuleName = mode.RuleName, Message = $"Travel mode {travelMode} is not allowed by policy." });
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

      private static async Task<List<string>> ActiveDropdownValuesAsync(MySqlConnection db, string type)
      {
          var rows = await db.QueryAsync<string>("SELECT Value FROM dropdownmasters WHERE IsActive=TRUE AND Type=@Type ORDER BY Value", new { Type = type });
          return rows.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      }

      private static async Task AttachTravelLegsAsync(MySqlConnection db, List<EssTravelRequest> requests)
      {
          if (requests.Count == 0) return;
          var ids = requests.Select(item => item.Id).ToArray();
          var legs = (await db.QueryAsync<EssTravelLegRow>(@"SELECT RequestId,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode,TravelClass,Remarks
FROM ess_travel_request_legs WHERE RequestId IN @Ids ORDER BY RequestId,SequenceNo", new { Ids = ids })).ToList();
          var grouped = legs.GroupBy(item => item.RequestId).ToDictionary(group => group.Key, group => group.Select(ToTravelCity).ToList());
          foreach (var request in requests)
          {
              request.Legs = grouped.TryGetValue(request.Id, out var rows) ? rows : [new EssTravelCity { FromLocation = request.FromLocation, ToLocation = request.ToLocation, StartDateTime = request.StartDateTime, EndDateTime = request.EndDateTime, TravelMode = request.TravelMode }];
          }
      }

      private static async Task<List<EssTravelCity>> GetTravelLegsAsync(MySqlConnection db, long requestId)
      {
          var rows = await db.QueryAsync<EssTravelLegRow>(@"SELECT RequestId,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode,TravelClass,Remarks
FROM ess_travel_request_legs WHERE RequestId=@RequestId ORDER BY SequenceNo", new { RequestId = requestId });
          return rows.Select(ToTravelCity).ToList();
      }

      private static async Task ReplaceTravelLegsAsync(MySqlConnection db, MySqlTransaction tx, long requestId, List<EssTravelCity> legs)
      {
          await db.ExecuteAsync("DELETE FROM ess_travel_request_legs WHERE RequestId=@RequestId", new { RequestId = requestId }, tx);
          var sequence = 1;
          foreach (var leg in legs)
          {
              await db.ExecuteAsync(@"INSERT INTO ess_travel_request_legs (RequestId,SequenceNo,FromLocation,ToLocation,StartDateTime,EndDateTime,TravelMode,TravelClass,Remarks)
VALUES (@RequestId,@SequenceNo,@FromLocation,@ToLocation,@StartDateTime,@EndDateTime,@TravelMode,@TravelClass,@Remarks)", new { RequestId = requestId, SequenceNo = sequence++, leg.FromLocation, leg.ToLocation, leg.StartDateTime, leg.EndDateTime, leg.TravelMode, leg.TravelClass, leg.Remarks }, tx);
          }
      }

      private static EssTravelCity ToTravelCity(EssTravelLegRow row) => new()
      {
          FromLocation = row.FromLocation,
          ToLocation = row.ToLocation,
          TravelMode = row.TravelMode,
          TravelClass = row.TravelClass,
          Remarks = row.Remarks,
          StartDateTime = row.StartDateTime,
          EndDateTime = row.EndDateTime
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
          Customer = CleanText(employee.ClientName, request.Customer).Trim(),
        Project = request.Project.Trim(),
        CostCenter = request.CostCenter.Trim(),
        request.TravelScope,
          request.TravelType,
            request.Priority,
            FromLocation = CleanText(first?.FromLocation, request.FromLocation).Trim(),
            ToLocation = CleanText(last?.ToLocation, request.ToLocation).Trim(),
            StartDateTime = start,
            EndDateTime = end,
          EstimatedCost = 0,
          PolicyId = policyId,
          TravelMode = CleanText(first?.TravelMode, request.TravelMode),
          request.AccommodationRequired,
          request.LocalConveyanceRequired,
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
      private sealed class EssTravelLegRow { public long RequestId { get; set; } public string FromLocation { get; set; } = ""; public string ToLocation { get; set; } = ""; public DateTime? StartDateTime { get; set; } public DateTime? EndDateTime { get; set; } public string TravelMode { get; set; } = ""; public string TravelClass { get; set; } = ""; public string Remarks { get; set; } = ""; }
      private sealed class EssTravelEmployee { public int EmployeeId { get; set; } public int ClientId { get; set; } public string ClientName { get; set; } = ""; public string EmployeeName { get; set; } = ""; public string Department { get; set; } = ""; public string Designation { get; set; } = ""; public int ReportingManagerId { get; set; } public string ReportingManager { get; set; } = ""; }
    private sealed class EssLeaveSelection { public int Id { get; set; } public int ClientId { get; set; } public string Name { get; set; } = ""; public string Code { get; set; } = ""; public string Type { get; set; } = "Paid"; public decimal Balance { get; set; } public bool AllowNegativeLeaveBalance { get; set; } public bool AllowHalfDay { get; set; } = true; }
    private sealed class ApprovedLeaveRequestRow { public long Id { get; set; } public int EmployeeId { get; set; } public int ClientId { get; set; } public int LeaveTypeId { get; set; } public string DayType { get; set; } = "Full Day"; public decimal Days { get; set; } public DateTime ToDate { get; set; } public string LeaveCode { get; set; } = ""; public string LeaveTypeKind { get; set; } = "Paid"; }
    private sealed class EssPayslipTemplate { public long Id { get; set; } public int ClientId { get; set; } public string Name { get; set; } = "Standard Payslip"; public string Theme { get; set; } = "Classic"; public bool ShowLogo { get; set; } = true; public bool ShowClient { get; set; } = true; public bool ShowYtd { get; set; } = true; public bool ShowBank { get; set; } = true; public string Note { get; set; } = "This is a system generated payslip."; public bool Active { get; set; } = true; }
    private sealed class EssPayslipYtd { public decimal Gross { get; set; } public decimal Deductions { get; set; } public decimal NetPay { get; set; } }
    private sealed class EssPayslipRow { public int PayRunEmployeeId { get; set; } public int PayRunId { get; set; } public int EmployeeId { get; set; } public int ClientId { get; set; } public string EmployeeCode { get; set; } = ""; public string EmployeeName { get; set; } = ""; public string Department { get; set; } = ""; public decimal PresentDays { get; set; } public decimal PayableDays { get; set; } public decimal GrossPay { get; set; } public decimal StatutoryDeductions { get; set; } public decimal OneTimeEarnings { get; set; } public decimal OneTimeDeductions { get; set; } public decimal NetPay { get; set; } public string PaymentStatus { get; set; } = ""; public DateTime? PaymentDate { get; set; } public string DetailsJson { get; set; } = ""; public string PayPeriod { get; set; } = ""; public DateTime PayDate { get; set; } public string RunStatus { get; set; } = ""; public int TotalWorkingDays { get; set; } public string ClientName { get; set; } = ""; public string WorkEmail { get; set; } = ""; public string Designation { get; set; } = ""; public DateTime? DateOfJoining { get; set; } public string WorkLocation { get; set; } = ""; public string Address { get; set; } = ""; public string PanNumber { get; set; } = ""; public string UanNumber { get; set; } = ""; public string BankName { get; set; } = ""; public string BankAccountNo { get; set; } = ""; public string IfscCode { get; set; } = ""; }
}
