namespace Payroll.API.Models;

public class ClientFileUploadRequest
{
    public int ClientId { get; set; }
    public IFormFile? File { get; set; }
    public string Mode { get; set; } = "upsert";
    public Guid? ReviewToken { get; set; }
    public string DecisionsJson { get; set; } = string.Empty;
}

public class LeaveBalancePreviewUploadRequest : ClientFileUploadRequest
{
    public string Encoding { get; set; } = string.Empty;
    public string? MappingJson { get; set; }
}

public sealed class EmployeeFileUploadRequest : ClientFileUploadRequest
{
    public bool ForceSalaryAssignment { get; set; }
    public bool RemoveTestPayroll { get; set; }

    internal bool TryAuthorizeTestCleanup(AuthUser user, out string? actor)
    {
        actor = null;
        if (!RemoveTestPayroll) return true;
        if (ForceSalaryAssignment || !IsAssignmentAdmin(user)
            || !user.Permissions.Contains("payroll.approve", StringComparer.OrdinalIgnoreCase)
            || !user.Permissions.Contains("payroll.payments", StringComparer.OrdinalIgnoreCase)) return false;
        actor = $"Admin #{user.Id}";
        return true;
    }

    private static bool IsAssignmentAdmin(AuthUser user) =>
        Services.SelectedClientAccess.IsUnrestrictedAdministrator(user)
        && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase)
        && user.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase);

    internal bool TryAuthorizeForce(AuthUser user, out string? forceSalaryActor)
    {
        forceSalaryActor = null;
        if (!ForceSalaryAssignment) return true;
        if (!IsAssignmentAdmin(user)) return false;

        forceSalaryActor = $"Admin #{user.Id}";
        return true;
    }
}
