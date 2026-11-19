using System.Text.Json.Serialization;

namespace Payroll.API.Models;

// A template-only command. A complete setup or new component masters must not
// accidentally be accepted by this endpoint.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SalaryTemplateCreateRequest
{
    public string Name { get; set; } = "";
    public string AnnualCtc { get; set; } = "";
    public bool Active { get; set; } = true;
    public List<SalaryTemplateCreateLine> Lines { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SalaryTemplateCreateLine
{
    public string ComponentId { get; set; } = "";
    public string Value { get; set; } = "";
    public string CalculationType { get; set; } = "";
    public string Formula { get; set; } = "";
    public string BaseComponent { get; set; } = "";
    public string ProRataOverride { get; set; } = "";
    public string RoundingMode { get; set; } = "";
}
