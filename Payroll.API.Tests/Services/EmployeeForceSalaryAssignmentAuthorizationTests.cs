using Payroll.API.Models;

namespace Payroll.API.Tests.Services;

public sealed class EmployeeForceSalaryAssignmentAuthorizationTests
{
    private static AuthUser Administrator() => new()
    {
        Id = 73, IsActive = true, Roles = ["SUPER_ADMIN"], Permissions = ["employees.manage"]
    };

    [Fact]
    public void Force_uses_the_authenticated_administrators_id()
    {
        var request = new EmployeeFileUploadRequest { ForceSalaryAssignment = true };
        Assert.True(request.TryAuthorizeForce(Administrator(), out var actor));
        Assert.Equal("Admin #73", actor);
    }

    [Fact]
    public void The_ordinary_assignment_does_not_request_force_privileges()
    {
        var user = new AuthUser { Id = 12, IsActive = true, Permissions = ["employees.manage"] };
        Assert.True(new EmployeeFileUploadRequest().TryAuthorizeForce(user, out var actor));
        Assert.Null(actor);
    }

    [Fact]
    public void Deleting_test_payroll_requires_explicit_consent_and_employee_approval_and_payment_permissions()
    {
        var admin = Administrator();
        var request = new EmployeeFileUploadRequest { RemoveTestPayroll = true };
        Assert.False(request.TryAuthorizeTestCleanup(admin, out _));
        admin.Permissions.Add("payroll.approve");
        Assert.False(request.TryAuthorizeTestCleanup(admin, out _));
        admin.Permissions.Add("payroll.payments");
        Assert.True(request.TryAuthorizeTestCleanup(admin, out var actor));
        Assert.Equal("Admin #73", actor);
        admin.Roles.Clear();
        Assert.False(request.TryAuthorizeTestCleanup(admin, out _));
        Assert.True(new EmployeeFileUploadRequest().TryAuthorizeTestCleanup(admin, out actor));
        Assert.Null(actor);
    }

    [Fact]
    public void Force_requires_an_unrestricted_active_super_admin_with_employee_management_access()
    {
        var request = new EmployeeFileUploadRequest { ForceSalaryAssignment = true };
        Action<AuthUser>[] restrictions =
        [
            u => u.IsActive = false,
            u => u.MustChangePassword = true,
            u => u.ClientId = 4,
            u => { u.ClientAccessMode = "Selected"; u.AssignedClientIds = [4]; },
            u => u.Permissions.Clear(),
            u => { u.Roles = ["hr_manager"]; u.Permissions.Add("settings.manage"); },
            u => u.Id = 0
        ];
        foreach (var restrict in restrictions)
        {
            var user = Administrator();
            restrict(user);
            Assert.False(request.TryAuthorizeForce(user, out var actor));
            Assert.Null(actor);
        }
    }
}
