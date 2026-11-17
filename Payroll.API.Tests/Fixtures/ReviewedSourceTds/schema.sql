-- Synthetic loopback fixture only; its hostname/database/sentinel are checked
-- before this additive bootstrap. This file is not an application migration.
CREATE TABLE IF NOT EXISTS employee_tax_regime_selections(employee_id INT,client_id INT,financial_year VARCHAR(10),regime VARCHAR(20),status VARCHAR(30),PRIMARY KEY(employee_id,financial_year));
CREATE TABLE IF NOT EXISTS tax_financial_years(id INT PRIMARY KEY AUTO_INCREMENT,code VARCHAR(10));
CREATE TABLE IF NOT EXISTS tax_regimes(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),rule_version_id INT,code VARCHAR(20));
CREATE TABLE IF NOT EXISTS tax_standard_deductions(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),rule_version_id INT,regime VARCHAR(20),amount DECIMAL(14,2),active BOOLEAN DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS tax_slabs(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),rule_version_id INT,regime VARCHAR(20),income_from DECIMAL(14,2),income_to DECIMAL(14,2) NULL,rate_percent DECIMAL(6,2),effective_from DATE,effective_to DATE NULL,active BOOLEAN DEFAULT TRUE,source VARCHAR(250),notes VARCHAR(1000));
CREATE TABLE IF NOT EXISTS tax_rebate_rules(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),rule_version_id INT,regime VARCHAR(20),income_limit DECIMAL(14,2),rebate_amount DECIMAL(14,2),active BOOLEAN DEFAULT TRUE,source VARCHAR(250));
CREATE TABLE IF NOT EXISTS tax_surcharges(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),rule_version_id INT,income_from DECIMAL(14,2),income_to DECIMAL(14,2) NULL,surcharge_percent DECIMAL(6,2),active BOOLEAN DEFAULT TRUE,source VARCHAR(250),notes VARCHAR(1000));
CREATE TABLE IF NOT EXISTS tax_final_adjustments(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),rule_version_id INT,label VARCHAR(120),value_type VARCHAR(20),value DECIMAL(14,4),apply_order INT,active BOOLEAN DEFAULT TRUE,source VARCHAR(250),notes VARCHAR(1000));
CREATE TABLE IF NOT EXISTS tax_computation_snapshots (
id BIGINT PRIMARY KEY AUTO_INCREMENT,employee_id INT,client_id INT DEFAULT 0,financial_year VARCHAR(10) DEFAULT '',pay_period VARCHAR(7) DEFAULT '',pay_run_id INT NULL,
financial_year_id INT NULL,rule_version_id INT DEFAULT 0,regime_id INT NULL,regime VARCHAR(20) DEFAULT '',gross_salary DECIMAL(14,2) DEFAULT 0,exemptions_amount DECIMAL(14,2) DEFAULT 0,deductions_amount DECIMAL(14,2) DEFAULT 0,taxable_income DECIMAL(14,2) DEFAULT 0,
tax_before_rebate DECIMAL(14,2) DEFAULT 0,rebate_amount DECIMAL(14,2) DEFAULT 0,tax_after_rebate DECIMAL(14,2) DEFAULT 0,surcharge_amount DECIMAL(14,2) DEFAULT 0,cess_amount DECIMAL(14,2) DEFAULT 0,total_annual_tax DECIMAL(14,2) DEFAULT 0,
tds_deducted_till_date DECIMAL(14,2) DEFAULT 0,remaining_tax DECIMAL(14,2) DEFAULT 0,annual_tax DECIMAL(14,2) DEFAULT 0,monthly_tds DECIMAL(14,2) DEFAULT 0,snapshot_json JSON NULL,rule_breakup_json JSON NULL,declaration_json JSON NULL,proof_json JSON NULL,calculated_by INT NULL,created_at DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS payrunemployeelines(Id INT PRIMARY KEY AUTO_INCREMENT,PayRunEmployeeId INT,PayRunId INT,EmployeeId INT,ComponentCode VARCHAR(80),StatutoryType VARCHAR(80),Amount DECIMAL(18,2));
