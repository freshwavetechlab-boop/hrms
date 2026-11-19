using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

// Pure calculator only: no WebApplication, IConfiguration, database or mail services.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
try
{
    using var document = JsonDocument.Parse(await Console.In.ReadToEndAsync());
    var root = document.RootElement;
    object result;
    if (root.GetProperty("operation").GetString() is "employee" or "employeeBatch")
    {
        var assembly = typeof(PayRunRepository).Assembly;
        if (root.TryGetProperty("baselineAssembly", out var baseline))
        {
            var path = Path.GetFullPath(baseline.GetString()!);
            var context = new AssemblyLoadContext("read-only-native-baseline", isCollectible: true);
            context.Resolving += (loader, name) => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll"))
                ? loader.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll")) : null;
            assembly = context.LoadFromAssemblyPath(path);
        }
        var settings = root.GetProperty("settings").GetRawText();
        result = root.GetProperty("operation").GetString() == "employee"
            ? NativeEmployee(root.GetProperty("request"), settings, assembly, json)
            : root.GetProperty("requests").EnumerateArray().Select(request => NativeEmployee(request, settings, assembly, json)).ToArray();
    }
    else if (root.GetProperty("operation").GetString() == "catalogue")
        result = PayrollValueCatalogue.Build(root.GetProperty("request").Deserialize<PayrollCatalogueRequest>(json)!);
    else if (root.GetProperty("operation").GetString() == "simulate")
        result = PayRunRepository.SimulateTemplate(root.GetProperty("request").Deserialize<PayrollSimulationRequest>(json)!);
    else
    {
        var masters = SalaryTemplateExcelExtractionService.ReadMasterCatalogue("{\"salaryComponents\":" + root.GetProperty("components").GetRawText() + "}");
        result = SalaryTemplateExcelExtractionService.Extract(root.GetProperty("request").Deserialize<SalaryTemplateExcelExtractionRequest>(json)!, masters);
    }
    Console.WriteLine(JsonSerializer.Serialize(result, json));
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error = error.Message }, json));
    Environment.ExitCode = 1;
}

static object NativeEmployee(JsonElement request, string settings, Assembly assembly, JsonSerializerOptions json)
{
    var repository = assembly.GetType("Payroll.API.Repositories.PayRunRepository")!;
    var sourceType = repository.GetNestedType("PayRunSourceEmployee", BindingFlags.NonPublic)!;
    var employee = JsonSerializer.Deserialize(request.GetProperty("employee").GetRawText(), sourceType, json)!;
    if (request.TryGetProperty("payableOnlyEvidence", out var evidence))
    {
        var property = sourceType.GetProperty("PayableOnlyAttendance")!;
        property.SetValue(employee, JsonSerializer.Deserialize(evidence.GetRawText(), property.PropertyType, json));
    }
    var adjustments = Array.CreateInstance(assembly.GetType("Payroll.API.Models.PayrollAdjustment")!, 0);
    try
    {
        return repository.GetMethod("BuildEmployee", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [0, employee, settings, request.GetProperty("payPeriod").GetString(), request.GetProperty("payrollDays").GetInt32(),
             request.GetProperty("presentDays").ValueKind == JsonValueKind.Null ? null : request.GetProperty("presentDays").GetDecimal(),
             request.GetProperty("payableDays").GetDecimal(), adjustments, 0m,
             request.TryGetProperty("oneTimeDeductions", out var deduction) ? deduction.GetDecimal() : 0m, -1m, false, "TDS"])!;
    }
    catch (TargetInvocationException error) { throw error.InnerException!; }
}
