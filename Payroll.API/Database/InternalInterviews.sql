-- Additive/idempotent. Existing schedules, panel feedback and hiring outcomes are unchanged.
CREATE TABLE IF NOT EXISTS recruitment_internal_interview_sessions (
    InterviewId BIGINT NOT NULL PRIMARY KEY,
    RoomId VARCHAR(64) NOT NULL,
    LinkVersion VARCHAR(64) NOT NULL,
    LinkExpiresAtUtc DATETIME(6) NOT NULL,
    Status VARCHAR(24) NOT NULL DEFAULT 'Scheduled',
    Control VARCHAR(16) NOT NULL DEFAULT 'Human',
    ConfigurationJson JSON NOT NULL,
    QuestionsJson JSON NOT NULL,
    ConsentAtUtc DATETIME(6) NULL,
    RecordingConsent BOOLEAN NOT NULL DEFAULT FALSE,
    TranscriptionConsent BOOLEAN NOT NULL DEFAULT FALSE,
    StartedAtUtc DATETIME(6) NULL,
    EndedAtUtc DATETIME(6) NULL,
    RetainUntilUtc DATETIME(6) NULL,
    ScheduledStartUtc DATETIME(6) NOT NULL,
    ScheduledEndUtc DATETIME(6) NOT NULL,
    MediaState VARCHAR(24) NOT NULL DEFAULT 'None',
    MediaError VARCHAR(600) NOT NULL DEFAULT '',
    Revision BIGINT NOT NULL DEFAULT 1,
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_internal_interview_room (RoomId),
    KEY IX_internal_interview_retention (RetainUntilUtc)
);
CREATE TABLE IF NOT EXISTS recruitment_interview_questions (
    Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    ClientId INT NOT NULL,
    PositionId BIGINT NOT NULL,
    Skill VARCHAR(120) NOT NULL,
    Difficulty VARCHAR(24) NOT NULL,
    Language VARCHAR(16) NOT NULL,
    Question TEXT NOT NULL,
    EvaluationCriteria TEXT NOT NULL,
    FollowUpInstructions TEXT NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedByUserId INT NOT NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    KEY IX_interview_question_scope (ClientId,PositionId,IsActive)
);
CREATE TABLE IF NOT EXISTS recruitment_internal_interview_events (
    Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    InterviewId BIGINT NOT NULL,
    EventKey VARCHAR(64) NOT NULL,
    Kind VARCHAR(40) NOT NULL,
    Actor VARCHAR(64) NOT NULL,
    Source VARCHAR(32) NOT NULL,
    Text MEDIUMTEXT NOT NULL,
    QuestionEventId BIGINT NULL,
    OffsetMs BIGINT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_interview_event_key (InterviewId,EventKey),
    KEY IX_interview_event_timeline (InterviewId,Id)
);
CREATE TABLE IF NOT EXISTS recruitment_internal_interview_recordings (
    Id CHAR(32) NOT NULL PRIMARY KEY,
    InterviewId BIGINT NOT NULL,
    Status VARCHAR(24) NOT NULL,
    StorageKey VARCHAR(512) NOT NULL,
    ContentType VARCHAR(64) NOT NULL,
    EgressId VARCHAR(100) NOT NULL DEFAULT '',
    SizeBytes BIGINT NOT NULL DEFAULT 0,
    StartedAtUtc DATETIME(6) NOT NULL,
    EndedAtUtc DATETIME(6) NULL,
    CreatedByUserId INT NOT NULL,
    KEY IX_interview_recording (InterviewId,Status)
);
