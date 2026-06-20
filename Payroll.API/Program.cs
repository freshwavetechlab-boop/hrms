using Payroll.API.Models;
using Payroll.API.Repositories;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<OrganizationRepository>();
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<EmployeeRepository>();
builder.Services.AddSingleton<PayRunRepository>();
builder.Services.AddSingleton<AuthRepository>();
builder.Services.AddSingleton<LeaveAttendanceRepository>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<OrganizationRepository>();
    await repository.InitializeAsync();
    var payRunRepository = scope.ServiceProvider.GetRequiredService<PayRunRepository>();
    await payRunRepository.InitializeAsync();
    var authRepository = scope.ServiceProvider.GetRequiredService<AuthRepository>();
    await authRepository.InitializeAsync();
    var leaveAttendanceRepository = scope.ServiceProvider.GetRequiredService<LeaveAttendanceRepository>();
    await leaveAttendanceRepository.InitializeAsync();
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
    var authorization = context.Request.Headers.Authorization.ToString();
    var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : string.Empty;
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
    return result is null ? Results.Unauthorized() : Results.Ok(result);
})
.WithName("Login")
.WithOpenApi();

app.MapGet("/api/auth/me", (HttpContext context) =>
    Results.Ok(CurrentUser(context)))
.WithName("GetCurrentUser")
.WithOpenApi();

app.MapPost("/api/auth/logout", async (AuthRepository repository, HttpContext context) =>
{
    var authorization = context.Request.Headers.Authorization.ToString();
    var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : string.Empty;
    await repository.LogoutAsync(token, CurrentUser(context), context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString());
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

app.MapGet("/api/leave-attendance/setup", async (LeaveAttendanceRepository repository) =>
    Results.Ok(await repository.GetAsync()))
.WithName("GetLeaveAttendanceSetup")
.WithOpenApi();

app.MapPost("/api/leave-attendance/module", async (LeaveAttendanceRepository repository, UpdateLeaveAttendanceModuleRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Ok(await repository.SetEnabledAsync(request.IsEnabled));
})
.WithName("UpdateLeaveAttendanceModule")
.WithOpenApi();

app.MapPut("/api/leave-attendance/setup/{stepCode}", async (LeaveAttendanceRepository repository, string stepCode, UpdateLeaveAttendanceStepRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "settings.manage"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var setup = await repository.UpdateStepAsync(stepCode, request.Status);
    return setup is null ? Results.BadRequest(new { error = "Invalid setup step/status, or mandatory General Settings cannot be disabled." }) : Results.Ok(setup);
})
.WithName("UpdateLeaveAttendanceSetupStep")
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
    if (!System.Text.RegularExpressions.Regex.IsMatch(location.PostalCode ?? "", @"^[1-9][0-9]{5}$"))
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

app.MapPost("/api/pay-runs", async (PayRunRepository repository, CreatePayRunRequest request, HttpContext context) =>
{
    if (!HasPermission(context, "payroll.run"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.ClientId == 0 || !System.Text.RegularExpressions.Regex.IsMatch(request.PayPeriod ?? "", @"^\d{4}-(0[1-9]|1[0-2])$") || request.TotalWorkingDays is < 1 or > 31)
        return Results.BadRequest(new { error = "Select a client and enter a valid pay period with 1 to 31 working days." });
    var payRun = await repository.CreateAsync(request);
    return payRun is null ? Results.Conflict(new { error = "A pay run already exists for this period." }) : Results.Created($"/api/pay-runs/{payRun.Id}", payRun);
})
.WithName("CreatePayRun")
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

app.Run();
