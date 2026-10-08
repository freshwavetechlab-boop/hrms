-- Used by --migrate-excel-payslips. The migrator also transfers existing batch snapshots.
-- Contains source values and delivery metadata only; generated PDFs are never persisted.
CREATE TABLE IF NOT EXISTS excel_payslip_batches (
    client_id INT NOT NULL,
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    batch_json JSON NOT NULL,
    send_state_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (client_id, id),
    INDEX ix_excel_payslip_batches_created (client_id, created_at)
) ENGINE=InnoDB;

-- Independent Excel calculation inputs/formulas, one immutable source per saved batch.
CREATE TABLE IF NOT EXISTS excel_payslip_calculation_sources (
    client_id INT NOT NULL,
    batch_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    source_json JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (client_id, batch_id)
) ENGINE=InnoDB;
