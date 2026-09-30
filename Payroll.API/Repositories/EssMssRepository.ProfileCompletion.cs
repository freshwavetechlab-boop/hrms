using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Text.Json;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    internal const string ProfileEditResource = "EmployeeProfileEdit";
    internal const string ProfileEditPolicyResource = "EmployeeProfileFirstEdit";

    internal static bool IsProfileEditAllowed(string? state, bool hasSaved, bool firstEditEnabled) =>
        state == "Approved" || (string.IsNullOrEmpty(state) && !hasSaved && firstEditEnabled);

    internal static async Task<(bool CanEdit, string Status)> ProfileEditAccessAsync(MySqlConnection db, int employeeId, MySqlTransaction? tx = null)
    {
        var row = await db.QueryFirstOrDefaultAsync<ProfileEditAccessRow>(@"SELECT e.IsActive,
COALESCE(r.CurrentState,'') State,
EXISTS(SELECT 1 FROM ess_profile_update_audit a WHERE a.EmployeeId=e.Id) HasSaved,
COALESCE(policy.CurrentState,'Enabled')<>'Disabled' FirstEditEnabled
FROM employees e
LEFT JOIN ResourceStates r ON r.ResourceType='EmployeeProfileEdit' AND r.ResourceId=CAST(e.Id AS CHAR)
LEFT JOIN ResourceStates policy ON policy.ResourceType='EmployeeProfileFirstEdit' AND policy.ResourceId=CAST(e.ClientId AS CHAR)
WHERE e.Id=@EmployeeId", new { EmployeeId = employeeId }, tx);
        if (row is null || !row.IsActive) return (false, "Unavailable");
        var allowed = IsProfileEditAllowed(row.State, row.HasSaved, row.FirstEditEnabled);
        return (allowed, allowed ? "Editable" : row.State == "Pending" ? "Pending approval" : "Locked");
    }

    public async Task<bool> GetFirstProfileEditAsync(int clientId)
    {
        await using var db = Connection(); await db.OpenAsync();
        return await db.ExecuteScalarAsync<string?>("SELECT CurrentState FROM ResourceStates WHERE ResourceType=@ResourceType AND ResourceId=@Id", new { ResourceType = ProfileEditPolicyResource, Id = clientId.ToString() }) != "Disabled";
    }

    public async Task SetFirstProfileEditAsync(int clientId, bool enabled, int actor)
    {
        await using var db = Connection(); await db.OpenAsync();
        await db.ExecuteAsync(@"INSERT INTO ResourceStates (ResourceType,ResourceId,CurrentState,CreatedBy,ModifiedBy)
VALUES (@ResourceType,@Id,@State,@Actor,@Actor)
ON DUPLICATE KEY UPDATE CurrentState=@State,ModifiedBy=@Actor,ModifiedOn=UTC_TIMESTAMP()", new { ResourceType = ProfileEditPolicyResource, Id = clientId.ToString(), State = enabled ? "Enabled" : "Disabled", Actor = actor });
    }

    public async Task<(WorkflowInstance? Instance, string Error)> RequestProfileEditAsync(AuthUser user, string reason, WorkflowRepository workflows)
    {
        if (!user.EmployeeId.HasValue) return (null, "Your account is not linked to an employee.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) return (null, "Enter an edit reason, up to 1,000 characters.");
        await using var db = Connection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var employee = await db.QueryFirstOrDefaultAsync<Employee>("SELECT * FROM employees WHERE Id=@Id AND IsActive=TRUE AND (@ClientId IS NULL OR ClientId=@ClientId) FOR UPDATE", new { Id = user.EmployeeId.Value, user.ClientId }, tx);
        if (employee is null) return (null, "Active employee profile was not found.");
        var access = await ProfileEditAccessAsync(db, employee.Id, tx);
        if (access.CanEdit) return (null, "You already have one profile save available.");
        if (access.Status == "Pending approval") return (null, "Your edit request is already pending approval.");
        // Use a configured workflow, or the same client-scoped employee-administrator fallback.
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { employee.ClientId }, tx);
        var workflowId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM workflowmasters WHERE ResourceType=@ResourceType AND IsActive=TRUE AND (ClientId=@ClientId OR ClientId IS NULL) ORDER BY ClientId IS NULL,Id LIMIT 1", new { ResourceType = ProfileEditResource, employee.ClientId }, tx);
        if (!workflowId.HasValue)
        {
            if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM workflowmasters WHERE ResourceType=@ResourceType AND (ClientId=@ClientId OR ClientId IS NULL))", new { ResourceType = ProfileEditResource, employee.ClientId }, tx))
                return (null, "The profile-edit approval workflow is disabled. Ask HR to enable it.");
            workflowId = await db.ExecuteScalarAsync<int>(@"INSERT INTO workflowmasters (ClientId,Code,Name,ResourceType,IsActive)
VALUES (@ClientId,'ESS_PROFILE_EDIT','Employee profile edit approval','EmployeeProfileEdit',TRUE)
; SELECT LAST_INSERT_ID();", new { employee.ClientId }, tx);
            await db.ExecuteAsync(@"INSERT IGNORE INTO workflowstages (WorkflowId,StageOrder,Name,ApproverType)
VALUES (@WorkflowId,1,'Approve one-time employee profile edit','Employee Administrator')", new { WorkflowId = workflowId }, tx);
        }
        var instance = await workflows.StartInTransactionAsync(db, tx, new StartWorkflowRequest
        {
            WorkflowId = workflowId.Value, ResourceType = ProfileEditResource, ResourceId = employee.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { Employee = (employee.FirstName + " " + employee.LastName).Trim(), employee.EmployeeCode, employee.ClientId, Reason = reason.Trim(), Access = "One successful profile save; locks again afterwards" })
        }, user.Id);
        if (instance is null) return (null, "No authorised employee administrator is available. Ask HR to configure the profile-edit approval workflow.");
        await tx.CommitAsync();
        return (instance, "");
    }

    private sealed class ProfileEditAccessRow
    {
        public bool IsActive { get; set; }
        public string State { get; set; } = "";
        public bool HasSaved { get; set; }
        public bool FirstEditEnabled { get; set; }
    }
}
