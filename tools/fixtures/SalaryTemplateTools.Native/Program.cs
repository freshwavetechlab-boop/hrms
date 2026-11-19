using System.Text.Json;
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
    if (root.GetProperty("operation").GetString() == "simulate")
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
