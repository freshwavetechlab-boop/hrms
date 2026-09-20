# Internal interviews — implementation checkpoint

Status: additive implementation and local validation; **not production-ready**. Feature stays off. No production migration, deployment, real mail, model switch, gateway edit or service restart was performed.

## Implemented / reused

- Existing Talent Acquisition schedule drawer, panel membership, tracker, client/location RBAC, notifications, feedback/rubric and final human decision reused. Internal delivery is optional; ordinary external interviews stay on their existing path.
- Human/AI/Hybrid setup, scoped question bank, approved skill-section handoff, immutable question/rubric snapshots, instant human questions and panel notes.
- Expiring/revocable candidate-only links, separate authenticated panel access, consent/waiting/live/completed lifecycle, schedule/duration guards. Internal completion does not update the hiring result.
- Self-hosted LiveKit adapter, consent-bound room/recording request, short room-only grants, persisted closure retries, prioritized bounded parallel maintenance, private replay routes and retention cleanup.
- Native cookie-authenticated byte-range replay, including an actual HTTP 206/416 private-mount test. No full-file browser blob or URL credential. The explicit private mount uses the existing mounted-filesystem adapter.
- Joined participants are revalidated against fresh active-user, role, client, panel and location scope; unauthorized removals are audited and retried on the bounded maintenance cadence. Candidate/recording-worker identities remain separate. This is not an instantaneous revocation guarantee.
- Opt-in signed LiveKit callbacks now persist room-bound participant join/leave/connection-abort evidence in the existing event table. Signature, issuer, expiry and exact-body hash are checked before any database work; raw participant metadata is discarded. Duplicate delivery is idempotent, conflicting IDs fail, and unknown/rotated/purged/expired rooms cannot restore history. The private panel table distinguishes media-event time from HRMS receipt time and orders delayed deliveries correctly; candidates do not receive it. Actual LiveKit callback delivery remains unverified.
- Unstarted original reschedules can be reset with revision checks, a new room/link generation and fresh consent; question snapshots/audit remain. Started recordings cannot be reset/overwritten. Managers can maintain the job bank after session configuration/completion without altering the frozen session.
- Session confirmations use a fixed modal instead of the observed flickering anchored popover, mounted within the session so fullscreen does not hide it. The per-second timer updates independently; Playwright checks confirmation bounds through status/timer updates and ordinary cancel/confirm clicks, including fullscreen.
- Existing saved LocalOpenAICompatible provider reused, including encrypted credentials, admission and usage. Known runtime is llama.cpp hrms-local / Qwen3.5-4B, not a verified separate Ollama 3.5B/9B installation. Optional Ollama adapter does not replace the server.
- Bank questions, bounded model-selected approved follow-ups, exact-quote draft review, durable errors and human-triggered retry. Malformed model JSON is a controlled failure. No automatic hiring scores/decisions or browser-event model inputs.
- The durable AI queue now selects actionable live turns, not sessions still awaiting an answer or blocked on a recorded failure. Explicit retry restores readiness; the existing answer deadline and first-voice review grace remain enforced. Completed review work no longer competes with inert live sessions for the same bounded selection window.
- Combined interview review is now a separate durable queue pass over the full submitted question/answer set. Its small local-model contract returns at most two grounded highlights: interview summary, skill evidence, human-review topics or directly supported cross-answer clarification. Every evidence reference is rebound to actual answer/question IDs and UTC timestamps; cross-answer observations require quotes from distinct answers. No new schema/provider switch. Oversized full transcripts fail visibly without truncation or a fabricated summary.
- Combined-review UI links to original answers, lists coverage/unanswered counts and keeps human notes separate. Recovered AI errors remain in expandable history instead of stale warnings; raw draft JSON is no longer duplicated in the session audit. Without a recording, review cards use the available width.
- Private local speech sidecar and microphone → editable transcript draft → explicit Submit. WAV playback uses a separate named audio track in a connected room, with local-only delivery explicitly labelled when not connected. Publication is acknowledged before playback; question change, cancellation, disconnect and unmount clean up the clip without muting the candidate microphone. Six audio-graph contract tests pass; real LiveKit/Egress capture is still unverified.
- Feature-owned event/session/question inserts use explicit UTC timestamps rather than the database session's timezone. A real MySQL test writes under a +05:30 session and verifies UTC; no global database timezone or historical recruitment records changed.
- Payload-free browser buffer: 100 entries, four-hour lifetime, same-ID retry, one-per-second flush, 429 backoff, credential-fingerprint partition. Reload preserves pending evidence without saving bearer credentials or clipboard contents. Closed/revoked sessions stop retries and show unconfirmed-event counts.
- Existing engine monitoring observes real model/speech work; status polls do not manufacture AI load.
- Additive deployment-only drain/maintenance controls: pause new configuration/link/consent/start without stopping existing live workflows; optionally continue audited room closure and retention with the feature hard-off. Existing global worker isolation and default-off behavior stay unchanged. No new migration is needed for these flags.

## Files, schema, dependencies

Backend additions: Payroll.API/Models/InternalInterviews.cs, Database/InternalInterviews.sql, Repositories/InternalInterviewRepository*.cs, Services/InternalInterview*.cs and RecruitmentAiScoringService.Interview.cs.

Existing API changes: registration, additive migrator, opt-in invite resolution and one engine definition. **Four feature-owned tables; no existing recruitment-table columns altered.**

UI additions: InternalInterviewPage, InternalInterviewSetup, InternalInterviewDashboard, InterviewMediaRoom, InterviewVoiceControls, InterviewEvidenceReview, InterviewParticipantHistory, internalInterviewService, interviewEventBuffer, interviewVoicePlayback, interviewParticipantHistory and internalInterviews types. Existing routes, schedule editor and tracker link these in. LiveKit client 2.22.3 added; prior locked package versions retained.

Prepared deployment: deploy/internal-interviews/compose.example.yml plus private speech server/Dockerfile/tests. See [deployment guide](internal-interviews-deployment.md) for environment variables, service topology, migrator and release gates.

## Verification — 20 September 2026 IST

- Backend interview guards/repository suite: **83 passed**. Actual loopback MySQL 8.4.9 disposable GUID database, migration twice, consent/scope/question snapshots, mocked-model AI orchestration, human-result preservation, protocol-confirmed closure retry, hybrid section selection, participant revocation, unstarted reschedule and bounded room lifetime. Direct real HTTP checks cover anonymous/wrong-location denial, signed candidate access, wrong interview, invalid consent, private recording denial, byte ranges, feature-off and drain admission/read-only access. Additional tests cover default-off inactivity, all flag combinations, live completion during drain, maintenance-only cancellation/closure, expired evidence purge, combined-summary evidence grounding/input bounds and malformed-summary persistence/manual retry/idempotency with hiring results unchanged. Signed media callbacks cover tampering, expiry, unrelated/case-variant rooms, concurrent duplicates, conflicting IDs, retention/null-retention rejection, explicit maintenance admission and candidate exclusion. Queue tests cover awaiting-answer, failure/manual retry and voice-grace readiness. Test database dropped afterward; neither local nor production payroll database modified. Separate live-model and full-portal fixtures are opt-in; see their results below.
- Existing backend local-protocol/engine/resume-recovery regressions: **146 passed**.
- Actual Program/auth/recruitment/browser fixtures: **Human, AI and Hybrid all passed**, bringing the combined backend run to **86 passed, 1 live-model test explicitly skipped**. Normal HR login/cookie, existing schedule drawer, question-bank/configuration/link, consent, human and bank/follow-up Q/A, hybrid section handoff/return, malformed-model review failure, human retry, saved per-answer drafts and combined summary, completion and existing feedback editor persist to independently inspected disposable loopback GUID databases. Exact answer/review counts, one combined summary and unchanged Pending hiring result are verified; timestamps are UTC. Fixture accounts/databases are removed in finally. Only the model is mocked in the actual AI processor; no production data, SMTP or live media/speech/provider inference in these tests. The final null-retention/canonical-room callback guards were followed by another passing 83-test non-portal run.
- UI deployment/model-action/chart/history/event-buffer/audio-graph/evidence-review/participant-history/timeout regressions: **46 passed**.
- Speech HTTP contracts: **7 passed**, mocked STT/Piper; actual speech models are unverified.
- Actual built UI / mocked API Playwright: **25 checks passed**, zero browser errors. Bank/setup/settings reset, candidate link, consent, waiting, maintenance admission pause/live completion, human Q/A, synthetic microphone, mocked WAV/STT editable draft with honest local-only label, private-data exclusion, event network failure/reload retry, hybrid control, live AI-error visibility, stationary/fullscreen confirmation, completion, combined-summary display/recovered-error history, full-width review without recordings, ordered participant history/candidate exclusion, bank-only maintenance, native range-video metadata, mobile width and invalid link. Replay uses a short browser-generated WebM, not real Egress output; long-recording playback remains unverified.
- Screenshots inspected under .codex-validation/internal-interviews/ui, portal/ai and portal/hybrid. These prove rendered synthetic-model drafts and real saved timeline data, not real video or a completed live-provider AI review. Earlier failure artifacts remain timestamped; use each mode's latest result.json for the final passing run.
- Workspace build and **isolated locked-dependency frontend build passed** with updated source, including the modal/fullscreen and audio-publication changes, and locked Ant Design 5.5.0/Vite 8.0.16. The isolated dependency install was created using fresh npm ci in the preceding validation; the final lockfile hash matches the workspace. Final isolated copy: C:/Users/EESL/AppData/Local/Temp/hrms-interview-ui-final-9c1c2d8ea3d949ee9e7378ea071a3be7. Host Node 24 is not a Linux/Node 22 Docker deployment test.
- Existing MailKit NU1902, unrelated nullable warnings, qrcode React peer warning and large bundles remain separately noted.

The live-model fixture now includes a third, synthetic combined-review request after its existing follow-up and per-answer checks. That extension has not run successfully against the actual provider; the observed gateway failure below remains the latest real-service evidence.

### Actual local gateway test — failed overall

Opt-in InternalInterviewLiveLlmTests reads the supplied private environment path in memory; no secret copied/logged. Uses actual interview prompts/evidence guards, but does not prove saved HRMS-provider selection or full portal inference wiring.

1. Approved follow-up choice: HTTP 200, complete validated JSON, **18.173 seconds**.
2. Per-answer review: **HTTP 503 after 16.312 seconds**; no valid review, no cloud fallback.
3. Later invalid max_tokens:0 probe (no inference) returned **503 / unavailable**, confirming closed/stopped admission.
4. Both read-only WinRM checks to 10.10.91.100:5001 timed out. Stop reason is unverified; watchdog/resource failure is only a possibility. No restart attempted.

Live test is explicitly skipped in deterministic runs; its observed failure must not be hidden by passing mocks.

## Remaining release blockers / incomplete work

### Camera/audio checkpoint — 20 September 2026

Added local pre-join preview, device selection, bounded microphone-level display,
speaker tone with human confirmation, explicit permission/hardware errors, cancel
and late-permission cleanup. Join retains mute/device choices; in-call device changes
keep AI voice separate. Selected devices are reused for voice answers/questions.
No schema/package addition; feature remains off on the main shared-database API.

Verification in this slice: 54 focused UI/service regressions passed, 84 interview
backend tests passed (portal/live model opt-in skips), workspace production build and
fresh isolated `npm ci --ignore-scripts` / build passed against locked AntD 5.5.0.
Isolated copy: `C:/Users/EESL/AppData/Local/Temp/hrms-interview-devices-fd6750d84d59494088e0baec82c21ddf`.
Known dependency audit warnings (10 findings: 2 moderate, 8 high), React peer,
MailKit and bundle warnings remain; this is not a clean security/deployment claim.

Headed Playwright using the actual built UI and synthetic Chrome camera/microphone
passed 33 checks including permission denial, missing/busy devices, device removal,
late permission cancellation, exact device IDs, camera-off, local signal detection,
speaker confirmation and mobile layout. No browser errors. Screenshot files
`camera-audio-check.png` and `camera-audio-mobile.png` were visually inspected under
`.codex-validation/internal-interviews/ui`; latest `result.json` is the test record.
These are **synthetic devices / mocked API**, not a physical headset/webcam or
remote media-server test. Link-change/unmount cleanup also passed. The existing
real-API portal rerun was interrupted at the user's pause request. Its AI-mode
fixture passed; the other modes have no new passing result. The remaining run-owned
loopback test database was removed. Temporary fixture files remain because the
cleanup command was policy-blocked; these contain synthetic test attachments/keys,
not production data. No new test or service startup should be triggered during this pause.

Actual saved-server gateway probe through the opt-in interview fixture again failed:
HTTP **503 in 0.534 seconds** on the first synthetic follow-up request. No retry,
cloud fallback, server restart or key change. Docker Linux engine is unavailable;
local media/speech ports are not listening. Per user instruction, leave installation,
real-service startup and deployment paused for the next discussion. Do not mark this
goal complete or enable real candidates based on the synthetic-device checks.

20 September follow-up: the deterministic backend subset was rerun with **84 passed,
2 explicitly skipped** (full portal and live model require opt-in). Local Docker's
Linux-engine named pipe is still unavailable; loopback media/speech ports are not
listening. The production LLM admin port read-only check timed out. No production
restart, media provisioning, migration or model inference occurred in this follow-up.
The existing real-service failure below/above remains unresolved; passing recruitment
and synthetic interview fixtures cannot close this goal.

1. Restore authorized VPN/server connectivity, inspect runtime/watchdog failure, then propose exact recovery. Server operations rules require preview and explicit approval for production restart/file changes.
2. Approve actual LiveKit/Egress/speech host, DNS/TLS/TURN/firewall/private mounts and capacity. Local Linux Docker daemon is unavailable; no video/speech services/models installed for this feature.
3. Real two-browser video/TURN/reconnect, real microphone/voices, long recording/replay/retention and resource/concurrency validation. Fully lock speech dependencies and verify model licenses/checksums.
4. Actual Program/DB login → schedule → internal session → draft/retry → feedback browser journeys now pass for Human, AI and Hybrid (tools/test-internal-interview-backend.mjs --portal). Bulk internal scheduling, approved real invite delivery and existing pipeline notification hooks need further verification.
5. AI TTS room publication is implemented with focused graph/cleanup tests, but actual panel delivery and recorded Egress audio remain unverified. Multiple recording segments/retries and real long-video seeking, participant-removal timing and room reconnect still need implementation/integration validation.
6. Started sessions require another round if the original schedule changes. Unstarted reset and independent bank UI are implemented and locally tested; validate notification resend and late-link races with real services.
7. Load, late responses, link rotation races and real restart/drain tests remain. Local tests cover drain admission, active-session completion and explicit feature-off maintenance. Verify coordinated production flags and at least one active maintenance worker before stopping services; the global worker-off switch intentionally still stops work.
8. The combined-summary contract and durable orchestration are implemented with grounded unit/database/browser checks. Actual local-provider accuracy and 256-output-token behavior remain unverified. Current input is the FULL question/answer set within the saved local provider's 12,000-byte combined content budget; oversized sessions require manual review or a separately validated larger context/chunking design. Two highlights are explicitly not exhaustive. Signed participant-history ingestion and private UI now have HTTP/database/browser checks, but actual media-server delivery, reconnect/late-callback behavior and event-volume limits still require real-service validation.

### Expanded real-API browser coverage — passed

`InternalInterviewPortalTests` runs separate Human, AI and Hybrid fixtures. Each uses actual Program/auth/recruitment APIs and a fresh disposable database. A fixture-owned background loop calls the real durable AI queue/repository processor with a strict synthetic model; it never enables unrelated production workers or uses an API key. The model deliberately returns one malformed per-answer draft so the browser must display the failure and request a human retry. The harness independently verifies 1/2/3 saved answers and per-answer drafts, one combined review, unchanged hiring outcome, working evidence links, resolved-error history and candidate exclusion from private drafts. This is not an actual model, media, STT/TTS or hosted-worker deployment test. A transient harness assertion after Start observed AntD's `loading Ask question` name; it now waits for the real accessible action name instead of force-clicking or adding a fixed sleep.

Do not mark complete or enable real candidates based on mocks. Continue from this checkpoint, without replacing existing LLM/recruitment wiring.
