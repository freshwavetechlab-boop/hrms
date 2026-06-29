using Payroll.API.Models;
using Payroll.API.Repositories;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Dapper;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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
builder.Services.AddSingleton<EmployeeRepository>();
builder.Services.AddSingleton<PayRunRepository>();
builder.Services.AddSingleton<AuthRepository>();
builder.Services.AddSingleton<LeaveAttendanceRepository>();
builder.Services.AddSingleton<LeaveBalanceImportRepository>();
builder.Services.AddSingleton<ReportingRepository>();
builder.Services.AddSingleton<EssMssRepository>();
builder.Services.AddSingleton<WorkflowRepository>();
builder.Services.AddSingleton<TaxEngineRepository>();
builder.Services.AddSingleton<DashboardRepository>();

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

app.UseHttpsRedirection();
app.UseCors();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method) || !context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/api/auth/login"))
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

app.MapGet("/api/dashboard", async (DashboardRepository repository, int? clientId, HttpContext context) =>
{
    var user = CurrentUser(context);
    var effectiveClientId = user.ClientId ?? Math.Max(clientId.GetValueOrDefault(), 0);
    return Results.Ok(await repository.GetAsync(effectiveClientId, user));
})
.WithName("GetDashboard")
.WithOpenApi();

app.MapGet("/api/workflows", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/approvers", async (WorkflowRepository repository, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetApproversAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/departments", async (WorkflowRepository repository, int clientId, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetDepartmentsAsync(clientId)) : Results.StatusCode(403));
app.MapGet("/api/workflows/department-heads", async (WorkflowRepository repository, int clientId, HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetDepartmentHeadsAsync(clientId)) : Results.StatusCode(403));
app.MapPost("/api/workflows/department-heads", async (WorkflowRepository repository, SaveDepartmentHeadAssignmentRequest request, HttpContext context) => { if(!HasPermission(context,"workflow.manage")) return Results.StatusCode(403); if(request.ClientId<=0||string.IsNullOrWhiteSpace(request.Department)||request.UserId<=0)return Results.BadRequest(new{error="Client, department, and assigned user are required."}); return Results.Ok(await repository.SaveDepartmentHeadAsync(request)); });
app.MapPost("/api/workflows", async (WorkflowRepository repository, SaveWorkflowRequest request, HttpContext context) => { if(!HasPermission(context,"workflow.manage")) return Results.StatusCode(403); return Results.Ok(await repository.SaveAsync(request)); });
app.MapPost("/api/workflows/start", async (WorkflowRepository repository, StartWorkflowRequest request, HttpContext context) => { var item=await repository.StartAsync(request,CurrentUser(context).Id); return item is null ? Results.BadRequest(new {error="Workflow cannot start. Check stages and approver setup."}) : Results.Ok(item); });
app.MapGet("/api/workflows/tasks/pending", async (WorkflowRepository repository,HttpContext context) => Results.Ok(await repository.PendingAsync(CurrentUser(context).Id)));
app.MapGet("/api/workflows/history", async (WorkflowRepository repository,HttpContext context) => HasPermission(context,"workflow.manage") ? Results.Ok(await repository.GetInstancesAsync()) : Results.StatusCode(403));
app.MapGet("/api/workflows/{instanceId:long}/history", async (WorkflowRepository repository,long instanceId,HttpContext context) => Results.Ok(await repository.HistoryAsync(instanceId)));
app.MapPost("/api/workflows/tasks/{taskId:long}/{action}", async (WorkflowRepository repository, EssMssRepository essRepository,long taskId,string action,WorkflowActionRequest request,HttpContext context) => { if(action is not ("Approved" or "Rejected" or "Sent Back")) return Results.BadRequest(); var task=await repository.ActionAsync(taskId,CurrentUser(context).Id,action,request.Comment); if(!task)return Results.NotFound(); var instance=await repository.GetInstanceForTaskAsync(taskId); if(instance?.ResourceType=="LeaveRequest")await essRepository.SyncLeaveWorkflowStatusAsync(instance.ResourceId,instance.Status); return Results.NoContent(); });

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

app.MapGet("/api/ess/leave/requests", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetLeaveRequestsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/leave/requests/{id:long}/trail", async (EssMssRepository repository, long id, HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var trail=await repository.GetLeaveRequestTrailAsync(id,user.EmployeeId.Value,user.ClientId); return trail is null ? Results.NotFound() : Results.Ok(trail); });
app.MapGet("/api/ess/pay/payslips", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetPayslipsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/tax", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetTaxPortalAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapPost("/api/ess/tax/regime", async (EssMssRepository repository, SaveEssTaxRegimeRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(ok,error)=await repository.SaveTaxRegimeAsync(user.EmployeeId.Value,user.ClientId,request); return ok ? Results.NoContent() : Results.BadRequest(new{error}); });
app.MapPost("/api/ess/tax/declarations", async (EssMssRepository repository, SaveEssTaxDeclarationsRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(ok,error)=await repository.SaveTaxDeclarationsAsync(user.EmployeeId.Value,user.ClientId,request); return ok ? Results.NoContent() : Results.BadRequest(new{error}); });
app.MapGet("/api/ess/dashboard/attendance", async (EssMssRepository repository, string month, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetAttendanceSummaryAsync(user.EmployeeId.Value,user.ClientId,month)); });
app.MapGet("/api/ess/dashboard/attendance/daily", async (EssMssRepository repository, string month, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetDailyAttendanceAsync(user.EmployeeId.Value,user.ClientId,month)); });
app.MapPost("/api/ess/attendance/punch/validate", async (EssMssRepository repository, ValidateAttendancePunchRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); return Results.Ok(await repository.ValidateAttendancePunchAsync(user.EmployeeId.Value,user.ClientId,request)); });
app.MapPost("/api/ess/attendance/punch", async (EssMssRepository repository, ValidateAttendancePunchRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var result=await repository.RecordAttendancePunchAsync(user.EmployeeId.Value,user.ClientId,request); return result.PunchRecorded ? Results.Created($"/api/ess/attendance/punch/{result.PunchId}",result) : Results.BadRequest(result); });
app.MapGet("/api/ess/dashboard/holidays", async (EssMssRepository repository, string month, HttpContext context) => Results.Ok(await repository.GetHolidaysAsync(CurrentUser(context).ClientId,month)));
app.MapGet("/api/ess/dashboard/birthdays", async (EssMssRepository repository, HttpContext context) => Results.Ok(await repository.GetTodaysBirthdaysAsync(CurrentUser(context).ClientId)));
app.MapPost("/api/ess/leave/requests", async (EssMssRepository repository, WorkflowRepository workflows, CreateEssLeaveRequest request, HttpContext context) => { var user=CurrentUser(context); if(!user.Permissions.Contains("ess.self",StringComparer.OrdinalIgnoreCase)||user.EmployeeId is null)return Results.StatusCode(403); var(result,error)=await repository.CreateLeaveRequestAsync(user.EmployeeId.Value,user.ClientId,request); if(result is null)return Results.BadRequest(new{error}); var workflowId=await workflows.GetDefaultIdAsync("LeaveRequest",user.ClientId); if(workflowId is not null) await workflows.StartAsync(new StartWorkflowRequest{WorkflowId=workflowId.Value,ResourceType="LeaveRequest",ResourceId=result.Id.ToString(),PayloadJson=System.Text.Json.JsonSerializer.Serialize(result)},user.Id); return Results.Created($"/api/ess/leave/requests/{result.Id}",result); });

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

app.MapGet("/api/reports/{code}", async (ReportingRepository repository, string code, int clientId, string? department, int? workLocationId, string? fromDate, string? toDate, string? month, HttpContext context) =>
{
    if (!HasPermission(context, "reports.view")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    return Results.Ok(await repository.RunAsync(code, new ReportFilter { ClientId = clientId, Department = department, WorkLocationId = workLocationId, FromDate = fromDate, ToDate = toDate, Month = month }));
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

app.MapGet("/api/tax-engine", async (TaxEngineRepository repository, HttpContext context) => HasPermission(context, "settings.manage") || HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.GetAsync()) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/client-settings", async (TaxEngineRepository repository, ClientTaxSetting request, HttpContext context) => HasPermission(context, "settings.manage") ? Results.Ok(await repository.SaveClientSettingAsync(request)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/slabs", async (TaxEngineRepository repository, TaxSlab request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveSlabAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/surcharges", async (TaxEngineRepository repository, TaxSurcharge request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveSurchargeAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/final-adjustments", async (TaxEngineRepository repository, TaxFinalAdjustment request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveFinalAdjustmentAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/sections", async (TaxEngineRepository repository, TaxDeclarationSection request, HttpContext context) => HasPermission(context, "tax.statutory.manage") ? Results.Ok(await repository.SaveSectionAsync(request, CurrentUser(context).Id)) : Results.StatusCode(403));
app.MapPost("/api/tax-engine/compute", async (TaxEngineRepository repository, TaxComputationRequest request, HttpContext context) => HasPermission(context, "payroll.run") || HasPermission(context, "settings.manage") ? Results.Ok(await repository.ComputeAsync(request)) : Results.StatusCode(403));
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

app.MapGet("/api/leave-attendance/preferences", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetPreferencesAsync(clientId)))
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

app.MapGet("/api/leave-attendance/attendance/monthly", async (LeaveAttendanceRepository repository, int clientId, string month) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetMonthlyAttendanceAsync(clientId, month)))
.WithName("GetMonthlyAttendance")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/context", async (LeaveAttendanceRepository repository, int clientId, string month) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetAttendanceReviewContextAsync(clientId, month)))
.WithName("GetAttendanceReviewContext")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance/monthly", async (LeaveAttendanceRepository repository, SaveMonthlyAttendanceRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
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

app.MapGet("/api/leave-attendance/attendance/daily-grid", async (LeaveAttendanceRepository repository, int clientId, string month) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetDailyAttendanceMonthAsync(clientId, month)))
.WithName("GetDailyAttendanceGrid")
.WithOpenApi();

app.MapPost("/api/leave-attendance/attendance/daily", async (LeaveAttendanceRepository repository, SaveDailyAttendanceRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (rows, error) = await repository.SaveDailyAttendanceAsync(request);
    return rows is null ? Results.BadRequest(new { error }) : Results.Ok(rows);
})
.WithName("SaveDailyAttendance")
.WithOpenApi();

app.MapGet("/api/leave-attendance/leave-types", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetLeaveTypesAsync(clientId)))
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

app.MapPost("/api/leave-attendance/import-balances/preview", async (LeaveBalanceImportRepository repository, [FromForm] int clientId, [FromForm] IFormFile file, [FromForm] string encoding, [FromForm] string? mappingJson, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0)
        return Results.BadRequest(new { error = "Select a client." });
    if (file.Length == 0)
        return Results.BadRequest(new { error = "Select a CSV, XLS or XLSX file." });
    var preview = await repository.PreviewAsync(clientId, file, encoding, mappingJson);
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

app.MapGet("/api/employees", async (EmployeeRepository repository) =>
    Results.Ok(await repository.GetAsync()))
.WithName("GetEmployees")
.WithOpenApi();

app.MapPost("/api/employees", async (EmployeeRepository repository, Employee employee) =>
{
    if (employee.ClientId == 0 || string.IsNullOrWhiteSpace(employee.EmployeeCode) || string.IsNullOrWhiteSpace(employee.FirstName))
        return Results.BadRequest(new { error = "Client, employee code and first name are required." });
    employee.SalaryJson = string.IsNullOrWhiteSpace(employee.SalaryJson) ? "{}" : employee.SalaryJson;
    employee.PersonalJson = string.IsNullOrWhiteSpace(employee.PersonalJson) ? "{}" : employee.PersonalJson;
    employee.PaymentJson = string.IsNullOrWhiteSpace(employee.PaymentJson) ? "{}" : employee.PaymentJson;
    var id = await repository.SaveAsync(employee);
    return Results.Ok(new { id });
})
.WithName("SaveEmployee")
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
        var payRun = await repository.CreateAsync(request, CurrentUser(context).Email);
        return payRun is null ? Results.Conflict(new { error = "A pay run already exists for this period." }) : Results.Created($"/api/pay-runs/{payRun.Id}", payRun);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
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
    var payRun = await repository.SubmitForApprovalAsync(id);
    return payRun is null ? Results.BadRequest(new { error = "Only draft pay runs can be locked and sent for approval." }) : Results.Ok(payRun);
})
.WithName("SubmitPayRunForApproval")
.WithOpenApi();

app.MapPost("/api/pay-runs/{id:int}/approve", async (PayRunRepository repository, int id, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.approve"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var payRun = await repository.ApproveAsync(id);
    return payRun is null ? Results.BadRequest(new { error = "Only draft or pending approval pay runs can be approved." }) : Results.Ok(payRun);
})
.WithName("ApprovePayRun")
.WithOpenApi();

app.MapDelete("/api/pay-runs/{id:int}", async (PayRunRepository repository, int id) =>
    await repository.DeleteDraftAsync(id) ? Results.NoContent() : Results.BadRequest(new { error = "Only draft pay runs can be deleted." }))
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
    var rows = new List<string> { "Employee Code,Employee,Present Days,Gross Pay,Deductions,Net Pay,Payment Status" };
    rows.AddRange(payRun.Employees.Where(employee => !employee.IsSkipped).Select(employee => $"{employee.EmployeeCode},\"{employee.EmployeeName.Replace("\"", "\"\"")}\",{employee.PresentDays},{employee.GrossPay},{employee.StatutoryDeductions + employee.OneTimeDeductions},{employee.NetPay},{employee.PaymentStatus}"));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, rows)), "text/csv", $"payroll-{payRun.PayPeriod}.csv");
})
.WithName("ExportPayRun")
.WithOpenApi();

static AuthUser CurrentUser(HttpContext context) =>
    context.Items.TryGetValue("User", out var user) && user is AuthUser authUser
        ? authUser
        : new AuthUser();

static bool HasPermission(HttpContext context, string permission) =>
    CurrentUser(context).Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

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
    await scopedServices.GetRequiredService<PayRunRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<AuthRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<LeaveAttendanceRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<WorkflowRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<TaxEngineRepository>().InitializeAsync();

    await using var workflowDb = new MySqlConnector.MySqlConnection(configuration.GetConnectionString("Default"));
    await workflowDb.OpenAsync();
    await workflowDb.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS essleaverequests (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL,
    LeaveTypeId INT NOT NULL,
    FromDate DATE NOT NULL,
    ToDate DATE NOT NULL,
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
