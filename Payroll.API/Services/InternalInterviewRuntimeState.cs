using Payroll.API.Models;

namespace Payroll.API.Services;

// Deployment settings only: no browser-supplied override and no change to the global worker switch.
public sealed record InternalInterviewRuntimeState(bool Enabled, bool Draining, bool Maintenance)
{
    public bool AcceptingNewSessions => Enabled && !Draining;
    public static InternalInterviewRuntimeState Read(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("InternalInterviews:Enabled", false);
        return new(enabled, configuration.GetValue("InternalInterviews:DrainMode", false),
            enabled || configuration.GetValue("InternalInterviews:MaintenanceEnabled", false));
    }
    public void RequireAdmission()
    {
        if (!AcceptingNewSessions)
            throw new InternalInterviewException(503, Enabled
                ? "Internal interviews are draining for maintenance. Existing live sessions can finish; new sessions cannot start. Contact HR to reschedule."
                : "Internal interviews are not enabled on this API.");
    }
}
