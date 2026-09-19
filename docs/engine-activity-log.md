# Engine activity log

App Settings > Engine Control Center > Engine activity log records individual
observed operations for all eight engines, alongside the existing aggregate graphs.

| Engine | Recorded boundary |
| --- | --- |
| FrevoPilot | Dashboard workflow, not status polls/chat history |
| Resume Parser | Classified intake requests, public saved-resume/background parsing, draft preview parsing |
| JD / Hiring Parser | Parse-source requests |
| ATS Scoring | Actual scoring job attempts, not enqueue/status requests |
| Payroll Processor | Payroll commands and actual queued payroll processing |
| Bulk Data Jobs | Classified import/export commands and attendance batch worker |
| Notification Delivery | Notification/invite commands and actual queued email delivery |
| Documents & Storage | Classified attachment/storage/document-generation mutations |

This is instrumentation coverage, not a claim that every internal method has its
own timer. HTTP and worker entries are separate operations, never additive task
totals. An accepted HTTP request is not a completed background job. Completed
parsing measures the operation, not extraction accuracy. Email completion means
SMTP send returned, not proof that a recipient read/received it.

Rows show operation, stable references, start/end, measured processing duration,
and outcome. Where application/candidate/position IDs are known, current candidate
name, application code and position title are resolved on read; they are not
duplicated in log storage. Unsaved uploads have no saved candidate identity.
Deleted records retain only their stable references. No email address, phone,
filename, document, prompt, answer, credentials or raw exception text is stored.
HTTP operations use route templates, not token-bearing URLs or query strings.

ATS additionally records queue wait (job availability to worker start), attempt,
and the AI-analysis phase where enabled/measured. AI phase includes admission,
provider/fallback and related service work: it is not pure model compute time.
It is contained within processing duration, not extra time. Other engines leave
unknown sub-timings blank. Total durations use a monotonic stopwatch; timestamps
are UTC and the UI displays browser-local time. Running elapsed time is a UI
estimate. Unrecorded durations remain unknown, never zero. Stale running rows
without a heartbeat for five minutes are shown as Interrupted / unknown.

## Persistence and limits

- New additive table `engine_activity_log` is created by the worker; no existing
  business table is altered. Restricted deployments can provision
  `EngineActivityStore.SchemaSql` using their schema-management account.
- `EngineActivity:Enabled` defaults true. False stops collection/writes, preserving
  existing history. Recording runs independently of `BackgroundWorkers:Enabled`.
- `EngineActivity:RetentionDays` defaults 7, bounded 1–30; graph aggregates retain
  their existing 30-day policy. No historical task durations are backfilled.
- Deployment partition reuses `EngineHistory:Deployment`; all instances of the
  same deployment must match, and local/prod sharing a DB must differ.
- Flush up to 250 records every five seconds; bounded 5,000-record memory buffer,
  one-hour completed-record retry horizon. Idle flushes do not query the DB except
  periodic cleanup. Overflow/expiry is reported, not silently claimed complete.
- GUID plus increasing revision makes checkpoint retry idempotent and prevents
  an older running checkpoint overwriting completion. Saved records survive API
  restart. Abrupt shutdown or exhausted buffers can lose unflushed records; this
  is operational telemetry, not a transactional audit ledger.
- Hourly retention cleanup deletes at most 5,000 expired rows per pass for this
  deployment. SQL waits and final shutdown flush are bounded. Storage failures
  cannot replace business results/errors or stop business processing.

The server requires an active exact global `super_admin` before reading any logs.
Responses are `no-store`; no extra access is granted to client administrators.
The UI filters by engine/outcome/dates, refreshes every five seconds, pauses on
hidden tabs/older pages, and loads at most 50 rows per cursor page. API monitoring
reads themselves do not create activity records.

## Verification

EngineActivityTests cover all engines, outcome classification, metadata privacy,
late acknowledgements, bounded buffering/fair heartbeat, failed storage expiry,
poll exclusion and fail-open behavior. Opt-in EngineHistoryDevDatabaseTests verify
real MySQL persistence, stale-write retry, UTC round-trip, pagination, restart,
deployment isolation and interrupted tasks. DEV tests reject non-local DB targets
and remove only their unique test deployment's telemetry rows. Frontend tests
verify duration/outcome formatting; both applications are built.

No actual candidate scoring, payroll, email delivery or browser journey is required
or triggered by these isolated checks.
