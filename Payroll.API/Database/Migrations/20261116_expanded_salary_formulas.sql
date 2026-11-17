-- Existing formula strings use the unchanged 4096-character payroll AST.
-- Run only after taking the existing schema/data snapshot and checking current
-- VARCHAR(1000), NOT NULL, empty default, utf8mb4_0900_ai_ci, InnoDB definitions.
-- Both old and new limits use a two-byte VARCHAR length prefix in utf8mb4.
-- Require INPLACE/LOCK=NONE; never silently fall back to a copying/blocking ALTER.
-- https://dev.mysql.com/doc/refman/8.4/en/innodb-online-ddl-operations.html
-- DDL commits independently. Inspect each actual result before continuing.
-- Do not shrink these fields after longer reviewed formulas have been saved.
SET SESSION lock_wait_timeout = 5;
ALTER TABLE salarycomponents
    MODIFY COLUMN Formula VARCHAR(4096) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL DEFAULT '',
    ALGORITHM=INPLACE, LOCK=NONE;
ALTER TABLE salarystructurelines
    MODIFY COLUMN Formula VARCHAR(4096) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL DEFAULT '',
    ALGORITHM=INPLACE, LOCK=NONE;
