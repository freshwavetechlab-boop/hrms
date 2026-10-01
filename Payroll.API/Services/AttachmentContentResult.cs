using System.Net.Http.Headers;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class AttachmentContentResult(
    AttachmentStorageService storageService,
    AttachmentStorageServer server,
    EntityAttachment attachment,
    bool inline) : IResult
{
    public static bool CanPreview(string mimeType) => mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        AttachmentFileHandle handle;
        try { handle = await OpenAsync(httpContext); }
        catch (FileNotFoundException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            httpContext.Response.ContentType = "text/plain; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "private, no-store";
            await httpContext.Response.WriteAsync("This attachment is unavailable in the configured storage. Ask HR to restore or re-upload the original file.", httpContext.RequestAborted);
            return;
        }
        await using var content = handle;
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = string.IsNullOrWhiteSpace(attachment.DetectedMimeType) ? "application/octet-stream" : attachment.DetectedMimeType;
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        httpContext.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        httpContext.Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
        httpContext.Response.ContentLength = attachment.FileSizeBytes;
        var disposition = new ContentDispositionHeaderValue(inline && CanPreview(attachment.DetectedMimeType) ? "inline" : "attachment")
        {
            FileNameStar = attachment.OriginalFileName
        };
        httpContext.Response.Headers.ContentDisposition = disposition.ToString();

        await handle.Stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    }

    private async Task<AttachmentFileHandle> OpenAsync(HttpContext context)
    {
        try { return await storageService.OpenReadAsync(server, attachment.StorageKey, context.RequestAborted); }
        catch (FileNotFoundException)
        {
            // A shared DB ticket authorizes the same file on the production API.
            var settings = context.RequestServices.GetService<IConfiguration>();
            var environment = context.RequestServices.GetService<IHostEnvironment>();
            if (server.StorageType is not ("LocalFileSystem" or "MountedFileSystem")
                || environment?.IsDevelopment() != true || context.Request.Path != "/api/public/attachments/content"
                || string.IsNullOrWhiteSpace(context.Request.Query["token"])
                || !Uri.TryCreate(settings?["AttachmentStorage:RemoteReadBaseUrl"], UriKind.Absolute, out var origin)
                || origin.Scheme != Uri.UriSchemeHttps || origin.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)) throw;
            var url = new Uri(origin, $"/api/public/attachments/content?token={Uri.EscapeDataString(context.Request.Query["token"].ToString())}");
            HttpResponseMessage response;
            try
            {
                response = await context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AttachmentContentResult))
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (HttpRequestException) { throw new FileNotFoundException("The shared attachment server is unavailable."); }
            if (!response.IsSuccessStatusCode) { response.Dispose(); throw; }
            return new AttachmentFileHandle(await response.Content.ReadAsStreamAsync(context.RequestAborted), response);
        }
    }
}
