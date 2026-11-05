using Microsoft.Extensions.Configuration;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientOwnershipCatalogTests
{
    [Theory]
    [InlineData("/api/employees/81", "employee", "81")]
    [InlineData("/api/pay-runs/12/export", "payrun", "12")]
    [InlineData("/api/tax-engine/employee-profiles/81", "employee", "81")]
    [InlineData("/api/recruitment/interviews/rounds/42", "application", "42")]
    [InlineData("/api/recruitment/interviews/scheduling-context/42", "application", "42")]
    [InlineData("/api/recruitment/internal-interviews/questions/7", "interview-question", "7")]
    [InlineData("/api/recruitment/document-roster/rows/9/draft", "document-roster", "9")]
    [InlineData("/api/recruitment-orchestration/forms/7/versions", "form", "7")]
    [InlineData("/api/employee-communications/campaigns/25/retry-failed", "communication-campaign", "25")]
    [InlineData("/api/ess/admin/expenses/claims/25/revert", "expense-claim", "25")]
    [InlineData("/api/ess/recruitment/requisitions/25/submit", "requisition", "25")]
    [InlineData("/api/workflows/tasks/25/approve", "workflow-task", "25")]
    [InlineData("/api/client-billing/advanced/lines/25", "billing-line", "25")]
    [InlineData("/api/travel-expense/policy/25", "travel-policy", "25")]
    [InlineData("/api/recruitment/clients/25/final-offer-settings", "client", "25")]
    [InlineData("/api/attachments/607c7de1-45ec-4921-97a5-67a32982604f/content", "attachment", "607c7de1-45ec-4921-97a5-67a32982604f")]
    [InlineData("/api/clients/20/pf-policy-versions/607c7de145ec492197a567a32982604f/publish", "pf-policy-version", "607c7de145ec492197a567a32982604f")]
    [InlineData("/api/notifications/recruitment-triggers/8", "recruitment-notification-rule", "8")]
    [InlineData("/api/leave-attendance/attendance/daily/batch-jobs/607c7de1-45ec-4921-97a5-67a32982604f", "attendance-job", "607c7de1-45ec-4921-97a5-67a32982604f")]
    [InlineData("/api/employees/import-jobs/607c7de1-45ec-4921-97a5-67a32982604f", "employee-import-job", "607c7de1-45ec-4921-97a5-67a32982604f")]
    [InlineData("/api/leave-attendance/leave-types/import-jobs/607c7de1-45ec-4921-97a5-67a32982604f", "leave-type-import-job", "607c7de1-45ec-4921-97a5-67a32982604f")]
    [InlineData("/api/leave-attendance/holidays/import-jobs/607c7de1-45ec-4921-97a5-67a32982604f", "holiday-import-job", "607c7de1-45ec-4921-97a5-67a32982604f")]
    public void IdRoutesResolveTheActualResource(string path, string resource, string value)
    {
        Assert.Contains(new ScopeOwnershipReference(resource, value), SelectedClientOwnershipCatalog.RouteReferences(path));
        Assert.False(SelectedClientOwnershipCatalog.HasUnmappedRouteIdentity(path));
    }

    [Theory]
    [InlineData("/api/pay-runs/12/employees/81", "employee", "81")]
    [InlineData("/api/pay-runs/12/payslips/81", "employee", "81")]
    [InlineData("/api/recruitment/applications/42/checklist/9/complete", "candidate-checklist", "9")]
    [InlineData("/api/recruitment/applications/42/score-jobs/9", "scoring-job", "9")]
    [InlineData("/api/recruitment-orchestration/applications/42/transitions/9", "pipeline-transition", "9")]
    public void BothParentAndNestedChildAreChecked(string path, string child, string childId)
    {
        var references = SelectedClientOwnershipCatalog.RouteReferences(path);
        Assert.Equal(2, references.Count);
        Assert.Contains(new ScopeOwnershipReference(child, childId), references);
    }

    [Theory]
    [InlineData("/api/employees", "employee")]
    [InlineData("/api/travel-expense/rules", "travel-rule")]
    [InlineData("/api/attachment-attributes", "attachment-attribute")]
    [InlineData("/api/attachment-configurations", "attachment-configuration")]
    [InlineData("/api/leave-attendance/attendance-settings", "attendance-settings")]
    [InlineData("/api/recruitment-admin/ai-scoring", "ai-setting")]
    [InlineData("/api/notifications/recruitment-triggers", "recruitment-notification-rule")]
    [InlineData("/api/recruitment-orchestration/forms/7/versions", "form-version")]
    [InlineData("/api/recruitment-orchestration/pipelines/7/versions", "pipeline-version")]
    [InlineData("/api/recruitment-orchestration/pipeline-stages/7/actions", "pipeline-stage-action")]
    public void SavedRecordIdHasIndependentOwnership(string path, string resource)
    {
        Assert.True(SelectedClientOwnershipCatalog.TryRootReference(path, "8", out var reference));
        Assert.Equal(new ScopeOwnershipReference(resource, "8"), reference);
    }

    [Theory]
    [InlineData("/api/new-workspace", "employeeId", "8", "employee")]
    [InlineData("/api/payroll/simulate", "templateId", "8", "salary-structure")]
    [InlineData("/api/recruitment/offers", "templateId", "8", "recruitment-template")]
    [InlineData("/api/recruitment/hiring-cases/7", "pipelineStageInstanceId", "8", "position-stage-instance")]
    [InlineData("/api/recruitment-orchestration/applications/7", "pipelineStageInstanceId", "8", "application-stage-instance")]
    [InlineData("/api/travel-expense/rules", "policyId", "8", "travel-policy")]
    public void ForeignKeysUseTheirWorkspaceContext(string path, string key, string value, string resource)
    {
        Assert.True(SelectedClientOwnershipCatalog.TryReference(path, key, value, out var reference));
        Assert.Equal(resource, reference.Resource);
        Assert.Equal(value, reference.Value);
    }

    [Theory]
    [InlineData("directClientId", "client", false)]
    [InlineData("existingEmployeeId", "employee", false)]
    [InlineData("reportingManagerId", "employee", false)]
    [InlineData("jobDescriptionTemplateId", "recruitment-template", true)]
    [InlineData("defaultAtsScoringProfileId", "scoring-profile", true)]
    [InlineData("fieldConfigurationId", "attachment-configuration", true)]
    [InlineData("attachmentAttributeId", "attachment-attribute", true)]
    [InlineData("includedEmployeeIds", "employee", false)]
    [InlineData("excludedEmployeeIds", "employee", false)]
    [InlineData("adjustmentIds", "adjustment", false)]
    [InlineData("attendanceGroupIds", "attendance-group", false)]
    public void SuppliedReferencesCannotCrossClientViaAlternativePropertyNames(string property, string resource, bool shared)
    {
        Assert.True(SelectedClientOwnershipCatalog.TryReference(property, "8", out var reference));
        Assert.Equal(new ScopeOwnershipReference(resource, "8", shared), reference);
    }

    [Theory]
    [InlineData(20, "20:TMT,21:Other", 20, 20)]
    [InlineData(0, "20:TMT,21:Other", 21, 21)]
    [InlineData(0, "20:TMT,21:Other", 99, 20)]
    [InlineData(20, "", 20, 20)]
    [InlineData(20, "", 21, 20)]
    [InlineData(0, "", 20, 0)]
    [InlineData(0, "All", 20, 0)]
    [InlineData(0, "unknown", 20, null)]
    public void SharedSalaryStructuresResolveOnlyTheirDeclaredClients(int stored, string clientReference, int active, int? expected) =>
        Assert.Equal(expected, SelectedClientOwnershipStore.ResolveSalaryStructureOwner(stored, clientReference, active));

    [Fact]
    public void SharedReferenceUseDoesNotGrantSharedRootWriteAccess()
    {
        Assert.True(SelectedClientOwnershipCatalog.TryReference("workflowId", "8", out var reference));
        Assert.True(reference.AllowGlobal);
        Assert.True(SelectedClientOwnershipCatalog.TryRootReference("/api/recruitment-orchestration/forms", "8", out var root));
        Assert.False(root.AllowGlobal);
        Assert.True(SelectedClientOwnershipCatalog.IsSharedReadReference(root));
    }

    [Theory]
    [InlineData("RecruitmentPipelineTransition", "CASE:8", "hiring-case-transition", "8")]
    [InlineData("RecruitmentPipelineTransition", "MOM:8", "process-document", "8")]
    [InlineData("RecruitmentPipelineTransition", "8", "application-transition", "8")]
    [InlineData("EmployeeProfileFirstEdit", "8", "client", "8")]
    [InlineData("RecruitmentOpenPosition", "8", "position", "8")]
    public void WorkflowResourcesResolveBusinessOwnershipRatherThanWorkflowDefinitions(string entityType, string id, string resource, string value)
    {
        Assert.True(SelectedClientOwnershipCatalog.TryEntityReference(entityType, id, out var reference));
        Assert.Equal(new ScopeOwnershipReference(resource, value), reference);
    }

    [Theory]
    [InlineData("POST", "/api/dropdowns")]
    [InlineData("GET", "/api/security/users")]
    [InlineData("GET", "/api/excel-payslips/batches")]
    [InlineData("POST", "/api/client-billing/module")]
    [InlineData("POST", "/api/recruitment/applications/8/global-talent-pool")]
    [InlineData("POST", "/api/travel-expense/categories")]
    [InlineData("DELETE", "/api/recruitment/unmapped-resource/8")]
    [InlineData("GET", "/api/new-workspace")]
    [InlineData("GET", "/api/employees/8/unknown-child/9")]
    [InlineData("GET", "/api/employees/8/unknown-child/8")]
    public void GlobalAndUnmappedResourceOperationsFailClosed(string method, string path) =>
        Assert.False(SelectedClientOwnershipCatalog.IsAllowedScopedRequest(method, path));

    [Theory]
    [InlineData("GET", "/api/setup")]
    [InlineData("GET", "/api/attachment-configurations")]
    [InlineData("POST", "/api/attachment-configurations")]
    [InlineData("POST", "/api/attachment-attributes")]
    [InlineData("GET", "/api/workflows")]
    [InlineData("POST", "/api/setup")]
    [InlineData("GET", "/api/client-billing/module")]
    [InlineData("GET", "/api/travel-expense/setup")]
    [InlineData("GET", "/api/recruitment-orchestration/lookups")]
    [InlineData("POST", "/api/recruitment/applications")]
    [InlineData("GET", "/api/reports/payroll-summary")]
    [InlineData("GET", "/api/employees/import-jobs/607c7de1-45ec-4921-97a5-67a32982604f")]
    [InlineData("GET", "/api/leave-attendance/holidays/import-jobs/607c7de1-45ec-4921-97a5-67a32982604f")]
    public void ScopedCommercialFlowsRemainAvailable(string method, string path) =>
        Assert.True(SelectedClientOwnershipCatalog.IsAllowedScopedRequest(method, path));

    [Fact]
    public async Task CallerCannotChooseAnArbitraryTableOrSql()
    {
        Assert.False(SelectedClientOwnershipCatalog.TryReference("employees;DROP TABLE clients", "1", out _));
        Assert.False(SelectedClientOwnershipCatalog.TryReference("employeeId", "1 OR 1=1", out _));
        Assert.False(SelectedClientOwnershipCatalog.TryEntityReference("employees", "1", out _));
        Assert.False(SelectedClientOwnershipCatalog.TryRootReference("/api/unknown", "8", out _));
        var lookup = new SelectedClientOwnershipStore(new ConfigurationBuilder().Build());
        Assert.Null(await lookup.ClientIdAsync(new("arbitrary-table", "1")));
    }
}
