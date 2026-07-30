using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Dapper;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
var attachmentDataRoot = builder.Configuration["AttachmentStorage:DataRootPath"];
if (string.IsNullOrWhiteSpace(attachmentDataRoot))
    attachmentDataRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var attachmentKeyPath = builder.Configuration["AttachmentStorage:DataProtectionKeyPath"];
if (string.IsNullOrWhiteSpace(attachmentKeyPath))
    attachmentKeyPath = Path.Combine(attachmentDataRoot, "data-protection-keys");
Directory.CreateDirectory(attachmentKeyPath);
builder.Services.AddDataProtection()
    .SetApplicationName("Payroll.API.Attachments")
    .PersistKeysToFileSystem(new DirectoryInfo(attachmentKeyPath));
// builder.Services.AddCors(options =>
// {
//     options.AddDefaultPolicy(policy =>
//     {
//         policy.SetIsOriginAllowed(origin =>
//               {
//                   if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
//                   return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
//                          || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
//               })
//               .AllowCredentials()
//               .AllowAnyHeader()
//               .AllowAnyMethod();
//     });
// });
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSingleton<OrganizationRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<ClientBillingRepository>();
builder.Services.AddSingleton<EmployeeRepository>();
builder.Services.AddSingleton<EmployeeAttributeRepository>();
builder.Services.AddSingleton<PayRunRepository>();
builder.Services.AddSingleton<AuthRepository>();
builder.Services.AddSingleton<LeaveAttendanceRepository>();
builder.Services.AddSingleton<LeaveBalanceImportRepository>();
builder.Services.AddSingleton<ReportingRepository>();
builder.Services.AddSingleton<EssMssRepository>();
builder.Services.AddSingleton<WorkflowRepository>();
builder.Services.AddSingleton<TaxEngineRepository>();
builder.Services.AddSingleton<DashboardRepository>();
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<CommunicationRepository>();
builder.Services.AddSingleton<ScheduledJobRepository>();
builder.Services.AddSingleton<TravelExpenseRepository>();
builder.Services.AddSingleton<RecruitmentAdminRepository>();
builder.Services.AddSingleton<RecruitmentRepository>();
builder.Services.AddSingleton<ResumeParsingService>();
builder.Services.AddSingleton<TemplatePdfService>();
builder.Services.AddSingleton<RecruitmentTalentRepository>();
builder.Services.AddSingleton<RecruitmentFormRepository>();
builder.Services.AddSingleton<RecruitmentPipelineRepository>();
builder.Services.AddSingleton<RecruitmentCandidateActionRepository>();
builder.Services.AddSingleton<RecruitmentCaseRepository>();
builder.Services.AddSingleton<RecruitmentPipelineActionService>();
builder.Services.AddSingleton<GoogleDriveOAuthService>();
builder.Services.AddSingleton<AttachmentStorageService>();
builder.Services.AddSingleton<AttachmentRepository>();
builder.Services.AddSingleton<FrevoPilotChatStorageService>();
builder.Services.AddHostedService<PayrollRunWorker>();
builder.Services.AddHostedService<ScheduledJobWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<CommunicationWorker>();
builder.Services.AddHostedService<RecruitmentPipelineAutomationWorker>();
builder.Services.AddHostedService<AttendanceBatchJobWorker>();

var app = builder.Build();
const string AuthCookieName = "payroll_auth";

var migrateDatabaseOnly = args.Any(arg =>
    arg.Equals("--migrate", StringComparison.OrdinalIgnoreCase) ||
    arg.Equals("--migrate-database", StringComparison.OrdinalIgnoreCase));

if (migrateDatabaseOnly)
{
    await RunDatabaseSetupAsync(app.Services, app.Configuration);
    app.Logger.LogInformation("Database setup completed.");
    return;
}

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    await RunDatabaseSetupAsync(app.Services, app.Configuration);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment() || app.Configuration.GetValue<int?>("HttpsRedirection:HttpsPort").HasValue)
{
    app.UseHttpsRedirection();
}
app.UseCors();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method) || !context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/api/auth/login") || context.Request.Path.StartsWithSegments("/api/public"))
    {
        await next();
        return;
    }

    var authRepository = context.RequestServices.GetRequiredService<AuthRepository>();
    var token = ReadAuthToken(context, AuthCookieName);
    var user = string.IsNullOrWhiteSpace(token) ? null : await authRepository.GetUserByTokenAsync(token);
    if (user is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Authentication is required." });
        return;
    }

    if (!IsEssAllowedApi(context.Request.Path) && !AuthRepository.HasBackofficeAccess(user))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Admin portal access is not enabled for this user." });
        return;
    }

    context.Items["User"] = user;
    await next();

    if (context.Request.Method != HttpMethods.Get)
    {
        await authRepository.WriteAuditAsync(
            user,
            $"{context.Request.Method.ToLowerInvariant()}.{context.Request.Path.Value?.Trim('/').Replace('/', '.')}",
            context.GetEndpoint()?.DisplayName ?? "api",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            context.Connection.RemoteIpAddress?.ToString() ?? "",
            context.Request.Headers.UserAgent.ToString());
    }
});

app.UseMiddleware<WorkflowActionMiddleware>();

app.MapPost("/api/auth/login", async (AuthRepository repository, LoginRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Email and password are required." });
    var result = await repository.LoginAsync(request, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    if (result is not null)
        WriteAuthCookie(context, AuthCookieName, result.Token, result.ExpiresAt);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
})
.WithName("Login")
.WithOpenApi();

app.MapGet("/api/auth/me", (HttpContext context) =>
    Results.Ok(CurrentUser(context)))
.WithName("GetCurrentUser")
.WithOpenApi();

app.MapPost("/api/auth/change-password", async (AuthRepository repository, ChangePasswordRequest request, HttpContext context) =>
{
    var user = CurrentUser(context);
    var (updated, error) = await repository.ChangePasswordAsync(user.Id, request.CurrentPassword, request.NewPassword);
    return updated is null ? Results.BadRequest(new { error }) : Results.Ok(updated);
})
.WithName("ChangePassword")
.WithOpenApi();

app.MapGet("/api/attachment-targets", (HttpContext context) =>
    HasPermission(context, "settings.manage") || HasPermission(context, "attachment.config.manage")
        ? Results.Ok(AttachmentRepository.Targets)
        : Results.StatusCode(403))
.WithName("GetAttachmentTargets")
.WithOpenApi();

app.MapGet("/api/attachment-attributes", async (AttachmentRepository repository, int? clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage")) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var effectiveClientId = user.ClientId ?? clientId;
    return Results.Ok(await repository.GetAttributesAsync(effectiveClientId));
})
.WithName("GetAttachmentAttributes")
.WithOpenApi();

app.MapPost("/api/attachment-attributes", async (AttachmentRepository repository, AttachmentAttribute request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage")) return Results.StatusCode(403);
    var (item, error) = await repository.SaveAttributeAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveAttachmentAttribute")
.WithOpenApi();

app.MapGet("/api/attachment-configurations", async (AttachmentRepository repository, int? clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage")) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var effectiveClientId = user.ClientId ?? clientId;
    return Results.Ok(await repository.GetConfigurationsAsync(effectiveClientId));
})
.WithName("GetAttachmentConfigurations")
.WithOpenApi();

app.MapGet("/api/attachment-configurations/effective", async (AttachmentRepository repository, int clientId, string moduleCode, string formCode, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user.ClientId is not null && user.ClientId != clientId) return Results.StatusCode(403);
    return Results.Ok(await repository.GetEffectiveConfigurationsAsync(clientId, moduleCode, formCode));
})
.WithName("GetEffectiveAttachmentConfigurations")
.WithOpenApi();

app.MapPost("/api/attachment-configurations", async (AttachmentRepository repository, AttachmentFieldConfiguration request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage")) return Results.StatusCode(403);
    var (item, error) = await repository.SaveConfigurationAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveAttachmentConfiguration")
.WithOpenApi();

app.MapGet("/api/attachment-storage-servers", async (AttachmentRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") || HasPermission(context, "attachment.config.manage")
        ? Results.Ok(await repository.GetStorageServersAsync())
        : Results.StatusCode(403))
.WithName("GetAttachmentStorageServers")
.WithOpenApi();

app.MapPost("/api/attachment-storage-servers", async (AttachmentRepository repository, AttachmentStorageServer request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage")) return Results.StatusCode(403);
    var (item, error) = await repository.SaveStorageServerAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveAttachmentStorageServer")
.WithOpenApi();

app.MapPost("/api/attachment-storage-servers/{id:long}/test", async (AttachmentRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage")) return Results.StatusCode(403);
    var result = await repository.TestStorageServerAsync(id);
    return result.Healthy ? Results.Ok(result) : Results.BadRequest(result);
})
.WithName("TestAttachmentStorageServer")
.WithOpenApi();

app.MapGet("/api/frevopilot/chat-threads/status", async (FrevoPilotChatStorageService chats, HttpContext context) =>
{
    if (!CanManageFrevoPilotChats(context)) return Results.StatusCode(403);
    context.Response.Headers.CacheControl = "private, no-store";
    return Results.Ok(await chats.GetStatusAsync(context.RequestAborted));
})
.WithName("GetFrevoPilotChatStorageStatus")
.WithOpenApi();

app.MapGet("/api/frevopilot/chat-threads", async (FrevoPilotChatStorageService chats, HttpContext context) =>
{
    if (!CanManageFrevoPilotChats(context)) return Results.StatusCode(403);
    context.Response.Headers.CacheControl = "private, no-store";
    return Results.Ok(await chats.ListAsync(CurrentUser(context).Id, context.RequestAborted));
})
.WithName("ListFrevoPilotChatThreads")
.WithOpenApi();

app.MapGet("/api/frevopilot/chat-threads/{threadId:guid}", async (FrevoPilotChatStorageService chats, Guid threadId, HttpContext context) =>
{
    if (!CanManageFrevoPilotChats(context)) return Results.StatusCode(403);
    context.Response.Headers.CacheControl = "private, no-store";
    var thread = await chats.GetAsync(threadId, CurrentUser(context).Id, context.RequestAborted);
    return thread is null ? Results.NotFound(new { error = "FrevoPilot chat was not found." }) : Results.Ok(thread);
})
.WithName("GetFrevoPilotChatThread")
.WithOpenApi();

app.MapPost("/api/frevopilot/chat-threads", async (FrevoPilotChatStorageService chats, SaveFrevoPilotChatThreadRequest request, HttpContext context) =>
{
    if (!CanManageFrevoPilotChats(context)) return Results.StatusCode(403);
    try
    {
        return Results.Ok(await chats.SaveAsync(null, request, CurrentUser(context).Id, context.RequestAborted));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.WithName("CreateFrevoPilotChatThread")
.WithOpenApi();

app.MapPut("/api/frevopilot/chat-threads/{threadId:guid}", async (FrevoPilotChatStorageService chats, Guid threadId, SaveFrevoPilotChatThreadRequest request, HttpContext context) =>
{
    if (!CanManageFrevoPilotChats(context)) return Results.StatusCode(403);
    try
    {
        return Results.Ok(await chats.SaveAsync(threadId, request, CurrentUser(context).Id, context.RequestAborted));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.WithName("UpdateFrevoPilotChatThread")
.WithOpenApi();

app.MapDelete("/api/frevopilot/chat-threads/{threadId:guid}", async (FrevoPilotChatStorageService chats, Guid threadId, HttpContext context) =>
{
    if (!CanManageFrevoPilotChats(context)) return Results.StatusCode(403);
    try
    {
        return await chats.DeleteAsync(threadId, CurrentUser(context).Id, context.RequestAborted)
            ? Results.NoContent()
            : Results.NotFound(new { error = "FrevoPilot chat was not found." });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.WithName("DeleteFrevoPilotChatThread")
.WithOpenApi();

app.MapGet("/api/attachment-storage-servers/google/setup", async (
    AttachmentRepository repository,
    GoogleDriveOAuthService googleDrive,
    HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var server = (await repository.GetStorageServersAsync())
        .Where(item => item.StorageType.Equals(GoogleDriveOAuthService.StorageType, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => item.IsDefaultWriteServer)
        .ThenBy(item => item.Id)
        .FirstOrDefault();
    var apiBaseUri = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
    try
    {
        return Results.Ok(BuildGoogleDriveOAuthSetup(googleDrive, server, apiBaseUri));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.WithName("GetGoogleDriveOAuthSetup")
.WithOpenApi();

app.MapPost("/api/attachment-storage-servers/google/configure", async (
    AttachmentRepository repository,
    GoogleDriveOAuthService googleDrive,
    [FromForm] IFormFile? credentialFile,
    long? storageServerId,
    HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (credentialFile is null)
        return Results.BadRequest(new { error = "Select the downloaded Google Web OAuth client JSON file." });
    try
    {
        var apiBaseUri = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
        var callbackUrl = googleDrive.ResolveRedirectUri(apiBaseUri);
        var oauthClient = await googleDrive.ParseOAuthClientConfigurationAsync(
            credentialFile,
            callbackUrl,
            context.RequestAborted);
        var (server, ensureError) = await repository.EnsureGoogleDriveStorageServerAsync(storageServerId, CurrentUser(context));
        if (server is null) return Results.BadRequest(new { error = ensureError });
        var (configured, configureError) = await repository.ConfigureGoogleDriveOAuthAsync(server.Id, oauthClient, CurrentUser(context));
        return configured is null
            ? Results.BadRequest(new { error = configureError })
            : Results.Ok(BuildGoogleDriveOAuthSetup(googleDrive, configured, apiBaseUri));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.DisableAntiforgery()
.WithMetadata(new RequestSizeLimitAttribute(128 * 1024))
.WithName("ConfigureGoogleDriveOAuth")
.WithOpenApi();

app.MapPost("/api/attachment-storage-servers/google/connect", async (
    AttachmentRepository repository,
    GoogleDriveOAuthService googleDrive,
    long? storageServerId,
    HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (server, error) = await repository.EnsureGoogleDriveStorageServerAsync(storageServerId, CurrentUser(context));
    if (server is null) return Results.BadRequest(new { error });
    try
    {
        var (credential, credentialError) = await repository.GetGoogleDriveCredentialAsync(server.Id);
        if (!string.IsNullOrWhiteSpace(credentialError)) return Results.BadRequest(new { error = credentialError });
        var portalOrigin = googleDrive.ResolvePortalOrigin(
            context.Request.Headers.Origin.ToString(),
            context.Request.Headers.Referer.ToString());
        var apiBaseUri = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
        var authorization = googleDrive.CreateAuthorizationRequest(
            server.Id,
            CurrentUser(context).Id,
            portalOrigin,
            apiBaseUri,
            server.BasePath,
            credential);
        return Results.Ok(new GoogleDriveConnectResponse
        {
            StorageServerId = authorization.StorageServerId,
            AuthorizationUrl = authorization.AuthorizationUrl
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
})
.WithName("ConnectGoogleDriveStorage")
.WithOpenApi();

app.MapGet("/api/attachment-storage-servers/{id:long}/google/status", async (
    AttachmentRepository repository,
    long id,
    HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (status, error) = await repository.GetGoogleDriveConnectionStatusAsync(id);
    return status is null ? Results.BadRequest(new { error }) : Results.Ok(status);
})
.WithName("GetGoogleDriveStorageStatus")
.WithOpenApi();

app.MapPost("/api/attachment-storage-servers/{id:long}/google/disconnect", async (
    AttachmentRepository repository,
    long id,
    HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attachment.config.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (ok, error) = await repository.DisconnectGoogleDriveAsync(id, CurrentUser(context));
    return ok ? Results.Ok(new { disconnected = true }) : Results.BadRequest(new { error });
})
.WithName("DisconnectGoogleDriveStorage")
.WithOpenApi();

app.MapGet(GoogleDriveOAuthService.CallbackPath, async (
    AttachmentRepository repository,
    GoogleDriveOAuthService googleDrive,
    string? code,
    string? state,
    string? error,
    [FromQuery(Name = "error_description")] string? errorDescription,
    HttpContext context) =>
{
    GoogleDriveOAuthState oauthState;
    try
    {
        oauthState = googleDrive.ReadAndValidateState(state ?? "");
    }
    catch (InvalidOperationException exception)
    {
        return GoogleDrivePopupResult(context, "", false, exception.Message, null);
    }

    if (!string.IsNullOrWhiteSpace(error))
    {
        var message = string.IsNullOrWhiteSpace(errorDescription)
            ? $"Google authorization was cancelled ({error})."
            : errorDescription;
        return GoogleDrivePopupResult(context, oauthState.PortalOrigin, false, message, oauthState.StorageServerId);
    }

    try
    {
        var (credential, credentialError) = await repository.GetGoogleDriveCredentialAsync(oauthState.StorageServerId);
        if (!string.IsNullOrWhiteSpace(credentialError))
            return GoogleDrivePopupResult(context, oauthState.PortalOrigin, false, credentialError, oauthState.StorageServerId);
        var oauthClient = googleDrive.RequireOAuthClient(credential);
        var authorization = await googleDrive.CompleteAuthorizationAsync(
            code ?? "",
            oauthState,
            oauthClient,
            context.RequestAborted);
        var (server, connectionError) = await repository.CompleteGoogleDriveConnectionAsync(authorization);
        return server is null
            ? GoogleDrivePopupResult(context, oauthState.PortalOrigin, false, connectionError ?? "Google Drive could not be connected.", oauthState.StorageServerId)
            : GoogleDrivePopupResult(context, oauthState.PortalOrigin, true, "Google Drive connected and selected as the active attachment storage.", server.Id);
    }
    catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return GoogleDrivePopupResult(context, oauthState.PortalOrigin, false, exception.Message, oauthState.StorageServerId);
    }
})
.WithName("CompleteGoogleDriveStorageConnection");

app.MapGet("/api/attachments", async (AttachmentRepository repository, string entityType, long entityId, HttpContext context) =>
    Results.Ok(await repository.GetAttachmentsAsync(entityType, entityId, CurrentUser(context))))
.WithName("GetEntityAttachments")
.WithOpenApi();

app.MapPost("/api/attachments", async (
    AttachmentRepository repository,
    [FromForm] AttachmentUploadRequest request,
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (request.File is null)
        return Results.BadRequest(new { error = "Select a file." });
    var metadata = new AttachmentUploadMetadata
    {
        FieldConfigurationId = request.FieldConfigurationId,
        EntityType = request.EntityType ?? "",
        EntityId = request.EntityId,
        DocumentNumber = request.DocumentNumber ?? "",
        IssueDate = request.IssueDate,
        ExpiryDate = request.ExpiryDate
    };
    try
    {
        var (attachment, error) = await repository.UploadAsync(metadata, request.File, CurrentUser(context),
            context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
        return attachment is null ? Results.BadRequest(new { error }) : Results.Created($"/api/attachments/{attachment.PublicId}", attachment);
    }
    catch (Exception exception)
    {
        loggerFactory.CreateLogger("AttachmentUpload").LogError(exception,
            "Attachment upload failed for entity {EntityType}/{EntityId} and field {FieldConfigurationId}.",
            metadata.EntityType, metadata.EntityId, metadata.FieldConfigurationId);
        return Results.Problem(
            title: "Attachment upload failed",
            detail: "The document could not be uploaded. Please retry or contact support.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
})
.DisableAntiforgery()
.WithMetadata(new RequestSizeLimitAttribute(30L * 1024 * 1024))
.WithName("UploadAttachment")
.WithOpenApi();

app.MapGet("/api/attachments/{publicId:guid}/content", async (
    AttachmentRepository repository,
    AttachmentStorageService storage,
    Guid publicId,
    bool? download,
    HttpContext context) =>
{
    var action = download == true ? "DOWNLOAD" : "PREVIEW";
    var (attachment, server, error) = await repository.GetForContentAsync(publicId, CurrentUser(context), action,
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return attachment is null || server is null
        ? Results.NotFound(new { error })
        : new AttachmentContentResult(storage, server, attachment, download != true);
})
.WithName("ReadAttachmentContent")
.WithOpenApi();

app.MapPost("/api/attachments/{publicId:guid}/access-ticket", async (
    AttachmentRepository repository,
    Guid publicId,
    JsonElement body,
    HttpContext context) =>
{
    var purpose = body.TryGetProperty("purpose", out var value) ? value.GetString() ?? "Preview" : "Preview";
    var (ticket, error) = await repository.IssueAccessTicketAsync(publicId, CurrentUser(context), purpose,
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return ticket is null ? Results.NotFound(new { error }) : Results.Ok(ticket);
})
.WithName("IssueAttachmentAccessTicket")
.WithOpenApi();

app.MapDelete("/api/attachments/{publicId:guid}", async (AttachmentRepository repository, Guid publicId, HttpContext context) =>
{
    var (ok, error) = await repository.DeleteAsync(publicId, CurrentUser(context),
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return ok ? Results.NoContent() : Results.BadRequest(new { error });
})
.WithName("DeleteAttachment")
.WithOpenApi();

app.MapPost("/api/attachments/{publicId:guid}/verify", async (AttachmentRepository repository, Guid publicId, HttpContext context) =>
{
    var (item, error) = await repository.ReviewAsync(publicId, true, "", CurrentUser(context),
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("VerifyAttachment")
.WithOpenApi();

app.MapPost("/api/attachments/{publicId:guid}/reject", async (AttachmentRepository repository, Guid publicId, AttachmentReviewRequest request, HttpContext context) =>
{
    var (item, error) = await repository.ReviewAsync(publicId, false, request.Reason, CurrentUser(context),
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("RejectAttachment")
.WithOpenApi();

app.MapGet("/api/public/attachments/content", async (
    AttachmentRepository repository,
    AttachmentStorageService storage,
    string token,
    HttpContext context) =>
{
    var (attachment, server, purpose) = await repository.ConsumeAccessTicketAsync(token,
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return attachment is null || server is null
        ? Results.NotFound()
        : new AttachmentContentResult(storage, server, attachment, !purpose!.Equals("Download", StringComparison.OrdinalIgnoreCase));
})
.WithName("ReadAttachmentByTicket")
.WithOpenApi();

app.MapGet("/api/dashboard", async (DashboardRepository repository, int? clientId, HttpContext context) =>
{
    var user = CurrentUser(context);
    var effectiveClientId = user.ClientId ?? Math.Max(clientId.GetValueOrDefault(), 0);
    return Results.Ok(await repository.GetAsync(effectiveClientId, user));
})
.WithName("GetDashboard")
.WithOpenApi();

app.MapGet("/api/workflows", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/activities", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetActivitiesAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/activities/catalog", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetActivitiesForSetupAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/action-rules", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetActionRulesForSetupAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/approvers", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetApproversAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/approver-preview", async (WorkflowRepository repository, string? approverType, int? clientId, int? approverUserId, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetApproverPreviewAsync(approverType,clientId,approverUserId)) : Results.StatusCode(403));
app.MapGet("/api/workflows/departments", async (WorkflowRepository repository, int clientId, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetDepartmentsAsync(clientId)) : Results.StatusCode(403));
app.MapGet("/api/workflows/department-heads", async (WorkflowRepository repository, int clientId, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetDepartmentHeadsAsync(clientId)) : Results.StatusCode(403));
app.MapPost("/api/workflows/department-heads", async (WorkflowRepository repository, SaveDepartmentHeadAssignmentRequest request, HttpContext context) => { if(!HasPermission(context,"workflow.manage")) return Results.StatusCode(403); if(request.ClientId<=0||string.IsNullOrWhiteSpace(request.Department)||request.UserId<=0)return Results.BadRequest(new{error="Client, department, and assigned user are required."}); return Results.Ok(await repository.SaveDepartmentHeadAsync(request)); });
app.MapPost("/api/workflows", async (WorkflowRepository repository, SaveWorkflowRequest request, HttpContext context) => { if(!HasPermission(context,"workflow.manage")) return Results.StatusCode(403); return Results.Ok(await repository.SaveAsync(request)); });
app.MapPost("/api/workflows/activities", async (WorkflowRepository repository, SaveWorkflowActivityRequest request, HttpContext context) => { if(!HasPermission(context,"workflow.manage")) return Results.StatusCode(403); if(string.IsNullOrWhiteSpace(request.ActivityCode)||string.IsNullOrWhiteSpace(request.DisplayName)||string.IsNullOrWhiteSpace(request.ModuleCode)||string.IsNullOrWhiteSpace(request.ResourceType)) return Results.BadRequest(new{error="Activity code, activity name, module, and record type are required."}); return Results.Ok(await repository.SaveActivityAsync(request)); });
app.MapPost("/api/workflows/action-rules", async (WorkflowRepository repository, SaveWorkflowActionRuleRequest request, HttpContext context) => { if(!HasPermission(context,"workflow.manage")) return Results.StatusCode(403); if(string.IsNullOrWhiteSpace(request.ActivityCode)||string.IsNullOrWhiteSpace(request.HttpMethod)||string.IsNullOrWhiteSpace(request.PathPattern)||string.IsNullOrWhiteSpace(request.ResourceType)||string.IsNullOrWhiteSpace(request.ResourceIdSource)) return Results.BadRequest(new{error="Activity, method, path, resource type, and resource id source are required."}); if(!request.ResourceIdSource.Contains('.')) return Results.BadRequest(new{error="Resource id source must use scope.field format, for example route.id or body.employeeId."}); return Results.Ok(await repository.SaveActionRuleAsync(request)); });
app.MapPost("/api/workflows/start", async (WorkflowRepository repository, StartWorkflowRequest request, HttpContext context) => { var item=await repository.StartAsync(request,CurrentUser(context).Id); return item is null ? Results.BadRequest(new {error="Workflow cannot start. Check stages and approver setup."}) : Results.Ok(item); });
app.MapGet("/api/workflows/tasks/pending", async (WorkflowRepository repository,HttpContext context) => Results.Ok(await repository.PendingAsync(CurrentUser(context).Id)));
app.MapGet("/api/workflows/tasks/actioned", async (WorkflowRepository repository,string? scope,HttpContext context) =>
{
    var all = scope?.Equals("all", StringComparison.OrdinalIgnoreCase) == true && HasPermission(context, "workflow.manage");
    return Results.Ok(await repository.ActionedAsync(CurrentUser(context).Id, all));
});
app.MapGet("/api/workflows/history", async (WorkflowRepository repository,HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetInstancesAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/{instanceId:long}/history", async (WorkflowRepository repository,long instanceId,HttpContext context) => Results.Ok(await repository.HistoryAsync(instanceId)));
app.MapPost("/api/workflows/tasks/{taskId:long}/{action}", async (WorkflowRepository repository, EssMssRepository essRepository, PayRunRepository payRuns, RecruitmentRepository recruitment, RecruitmentTalentRepository recruitmentTalent, RecruitmentPipelineRepository recruitmentPipeline, RecruitmentCaseRepository recruitmentCases, RecruitmentCandidateActionRepository candidateActions, RecruitmentPipelineActionService pipelineActions, NotificationRepository notifications,long taskId,string action,WorkflowActionRequest request,HttpContext context) =>
{
    if(action is not ("Approved" or "Rejected" or "Sent Back")) return Results.BadRequest();
    var user=CurrentUser(context);
    var task=await repository.ActionAsync(taskId,user.Id,action,request.Comment);
    if(!task)return Results.NotFound();
    var instance=await repository.GetInstanceForTaskAsync(taskId);
    if(instance?.ResourceType=="LeaveRequest")await essRepository.SyncLeaveWorkflowStatusAsync(instance.ResourceId,instance.Status);
    if(instance?.ResourceType=="TravelRequest")await essRepository.SyncTravelWorkflowStatusAsync(instance.ResourceId,instance.Status);
    if(instance?.ResourceType=="ExpenseClaim")await essRepository.SyncExpenseWorkflowStatusAsync(instance.ResourceId,instance.Status);
    if(instance?.ResourceType=="RecruitmentRequisition")await recruitment.SyncWorkflowStatusAsync(instance.ResourceId,instance.Status,user.Id);
    if(instance?.ResourceType=="RecruitmentOffer")await recruitmentTalent.SyncOfferWorkflowStatusAsync(instance.ResourceId,instance.Status,user,instance.Id);
    if(instance?.ResourceType=="RecruitmentJobDescription" && long.TryParse(instance.ResourceId,out var jobDescriptionId))await recruitmentPipeline.SyncJobDescriptionWorkflowStatusAsync(jobDescriptionId,instance.Status,user);
    if(instance?.ResourceType=="RecruitmentPipelineTransition" && instance.ResourceId.StartsWith("HIRING_CASE:",StringComparison.OrdinalIgnoreCase) && long.TryParse(instance.ResourceId[12..],out var hiringCaseAdvanceRequestId))
    {
        var (_, hiringCaseError) = await recruitmentCases.SyncHiringCaseAdvanceWorkflowStatusAsync(hiringCaseAdvanceRequestId,instance.Status,user);
        if(hiringCaseError.Length>0)return Results.Conflict(new{error=hiringCaseError});
    }
    else if(instance?.ResourceType=="RecruitmentPipelineTransition" && long.TryParse(instance.ResourceId,out var transitionRequestId))
    {
        var transition = await recruitmentPipeline.SyncTransitionWorkflowStatusAsync(transitionRequestId,instance.Status,user);
        if(transition.Result?.Status=="Applied")
        {
            await pipelineActions.ExecuteAsync(transition.Result.ApplicationId,"OnApproval",user);
            await pipelineActions.ExecuteAsync(transition.Result.ApplicationId,"OnExit",user);
            var entry = await pipelineActions.ExecuteAsync(transition.Result.ApplicationId,"OnEntry",user);
            if (!entry.Executions.Any(item => item.ActionCode=="GENERATE_ACTION_LINK"))
                await candidateActions.EnsureForCurrentStageAsync(transition.Result.ApplicationId,user);
        }
    }
    if(instance?.ResourceType=="RecruitmentPipelineStageAction")
    {
        var completion = await pipelineActions.CompleteWorkflowAsync(instance.Id,instance.Status);
        if(completion.Approved && completion.ApplicationId>0)
            await pipelineActions.ExecuteAsync(completion.ApplicationId,"OnApproval",user,completion.StageInstanceId);
    }
    if(instance?.ResourceType=="PayRun" && int.TryParse(instance.ResourceId,out var payRunId))
    {
        if(instance.Status=="Approved") await payRuns.ApproveAsync(payRunId);
        if(instance.Status is "Rejected" or "Sent Back") await payRuns.RecallAsync(payRunId);
    }
    if(instance?.ResourceType=="ExpenseClaim")
    {
        await notifications.PublishEventAsync(new NotificationEvent{EventCode=$"EXPENSE_CLAIM.{action.ToUpperInvariant().Replace(" ","_")}",ResourceType="ExpenseClaim",ResourceId=instance.ResourceId,ClientId=user.ClientId,ActorUserId=user.Id,ActorName=user.DisplayName,ActorEmail=user.Email,PayloadJson=System.Text.Json.JsonSerializer.Serialize(new{Action=action,Status=instance.Status,Comment=request.Comment,TaskId=taskId})});
    }
    return Results.NoContent();
});

app.MapGet("/api/ess/leave/balances", async (EssMssRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!user.Permissions.Contains("ess.self", StringComparer.OrdinalIgnoreCase) || user.EmployeeId is null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Results.Ok(await repository.GetLeaveBalancesAsync(user.EmployeeId.Value, user.ClientId));
    }
    catch (Exception exception)
    {
        return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("GetEssLeaveBalances")
.WithOpenApi();

app.MapGet("/api/ess/profile", async (EssMssRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!user.Permissions.Contains("ess.self", StringComparer.OrdinalIgnoreCase) || user.EmployeeId is null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var profile = await repository.GetProfileAsync(user.EmployeeId.Value, user.ClientId);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
})
.WithName("GetEssProfile")
.WithOpenApi();

app.MapPost("/api/ess/profile", async (EssMssRepository repository, SaveEssProfileRequest request, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!user.Permissions.Contains("ess.self", StringComparer.OrdinalIgnoreCase) || user.EmployeeId is null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (profile, error) = await repository.SaveProfileAsync(user.EmployeeId.Value, user.ClientId, request, user.Email);
    return profile is null ? Results.BadRequest(new { error }) : Results.Ok(profile);
})
.WithName("SaveEssProfile")
.WithOpenApi();

app.MapGet("/api/ess/features", async (EssMssRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!user.Permissions.Contains("ess.self", StringComparer.OrdinalIgnoreCase) || user.EmployeeId is null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.GetFeatureAccessAsync(user.EmployeeId.Value, user.ClientId));
})
.WithName("GetEssFeatures")
.WithOpenApi();

app.MapGet("/api/ess-admin/settings", async (EssMssRepository repository, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "employees.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.GetEssClientSettingsAsync());
})
.WithName("GetEssClientSettings")
.WithOpenApi();

app.MapPost("/api/ess-admin/settings", async (EssMssRepository repository, EssClientSetting setting, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "employees.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (setting.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    return Results.Ok(await repository.SaveEssClientSettingAsync(setting));
})
.WithName("SaveEssClientSetting")
.WithOpenApi();

app.MapGet("/api/ess/leave/requests", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetLeaveRequestsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/leave/requests/{id:long}/trail", async (EssMssRepository repository, long id, HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var trail=await repository.GetLeaveRequestTrailAsync(id,user.EmployeeId.Value,user.ClientId); return trail is null ? Results.NotFound() : Results.Ok(trail); });
app.MapGet("/api/ess/pay/payslips", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetPayslipsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/pay/payslips/{payRunId:int}", async (EssMssRepository repository, int payRunId, HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var document=await repository.GetPayslipDocumentAsync(user.EmployeeId.Value,user.ClientId,payRunId); return document is null ? Results.NotFound() : Results.Ok(document); });
app.MapGet("/api/ess/tax", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetTaxPortalAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapPost("/api/ess/tax/regime", async (EssMssRepository repository, SaveEssTaxRegimeRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(ok,error)=await repository.SaveTaxRegimeAsync(user.EmployeeId.Value,user.ClientId,request); return ok ? Results.NoContent() : Results.BadRequest(new{error}); });
app.MapPost("/api/ess/tax/declarations", async (EssMssRepository repository, SaveEssTaxDeclarationsRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(ok,error)=await repository.SaveTaxDeclarationsAsync(user.EmployeeId.Value,user.ClientId,request); return ok ? Results.NoContent() : Results.BadRequest(new{error}); });
app.MapGet("/api/ess/dashboard/attendance", async (EssMssRepository repository, string month, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetAttendanceSummaryAsync(user.EmployeeId.Value,user.ClientId,month)); });
app.MapGet("/api/ess/dashboard/attendance/daily", async (EssMssRepository repository, string month, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetDailyAttendanceAsync(user.EmployeeId.Value,user.ClientId,month)); });
app.MapGet("/api/ess/attendance/history", async (EssMssRepository repository, string month, string? scope, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var result=await repository.GetAttendanceHistoryAsync(user.EmployeeId.Value,user.ClientId,month,scope??"calendar-month"); return result is null ? Results.BadRequest(new{error=new{code="ATTENDANCE_POLICY_INVALID",message="Attendance month or employee mapping is invalid."}}) : Results.Ok(result); });
app.MapGet("/api/ess/attendance/today", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var state=await repository.GetAttendanceTodayAsync(user.EmployeeId.Value,user.ClientId); return state is null ? Results.NotFound(new{error="Active employee attendance profile was not found."}) : Results.Ok(state); });
app.MapPost("/api/ess/attendance/punch/validate", async (EssMssRepository repository, ValidateAttendancePunchRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||!user.Permissions.Contains("ess.attendance.mark",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); return Results.Ok(await repository.ValidateAttendancePunchAsync(user.EmployeeId.Value,user.ClientId,request)); });
app.MapPost("/api/ess/attendance/punch", async (EssMssRepository repository, ValidateAttendancePunchRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||!user.Permissions.Contains("ess.attendance.mark",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var result=await repository.RecordAttendancePunchAsync(user.EmployeeId.Value,user.ClientId,request); return result.PunchRecorded ? Results.Created($"/api/ess/attendance/punch/{result.PunchId}",result) : Results.BadRequest(result); });
app.MapGet("/api/ess/mss/attendance/groups", async (LeaveAttendanceRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return !HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetAttendanceGroupsAsync(user.ClientId.Value, user.Id));
});
app.MapGet("/api/ess/mss/attendance/monthly", async (LeaveAttendanceRepository repository, string month, int? workLocationId, HttpContext context) =>
{
    var user = CurrentUser(context);
    return !HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetMonthlyAttendanceAsync(user.ClientId.Value, month, workLocationId, user.Id));
});
app.MapGet("/api/ess/mss/attendance/daily-grid", async (LeaveAttendanceRepository repository, string month, int? workLocationId, HttpContext context) =>
{
    var user = CurrentUser(context);
    return !HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetDailyAttendanceMonthAsync(user.ClientId.Value, month, workLocationId, user.Id));
});
app.MapGet("/api/ess/mss/attendance/context", async (LeaveAttendanceRepository repository, string month, int? workLocationId, HttpContext context) =>
{
    var user = CurrentUser(context);
    return !HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetAttendanceReviewContextAsync(user.ClientId.Value, month, workLocationId, user.Id));
});
app.MapGet("/api/ess/mss/attendance/leave-types", async (LeaveAttendanceRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return !HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetLeaveTypesAsync(user.ClientId.Value));
});
app.MapGet("/api/ess/mss/attendance/dropdowns", async (OrganizationRepository repository, HttpContext context) =>
    !HasPermission(context, "mss.attendance.manage")
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetDropdownMastersAsync()));
app.MapPost("/api/ess/mss/attendance/daily/batch-jobs", async (LeaveAttendanceRepository repository, SaveDailyAttendanceBatchRequest request, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue || request.ClientId != user.ClientId.Value)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var requestedEmployeeIds = request.Rows.Select(row => row.EmployeeId)
        .Concat(request.RollupEmployeeIds ?? [])
        .Distinct()
        .ToArray();
    if (!await repository.AreActiveDirectReportsAsync(user.ClientId.Value, user.Id, requestedEmployeeIds))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (job, error) = await repository.StartDailyAttendanceBatchJobAsync(request, user.Email, user.Id);
    if (job is null && string.Equals(error, LeaveAttendanceRepository.ManagedAttendanceScopeError, StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return job is null ? Results.BadRequest(new { error }) : Results.Accepted($"/api/ess/mss/attendance/daily/batch-jobs/{job.JobId}", job);
});
app.MapGet("/api/ess/mss/attendance/daily/batch-jobs/{jobId:guid}", async (LeaveAttendanceRepository repository, Guid jobId, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!HasPermission(context, "mss.attendance.manage") || !user.ClientId.HasValue)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var job = await repository.GetDailyAttendanceBatchJobAsync(jobId);
    return job is null ? Results.NotFound(new { error = "Attendance batch job not found." }) : job.ClientId != user.ClientId.Value ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(job);
});
app.MapGet("/api/ess/dashboard/holidays", async (EssMssRepository repository, string month, HttpContext context) => Results.Ok(await repository.GetHolidaysAsync(CurrentUser(context).ClientId,month)));
app.MapGet("/api/ess/dashboard/birthdays", async (EssMssRepository repository, HttpContext context) => Results.Ok(await repository.GetTodaysBirthdaysAsync(CurrentUser(context).ClientId)));
app.MapPost("/api/ess/leave/requests", async (EssMssRepository repository, WorkflowRepository workflows, CreateEssLeaveRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(result,error)=await repository.CreateLeaveRequestAsync(user.EmployeeId.Value,user.ClientId,request); if(result is null)return Results.BadRequest(new{error}); var workflowId=await workflows.GetDefaultIdAsync("LeaveRequest",user.ClientId); if(workflowId is not null) await workflows.StartAsync(new StartWorkflowRequest{WorkflowId=workflowId.Value,ResourceType="LeaveRequest",ResourceId=result.Id.ToString(),PayloadJson=System.Text.Json.JsonSerializer.Serialize(result)},user.Id); return Results.Created($"/api/ess/leave/requests/{result.Id}",result); });

app.MapGet("/api/ess/travel/options", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetTravelOptionsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/travel/requests", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetTravelRequestsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/travel/requests/{id:long}", async (EssMssRepository repository,long id,HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var request=await repository.GetTravelRequestAsync(id,user.EmployeeId.Value,user.ClientId); return request is null ? Results.NotFound() : Results.Ok(request); });
app.MapPost("/api/ess/travel/requests", async (EssMssRepository repository, SaveEssTravelRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(result,error)=await repository.SaveTravelDraftAsync(user.EmployeeId.Value,user.ClientId,request); return result is null ? Results.BadRequest(new{error}) : Results.Ok(result); });
app.MapPost("/api/ess/travel/requests/{id:long}/submit", async (EssMssRepository repository, WorkflowRepository workflows,long id,HttpContext context) =>
{
    var user=CurrentUser(context);
    if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403);
    var(result,error)=await repository.SubmitTravelRequestAsync(user.EmployeeId.Value,user.ClientId,id);
    if(result is null)return Results.BadRequest(new{error});
    var workflowId=await workflows.GetDefaultIdAsync("TravelRequest",user.ClientId);
    if(workflowId is not null) await workflows.StartAsync(new StartWorkflowRequest{WorkflowId=workflowId.Value,ResourceType="TravelRequest",ResourceId=result.Id.ToString(),PayloadJson=System.Text.Json.JsonSerializer.Serialize(result)},user.Id);
    return Results.Ok(result);
});
app.MapPost("/api/ess/travel/requests/{id:long}/withdraw", async (EssMssRepository repository,long id,HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var(ok,error)=await repository.WithdrawTravelRequestAsync(user.EmployeeId.Value,user.ClientId,id); return ok ? Results.NoContent() : Results.BadRequest(new{error}); });
app.MapPost("/api/ess/travel/requests/{id:long}/cancel", async (EssMssRepository repository,long id, JsonElement body,HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var reason=body.TryGetProperty("reason",out var value)?value.GetString()??"":""; var(ok,error)=await repository.CancelTravelRequestAsync(user.EmployeeId.Value,user.ClientId,id,reason); return ok ? Results.NoContent() : Results.BadRequest(new{error}); });
app.MapGet("/api/ess/travel/requests/{id:long}/trail", async (EssMssRepository repository,long id,HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var trail=await repository.GetTravelRequestTrailAsync(id,user.EmployeeId.Value,user.ClientId); return trail is null ? Results.NotFound() : Results.Ok(trail); });
app.MapGet("/api/ess/travel/dashboard", async (EssMssRepository repository,HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetTravelDashboardAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/travel/calendar", async (EssMssRepository repository,string from,string to,HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetTravelCalendarAsync(user.EmployeeId.Value,user.ClientId,from,to)); });

app.MapGet("/api/ess/expenses/options", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetExpenseOptionsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/expenses/dashboard", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetExpenseDashboardAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/expenses/claims", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetExpenseClaimsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/expenses/claims/{id:long}", async (EssMssRepository repository,long id,HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var claim=await repository.GetExpenseClaimAsync(id,user.EmployeeId.Value,user.ClientId); return claim is null ? Results.NotFound() : Results.Ok(claim); });
app.MapPost("/api/ess/expenses/claims", async (EssMssRepository repository, SaveEssExpenseClaim request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(result,error)=await repository.SaveExpenseDraftAsync(user.EmployeeId.Value,user.ClientId,request); return result is null ? Results.BadRequest(new{error}) : Results.Ok(result); });
app.MapPost("/api/ess/expenses/claims/{id:long}/submit", async (EssMssRepository repository, WorkflowRepository workflows,long id,HttpContext context) =>
{
    var user=CurrentUser(context);
    if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403);
    var(result,error)=await repository.SubmitExpenseClaimAsync(user.EmployeeId.Value,user.ClientId,id);
    if(result is null)return Results.BadRequest(new{error});
    var workflowId=await workflows.GetDefaultIdAsync("ExpenseClaim",user.ClientId);
    if(workflowId is not null) await workflows.StartAsync(new StartWorkflowRequest{WorkflowId=workflowId.Value,ResourceType="ExpenseClaim",ResourceId=result.Id.ToString(),PayloadJson=System.Text.Json.JsonSerializer.Serialize(result)},user.Id);
    return Results.Ok(result);
});
app.MapGet("/api/ess/expenses/claims/{id:long}/trail", async (EssMssRepository repository,long id,HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var trail=await repository.GetExpenseClaimTrailAsync(id,user.EmployeeId.Value,user.ClientId); return trail is null ? Results.NotFound() : Results.Ok(trail); });

app.MapGet("/api/ess/recruitment/options", async (RecruitmentRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetOptionsAsync(user.EmployeeId.Value, user.ClientId, user));
});
app.MapGet("/api/ess/recruitment/dashboard", async (RecruitmentRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentAccess(user) ? Results.StatusCode(403) : Results.Ok(await repository.DashboardAsync(user, true));
});
app.MapGet("/api/ess/recruitment/requisitions", async (RecruitmentRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentAccess(user) ? Results.StatusCode(403) : Results.Ok(await repository.GetMineAsync(user.EmployeeId.Value, user.ClientId));
});
app.MapGet("/api/ess/recruitment/requisitions/{id:long}", async (RecruitmentRepository repository, long id, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!RecruitmentRepository.HasRecruitmentAccess(user)) return Results.StatusCode(403);
    return await repository.GetAsync(id, user) is { } row ? Results.Ok(row) : Results.NotFound();
});
app.MapPost("/api/ess/recruitment/requisitions", async (RecruitmentRepository repository, SaveRecruitmentRequisition request, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentCreateAccess(user)) return Results.StatusCode(403);
    var (row, error) = await repository.SaveDraftAsync(request, user);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapDelete("/api/ess/recruitment/requisitions/{id:long}", async (RecruitmentRepository repository, long id, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!RecruitmentRepository.HasRecruitmentCreateAccess(user)) return Results.StatusCode(403);
    var (ok, error) = await repository.DeleteDraftAsync(id, user);
    return ok ? Results.NoContent() : Results.BadRequest(new { error });
});
app.MapPost("/api/ess/recruitment/requisitions/{id:long}/submit", async (RecruitmentRepository repository, WorkflowRepository workflows, long id, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!RecruitmentRepository.HasRecruitmentCreateAccess(user)) return Results.StatusCode(403);
    var (row, error) = await repository.SubmitAsync(id, user, workflows);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/ess/recruitment/requisitions/{id:long}/withdraw", async (RecruitmentRepository repository, long id, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (!RecruitmentRepository.HasRecruitmentCreateAccess(user)) return Results.StatusCode(403);
    var (ok, error) = await repository.WithdrawAsync(id, user);
    return ok ? Results.NoContent() : Results.BadRequest(new { error });
});
app.MapGet("/api/ess/recruitment/requisitions/{id:long}/trail", async (RecruitmentRepository repository, long id, HttpContext context) =>
{
    var user = CurrentUser(context);
    return RecruitmentRepository.HasRecruitmentAccess(user) ? Results.Ok(await repository.TrailAsync(id, user)) : Results.StatusCode(403);
});
app.MapGet("/api/ess/recruitment/internal-openings", async (RecruitmentRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentAccess(user) ? Results.StatusCode(403) : Results.Ok(await repository.InternalOpeningsAsync(user));
});
app.MapGet("/api/ess/recruitment/referrals", async (RecruitmentRepository repository, HttpContext context) =>
{
    var user = CurrentUser(context);
    return user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentAccess(user) ? Results.StatusCode(403) : Results.Ok(await repository.MyReferralsAsync(user));
});
app.MapPost("/api/ess/recruitment/referrals", async (RecruitmentRepository repository, RecruitmentTalentRepository talent, NotificationRepository notifications, SaveEmployeeReferral request, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentAccess(user)) return Results.StatusCode(403);
    var (row, error) = await repository.SubmitReferralAsync(request, user, notifications);
    if (row is null) return Results.BadRequest(new { error });
    return Results.Ok(await talent.LinkReferralAsync(row, user));
});

app.MapGet("/api/recruitment/dashboard", async (RecruitmentRepository repository, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.DashboardAsync(CurrentUser(context), false)) : Results.StatusCode(403));
app.MapGet("/api/recruitment/requisitions", async (RecruitmentRepository repository, int? clientId, string? status, string? query, string? department, string? hiringType, string? employmentType, string? priority, string? businessUnit, string? positionCategory, string? experience, string? location, string? project, bool? replacementHiring, decimal? budgetMin, decimal? budgetMax, DateTime? dateFrom, DateTime? dateTo, int? recruiterUserId, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    return Results.Ok(await repository.SearchAsync(new RecruitmentSearchRequest { ClientId = clientId, Status = status ?? "", Query = query ?? "", Department = department ?? "", HiringType = hiringType ?? "", EmploymentType = employmentType ?? "", Priority = priority ?? "", BusinessUnit = businessUnit ?? "", PositionCategory = positionCategory ?? "", Experience = experience ?? "", Location = location ?? "", Project = project ?? "", ReplacementHiring = replacementHiring, BudgetMin = budgetMin, BudgetMax = budgetMax, DateFrom = dateFrom, DateTo = dateTo, RecruiterUserId = recruiterUserId }, CurrentUser(context)));
});
app.MapPost("/api/recruitment/requisitions", async (RecruitmentRepository repository, SaveRecruitmentRequisition request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveDraftAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/requisitions/{id:long}/submit", async (RecruitmentRepository repository, WorkflowRepository workflows, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SubmitAsync(id, CurrentUser(context), workflows);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});

app.MapGet("/api/recruitment/work-orders", async (RecruitmentCaseRepository repository, int? clientId, string? query, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.work-order.view") && !HasPermission(context, "recruitment.work-order.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    return Results.Ok(await repository.ListWorkOrdersAsync(CurrentUser(context), clientId, query ?? ""));
});
app.MapGet("/api/recruitment/work-orders/{id:long}", async (RecruitmentCaseRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.work-order.view") && !HasPermission(context, "recruitment.work-order.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var row = await repository.GetWorkOrderAsync(id, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
app.MapPost("/api/recruitment/work-orders", async (RecruitmentCaseRepository repository, SaveRecruitmentWorkOrder request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.work-order.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveWorkOrderAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/hiring-cases", async (RecruitmentCaseRepository repository, int? clientId, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.hiring-case.view") && !HasPermission(context, "recruitment.hiring-case.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    return Results.Ok(await repository.ListHiringCasesAsync(CurrentUser(context), clientId));
});
app.MapGet("/api/recruitment/hiring-cases/{id:long}", async (RecruitmentCaseRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.hiring-case.view") && !HasPermission(context, "recruitment.hiring-case.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var row = await repository.GetHiringCaseAsync(id, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
app.MapPost("/api/recruitment/hiring-cases/start", async (RecruitmentCaseRepository repository, StartRecruitmentHiringCaseRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.hiring-case.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.StartHiringCaseAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/hiring-cases/{id:long}/advance", async (RecruitmentCaseRepository repository, long id, MoveRecruitmentHiringCaseRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.hiring-case.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.AdvanceHiringCaseAsync(id, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/hiring-cases/{id:long}/pause", async (RecruitmentCaseRepository repository, long id, RecruitmentStagePauseRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.sla.pause") && !HasPermission(context, "recruitment.hiring-case.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.PauseHiringCaseAsync(id, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/hiring-cases/{id:long}/resume", async (RecruitmentCaseRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.sla.pause") && !HasPermission(context, "recruitment.hiring-case.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.ResumeHiringCaseAsync(id, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/process-documents", async (RecruitmentCaseRepository repository, long? hiringCaseId, long? applicationId, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.document.view") && !HasPermission(context, "recruitment.document.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    return Results.Ok(await repository.ListProcessDocumentsAsync(CurrentUser(context), hiringCaseId, applicationId));
});
app.MapPost("/api/recruitment/process-documents", async (RecruitmentCaseRepository repository, SaveRecruitmentProcessDocument request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.document.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    if (string.Equals(request.Status, "Signed", StringComparison.OrdinalIgnoreCase) && !HasPermission(context, "recruitment.document.sign") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveProcessDocumentAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/process-documents/{id:long}/generate", async (RecruitmentCaseRepository repository, long id, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!HasPermission(context, "recruitment.document.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.GenerateProcessDocumentAsync(id, CurrentUser(context),
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), cancellationToken);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/profile-submission-batches", async (RecruitmentCaseRepository repository, SaveRecruitmentProfileSubmissionBatch request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.shortlist.forward") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.CreateProfileBatchAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/profile-submission-batches", async (RecruitmentCaseRepository repository, long hiringCaseId, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.shortlist.approve") && !HasPermission(context, "recruitment.shortlist.forward") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    return Results.Ok(await repository.ListProfileBatchesAsync(hiringCaseId, CurrentUser(context)));
});
app.MapPost("/api/recruitment/profile-submission-batches/{id:long}/approve", async (RecruitmentCaseRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.shortlist.approve") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.ApproveProfileBatchAsync(id, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/profile-submission-batches/{id:long}/forward", async (RecruitmentCaseRepository repository, NotificationRepository notifications, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.shortlist.forward") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.ForwardProfileBatchAsync(id, CurrentUser(context), notifications);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/open-positions", async (RecruitmentRepository repository, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "recruitment.position.view") || HasPermission(context, "recruitment.position.manage") || HasPermission(context, "recruitment.work-order.manage") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.OpenPositionsAsync(CurrentUser(context))) : Results.StatusCode(403));
app.MapGet("/api/recruitment/operations/options", async (RecruitmentRepository repository, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "recruitment.position.view") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.OperationsOptionsAsync(CurrentUser(context))) : Results.StatusCode(403));
app.MapGet("/api/recruitment/masters/{masterType}", async (RecruitmentRepository repository, string masterType, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.MasterOptionsAsync(masterType, CurrentUser(context))) : Results.StatusCode(403));
app.MapGet("/api/recruitment/open-positions/{id:long}", async (RecruitmentRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var detail = await repository.OpenPositionDetailAsync(id, CurrentUser(context));
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});
app.MapPost("/api/recruitment/open-positions/{id:long}/status", async (RecruitmentRepository repository, long id, UpdateRecruitmentPositionStatus request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.UpdatePositionStatusAsync(id, request, CurrentUser(context));
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});
app.MapPost("/api/recruitment/open-positions/{id:long}/notes", async (RecruitmentRepository repository, long id, SaveRecruitmentPositionNote request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.AddPositionNoteAsync(id, request, CurrentUser(context));
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});
app.MapPost("/api/recruitment/open-positions/{id:long}/assign-recruiter", async (RecruitmentRepository repository, NotificationRepository notifications, long id, SaveRecruiterAssignment request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.assign.recruiter") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.AssignRecruiterAsync(id, request, CurrentUser(context), notifications);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});
app.MapPost("/api/recruitment/open-positions/{id:long}/vendors", async (RecruitmentRepository repository, NotificationRepository notifications, long id, SavePartnerAssignment request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.assign.partner") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.AssignPartnerAsync(id, "Vendor", request, CurrentUser(context), notifications);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});
app.MapPost("/api/recruitment/open-positions/{id:long}/consultants", async (RecruitmentRepository repository, NotificationRepository notifications, long id, SavePartnerAssignment request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.assign.partner") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.AssignPartnerAsync(id, "Consultant", request, CurrentUser(context), notifications);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});
app.MapPost("/api/recruitment/open-positions/{id:long}/publish", async (RecruitmentRepository repository, NotificationRepository notifications, long id, SaveJobPublication request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.publish") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.PublishPositionAsync(id, request, CurrentUser(context), notifications);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});
app.MapPost("/api/recruitment/open-positions/{id:long}/referral-campaigns", async (RecruitmentRepository repository, NotificationRepository notifications, long id, SaveReferralCampaign request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.referral.manage") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (detail, error) = await repository.CreateReferralCampaignAsync(id, request, CurrentUser(context), notifications);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(detail) : Results.BadRequest(new { message = error });
});

app.MapGet("/api/recruitment/talent/dashboard", async (RecruitmentTalentRepository repository, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage")
        ? Results.Ok(await repository.DashboardAsync(CurrentUser(context)))
        : Results.StatusCode(403));
app.MapGet("/api/recruitment/candidates", async (RecruitmentTalentRepository repository, int? clientId, string? query, string? status, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage")
        ? Results.Ok(await repository.SearchCandidatesAsync(CurrentUser(context), clientId, query ?? "", status ?? ""))
        : Results.StatusCode(403));
app.MapGet("/api/recruitment/candidates/{id:long}", async (RecruitmentTalentRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.candidate.view") && !HasPermission(context, "recruitment.interview.panel") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var row = await repository.GetCandidateDetailAsync(id, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
app.MapPost("/api/recruitment/candidates", async (RecruitmentTalentRepository repository, SaveRecruitmentCandidate request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveCandidateAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPut("/api/recruitment/candidates/{id:long}/profile-sections", async (RecruitmentTalentRepository repository, long id, SaveCandidateProfileSections request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveCandidateProfileSectionsAsync(id, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/candidates/{candidateId:long}/resume", async (RecruitmentTalentRepository repository, long candidateId, [FromForm] CandidateResumeUploadRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "attachment.recruitment.upload") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (attachment, resume, error) = await repository.UploadResumeAsync(candidateId, request, CurrentUser(context), context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    return attachment is null ? Results.BadRequest(new { error }) : Results.Ok(new { attachment, resume });
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(30L * 1024 * 1024));
app.MapPost("/api/recruitment/resume-intake", async (RecruitmentTalentRepository talent, RecruitmentPipelineRepository pipelines, RecruitmentPipelineActionService actions, RecruitmentCandidateActionRepository candidateActions, [FromForm] RecruitmentResumeIntakeRequest request, HttpContext context) =>
{
    // Intake creates/updates talent profiles, applications, ATS scores and pipeline state.
    // Attachment-only permission is intentionally insufficient for this business action.
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    // Complex minimal-API form binding can populate the scalar fields while leaving
    // repeated browser file parts out of the nested list. The parsed form collection
    // is the authoritative fallback for single and batch resume intake.
    if ((request.Files is null || request.Files.Count == 0) && context.Request.HasFormContentType)
        request.Files = context.Request.Form.Files.ToList();
    var user = CurrentUser(context);
    var result = await talent.IntakeResumesAsync(request, user, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    foreach (var item in result.Items.Where(item => item.Success && item.Application is not null))
    {
        var applicationId = item.Application!.Id;
        var (pipelineId, _) = await pipelines.EnsureApplicationPipelineAsync(applicationId, user);
        if (pipelineId.HasValue)
        {
            var entry = await actions.ExecuteAsync(applicationId, "OnEntry", user);
            if (!entry.Executions.Any(execution => execution.ActionCode == "GENERATE_ACTION_LINK"))
                await candidateActions.EnsureForCurrentStageAsync(applicationId, user);
        }
        item.Application = (await talent.GetApplicationsAsync(user, item.Application.PositionId, item.Application.CandidateId, "")).FirstOrDefault(row => row.Id == applicationId) ?? item.Application;
    }
    return Results.Ok(result);
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(550L * 1024 * 1024));
app.MapPost("/api/ess/recruitment/referrals/{referralId:long}/resume", async (RecruitmentTalentRepository repository, long referralId, [FromForm] CandidateResumeUploadRequest request, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user.EmployeeId is null || !RecruitmentRepository.HasRecruitmentAccess(user)) return Results.StatusCode(403);
    var (attachment, resume, error) = await repository.UploadReferralResumeAsync(referralId, request, user, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    return attachment is null ? Results.BadRequest(new { error }) : Results.Ok(new { attachment, resume });
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(30L * 1024 * 1024));
app.MapGet("/api/recruitment/applications", async (RecruitmentTalentRepository repository, long? positionId, long? candidateId, string? stage, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage")
        ? Results.Ok(await repository.GetApplicationsAsync(CurrentUser(context), positionId, candidateId, stage ?? ""))
        : Results.StatusCode(403));
app.MapPost("/api/recruitment/applications", async (RecruitmentTalentRepository repository, SaveCandidateApplication request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.CreateApplicationAsync(request, CurrentUser(context));
    return row is null || !string.IsNullOrWhiteSpace(error) ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/applications/{id:long}/stage", async (RecruitmentTalentRepository repository, long id, ChangeCandidateStageRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.ChangeStageAsync(id, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/applications/{id:long}/score", async (RecruitmentTalentRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.ScoreApplicationAsync(id, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/application-scores/{id:long}/override", async (RecruitmentTalentRepository repository, long id, OverrideApplicationScoreRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.OverrideScoreAsync(id, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/interviews", async (RecruitmentTalentRepository repository, long? applicationId, HttpContext context) =>
    HasPermission(context, "recruitment.interview.panel") || HasPermission(context, "recruitment.interview.schedule") || HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetInterviewsAsync(CurrentUser(context), applicationId)) : Results.StatusCode(403));
app.MapGet("/api/recruitment/interviews/scheduling-context/{applicationId:long}", async (RecruitmentTalentRepository repository, long applicationId, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.interview.schedule") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.GetInterviewSchedulingContextAsync(applicationId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/interviews", async (RecruitmentTalentRepository repository, SaveRecruitmentInterview request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.interview.schedule") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveInterviewAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/interviews/{id:long}/feedback", async (RecruitmentTalentRepository repository, long id, HttpContext context) =>
    HasPermission(context, "recruitment.interview.panel") || HasPermission(context, "recruitment.interview.schedule") || HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetInterviewFeedbackAsync(id, CurrentUser(context))) : Results.StatusCode(403));
app.MapPost("/api/recruitment/interviews/{id:long}/feedback", async (RecruitmentTalentRepository repository, long id, SaveRecruitmentInterviewFeedback request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.interview.panel") && !HasPermission(context, "recruitment.interview.schedule") && !HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveInterviewFeedbackAsync(id, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment/offers", async (RecruitmentTalentRepository repository, long? applicationId, HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetOffersAsync(CurrentUser(context), applicationId)) : Results.StatusCode(403));
app.MapPost("/api/recruitment/offers", async (RecruitmentTalentRepository repository, SaveRecruitmentOffer request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveOfferAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/offers/{id:long}/generate-letter", async (RecruitmentTalentRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.GenerateOfferLetterAsync(id, CurrentUser(context),
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/offers/{id:long}/status", async (RecruitmentTalentRepository repository, RecruitmentPipelineActionService pipelineActions, long id, JsonElement request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var status = request.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";
    var remarks = request.TryGetProperty("remarks", out var note) ? note.GetString() ?? "" : "";
    var (row, error) = await repository.UpdateOfferStatusAsync(id, status, remarks, user);
    if (row?.Status == "Pending Candidate")
        await pipelineActions.ExecuteAsync(row.ApplicationId, "OnEntry", user, row.PipelineStageInstanceId);
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/applications/{applicationId:long}/checklist/{itemId:long}/complete", async (RecruitmentTalentRepository repository, long applicationId, long itemId, JsonElement request, HttpContext context) =>
{
    if (!HasPermission(context, "recruitment.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    Guid? publicId = request.TryGetProperty("attachmentPublicId", out var value) && Guid.TryParse(value.GetString(), out var parsed) ? parsed : null;
    var (row, error) = await repository.CompleteChecklistItemAsync(applicationId, itemId, publicId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPost("/api/recruitment/applications/{applicationId:long}/convert-to-employee", async (RecruitmentTalentRepository repository, long applicationId, ConvertCandidateToEmployeeRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "employees.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.ConvertToEmployeeAsync(applicationId, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/employees/{employeeId:int}/activity-360", async (RecruitmentTalentRepository repository, int employeeId, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user.EmployeeId != employeeId && !HasPermission(context, "employees.view") && !HasPermission(context, "employees.manage") && !HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    return Results.Ok(await repository.GetEmployee360Async(employeeId, user));
});

var recruitmentOrchestration = app.MapGroup("/api/recruitment-orchestration");

recruitmentOrchestration.MapGet("/lookups", async (RecruitmentFormRepository forms, AttachmentRepository attachments, WorkflowRepository workflows, RecruitmentRepository recruitment, RecruitmentTalentRepository talent, RecruitmentPipelineRepository pipelines, RecruitmentAdminRepository recruitmentAdmin, int? clientId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var scopedClientId = user.ClientId ?? clientId;
    var administration = await recruitmentAdmin.GetAsync();
    return Results.Ok(new
    {
        lookupSources = await forms.LookupSourcesAsync(),
        attachmentConfigurations = await attachments.GetConfigurationsAsync(scopedClientId),
        workflows = await workflows.GetAsync(),
        forms = await forms.ListAsync(user, scopedClientId),
        positions = await recruitment.OpenPositionsAsync(user),
        atsScoringProfiles = await talent.GetScoringProfilesAsync(user, scopedClientId),
        interviewCompetencies = await pipelines.GetInterviewCompetenciesAsync(scopedClientId, user),
        templates = administration.Templates.Where(row => !scopedClientId.HasValue || row.ClientId == 0 || row.ClientId == scopedClientId.Value)
    });
});
recruitmentOrchestration.MapGet("/lookups/{sourceCode}/options", async (RecruitmentFormRepository repository, string sourceCode, int clientId, string? search, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.ResolveLookupAsync(sourceCode, CurrentUser(context).ClientId ?? clientId, search ?? "")));

recruitmentOrchestration.MapGet("/forms", async (RecruitmentFormRepository repository, int? clientId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.ListAsync(CurrentUser(context), clientId)));
recruitmentOrchestration.MapGet("/forms/{id:long}", async (RecruitmentFormRepository repository, long id, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var row = await repository.GetAsync(id, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/forms", async (RecruitmentFormRepository repository, SaveDynamicFormDefinition request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.SaveDefinitionAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/forms/{id:long}/versions", async (RecruitmentFormRepository repository, long id, SaveDynamicFormVersion request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    request.FormDefinitionId = id;
    var (row, error) = await repository.SaveVersionAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/form-versions/{versionId:long}/publish", async (RecruitmentFormRepository repository, long versionId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.PublishAsync(versionId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});

recruitmentOrchestration.MapGet("/job-descriptions", async (RecruitmentPipelineRepository repository, long requisitionId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetJobDescriptionVersionsAsync(requisitionId, CurrentUser(context))));
recruitmentOrchestration.MapGet("/job-descriptions/{id:long}", async (RecruitmentPipelineRepository repository, long id, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var row = await repository.GetJobDescriptionVersionAsync(id, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/job-descriptions", async (RecruitmentPipelineRepository repository, SaveRecruitmentJobDescriptionVersion request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.SaveJobDescriptionVersionAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/job-descriptions/{id:long}/submit", async (RecruitmentPipelineRepository repository, long id, long workflowId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.SubmitJobDescriptionForApprovalAsync(id, workflowId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});

recruitmentOrchestration.MapGet("/job-postings", async (RecruitmentPipelineRepository repository, int? clientId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetJobPostingsAsync(clientId, CurrentUser(context))));
recruitmentOrchestration.MapGet("/job-postings/{id:long}", async (RecruitmentPipelineRepository repository, long id, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var row = await repository.GetJobPostingAsync(id, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/job-postings", async (RecruitmentPipelineRepository repository, SaveRecruitmentJobPosting request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.SaveJobPostingAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/job-postings/{id:long}/publish", async (RecruitmentPipelineRepository repository, long id, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.PublishJobPostingAsync(id, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/job-postings/{id:long}/close", async (RecruitmentPipelineRepository repository, long id, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : await repository.CloseJobPostingAsync(id, CurrentUser(context)) ? Results.NoContent() : Results.NotFound());

recruitmentOrchestration.MapGet("/pipelines", async (RecruitmentPipelineRepository repository, int? clientId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetPipelinesAsync(clientId, CurrentUser(context))));
recruitmentOrchestration.MapGet("/pipelines/{id:long}", async (RecruitmentPipelineRepository repository, long id, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var definition = (await repository.GetPipelinesAsync(CurrentUser(context).ClientId, CurrentUser(context))).FirstOrDefault(row => row.Id == id);
    if (definition is null) return Results.NotFound();
    return Results.Ok(new { definition, versions = await repository.GetPipelineVersionsAsync(id, CurrentUser(context)) });
});
recruitmentOrchestration.MapPost("/pipelines", async (RecruitmentPipelineRepository repository, SaveRecruitmentPipelineDefinition request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.SavePipelineAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapGet("/pipelines/{id:long}/versions", async (RecruitmentPipelineRepository repository, long id, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetPipelineVersionsAsync(id, CurrentUser(context))));
recruitmentOrchestration.MapGet("/pipeline-versions/{versionId:long}", async (RecruitmentPipelineRepository repository, long versionId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var row = await repository.GetPipelineVersionAsync(versionId, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/pipelines/{id:long}/versions", async (RecruitmentPipelineRepository repository, long id, SaveRecruitmentPipelineVersion request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    request.PipelineDefinitionId = id;
    var (row, error) = await repository.SavePipelineVersionAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/pipeline-versions/{versionId:long}/publish", async (RecruitmentPipelineRepository repository, long versionId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.PublishPipelineVersionAsync(versionId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/pipeline-assignments", async (RecruitmentPipelineRepository repository, AssignRecruitmentPipelineRequest request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.AssignPipelineAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapGet("/pipeline-assignments/{positionId:long}", async (RecruitmentPipelineRepository repository, long positionId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var row = await repository.GetPositionPipelineAssignmentAsync(positionId, CurrentUser(context));
    return row is null ? Results.NotFound() : Results.Ok(row);
});
recruitmentOrchestration.MapGet("/interview-competencies", async (RecruitmentPipelineRepository repository, int? clientId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetInterviewCompetenciesAsync(clientId, CurrentUser(context))));
recruitmentOrchestration.MapPost("/interview-competencies", async (RecruitmentPipelineRepository repository, RecruitmentInterviewCompetencyDefinition request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.SaveInterviewCompetencyAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});

recruitmentOrchestration.MapPost("/applications/{applicationId:long}/pipeline", async (RecruitmentPipelineRepository repository, RecruitmentPipelineActionService actions, RecruitmentCandidateActionRepository candidateActions, long applicationId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var (id, error) = await repository.EnsureApplicationPipelineAsync(applicationId, user);
    if (id.HasValue)
    {
        var entry = await actions.ExecuteAsync(applicationId, "OnEntry", user);
        if (!entry.Executions.Any(item => item.ActionCode == "GENERATE_ACTION_LINK"))
            await candidateActions.EnsureForCurrentStageAsync(applicationId, user);
    }
    return id.HasValue ? Results.Ok(new { pipelineInstanceId = id.Value }) : Results.BadRequest(new { error });
});
recruitmentOrchestration.MapGet("/pipeline-board", async (RecruitmentPipelineRepository repository, long positionId, long? jobPostingId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var board = await repository.GetPipelineBoardAsync(positionId, CurrentUser(context), jobPostingId);
    return board is null ? Results.NotFound() : Results.Ok(board);
});
recruitmentOrchestration.MapGet("/applications/{applicationId:long}/transitions", async (RecruitmentPipelineRepository repository, long applicationId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetAvailableTransitionsAsync(applicationId, CurrentUser(context))));
recruitmentOrchestration.MapGet("/pipeline-stages/{stageId:long}/actions", async (RecruitmentPipelineRepository repository, long stageId, string? triggerEvent, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetStageActionsAsync(stageId, triggerEvent ?? "OnEntry", CurrentUser(context))));
recruitmentOrchestration.MapGet("/applications/{applicationId:long}/stage-action-executions", async (RecruitmentPipelineActionService actions, long applicationId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await actions.GetExecutionsAsync(applicationId, CurrentUser(context))));
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/stage-actions/{triggerEvent}", async (RecruitmentPipelineActionService actions, long applicationId, string triggerEvent, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var result = await actions.ExecuteAsync(applicationId, triggerEvent, CurrentUser(context));
    return string.IsNullOrWhiteSpace(result.TriggerEvent) ? Results.BadRequest(new { error = "Unsupported stage action trigger." }) : Results.Ok(result);
});
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/evaluate-ats", async (RecruitmentPipelineRepository repository, RecruitmentCandidateActionRepository candidateActions, RecruitmentPipelineActionService actions, long applicationId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var (row, error) = await repository.EvaluateAtsStageAutomationAsync(applicationId, user);
    if (row?.Status == "Applied")
    {
        await actions.ExecuteAsync(applicationId, "OnExit", user);
        var entry = await actions.ExecuteAsync(applicationId, "OnEntry", user);
        if (!entry.Executions.Any(item => item.ActionCode == "GENERATE_ACTION_LINK"))
            await candidateActions.EnsureForCurrentStageAsync(applicationId, user);
    }
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/transitions/{transitionId:long}", async (RecruitmentPipelineRepository repository, RecruitmentCandidateActionRepository candidateActions, RecruitmentPipelineActionService actions, long applicationId, long transitionId, RecruitmentPipelineTransitionRequest request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var currentActions = await actions.ExecuteAsync(applicationId, "OnEntry", user);
    if (currentActions.HasBlockingFailure)
    {
        var blockers = currentActions.Executions
            .Where(item => item.IsBlocking && !item.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var actionName = item.ActionCode.Replace('_', ' ').ToLowerInvariant();
                var reason = !string.IsNullOrWhiteSpace(item.ErrorMessage)
                    ? item.ErrorMessage
                    : item.Status.Equals("Pending Approval", StringComparison.OrdinalIgnoreCase)
                        ? "Approval is still pending."
                        : $"Current status is {item.Status}.";
                return $"{actionName}: {reason}";
            })
            .ToList();
        var detail = blockers.Count > 0
            ? string.Join(" ", blockers)
            : "A required stage action is still incomplete.";
        return Results.BadRequest(new
        {
            code = "RECRUITMENT_STAGE_ACTION_BLOCKED",
            error = $"Candidate cannot leave this stage yet. {detail}",
            executions = currentActions.Executions
        });
    }
    request.TransitionId = transitionId;
    var (row, error) = await repository.RequestTransitionAsync(applicationId, request, user);
    if (row?.Status == "Applied")
    {
        await actions.ExecuteAsync(applicationId, "OnExit", user);
        var entry = await actions.ExecuteAsync(applicationId, "OnEntry", user);
        if (!entry.Executions.Any(item => item.ActionCode == "GENERATE_ACTION_LINK"))
            await candidateActions.EnsureForCurrentStageAsync(applicationId, user);
    }
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/pause", async (RecruitmentPipelineRepository repository, long applicationId, RecruitmentStagePauseRequest request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.PauseStageAsync(applicationId, request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/resume", async (RecruitmentPipelineRepository repository, long applicationId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.ResumeStageAsync(applicationId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});

recruitmentOrchestration.MapGet("/applications/{applicationId:long}/candidate-actions", async (RecruitmentCandidateActionRepository repository, long applicationId, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : Results.Ok(await repository.ListAsync(applicationId, CurrentUser(context))));
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/candidate-actions", async (RecruitmentCandidateActionRepository repository, long applicationId, CreateRecruitmentCandidateActionRequest request, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    request.ApplicationId = applicationId;
    var (row, error) = await repository.CreateAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/applications/{applicationId:long}/candidate-actions/current-stage", async (RecruitmentCandidateActionRepository repository, long applicationId, HttpContext context) =>
{
    if (!HasRecruitmentManagement(context)) return Results.StatusCode(403);
    var (row, error) = await repository.CreateForCurrentStageAsync(applicationId, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
recruitmentOrchestration.MapPost("/candidate-actions/{id:long}/revoke", async (RecruitmentCandidateActionRepository repository, long id, HttpContext context) =>
    !HasRecruitmentManagement(context) ? Results.StatusCode(403) : await repository.RevokeAsync(id, CurrentUser(context)) ? Results.NoContent() : Results.NotFound());

app.MapGet("/api/public/recruitment/jobs/{slug}", async (RecruitmentFormRepository repository, string slug) =>
{
    var row = await repository.GetPublicJobAsync(slug);
    return row is null ? Results.NotFound(new { error = "This vacancy is unavailable or closed." }) : Results.Ok(row);
});
app.MapPost("/api/public/recruitment/jobs/{slug}/sessions", async (RecruitmentFormRepository repository, string slug, StartPublicApplicationRequest request, HttpContext context) =>
{
    var (row, error) = await repository.StartPublicSessionAsync(slug, request, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapPut("/api/public/recruitment/sessions/{token}/values", async (RecruitmentFormRepository repository, string token, SavePublicFormValuesRequest request, HttpContext context) =>
{
    var (ok, error) = await repository.SavePublicValuesAsync(token, request, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return ok ? Results.NoContent() : Results.BadRequest(new { error });
});
app.MapGet("/api/public/recruitment/sessions/{token}/fields/{fieldId:long}/options", async (RecruitmentFormRepository repository, string token, long fieldId, string? search) =>
{
    var (items, error) = await repository.ResolvePublicLookupAsync(token, fieldId, search ?? "");
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(items) : Results.BadRequest(new { error });
});
app.MapPost("/api/public/recruitment/sessions/{token}/files/{fieldId:long}", async (RecruitmentFormRepository forms, AttachmentRepository attachments, string token, long fieldId, [FromForm] PublicFormAttachmentUploadRequest request, HttpContext context) =>
{
    if (request.File is null || request.File.Length == 0) return Results.BadRequest(new { error = "Select a non-empty file." });
    var (authorization, authorizationError) = await forms.AuthorizeUploadAsync(token, fieldId);
    if (authorization is null) return Results.BadRequest(new { error = authorizationError });
    var externalUser = new AuthUser
    {
        Id = 0,
        ClientId = authorization.ClientId,
        IsActive = true,
        DisplayName = "External candidate",
        Permissions = ["attachment.recruitment.upload"]
    };
    var (attachment, uploadError) = await attachments.UploadAsync(new AttachmentUploadMetadata
    {
        FieldConfigurationId = authorization.AttachmentFieldConfigurationId,
        EntityType = "FORM_SUBMISSION",
        EntityId = authorization.SubmissionId,
        DocumentNumber = request.DocumentNumber,
        IssueDate = request.IssueDate,
        ExpiryDate = request.ExpiryDate
    }, request.File, externalUser, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    if (attachment is null) return Results.BadRequest(new { error = uploadError });
    await forms.LinkAttachmentAsync(token, fieldId, attachment.Id, attachment.PublicId, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return Results.Ok(new { fieldId, attachmentPublicId = attachment.PublicId, attachment.OriginalFileName, attachment.FileSizeBytes, attachment.UploadedAtUtc });
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(30L * 1024 * 1024));
app.MapPost("/api/public/recruitment/sessions/{token}/submit", async (RecruitmentFormRepository forms, RecruitmentTalentRepository talent, RecruitmentPipelineRepository pipelines, RecruitmentPipelineActionService pipelineActions, RecruitmentCandidateActionRepository candidateActions, string token, HttpContext context) =>
{
    var (row, error) = await forms.SubmitPublicApplicationAsync(token, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    if (row is null) return Results.BadRequest(new { error });
    var systemUser = new AuthUser { Id = 0, ClientId = null, IsActive = true, DisplayName = "Public recruitment portal", Permissions = ["recruitment.manage"] };
    var (_, resumeWarning) = await talent.ProcessPublicApplicationResumeAsync(row.ApplicationId, systemUser,
        context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    var (_, pipelineError) = await pipelines.EnsureApplicationPipelineAsync(row.ApplicationId, systemUser);
    if (string.IsNullOrWhiteSpace(pipelineError))
    {
        var entry = await pipelineActions.ExecuteAsync(row.ApplicationId, "OnEntry", systemUser);
        if (!entry.Executions.Any(item => item.ActionCode == "GENERATE_ACTION_LINK"))
            await candidateActions.EnsureForCurrentStageAsync(row.ApplicationId, systemUser);
    }
    return string.IsNullOrWhiteSpace(resumeWarning) && string.IsNullOrWhiteSpace(pipelineError)
        ? Results.Ok(row)
        : Results.Ok(new
        {
            row.SubmissionId,
            row.CandidateId,
            row.ApplicationId,
            row.ApplicationCode,
            row.Status,
            row.Message,
            resumeWarning,
            pipelineWarning = pipelineError
        });
});

app.MapGet("/api/public/recruitment/actions/{token}", async (RecruitmentCandidateActionRepository repository, string token) =>
{
    var row = await repository.GetPublicAsync(token);
    return row is null ? Results.NotFound(new { error = "This secure candidate link is invalid, expired or already completed." }) : Results.Ok(row);
});
app.MapPut("/api/public/recruitment/actions/{token}/values", async (RecruitmentCandidateActionRepository repository, string token, SavePublicFormValuesRequest request) =>
{
    var (ok, error) = await repository.SaveValuesAsync(token, request);
    return ok ? Results.NoContent() : Results.BadRequest(new { error });
});
app.MapGet("/api/public/recruitment/actions/{token}/fields/{fieldId:long}/options", async (RecruitmentCandidateActionRepository actions, string token, long fieldId, string? search) =>
    Results.Ok(await actions.ResolveLookupAsync(token, fieldId, search ?? "")));
app.MapPost("/api/public/recruitment/actions/{token}/files/{fieldId:long}", async (RecruitmentCandidateActionRepository actions, AttachmentRepository attachments, string token, long fieldId, [FromForm] PublicFormAttachmentUploadRequest request, HttpContext context) =>
{
    if (request.File is null || request.File.Length == 0) return Results.BadRequest(new { error = "Select a non-empty file." });
    var (authorization, authorizationError) = await actions.AuthorizeUploadAsync(token, fieldId);
    if (authorization is null) return Results.BadRequest(new { error = authorizationError });
    var externalUser = new AuthUser { Id = 0, ClientId = authorization.ClientId, IsActive = true, DisplayName = "External candidate", Permissions = ["attachment.recruitment.upload"] };
    var (attachment, uploadError) = await attachments.UploadAsync(new AttachmentUploadMetadata
    {
        FieldConfigurationId = authorization.AttachmentFieldConfigurationId,
        EntityType = "FORM_SUBMISSION",
        EntityId = authorization.SubmissionId,
        DocumentNumber = request.DocumentNumber,
        IssueDate = request.IssueDate,
        ExpiryDate = request.ExpiryDate
    }, request.File, externalUser, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
    if (attachment is null) return Results.BadRequest(new { error = uploadError });
    await actions.LinkAttachmentAsync(token, fieldId, attachment.Id, attachment.PublicId);
    return Results.Ok(new { fieldId, attachmentPublicId = attachment.PublicId, attachment.OriginalFileName, attachment.FileSizeBytes, attachment.UploadedAtUtc });
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(30L * 1024 * 1024));
app.MapPost("/api/public/recruitment/actions/{token}", async (RecruitmentCandidateActionRepository repository, RecruitmentPipelineActionService pipelineActions, string token, CompletePublicCandidateActionRequest request, HttpContext context) =>
{
    var (row, error) = await repository.CompleteAsync(token, request, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    if (row is not null)
    {
        var systemUser = new AuthUser { Id = 0, ClientId = null, IsActive = true, DisplayName = "External candidate portal" };
        await pipelineActions.ExecuteAsync(row.ApplicationId, "OnSubmission", systemUser, row.PipelineStageInstanceId);
    }
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/public/recruitment/actions/{token}/offer-document", async (RecruitmentCandidateActionRepository actions, AttachmentRepository attachments, AttachmentStorageService storage, string token, HttpContext context) =>
{
    var document = await actions.GetOfferDocumentAsync(token);
    if (!document.PublicId.HasValue) return Results.NotFound();
    var externalUser = new AuthUser { Id = 0, ClientId = document.ClientId, IsActive = true, DisplayName = "External candidate", Permissions = ["attachment.recruitment.view"] };
    var (attachment, server, _) = await attachments.GetForContentAsync(document.PublicId.Value, externalUser, "CANDIDATE_OFFER_PREVIEW", context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    return attachment is null || server is null ? Results.NotFound() : new AttachmentContentResult(storage, server, attachment, true);
});

app.MapGet("/api/travel-advances", async (EssMssRepository repository, int? clientId, string? status, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.payments") && !HasPermission(context, "payroll.run")) return Results.StatusCode(403);
    var user = CurrentUser(context);
    var scopedClientId = user.ClientId ?? clientId;
    return Results.Ok(await repository.GetTravelAdvancesAsync(scopedClientId, status ?? ""));
});
app.MapPost("/api/travel-advances/{id:long}/pay", async (EssMssRepository repository, long id, PayTravelAdvanceRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.payments")) return Results.StatusCode(403);
    var(result,error)=await repository.PayTravelAdvanceAsync(id,request,CurrentUser(context).Id);
    return result is null ? Results.BadRequest(new{error}) : Results.Ok(result);
});
app.MapPost("/api/travel-advances/{id:long}/settle", async (EssMssRepository repository, long id, SettleTravelAdvanceRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.payments")) return Results.StatusCode(403);
    var(result,error)=await repository.SettleTravelAdvanceAsync(id,request,CurrentUser(context).Id);
    return result is null ? Results.BadRequest(new{error}) : Results.Ok(result);
});
app.MapPost("/api/travel-advances/{id:long}/recover", async (EssMssRepository repository, long id, RecoverTravelAdvanceRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.payments")) return Results.StatusCode(403);
    var(result,error)=await repository.MarkTravelAdvanceRecoverableAsync(id,request,CurrentUser(context).Id);
    return result is null ? Results.BadRequest(new{error}) : Results.Ok(result);
});

app.MapPost("/api/auth/logout", async (AuthRepository repository, HttpContext context) =>
{
    var token = ReadAuthToken(context, AuthCookieName);
    await repository.LogoutAsync(token, CurrentUser(context), context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
    ClearAuthCookie(context, AuthCookieName);
    return Results.NoContent();
})
.WithName("Logout")
.WithOpenApi();

app.MapGet("/api/security/users", async (AuthRepository repository, HttpContext context) =>
    HasPermission(context, "security.manage") ? Results.Ok(await repository.GetUsersAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden))
.WithName("GetSecurityUsers")
.WithOpenApi();

app.MapGet("/api/security/roles", async (AuthRepository repository, HttpContext context) =>
    HasPermission(context, "security.manage") ? Results.Ok(await repository.GetRolesAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden))
.WithName("GetSecurityRoles")
.WithOpenApi();

app.MapGet("/api/security/permissions", async (AuthRepository repository, HttpContext context) =>
    HasPermission(context, "security.manage") ? Results.Ok(await repository.GetPermissionsAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden))
.WithName("GetSecurityPermissions")
.WithOpenApi();

app.MapPost("/api/security/users", async (AuthRepository repository, SaveAuthUserRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "security.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
        return Results.BadRequest(new { error = "Email and display name are required." });
    if (request.Id == 0 && string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Temporary password is required for a new user." });
    try
    {
        var user = await repository.SaveUserAsync(request);
        return user is null ? Results.BadRequest(new { error = "Unable to save user." }) : Results.Ok(user);
    }
    catch (Exception ex) when (ex.Message.Contains("UX_AuthUsers_Email", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "A user with this email/login ID already exists." });
    }
    catch (Exception)
    {
        return Results.BadRequest(new { error = "Unable to save user. Please verify user details and try again." });
    }
})
.WithName("SaveSecurityUser")
.WithOpenApi();

app.MapDelete("/api/security/users/{id:int}", async (AuthRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "security.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return await repository.DeleteUserAsync(id) ? Results.NoContent() : Results.NotFound(new { error = "User not found." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception)
    {
        return Results.BadRequest(new { error = "Unable to delete user." });
    }
})
.WithName("DeleteSecurityUser")
.WithOpenApi();

app.MapGet("/api/security/users/employee-provision-preview", async (AuthRepository repository, HttpContext context, int? clientId) =>
{
    if (!HasPermission(context, "security.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.GetEmployeeProvisionPreviewAsync(clientId));
})
.WithName("GetEmployeeProvisionPreview")
.WithOpenApi();

app.MapPost("/api/security/users/provision-employees", async (AuthRepository repository, ProvisionEmployeeLoginsRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "security.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.EmployeeIds.Count == 0)
        return Results.BadRequest(new { error = "Select at least one employee." });
    try
    {
        return Results.Ok(await repository.ProvisionEmployeeLoginsAsync(request));
    }
    catch (Exception)
    {
        return Results.BadRequest(new { error = "Unable to provision employee logins. Please review selected employees and try again." });
    }
})
.WithName("ProvisionEmployeeLogins")
.WithOpenApi();

app.MapPost("/api/security/roles", async (AuthRepository repository, SaveAuthRoleRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "security.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Role code and name are required." });
    try
    {
        var role = await repository.SaveRoleAsync(request);
        return role is null ? Results.BadRequest(new { error = "Unable to save role." }) : Results.Ok(role);
    }
    catch (Exception ex) when (ex.Message.Contains("UX_AuthRoles_Code", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "A role with this code already exists." });
    }
    catch (Exception)
    {
        return Results.BadRequest(new { error = "Unable to save role. Please verify role details and try again." });
    }
})
.WithName("SaveSecurityRole")
.WithOpenApi();

app.MapDelete("/api/security/roles/{id:int}", async (AuthRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "security.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return await repository.DeleteRoleAsync(id) ? Results.NoContent() : Results.NotFound(new { error = "Role not found." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception)
    {
        return Results.BadRequest(new { error = "Unable to delete role." });
    }
})
.WithName("DeleteSecurityRole")
.WithOpenApi();

app.MapGet("/api/audit-logs", async (AuthRepository repository, HttpContext context, int limit = 100) =>
    HasPermission(context, "audit.view") ? Results.Ok(await repository.GetAuditLogsAsync(limit)) : Results.StatusCode(StatusCodes.Status403Forbidden))
.WithName("GetAuditLogs")
.WithOpenApi();

app.MapPost("/api/admin/database/migrate", async (HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    await RunDatabaseSetupAsync(context.RequestServices, context.RequestServices.GetRequiredService<IConfiguration>());
    return Results.Ok(new { message = "Database setup completed." });
})
.WithName("MigrateDatabase")
.WithOpenApi();

app.MapGet("/api/notifications/setup", async (NotificationRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetSetupAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/notifications/smtp", async (NotificationRepository repository, NotificationSmtpSetting request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveSmtpAsync(request)) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/notifications/templates", async (NotificationRepository repository, NotificationTemplate request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(await repository.SaveTemplateAsync(request)); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPost("/api/notifications/rules", async (NotificationRepository repository, NotificationRule request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(await repository.SaveRuleAsync(request)); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPost("/api/notifications/queue/{id:long}/retry", async (NotificationRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var processed = await repository.RetryAndProcessAsync(id, context.RequestAborted);
    return Results.Ok(new { processed });
});
app.MapPost("/api/notifications/test", async (NotificationRepository repository, NotificationTestRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.RuleId <= 0 || string.IsNullOrWhiteSpace(request.ToEmail)) return Results.BadRequest(new { error = "Rule and test email are required." });
    await repository.QueueTestAsync(request, CurrentUser(context).Id);
    return Results.NoContent();
});

app.MapGet("/api/communication-settings/providers", async (CommunicationRepository repository, int? clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (CurrentUser(context).ClientId.HasValue && clientId.HasValue && CurrentUser(context).ClientId != clientId) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.GetProvidersAsync(clientId, CurrentUser(context)));
})
.WithName("GetCommunicationProviders")
.WithOpenApi();

app.MapPost("/api/communication-settings/providers", async (CommunicationRepository repository, SaveCommunicationProviderAccountRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (item, error) = await repository.SaveProviderAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveCommunicationProvider")
.WithOpenApi();

app.MapPost("/api/communication-settings/providers/{id:long}/test", async (CommunicationRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var result = await repository.TestProviderAsync(id, CurrentUser(context), context.RequestAborted);
    return Results.Ok(result);
})
.WithName("TestCommunicationProvider")
.WithOpenApi();

app.MapGet("/api/communication-settings/templates", async (CommunicationRepository repository, int? clientId, string? channel, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "employee.communication.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (CurrentUser(context).ClientId.HasValue && clientId.HasValue && CurrentUser(context).ClientId != clientId) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.GetTemplatesAsync(clientId, channel, CurrentUser(context), HasPermission(context, "settings.manage")));
})
.WithName("GetCommunicationTemplates")
.WithOpenApi();

app.MapPost("/api/communication-settings/templates", async (CommunicationRepository repository, CommunicationTemplate request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (item, error) = await repository.SaveTemplateAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveCommunicationTemplate")
.WithOpenApi();

app.MapPost("/api/employee-communications/drafts", async (CommunicationRepository repository, CreateEmployeeCommunicationDraftRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.send")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (!CanAccessClient(context, request.ClientId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (item, error) = await repository.CreateDraftAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("CreateEmployeeCommunicationDraft")
.WithOpenApi();

app.MapGet("/api/employee-communications/recipients", async (CommunicationRepository repository, int clientId, string? search, int? workLocationId, string? department, string? designation, int? limit, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (!CanAccessClient(context, clientId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.SearchRecipientsAsync(CurrentUser(context), clientId, search, workLocationId, department, designation, limit ?? 250));
})
.WithName("GetEmployeeCommunicationRecipients")
.WithOpenApi();

app.MapPost("/api/employee-communications/preview", async (CommunicationRepository repository, CommunicationSelectionRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.send")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (!CanAccessClient(context, request.ClientId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.PreviewAsync(request, CurrentUser(context)));
})
.WithName("PreviewEmployeeCommunication")
.WithOpenApi();

app.MapPost("/api/employee-communications/send", async (CommunicationRepository repository, SendEmployeeCommunicationRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.send")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!CanAccessClient(context, request.ClientId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (item, error) = await repository.SendAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SendEmployeeCommunication")
.WithOpenApi();

app.MapGet("/api/employee-communications/campaigns", async (CommunicationRepository repository, int? clientId, string? channel, string? status, string? search, int? page, int? pageSize, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (CurrentUser(context).ClientId.HasValue && clientId.HasValue && CurrentUser(context).ClientId != clientId) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.GetCampaignsAsync(CurrentUser(context), clientId, channel, status, search, page ?? 1, pageSize ?? 25));
})
.WithName("GetEmployeeCommunicationCampaigns")
.WithOpenApi();

app.MapGet("/api/employee-communications/campaigns/{id:long}", async (CommunicationRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var item = await repository.GetCampaignAsync(id, CurrentUser(context));
    return item is null ? Results.NotFound(new { error = "Campaign not found." }) : Results.Ok(item);
})
.WithName("GetEmployeeCommunicationCampaign")
.WithOpenApi();

app.MapPost("/api/employee-communications/campaigns/{id:long}/retry-failed", async (CommunicationRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.send")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (item, error) = await repository.RetryFailedAsync(id, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("RetryEmployeeCommunicationCampaign")
.WithOpenApi();

app.MapGet("/api/employee-communications/conversations", async (CommunicationRepository repository, int? clientId, string? channel, string? status, string? search, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (CurrentUser(context).ClientId.HasValue && clientId.HasValue && CurrentUser(context).ClientId != clientId) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var items = await repository.GetConversationsAsync(CurrentUser(context), clientId, channel, status, search);
    return Results.Ok(new { items, total = items.Count });
})
.WithName("GetEmployeeCommunicationConversations")
.WithOpenApi();

app.MapGet("/api/employee-communications/conversations/{id:long}", async (CommunicationRepository repository, long id, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var item = await repository.GetConversationAsync(id, CurrentUser(context));
    return item is null ? Results.NotFound(new { error = "Conversation not found." }) : Results.Ok(item);
})
.WithName("GetEmployeeCommunicationConversation")
.WithOpenApi();

app.MapPost("/api/employee-communications/conversations/{id:long}/reply", async (CommunicationRepository repository, long id, CommunicationConversationReplyRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "employee.communication.send")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (item, error) = await repository.ReplyAsync(id, request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("ReplyEmployeeCommunicationConversation")
.WithOpenApi();

app.MapPost("/api/public/employee-communications/webhooks/{providerCode}", async (CommunicationRepository repository, string providerCode, long accountId, CommunicationWebhookRequest request, HttpContext context) =>
{
    var secret = context.Request.Headers["X-Communication-Webhook-Secret"].ToString();
    var result = await repository.HandleWebhookAsync(accountId, providerCode, secret, request);
    if (!result.Accepted && result.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
    return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
})
.WithName("ReceiveEmployeeCommunicationWebhook")
.WithOpenApi();

app.MapGet("/api/scheduled-jobs", async (ScheduledJobRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/scheduled-jobs/actions", async (ScheduledJobRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetActionsAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/scheduled-jobs/actions", async (ScheduledJobRepository repository, ScheduledJobActionSaveRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(await repository.SaveActionAsync(request)); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapGet("/api/scheduled-jobs/handlers", async (ScheduledJobRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetHandlerOptionsAsync()) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/scheduled-jobs/runs", async (ScheduledJobRepository repository, int? jobId, int? limit, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetRunsAsync(jobId, limit ?? 100)) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/scheduled-jobs", async (ScheduledJobRepository repository, ScheduledJobSaveRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.JobCode) || string.IsNullOrWhiteSpace(request.JobName) || string.IsNullOrWhiteSpace(request.HandlerKey))
        return Results.BadRequest(new { error = "Job code, job name, and handler are required." });
    try { return Results.Ok(await repository.SaveAsync(request)); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPost("/api/scheduled-jobs/{id:int}/enabled", async (ScheduledJobRepository repository, int id, bool isEnabled, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var job = await repository.SetEnabledAsync(id, isEnabled);
    return job is null ? Results.NotFound(new { error = "Scheduled job not found." }) : Results.Ok(job);
});
app.MapPost("/api/scheduled-jobs/{id:int}/run-now", async (ScheduledJobRepository repository, int id, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        var run = await repository.RunJobAsync(id, CurrentUser(context).Email, cancellationToken);
        return run is null ? Results.NotFound(new { error = "Scheduled job not found." }) : Results.Ok(run);
    }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/reports/{code}", async (ReportingRepository repository, string code, int clientId, string? department, int? workLocationId, string? fromDate, string? toDate, string? month, int? payRunId, int? employeeId, string? componentCode, HttpContext context) =>
{
    if (!HasPermission(context, "reports.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    return Results.Ok(await repository.RunAsync(code, new ReportFilter { ClientId = clientId, Department = department, WorkLocationId = workLocationId, FromDate = fromDate, ToDate = toDate, Month = month, PayRunId = payRunId, EmployeeId = employeeId, ComponentCode = componentCode }));
})
.WithName("RunReport")
.WithOpenApi();

app.MapGet("/api/organization", async (OrganizationRepository repository) =>
{
    var organization = await repository.GetAsync();
    return organization is not null ? Results.Ok(organization) : Results.NotFound();
})
.WithName("GetOrganization")
.WithOpenApi();

app.MapGet("/api/public/organization-brand", async (OrganizationRepository repository) =>
{
    var organization = await repository.GetAsync();
    return organization is not null ? Results.Ok(new { organization.Name, organization.LogoDataUrl }) : Results.NotFound();
})
.WithName("GetPublicOrganizationBrand")
.WithOpenApi();

app.MapPost("/api/organization", async (OrganizationRepository repository, Organization organization) =>
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(organization.Name))
    {
        errors[nameof(organization.Name)] = ["Organization name is required."];
    }

    if (string.IsNullOrWhiteSpace(organization.BusinessLocation))
        errors[nameof(organization.BusinessLocation)] = ["Business location is required."];

    if (string.IsNullOrWhiteSpace(organization.Industry))
        errors[nameof(organization.Industry)] = ["Industry is required."];

    if (string.IsNullOrWhiteSpace(organization.AddressLine1))
        errors[nameof(organization.AddressLine1)] = ["Address is required."];

    if (string.IsNullOrWhiteSpace(organization.City))
        errors[nameof(organization.City)] = ["City is required."];

    if (string.IsNullOrWhiteSpace(organization.State))
        errors[nameof(organization.State)] = ["State is required."];

    if (!System.Text.RegularExpressions.Regex.IsMatch(organization.PostalCode ?? "", @"^[1-9][0-9]{5}$"))
        errors[nameof(organization.PostalCode)] = ["Enter a valid 6-digit Indian postal code."];

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    organization.Name = organization.Name.Trim();
    organization.BusinessLocation = organization.BusinessLocation.Trim();
    organization.Industry = organization.Industry.Trim();
    organization.SetupCompleted = true;

    var id = await repository.SaveAsync(organization);
    var saved = await repository.GetAsync();
    return Results.Created($"/api/organization/{id}", saved);
})
.WithName("SaveOrganization")
.WithOpenApi();

app.MapGet("/api/setup", async (SettingsRepository repository) =>
    Results.Text(await repository.GetAsync(), "application/json"))
.WithName("GetPayrollSetup")
.WithOpenApi();

app.MapPost("/api/setup", async (SettingsRepository repository, JsonElement setup) =>
{
    await repository.SaveAsync(setup.GetRawText());
    return Results.Ok(setup);
})
.WithName("SavePayrollSetup")
.WithOpenApi();

app.MapGet("/api/client-billing/module", async (ClientBillingRepository repository) =>
    Results.Ok(await repository.GetModuleAsync()))
.WithName("GetClientBillingModule")
.WithOpenApi();

app.MapPost("/api/client-billing/module", async (ClientBillingRepository repository, ClientBillingModule module, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    await repository.SaveModuleAsync(module);
    return Results.NoContent();
})
.WithName("SaveClientBillingModule")
.WithOpenApi();

app.MapGet("/api/client-billing/configurations", async (ClientBillingRepository repository) =>
    Results.Ok(await repository.GetAsync()))
.WithName("GetClientBillingConfigurations")
.WithOpenApi();

app.MapPost("/api/client-billing/configurations", async (ClientBillingRepository repository, ClientBillingConfiguration row, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (id, error) = await repository.SaveAsync(row);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("SaveClientBillingConfiguration")
.WithOpenApi();

app.MapGet("/api/client-billing/advanced", async (ClientBillingRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetAdvancedAsync()) : Results.StatusCode(403))
.WithName("GetClientBillingAdvanced")
.WithOpenApi();

app.MapPost("/api/client-billing/advanced/headers", async (ClientBillingRepository repository, ClientBillingCostRuleHeader row, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (id, error) = await repository.SaveAdvancedHeaderAsync(row);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("SaveClientBillingAdvancedHeader")
.WithOpenApi();

app.MapPost("/api/client-billing/advanced/lines", async (ClientBillingRepository repository, ClientBillingCostRuleLine row, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (id, error) = await repository.SaveAdvancedLineAsync(row);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("SaveClientBillingAdvancedLine")
.WithOpenApi();

app.MapPost("/api/client-billing/advanced/templates/standard", async (ClientBillingRepository repository, JsonElement body, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var clientId = body.TryGetProperty("clientId", out var clientValue) ? clientValue.GetInt32() : 0;
    int? locationId = body.TryGetProperty("workLocationId", out var locationValue) && locationValue.ValueKind != JsonValueKind.Null ? locationValue.GetInt32() : null;
    var commission = body.TryGetProperty("commissionPercent", out var commissionValue) ? commissionValue.GetDecimal() : 5m;
    var gst = body.TryGetProperty("gstRatePercent", out var gstValue) ? gstValue.GetDecimal() : 18m;
    var (id, error) = await repository.CreateStandardAdvancedTemplateAsync(clientId, locationId, commission, gst);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("CreateClientBillingStandardTemplate")
.WithOpenApi();

app.MapGet("/api/client-billing/configurations/import-template", async (ClientBillingRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage")
        ? Results.File(await repository.BuildImportTemplateAsync(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "client-billing-import-template.xlsx")
        : Results.StatusCode(403))
.WithName("GetClientBillingImportTemplate")
.WithOpenApi();

app.MapPost("/api/client-billing/configurations/import-jobs", async (ClientBillingRepository repository, [FromForm] IFormFile file, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a client billing import file." });
    return Results.Accepted("/api/client-billing/configurations/import-jobs", await repository.StartImportJobAsync(file));
})
.WithName("StartClientBillingImportJob")
.DisableAntiforgery()
.WithOpenApi();

app.MapGet("/api/client-billing/configurations/import-jobs/{jobId:guid}", (ClientBillingRepository repository, Guid jobId, HttpContext context) =>
    HasPermission(context, "settings.manage")
        ? repository.GetImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." })
        : Results.StatusCode(403))
.WithName("GetClientBillingImportJob")
.WithOpenApi();

app.MapGet("/api/travel-expense/setup", async (TravelExpenseRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(403))
.WithName("GetTravelExpenseSetup")
.WithOpenApi();

app.MapPost("/api/travel-expense/policies", async (TravelExpenseRepository repository, TravelPolicy request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (id, error) = await repository.SavePolicyAsync(request, CurrentUser(context).Email);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("SaveTravelPolicy")
.WithOpenApi();

app.MapPost("/api/travel-expense/client-settings", async (TravelExpenseRepository repository, TravelExpenseClientSetting request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    try { return Results.Ok(await repository.SaveClientSettingAsync(request, CurrentUser(context).Email)); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
})
.WithName("SaveTravelExpenseClientSetting")
.WithOpenApi();

app.MapPost("/api/travel-expense/assignments", async (TravelExpenseRepository repository, TravelPolicyAssignment request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (id, error) = await repository.SaveAssignmentAsync(request, CurrentUser(context).Email);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("SaveTravelPolicyAssignment")
.WithOpenApi();

app.MapPost("/api/travel-expense/rules", async (TravelExpenseRepository repository, TravelPolicyRule request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    try
    {
        var (id, error) = await repository.SaveRuleAsync(request, CurrentUser(context).Email);
        return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Eligibility/config JSON is invalid." });
    }
})
.WithName("SaveTravelPolicyRule")
.WithOpenApi();

app.MapPost("/api/travel-expense/categories", async (TravelExpenseRepository repository, TravelExpenseCategory request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (id, error) = await repository.SaveCategoryAsync(request, CurrentUser(context).Email);
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(new { id }) : Results.BadRequest(new { error });
})
.WithName("SaveTravelExpenseCategory")
.WithOpenApi();

app.MapGet("/api/recruitment-admin", async (RecruitmentAdminRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(403))
.WithName("GetRecruitmentAdminSetup")
.WithOpenApi();
app.MapPost("/api/recruitment-admin/settings", async (RecruitmentAdminRepository repository, RecruitmentSetting request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    try { return Results.Ok(await repository.SaveSettingAsync(request, CurrentUser(context).Id)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(exception.Message); }
});
app.MapPost("/api/recruitment-admin/masters", async (RecruitmentAdminRepository repository, RecruitmentMasterValue request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveMasterAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/partners", async (RecruitmentAdminRepository repository, RecruitmentPartner request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SavePartnerAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/assignment-rules", async (RecruitmentAdminRepository repository, RecruitmentAssignmentRule request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveAssignmentRuleAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/sla-rules", async (RecruitmentAdminRepository repository, RecruitmentSlaRule request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveSlaRuleAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/document-checklist", async (RecruitmentAdminRepository repository, RecruitmentDocumentChecklist request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveDocumentChecklistAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/approval-mappings", async (RecruitmentAdminRepository repository, RecruitmentApprovalMapping request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveApprovalMappingAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/templates", async (RecruitmentAdminRepository repository, RecruitmentTemplate request, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveTemplateAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapGet("/api/recruitment-admin/ats-profiles", async (RecruitmentTalentRepository repository, int? clientId, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetScoringProfilesAsync(CurrentUser(context), clientId)) : Results.StatusCode(403));
app.MapGet("/api/recruitment-admin/ats-criteria", (RecruitmentTalentRepository repository, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(repository.GetScoringCriterionCatalog()) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/ats-profiles", async (RecruitmentTalentRepository repository, RecruitmentAtsScoringProfile request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveScoringProfileAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});
app.MapGet("/api/recruitment-admin/skills", async (RecruitmentTalentRepository repository, int? clientId, HttpContext context) =>
    HasPermission(context, "settings.manage") ? Results.Ok(await repository.GetSkillsAsync(CurrentUser(context), clientId)) : Results.StatusCode(403));
app.MapPost("/api/recruitment-admin/skills", async (RecruitmentTalentRepository repository, RecruitmentSkill request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(403);
    var (row, error) = await repository.SaveSkillAsync(request, CurrentUser(context));
    return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
});

app.MapGet("/api/tax-engine", async (TaxEngineRepository repository, HttpContext context) => HasPermission(context, "settings.manage") || HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/client-settings", async (TaxEngineRepository repository, ClientTaxSetting request, HttpContext context) => HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveClientSettingAsync(request)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/slabs", async (TaxEngineRepository repository, TaxSlab request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveSlabAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/surcharges", async (TaxEngineRepository repository, TaxSurcharge request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveSurchargeAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/final-adjustments", async (TaxEngineRepository repository, TaxFinalAdjustment request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveFinalAdjustmentAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/sections", async (TaxEngineRepository repository, TaxDeclarationSection request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveSectionAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/compute", async (TaxEngineRepository repository, TaxComputationRequest request, HttpContext context) => HasPermission(context, "payroll.run") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.ComputeAsync(request)) : Results.StatusCode(403));
app.MapGet("/api/tax-engine/employee-profiles/{employeeId:int}", async (TaxEngineRepository repository, int employeeId, string? financialYear, HttpContext context) => HasPermission(context, "payroll.run") || HasPermission(context, "settings.manage") ? await repository.GetEmployeeTaxProfileAsync(employeeId, financialYear ?? "") is { } profile ? Results.Ok(profile) : Results.NotFound(new { error = "Employee tax profile not found." }) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/employee-profiles", async (TaxEngineRepository repository, EmployeeTaxProfile request, HttpContext context) =>
{
    if (!(HasPermission(context, "payroll.run") || HasPermission(context, "settings.manage"))) return Results.StatusCode(403);
    if (request.EmployeeId <= 0) return Results.BadRequest(new { error = "Select employee before saving tax profile." });
    var profile = await repository.SaveEmployeeTaxProfileAsync(request);
    return profile is null ? Results.BadRequest(new { error = "Employee tax profile could not be saved. Refresh employee list and try again." }) : Results.Ok(profile);
});
app.MapPost("/api/tax-engine/employee-profiles/{employeeId:int}", async (TaxEngineRepository repository, int employeeId, EmployeeTaxProfile request, HttpContext context) =>
{
    if (!(HasPermission(context, "payroll.run") || HasPermission(context, "settings.manage"))) return Results.StatusCode(403);
    request.EmployeeId = employeeId;
    var profile = await repository.SaveEmployeeTaxProfileAsync(request);
    return profile is null ? Results.BadRequest(new { error = "Employee tax profile could not be saved. Refresh employee list and try again." }) : Results.Ok(profile);
});
app.MapDelete("/api/tax-engine/{kind}/{id:int}", async (TaxEngineRepository repository, string kind, int id, HttpContext context) => { var clientKind = kind == "client-settings"; if (!(clientKind ? HasPermission(context, "settings.manage") : HasPermission(context, "tax.statutory.manage"))) return Results.StatusCode(403); await repository.DeleteAsync(kind, id); return Results.NoContent(); });

app.MapGet("/api/leave-attendance/setup", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetAsync(clientId)))
.WithName("GetLeaveAttendanceSetup")
.WithOpenApi();

app.MapPost("/api/leave-attendance/module", async (LeaveAttendanceRepository repository, UpdateLeaveAttendanceModuleRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return request.ClientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.SetEnabledAsync(request.ClientId, request.IsEnabled));
})
.WithName("UpdateLeaveAttendanceModule")
.WithOpenApi();

app.MapPut("/api/leave-attendance/setup/{stepCode}", async (LeaveAttendanceRepository repository, string stepCode, UpdateLeaveAttendanceStepRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var setup = request.ClientId <= 0 ? null : await repository.UpdateStepAsync(request.ClientId, stepCode, request.Status);
    return setup is null ? Results.BadRequest(new { error = "Invalid setup step/status, or mandatory General Settings cannot be disabled." }) : Results.Ok(setup);
})
.WithName("UpdateLeaveAttendanceSetupStep")
.WithOpenApi();

app.MapGet("/api/leave-attendance/preferences", async (LeaveAttendanceRepository repository, int clientId, int? workLocationId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetPreferencesAsync(clientId, workLocationId)))
.WithName("GetLeaveAttendancePreferences")
.WithOpenApi();

app.MapPost("/api/leave-attendance/preferences", async (LeaveAttendanceRepository repository, SaveLeaveAttendancePreferencesRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (preferences, error) = await repository.SavePreferencesAsync(request);
    return preferences is null ? Results.BadRequest(new { error }) : Results.Ok(preferences);
})
.WithName("SaveLeaveAttendancePreferences")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance-settings", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetAttendanceSettingsAsync(clientId)))
.WithName("GetAttendanceSettings")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance-settings", async (LeaveAttendanceRepository repository, SaveAttendanceSettingsRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (settings, error) = await repository.SaveAttendanceSettingsAsync(request);
    return settings is null ? Results.BadRequest(new { error }) : Results.Ok(settings);
})
.WithName("SaveAttendanceSettings")
.WithOpenApi();

app.MapGet("/api/leave-attendance/geo-fences", async (LeaveAttendanceRepository repository, int clientId, string? scopeType) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetGeoFenceRulesAsync(clientId, scopeType)))
.WithName("GetGeoFenceRules")
.WithOpenApi();

app.MapGet("/api/leave-attendance/geo-fences/employees", async (LeaveAttendanceRepository repository, int clientId, int workLocationId) =>
    clientId <= 0 || workLocationId <= 0
        ? Results.BadRequest(new { error = "Select a client and work location." })
        : Results.Ok(await repository.GetGeoFenceEmployeesAsync(clientId, workLocationId)))
.WithName("GetGeoFenceEmployees")
.WithOpenApi();

app.MapGet("/api/leave-attendance/geo-fences/applicable", async (LeaveAttendanceRepository repository, int clientId, int employeeId, DateTime? onDate) =>
    clientId <= 0 || employeeId <= 0 ? Results.BadRequest(new { error = "Select a client and employee." }) : Results.Ok(await repository.GetApplicableGeoFenceRuleAsync(clientId, employeeId, onDate)))
.WithName("GetApplicableGeoFenceRule")
.WithOpenApi();

app.MapPost("/api/leave-attendance/geo-fences", async (LeaveAttendanceRepository repository, SaveGeoFenceRuleRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (rule, error) = await repository.SaveGeoFenceRuleAsync(request);
    return rule is null ? Results.BadRequest(new { error }) : Results.Ok(rule);
})
.WithName("SaveGeoFenceRule")
.WithOpenApi();

app.MapDelete("/api/leave-attendance/geo-fences/{id:int}", async (LeaveAttendanceRepository repository, int id, int clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return clientId > 0 && await repository.DeleteGeoFenceRuleAsync(id, clientId) ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteGeoFenceRule")
.WithOpenApi();

app.MapGet("/api/leave-attendance/groups", async (LeaveAttendanceRepository repository, int? clientId, HttpContext context) =>
    !HasAttendanceManagement(context) || !CanAccessClient(context, Math.Max(0, clientId.GetValueOrDefault()))
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(await repository.GetAttendanceGroupsAsync(Math.Max(0, clientId.GetValueOrDefault()))))
.WithName("GetAttendanceGroups")
.WithOpenApi();

app.MapPost("/api/leave-attendance/groups", async (LeaveAttendanceRepository repository, SaveAttendanceGroupRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attendance.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (group, error) = await repository.SaveAttendanceGroupAsync(request);
    return group is null ? Results.BadRequest(new { error }) : Results.Ok(group);
})
.WithName("SaveAttendanceGroup")
.WithOpenApi();

app.MapPost("/api/leave-attendance/groups/batch", async (LeaveAttendanceRepository repository, SaveAttendanceGroupBatchRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attendance.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (groups, error) = await repository.SaveAttendanceGroupBatchAsync(request);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(groups);
})
.WithName("SaveAttendanceGroupBatch")
.WithOpenApi();

app.MapDelete("/api/leave-attendance/groups/{id:int}", async (LeaveAttendanceRepository repository, int id, int clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage") && !HasPermission(context, "attendance.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return clientId > 0 && await repository.DeleteAttendanceGroupAsync(id, clientId) ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteAttendanceGroup")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/monthly", async (LeaveAttendanceRepository repository, int clientId, string month, int? workLocationId, HttpContext context) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : !HasAttendanceManagement(context) || !CanAccessClient(context, clientId) ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(await repository.GetMonthlyAttendanceAsync(clientId, month, workLocationId)))
.WithName("GetMonthlyAttendance")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/context", async (LeaveAttendanceRepository repository, int clientId, string month, int? workLocationId, HttpContext context) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : !HasAttendanceManagement(context) || !CanAccessClient(context, clientId) ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(await repository.GetAttendanceReviewContextAsync(clientId, month, workLocationId)))
.WithName("GetAttendanceReviewContext")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance/monthly", async (LeaveAttendanceRepository repository, SaveMonthlyAttendanceRequest request, HttpContext context) =>
{
    if (!HasAttendanceManagement(context) || !CanAccessClient(context, request.ClientId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (rows, error) = await repository.SaveMonthlyAttendanceAsync(request);
    return rows is null ? Results.BadRequest(new { error }) : Results.Ok(rows);
})
.WithName("SaveMonthlyAttendance")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/daily", async (LeaveAttendanceRepository repository, int clientId, int employeeId, string month) =>
    clientId <= 0 || employeeId <= 0 ? Results.BadRequest(new { error = "Select a client and employee." }) : Results.Ok(await repository.GetDailyAttendanceAsync(clientId, employeeId, month)))
.WithName("GetDailyAttendance")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/daily-grid", async (LeaveAttendanceRepository repository, int clientId, string month, int? workLocationId, HttpContext context) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : !HasAttendanceManagement(context) || !CanAccessClient(context, clientId) ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(await repository.GetDailyAttendanceMonthAsync(clientId, month, workLocationId)))
.WithName("GetDailyAttendanceGrid")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance/daily", async (LeaveAttendanceRepository repository, SaveDailyAttendanceRequest request, HttpContext context) =>
{
    if (!HasAttendanceManagement(context) || !CanAccessClient(context, request.ClientId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (rows, error) = await repository.SaveDailyAttendanceAsync(request);
    return rows is null ? Results.BadRequest(new { error }) : Results.Ok(rows);
})
.WithName("SaveDailyAttendance")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance/daily/batch", async (LeaveAttendanceRepository repository, SaveDailyAttendanceBatchRequest request, HttpContext context) =>
{
    if (!HasAttendanceManagement(context) || !CanAccessClient(context, request.ClientId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (rows, error) = await repository.SaveDailyAttendanceBatchAsync(request);
    return rows is null ? Results.BadRequest(new { error }) : Results.Ok(rows);
})
.WithName("SaveDailyAttendanceBatch")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance/daily/batch-jobs", async (LeaveAttendanceRepository repository, SaveDailyAttendanceBatchRequest request, HttpContext context) =>
{
    if (!HasAttendanceManagement(context))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var user = CurrentUser(context);
    if (!CanAccessClient(context, request.ClientId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (job, error) = await repository.StartDailyAttendanceBatchJobAsync(request, user.Email);
    return job is null
        ? Results.BadRequest(new { error })
        : Results.Accepted($"/api/leave-attendance/attendance/daily/batch-jobs/{job.JobId}", job);
})
.WithName("StartDailyAttendanceBatchJob")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/daily/batch-jobs/{jobId:guid}", async (LeaveAttendanceRepository repository, Guid jobId, HttpContext context) =>
{
    if (!HasAttendanceManagement(context))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var job = await repository.GetDailyAttendanceBatchJobAsync(jobId);
    if (job is null)
        return Results.NotFound(new { error = "Attendance batch job not found." });
    var user = CurrentUser(context);
    return user.ClientId.HasValue && user.ClientId.Value != job.ClientId
        ? Results.StatusCode(StatusCodes.Status403Forbidden)
        : Results.Ok(job);
})
.WithName("GetDailyAttendanceBatchJob")
.WithOpenApi();

app.MapGet("/api/leave-attendance/leave-types", async (LeaveAttendanceRepository repository, int clientId, HttpContext context) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : !HasAttendanceManagement(context) || !CanAccessClient(context, clientId) ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(await repository.GetLeaveTypesAsync(clientId)))
.WithName("GetLeaveTypes")
.WithOpenApi();

app.MapPost("/api/leave-attendance/leave-types", async (LeaveAttendanceRepository repository, SaveLeaveTypeRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (leaveType, error) = await repository.SaveLeaveTypeAsync(request);
    return leaveType is null ? Results.BadRequest(new { error }) : Results.Ok(leaveType);
})
.WithName("SaveLeaveType")
.WithOpenApi();

app.MapPost("/api/leave-attendance/leave-types/{id:int}/status", async (LeaveAttendanceRepository repository, int id, int clientId, bool isActive, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var leaveType = clientId <= 0 ? null : await repository.SetLeaveTypeActiveAsync(id, clientId, isActive);
    return leaveType is null ? Results.NotFound() : Results.Ok(leaveType);
})
.WithName("UpdateLeaveTypeStatus")
.WithOpenApi();

app.MapDelete("/api/leave-attendance/leave-types/{id:int}", async (LeaveAttendanceRepository repository, int id, int clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return clientId > 0 && await repository.DeleteLeaveTypeAsync(id, clientId) ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteLeaveType")
.WithOpenApi();

app.MapGet("/api/leave-attendance/leave-types/import-template", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0
        ? Results.BadRequest(new { error = "Select a client." })
        : Results.File(await repository.BuildLeaveTypeImportTemplateAsync(clientId), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "leave-type-import-template.xlsx"))
.WithName("DownloadLeaveTypeImportTemplate")
.WithOpenApi();

app.MapPost("/api/leave-attendance/leave-types/import-jobs", async (LeaveAttendanceRepository repository, [FromForm] ClientFileUploadRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (request.File is null || request.File.Length == 0) return Results.BadRequest(new { error = "Select a leave type import file." });
    return Results.Accepted("/api/leave-attendance/leave-types/import-jobs", await repository.StartLeaveTypeImportJobAsync(request.ClientId, request.File));
})
.DisableAntiforgery()
.WithName("StartLeaveTypeImportJob")
.WithOpenApi();

app.MapGet("/api/leave-attendance/leave-types/import-jobs/{jobId:guid}", (LeaveAttendanceRepository repository, Guid jobId) =>
    repository.GetLeaveTypeImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetLeaveTypeImportJob")
.WithOpenApi();

app.MapGet("/api/leave-attendance/holidays", async (LeaveAttendanceRepository repository, int clientId, int? year, int? workLocationId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetHolidaysAsync(clientId, year, workLocationId)))
.WithName("GetHolidays")
.WithOpenApi();

app.MapPost("/api/leave-attendance/holidays", async (LeaveAttendanceRepository repository, SaveHolidayRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (holiday, error) = await repository.SaveHolidayAsync(request);
    return holiday is null ? Results.BadRequest(new { error }) : Results.Ok(holiday);
})
.WithName("SaveHoliday")
.WithOpenApi();

app.MapDelete("/api/leave-attendance/holidays/{id:int}", async (LeaveAttendanceRepository repository, int id, int clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return clientId > 0 && await repository.DeleteHolidayAsync(id, clientId) ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteHoliday")
.WithOpenApi();

app.MapGet("/api/leave-attendance/holidays/import-template", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0
        ? Results.BadRequest(new { error = "Select a client." })
        : Results.File(await repository.BuildHolidayImportTemplateAsync(clientId), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "holiday-import-template.xlsx"))
.WithName("DownloadHolidayImportTemplate")
.WithOpenApi();

app.MapPost("/api/leave-attendance/holidays/import-jobs", async (LeaveAttendanceRepository repository, [FromForm] ClientFileUploadRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (request.File is null || request.File.Length == 0) return Results.BadRequest(new { error = "Select a holiday import file." });
    return Results.Accepted("/api/leave-attendance/holidays/import-jobs", await repository.StartHolidayImportJobAsync(request.ClientId, request.File));
})
.DisableAntiforgery()
.WithName("StartHolidayImportJob")
.WithOpenApi();

app.MapGet("/api/leave-attendance/holidays/import-jobs/{jobId:guid}", (LeaveAttendanceRepository repository, Guid jobId) =>
    repository.GetHolidayImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetHolidayImportJob")
.WithOpenApi();

app.MapGet("/api/leave-attendance/import-balances/sample", async (LeaveBalanceImportRepository repository, int clientId, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0)
        return Results.BadRequest(new { error = "Select a client." });
    var csv = await repository.GetSampleCsvAsync(clientId);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "leave-balance-import-sample.csv");
})
.WithName("DownloadLeaveBalanceImportSample")
.WithOpenApi();

app.MapPost("/api/leave-attendance/import-balances/preview", async (LeaveBalanceImportRepository repository, [FromForm] LeaveBalancePreviewUploadRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId <= 0)
        return Results.BadRequest(new { error = "Select a client." });
    if (request.File is null || request.File.Length == 0)
        return Results.BadRequest(new { error = "Select a CSV, XLS or XLSX file." });
    var preview = await repository.PreviewAsync(request.ClientId, request.File, request.Encoding, request.MappingJson);
    return Results.Ok(preview);
})
.DisableAntiforgery()
.WithName("PreviewLeaveBalanceImport")
.ExcludeFromDescription();

app.MapPost("/api/leave-attendance/import-balances/finalize", async (LeaveBalanceImportRepository repository, FinalizeLeaveBalanceImportRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId <= 0)
        return Results.BadRequest(new { error = "Select a client." });
    var result = await repository.ImportAsync(request, CurrentUser(context).Email);
    return Results.Ok(result);
})
.WithName("FinalizeLeaveBalanceImport")
.WithOpenApi();

app.MapGet("/api/clients", async (OrganizationRepository repository) =>
    Results.Ok(await repository.GetClientsAsync()))
.WithName("GetClients")
.WithOpenApi();

app.MapPost("/api/clients", async (OrganizationRepository repository, Client client) =>
{
    if (string.IsNullOrWhiteSpace(client.Name))
        return Results.BadRequest(new { error = "Client name is required." });
    client.Name = client.Name.Trim();
    var id = await repository.SaveClientAsync(client);
    return Results.Ok(new { id });
})
.WithName("SaveClient")
.WithOpenApi();

app.MapGet("/api/clients/import-template", async (OrganizationRepository repository) =>
    Results.File(await repository.BuildClientImportTemplateAsync(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "client-import-template.xlsx"))
.WithName("DownloadClientImportTemplate")
.WithOpenApi();

app.MapPost("/api/clients/import-jobs", async (OrganizationRepository repository, [FromForm] IFormFile file) =>
{
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a client import file." });
    return Results.Accepted("/api/clients/import-jobs", await repository.StartClientImportJobAsync(file));
})
.DisableAntiforgery()
.WithName("StartClientImportJob")
.WithOpenApi();

app.MapGet("/api/clients/import-jobs/{jobId:guid}", (OrganizationRepository repository, Guid jobId) =>
    repository.GetClientImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetClientImportJob")
.WithOpenApi();

app.MapGet("/api/work-locations", async (OrganizationRepository repository) =>
    Results.Ok(await repository.GetWorkLocationsAsync()))
.WithName("GetWorkLocations")
.WithOpenApi();

app.MapPost("/api/work-locations", async (OrganizationRepository repository, WorkLocation location) =>
{
    if (string.IsNullOrWhiteSpace(location.Name))
        return Results.BadRequest(new { error = "Work location name is required." });
    if (location.ClientId <= 0)
        return Results.BadRequest(new { error = "Client is required for work location." });
    if (!string.IsNullOrWhiteSpace(location.PostalCode) && !System.Text.RegularExpressions.Regex.IsMatch(location.PostalCode, @"^[1-9][0-9]{5}$"))
        return Results.BadRequest(new { error = "Enter a valid 6-digit PIN code." });
    var id = await repository.SaveWorkLocationAsync(location);
    return Results.Ok(new { id });
})
.WithName("SaveWorkLocation")
.WithOpenApi();

app.MapGet("/api/work-locations/import-template", async (OrganizationRepository repository) =>
    Results.File(await repository.BuildWorkLocationImportTemplateAsync(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "work-location-import-template.xlsx"))
.WithName("DownloadWorkLocationImportTemplate")
.WithOpenApi();

app.MapPost("/api/work-locations/import-jobs", async (OrganizationRepository repository, [FromForm] IFormFile file) =>
{
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a work-location import file." });
    return Results.Accepted("/api/work-locations/import-jobs", await repository.StartWorkLocationImportJobAsync(file));
})
.DisableAntiforgery()
.WithName("StartWorkLocationImportJob")
.WithOpenApi();

app.MapGet("/api/work-locations/import-jobs/{jobId:guid}", (OrganizationRepository repository, Guid jobId) =>
    repository.GetWorkLocationImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetWorkLocationImportJob")
.WithOpenApi();

app.MapGet("/api/dropdowns", async (OrganizationRepository repository) =>
    Results.Ok(await repository.GetDropdownMastersAsync()))
.WithName("GetDropdownMasters")
.WithOpenApi();

app.MapPost("/api/dropdowns", async (OrganizationRepository repository, DropdownMaster item) =>
{
    if (string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Value))
        return Results.BadRequest(new { error = "Dropdown type and value are required." });
    item.Type = item.Type.Trim();
    item.Value = item.Value.Trim();
    var id = await repository.SaveDropdownMasterAsync(item);
    return Results.Ok(new { id });
})
.WithName("SaveDropdownMaster")
.WithOpenApi();

app.MapGet("/api/dropdowns/import-template", async (OrganizationRepository repository) =>
    Results.File(await repository.BuildDropdownImportTemplateAsync(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "dropdown-master-import-template.xlsx"))
.WithName("DownloadDropdownImportTemplate")
.WithOpenApi();

app.MapPost("/api/dropdowns/import-jobs", async (OrganizationRepository repository, [FromForm] IFormFile file) =>
{
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a dropdown import file." });
    return Results.Accepted("/api/dropdowns/import-jobs", await repository.StartDropdownImportJobAsync(file));
})
.DisableAntiforgery()
.WithName("StartDropdownImportJob")
.WithOpenApi();

app.MapGet("/api/dropdowns/import-jobs/{jobId:guid}", (OrganizationRepository repository, Guid jobId) =>
    repository.GetDropdownImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetDropdownImportJob")
.WithOpenApi();

app.MapGet("/api/salary-components/import-template", async (OrganizationRepository repository) =>
    Results.File(await repository.BuildSalaryComponentImportTemplateAsync(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "salary-component-import-template.xlsx"))
.WithName("DownloadSalaryComponentImportTemplate")
.WithOpenApi();

app.MapPost("/api/salary-components/import-jobs", async (OrganizationRepository repository, [FromForm] IFormFile file) =>
{
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a salary component import file." });
    return Results.Accepted("/api/salary-components/import-jobs", await repository.StartSalaryComponentImportJobAsync(file));
})
.DisableAntiforgery()
.WithName("StartSalaryComponentImportJob")
.WithOpenApi();

app.MapGet("/api/salary-components/import-jobs/{jobId:guid}", (OrganizationRepository repository, Guid jobId) =>
    repository.GetSalaryComponentImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetSalaryComponentImportJob")
.WithOpenApi();

app.MapGet("/api/salary-templates/import-template", async (OrganizationRepository repository) =>
    Results.File(await repository.BuildSalaryTemplateImportTemplateAsync(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "salary-template-import-template.xlsx"))
.WithName("DownloadSalaryTemplateImportTemplate")
.WithOpenApi();

app.MapPost("/api/salary-templates/import-jobs", async (OrganizationRepository repository, [FromForm] IFormFile file) =>
{
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a salary template import file." });
    return Results.Accepted("/api/salary-templates/import-jobs", await repository.StartSalaryTemplateImportJobAsync(file));
})
.DisableAntiforgery()
.WithName("StartSalaryTemplateImportJob")
.WithOpenApi();

app.MapGet("/api/salary-templates/import-jobs/{jobId:guid}", (OrganizationRepository repository, Guid jobId) =>
    repository.GetSalaryTemplateImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetSalaryTemplateImportJob")
.WithOpenApi();

app.MapGet("/api/employees", async (EmployeeRepository repository) =>
    Results.Ok(await repository.GetAsync()))
.WithName("GetEmployees")
.WithOpenApi();

app.MapGet("/api/employees/manager-users", async (EmployeeRepository repository) =>
    Results.Ok(await repository.GetManagerUsersAsync()))
.WithName("GetEmployeeManagerUsers")
.WithOpenApi();

app.MapPost("/api/employees", async (EmployeeRepository repository, Employee employee, HttpContext context, string? infotypeCode, string? changeReason) =>
{
    if (employee.ClientId == 0 || string.IsNullOrWhiteSpace(employee.EmployeeCode) || string.IsNullOrWhiteSpace(employee.FirstName))
        return Results.BadRequest(new { error = "Client, employee code and first name are required." });
    employee.SalaryJson = string.IsNullOrWhiteSpace(employee.SalaryJson) ? "{}" : employee.SalaryJson;
    employee.PersonalJson = string.IsNullOrWhiteSpace(employee.PersonalJson) ? "{}" : employee.PersonalJson;
    employee.PaymentJson = string.IsNullOrWhiteSpace(employee.PaymentJson) ? "{}" : employee.PaymentJson;
    var id = await repository.SaveAsync(employee, CurrentUser(context).Email, infotypeCode, changeReason);
    return Results.Ok(new { id });
})
.WithName("SaveEmployee")
.WithOpenApi();

app.MapGet("/api/employees/{id:int}/delete-preview", async (EmployeeRepository repository, int id) =>
    await repository.GetDeletePreviewAsync(id) is { } preview ? Results.Ok(preview) : Results.NotFound(new { error = "Employee not found." }))
.WithName("GetEmployeeDeletePreview")
.WithOpenApi();

app.MapGet("/api/employees/{id:int}/infotypes", async (EmployeeRepository repository, int id, bool activeOnly) =>
    Results.Ok(await repository.GetInfotypesAsync(id, activeOnly)))
.WithName("GetEmployeeInfotypes")
.WithOpenApi();

app.MapGet("/api/employees/{id:int}/audit", async (EmployeeRepository repository, int id) =>
    Results.Ok(await repository.GetAuditTrailAsync(id)))
.WithName("GetEmployeeAuditTrail")
.WithOpenApi();

app.MapGet("/api/employees/infotypes/active", async (EmployeeRepository repository, int clientId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetActiveInfotypesAsync(clientId)))
.WithName("GetActiveEmployeeInfotypes")
.WithOpenApi();

app.MapPost("/api/employees/actions", async (EmployeeRepository repository, EmployeeActionRequest request, HttpContext context) =>
{
    var (employee, error) = await repository.ProcessActionAsync(request, CurrentUser(context).Email);
    return employee is null ? Results.BadRequest(new { error }) : Results.Ok(employee);
})
.WithName("ProcessEmployeeAction")
.WithOpenApi();

app.MapDelete("/api/employees/{id:int}", async (EmployeeRepository repository, int id) =>
{
    var (ok, error) = await repository.DeleteAsync(id);
    return ok ? Results.NoContent() : Results.BadRequest(new { error });
})
.WithName("DeleteEmployee")
.WithOpenApi();

app.MapGet("/api/employees/{id:int}/dynamic-fields", async (EmployeeAttributeRepository repository, int id, int clientId, string? infotypeCode, DateTime? asOfUtc, HttpContext context) =>
{
    var (item, error) = await repository.GetEmployeeFieldsAsync(id, clientId, infotypeCode, CurrentUser(context), asOfUtc);
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("GetEmployeeDynamicFields")
.WithOpenApi();

app.MapPost("/api/employees/{id:int}/dynamic-fields", async (EmployeeAttributeRepository repository, int id, SaveEmployeeAttributeValuesRequest request, HttpContext context) =>
{
    var (item, error) = await repository.SaveEffectiveRevisionAsync(id, request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveEmployeeDynamicFields")
.WithOpenApi();

app.MapGet("/api/employees/{id:int}/dynamic-fields/{fieldId:long}/lookup", async (EmployeeAttributeRepository repository, int id, long fieldId, int clientId, string? search, HttpContext context) =>
{
    var (items, error) = await repository.ResolveLookupAsync(id, clientId, fieldId, search ?? "", CurrentUser(context));
    return string.IsNullOrWhiteSpace(error) ? Results.Ok(items) : Results.BadRequest(new { error });
})
.WithName("ResolveEmployeeDynamicFieldLookup")
.WithOpenApi();

app.MapGet("/api/employee-form-bindings", async (EmployeeAttributeRepository repository, int? clientId, HttpContext context) =>
    Results.Ok(await repository.ListBindingsAsync(clientId, CurrentUser(context))))
.WithName("GetEmployeeFormBindings")
.WithOpenApi();

app.MapPost("/api/employee-form-bindings", async (EmployeeAttributeRepository repository, SaveEmployeeFormBinding request, HttpContext context) =>
{
    var (item, error) = await repository.SaveBindingAsync(request, CurrentUser(context));
    return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
})
.WithName("SaveEmployeeFormBinding")
.WithOpenApi();

app.MapGet("/api/employees/import-template", async (OrganizationRepository organizationRepository, AuthRepository authRepository, EmployeeRepository repository, int clientId) =>
{
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    await organizationRepository.InitializeAsync();
    await authRepository.InitializeAsync();
    await repository.InitializeAsync();
    return Results.File(await repository.BuildImportTemplateAsync(clientId), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "employee-import-template.xlsx");
})
.WithName("DownloadEmployeeImportTemplate")
.WithOpenApi();

app.MapPost("/api/employees/import-preflight", async (EmployeeRepository repository, [FromForm] ClientFileUploadRequest request) =>
{
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (request.File is null || request.File.Length == 0) return Results.BadRequest(new { error = "Select an employee CSV or Excel file." });
    var result = await repository.PreflightImportCsvAsync(request.ClientId, request.File, request.Mode);
    return result.CanImport ? Results.Ok(result) : Results.UnprocessableEntity(result);
})
.DisableAntiforgery()
.WithName("PreflightEmployeeImport")
.WithOpenApi();

app.MapPost("/api/employees/import", async (EmployeeRepository repository, [FromForm] ClientFileUploadRequest request) =>
{
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (request.File is null || request.File.Length == 0)
        return Results.BadRequest(new { error = "Select an employee CSV or Excel file." });
    var result = await repository.ImportCsvAsync(request.ClientId, request.File, request.Mode, request.ReviewToken, request.DecisionsJson);
    return result.RequiresConfirmation
        ? Results.Conflict(result)
        : result.Errors.Count > 0 ? Results.BadRequest(result) : Results.Ok(result);
})
.DisableAntiforgery()
.WithName("ImportEmployees")
.WithOpenApi();

app.MapPost("/api/employees/import-jobs", async (EmployeeRepository repository, [FromForm] ClientFileUploadRequest request) =>
{
    if (request.ClientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (request.File is null || request.File.Length == 0)
        return Results.BadRequest(new { error = "Select an employee CSV or Excel file." });
    return Results.Accepted($"/api/employees/import-jobs", await repository.StartImportCsvJobAsync(request.ClientId, request.File, request.Mode, request.ReviewToken, request.DecisionsJson));
})
.DisableAntiforgery()
.WithName("StartEmployeeImportJob")
.WithOpenApi();

app.MapGet("/api/employees/import-jobs/{jobId:guid}", (EmployeeRepository repository, Guid jobId) =>
    repository.GetImportJob(jobId) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Import job not found." }))
.WithName("GetEmployeeImportJob")
.WithOpenApi();

app.MapGet("/api/pay-runs", async (PayRunRepository repository) =>
    Results.Ok(await repository.GetAllAsync()))
.WithName("GetPayRuns")
.WithOpenApi();

app.MapGet("/api/pay-runs/{id:int}", async (PayRunRepository repository, int id) =>
{
    var payRun = await repository.GetAsync(id);
    return payRun is null ? Results.NotFound() : Results.Ok(payRun);
})
.WithName("GetPayRun")
.WithOpenApi();

app.MapGet("/api/pay-runs/{id:int}/diagnostics", async (PayRunRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run") && !HasPermission(context, "payroll.approve"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var diagnostics = await repository.GetDiagnosticsAsync(id);
    return diagnostics is null ? Results.NotFound() : Results.Ok(diagnostics);
})
.WithName("GetPayRunDiagnostics")
.WithOpenApi();

app.MapPost("/api/pay-runs", async (PayRunRepository repository, CreatePayRunRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId == 0 || !System.Text.RegularExpressions.Regex.IsMatch(request.PayPeriod ?? "", @"^\d{4}-(0[1-9]|1[0-2])$") || request.TotalWorkingDays is < 1 or > 31)
        return Results.BadRequest(new { error = "Select a client and enter a valid pay period with 1 to 31 working days." });
    if (string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) && request.IncludedEmployeeIds.Count == 0 && request.AdjustmentIds.Count == 0)
        return Results.BadRequest(new { error = "Off-cycle payroll needs at least one employee or approved adjustment." });
    try
    {
        var payRun = await repository.QueueAsync(request, CurrentUser(context).Email);
        return payRun is null ? Results.Conflict(new { error = "An approved or pending payroll already exists for this period." }) : Results.Created($"/api/pay-runs/{payRun.Id}", payRun);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        try
        {
            var failedRun = await repository.CreateFailedAttemptAsync(request, CurrentUser(context).Email, exception);
            return failedRun is null ? Results.BadRequest(new { error = exception.Message }) : Results.Created($"/api/pay-runs/{failedRun.Id}", failedRun);
        }
        catch (Exception diagnosticException)
        {
            return Results.BadRequest(new { error = exception.Message, diagnosticError = diagnosticException.Message });
        }
    }
})
.WithName("CreatePayRun")
.WithOpenApi();

app.MapGet("/api/payroll-adjustments", async (PayRunRepository repository, int? clientId, string? payPeriod, string? status) =>
    Results.Ok(await repository.GetAdjustmentsAsync(clientId, payPeriod, status)))
.WithName("GetPayrollAdjustments")
.WithOpenApi();

app.MapPost("/api/payroll-adjustments", async (PayRunRepository repository, PayrollAdjustment adjustment, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (adjustment.ClientId == 0 || adjustment.EmployeeId == 0 || adjustment.Amount <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(adjustment.PayPeriod ?? "", @"^\d{4}-(0[1-9]|1[0-2])$"))
        return Results.BadRequest(new { error = "Client, employee, pay period and positive amount are required." });
    var saved = await repository.SaveAdjustmentAsync(adjustment);
    return saved is null ? Results.BadRequest(new { error = "Adjustment could not be saved or has already been applied." }) : Results.Ok(saved);
})
.WithName("SavePayrollAdjustment")
.WithOpenApi();

app.MapDelete("/api/payroll-adjustments/{id:int}", async (PayRunRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return await repository.CancelAdjustmentAsync(id) ? Results.NoContent() : Results.BadRequest(new { error = "Applied adjustments cannot be cancelled." });
})
.WithName("CancelPayrollAdjustment")
.WithOpenApi();

app.MapPut("/api/pay-runs/{payRunId:int}/employees/{employeeId:int}", async (PayRunRepository repository, int payRunId, int employeeId, UpdatePayRunEmployeeRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var employee = await repository.UpdateEmployeeAsync(payRunId, employeeId, request);
    return employee is null ? Results.BadRequest(new { error = "Only draft pay runs can be updated." }) : Results.Ok(employee);
})
.WithName("UpdatePayRunEmployee")
.WithOpenApi();

app.MapPost("/api/pay-runs/{id:int}/submit", async (PayRunRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var existing = await repository.GetAsync(id);
    if (existing is null) return Results.NotFound(new { error = "Pay run not found." });
    var payRun = await repository.SubmitForApprovalAsync(id);
    return payRun is null ? Results.BadRequest(new { error = "Only draft pay runs can be locked and sent for approval." }) : Results.Ok(payRun);
})
.WithName("SubmitPayRunForApproval")
.WithOpenApi();

app.MapPost("/api/pay-runs/{id:int}/approve", async (PayRunRepository repository, WorkflowRepository workflows, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.approve"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var existing = await repository.GetAsync(id);
    if (existing is null) return Results.NotFound(new { error = "Pay run not found." });
    var workflowId = await workflows.GetDefaultIdForActivityAsync("PAYRUN.SUBMIT", existing.ClientId);
    var state = await workflows.GetResourceStateAsync("PayRun", id.ToString());
    if (workflowId is not null && existing.Status == "Pending Approval" && state?.CurrentState == "Pending")
        return Results.BadRequest(new { error = "This payroll is under workflow approval. Approve it from My Tasks." });
    var payRun = await repository.ApproveAsync(id);
    return payRun is null ? Results.BadRequest(new { error = "Only draft or pending approval pay runs can be approved." }) : Results.Ok(payRun);
})
.WithName("ApprovePayRun")
.WithOpenApi();

app.MapDelete("/api/pay-runs/{id:int}", async (PayRunRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return await repository.DeleteAsync(id) ? Results.NoContent() : Results.BadRequest(new { error = "Paid or partially paid pay runs cannot be hard deleted." });
})
.WithName("DeleteDraftPayRun")
.WithOpenApi();

app.MapPost("/api/pay-runs/{id:int}/recall", async (PayRunRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.approve"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var payRun = await repository.RecallAsync(id);
    return payRun is null ? Results.BadRequest(new { error = "Only unpaid approved pay runs can be recalled." }) : Results.Ok(payRun);
})
.WithName("RecallPayRun")
.WithOpenApi();

app.MapPost("/api/pay-runs/{id:int}/payments", async (PayRunRepository repository, int id, RecordPaymentRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.payments"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var payRun = await repository.RecordPaymentsAsync(id, request);
    return payRun is null ? Results.BadRequest(new { error = "Payments can only be recorded for approved, unpaid employees." }) : Results.Ok(payRun);
})
.WithName("RecordPayRunPayments")
.WithOpenApi();

app.MapGet("/api/pay-runs/{id:int}/export", async (PayRunRepository repository, int id) =>
{
    var payRun = await repository.GetAsync(id);
    if (payRun is null) return Results.NotFound();
    var rows = new List<string> { "Client,Pay Period,Run Code,Run Type,Run Name,Employee Code,Employee,Department,Present Days,Payable Days,Gross Pay,Statutory Deductions,One-Time Earnings,One-Time Deductions,Manual TDS,Total Deductions,Net Pay,Payment Status" };
    rows.AddRange(payRun.Employees.Where(employee => !employee.IsSkipped).Select(employee =>
    {
        var totalDeductions = employee.StatutoryDeductions + employee.OneTimeDeductions + employee.ManualTds;
        return string.Join(",", [
            Csv(payRun.ClientName),
            Csv(payRun.PayPeriod),
            Csv(payRun.RunCode),
            Csv(payRun.RunType),
            Csv(payRun.RunName),
            Csv(employee.EmployeeCode),
            Csv(employee.EmployeeName),
            Csv(employee.Department),
            Csv(employee.PresentDays),
            Csv(employee.PayableDays),
            Csv(employee.GrossPay),
            Csv(employee.StatutoryDeductions),
            Csv(employee.OneTimeEarnings),
            Csv(employee.OneTimeDeductions),
            Csv(employee.ManualTds),
            Csv(totalDeductions),
            Csv(employee.NetPay),
            Csv(employee.PaymentStatus)
        ]);
    }));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, rows)), "text/csv", $"pay-register-{payRun.PayPeriod}.csv");
})
.WithName("ExportPayRun")
.WithOpenApi();

static GoogleDriveOAuthSetup BuildGoogleDriveOAuthSetup(
    GoogleDriveOAuthService googleDrive,
    AttachmentStorageServer? server,
    string apiBaseUri)
{
    var oauthConfigured = server?.GoogleOAuthConfigured ?? googleDrive.HasOAuthClientConfiguration(null);
    var connectionStatus = server?.GoogleConnectionStatus;
    if (string.IsNullOrWhiteSpace(connectionStatus))
        connectionStatus = googleDrive.ConnectionStatus(null);
    return new GoogleDriveOAuthSetup
    {
        StorageServerId = server?.Id ?? 0,
        GoogleOAuthConfigured = oauthConfigured,
        ConnectionStatus = connectionStatus,
        CallbackUrl = googleDrive.ResolveRedirectUri(apiBaseUri),
        GoogleCloudCredentialsUrl = "https://console.cloud.google.com/apis/credentials"
    };
}

static IResult GoogleDrivePopupResult(
    HttpContext context,
    string portalOrigin,
    bool success,
    string message,
    long? storageServerId)
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';";
    var payload = JsonSerializer.Serialize(new
    {
        type = GoogleDriveOAuthService.PopupMessageType,
        success,
        message,
        storageServerId
    });
    var postMessageScript = string.IsNullOrWhiteSpace(portalOrigin)
        ? "setTimeout(function(){ window.close(); }, 2500);"
        : $"if (window.opener && !window.opener.closed) window.opener.postMessage({payload}, {JsonSerializer.Serialize(portalOrigin)}); setTimeout(function(){{ window.close(); }}, 250);";
    var safeMessage = System.Net.WebUtility.HtmlEncode(message);
    var safeHeading = success ? "Google Drive connected" : "Google Drive connection failed";
    var html = $@"<!doctype html>
<html lang=""en"">
<head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>{safeHeading}</title>
<style>body{{font-family:system-ui,sans-serif;margin:0;display:grid;min-height:100vh;place-items:center;background:#f7f9fc;color:#172033}}main{{max-width:560px;padding:32px;text-align:center}}h1{{font-size:22px}}p{{line-height:1.5;color:#4d5b73}}</style></head>
<body><main><h1>{safeHeading}</h1><p>{safeMessage}</p></main><script>{postMessageScript}</script></body>
</html>";
    return Results.Content(html, "text/html; charset=utf-8");
}

static string Csv(object? value)
{
    var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
}

static AuthUser CurrentUser(HttpContext context) =>
    context.Items.TryGetValue("User", out var user) && user is AuthUser authUser
        ? authUser
        : new AuthUser();

static bool HasPermission(HttpContext context, string permission) =>
    CurrentUser(context).Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

static bool HasRecruitmentManagement(HttpContext context) =>
    HasPermission(context, "recruitment.manage") || HasPermission(context, "settings.manage");

static bool HasAttendanceManagement(HttpContext context) =>
    HasPermission(context, "attendance.manage") || HasPermission(context, "mss.attendance.manage") || HasPermission(context, "settings.manage");

static bool CanManageFrevoPilotChats(HttpContext context)
{
    var user = CurrentUser(context);
    return user.ClientId is null && user.Permissions.Contains("security.manage", StringComparer.OrdinalIgnoreCase);
}

static bool CanAccessClient(HttpContext context, int clientId)
{
    var user = CurrentUser(context);
    return !user.ClientId.HasValue || user.ClientId.Value == clientId;
}

static bool IsEssAllowedApi(PathString path)
{
    if (path.StartsWithSegments("/api/ess")) return true;
    if (path.StartsWithSegments("/api/attachments")) return true;
    if (path.StartsWithSegments("/api/attachment-configurations/effective")) return true;
    if (path.StartsWithSegments("/api/auth/me")) return true;
    if (path.StartsWithSegments("/api/auth/logout")) return true;
    if (path.StartsWithSegments("/api/auth/change-password")) return true;
    if (path.StartsWithSegments("/api/workflows/tasks")) return true;
    return false;
}

static string ReadAuthToken(HttpContext context, string cookieName)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return authorization["Bearer ".Length..].Trim();
    return context.Request.Cookies.TryGetValue(cookieName, out var token) ? token : string.Empty;
}

static void WriteAuthCookie(HttpContext context, string cookieName, string token, DateTime expiresAt)
{
    context.Response.Cookies.Append(cookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
        Path = "/"
    });
}

static void ClearAuthCookie(HttpContext context, string cookieName)
{
    context.Response.Cookies.Delete(cookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/"
    });
}

static async Task RunDatabaseSetupAsync(IServiceProvider services, IConfiguration configuration)
{
    using var scope = services.CreateScope();
    var scopedServices = scope.ServiceProvider;

    await scopedServices.GetRequiredService<OrganizationRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<ClientBillingRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<EmployeeRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<PayRunRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<AuthRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<LeaveAttendanceRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<WorkflowRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<TaxEngineRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<AttachmentRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<NotificationRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<CommunicationRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<ScheduledJobRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<TravelExpenseRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentAdminRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentTalentRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentPipelineRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentFormRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<EmployeeAttributeRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentCandidateActionRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<RecruitmentCaseRepository>().InitializeAsync();

    await using var workflowDb = new MySqlConnector.MySqlConnection(configuration.GetConnectionString("Default"));
    await workflowDb.OpenAsync();
    await workflowDb.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS essleaverequests (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    LeaveTypeId INT NOT NULL,
    FromDate DATE NOT NULL,
    ToDate DATE NOT NULL,
    DayType VARCHAR(30) NOT NULL DEFAULT 'Full Day',
    Days DECIMAL(8,2) NOT NULL,
    Reason VARCHAR(1000),
    Status VARCHAR(40) NOT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);");

    var essRepository = scopedServices.GetRequiredService<EssMssRepository>();
    await essRepository.InitializeAsync();
    await essRepository.ReconcileLeaveWorkflowStatusesAsync();
}

app.Run();
