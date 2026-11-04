-- Additive input/receipt storage only. No salary formula, source batch, attendance
-- history or saved payroll is migrated or rewritten. Apply through deployment.
CREATE TABLE IF NOT EXISTS payroll_location_attendance_inputs (
    client_id INT NOT NULL,
    pay_period CHAR(7) NOT NULL,
    employee_id INT NOT NULL,
    part_key VARCHAR(100) NOT NULL,
    work_location_id INT NOT NULL,
    payroll_days INT NOT NULL,
    present_days DECIMAL(8,4) NOT NULL,
    payable_days DECIMAL(8,4) NOT NULL,
    receipt_id CHAR(32) NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (client_id, pay_period, employee_id, part_key),
    CONSTRAINT ck_native_location_days CHECK (payroll_days BETWEEN 1 AND 31 AND present_days BETWEEN 0 AND payroll_days AND payable_days BETWEEN 0 AND payroll_days)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS payroll_location_attendance_imports (
    id CHAR(32) NOT NULL,
    client_id INT NOT NULL,
    pay_period CHAR(7) NOT NULL,
    input_hash CHAR(64) NOT NULL,
    request_json JSON NOT NULL,
    result_json JSON NOT NULL,
    created_by VARCHAR(190) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    KEY ix_native_location_receipts (client_id, pay_period, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
