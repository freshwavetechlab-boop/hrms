-- Additive only: selected employer requester is separate from the creator.
-- Keep legacy employee requester IDs and all existing request/history rows.
SET @requester_column_exists = (SELECT COUNT(*) FROM information_schema.columns
 WHERE table_schema=DATABASE() AND table_name='recruitment_requisitions' AND column_name='RequesterUserId');
SET @requester_column_sql = IF(@requester_column_exists=0,
 'ALTER TABLE recruitment_requisitions ADD COLUMN RequesterUserId INT NULL', 'SELECT 1');
PREPARE requester_column_stmt FROM @requester_column_sql;
EXECUTE requester_column_stmt;
DEALLOCATE PREPARE requester_column_stmt;

SET @requester_index_exists = (SELECT COUNT(*) FROM information_schema.statistics
 WHERE table_schema=DATABASE() AND table_name='recruitment_requisitions' AND index_name='IX_recruitment_rfr_requester_user');
SET @requester_index_sql = IF(@requester_index_exists=0,
 'CREATE INDEX IX_recruitment_rfr_requester_user ON recruitment_requisitions (RequesterUserId,Status)', 'SELECT 1');
PREPARE requester_index_stmt FROM @requester_index_sql;
EXECUTE requester_index_stmt;
DEALLOCATE PREPARE requester_index_stmt;
