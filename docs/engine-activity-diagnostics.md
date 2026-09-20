# Engine activity diagnostics

The telemetry change is additive. The separately approved JD length-validation
follow-up is described below; neither change restarts the LLM or changes CORS policy.

- `AI Provider Requests` observes the shared recruitment AI transport, including
  local connection tests, JD/resume/ATS/FrevoPilot requests and cloud retry attempts.
  Each failed cloud attempt remains visible even if a later attempt succeeds.
  Provider times overlap the calling engine; do not add them to its duration or
  interpret busy percentages as host CPU. No HTTP polling traffic is counted.
- POST hiring-request draft saves are included under JD / Hiring Parser, separately
  from source parsing. A successful parse does not imply a successful database save.
- Existing engine HTTP/async observations record allow-listed diagnostic codes for
  database size/unique errors, transport failures, deadlines and cancellation.
  Other errors honestly retain the generic source-task/server-log fallback.
- Existing `FailureCode VARCHAR(80)` stores the code; `FailureReason` is generated
  at read time. No migration. Existing bounded buffering/retention, fail-open
  recording and exact-global-super-admin API access are unchanged.
- No raw provider error prose, document text, credentials, SQL, emails or request
  URLs are added to activity storage. Local 503 error codes are allow-listed. A
  gateway `unavailable` response means it reports a stopped model, not necessarily
  a CPU trip. Watchdog shutdown reason is unavailable unless separately reported;
  do not manufacture or retrospectively attach a reason to an unrelated request.
- Old unrecorded failures cannot be reconstructed from this update. New code must
  run on the API/workers handling the requests; UI-only deployment cannot record them.

## JD incident diagnosed from supplied production log

`parse-source` returned 200 in 36019 ms. Requisition OPTIONS returned 204 with
successful CORS policy evaluation. Requisition POST then threw MySQL 1406
`Data too long for column 'Qualification'` and returned empty 500 in 39 ms.
The application save exception is confirmed; the browser CORS message is not
evidence that the allowed-origin list caused this request's failure.

At diagnosis, Qualification was VARCHAR(250), AI parsing allowed up to 1800
characters, and deterministic parsing joined degree lines without a combined
250-character guard. Neither the UI nor SaveDraft checked that length. RAG
supplies evidence/vocabulary, not a storage-boundary guarantee.

## Approved field-length follow-up

- `RecruitmentRequisitionTextLimits` covers all 28 VARCHAR/TEXT input fields in
  the hiring-request save DTO, checked against existing SQL definitions. JSON
  evidence is not a VARCHAR/TEXT field; existing JSON handling is unchanged.
- The parser runs the shared check after deterministic/AI/RAG processing. An
  overflow becomes `NeedsReview` with field, actual length and maximum. Draft
  values are retained; field assignments no longer silently truncate to arbitrary
  character caps. Existing extraction heuristics and model-output budgets remain.
- The admin form loads the same contract from its authorized field-limits route.
  Inline validation, a visible summary and opening advanced fields make correction
  possible. Both autosave and explicit save stop before writing invalid values;
  browser drafts are preserved. Missing contract metadata blocks saving with a
  refresh message rather than bypassing validation.
- `SaveDraftAsync` validates before opening a database connection, protecting
  admin/ESS, imports and direct callers for creates and edits. Existing API routes
  return their normal HTTP 400 error response instead of an unhandled SQL 1406.
- VARCHAR lengths count Unicode code points; TEXT uses UTF-8 bytes (65535).
  No schema migration, existing-record rewrite, provider switch or CORS change.
  Deploy API and UI together. This is not proof that every possible CORS failure
  has the same cause, or that numeric/date/other unrelated constraints are covered.

## Verification

2026-09-20: 202 selected backend tests and 31 UI/contract checks passed, including
local 503 recording/redaction, cloud retry-then-success, database field-size
classification, cancellation, bounded history and existing local-provider/recovery
regressions. The checks used synthetic providers, not production inference or DB
writes. No Playwright, service restart or production deployment was triggered.

Length follow-up: 112 selected backend tests passed, with one explicitly opt-in
real OCR fixture skipped; 31 field-contract/UI tests and 25 existing engine/UI
compatibility checks passed. Boundary and overflow tests cover every bounded
field, Unicode, data retention and create/update rejection without a configured
database. No real AI, browser or production DB write was used in these checks.
