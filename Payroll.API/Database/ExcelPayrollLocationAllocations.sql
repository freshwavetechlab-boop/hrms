-- Additive schema plan for reviewed Settings-backed location salary assignments.
-- Apply only through an explicitly approved migration. Preview and Apply never
-- execute this DDL. Configuration dates are audit/version dates, not fabricated
-- calendar ranges for source-month attendance parts.

CREATE TABLE IF NOT EXISTS employee_payroll_location_assignments (
    id CHAR(32) NOT NULL,
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    work_location_id INT NOT NULL,
    work_state VARCHAR(100) NOT NULL,
    salary_structure_id VARCHAR(40) NOT NULL,
    annual_ctc DECIMAL(18,4) NOT NULL,
    salary_inputs_json JSON NOT NULL,
    component_scopes_json JSON NOT NULL,
    rules_hash CHAR(64) NOT NULL,
    source_hash CHAR(64) NOT NULL,
    configuration_effective_from DATE NOT NULL,
    setup_import_id CHAR(32) NOT NULL,
    source_batch_id CHAR(32) NOT NULL,
    source_row_id VARCHAR(100) NOT NULL,
    created_by VARCHAR(190) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Active',
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    UNIQUE KEY ux_location_assignment_owner (client_id, employee_id, id),
    KEY ix_location_assignment_configuration (client_id, employee_id, configuration_effective_from, id),
    KEY ix_location_assignment_import (client_id, setup_import_id),
    CONSTRAINT ck_location_assignment_annual_ctc CHECK (annual_ctc >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS employee_payroll_location_allocations (
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    pay_period CHAR(7) NOT NULL,
    source_part_key VARCHAR(100) NOT NULL,
    assignment_id CHAR(32) NOT NULL,
    payroll_days INT NOT NULL,
    present_days DECIMAL(8,4) NOT NULL,
    payable_days DECIMAL(8,4) NOT NULL,
    reference_assignment_id CHAR(32) NULL,
    setup_import_id CHAR(32) NOT NULL,
    source_batch_id CHAR(32) NOT NULL,
    source_row_id VARCHAR(100) NOT NULL,
    source_hash CHAR(64) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (client_id, employee_id, pay_period, source_part_key),
    KEY ix_location_allocation_assignment (client_id, employee_id, assignment_id),
    KEY ix_location_allocation_reference (client_id, employee_id, reference_assignment_id),
    KEY ix_location_allocation_import (client_id, setup_import_id),
    CONSTRAINT fk_location_allocation_assignment
        FOREIGN KEY (client_id, employee_id, assignment_id)
        REFERENCES employee_payroll_location_assignments (client_id, employee_id, id),
    CONSTRAINT fk_location_allocation_reference
        FOREIGN KEY (client_id, employee_id, reference_assignment_id)
        REFERENCES employee_payroll_location_assignments (client_id, employee_id, id),
    CONSTRAINT ck_location_allocation_days
        CHECK (payroll_days BETWEEN 1 AND 31
            AND present_days BETWEEN 0 AND payroll_days
            AND payable_days BETWEEN 0 AND payroll_days)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
