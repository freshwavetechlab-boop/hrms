-- MySQL: run against the HRMS database before starting the updated API.
-- Additive and repeatable; existing shifts remain unassigned (NULL).
CREATE TABLE IF NOT EXISTS attendance_shifts (
    id INT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    shift_code VARCHAR(50) NOT NULL,
    shift_name VARCHAR(180) NOT NULL,
    shift_type VARCHAR(20) NOT NULL DEFAULT 'Fixed',
    start_time TIME NULL,
    end_time TIME NULL,
    is_overnight BOOLEAN NOT NULL DEFAULT FALSE,
    grace_minutes INT NOT NULL DEFAULT 0,
    break_minutes INT NOT NULL DEFAULT 0,
    minimum_full_day_hours DECIMAL(5,2) NOT NULL DEFAULT 8,
    minimum_half_day_hours DECIMAL(5,2) NOT NULL DEFAULT 4,
    effective_from DATE NOT NULL,
    effective_to DATE NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY ux_attendance_shifts_client_code (client_id,shift_code),
    UNIQUE KEY ux_attendance_shifts_id_client (id,client_id),
    CONSTRAINT fk_attendance_shifts_client FOREIGN KEY (client_id) REFERENCES clients(Id) ON DELETE CASCADE
);

SET @shift_ddl = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_settings' AND column_name='shift_id'), 'SELECT 1', 'ALTER TABLE attendance_settings ADD COLUMN shift_id INT NULL AFTER client_id');
PREPARE shift_upgrade FROM @shift_ddl; EXECUTE shift_upgrade; DEALLOCATE PREPARE shift_upgrade;
SET @shift_ddl = IF(EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_groups' AND column_name='shift_id'), 'SELECT 1', 'ALTER TABLE attendance_groups ADD COLUMN shift_id INT NULL AFTER client_id');
PREPARE shift_upgrade FROM @shift_ddl; EXECUTE shift_upgrade; DEALLOCATE PREPARE shift_upgrade;
SET @shift_ddl = IF(EXISTS(SELECT 1 FROM information_schema.table_constraints WHERE constraint_schema=DATABASE() AND table_name='attendance_settings' AND constraint_name='fk_attendance_settings_shift_client'), 'SELECT 1', 'ALTER TABLE attendance_settings ADD CONSTRAINT fk_attendance_settings_shift_client FOREIGN KEY (shift_id,client_id) REFERENCES attendance_shifts(id,client_id)');
PREPARE shift_upgrade FROM @shift_ddl; EXECUTE shift_upgrade; DEALLOCATE PREPARE shift_upgrade;
SET @shift_ddl = IF(EXISTS(SELECT 1 FROM information_schema.table_constraints WHERE constraint_schema=DATABASE() AND table_name='attendance_groups' AND constraint_name='fk_attendance_groups_shift_client'), 'SELECT 1', 'ALTER TABLE attendance_groups ADD CONSTRAINT fk_attendance_groups_shift_client FOREIGN KEY (shift_id,client_id) REFERENCES attendance_shifts(id,client_id)');
PREPARE shift_upgrade FROM @shift_ddl; EXECUTE shift_upgrade; DEALLOCATE PREPARE shift_upgrade;
