using Payroll.API.Models;

namespace Payroll.API.Services;

// A path rule can be shared by every client. Its explicitly chosen workflow
// still belongs to the workflow master's scope; it is not a tenant override.
internal sealed record WorkflowActionResolution(int? WorkflowId, int? ClientId);

internal static class WorkflowClientScope
{
    internal static bool Applies(WorkflowMaster workflow, int? clientId) => workflow.IsActive
        && (workflow.ClientId is null || workflow.ClientId > 0 && workflow.ClientId == clientId);

    internal static int? ResolveClient(int? suppliedClientId, bool knownResource, int? resourceClientId)
    {
        if (suppliedClientId is <= 0) suppliedClientId = null;
        if (!knownResource) return suppliedClientId;
        if (resourceClientId is null or <= 0)
            throw new InvalidOperationException("The workflow resource has no verified client owner. No approval task was created.");
        if (suppliedClientId.HasValue && suppliedClientId != resourceClientId)
            throw new InvalidOperationException("Workflow client metadata differs from the actual resource owner. No approval task was created.");
        return resourceClientId;
    }

    internal static int? Select(WorkflowActionRule rule, int? clientId, IReadOnlyList<WorkflowMaster> workflows)
    {
        if (rule.WorkflowId is int explicitId)
        {
            var chosen = workflows.SingleOrDefault(workflow => workflow.Id == explicitId);
            if (chosen is null || !chosen.IsActive || chosen.ClientId is <= 0)
                throw new InvalidOperationException("The configured workflow is missing, inactive or has an invalid client scope. Review its setup before approval.");
            if (Applies(chosen, clientId)) return chosen.Id;
            if (clientId is null)
                throw new InvalidOperationException("The resource client must be verified before selecting a client-specific workflow.");
        }

        // A foreign-client explicit workflow is inapplicable, not a global
        // default. Keep the existing same-client-before-global resolution.
        return workflows.Where(workflow => string.Equals(workflow.Code, rule.ActivityCode, StringComparison.OrdinalIgnoreCase)
                && Applies(workflow, clientId))
            .OrderBy(workflow => workflow.ClientId is null).ThenBy(workflow => workflow.Id)
            .Select(workflow => (int?)workflow.Id).FirstOrDefault();
    }

    internal static void RequireStartScope(WorkflowMaster workflow, int? expectedClientId, bool knownResource, int? resourceClientId)
    {
        var clientId = ResolveClient(expectedClientId, knownResource, resourceClientId);
        // Uncatalogued legacy custom resources without an expected scope retain
        // their existing manual-start contract. Known payroll resources and action
        // middleware calls always prove scope before any task/history insert.
        if (!knownResource && expectedClientId is null) return;
        if (!Applies(workflow, clientId))
            throw new InvalidOperationException("This workflow does not apply to the actual resource client. No approval task was created.");
    }
}
