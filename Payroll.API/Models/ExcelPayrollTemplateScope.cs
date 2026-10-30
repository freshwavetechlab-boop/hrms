namespace Payroll.API.Models;
public sealed class ExcelPayrollTemplateScope
{
    public bool Reviewed { get; set; }
    public string EffectiveTo { get; set; } = "";
    public bool OpenEnded { get; set; }
    public string LocationMode { get; set; } = ""; // AllClientLocations or Listed
    public List<int> WorkLocationIds { get; set; } = [];
    public List<string> NewLocationNames { get; set; } = [];
    public string CategoryKind { get; set; } = ""; // AllClientCategories, SkillCategory or EmploymentType
    public List<string> Categories { get; set; } = [];
    public string DivisorMeaning { get; set; } = ""; // CalendarDays, ContractFixedDays or WorkingDays
    public string PayableMeaning { get; set; } = ""; // AlreadyPayable
    public string PolicyBasis { get; set; } = ""; // SavedSourceRules or DatedPolicy
    public string PolicyVersion { get; set; } = "";
}
public sealed record ExcelPayrollRecordedTemplateScope(int ClientId,string TemplateId,string RulesSignature,string EffectiveFrom,string PolicyKey,ExcelPayrollTemplateScope Scope,string SourceBatchId,string SourceMonth);
