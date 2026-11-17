-- Explicit operator migration, never an automatic startup migration.
-- Prerequisites: compatible API AND payroll-worker readers, complete backups,
-- no active payroll writers/transactions, and a reviewed recovery plan.
-- Preserve all existing values and the monthly legacy DEFAULT 0.
-- Each ALTER commits independently; inspect actual schema after any interruption.
-- LOCK=NONE refuses a less-concurrent fallback. Keep the metadata-lock wait bounded.
-- MySQL 8.4 reference: https://dev.mysql.com/doc/refman/8.4/en/innodb-online-ddl-operations.html
SET SESSION lock_wait_timeout = 5;
ALTER TABLE employee_monthly_attendance
    MODIFY COLUMN present_days DECIMAL(5,2) NULL DEFAULT 0,
    ALGORITHM=INPLACE, LOCK=NONE;
ALTER TABLE payrunemployees
    MODIFY COLUMN PresentDays DECIMAL(5,2) NULL,
    ALGORITHM=INPLACE, LOCK=NONE;
