using Payroll.API.Models;
using Payroll.API.Repositories;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

public sealed record InternalInterviewInvitation(string CandidateUrl, string PanelUrl);
public sealed class InternalInterviewInvitationService(IConfiguration configuration, InternalInterviewRepository repository, InternalInterviewLinks links)
{
    public async Task<InternalInterviewInvitation?> ResolveAsync(long id, AuthUser user)
    {
        if (!configuration.GetValue("InternalInterviews:Enabled", false)) return null;
        if (!await repository.HasInternalSessionAsync(id, user)) return null;
        InternalInterviewRuntimeState.Read(configuration).RequireAdmission();
        var access = await repository.AccessAsync(id, user);
        RequireOpen(access.Context, access.Session, DateTime.UtcNow);
        // Resending an invite must not revoke a working candidate link/consent.
        return new(links.Create(access.Session).Url, new Uri(links.PortalOrigin(), "/recruitment/interview-session/" + id).AbsoluteUri);
    }
}
