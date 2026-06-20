using Payroll.API.Models;
using Payroll.API.Repositories;

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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<OrganizationRepository>();
    await repository.InitializeAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();

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

app.Run();
