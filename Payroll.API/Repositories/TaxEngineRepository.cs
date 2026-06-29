using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class TaxEngineRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS tax_client_settings (
id INT PRIMARY KEY AUTO_INCREMENT, client_id INT NOT NULL, enabled BOOLEAN NOT NULL DEFAULT TRUE, financial_year VARCHAR(10) NOT NULL,
default_regime VARCHAR(10) NOT NULL DEFAULT 'New', allow_employee_regime_selection BOOLEAN NOT NULL DEFAULT TRUE, regime_selection_window_open BOOLEAN NOT NULL DEFAULT FALSE, regime_selection_cutoff DATE NULL,
allow_declarations BOOLEAN NOT NULL DEFAULT TRUE, planned_declaration_window_open BOOLEAN NOT NULL DEFAULT FALSE, actual_declaration_window_open BOOLEAN NOT NULL DEFAULT FALSE, declaration_window_start DATE NULL, declaration_window_end DATE NULL,
planned_declaration_start DATE NULL, planned_declaration_end DATE NULL, actual_declaration_start DATE NULL, actual_declaration_end DATE NULL,
poi_processing_month VARCHAR(7) NOT NULL DEFAULT '', reminder_emails_enabled BOOLEAN NOT NULL DEFAULT TRUE, reminder_frequency VARCHAR(30) NOT NULL DEFAULT 'Weekly', reminder_before_lock_days INT NOT NULL DEFAULT 7,
require_proof_upload BOOLEAN NOT NULL DEFAULT TRUE, require_approval BOOLEAN NOT NULL DEFAULT TRUE, tax_deduction_component_code VARCHAR(40) NOT NULL DEFAULT 'TDS',
project_monthly_tds BOOLEAN NOT NULL DEFAULT TRUE, lock_after_approval BOOLEAN NOT NULL DEFAULT TRUE, active BOOLEAN NOT NULL DEFAULT TRUE,
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_tax_client_fy (client_id, financial_year));
CREATE TABLE IF NOT EXISTS tax_financial_years (
id INT PRIMARY KEY AUTO_INCREMENT, code VARCHAR(10) NOT NULL, start_date DATE NOT NULL, end_date DATE NOT NULL, active BOOLEAN NOT NULL DEFAULT TRUE,
notes VARCHAR(1000) NOT NULL DEFAULT '', created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_tax_financial_year_code (code));
CREATE TABLE IF NOT EXISTS tax_rule_versions (
id INT PRIMARY KEY AUTO_INCREMENT, financial_year_id INT NULL, financial_year VARCHAR(10) NOT NULL, version_number VARCHAR(30) NOT NULL DEFAULT '1.0', version_name VARCHAR(120) NOT NULL DEFAULT '', effective_from DATE NOT NULL, effective_to DATE NULL,
is_published BOOLEAN NOT NULL DEFAULT TRUE, active BOOLEAN NOT NULL DEFAULT TRUE, source_reference_id INT NULL, source VARCHAR(250) NOT NULL DEFAULT '', notes VARCHAR(1000) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_tax_rule_version (financial_year, version_number));
CREATE TABLE IF NOT EXISTS tax_regimes (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, code VARCHAR(20) NOT NULL, name VARCHAR(120) NOT NULL,
is_default BOOLEAN NOT NULL DEFAULT FALSE, active BOOLEAN NOT NULL DEFAULT TRUE, notes VARCHAR(1000) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_tax_regime_fy_code (financial_year, code));
CREATE TABLE IF NOT EXISTS tax_slabs (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL DEFAULT '', regime VARCHAR(10) NOT NULL, income_from DECIMAL(14,2) NOT NULL DEFAULT 0, income_to DECIMAL(14,2) NULL,
rate_percent DECIMAL(6,2) NOT NULL DEFAULT 0,
effective_from DATE NOT NULL, effective_to DATE NULL, active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '', notes VARCHAR(1000) NOT NULL DEFAULT '', created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_surcharges (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL DEFAULT '', income_from DECIMAL(14,2) NOT NULL DEFAULT 0, income_to DECIMAL(14,2) NULL,
surcharge_percent DECIMAL(6,2) NOT NULL DEFAULT 0, active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '', notes VARCHAR(1000) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_final_adjustments (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL DEFAULT '', label VARCHAR(120) NOT NULL, value_type VARCHAR(20) NOT NULL DEFAULT 'Percent',
value DECIMAL(14,4) NOT NULL DEFAULT 0, apply_order INT NOT NULL DEFAULT 100, rule_type VARCHAR(40) NOT NULL DEFAULT 'FinalAdjustment', active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '', notes VARCHAR(1000) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_declaration_sections (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL DEFAULT '', code VARCHAR(40) NOT NULL, name VARCHAR(180) NOT NULL, category VARCHAR(80) NOT NULL DEFAULT 'Investment', regime VARCHAR(10) NOT NULL DEFAULT 'Old',
limit_amount DECIMAL(14,2) NULL, proof_required BOOLEAN NOT NULL DEFAULT TRUE, requires_approval BOOLEAN NOT NULL DEFAULT TRUE, active BOOLEAN NOT NULL DEFAULT TRUE,
source VARCHAR(250) NOT NULL DEFAULT '', notes VARCHAR(1000) NOT NULL DEFAULT '', created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP, UNIQUE KEY UX_tax_section_code_fy (financial_year, code));
CREATE TABLE IF NOT EXISTS tax_deduction_options (
id INT PRIMARY KEY AUTO_INCREMENT, section_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, code VARCHAR(60) NOT NULL, name VARCHAR(180) NOT NULL,
limit_amount DECIMAL(14,2) NULL, proof_required BOOLEAN NOT NULL DEFAULT TRUE, active BOOLEAN NOT NULL DEFAULT TRUE, notes VARCHAR(1000) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_tax_deduction_option (financial_year, code));
CREATE TABLE IF NOT EXISTS tax_standard_deductions (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, regime VARCHAR(20) NOT NULL DEFAULT 'Both',
amount DECIMAL(14,2) NOT NULL DEFAULT 0, active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '', notes VARCHAR(1000) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_rebate_rules (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, regime VARCHAR(20) NOT NULL DEFAULT 'Both',
income_limit DECIMAL(14,2) NOT NULL DEFAULT 0, rebate_amount DECIMAL(14,2) NOT NULL DEFAULT 0, active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_exemption_rules (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, code VARCHAR(60) NOT NULL, name VARCHAR(180) NOT NULL,
formula_json JSON NULL, active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '', created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_hra_rules (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, regime_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, is_applicable BOOLEAN NOT NULL DEFAULT TRUE, metro_salary_percent DECIMAL(6,2) NOT NULL DEFAULT 50,
non_metro_salary_percent DECIMAL(6,2) NOT NULL DEFAULT 40, rent_minus_basic_percent DECIMAL(6,2) NOT NULL DEFAULT 10, formula_type VARCHAR(40) NOT NULL DEFAULT 'LeastOf', active BOOLEAN NOT NULL DEFAULT TRUE, source VARCHAR(250) NOT NULL DEFAULT '',
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_rule_source_references (
id INT PRIMARY KEY AUTO_INCREMENT, rule_version_id INT NOT NULL DEFAULT 0, financial_year VARCHAR(10) NOT NULL, source_type VARCHAR(80) NOT NULL, title VARCHAR(250) NOT NULL,
url VARCHAR(500) NOT NULL DEFAULT '', document_number VARCHAR(120) NOT NULL DEFAULT '', published_date DATE NULL, effective_from DATE NULL, notes VARCHAR(1000) NOT NULL DEFAULT '', active BOOLEAN NOT NULL DEFAULT TRUE, created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_rule_audit_logs (
id BIGINT PRIMARY KEY AUTO_INCREMENT, entity_name VARCHAR(120) NOT NULL, entity_id BIGINT NOT NULL, action VARCHAR(40) NOT NULL, old_value_json JSON NULL, new_value_json JSON NULL,
changed_by INT NULL, changed_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, change_reason VARCHAR(500) NOT NULL DEFAULT '', financial_year_id INT NULL, tax_rule_version_id INT NULL);
CREATE TABLE IF NOT EXISTS company_income_tax_section_settings (
id INT PRIMARY KEY AUTO_INCREMENT, company_id INT NOT NULL, financial_year_id INT NULL, financial_year VARCHAR(10) NOT NULL, tax_deduction_section_id INT NOT NULL,
is_enabled BOOLEAN NOT NULL DEFAULT TRUE, is_proof_required BOOLEAN NOT NULL DEFAULT TRUE, is_approval_required BOOLEAN NOT NULL DEFAULT TRUE, display_order INT NOT NULL DEFAULT 100,
created_by INT NULL, created_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, modified_by INT NULL, modified_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_company_tax_section (company_id, financial_year, tax_deduction_section_id));
CREATE TABLE IF NOT EXISTS tax_activity_windows (
id INT PRIMARY KEY AUTO_INCREMENT, client_setting_id INT NULL, client_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, activity_code VARCHAR(40) NOT NULL,
is_open BOOLEAN NOT NULL DEFAULT FALSE, start_date DATE NULL, end_date DATE NULL, cutoff_date DATE NULL, processing_month VARCHAR(7) NOT NULL DEFAULT '',
active BOOLEAN NOT NULL DEFAULT TRUE, created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_tax_activity_client_fy (client_id, financial_year, activity_code));
CREATE TABLE IF NOT EXISTS employee_tax_regime_selections (
id BIGINT PRIMARY KEY AUTO_INCREMENT, employee_id INT NOT NULL, client_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, regime VARCHAR(10) NOT NULL,
status VARCHAR(30) NOT NULL DEFAULT 'Submitted', submitted_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, approved_by_user_id INT NULL, approved_at DATETIME NULL,
UNIQUE KEY UX_employee_tax_regime (employee_id, financial_year));
CREATE TABLE IF NOT EXISTS employee_tax_declaration_headers (
id BIGINT PRIMARY KEY AUTO_INCREMENT, employee_id INT NOT NULL, client_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, activity_code VARCHAR(40) NOT NULL,
status VARCHAR(30) NOT NULL DEFAULT 'Draft', submitted_at DATETIME NULL, approved_by_user_id INT NULL, approved_at DATETIME NULL, remarks VARCHAR(1000) NULL,
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_employee_tax_declaration_header (employee_id, financial_year, activity_code));
CREATE TABLE IF NOT EXISTS employee_tax_declaration_lines (
id BIGINT PRIMARY KEY AUTO_INCREMENT, header_id BIGINT NOT NULL, section_id INT NOT NULL, amount DECIMAL(14,2) NOT NULL DEFAULT 0, approved_amount DECIMAL(14,2) NULL,
status VARCHAR(30) NOT NULL DEFAULT 'Draft', remarks VARCHAR(1000) NULL, created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_employee_tax_declaration_line (header_id, section_id));
CREATE TABLE IF NOT EXISTS employee_tax_declarations (
id BIGINT PRIMARY KEY AUTO_INCREMENT, employee_id INT NOT NULL, client_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, section_id INT NOT NULL,
declared_amount DECIMAL(14,2) NOT NULL DEFAULT 0, approved_amount DECIMAL(14,2) NULL, status VARCHAR(30) NOT NULL DEFAULT 'Draft',
planned_amount DECIMAL(14,2) NOT NULL DEFAULT 0, actual_amount DECIMAL(14,2) NOT NULL DEFAULT 0,
remarks VARCHAR(1000), created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS employee_tax_declaration_proofs (
id BIGINT PRIMARY KEY AUTO_INCREMENT, declaration_id BIGINT NOT NULL, file_name VARCHAR(260) NOT NULL, content_type VARCHAR(100), file_path VARCHAR(500) NOT NULL,
uploaded_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS employee_tax_projection_runs (
id BIGINT PRIMARY KEY AUTO_INCREMENT, employee_id INT NOT NULL, client_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, pay_period VARCHAR(7) NOT NULL,
regime VARCHAR(10) NOT NULL, taxable_income DECIMAL(14,2) NOT NULL DEFAULT 0, approved_deductions DECIMAL(14,2) NOT NULL DEFAULT 0,
annual_tax DECIMAL(14,2) NOT NULL DEFAULT 0, monthly_tds DECIMAL(14,2) NOT NULL DEFAULT 0, calculation_json JSON NULL, created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);");
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS tax_computation_snapshots (
id BIGINT PRIMARY KEY AUTO_INCREMENT, employee_id INT NOT NULL, client_id INT NOT NULL, financial_year VARCHAR(10) NOT NULL, pay_period VARCHAR(7) NOT NULL,
pay_run_id INT NULL,
financial_year_id INT NULL, rule_version_id INT NOT NULL, regime_id INT NULL, regime VARCHAR(20) NOT NULL, gross_salary DECIMAL(14,2) NOT NULL, exemptions_amount DECIMAL(14,2) NOT NULL DEFAULT 0, deductions_amount DECIMAL(14,2) NOT NULL DEFAULT 0, taxable_income DECIMAL(14,2) NOT NULL,
tax_before_rebate DECIMAL(14,2) NOT NULL DEFAULT 0, rebate_amount DECIMAL(14,2) NOT NULL DEFAULT 0, tax_after_rebate DECIMAL(14,2) NOT NULL DEFAULT 0, surcharge_amount DECIMAL(14,2) NOT NULL DEFAULT 0, cess_amount DECIMAL(14,2) NOT NULL DEFAULT 0, total_annual_tax DECIMAL(14,2) NOT NULL DEFAULT 0,
tds_deducted_till_date DECIMAL(14,2) NOT NULL DEFAULT 0, remaining_tax DECIMAL(14,2) NOT NULL DEFAULT 0, annual_tax DECIMAL(14,2) NOT NULL, monthly_tds DECIMAL(14,2) NOT NULL, snapshot_json JSON NOT NULL, rule_breakup_json JSON NULL, declaration_json JSON NULL, proof_json JSON NULL, calculated_by INT NULL, created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);");
        await EnsureColumnAsync(db, "tax_slabs", "financial_year", "financial_year VARCHAR(10) NOT NULL DEFAULT '' AFTER id");
        await EnsureColumnAsync(db, "tax_slabs", "rule_version_id", "rule_version_id INT NOT NULL DEFAULT 0 AFTER id");
        await EnsureColumnAsync(db, "tax_slabs", "regime_id", "regime_id INT NOT NULL DEFAULT 0 AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_slabs", "effective_to", "effective_to DATE NULL AFTER effective_from");
        await EnsureColumnAsync(db, "tax_slabs", "source", "source VARCHAR(250) NOT NULL DEFAULT '' AFTER active");
        await EnsureColumnAsync(db, "tax_slabs", "notes", "notes VARCHAR(1000) NOT NULL DEFAULT '' AFTER source");
        await EnsureColumnAsync(db, "tax_surcharges", "rule_version_id", "rule_version_id INT NOT NULL DEFAULT 0 AFTER id");
        await EnsureColumnAsync(db, "tax_surcharges", "regime_id", "regime_id INT NOT NULL DEFAULT 0 AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_surcharges", "financial_year", "financial_year VARCHAR(10) NOT NULL DEFAULT '' AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_surcharges", "source", "source VARCHAR(250) NOT NULL DEFAULT '' AFTER active");
        await EnsureColumnAsync(db, "tax_surcharges", "notes", "notes VARCHAR(1000) NOT NULL DEFAULT '' AFTER source");
        await EnsureColumnAsync(db, "tax_final_adjustments", "rule_version_id", "rule_version_id INT NOT NULL DEFAULT 0 AFTER id");
        await EnsureColumnAsync(db, "tax_final_adjustments", "financial_year", "financial_year VARCHAR(10) NOT NULL DEFAULT '' AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_final_adjustments", "rule_type", "rule_type VARCHAR(40) NOT NULL DEFAULT 'FinalAdjustment' AFTER apply_order");
        await EnsureColumnAsync(db, "tax_final_adjustments", "source", "source VARCHAR(250) NOT NULL DEFAULT '' AFTER active");
        await EnsureColumnAsync(db, "tax_final_adjustments", "notes", "notes VARCHAR(1000) NOT NULL DEFAULT '' AFTER source");
        await EnsureColumnAsync(db, "tax_declaration_sections", "financial_year", "financial_year VARCHAR(10) NOT NULL DEFAULT '' AFTER id");
        await EnsureColumnAsync(db, "tax_declaration_sections", "rule_version_id", "rule_version_id INT NOT NULL DEFAULT 0 AFTER id");
        await EnsureColumnAsync(db, "tax_declaration_sections", "regime_id", "regime_id INT NOT NULL DEFAULT 0 AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_declaration_sections", "category", "category VARCHAR(80) NOT NULL DEFAULT 'Investment' AFTER name");
        await EnsureColumnAsync(db, "tax_declaration_sections", "source", "source VARCHAR(250) NOT NULL DEFAULT '' AFTER active");
        await EnsureColumnAsync(db, "tax_declaration_sections", "notes", "notes VARCHAR(1000) NOT NULL DEFAULT '' AFTER source");
        await EnsureColumnAsync(db, "tax_client_settings", "regime_selection_window_open", "regime_selection_window_open BOOLEAN NOT NULL DEFAULT FALSE AFTER allow_employee_regime_selection");
        await EnsureColumnAsync(db, "tax_client_settings", "planned_declaration_window_open", "planned_declaration_window_open BOOLEAN NOT NULL DEFAULT FALSE AFTER allow_declarations");
        await EnsureColumnAsync(db, "tax_client_settings", "actual_declaration_window_open", "actual_declaration_window_open BOOLEAN NOT NULL DEFAULT FALSE AFTER planned_declaration_window_open");
        await EnsureColumnAsync(db, "tax_client_settings", "planned_declaration_start", "planned_declaration_start DATE NULL AFTER declaration_window_end");
        await EnsureColumnAsync(db, "tax_client_settings", "planned_declaration_end", "planned_declaration_end DATE NULL AFTER planned_declaration_start");
        await EnsureColumnAsync(db, "tax_client_settings", "actual_declaration_start", "actual_declaration_start DATE NULL AFTER planned_declaration_end");
        await EnsureColumnAsync(db, "tax_client_settings", "actual_declaration_end", "actual_declaration_end DATE NULL AFTER actual_declaration_start");
        await EnsureColumnAsync(db, "tax_client_settings", "poi_processing_month", "poi_processing_month VARCHAR(7) NOT NULL DEFAULT '' AFTER actual_declaration_end");
        await EnsureColumnAsync(db, "tax_client_settings", "reminder_emails_enabled", "reminder_emails_enabled BOOLEAN NOT NULL DEFAULT TRUE AFTER poi_processing_month");
        await EnsureColumnAsync(db, "tax_client_settings", "reminder_frequency", "reminder_frequency VARCHAR(30) NOT NULL DEFAULT 'Weekly' AFTER reminder_emails_enabled");
        await EnsureColumnAsync(db, "tax_client_settings", "reminder_before_lock_days", "reminder_before_lock_days INT NOT NULL DEFAULT 7 AFTER reminder_frequency");
        await EnsureColumnAsync(db, "tax_client_settings", "financial_year_id", "financial_year_id INT NULL AFTER financial_year");
        await EnsureColumnAsync(db, "tax_activity_windows", "financial_year_id", "financial_year_id INT NULL AFTER financial_year");
        await EnsureColumnAsync(db, "employee_tax_regime_selections", "financial_year_id", "financial_year_id INT NULL AFTER financial_year");
        await EnsureColumnAsync(db, "employee_tax_declaration_headers", "financial_year_id", "financial_year_id INT NULL AFTER financial_year");
        await EnsureColumnAsync(db, "employee_tax_declarations", "financial_year_id", "financial_year_id INT NULL AFTER financial_year");
        await EnsureColumnAsync(db, "tax_rule_versions", "financial_year_id", "financial_year_id INT NULL AFTER id");
        await EnsureColumnAsync(db, "tax_rule_versions", "version_name", "version_name VARCHAR(120) NOT NULL DEFAULT '' AFTER version_number");
        await EnsureColumnAsync(db, "tax_rule_versions", "is_published", "is_published BOOLEAN NOT NULL DEFAULT TRUE AFTER effective_to");
        await EnsureColumnAsync(db, "tax_rule_versions", "source_reference_id", "source_reference_id INT NULL AFTER active");
        await EnsureColumnAsync(db, "tax_rule_source_references", "document_number", "document_number VARCHAR(120) NOT NULL DEFAULT '' AFTER url");
        await EnsureColumnAsync(db, "tax_rule_source_references", "published_date", "published_date DATE NULL AFTER document_number");
        await EnsureColumnAsync(db, "tax_rule_source_references", "effective_from", "effective_from DATE NULL AFTER published_date");
        await EnsureColumnAsync(db, "tax_hra_rules", "regime_id", "regime_id INT NOT NULL DEFAULT 0 AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_hra_rules", "is_applicable", "is_applicable BOOLEAN NOT NULL DEFAULT TRUE AFTER financial_year");
        await EnsureColumnAsync(db, "tax_hra_rules", "formula_type", "formula_type VARCHAR(40) NOT NULL DEFAULT 'LeastOf' AFTER rent_minus_basic_percent");
        await EnsureSnapshotColumnsAsync(db);
        await EnsureColumnAsync(db, "employee_tax_declarations", "planned_amount", "planned_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER status");
        await EnsureColumnAsync(db, "employee_tax_declarations", "actual_amount", "actual_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER planned_amount");
        await DropColumnIfExistsAsync(db, "tax_slabs", "cess_percent");
        await DropColumnIfExistsAsync(db, "tax_slabs", "surcharge_percent");
        await DropIndexIfExistsAsync(db, "tax_declaration_sections", "UX_tax_section_code");
        await EnsureIndexAsync(db, "tax_declaration_sections", "UX_tax_section_code_fy", "UNIQUE KEY UX_tax_section_code_fy (financial_year, code)");
        await EnsureIndexAsync(db, "employee_tax_declarations", "UX_employee_tax_declaration_section", "UNIQUE KEY UX_employee_tax_declaration_section (employee_id, financial_year, section_id)");
    }

    public async Task<TaxEngineSetup> GetAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        return new TaxEngineSetup
        {
            ClientSettings = (await db.QueryAsync<ClientTaxSetting>(@"SELECT s.id Id,s.client_id ClientId,COALESCE(c.Name,'') ClientName,s.enabled Enabled,s.financial_year FinancialYear,s.default_regime DefaultRegime,s.allow_employee_regime_selection AllowEmployeeRegimeSelection,COALESCE(reg.is_open,s.regime_selection_window_open) RegimeSelectionWindowOpen,COALESCE(reg.end_date,s.regime_selection_cutoff) RegimeSelectionCutoff,s.allow_declarations AllowDeclarations,COALESCE(it.is_open,s.planned_declaration_window_open) PlannedDeclarationWindowOpen,COALESCE(poi.is_open,s.actual_declaration_window_open) ActualDeclarationWindowOpen,s.declaration_window_start DeclarationWindowStart,s.declaration_window_end DeclarationWindowEnd,COALESCE(it.start_date,s.planned_declaration_start) PlannedDeclarationStart,COALESCE(it.end_date,s.planned_declaration_end) PlannedDeclarationEnd,COALESCE(poi.start_date,s.actual_declaration_start) ActualDeclarationStart,COALESCE(poi.end_date,s.actual_declaration_end) ActualDeclarationEnd,COALESCE(poi.processing_month,s.poi_processing_month) PoiProcessingMonth,s.reminder_emails_enabled ReminderEmailsEnabled,s.reminder_frequency ReminderFrequency,s.reminder_before_lock_days ReminderBeforeLockDays,s.require_proof_upload RequireProofUpload,s.require_approval RequireApproval,s.tax_deduction_component_code TaxDeductionComponentCode,s.project_monthly_tds ProjectMonthlyTds,s.lock_after_approval LockAfterApproval,s.active Active FROM tax_client_settings s LEFT JOIN clients c ON c.Id=s.client_id LEFT JOIN tax_activity_windows reg ON reg.client_id=s.client_id AND reg.financial_year=s.financial_year AND reg.activity_code='REGIME_SELECTION' LEFT JOIN tax_activity_windows it ON it.client_id=s.client_id AND it.financial_year=s.financial_year AND it.activity_code='IT_DECLARATION' LEFT JOIN tax_activity_windows poi ON poi.client_id=s.client_id AND poi.financial_year=s.financial_year AND poi.activity_code='POI' ORDER BY c.Name,s.financial_year DESC")).ToList(),
            FinancialYears = (await db.QueryAsync<TaxFinancialYear>(@"SELECT id Id,code Code,start_date StartDate,end_date EndDate,active Active,notes Notes FROM tax_financial_years ORDER BY code DESC")).ToList(),
            RuleVersions = (await db.QueryAsync<TaxRuleVersion>(@"SELECT id Id,financial_year_id FinancialYearId,financial_year FinancialYear,version_number VersionNumber,version_name VersionName,effective_from EffectiveFrom,effective_to EffectiveTo,is_published IsPublished,active Active,source_reference_id SourceReferenceId,source Source,notes Notes FROM tax_rule_versions ORDER BY financial_year DESC,version_number DESC")).ToList(),
            Regimes = (await db.QueryAsync<TaxRegimeMaster>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,code Code,name Name,is_default IsDefault,active Active,notes Notes FROM tax_regimes ORDER BY financial_year DESC,code")).ToList(),
            Slabs = (await db.QueryAsync<TaxSlab>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,regime Regime,income_from IncomeFrom,income_to IncomeTo,rate_percent RatePercent,effective_from EffectiveFrom,effective_to EffectiveTo,active Active,source Source,notes Notes FROM tax_slabs ORDER BY financial_year DESC,regime,income_from")).ToList(),
            Surcharges = (await db.QueryAsync<TaxSurcharge>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,income_from IncomeFrom,income_to IncomeTo,surcharge_percent SurchargePercent,active Active,source Source,notes Notes FROM tax_surcharges ORDER BY financial_year DESC,income_from")).ToList(),
            FinalAdjustments = (await db.QueryAsync<TaxFinalAdjustment>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,label Label,value_type ValueType,value Value,apply_order ApplyOrder,rule_type RuleType,active Active,source Source,notes Notes FROM tax_final_adjustments ORDER BY financial_year DESC,apply_order,label")).ToList(),
            DeclarationSections = (await db.QueryAsync<TaxDeclarationSection>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,code Code,name Name,category Category,regime Regime,limit_amount LimitAmount,proof_required ProofRequired,requires_approval RequiresApproval,active Active,source Source,notes Notes FROM tax_declaration_sections ORDER BY financial_year DESC,code")).ToList(),
            DeductionOptions = (await db.QueryAsync<TaxDeductionOption>(@"SELECT id Id,section_id SectionId,financial_year FinancialYear,code Code,name Name,limit_amount LimitAmount,proof_required ProofRequired,active Active,notes Notes FROM tax_deduction_options ORDER BY financial_year DESC,code")).ToList(),
            StandardDeductions = (await db.QueryAsync<TaxStandardDeduction>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,regime Regime,amount Amount,active Active,source Source,notes Notes FROM tax_standard_deductions ORDER BY financial_year DESC,regime")).ToList(),
            RebateRules = (await db.QueryAsync<TaxRebateRule>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,regime Regime,income_limit IncomeLimit,rebate_amount RebateAmount,active Active,source Source FROM tax_rebate_rules ORDER BY financial_year DESC,regime")).ToList(),
            ExemptionRules = (await db.QueryAsync<TaxExemptionRule>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,code Code,name Name,COALESCE(formula_json,'{}') FormulaJson,active Active,source Source FROM tax_exemption_rules ORDER BY financial_year DESC,code")).ToList(),
            HraRules = (await db.QueryAsync<TaxHraRule>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,is_applicable IsApplicable,metro_salary_percent MetroSalaryPercent,non_metro_salary_percent NonMetroSalaryPercent,rent_minus_basic_percent RentMinusBasicPercent,formula_type FormulaType,active Active,source Source FROM tax_hra_rules ORDER BY financial_year DESC")).ToList(),
            SourceReferences = (await db.QueryAsync<TaxRuleSourceReference>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,source_type SourceType,title Title,url Url,document_number DocumentNumber,published_date PublishedDate,effective_from EffectiveFrom,notes Notes,active Active FROM tax_rule_source_references ORDER BY financial_year DESC,source_type,title")).ToList(),
            AuditLogs = (await db.QueryAsync<TaxRuleAuditLog>(@"SELECT id Id,entity_name EntityName,entity_id EntityId,action Action,COALESCE(old_value_json,'') OldValueJson,COALESCE(new_value_json,'') NewValueJson,changed_by ChangedBy,changed_on ChangedOn,change_reason ChangeReason,financial_year_id FinancialYearId,tax_rule_version_id TaxRuleVersionId FROM tax_rule_audit_logs ORDER BY changed_on DESC,id DESC LIMIT 100")).ToList()
        };
    }

    public async Task<ClientTaxSetting> SaveClientSettingAsync(ClientTaxSetting row)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        row.DeclarationWindowStart = row.PlannedDeclarationStart ?? row.DeclarationWindowStart;
        row.DeclarationWindowEnd = row.PlannedDeclarationEnd ?? row.DeclarationWindowEnd;
        var id = row.Id == 0 ? await db.ExecuteScalarAsync<int>(@"INSERT INTO tax_client_settings (client_id,enabled,financial_year,default_regime,allow_employee_regime_selection,regime_selection_window_open,regime_selection_cutoff,allow_declarations,planned_declaration_window_open,actual_declaration_window_open,declaration_window_start,declaration_window_end,planned_declaration_start,planned_declaration_end,actual_declaration_start,actual_declaration_end,poi_processing_month,reminder_emails_enabled,reminder_frequency,reminder_before_lock_days,require_proof_upload,require_approval,tax_deduction_component_code,project_monthly_tds,lock_after_approval,active) VALUES (@ClientId,@Enabled,@FinancialYear,@DefaultRegime,@AllowEmployeeRegimeSelection,@RegimeSelectionWindowOpen,@RegimeSelectionCutoff,@AllowDeclarations,@PlannedDeclarationWindowOpen,@ActualDeclarationWindowOpen,@DeclarationWindowStart,@DeclarationWindowEnd,@PlannedDeclarationStart,@PlannedDeclarationEnd,@ActualDeclarationStart,@ActualDeclarationEnd,@PoiProcessingMonth,@ReminderEmailsEnabled,@ReminderFrequency,@ReminderBeforeLockDays,@RequireProofUpload,@RequireApproval,@TaxDeductionComponentCode,@ProjectMonthlyTds,@LockAfterApproval,@Active); SELECT LAST_INSERT_ID();", row)
            : row.Id;
        if (row.Id != 0) await db.ExecuteAsync(@"UPDATE tax_client_settings SET client_id=@ClientId,enabled=@Enabled,financial_year=@FinancialYear,default_regime=@DefaultRegime,allow_employee_regime_selection=@AllowEmployeeRegimeSelection,regime_selection_window_open=@RegimeSelectionWindowOpen,regime_selection_cutoff=@RegimeSelectionCutoff,allow_declarations=@AllowDeclarations,planned_declaration_window_open=@PlannedDeclarationWindowOpen,actual_declaration_window_open=@ActualDeclarationWindowOpen,declaration_window_start=@DeclarationWindowStart,declaration_window_end=@DeclarationWindowEnd,planned_declaration_start=@PlannedDeclarationStart,planned_declaration_end=@PlannedDeclarationEnd,actual_declaration_start=@ActualDeclarationStart,actual_declaration_end=@ActualDeclarationEnd,poi_processing_month=@PoiProcessingMonth,reminder_emails_enabled=@ReminderEmailsEnabled,reminder_frequency=@ReminderFrequency,reminder_before_lock_days=@ReminderBeforeLockDays,require_proof_upload=@RequireProofUpload,require_approval=@RequireApproval,tax_deduction_component_code=@TaxDeductionComponentCode,project_monthly_tds=@ProjectMonthlyTds,lock_after_approval=@LockAfterApproval,active=@Active WHERE id=@Id", row);
        var settingId = id;
        await UpsertActivityWindowAsync(db, settingId, row.ClientId, row.FinancialYear, "REGIME_SELECTION", row.RegimeSelectionWindowOpen, null, row.RegimeSelectionCutoff, row.RegimeSelectionCutoff, "", row.Active);
        await UpsertActivityWindowAsync(db, settingId, row.ClientId, row.FinancialYear, "IT_DECLARATION", row.PlannedDeclarationWindowOpen, row.PlannedDeclarationStart, row.PlannedDeclarationEnd, null, "", row.Active);
        await UpsertActivityWindowAsync(db, settingId, row.ClientId, row.FinancialYear, "POI", row.ActualDeclarationWindowOpen, row.ActualDeclarationStart, row.ActualDeclarationEnd, null, row.PoiProcessingMonth ?? "", row.Active);
        return (await GetAsync()).ClientSettings.First(x => x.Id == id);
    }

    public async Task<TaxSlab> SaveSlabAsync(TaxSlab row, int? changedBy = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var old = row.Id == 0 ? null : await db.QueryFirstOrDefaultAsync<TaxSlab>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,regime Regime,income_from IncomeFrom,income_to IncomeTo,rate_percent RatePercent,effective_from EffectiveFrom,effective_to EffectiveTo,active Active,source Source,notes Notes FROM tax_slabs WHERE id=@Id", new { row.Id });
        row.RuleVersionId = row.RuleVersionId == 0 ? await GetActiveRuleVersionIdAsync(db, row.FinancialYear) : row.RuleVersionId;
        var id = row.Id == 0 ? await db.ExecuteScalarAsync<int>(@"INSERT INTO tax_slabs (rule_version_id,financial_year,regime,income_from,income_to,rate_percent,effective_from,effective_to,active,source,notes) VALUES (@RuleVersionId,@FinancialYear,@Regime,@IncomeFrom,@IncomeTo,@RatePercent,@EffectiveFrom,@EffectiveTo,@Active,@Source,@Notes); SELECT LAST_INSERT_ID();", row) : row.Id;
        if (row.Id != 0) await db.ExecuteAsync(@"UPDATE tax_slabs SET rule_version_id=@RuleVersionId,financial_year=@FinancialYear,regime=@Regime,income_from=@IncomeFrom,income_to=@IncomeTo,rate_percent=@RatePercent,effective_from=@EffectiveFrom,effective_to=@EffectiveTo,active=@Active,source=@Source,notes=@Notes WHERE id=@Id", row);
        await AuditRuleChangeAsync(db, "TaxSlab", id, row.Id == 0 ? "Create" : "Update", old, row, changedBy, row.FinancialYear, row.RuleVersionId);
        return (await GetAsync()).Slabs.First(x => x.Id == id);
    }

    public async Task<TaxSurcharge> SaveSurchargeAsync(TaxSurcharge row, int? changedBy = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var old = row.Id == 0 ? null : await db.QueryFirstOrDefaultAsync<TaxSurcharge>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,income_from IncomeFrom,income_to IncomeTo,surcharge_percent SurchargePercent,active Active,source Source,notes Notes FROM tax_surcharges WHERE id=@Id", new { row.Id });
        row.RuleVersionId = row.RuleVersionId == 0 ? await GetActiveRuleVersionIdAsync(db, row.FinancialYear) : row.RuleVersionId;
        var id = row.Id == 0 ? await db.ExecuteScalarAsync<int>(@"INSERT INTO tax_surcharges (rule_version_id,financial_year,income_from,income_to,surcharge_percent,active,source,notes) VALUES (@RuleVersionId,@FinancialYear,@IncomeFrom,@IncomeTo,@SurchargePercent,@Active,@Source,@Notes); SELECT LAST_INSERT_ID();", row) : row.Id;
        if (row.Id != 0) await db.ExecuteAsync(@"UPDATE tax_surcharges SET rule_version_id=@RuleVersionId,financial_year=@FinancialYear,income_from=@IncomeFrom,income_to=@IncomeTo,surcharge_percent=@SurchargePercent,active=@Active,source=@Source,notes=@Notes WHERE id=@Id", row);
        await AuditRuleChangeAsync(db, "TaxSurchargeRule", id, row.Id == 0 ? "Create" : "Update", old, row, changedBy, row.FinancialYear, row.RuleVersionId);
        return (await GetAsync()).Surcharges.First(x => x.Id == id);
    }

    public async Task<TaxDeclarationSection> SaveSectionAsync(TaxDeclarationSection row, int? changedBy = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var old = row.Id == 0 ? null : await db.QueryFirstOrDefaultAsync<TaxDeclarationSection>(@"SELECT id Id,rule_version_id RuleVersionId,regime_id RegimeId,financial_year FinancialYear,code Code,name Name,category Category,regime Regime,limit_amount LimitAmount,proof_required ProofRequired,requires_approval RequiresApproval,active Active,source Source,notes Notes FROM tax_declaration_sections WHERE id=@Id", new { row.Id });
        row.RuleVersionId = row.RuleVersionId == 0 ? await GetActiveRuleVersionIdAsync(db, row.FinancialYear) : row.RuleVersionId;
        var id = row.Id == 0 ? await db.ExecuteScalarAsync<int>(@"INSERT INTO tax_declaration_sections (rule_version_id,financial_year,code,name,category,regime,limit_amount,proof_required,requires_approval,active,source,notes) VALUES (@RuleVersionId,@FinancialYear,@Code,@Name,@Category,@Regime,@LimitAmount,@ProofRequired,@RequiresApproval,@Active,@Source,@Notes); SELECT LAST_INSERT_ID();", row) : row.Id;
        if (row.Id != 0) await db.ExecuteAsync(@"UPDATE tax_declaration_sections SET rule_version_id=@RuleVersionId,financial_year=@FinancialYear,code=@Code,name=@Name,category=@Category,regime=@Regime,limit_amount=@LimitAmount,proof_required=@ProofRequired,requires_approval=@RequiresApproval,active=@Active,source=@Source,notes=@Notes WHERE id=@Id", row);
        await AuditRuleChangeAsync(db, "TaxDeductionSection", id, row.Id == 0 ? "Create" : "Update", old, row, changedBy, row.FinancialYear, row.RuleVersionId);
        return (await GetAsync()).DeclarationSections.First(x => x.Id == id);
    }

    public async Task<TaxFinalAdjustment> SaveFinalAdjustmentAsync(TaxFinalAdjustment row, int? changedBy = null)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var old = row.Id == 0 ? null : await db.QueryFirstOrDefaultAsync<TaxFinalAdjustment>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,label Label,value_type ValueType,value Value,apply_order ApplyOrder,rule_type RuleType,active Active,source Source,notes Notes FROM tax_final_adjustments WHERE id=@Id", new { row.Id });
        row.RuleVersionId = row.RuleVersionId == 0 ? await GetActiveRuleVersionIdAsync(db, row.FinancialYear) : row.RuleVersionId;
        row.RuleType = string.IsNullOrWhiteSpace(row.RuleType) ? "FinalAdjustment" : row.RuleType;
        var id = row.Id == 0 ? await db.ExecuteScalarAsync<int>(@"INSERT INTO tax_final_adjustments (rule_version_id,financial_year,label,value_type,value,apply_order,rule_type,active,source,notes) VALUES (@RuleVersionId,@FinancialYear,@Label,@ValueType,@Value,@ApplyOrder,@RuleType,@Active,@Source,@Notes); SELECT LAST_INSERT_ID();", row) : row.Id;
        if (row.Id != 0) await db.ExecuteAsync(@"UPDATE tax_final_adjustments SET rule_version_id=@RuleVersionId,financial_year=@FinancialYear,label=@Label,value_type=@ValueType,value=@Value,apply_order=@ApplyOrder,rule_type=@RuleType,active=@Active,source=@Source,notes=@Notes WHERE id=@Id", row);
        await AuditRuleChangeAsync(db, row.RuleType == "Cess" ? "TaxCessRule" : "TaxFinalAdjustmentRule", id, row.Id == 0 ? "Create" : "Update", old, row, changedBy, row.FinancialYear, row.RuleVersionId);
        return (await GetAsync()).FinalAdjustments.First(x => x.Id == id);
    }

    public async Task<TaxComputationResult?> ComputeAsync(TaxComputationRequest request)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var fy = string.IsNullOrWhiteSpace(request.FinancialYear) ? CurrentFinancialYear() : request.FinancialYear;
        var version = await db.QueryFirstOrDefaultAsync<TaxRuleVersion>(@"SELECT id Id,financial_year FinancialYear,version_number VersionNumber,effective_from EffectiveFrom,effective_to EffectiveTo,active Active,source Source,notes Notes FROM tax_rule_versions WHERE financial_year=@Fy AND active=TRUE ORDER BY effective_from DESC,id DESC LIMIT 1", new { Fy = fy });
        if (version is null) return null;
        var selectedRegime = await db.ExecuteScalarAsync<string?>(@"SELECT regime FROM employee_tax_regime_selections WHERE employee_id=@EmployeeId AND financial_year=@Fy", new { request.EmployeeId, Fy = fy });
        var companyDefault = await db.ExecuteScalarAsync<string?>(@"SELECT default_regime FROM tax_client_settings WHERE client_id=@ClientId AND financial_year=@Fy AND active=TRUE", new { request.ClientId, Fy = fy });
        var regime = string.IsNullOrWhiteSpace(selectedRegime) ? companyDefault ?? "New" : selectedRegime;
        var standardDeduction = await db.ExecuteScalarAsync<decimal?>(@"SELECT amount FROM tax_standard_deductions WHERE financial_year=@Fy AND rule_version_id=@RuleVersionId AND active=TRUE AND (regime=@Regime OR regime='Both') ORDER BY CASE WHEN regime=@Regime THEN 0 ELSE 1 END LIMIT 1", new { Fy = fy, RuleVersionId = version.Id, Regime = regime }) ?? 0;
        var approvedDeductions = regime == "Old" ? await db.ExecuteScalarAsync<decimal?>(@"SELECT SUM(l.approved_amount) FROM employee_tax_declaration_headers h JOIN employee_tax_declaration_lines l ON l.header_id=h.id WHERE h.employee_id=@EmployeeId AND h.financial_year=@Fy AND h.activity_code='POI' AND h.status IN ('Approved','Submitted')", new { request.EmployeeId, Fy = fy }) ?? 0 : 0;
        var taxableIncome = Math.Max(0, request.AnnualGrossSalary - standardDeduction - approvedDeductions);
        var slabs = (await db.QueryAsync<TaxSlab>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,regime Regime,income_from IncomeFrom,income_to IncomeTo,rate_percent RatePercent,effective_from EffectiveFrom,effective_to EffectiveTo,active Active,source Source,notes Notes FROM tax_slabs WHERE financial_year=@Fy AND rule_version_id=@RuleVersionId AND regime=@Regime AND active=TRUE ORDER BY income_from", new { Fy = fy, RuleVersionId = version.Id, Regime = regime })).ToList();
        var slabTax = slabs.Sum(slab => TaxForSlab(taxableIncome, slab));
        var rebateRule = await db.QueryFirstOrDefaultAsync<TaxRebateRule>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,regime Regime,income_limit IncomeLimit,rebate_amount RebateAmount,active Active,source Source FROM tax_rebate_rules WHERE financial_year=@Fy AND rule_version_id=@RuleVersionId AND active=TRUE AND (regime=@Regime OR regime='Both') ORDER BY CASE WHEN regime=@Regime THEN 0 ELSE 1 END LIMIT 1", new { Fy = fy, RuleVersionId = version.Id, Regime = regime });
        var rebate = rebateRule is not null && taxableIncome <= rebateRule.IncomeLimit ? Math.Min(slabTax, rebateRule.RebateAmount) : 0;
        var taxAfterRebate = Math.Max(0, slabTax - rebate);
        var surchargeRule = await db.QueryFirstOrDefaultAsync<TaxSurcharge>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,income_from IncomeFrom,income_to IncomeTo,surcharge_percent SurchargePercent,active Active,source Source,notes Notes FROM tax_surcharges WHERE financial_year=@Fy AND rule_version_id=@RuleVersionId AND active=TRUE AND @Income>=income_from AND (income_to IS NULL OR @Income<=income_to) ORDER BY income_from DESC LIMIT 1", new { Fy = fy, RuleVersionId = version.Id, Income = taxableIncome });
        var surcharge = surchargeRule is null ? 0 : taxAfterRebate * surchargeRule.SurchargePercent / 100m;
        var adjustments = (await db.QueryAsync<TaxFinalAdjustment>(@"SELECT id Id,rule_version_id RuleVersionId,financial_year FinancialYear,label Label,value_type ValueType,value Value,apply_order ApplyOrder,active Active,source Source,notes Notes FROM tax_final_adjustments WHERE financial_year=@Fy AND rule_version_id=@RuleVersionId AND active=TRUE ORDER BY apply_order,label", new { Fy = fy, RuleVersionId = version.Id })).ToList();
        var cess = adjustments.Sum(item => item.ValueType == "Percent" ? (taxAfterRebate + surcharge) * item.Value / 100m : item.Value);
        var annualTax = Math.Round(taxAfterRebate + surcharge + cess, 0, MidpointRounding.AwayFromZero);
        var remaining = Math.Max(0, annualTax - request.TdsAlreadyDeducted);
        var monthly = Math.Round(remaining / 12m, 0, MidpointRounding.AwayFromZero);
        var result = new TaxComputationResult { FinancialYear = fy, RuleVersionId = version.Id, Regime = regime, GrossSalary = request.AnnualGrossSalary, StandardDeduction = standardDeduction, ApprovedDeductions = approvedDeductions, TaxableIncome = taxableIncome, SlabTax = slabTax, Rebate = rebate, Surcharge = surcharge, Cess = cess, AnnualTax = annualTax, TdsAlreadyDeducted = request.TdsAlreadyDeducted, RemainingTax = remaining, MonthlyTds = monthly };
        result.SnapshotJson = System.Text.Json.JsonSerializer.Serialize(new { request, result, ruleVersion = version, slabs, rebateRule, surchargeRule, adjustments, calculatedAt = DateTime.UtcNow });
        await EnsureSnapshotColumnsAsync(db);
        var financialYearId = await db.ExecuteScalarAsync<int?>("SELECT id FROM tax_financial_years WHERE code=@Fy", new { Fy = fy });
        var regimeId = await db.ExecuteScalarAsync<int?>("SELECT id FROM tax_regimes WHERE financial_year=@Fy AND rule_version_id=@RuleVersionId AND code=@Regime", new { Fy = fy, RuleVersionId = version.Id, Regime = regime });
        var ruleBreakup = System.Text.Json.JsonSerializer.Serialize(new { ruleVersion = version, slabs, rebateRule, surchargeRule, adjustments });
        await db.ExecuteAsync(@"INSERT INTO tax_computation_snapshots (employee_id,client_id,financial_year,financial_year_id,pay_period,pay_run_id,rule_version_id,regime_id,regime,gross_salary,exemptions_amount,deductions_amount,taxable_income,tax_before_rebate,rebate_amount,tax_after_rebate,surcharge_amount,cess_amount,total_annual_tax,tds_deducted_till_date,remaining_tax,annual_tax,monthly_tds,snapshot_json,rule_breakup_json,declaration_json,proof_json)
VALUES (@EmployeeId,@ClientId,@FinancialYear,@FinancialYearId,@PayPeriod,@PayRunId,@RuleVersionId,@RegimeId,@Regime,@GrossSalary,0,@ApprovedDeductions,@TaxableIncome,@SlabTax,@Rebate,@TaxAfterRebate,@Surcharge,@Cess,@AnnualTax,@TdsAlreadyDeducted,@RemainingTax,@AnnualTax,@MonthlyTds,@SnapshotJson,@RuleBreakupJson,@DeclarationJson,@ProofJson)",
            new { request.EmployeeId, request.ClientId, result.FinancialYear, FinancialYearId = financialYearId, request.PayPeriod, request.PayRunId, result.RuleVersionId, RegimeId = regimeId, result.Regime, result.GrossSalary, result.ApprovedDeductions, result.TaxableIncome, result.SlabTax, result.Rebate, TaxAfterRebate = taxAfterRebate, result.Surcharge, result.Cess, result.AnnualTax, result.TdsAlreadyDeducted, result.RemainingTax, result.MonthlyTds, result.SnapshotJson, RuleBreakupJson = ruleBreakup, DeclarationJson = "{}", ProofJson = "{}" });
        return result;
    }

    public async Task DeleteAsync(string kind, int id)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        var table = kind switch { "client-settings" => "tax_client_settings", "slabs" => "tax_slabs", "surcharges" => "tax_surcharges", "final-adjustments" => "tax_final_adjustments", "sections" => "tax_declaration_sections", _ => "" };
        if (table != "") await db.ExecuteAsync($"DELETE FROM {table} WHERE id=@Id", new { Id = id });
    }

    private static async Task EnsureColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table AND COLUMN_NAME=@Column", new { Table = table, Column = column });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {definition}");
    }

    private static async Task EnsureSnapshotColumnsAsync(MySqlConnection db)
    {
        await EnsureColumnAsync(db, "tax_computation_snapshots", "financial_year_id", "financial_year_id INT NULL AFTER financial_year");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "pay_run_id", "pay_run_id INT NULL AFTER pay_period");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "regime_id", "regime_id INT NULL AFTER rule_version_id");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "exemptions_amount", "exemptions_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER gross_salary");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "deductions_amount", "deductions_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER exemptions_amount");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "tax_before_rebate", "tax_before_rebate DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER taxable_income");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "rebate_amount", "rebate_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER tax_before_rebate");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "tax_after_rebate", "tax_after_rebate DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER rebate_amount");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "surcharge_amount", "surcharge_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER tax_after_rebate");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "cess_amount", "cess_amount DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER surcharge_amount");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "total_annual_tax", "total_annual_tax DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER cess_amount");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "tds_deducted_till_date", "tds_deducted_till_date DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER total_annual_tax");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "remaining_tax", "remaining_tax DECIMAL(14,2) NOT NULL DEFAULT 0 AFTER tds_deducted_till_date");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "rule_breakup_json", "rule_breakup_json JSON NULL AFTER snapshot_json");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "declaration_json", "declaration_json JSON NULL AFTER rule_breakup_json");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "proof_json", "proof_json JSON NULL AFTER declaration_json");
        await EnsureColumnAsync(db, "tax_computation_snapshots", "calculated_by", "calculated_by INT NULL AFTER proof_json");
        await EnsureIndexAsync(db, "tax_computation_snapshots", "IX_tax_snapshots_pay_run", "INDEX IX_tax_snapshots_pay_run (pay_run_id, employee_id)");
    }

    private static async Task AuditRuleChangeAsync(MySqlConnection db, string entityName, long entityId, string action, object? oldValue, object newValue, int? changedBy, string financialYear, int ruleVersionId)
    {
        var financialYearId = await db.ExecuteScalarAsync<int?>("SELECT id FROM tax_financial_years WHERE code=@FinancialYear", new { FinancialYear = financialYear });
        await db.ExecuteAsync(@"INSERT INTO tax_rule_audit_logs (entity_name,entity_id,action,old_value_json,new_value_json,changed_by,change_reason,financial_year_id,tax_rule_version_id)
VALUES (@EntityName,@EntityId,@Action,@OldValueJson,@NewValueJson,@ChangedBy,@ChangeReason,@FinancialYearId,@TaxRuleVersionId)",
            new
            {
                EntityName = entityName,
                EntityId = entityId,
                Action = action,
                OldValueJson = oldValue is null ? null : System.Text.Json.JsonSerializer.Serialize(oldValue),
                NewValueJson = System.Text.Json.JsonSerializer.Serialize(newValue),
                ChangedBy = changedBy,
                ChangeReason = "Statutory tax rule maintenance",
                FinancialYearId = financialYearId,
                TaxRuleVersionId = ruleVersionId
            });
    }

    private static string CurrentFinancialYear()
    {
        var today = DateTime.Today;
        var startYear = today.Month >= 4 ? today.Year : today.Year - 1;
        return $"{startYear}-{(startYear + 1) % 100:00}";
    }

    private static (DateTime Start, DateTime End) FinancialYearRange(string fy)
    {
        var year = int.TryParse((fy ?? "").Split('-').FirstOrDefault(), out var parsed) ? parsed : DateTime.Today.Year;
        return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
    }

    private static decimal TaxForSlab(decimal income, TaxSlab slab)
    {
        if (income <= slab.IncomeFrom) return 0;
        var taxable = Math.Min(income, slab.IncomeTo ?? income) - slab.IncomeFrom;
        return Math.Max(0, taxable) * slab.RatePercent / 100m;
    }

    private static async Task<int> GetActiveRuleVersionIdAsync(MySqlConnection db, string financialYear)
    {
        var fy = string.IsNullOrWhiteSpace(financialYear) ? CurrentFinancialYear() : financialYear;
        var id = await db.ExecuteScalarAsync<int?>(@"SELECT id FROM tax_rule_versions WHERE financial_year=@Fy AND active=TRUE ORDER BY effective_from DESC,id DESC LIMIT 1", new { Fy = fy });
        if (id.HasValue) return id.Value;
        return 0;
    }

    private static Task UpsertActivityWindowAsync(MySqlConnection db, int settingId, int clientId, string financialYear, string activityCode, bool isOpen, DateTime? startDate, DateTime? endDate, DateTime? cutoffDate, string processingMonth, bool active) =>
        db.ExecuteAsync(@"INSERT INTO tax_activity_windows (client_setting_id,client_id,financial_year,activity_code,is_open,start_date,end_date,cutoff_date,processing_month,active)
VALUES (@SettingId,@ClientId,@FinancialYear,@ActivityCode,@IsOpen,@StartDate,@EndDate,@CutoffDate,@ProcessingMonth,@Active)
ON DUPLICATE KEY UPDATE client_setting_id=@SettingId,is_open=@IsOpen,start_date=@StartDate,end_date=@EndDate,cutoff_date=@CutoffDate,processing_month=@ProcessingMonth,active=@Active",
            new { SettingId = settingId, ClientId = clientId, FinancialYear = financialYear, ActivityCode = activityCode, IsOpen = isOpen, StartDate = startDate, EndDate = endDate, CutoffDate = cutoffDate, ProcessingMonth = processingMonth ?? "", Active = active });

    private static async Task DropColumnIfExistsAsync(MySqlConnection db, string table, string column)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table AND COLUMN_NAME=@Column", new { Table = table, Column = column });
        if (exists > 0) await db.ExecuteAsync($"ALTER TABLE {table} DROP COLUMN {column}");
    }

    private static async Task DropIndexIfExistsAsync(MySqlConnection db, string table, string index)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table AND INDEX_NAME=@Index", new { Table = table, Index = index });
        if (exists > 0) await db.ExecuteAsync($"ALTER TABLE {table} DROP INDEX {index}");
    }

    private static async Task EnsureIndexAsync(MySqlConnection db, string table, string index, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table AND INDEX_NAME=@Index", new { Table = table, Index = index });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE {table} ADD {definition}");
    }
}
