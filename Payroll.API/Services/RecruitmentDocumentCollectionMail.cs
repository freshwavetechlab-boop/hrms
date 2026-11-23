using System.Net;

namespace Payroll.API.Services;

public static class RecruitmentDocumentCollectionMail
{
    public static string BuildUploadUrl(IConfiguration configuration, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidOperationException("The document upload link is unavailable.");
        var configured = configuration["PublicPortal:BaseUrl"]?.Trim();
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var portal)
            || (portal.Scheme != Uri.UriSchemeHttps && !(portal.Scheme == Uri.UriSchemeHttp && portal.IsLoopback))
            || !string.IsNullOrEmpty(portal.UserInfo) || !string.IsNullOrEmpty(portal.Query) || !string.IsNullOrEmpty(portal.Fragment))
            throw new InvalidOperationException("Configure the public portal HTTPS address before sending document upload links.");
        return portal.AbsoluteUri.TrimEnd('/') + "/candidate-documents/" + Uri.EscapeDataString(token);
    }

    public static string AppendUploadLink(string body, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link)
            || (link.Scheme != Uri.UriSchemeHttps && !(link.Scheme == Uri.UriSchemeHttp && link.IsLoopback))
            || !string.IsNullOrEmpty(link.UserInfo))
            throw new InvalidOperationException("The document upload link is unavailable.");
        return body + "<p>Please upload your requested joining documents using the link below. No login is required. Enter this email address and submit all documents together once.</p>"
            + "<p><a href=\"" + WebUtility.HtmlEncode(url) + "\">Upload joining documents</a></p>"
            + "<p>After submission, this link will show Already submitted.</p>";
    }
}
