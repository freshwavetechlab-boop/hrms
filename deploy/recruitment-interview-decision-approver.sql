-- Run against the intended HRMS database before the updated API serves requests.
-- Additive and repeatable; existing rows keep their HR/scheduler decision policy.
SET @decision_approver_column = (
  SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema=DATABASE() AND table_name='recruitment_interviews'
    AND column_name='DecisionApproverUserId'
);
SET @decision_approver_ddl = IF(@decision_approver_column=0,
  'ALTER TABLE recruitment_interviews ADD COLUMN DecisionApproverUserId INT NULL',
  'SELECT 1');
PREPARE decision_approver_migration FROM @decision_approver_ddl;
EXECUTE decision_approver_migration;
DEALLOCATE PREPARE decision_approver_migration;
