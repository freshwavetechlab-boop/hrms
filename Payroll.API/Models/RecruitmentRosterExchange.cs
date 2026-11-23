namespace Payroll.API.Models;

public sealed class RosterExchangeSettings
{
    public int ClientId { get; set; }
    public int TemplateId { get; set; }
    public string SourceGroup { get; set; } = "";
    public string Mode { get; set; } = "Update";
    public List<int> SourceRows { get; set; } = [];
    public string PreviewToken { get; set; } = "";
}

public sealed record RosterExchangeColumn(string Key, string Header, string Infotype, string Type = "text", bool ReadOnly = false, bool RequiredOnInsert = false, string Description = "");
public sealed class RosterExchangeTemplate
{
    public string Version { get; set; } = "FREVO-EMPLOYEE-1";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public int TemplateId { get; set; }
    public string SourceGroup { get; set; } = "";
    public List<RosterExchangeColumn> Columns { get; set; } = [];
    public List<Dictionary<string, string>> Rows { get; set; } = [];
    public List<string> ImportBatches { get; set; } = [];
}
public sealed class RosterExchangePreviewRow
{
    public int SourceRow { get; set; }
    public string ProfileCode { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Action { get; set; } = "Skip";
    public string Reason { get; set; } = "";
    public List<string> ChangedFields { get; set; } = [];
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore] public long? RosterId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int? EmployeeId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Revision { get; set; } = "";
}
public sealed class RosterExchangePreview
{
    public string FileName { get; set; } = "";
    public string PreviewToken { get; set; } = "";
    public List<RosterExchangePreviewRow> Rows { get; set; } = [];
}
public sealed class RosterExchangeResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public List<RosterExchangePreviewRow> Items { get; set; } = [];
}
