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
}
