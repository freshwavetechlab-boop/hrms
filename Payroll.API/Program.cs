using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
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
builder.Services.AddSingleton<ClientBillingRepository>();
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
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<ScheduledJobRepository>();
builder.Services.AddSingleton<TravelExpenseRepository>();
builder.Services.AddHostedService<PayrollRunWorker>();
builder.Services.AddHostedService<ScheduledJobWorker>();

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
app.MapPost("/api/workflows/tasks/{taskId:long}/{action}", async (WorkflowRepository repository, EssMssRepository essRepository, PayRunRepository payRuns, NotificationRepository notifications,long taskId,string action,WorkflowActionRequest request,HttpContext context) =>
{
    if(action is not ("Approved" or "Rejected" or "Sent Back")) return Results.BadRequest();
    var user=CurrentUser(context);
    var task=await repository.ActionAsync(taskId,user.Id,action,request.Comment);
    if(!task)return Results.NotFound();
    var instance=await repository.GetInstanceForTaskAsync(taskId);
    if(instance?.ResourceType=="LeaveRequest")await essRepository.SyncLeaveWorkflowStatusAsync(instance.ResourceId,instance.Status);
    if(instance?.ResourceType=="TravelRequest")await essRepository.SyncTravelWorkflowStatusAsync(instance.ResourceId,instance.Status);
    if(instance?.ResourceType=="ExpenseClaim")await essRepository.SyncExpenseWorkflowStatusAsync(instance.ResourceId,instance.Status);
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

app.MapGet("/api/ess/leave/requests", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetLeaveRequestsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/leave/requests/{id:long}/trail", async (EssMssRepository repository, long id, HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var trail=await repository.GetLeaveRequestTrailAsync(id,user.EmployeeId.Value,user.ClientId); return trail is null ? Results.NotFound() : Results.Ok(trail); });
app.MapGet("/api/ess/pay/payslips", async (EssMssRepository repository, HttpContext context) => { var user=CurrentUser(context); return user.EmployeeId is null ? Results.StatusCode(403) : Results.Ok(await repository.GetPayslipsAsync(user.EmployeeId.Value,user.ClientId)); });
app.MapGet("/api/ess/pay/payslips/{payRunId:int}", async (EssMssRepository repository, int payRunId, HttpContext context) => { var user=CurrentUser(context); if(user.EmployeeId is null)return Results.StatusCode(403); var document=await repository.GetPayslipDocumentAsync(user.EmployeeId.Value,user.ClientId,payRunId); return document is null ? Results.NotFound() : Results.Ok(document); });
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
    await repository.RetryAsync(id);
    return Results.NoContent();
});
app.MapPost("/api/notifications/test", async (NotificationRepository repository, NotificationTestRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage")) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.RuleId <= 0 || string.IsNullOrWhiteSpace(request.ToEmail)) return Results.BadRequest(new { error = "Rule and test email are required." });
    await repository.QueueTestAsync(request, CurrentUser(context).Id);
    return Results.NoContent();
});

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

app.MapGet("/api/leave-attendance/groups", async (LeaveAttendanceRepository repository, int? clientId) =>
    Results.Ok(await repository.GetAttendanceGroupsAsync(Math.Max(0, clientId.GetValueOrDefault()))))
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

app.MapGet("/api/leave-attendance/attendance/monthly", async (LeaveAttendanceRepository repository, int clientId, string month, int? workLocationId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetMonthlyAttendanceAsync(clientId, month, workLocationId)))
.WithName("GetMonthlyAttendance")
.WithOpenApi();

app.MapGet("/api/leave-attendance/attendance/context", async (LeaveAttendanceRepository repository, int clientId, string month, int? workLocationId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetAttendanceReviewContextAsync(clientId, month, workLocationId)))
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

app.MapGet("/api/leave-attendance/attendance/daily-grid", async (LeaveAttendanceRepository repository, int clientId, string month, int? workLocationId) =>
    clientId <= 0 ? Results.BadRequest(new { error = "Select a client." }) : Results.Ok(await repository.GetDailyAttendanceMonthAsync(clientId, month, workLocationId)))
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

app.MapPost("/api/leave-attendance/attendance/daily/batch", async (LeaveAttendanceRepository repository, SaveDailyAttendanceBatchRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (rows, error) = await repository.SaveDailyAttendanceBatchAsync(request);
    return rows is null ? Results.BadRequest(new { error }) : Results.Ok(rows);
})
.WithName("SaveDailyAttendanceBatch")
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

app.MapGet("/api/leave-attendance/leave-types/import-template", async (LeaveAttendanceRepository repository, int clientId) =>
    clientId <= 0
        ? Results.BadRequest(new { error = "Select a client." })
        : Results.File(await repository.BuildLeaveTypeImportTemplateAsync(clientId), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "leave-type-import-template.xlsx"))
.WithName("DownloadLeaveTypeImportTemplate")
.WithOpenApi();

app.MapPost("/api/leave-attendance/leave-types/import-jobs", async (LeaveAttendanceRepository repository, [FromForm] int clientId, [FromForm] IFormFile file, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a leave type import file." });
    return Results.Accepted("/api/leave-attendance/leave-types/import-jobs", await repository.StartLeaveTypeImportJobAsync(clientId, file));
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

app.MapPost("/api/leave-attendance/holidays/import-jobs", async (LeaveAttendanceRepository repository, [FromForm] int clientId, [FromForm] IFormFile file, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a holiday import file." });
    return Results.Accepted("/api/leave-attendance/holidays/import-jobs", await repository.StartHolidayImportJobAsync(clientId, file));
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

app.MapGet("/api/employees/import-template", async (EmployeeRepository repository, int clientId) =>
    clientId <= 0
        ? Results.BadRequest(new { error = "Select a client." })
        : Results.File(await repository.BuildImportTemplateAsync(clientId), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "employee-import-template.xlsx"))
.WithName("DownloadEmployeeImportTemplate")
.WithOpenApi();

app.MapPost("/api/employees/import", async (EmployeeRepository repository, [FromForm] int clientId, [FromForm] IFormFile file) =>
{
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select an employee CSV file." });
    var result = await repository.ImportCsvAsync(clientId, file);
    return result.Errors.Count > 0 ? Results.BadRequest(result) : Results.Ok(result);
})
.DisableAntiforgery()
.WithName("ImportEmployees")
.WithOpenApi();

app.MapPost("/api/employees/import-jobs", async (EmployeeRepository repository, [FromForm] int clientId, [FromForm] IFormFile file) =>
{
    if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select an employee CSV file." });
    return Results.Accepted($"/api/employees/import-jobs", await repository.StartImportCsvJobAsync(clientId, file));
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
    await scopedServices.GetRequiredService<NotificationRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<ScheduledJobRepository>().InitializeAsync();
    await scopedServices.GetRequiredService<TravelExpenseRepository>().InitializeAsync();

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
