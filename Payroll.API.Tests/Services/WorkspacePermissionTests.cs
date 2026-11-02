using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class WorkspacePermissionTests
{
    private static AuthUser User(params string[] permissions)=>new(){Id=1,IsActive=true,Permissions=[..permissions]};
    [Fact]
    public void NarrowReportGrantsDoNotOpenEmployeeAttendanceAuditOrAnotherReportFamily()
    {
        var payroll=User("reports.payroll.view");
        Assert.True(WorkspacePermissions.CanReadReport(payroll,"salary-register"));
        Assert.True(WorkspacePermissions.CanReadPayslips(payroll));
        foreach(var code in new[]{"employee-master","daily-attendance","leave-balance","pf-register","client-billing-report","unknown"})Assert.False(WorkspacePermissions.CanReadReport(payroll,code));
        var compliance=User("reports.compliance.view");
        Assert.True(WorkspacePermissions.CanReadReport(compliance,"pf-ecr-report"));
        Assert.True(WorkspacePermissions.CanReadReport(compliance,"form16-register"));
        Assert.False(WorkspacePermissions.CanReadPayslips(compliance));
        Assert.False(WorkspacePermissions.CanReadReport(compliance,"salary-register"));
    }
    [Fact]
    public void BillingIsSeparateFromSettingsAndOperationalPayrollAuthority()
    {
        var billing=User("billing.manage","reports.billing.view");
        Assert.True(WorkspacePermissions.CanManageBilling(billing));
        Assert.True(WorkspacePermissions.CanReadReport(billing,"client-billing-report"));
        Assert.False(WorkspacePermissions.CanReadReport(billing,"employee-master"));
        Assert.False(WorkspacePermissions.CanReadPayslips(billing));
        Assert.False(WorkspacePermissions.CanReadPayrollAttendance(billing));
        Assert.DoesNotContain("settings.manage",billing.Permissions);
        billing.ClientId=123;
        Assert.False(WorkspacePermissions.CanManageBilling(billing));
    }
    [Fact]
    public void LegacyBroadPermissionsKeepTheirExistingScopeAndGrantsCombineAdditively()
    {
        Assert.True(WorkspacePermissions.CanReadReport(User("reports.view"),"employee-master"));
        Assert.True(WorkspacePermissions.CanReadReport(User("reports.view"),"daily-attendance"));
        Assert.True(WorkspacePermissions.CanManageBilling(User("settings.manage")));
        var combined=User("reports.payroll.view","reports.compliance.view");
        Assert.True(WorkspacePermissions.CanReadReport(combined,"pf-register"));
        Assert.True(WorkspacePermissions.CanReadReport(combined,"salary-register"));
        Assert.False(WorkspacePermissions.CanManageBilling(combined));
        Assert.True(WorkspacePermissions.CanReadPayrollContext(combined));
    }
}
