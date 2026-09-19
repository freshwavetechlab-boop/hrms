# Engine history

Engine Control Center now has Live, Today, Yesterday, Last 7 days and custom dates.
Browser dates are converted to UTC; graphs display browser-local time. Live cards
remain live even when the historical graph is selected.

`engine_metric_history` is an additive numeric-only table, created idempotently
by the history worker. No existing business table is altered. If the DB account
lacks CREATE permission, provision the DDL in `EngineHistoryStore.SchemaSql`
with the migration account. Failure does not block payroll, parsing or ATS.

Configuration (environment variables use double underscores):

- `EngineHistory:Enabled`: true by default; false stops collection/writes while
  leaving already saved history readable.
- `EngineHistory:Deployment`: stable short name (letters/digits/hyphen/underscore,
  max 64). Defaults: development / local-windows / production. All replicas of
  one deployment must share this value; environments sharing a DB must differ.
  Do not use a changing container ID. Each process separately gets a random writer ID.

Five-minute per-engine summaries contain completed attempts, failed attempts,
sum/max duration, observed milliseconds and union busy milliseconds. Concurrent
work is not double-counted as busy time. Retries of a checkpoint replace absolute
values for the same writer/bucket, rather than incrementing them again. Restarts
create a new writer; historical totals survive. Multi-instance busy percentage is
weighted observed-instance occupancy, not global CPU or total cluster capacity.
ATS records actual local job attempts, never repeated shared-queue snapshots.

The worker runs independently of the business-worker switch and browser activity.
It batches at most once a minute; shutdown attempts a final bounded flush.
Abrupt termination can lose the last unflushed minute. Storage outages retain a
bounded one-hour buffer; dropped coverage is reported. Missing observations are
null graph gaps, not fabricated zero-work periods. Persisted history is not a
transaction/audit log and cannot recover old unsaved live charts.

Only numeric data and deployment/writer/engine identifiers are saved. There are
no employee IDs, documents, prompts, keys or raw error messages. Endpoints keep
global-super-admin access and no-store response headers. Retention is 30 days,
with bounded hourly cleanup of this deployment's expired metrics only. Typical
continuous single-instance storage is 8 x 288 = 2,304 rows/day. Ranges longer than
two days are grouped hourly; averages use duration sums/counts, not averages of
averages. Date edges remain filtered before grouping.

Verification: EngineHistoryTests and opt-in EngineHistoryDevDatabaseTests cover
union occupancy, retries, late acknowledgement, long-running jobs, bounded memory,
real DB upsert/reload/restart and date-boundary grouping. DEV integration tests
require ENGINE_HISTORY_DEV_DB_TEST=1 and HRMS_SOURCE_ROOT, reject the production
target, and delete only their unique test-history-* metrics after completion.
