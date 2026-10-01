using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Payroll.API.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Payroll.API.Services;

// Keep interactive documentation on the same models and routes as the running API.
public class ApiCatalogDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        document.Info.Title = "Frevo HRMS API";
        document.Components.SecuritySchemes["AttendanceKey"] = new() { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Attendance API key generated in API Catalog. Valid only for its registered punch machine." };
        if (!document.Paths.TryGetValue("/api/integrations/attendance/punches", out var path) || !path.Operations.TryGetValue(OperationType.Post, out var operation)) return;
        operation.Summary = "Send punch-in and punch-out logs";
        operation.Description = "Send 1–500 logs. Use the same punchId when retrying; duplicates are ignored. Check every result: Accepted, Duplicate or Rejected. Employee and device must belong to the same client and work location. Times require a timezone.";
        operation.Security = [new() { [new OpenApiSecurityScheme { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "AttendanceKey" } }] = [] }];
        var body = operation.RequestBody.Content["application/json"];
        body.Example = new OpenApiObject { ["deviceId"] = new OpenApiString("GATE-01"), ["punches"] = new OpenApiArray { new OpenApiObject { ["punchId"] = new OpenApiString("unique-machine-log-123"), ["employeeCode"] = new OpenApiString("EMP001"), ["action"] = new OpenApiString("IN"), ["capturedAt"] = new OpenApiString("2026-09-01T09:00:00+05:30") } } };
        if (document.Components.Schemas.TryGetValue("MachineAttendanceRequest", out var batch))
        {
            batch.Required.UnionWith(["deviceId", "punches"]);
            batch.Properties["deviceId"].Description = "Machine ID saved in Attendance settings.";
            batch.Properties["punches"].MinItems = 1; batch.Properties["punches"].MaxItems = 500;
        }
        if (document.Components.Schemas.TryGetValue("MachineAttendancePunch", out var punch))
        {
            punch.Required.UnionWith(["punchId", "employeeCode", "action", "capturedAt"]);
            punch.Properties["punchId"].Description = "Unique machine log ID. Reuse this ID for retries."; punch.Properties["punchId"].MaxLength = 100;
            punch.Properties["employeeCode"].Description = "Employee code matching Employee Master.";
            punch.Properties["action"].Enum = [new OpenApiString("IN"), new OpenApiString("OUT"), new OpenApiString("CheckIn"), new OpenApiString("CheckOut")];
            punch.Properties["capturedAt"].Description = "Punch timestamp with timezone, e.g. 2026-09-01T09:00:00+05:30."; punch.Properties["capturedAt"].Format = "date-time";
        }
        operation.Responses["200"] = new()
        {
            Description = "Per-punch results: Accepted (attendance updated), Duplicate (already recorded), or Rejected (inspect error).",
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new() { Type = "object", Properties = new Dictionary<string, OpenApiSchema> { ["results"] = new() { Type = "array", Items = context.SchemaGenerator.GenerateSchema(typeof(MachinePunchResult), context.SchemaRepository) } } } } }
        };
        foreach (var (status, description) in new[] { ("400", "Invalid request or batch size."), ("401", "API key missing, invalid, expired or revoked."), ("403", "Machine ID does not match this API key.") }) operation.Responses[status] = new() { Description = description };
    }
}
