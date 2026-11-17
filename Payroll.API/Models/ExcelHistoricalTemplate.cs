using System.Text.Json.Nodes;
namespace Payroll.API.Models;

public sealed class HistoricalTemplateConfigurationRequest
{
    public HistoricalPeriodSalaryRequest SourceReview { get; set; }=new();
    public string TemplateId { get; set; }="";
    public string ExpectedSourceHash { get; set; }="";
    public string ExpectedSetupHash { get; set; }="";
}
public sealed class HistoricalTemplateConfigurationPreview
{
    public int ClientId { get; set; }
    public string SourceHash { get; set; }="";
    public string SetupHash { get; set; }="";
    public string Fingerprint { get; set; }="";
    public JsonObject Template { get; set; }=new();
    public List<string> Blockers { get; set; }=[];
    public bool CanCreate=>Blockers.Count==0;
    public string ReviewToken { get; set; }="";
    public DateTime? ExpiresAtUtc { get; set; }
    public string Meaning { get; set; }="Creates one reusable client salary template only. No employee assignment, salary history, attendance, policy, payroll result or PT control is saved; complete native historical proof is still required separately.";
}
public sealed record HistoricalTemplateConfigurationApplyRequest(string ReviewToken,string RequestId,bool Acknowledged);
public sealed record HistoricalTemplateConfigurationReceipt(int ClientId,string TemplateId,string RequestId,string Fingerprint,bool Replayed);
