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

-- Original bytes use the existing entity_attachments/global storage service.
-- Apply only after attachment schema initialization. No existing batches are backfilled with invented originals.
CREATE TABLE IF NOT EXISTS excel_payslip_originals (
    client_id INT NOT NULL, id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    attachment_id BIGINT NOT NULL, sha256_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY(client_id,id), UNIQUE KEY ux_excel_original_hash(client_id,sha256_hash),
    UNIQUE KEY ux_excel_original_attachment(attachment_id)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS excel_payslip_batch_originals (
    client_id INT NOT NULL, batch_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    original_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, original_file_name VARCHAR(255) NOT NULL,
    PRIMARY KEY(client_id,batch_id,original_id), INDEX ix_excel_original_refs(client_id,original_id)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS excel_payslip_deletions (
    client_id INT NOT NULL, batch_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    preview_token CHAR(64) NOT NULL, actor VARCHAR(190) NOT NULL, summary_json JSON NOT NULL,
    deleted_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6), PRIMARY KEY(client_id,batch_id)
) ENGINE=InnoDB;
INSERT IGNORE INTO attachment_attributes(client_id,attribute_code,attribute_name,data_classification)
VALUES(0,'EXCEL_WORKBOOK','Original payroll workbook','Confidential');
INSERT IGNORE INTO attachment_field_configurations(client_id,attachment_attribute_id,module_code,form_code,field_key,field_label,
allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes)
SELECT 0,id,'PAYROLL','EXCEL_PAYSLIP_ORIGINAL','originalWorkbook','Original payroll workbook',
'["xlsx","xlsm","xls","csv"]','["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","application/vnd.ms-excel.sheet.macroEnabled.12","application/vnd.ms-excel","text/csv"]',26214400
FROM attachment_attributes WHERE client_id=0 AND attribute_code='EXCEL_WORKBOOK';

-- Explicit, additive cleanup review/audit only. No startup cleanup or automatic legacy reclassification.
CREATE TABLE IF NOT EXISTS excel_payslip_cleanup_actions (
    client_id INT NOT NULL, batch_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    request_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, request_hash CHAR(64) NOT NULL,
    action_code VARCHAR(40) NOT NULL, object_id VARCHAR(80) NOT NULL DEFAULT '', batch_hash CHAR(64) NOT NULL,
    keeper_id VARCHAR(32) NOT NULL DEFAULT '', keeper_hash VARCHAR(64) NOT NULL DEFAULT '', proof_json JSON NOT NULL,
    actor VARCHAR(190) NOT NULL, created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY(client_id,request_id), INDEX ix_excel_cleanup_batch(client_id,batch_id,created_at_utc)
) ENGINE=InnoDB;
