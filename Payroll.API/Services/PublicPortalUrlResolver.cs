using Microsoft.AspNetCore.Http;

namespace Payroll.API.Services;

public sealed class PublicPortalUrlResolver(
    IHttpContextAccessor httpContextAccessor,
    IWebHostEnvironment environment)
{
    public string ResolveBaseUrl(string? configuredBaseUrl)
    {
        var configured = ParseOrigin(configuredBaseUrl);
        var runtime = RuntimeOrigin();
        if (runtime is not null && (configured is null || configured.IsLoopback))
            return runtime.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        if (configured is null || (!environment.IsDevelopment() && configured.IsLoopback))
            return "";
        var configuredBuilder = new UriBuilder(configured) { Query = "", Fragment = "" };
        return configuredBuilder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private Uri? RuntimeOrigin()
    {
        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null) return null;

        var origin = request.Headers.Origin.FirstOrDefault();
        var runtime = ParseOrigin(origin);
        if (runtime is null && Uri.TryCreate(request.Headers.Referer.FirstOrDefault(), UriKind.Absolute, out var referer))
            runtime = ParseOrigin(referer.GetLeftPart(UriPartial.Authority));
        if (runtime is null && request.Host.HasValue)
            runtime = ParseOrigin($"{request.Scheme}://{request.Host.Value}");
        if (runtime is null || (!environment.IsDevelopment() && (runtime.IsLoopback || runtime.Scheme != Uri.UriSchemeHttps)))
            return null;
        return runtime;
    }

    private static Uri? ParseOrigin(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
            return null;
        return uri;
    }
}
