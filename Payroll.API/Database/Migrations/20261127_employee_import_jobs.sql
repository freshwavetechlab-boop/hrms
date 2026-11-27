CREATE TABLE IF NOT EXISTS employee_import_jobs (
    JobId CHAR(36) PRIMARY KEY,
    ClientId INT NOT NULL,
    ActorUserId INT NOT NULL,
    Kind VARCHAR(40) NOT NULL,
    Operation VARCHAR(12) NOT NULL,
    State VARCHAR(16) NOT NULL,
    TotalRows INT NOT NULL,
    CompletedRows INT NOT NULL DEFAULT 0,
    InsertedRows INT NOT NULL DEFAULT 0,
    UpdatedRows INT NOT NULL DEFAULT 0,
    ErrorsJson JSON NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL,
    DismissedAtUtc DATETIME(6) NULL,
    INDEX IX_employee_import_owner (ActorUserId, DismissedAtUtc, ClientId, CreatedAtUtc)
);
