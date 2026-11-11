using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentDocumentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        var routes = app.MapGroup("/api/recruitment/document-roster").WithTags("Recruitment document roster");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var user = currentUser(context.HttpContext);
            if (user.Id <= 0 || !user.IsActive || user.MustChangePassword) return Results.Unauthorized();
            if (!RecruitmentPermissions.Has(user, "recruitment.candidate.manage", "recruitment.offer.manage", "recruitment.offer.issue")) return Results.StatusCode(403);
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (Exception e) when (e is InvalidOperationException or JsonException or InvalidDataException or ArgumentException or System.Xml.XmlException)
            { return Results.BadRequest(new { error = e is JsonException ? "The import settings are invalid." : e.Message }); }
        });
        routes.MapGet("/templates", async (RecruitmentDocumentRepository repository, int clientId, HttpContext context) =>
            Results.Ok(await repository.TemplatesAsync(clientId, currentUser(context))));
        routes.MapGet("/rows", async (RecruitmentDocumentRepository repository, int clientId, HttpContext context) =>
            Results.Ok(await repository.RowsAsync(clientId, currentUser(context))));
        routes.MapPost("/mail-preview",async(RecruitmentDocumentRepository repository,int clientId,RecruitmentOfferMailRequest request,HttpContext context)=>
            Results.Ok(new {Items=await repository.PreviewOfferMailAsync(clientId,request,currentUser(context))}));
        routes.MapPost("/mail-jobs",async(RecruitmentDocumentRepository repository,int clientId,RecruitmentOfferMailRequest request,HttpContext context)=>
            Results.Ok(await repository.SubmitOfferMailAsync(clientId,request,currentUser(context))));
        routes.MapGet("/mail-jobs",async(RecruitmentDocumentRepository repository,int? clientId,HttpContext context)=>
            Results.Ok(new{Items=await repository.OwnOfferMailJobsAsync(currentUser(context),clientId)}));
        routes.MapPost("/mail-jobs/{id}/dismiss",async(RecruitmentDocumentRepository repository,int clientId,string id,HttpContext context)=>
        {await repository.DismissOfferMailJobAsync(clientId,id,currentUser(context));return Results.Ok(new{Ok=true});});
        routes.MapGet("/work-locations", async (RecruitmentDocumentRepository repository, int clientId, HttpContext context) =>
            Results.Ok(await repository.WorkLocationsAsync(clientId, currentUser(context))));
        routes.MapPost("/rows/work-location", async (RecruitmentDocumentRepository repository, int clientId, RecruitmentRosterWorkLocationSelection request, HttpContext context) =>
        { await repository.AssignWorkLocationAsync(clientId, request, currentUser(context)); return Results.NoContent(); });
        routes.MapGet("/signing-asset", async (RecruitmentDocumentRepository repository, int clientId, HttpContext context) =>
            Results.Ok(await repository.SigningAssetAsync(clientId, currentUser(context))));
        routes.MapPost("/signing-asset", async (RecruitmentDocumentRepository repository, int clientId, HttpContext context) =>
        {
            // Authorize before accepting or decoding any uploaded image bytes.
            var user = currentUser(context);
            if (!RecruitmentPermissions.Has(user, "recruitment.offer.manage")) return Results.StatusCode(403);
            if (!context.Request.HasFormContentType) throw new InvalidOperationException("Upload one PNG or JPEG signature and stamp image.");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.GetFile("file");
            if (form.Files.Count != 1 || file is null || file.Length is < 1 or > DocumentSigningImage.MaximumBytes) throw new InvalidOperationException("Upload one PNG or JPEG image of up to 2 MB.");
            using var stream = new MemoryStream(); await file.CopyToAsync(stream, context.RequestAborted);
            return Results.Ok(await repository.SaveSigningAssetAsync(clientId, stream.ToArray(), file.FileName, user));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(3 * 1024 * 1024));
        routes.MapDelete("/signing-asset", async (RecruitmentDocumentRepository repository, int clientId, HttpContext context) =>
        { await repository.DeleteSigningAssetAsync(clientId, currentUser(context)); return Results.NoContent(); });
        routes.MapPost("/rows/{id:long}/release", async (RecruitmentDocumentRepository repository, int clientId, long id, bool? applySignature, HttpContext context) =>
        {
            var result = await repository.ReleaseAsync(clientId, id, applySignature ?? true, currentUser(context));
            context.Response.Headers.Append("Access-Control-Expose-Headers", "Content-Disposition");
            return Results.File(result.Bytes, "application/pdf", result.FileName);
        });
        routes.MapPost("/releases", async (RecruitmentDocumentRepository repository, int clientId, RecruitmentRosterReleaseSelection request, HttpContext context) =>
        {
            var result = await repository.ReleaseZipAsync(clientId, request, currentUser(context));
            context.Response.Headers.Append("Access-Control-Expose-Headers", "Content-Disposition");
            return Results.File(result.Bytes, "application/zip", result.FileName);
        });
        routes.MapPost("/inspect", async (RecruitmentDocumentRepository repository, HttpContext context) =>
        {
            var (file, name, settings) = await ReadUpload(context);
            return Results.Ok(await repository.InspectAsync(file, name, settings.ClientId, settings.TemplateId, settings.HeaderRow, currentUser(context)));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        routes.MapPost("/preview", async (RecruitmentDocumentRepository repository, HttpContext context) =>
        {
            var (file, name, settings) = await ReadUpload(context);
            return Results.Ok(await repository.PreviewAsync(file, name, settings, currentUser(context)));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        routes.MapPost("/import", async (RecruitmentDocumentRepository repository, HttpContext context) =>
        {
            var (file, name, settings) = await ReadUpload(context);
            return Results.Ok(await repository.ImportAsync(file, name, settings, currentUser(context)));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        routes.MapPut("/rows/{id:long}", async (RecruitmentDocumentRepository repository, int clientId, long id, RecruitmentRosterEdit request, HttpContext context) =>
        { await repository.EditAsync(clientId, id, request, currentUser(context)); return Results.NoContent(); });
        routes.MapPost("/rows/{id:long}/draft", async (RecruitmentDocumentRepository repository, int clientId, long id, HttpContext context) =>
        {
            var result = await repository.GenerateAsync(clientId, id, currentUser(context));
            context.Response.Headers.Append("Access-Control-Expose-Headers", "Content-Disposition");
            return Results.File(result.Bytes, "application/pdf", result.FileName);
        });
        routes.MapPost("/drafts", async (RecruitmentDocumentRepository repository, int clientId, DocumentDraftSelection request, HttpContext context) =>
        {
            var result = await repository.GenerateZipAsync(clientId, request.Ids ?? [], currentUser(context));
            context.Response.Headers.Append("Access-Control-Expose-Headers", "Content-Disposition");
            return Results.File(result.Bytes, "application/zip", result.FileName);
        });
    }
    private static async Task<(byte[] Bytes, string FileName, RecruitmentRosterImport Settings)> ReadUpload(HttpContext context)
    {
        if (!context.Request.HasFormContentType) throw new InvalidOperationException("Upload the employee workbook with its reviewed import settings.");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var file = form.Files.GetFile("file") ?? throw new InvalidOperationException("The employee workbook is missing.");
        if (form.Files.Count != 1 || file.Length is < 1 or > 10 * 1024 * 1024 || form["settings"].ToString().Length > 64000)
            throw new InvalidOperationException("Upload one .xlsx workbook of up to 10 MB.");
        var settings = JsonSerializer.Deserialize<RecruitmentRosterImport>(form["settings"].ToString(), Json) ?? throw new InvalidOperationException("Import settings are missing.");
        using var output = new MemoryStream(); await file.CopyToAsync(output, context.RequestAborted);
        return (output.ToArray(), file.FileName, settings);
    }
    public sealed record DocumentDraftSelection(List<long>? Ids);
}
