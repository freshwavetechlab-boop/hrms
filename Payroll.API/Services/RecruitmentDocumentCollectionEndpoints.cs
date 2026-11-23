using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentDocumentCollectionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/public/recruitment/document-requests/{token}", async (string token, HttpContext context, RecruitmentDocumentCollectionRepository repo) =>
        {
            PrivateResponse(context);
            var response = await repo.GetPublicAsync(token);
            return response is null ? Results.NotFound(new { error = "This document link is unavailable. Please contact HR." }) : Results.Ok(response);
        }).AllowAnonymous();

        app.MapPost("/api/public/recruitment/document-requests/{token}", async (string token, HttpContext context, RecruitmentDocumentCollectionRepository repo) =>
        {
            PrivateResponse(context);
            try
            {
                // A consumed bearer only returns a minimal receipt, with no file parsing or HR details.
                var state = await repo.GetPublicAsync(token);
                if (state is null) return Results.NotFound(new { error = "This document link is unavailable. Please contact HR." });
                if (state.Status == "Completed") return Results.Ok(new RecruitmentDocumentCollectionResult("Completed", true, "Already submitted."));
                if (state.Status != "Open") return Results.BadRequest(new { error = state.Message });
                if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Submit the document upload form." });
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form["email"].Count != 1 || form["metadata"].Count > 1 || form.Keys.Any(k => k is not ("email" or "metadata")))
                    return Results.BadRequest(new { error = "The document upload form is invalid." });
                var metadataJson = form["metadata"].ToString();
                if (metadataJson.Length > 65536) return Results.BadRequest(new { error = "Document details are too large." });
                var metadata = string.IsNullOrWhiteSpace(metadataJson) ? new PublicRecruitmentDocumentMetadata()
                    : JsonSerializer.Deserialize<PublicRecruitmentDocumentMetadata>(metadataJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (metadata is null) return Results.BadRequest(new { error = "Document details are invalid." });
                var result = await repo.SubmitAsync(token, form["email"].ToString(), metadata, form.Files,
                    context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
                return result is null ? Results.NotFound(new { error = "This document link is unavailable. Please contact HR." }) : Results.Ok(result);
            }
            catch (RecruitmentDocumentCollectionValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (JsonException) { return Results.BadRequest(new { error = "Document details are invalid." }); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "The upload is invalid or exceeds the allowed limit." }); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            { return Results.Json(new { error = "The document upload is too large. Upload up to 100 MB in total, within each document's file size limit." }, statusCode: StatusCodes.Status413PayloadTooLarge); }
            catch (BadHttpRequestException) { return Results.BadRequest(new { error = "The upload form is invalid. Please check your files and try again." }); }
        }).DisableAntiforgery().AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(128L * 1024 * 1024),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 128L * 1024 * 1024, ValueLengthLimit = 65536, MultipartHeadersCountLimit = 16 });
    }

    private static void PrivateResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store, private";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}
