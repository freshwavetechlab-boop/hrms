using Payroll.API.Models;

namespace Payroll.API.Services;

public static class RecruitmentPermissions
{
    public static bool CanViewRequisitions(AuthUser user) => Has(user, "recruitment.rfr.view", "recruitment.rfr.create");
    public static bool CanCreateRequisitions(AuthUser user) => Has(user, "recruitment.rfr.create");
    public static bool CanViewApplicationForms(AuthUser user) => Has(user, "recruitment.candidate.manage", "recruitment.rfr.create", "recruitment.publish", "recruitment.configuration.manage");
    public static bool CanViewJobPostings(AuthUser user) => Has(user, "recruitment.position.view", "recruitment.position.manage", "recruitment.publish");
    public static bool CanViewOrchestrationLookups(AuthUser user) => CanViewJobPostings(user) || CanViewApplicationForms(user);
    public static bool CanViewJobDescriptions(AuthUser user) => CanViewRequisitions(user) || CanViewJobPostings(user);
    public static bool CanPublishJobs(AuthUser user) => Has(user, "recruitment.publish");
    public static bool CanConfigure(AuthUser user) => Has(user, "recruitment.configuration.manage");
    public static bool IsAdmin(AuthUser user) => user.Permissions.Contains("settings.manage")
        || user.ClientId is null && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

    public static bool Has(AuthUser user, params string[] permissions) => IsAdmin(user)
        || user.Permissions.Contains("recruitment.manage")
        || permissions.Any(code => user.Permissions.Contains(code, StringComparer.OrdinalIgnoreCase));
}
