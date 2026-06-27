namespace Payroll.API.Models;

public class TaxEngineSetup
{
    public List<TaxFinancialYear> FinancialYears { get; set; } = [];
    public List<TaxRuleVersion> RuleVersions { get; set; } = [];
    public List<TaxRegimeMaster> Regimes { get; set; } = [];
    public List<ClientTaxSetting> ClientSettings { get; set; } = [];
    public List<TaxSlab> Slabs { get; set; } = [];
    public List<TaxSurcharge> Surcharges { get; set; } = [];
    public List<TaxFinalAdjustment> FinalAdjustments { get; set; } = [];
    public List<TaxDeclarationSection> DeclarationSections { get; set; } = [];
    public List<TaxDeductionOption> DeductionOptions { get; set; } = [];
    public List<TaxStandardDeduction> StandardDeductions { get; set; } = [];
    public List<TaxRebateRule> RebateRules { get; set; } = [];
    public List<TaxExemptionRule> ExemptionRules { get; set; } = [];
    public List<TaxHraRule> HraRules { get; set; } = [];
    public List<TaxRuleSourceReference> SourceReferences { get; set; } = [];
    public List<TaxRuleAuditLog> AuditLogs { get; set; } = [];
}

public class TaxFinancialYear
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool Active { get; set; } = true;
    public string Notes { get; set; } = "";
}

public class TaxRuleVersion
{
    public int Id { get; set; }
    public string FinancialYear { get; set; } = "";
    public int FinancialYearId { get; set; }
    public string VersionNumber { get; set; } = "1.0";
    public string VersionName { get; set; } = "";
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public bool IsPublished { get; set; } = true;
    public bool Active { get; set; } = true;
    public int? SourceReferenceId { get; set; }
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class TaxRegimeMaster
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public int RegimeId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Code { get; set; } = "New";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Active { get; set; } = true;
    public string Notes { get; set; } = "";
}

public class ClientTaxSetting
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string FinancialYear { get; set; } = "";
    public string DefaultRegime { get; set; } = "New";
    public bool AllowEmployeeRegimeSelection { get; set; } = true;
    public bool RegimeSelectionWindowOpen { get; set; }
    public DateTime? RegimeSelectionCutoff { get; set; }
    public bool AllowDeclarations { get; set; } = true;
    public bool PlannedDeclarationWindowOpen { get; set; }
    public bool ActualDeclarationWindowOpen { get; set; }
    public DateTime? DeclarationWindowStart { get; set; }
    public DateTime? DeclarationWindowEnd { get; set; }
    public DateTime? PlannedDeclarationStart { get; set; }
    public DateTime? PlannedDeclarationEnd { get; set; }
    public DateTime? ActualDeclarationStart { get; set; }
    public DateTime? ActualDeclarationEnd { get; set; }
    public string PoiProcessingMonth { get; set; } = "";
    public bool ReminderEmailsEnabled { get; set; } = true;
    public string ReminderFrequency { get; set; } = "Weekly";
    public int ReminderBeforeLockDays { get; set; } = 7;
    public bool RequireProofUpload { get; set; } = true;
    public bool RequireApproval { get; set; } = true;
    public string TaxDeductionComponentCode { get; set; } = "TDS";
    public bool ProjectMonthlyTds { get; set; } = true;
    public bool LockAfterApproval { get; set; } = true;
    public bool Active { get; set; } = true;
}

public class TaxSlab
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public int RegimeId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Regime { get; set; } = "New";
    public decimal IncomeFrom { get; set; }
    public decimal? IncomeTo { get; set; }
    public decimal RatePercent { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class TaxSurcharge
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public int RegimeId { get; set; }
    public string FinancialYear { get; set; } = "";
    public decimal IncomeFrom { get; set; }
    public decimal? IncomeTo { get; set; }
    public decimal SurchargePercent { get; set; }
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class TaxFinalAdjustment
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string RuleType { get; set; } = "FinalAdjustment";
    public string Label { get; set; } = "";
    public string ValueType { get; set; } = "Percent";
    public decimal Value { get; set; }
    public int ApplyOrder { get; set; } = 100;
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class TaxDeclarationSection
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public int RegimeId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Investment";
    public string Regime { get; set; } = "Old";
    public decimal? LimitAmount { get; set; }
    public bool ProofRequired { get; set; } = true;
    public bool RequiresApproval { get; set; } = true;
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class TaxDeductionOption
{
    public int Id { get; set; }
    public int SectionId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal? LimitAmount { get; set; }
    public bool ProofRequired { get; set; } = true;
    public bool Active { get; set; } = true;
    public string Notes { get; set; } = "";
}

public class TaxStandardDeduction
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public int RegimeId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Regime { get; set; } = "Both";
    public decimal Amount { get; set; }
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class TaxRebateRule
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Regime { get; set; } = "Both";
    public decimal IncomeLimit { get; set; }
    public decimal RebateAmount { get; set; }
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
}

public class TaxExemptionRule
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string FormulaJson { get; set; } = "{}";
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
}

public class TaxHraRule
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public string FinancialYear { get; set; } = "";
    public int RegimeId { get; set; }
    public decimal MetroSalaryPercent { get; set; } = 50;
    public decimal NonMetroSalaryPercent { get; set; } = 40;
    public decimal RentMinusBasicPercent { get; set; } = 10;
    public string FormulaType { get; set; } = "LeastOf";
    public bool IsApplicable { get; set; } = true;
    public bool Active { get; set; } = true;
    public string Source { get; set; } = "";
}

public class TaxRuleSourceReference
{
    public int Id { get; set; }
    public int RuleVersionId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public DateTime? PublishedDate { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public string Notes { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class TaxRuleAuditLog
{
    public long Id { get; set; }
    public string EntityName { get; set; } = "";
    public long EntityId { get; set; }
    public string Action { get; set; } = "";
    public string OldValueJson { get; set; } = "";
    public string NewValueJson { get; set; } = "";
    public int? ChangedBy { get; set; }
    public DateTime ChangedOn { get; set; }
    public string ChangeReason { get; set; } = "";
    public int? FinancialYearId { get; set; }
    public int? TaxRuleVersionId { get; set; }
}

public class TaxComputationRequest
{
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string FinancialYear { get; set; } = "";
    public string PayPeriod { get; set; } = "";
    public decimal AnnualGrossSalary { get; set; }
    public decimal TdsAlreadyDeducted { get; set; }
}

public class TaxComputationResult
{
    public string FinancialYear { get; set; } = "";
    public int RuleVersionId { get; set; }
    public string Regime { get; set; } = "";
    public decimal GrossSalary { get; set; }
    public decimal StandardDeduction { get; set; }
    public decimal ApprovedDeductions { get; set; }
    public decimal TaxableIncome { get; set; }
    public decimal SlabTax { get; set; }
    public decimal Rebate { get; set; }
    public decimal Surcharge { get; set; }
    public decimal Cess { get; set; }
    public decimal AnnualTax { get; set; }
    public decimal TdsAlreadyDeducted { get; set; }
    public decimal RemainingTax { get; set; }
    public decimal MonthlyTds { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}
