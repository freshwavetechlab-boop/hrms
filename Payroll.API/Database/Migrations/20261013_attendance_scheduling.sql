-- Additive MySQL upgrade after 20261006_attendance_shifts.sql.
-- Existing policies remain Legacy: no assignments or historic results are changed.
SET @ddl = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_shifts' AND column_name='rules_json'), 'SELECT 1', 'ALTER TABLE attendance_shifts ADD COLUMN rules_json JSON NULL');
PREPARE scheduling_upgrade FROM @ddl; EXECUTE scheduling_upgrade; DEALLOCATE PREPARE scheduling_upgrade;
SET @ddl = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_groups' AND column_name='attendance_mode'), 'SELECT 1', 'ALTER TABLE attendance_groups ADD COLUMN attendance_mode VARCHAR(20) NOT NULL DEFAULT ''Legacy''');
PREPARE scheduling_upgrade FROM @ddl; EXECUTE scheduling_upgrade; DEALLOCATE PREPARE scheduling_upgrade;
SET @ddl = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_groups' AND column_name='allowed_shift_ids_json'), 'SELECT 1', 'ALTER TABLE attendance_groups ADD COLUMN allowed_shift_ids_json JSON NULL');
PREPARE scheduling_upgrade FROM @ddl; EXECUTE scheduling_upgrade; DEALLOCATE PREPARE scheduling_upgrade;
CREATE TABLE IF NOT EXISTS attendance_shift_assignments (
id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,employee_id INT NOT NULL,shift_id INT NOT NULL,
effective_from DATE NOT NULL,effective_to DATE NOT NULL,week_days VARCHAR(20) NOT NULL DEFAULT '0,1,2,3,4,5,6',
reason VARCHAR(500) NOT NULL,is_active BOOLEAN NOT NULL DEFAULT TRUE,created_by INT NOT NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
cancelled_by INT NULL,cancelled_at DATETIME NULL,
INDEX ix_shift_assignment_scope(client_id,employee_id,effective_from,effective_to),
CONSTRAINT fk_shift_assignment_employee FOREIGN KEY(employee_id) REFERENCES employees(Id),
CONSTRAINT fk_shift_assignment_shift FOREIGN KEY(shift_id,client_id) REFERENCES attendance_shifts(id,client_id));
