-- Explicit, additive schema only. No payroll masters, employee data, attendance,
-- leave quotas, holidays, notifications, or pay runs are created by this migration.
CREATE TABLE IF NOT EXISTS excel_payroll_setup_imports (
    id CHAR(32) PRIMARY KEY,
    client_id INT NOT NULL,
    batch_id CHAR(32) NOT NULL,
    request_id VARCHAR(64) NOT NULL,
    selection_hash CHAR(64) NOT NULL,
    request_hash CHAR(64) NOT NULL,
    plan_hash VARCHAR(64) NOT NULL DEFAULT '',
    status VARCHAR(20) NOT NULL DEFAULT 'Draft',
    request_json JSON NOT NULL,
    result_json JSON NOT NULL,
    created_by VARCHAR(190) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY ux_excel_payroll_setup_request(client_id, request_id),
    UNIQUE KEY ux_excel_payroll_setup_selection(client_id, batch_id, selection_hash)
);
