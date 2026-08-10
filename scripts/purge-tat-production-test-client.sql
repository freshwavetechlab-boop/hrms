-- Production-safe cleanup for the seeded TAT test tenant.
-- Target verified on 2026-08-04:
--   clients.Id   = 13
--   clients.Code = TAT
--   clients.Name = TA Test Client Pvt Ltd
--
-- IMPORTANT
-- 1. Take a database backup/snapshot first.
-- 2. Run the complete file as a script in one MySQL session.
-- 3. The default confirmation value is intentionally locked.
-- 4. To execute, change PREVIEW_ONLY to DELETE_TAT_CLIENT_13.
-- 5. This removes database metadata. Physical attachment blobs on a file
--    server/local disk are not deleted by SQL and should be purged separately
--    from the deleted entity_attachments storage keys after retaining a backup.

SET @PURGE_CONFIRMATION := 'PREVIEW_ONLY';

DROP PROCEDURE IF EXISTS purge_tat_test_client;

DELIMITER $$

CREATE PROCEDURE purge_tat_test_client()
main: BEGIN
    DECLARE v_client_id INT DEFAULT NULL;
    DECLARE v_match_count INT DEFAULT 0;
    DECLARE v_old_fk_checks INT DEFAULT 1;
    DECLARE v_statement_id BIGINT DEFAULT NULL;
    DECLARE v_sql LONGTEXT DEFAULT NULL;
    DECLARE v_deleted_client_rows INT DEFAULT 0;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        SET FOREIGN_KEY_CHECKS = v_old_fk_checks;
        RESIGNAL;
    END;

    SELECT COUNT(*), MIN(Id)
      INTO v_match_count, v_client_id
      FROM clients
     WHERE BINARY Name = 'TA Test Client Pvt Ltd'
       AND BINARY Code = 'TAT';

    IF v_match_count <> 1 OR v_client_id <> 13 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Safety guard failed: expected exactly one TAT client with Id 13 and exact name/code.';
    END IF;

    -- Always show the target and important row counts before the confirmation guard.
    SELECT c.Id, c.Name, c.Code, c.IsActive
      FROM clients c
     WHERE c.Id = v_client_id;

    SELECT
        (SELECT COUNT(*) FROM authusers WHERE ClientId = v_client_id) AS users,
        (SELECT COUNT(*) FROM employees WHERE ClientId = v_client_id) AS employees,
        (SELECT COUNT(*) FROM employee_daily_attendance WHERE client_id = v_client_id) AS daily_attendance,
        (SELECT COUNT(*) FROM essleaverequests WHERE ClientId = v_client_id) AS leave_requests,
        (SELECT COUNT(*) FROM payruns WHERE ClientId = v_client_id) AS pay_runs,
        (SELECT COUNT(*) FROM recruitment_candidates WHERE ClientId = v_client_id) AS candidates,
        (SELECT COUNT(*) FROM recruitment_candidate_applications WHERE ClientId = v_client_id) AS applications,
        (SELECT COUNT(*) FROM form_definitions WHERE ClientId = v_client_id) AS dynamic_forms,
        (SELECT COUNT(*) FROM entity_attachments WHERE client_id = v_client_id) AS attachment_metadata;

    IF COALESCE(@PURGE_CONFIRMATION, '') <> CONCAT('DELETE_TAT_CLIENT_', v_client_id) THEN
        SELECT CONCAT(
            'PREVIEW ONLY. No rows were deleted. Set @PURGE_CONFIRMATION to DELETE_TAT_CLIENT_',
            v_client_id,
            ' and run the complete script again.'
        ) AS safety_message;
        LEAVE main;
    END IF;

    START TRANSACTION;
    SET v_old_fk_checks = @@FOREIGN_KEY_CHECKS;

    -- Capture identifiers before deleting parent rows. These temporary tables
    -- are session-local and disappear automatically when the connection closes.
    DROP TEMPORARY TABLE IF EXISTS _tat_user_ids;
    CREATE TEMPORARY TABLE _tat_user_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_user_ids SELECT Id FROM authusers WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_employee_ids;
    CREATE TEMPORARY TABLE _tat_employee_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_employee_ids SELECT Id FROM employees WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_payrun_ids;
    CREATE TEMPORARY TABLE _tat_payrun_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_payrun_ids SELECT Id FROM payruns WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_payrun_employee_ids;
    CREATE TEMPORARY TABLE _tat_payrun_employee_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_payrun_employee_ids
    SELECT Id FROM payrunemployees WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_workflow_instance_ids;
    CREATE TEMPORARY TABLE _tat_workflow_instance_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_workflow_instance_ids
    SELECT wi.Id
      FROM workflowinstances wi
      JOIN workflowmasters wm ON wm.Id = wi.WorkflowId
     WHERE wm.ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_attachment_ids;
    CREATE TEMPORARY TABLE _tat_attachment_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_attachment_ids
    SELECT id FROM entity_attachments WHERE client_id = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_notification_queue_ids;
    CREATE TEMPORARY TABLE _tat_notification_queue_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_notification_queue_ids
    SELECT Id FROM notification_queue WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_salary_structure_ids;
    CREATE TEMPORARY TABLE _tat_salary_structure_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_salary_structure_ids
    SELECT Id FROM salarystructures WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_billing_rule_header_ids;
    CREATE TEMPORARY TABLE _tat_billing_rule_header_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_billing_rule_header_ids
    SELECT Id FROM client_billing_cost_rule_headers WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_billing_invoice_ids;
    CREATE TEMPORARY TABLE _tat_billing_invoice_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_billing_invoice_ids
    SELECT Id FROM client_billing_invoices WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_tax_header_ids;
    CREATE TEMPORARY TABLE _tat_tax_header_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_tax_header_ids
    SELECT id FROM employee_tax_declaration_headers WHERE client_id = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_tax_declaration_ids;
    CREATE TEMPORARY TABLE _tat_tax_declaration_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_tax_declaration_ids
    SELECT id FROM employee_tax_declarations WHERE client_id = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_travel_request_ids;
    CREATE TEMPORARY TABLE _tat_travel_request_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_travel_request_ids
    SELECT Id FROM ess_travel_requests WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_expense_claim_ids;
    CREATE TEMPORARY TABLE _tat_expense_claim_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_expense_claim_ids
    SELECT Id FROM ess_expense_claims WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_travel_advance_ids;
    CREATE TEMPORARY TABLE _tat_travel_advance_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_travel_advance_ids
    SELECT Id FROM ess_travel_advances WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_travel_policy_ids;
    CREATE TEMPORARY TABLE _tat_travel_policy_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_travel_policy_ids
    SELECT Id FROM travel_policies WHERE CompanyId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_requisition_ids;
    CREATE TEMPORARY TABLE _tat_requisition_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_requisition_ids
    SELECT Id FROM recruitment_requisitions WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_position_ids;
    CREATE TEMPORARY TABLE _tat_position_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_position_ids
    SELECT Id FROM recruitment_open_positions WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_candidate_ids;
    CREATE TEMPORARY TABLE _tat_candidate_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_candidate_ids
    SELECT Id FROM recruitment_candidates WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_application_ids;
    CREATE TEMPORARY TABLE _tat_application_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_application_ids
    SELECT Id FROM recruitment_candidate_applications WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_interview_ids;
    CREATE TEMPORARY TABLE _tat_interview_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_interview_ids
    SELECT i.Id
      FROM recruitment_interviews i
      JOIN _tat_application_ids a ON a.id = i.ApplicationId;

    DROP TEMPORARY TABLE IF EXISTS _tat_interview_feedback_ids;
    CREATE TEMPORARY TABLE _tat_interview_feedback_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_interview_feedback_ids
    SELECT f.Id
      FROM recruitment_interview_feedback f
      JOIN _tat_interview_ids i ON i.id = f.InterviewId;

    DROP TEMPORARY TABLE IF EXISTS _tat_position_pipeline_instance_ids;
    CREATE TEMPORARY TABLE _tat_position_pipeline_instance_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_position_pipeline_instance_ids
    SELECT Id FROM recruitment_position_pipeline_instances WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_position_stage_instance_ids;
    CREATE TEMPORARY TABLE _tat_position_stage_instance_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_position_stage_instance_ids
    SELECT s.Id
      FROM recruitment_position_stage_instances s
      JOIN _tat_position_pipeline_instance_ids p ON p.id = s.PositionPipelineInstanceId;

    DROP TEMPORARY TABLE IF EXISTS _tat_profile_batch_ids;
    CREATE TEMPORARY TABLE _tat_profile_batch_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_profile_batch_ids
    SELECT Id FROM recruitment_profile_submission_batches WHERE ClientId = v_client_id;

    DROP TEMPORARY TABLE IF EXISTS _tat_stage_action_execution_ids;
    CREATE TEMPORARY TABLE _tat_stage_action_execution_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_stage_action_execution_ids
    SELECT e.Id
      FROM recruitment_stage_action_executions e
      JOIN _tat_application_ids a ON a.id = e.ApplicationId;

    DROP TEMPORARY TABLE IF EXISTS _tat_work_order_ids;
    CREATE TEMPORARY TABLE _tat_work_order_ids (id BIGINT PRIMARY KEY) ENGINE=MEMORY;
    INSERT IGNORE INTO _tat_work_order_ids
    SELECT Id FROM recruitment_work_orders WHERE ClientId = v_client_id;

    -- Disable FK enforcement only after every required parent identifier has
    -- been captured. All HRMS tables in the live schema are InnoDB, so the
    -- transaction still rolls back atomically if any statement fails.
    SET FOREIGN_KEY_CHECKS = 0;

    -- Non-FK employee/payroll/tax descendants.
    DELETE t FROM employee_audit_trail t JOIN _tat_employee_ids x ON x.id = t.EmployeeId;
    DELETE t FROM employeepaymentdetails t JOIN _tat_employee_ids x ON x.id = t.EmployeeId;
    DELETE t FROM employeepersonaldetails t JOIN _tat_employee_ids x ON x.id = t.EmployeeId;
    DELETE t FROM employeesalarycomponents t JOIN _tat_employee_ids x ON x.id = t.EmployeeId;
    DELETE t FROM employee_tax_declaration_lines t JOIN _tat_tax_header_ids x ON x.id = t.header_id;
    DELETE t FROM employee_tax_declaration_proofs t JOIN _tat_tax_declaration_ids x ON x.id = t.declaration_id;
    DELETE t FROM payrunemployeelines t
     WHERE t.PayRunId IN (SELECT id FROM _tat_payrun_ids)
        OR t.PayRunEmployeeId IN (SELECT id FROM _tat_payrun_employee_ids);
    DELETE t FROM payrun_step_logs t JOIN _tat_payrun_ids x ON x.id = t.PayRunId;
    DELETE t FROM payroll_calculation_traces t JOIN _tat_payrun_ids x ON x.id = t.PayRunId;
    DELETE t FROM payroll_reconciliation_results t JOIN _tat_payrun_ids x ON x.id = t.PayRunId;
    DELETE t FROM payroll_validation_issues t JOIN _tat_payrun_ids x ON x.id = t.PayRunId;

    -- Workflow and notification descendants that are not protected by FKs.
    DELETE t FROM workflowhistory t JOIN _tat_workflow_instance_ids x ON x.id = t.InstanceId;
    DELETE t FROM workflowtasks t JOIN _tat_workflow_instance_ids x ON x.id = t.InstanceId;
    DELETE t FROM workflowinstances t JOIN _tat_workflow_instance_ids x ON x.id = t.Id;
    DELETE t FROM notification_logs t JOIN _tat_notification_queue_ids x ON x.id = t.QueueId;

    -- Attachment access tokens are metadata-only descendants. Physical blobs
    -- are deliberately not removed by this SQL script.
    DELETE t FROM attachment_access_tokens t JOIN _tat_attachment_ids x ON x.id = t.attachment_id;

    -- Salary and client billing descendants without declared FKs.
    DELETE t FROM salarystructurelines t JOIN _tat_salary_structure_ids x ON x.id = t.StructureId;
    DELETE t FROM client_billing_cost_rule_lines t JOIN _tat_billing_rule_header_ids x ON x.id = t.HeaderId;
    DELETE t FROM client_billing_invoice_lines t JOIN _tat_billing_invoice_ids x ON x.id = t.InvoiceId;

    -- Travel and expense descendants without declared FKs.
    DELETE t FROM ess_travel_request_legs t JOIN _tat_travel_request_ids x ON x.id = t.RequestId;
    DELETE t FROM ess_travel_request_accommodation t JOIN _tat_travel_request_ids x ON x.id = t.RequestId;
    DELETE t FROM ess_travel_request_local_travel t JOIN _tat_travel_request_ids x ON x.id = t.RequestId;
    DELETE t FROM ess_travel_request_audit t JOIN _tat_travel_request_ids x ON x.id = t.RequestId;
    DELETE t FROM ess_expense_claim_attachments t JOIN _tat_expense_claim_ids x ON x.id = t.ClaimId;
    DELETE t FROM ess_expense_claim_audit t JOIN _tat_expense_claim_ids x ON x.id = t.ClaimId;
    DELETE t FROM ess_expense_claim_lines t JOIN _tat_expense_claim_ids x ON x.id = t.ClaimId;
    DELETE t FROM ess_travel_advance_audit t JOIN _tat_travel_advance_ids x ON x.id = t.AdvanceId;
    DELETE t FROM travel_policy_assignments t JOIN _tat_travel_policy_ids x ON x.id = t.PolicyId;
    DELETE t FROM travel_policy_rules t JOIN _tat_travel_policy_ids x ON x.id = t.PolicyId;
    DELETE t FROM travel_policy_audit t
     WHERE t.EntityType = 'TravelPolicy'
       AND t.EntityId IN (SELECT id FROM _tat_travel_policy_ids);
    DELETE t FROM travel_policies t JOIN _tat_travel_policy_ids x ON x.id = t.Id;

    -- Recruitment descendants without declared FKs.
    DELETE t FROM recruitment_requisition_documents t JOIN _tat_requisition_ids x ON x.id = t.RequisitionId;
    DELETE t FROM recruitment_position_timeline t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_position_notes t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_position_checklist t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_recruiter_assignments t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_partner_assignments t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_job_publications t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_referral_campaigns t JOIN _tat_position_ids x ON x.id = t.PositionId;
    DELETE t FROM recruitment_employee_referrals t
     WHERE t.PositionId IN (SELECT id FROM _tat_position_ids)
        OR t.CandidateId IN (SELECT id FROM _tat_candidate_ids)
        OR t.ApplicationId IN (SELECT id FROM _tat_application_ids);

    DELETE t FROM recruitment_candidate_certifications t JOIN _tat_candidate_ids x ON x.id = t.CandidateId;
    DELETE t FROM recruitment_candidate_education t JOIN _tat_candidate_ids x ON x.id = t.CandidateId;
    DELETE t FROM recruitment_candidate_experience t JOIN _tat_candidate_ids x ON x.id = t.CandidateId;
    DELETE t FROM recruitment_candidate_checklist_items t
     WHERE t.CandidateId IN (SELECT id FROM _tat_candidate_ids)
        OR t.ApplicationId IN (SELECT id FROM _tat_application_ids);
    DELETE t FROM recruitment_application_stage_history t JOIN _tat_application_ids x ON x.id = t.ApplicationId;
    DELETE t FROM recruitment_ats_scoring_jobs t
     WHERE t.ApplicationId IN (SELECT id FROM _tat_application_ids)
        OR t.RequestedByClientId = v_client_id;

    DELETE t FROM recruitment_interview_feedback_competency_scores t
      JOIN _tat_interview_feedback_ids x ON x.id = t.InterviewFeedbackId;
    DELETE t FROM recruitment_interview_panel_members t JOIN _tat_interview_ids x ON x.id = t.InterviewId;
    DELETE t FROM recruitment_interview_feedback t JOIN _tat_interview_ids x ON x.id = t.InterviewId;
    DELETE t FROM recruitment_interviews t JOIN _tat_interview_ids x ON x.id = t.Id;

    DELETE t FROM recruitment_stage_action_notification_deliveries t
      JOIN _tat_stage_action_execution_ids x ON x.id = t.StageActionExecutionId;
    DELETE t FROM recruitment_stage_action_executions t
      JOIN _tat_stage_action_execution_ids x ON x.id = t.Id;

    DELETE t FROM recruitment_profile_batch_notification_deliveries t
      JOIN _tat_profile_batch_ids x ON x.id = t.BatchId;
    DELETE t FROM recruitment_profile_submission_batch_items t
      JOIN _tat_profile_batch_ids x ON x.id = t.BatchId;

    DELETE t FROM recruitment_position_stage_events t
      JOIN _tat_position_pipeline_instance_ids x ON x.id = t.PositionPipelineInstanceId;
    DELETE t FROM recruitment_position_stage_pause_periods t
      JOIN _tat_position_stage_instance_ids x ON x.id = t.PositionStageInstanceId;
    DELETE t FROM recruitment_hiring_case_advance_requests t
     WHERE t.HiringCaseId IN (SELECT id FROM _tat_position_pipeline_instance_ids)
        OR t.PositionStageInstanceId IN (SELECT id FROM _tat_position_stage_instance_ids);
    DELETE t FROM recruitment_position_stage_instances t
      JOIN _tat_position_pipeline_instance_ids x ON x.id = t.PositionPipelineInstanceId;

    DELETE t FROM recruitment_work_order_lines t JOIN _tat_work_order_ids x ON x.id = t.WorkOrderId;

    -- Audit rows created by client-scoped test users. The normal entity data is
    -- removed separately below through its ClientId or FK relationship.
    DELETE t FROM recruitment_audit t JOIN _tat_user_ids x ON x.id = t.ChangedByUserId;
    DELETE t FROM recruitment_admin_audit t JOIN _tat_user_ids x ON x.id = t.ChangedByUserId;

    -- Build a live-schema deletion plan. Every base table containing ClientId
    -- or client_id is a root. All FK descendants are recursively included and
    -- deleted from deepest child to root. This keeps the cleanup resilient as
    -- new client-scoped tables are added to the product.
    SET SESSION cte_max_recursion_depth = 100;
    DROP TEMPORARY TABLE IF EXISTS _tat_delete_statements;
    CREATE TEMPORARY TABLE _tat_delete_statements (
        statement_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
        depth INT NOT NULL,
        table_name VARCHAR(128) NOT NULL,
        sql_text LONGTEXT NOT NULL
    ) ENGINE=InnoDB;

    INSERT INTO _tat_delete_statements (depth, table_name, sql_text)
    WITH RECURSIVE relation_paths AS (
        SELECT c.TABLE_NAME AS root_table,
               c.TABLE_NAME AS current_table,
               c.COLUMN_NAME AS client_column,
               CAST(CONCAT('|', c.TABLE_NAME, '|') AS CHAR(10000)) AS visited,
               0 AS depth,
               CAST(CONCAT('{a}.`', c.COLUMN_NAME, '` = ', v_client_id) AS CHAR(10000)) AS predicate_sql
          FROM information_schema.COLUMNS c
          JOIN information_schema.TABLES tbl
            ON tbl.TABLE_SCHEMA = c.TABLE_SCHEMA
           AND tbl.TABLE_NAME = c.TABLE_NAME
           AND tbl.TABLE_TYPE = 'BASE TABLE'
         WHERE c.TABLE_SCHEMA = DATABASE()
           AND LOWER(c.COLUMN_NAME) IN ('clientid', 'client_id')

        UNION ALL

        SELECT p.root_table,
               fk.TABLE_NAME AS current_table,
               p.client_column,
               CAST(CONCAT(p.visited, fk.TABLE_NAME, '|') AS CHAR(10000)) AS visited,
               p.depth + 1,
               CAST(CONCAT(
                   'EXISTS (SELECT 1 FROM `', p.current_table, '` AS p', p.depth + 1,
                   ' WHERE {a}.`', fk.COLUMN_NAME, '` = p', p.depth + 1,
                   '.`', fk.REFERENCED_COLUMN_NAME, '` AND (',
                   REPLACE(p.predicate_sql, '{a}', CONCAT('p', p.depth + 1)),
                   '))'
               ) AS CHAR(10000)) AS predicate_sql
          FROM relation_paths p
          JOIN information_schema.KEY_COLUMN_USAGE fk
            ON fk.CONSTRAINT_SCHEMA = DATABASE()
           AND fk.REFERENCED_TABLE_NAME = p.current_table
         WHERE p.depth < 20
           AND p.visited NOT LIKE CONCAT('%|', fk.TABLE_NAME, '|%')
    )
    SELECT depth,
           current_table,
           CONCAT(
               'DELETE a FROM `', current_table, '` AS a WHERE (',
               REPLACE(predicate_sql, '{a}', 'a'),
               ')'
           ) AS sql_text
      FROM relation_paths
     ORDER BY depth DESC, current_table;

    dynamic_delete_loop: LOOP
        SET v_statement_id = NULL;
        SET v_sql = NULL;

        SELECT statement_id, sql_text
          INTO v_statement_id, v_sql
          FROM _tat_delete_statements
         ORDER BY depth DESC, statement_id
         LIMIT 1;

        IF v_statement_id IS NULL THEN
            LEAVE dynamic_delete_loop;
        END IF;

        SET @tat_dynamic_sql = v_sql;
        PREPARE tat_delete_stmt FROM @tat_dynamic_sql;
        EXECUTE tat_delete_stmt;
        DEALLOCATE PREPARE tat_delete_stmt;

        DELETE FROM _tat_delete_statements WHERE statement_id = v_statement_id;
    END LOOP;

    -- The exact protected parent is deleted last.
    DELETE FROM clients
     WHERE Id = v_client_id
       AND BINARY Name = 'TA Test Client Pvt Ltd'
       AND BINARY Code = 'TAT';
    SET v_deleted_client_rows = ROW_COUNT();

    IF v_deleted_client_rows <> 1 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Final safety check failed: the exact TAT client row was not deleted.';
    END IF;

    SET FOREIGN_KEY_CHECKS = v_old_fk_checks;
    COMMIT;

    SELECT
        'COMMITTED' AS result,
        v_client_id AS deleted_client_id,
        'TA Test Client Pvt Ltd' AS deleted_client_name,
        'TAT' AS deleted_client_code;

    SELECT
        (SELECT COUNT(*) FROM clients WHERE Id = v_client_id OR Code = 'TAT' OR Name = 'TA Test Client Pvt Ltd') AS remaining_client_rows,
        (SELECT COUNT(*) FROM authusers WHERE ClientId = v_client_id) AS remaining_users,
        (SELECT COUNT(*) FROM employees WHERE ClientId = v_client_id) AS remaining_employees,
        (SELECT COUNT(*) FROM recruitment_candidates WHERE ClientId = v_client_id) AS remaining_candidates,
        (SELECT COUNT(*) FROM form_definitions WHERE ClientId = v_client_id) AS remaining_forms;
END$$

DELIMITER ;

CALL purge_tat_test_client();
DROP PROCEDURE IF EXISTS purge_tat_test_client;

