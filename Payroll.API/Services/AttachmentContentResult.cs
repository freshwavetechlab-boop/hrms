using System.Net.Http.Headers;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class AttachmentContentResult(
    AttachmentStorageService storageService,
    AttachmentStorageServer server,
    EntityAttachment attachment,
    bool inline) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await using var handle = await storageService.OpenReadAsync(server, attachment.StorageKey, httpContext.RequestAborted);
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = string.IsNullOrWhiteSpace(attachment.DetectedMimeType) ? "application/octet-stream" : attachment.DetectedMimeType;
        httpContext.Response.ContentLength = attachment.FileSizeBytes;
        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        httpContext.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        httpContext.Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
        var browserPreviewable = attachment.DetectedMimeType == "application/pdf" || attachment.DetectedMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var disposition = new ContentDispositionHeaderValue(inline && browserPreviewable ? "inline" : "attachment")
        {
            FileNameStar = attachment.OriginalFileName
        };
        httpContext.Response.Headers.ContentDisposition = disposition.ToString();

        await handle.Stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    }
}
