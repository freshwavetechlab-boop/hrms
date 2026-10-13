-- Optional pre-deployment setup when the application account cannot create tables.
-- Adds review/audit storage only; no existing payroll or salary rows are changed.
CREATE TABLE IF NOT EXISTS payroll_config_reviews (
 id varchar(36) NOT NULL PRIMARY KEY,
 user_id int NOT NULL,
 client_id int NOT NULL,
 snapshot_hash varchar(64) NOT NULL,
 snapshot_json longtext NOT NULL,
 status varchar(24) NOT NULL,
 result_json longtext NULL,
 error varchar(1000) NULL,
 created_at datetime(6) NOT NULL,
 started_at datetime(6) NULL,
 finished_at datetime(6) NULL,
 KEY ix_payroll_review_queue (status,created_at),
 KEY ix_payroll_review_owner (user_id,client_id,created_at)
);
CREATE TABLE IF NOT EXISTS payroll_config_review_events (
 id bigint NOT NULL AUTO_INCREMENT PRIMARY KEY,
 review_id varchar(36) NOT NULL,
 user_id int NOT NULL,
 finding_id varchar(140) NOT NULL,
 decision varchar(40) NOT NULL,
 created_at datetime(6) NOT NULL,
 KEY ix_payroll_review_event (review_id,created_at)
);
