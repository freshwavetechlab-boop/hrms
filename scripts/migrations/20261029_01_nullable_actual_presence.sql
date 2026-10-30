-- Version: 20261029_01. Reviewed payable-only attendance: unknown is NULL, not 0.
-- Execute only through the owner's reviewed schema-migration workflow after
-- target/backup/preflight. This file is a plan; application startup stays disabled.
-- Additive value-domain widening only: no row updates, drops, deletes or backfill.
-- Known values, including actual zero, remain unchanged. Preserve the legacy
-- monthly default; reviewed payable-only imports always insert explicit NULL.
-- REQUIRED BEFORE EITHER ALTER:
-- * Confirm the exact production database/version and completed recoverable
--   backup artifact, not merely a scheduled-backup setting.
-- * Verify every relevant reader/worker is compatible with NULL actual presence.
--   An older remote worker may coalesce NULL to zero even with local workers off.
-- * Pause the intended table writers under the owner's reviewed maintenance plan.
--   INPLACE rebuild + SHARED locking blocks writes; CASCADE foreign keys prevent
--   assuming LOCK=NONE. Never disable foreign-key checks or relax locks as fallback.
-- * Inspect metadata-lock/blocking transactions and expected rebuild duration.
-- This is TWO independently committed DDL steps, not one atomic transaction.
-- Execute one stage, verify it, and stop on ANY unexpected result before stage 2.

SELECT DATABASE() AS target_database;
SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT
FROM information_schema.columns
WHERE TABLE_SCHEMA=DATABASE()
  AND ((TABLE_NAME='employee_monthly_attendance' AND COLUMN_NAME='present_days')
    OR (TABLE_NAME='payrunemployees' AND COLUMN_NAME='PresentDays'));
SELECT COUNT(*) AS monthly_rows,COUNT(present_days) AS known_actual_presence,
       SUM(present_days IS NULL) AS unknown_actual_presence,
       SUM(present_days) AS known_presence_sum
FROM employee_monthly_attendance;
SELECT COUNT(*) AS payroll_employee_rows,COUNT(PresentDays) AS known_actual_presence,
       SUM(PresentDays IS NULL) AS unknown_actual_presence,
       SUM(PresentDays) AS known_presence_sum
FROM payrunemployees;

ALTER TABLE employee_monthly_attendance
    MODIFY COLUMN present_days DECIMAL(5,2) NULL DEFAULT 0,
    ALGORITHM=INPLACE, LOCK=SHARED;

-- STAGE 1 VERIFICATION: compare with the captured baseline before stage 2.
-- Counts/sums are aggregate checks, not a substitute for backup/row integrity proof.
SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT
FROM information_schema.columns
WHERE TABLE_SCHEMA=DATABASE()
  AND TABLE_NAME='employee_monthly_attendance' AND COLUMN_NAME='present_days';
SELECT COUNT(*) AS monthly_rows,COUNT(present_days) AS known_actual_presence,
       SUM(present_days IS NULL) AS unknown_actual_presence,
       SUM(present_days) AS known_presence_sum
FROM employee_monthly_attendance;

-- STOP here unless stage 1 matches the intended definition and all baseline data.
ALTER TABLE payrunemployees
    MODIFY COLUMN PresentDays DECIMAL(5,2) NULL DEFAULT NULL,
    ALGORITHM=INPLACE, LOCK=SHARED;

-- Verify the two column definitions and identical row/known-value counts/sums.
-- Reapplication is idempotent. Do NOT "rollback" by filling NULL with zero or
-- coercing back to NOT NULL after payable-only rows exist: that would invent data.
SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT
FROM information_schema.columns
WHERE TABLE_SCHEMA=DATABASE()
  AND ((TABLE_NAME='employee_monthly_attendance' AND COLUMN_NAME='present_days')
    OR (TABLE_NAME='payrunemployees' AND COLUMN_NAME='PresentDays'));
SELECT COUNT(*) AS monthly_rows,COUNT(present_days) AS known_actual_presence,
       SUM(present_days IS NULL) AS unknown_actual_presence,
       SUM(present_days) AS known_presence_sum
FROM employee_monthly_attendance;
SELECT COUNT(*) AS payroll_employee_rows,COUNT(PresentDays) AS known_actual_presence,
       SUM(PresentDays IS NULL) AS unknown_actual_presence,
       SUM(PresentDays) AS known_presence_sum
FROM payrunemployees;
