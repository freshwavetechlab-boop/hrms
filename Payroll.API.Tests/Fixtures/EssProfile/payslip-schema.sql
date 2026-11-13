-- Only used after the isolated ESS profile schema, never as an application migration.
CREATE TABLE IF NOT EXISTS employeesalarycomponents(EmployeeId INT,ComponentId VARCHAR(100),Amount DECIMAL(18,4));
CREATE TABLE IF NOT EXISTS employee_it0000_actions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,EmployeeId INT,ClientId INT,ActionType VARCHAR(80),EffectiveFrom DATE,Status VARCHAR(30));
CREATE TABLE IF NOT EXISTS excel_payslip_batches(client_id INT,id CHAR(32),batch_json JSON,send_state_json JSON,created_at DATETIME,PRIMARY KEY(client_id,id));
CREATE TABLE IF NOT EXISTS excel_payslip_calculation_sources(client_id INT,batch_id CHAR(32),source_json JSON,created_at DATETIME,PRIMARY KEY(client_id,batch_id));
