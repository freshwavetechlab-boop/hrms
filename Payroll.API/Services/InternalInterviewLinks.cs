using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

public sealed class InternalInterviewLinks(IDataProtectionProvider provider, IConfiguration configuration, TimeProvider time)
{
    private readonly IDataProtector protector = provider.CreateProtector("HRMS.InternalInterview.Candidate.v1");

    public Uri PortalOrigin()
    {
        var baseUrl = configuration["InternalInterviews:PublicPortalBaseUrl"] ?? configuration["PublicPortal:BaseUrl"];
        Require(Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) && origin.UserInfo == ""
            && (origin.Scheme == "https" || (origin.Scheme == "http" && origin.IsLoopback)),
            "Configure the internal interview portal HTTPS base URL (localhost HTTP is allowed for testing).", 503);
        return origin!;
    }

    public InterviewLink Create(InternalInterviewSession session)
    {
        var origin = PortalOrigin();
        var ticket = new InterviewCandidateTicket(session.InterviewId, session.LinkVersion, session.LinkExpiresAtUtc);
        var token = protector.Protect(JsonSerializer.Serialize(ticket));
        // Fragments are not sent to the web server or Referer. The client exchanges this via a header.
        return new($"{origin!.GetLeftPart(UriPartial.Authority)}/interview/{session.InterviewId}#access={Uri.EscapeDataString(token)}", session.LinkExpiresAtUtc);
    }

    public InterviewCandidateTicket Read(string? token)
    {
        Require(!string.IsNullOrWhiteSpace(token) && token.Length <= 4096, "A valid candidate interview link is required.", 401);
        try
        {
            var ticket = JsonSerializer.Deserialize<InterviewCandidateTicket>(protector.Unprotect(token!));
            Require(ticket is not null && ticket.InterviewId > 0 && Guid.TryParseExact(ticket.LinkVersion, "N", out _)
                && ticket.ExpiresAtUtc > time.GetUtcNow().UtcDateTime, "The candidate interview link is invalid or expired.", 401);
            return ticket!;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        { throw new InternalInterviewException(401, "The candidate interview link is invalid or expired."); }
    }
}
