USE payroll;

SET @has_client_id := (
  SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'WorkLocations' AND column_name = 'ClientId'
);
SET @sql := IF(@has_client_id = 0,
  'ALTER TABLE WorkLocations ADD COLUMN ClientId INT NOT NULL DEFAULT 0 AFTER Id',
  'SELECT 1'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @has_client_name := (
  SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'WorkLocations' AND column_name = 'ClientName'
);
SET @sql := IF(@has_client_name = 0,
  'ALTER TABLE WorkLocations ADD COLUMN ClientName VARCHAR(250) NULL AFTER ClientId',
  'SELECT 1'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @has_index := (
  SELECT COUNT(*) FROM information_schema.statistics
  WHERE table_schema = DATABASE() AND table_name = 'WorkLocations' AND index_name = 'IX_WorkLocations_ClientId'
);
SET @sql := IF(@has_index = 0,
  'CREATE INDEX IX_WorkLocations_ClientId ON WorkLocations (ClientId)',
  'SELECT 1'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
