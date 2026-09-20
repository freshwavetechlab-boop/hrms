# Internal interviews — deployment and validation gate

Status: implementation under test, feature **off by default**. No production migration, server install or LLM change has been performed. This guide is a prepared deployment plan, not evidence of a successful deployment.

## Reuse and scope

The existing schedule, panel assignments, client/location authorization, notification delivery, competency feedback and hiring result remain authoritative. The schedule drawer can opt into an internal session; questions/consent are configured after the existing schedule saves. Candidate invites contain only the candidate's expiring link; panel invites use normal authenticated HRMS access. Completing the media session does not select/reject a candidate or complete the original interview result.

The interview AI defaults to the **existing saved LocalOpenAICompatible provider** and its encrypted key, usage ledger and bounded inference gate. It does not use Gemini/Groq/cloud fallback. The separate previously supplied server workspace `.env.example` is not copied into this repository. `OllamaBaseUrl`/`OllamaModel` are optional only if the operator actually has that service; do not replace the verified llama.cpp gateway based on a model/product name in a prompt.

## Additive migration

Four feature-owned tables: `recruitment_internal_interview_sessions`, `recruitment_interview_questions`, `recruitment_internal_interview_events`, `recruitment_internal_interview_recordings`. No existing recruitment-table columns are altered. The narrow migrator also upgrades early local preview versions of these four tables.

After backup and explicit target verification, set the intended connection in the command environment and run:

```powershell
dotnet run --project Payroll.API -- --migrate-internal-interviews
```

Do not use `--prod`/the old full migrator as a shortcut. `appsettings.json` can target shared production, whereas Development is currently loopback. The automated repository tests create/drop only their own GUID-named local database; they do not migrate the configured `payroll` database.

## Private service topology

Prepared compose: `deploy/internal-interviews/compose.example.yml`. LiveKit 1.13.7, Egress 1.14.1, livekit-client 2.22.3; speech helper faster-whisper 1.2.1 and Piper 1.8.0. Pin reviewed image digests and a compatible Valkey image before release. Speech dependencies still require a fully resolved/hash-pinned build lock and Linux image build verification.

- LiveKit: operator's WSS domain via the existing HTTPS proxy; TCP 7881 and UDP 7882 for media, self-hosted TURN UDP 3478 / TLS 5349. Configure real public IP, DNS, TLS and firewall/NAT; verify from a second network, including UDP-blocked clients. WSS is not TURN TLS. Do not use Google's default STUN list.
- **Required `room.auto_create: false`**: only the server creates consented live rooms. Otherwise an old unexpired join JWT could recreate a deleted room. Candidate JWTs never carry room-create/admin/record grants.
- Egress: separate private worker, sandbox profile from the pinned upstream source, shared private `/recordings` volume. Do not disable the Chrome sandbox or add `privileged` to make tests pass. Current upstream guidance indicates roughly 4 CPUs/4 GB per composite Egress worker; measure real concurrency before provisioning.
- Speech: internal-only port 8094, separate shared credential, no public mapping, no runtime model downloads. Provision a licensed CTranslate2 Whisper model at `/models/whisper` and approved Piper `.onnx` + `.onnx.json` voices (`en`, optional `hi`). Verify each model license and checksum independently. Missing language/model returns an explicit failure, not cloud speech.
- Attach the HRMS API and reverse proxy to the prepared private network, or configure equivalent routed private endpoints. Mount the same recording volume into HRMS; never expose it with nginx/static-file hosting. Verify UID ownership, space/quota, backup and deletion policy before recording real candidates.

No new services are installed on the existing Windows LLM server. Exact production files/services/restarts require that workspace's approval/backup/rollback process. Docker's local Linux daemon was unavailable during implementation; template preparation is not a container or external-media test.

## HRMS API environment (secrets belong only in deployment settings)

| Setting | Purpose |
| --- | --- |
| `InternalInterviews__Enabled=true` | Enable only after migration/service verification; default false |
| `InternalInterviews__DrainMode=true` | Pause new configuration, links, consent and session starts; existing live interviews, review and maintenance continue. Default false |
| `InternalInterviews__MaintenanceEnabled=true` | Explicitly keep room closure/retention running with the feature disabled. Default false; requires the additive tables and existing global workers enabled |
| `InternalInterviews__PublicPortalBaseUrl` | Portal HTTPS origin; localhost HTTP only for local testing |
| `InternalInterviews__LiveKitUrl` | Browser-facing `wss://…` media endpoint |
| `InternalInterviews__LiveKitApiUrl` | Private LiveKit HTTP(S) API |
| `InternalInterviews__LiveKitApiKey`, `InternalInterviews__LiveKitApiSecret` | Private signing credentials; never returned to UI |
| `InternalInterviews__LiveKitWebhooksEnabled=true` | Opt-in signed media-history receiver; default false. Enable only with the configured private callback route |
| `InternalInterviews__RecordingDirectory` | API-side absolute path to the private shared recording mount |
| `InternalInterviews__EgressRecordingDirectory=/recordings` | Matching Egress-side mount |
| `InternalInterviews__MaxRecordingBytes=536870912` | Final replay-size guard; not a live filesystem quota |
| `InternalInterviews__SpeechBaseUrl=http://speech:8094` | Private speech sidecar |
| `InternalInterviews__SpeechApiKey` | Same private sidecar credential |
| `InternalInterviews__LocalModelId` | Optional explicit existing saved local model ID; otherwise the current primary must be local |
| `InternalInterviews__OllamaBaseUrl`, `InternalInterviews__OllamaModel` | Optional verified direct Ollama adapter, not necessary for the existing gateway |
| `InternalInterviews__ProviderTimeoutSeconds=120` | Direct Ollama budget; existing local gateway retains its own limits |
| `InternalInterviews__RetentionDays=30` | 1–365-day evidence retention |
| `BackgroundWorkers__Enabled=true` | Existing workers plus separate internal media/AI workers |

Preserve the existing `AttachmentStorage__DataProtectionKeyPath` across API restarts/replicas. Candidate link protection reuses that application's persisted protector with a separate purpose. Losing the key volume invalidates issued candidate links; it is not an AI-key migration. Verify API allowed-origin configuration also permits the `X-Interview-Token` header from the actual portal.

### Private media callbacks

Set compose `INTERVIEW_MEDIA_WEBHOOK_URL` to the HRMS API's privately reachable `/api/public/internal-interviews/media-events` URL and enable `InternalInterviews__LiveKitWebhooksEnabled`. Despite the public-route prefix (needed to bypass user-cookie login), this is a machine-authenticated endpoint: HS256 signature, configured issuer, JWT lifetime and SHA-256 of the exact raw body are mandatory. Normal room tokens/candidate links/admin cookies cannot authorize it. Keep the route restricted to the media network at the proxy where possible; preserve the `Authorization` header and unmodified body. HTTPS is required over untrusted networks. Keep host clocks synchronized.

Only signed participant join/leave/aborted and room start/end metadata is retained, under the existing private event table and client/location/panel authorization. Arbitrary participant attributes, tokens and raw callback bodies are not stored. Event UUIDs are idempotent; conflicting reuse fails. Unknown/noncanonical rooms, missing consent/start, expired/null retention and purged sessions cannot create/restore evidence. Late callbacks can still be received during explicit feature-off maintenance; all-off rejects them. Candidate APIs exclude this history and AI analysis never receives it.

The panel UI distinguishes source event time from HRMS receipt time and orders delayed events by source time. This is connection evidence, not proof of attention, attendance duration or candidate suitability. Callback delivery can have gaps; do not infer continuous attendance or score candidates from missing events. Real self-hosted callback delivery/reconnect remains a release test, not established by signed synthetic HTTP fixtures. The validation contract follows the official [LiveKit receiver](https://github.com/livekit/node-sdks/blob/main/packages/livekit-server-sdk/src/WebhookReceiver.ts) and [webhook protocol](https://github.com/livekit/protocol/blob/main/protobufs/livekit_webhook.proto).

## Behavior and honest limitations

### Camera/audio setup (20 September addition; activation paused by user)

The existing session now includes a browser-local pre-join camera preview, microphone
level meter, explicit speaker-test confirmation and camera/microphone/speaker selectors.
No device is opened on page load. Candidate checks appear after consent. Stop,
unmount/link change and late permission replies release acquired tracks. Device IDs
and preferences remain in page memory, not server logs or persistent storage.

Join respects selected inputs and camera/microphone-off choices. Inputs start
independently so a camera denial does not discard working audio. Call device controls
exclude the separate AI voice publication when switching the human microphone; a
blocked browser audio playback state offers an explicit Enable call audio action.
Voice-answer capture and AI question playback reuse the selected input/output.
Optional checks do not reject candidates or bypass any server consent/access guard.

Use HTTPS (localhost is suitable for local checks). Where output-device selection
is unsupported, choose the speaker through system/browser settings. A rendered
preview and moving level meter are local device checks, **not** verification of
remote reception, echo quality, recording, TURN connectivity or STT accuracy.
Actual headset/webcam, permission revoke/unplug during a call, reconnect and
two-person audio/video tests remain required before enabling real interviews.
See [browser media permissions](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getUserMedia)
and [audio output selection](https://developer.mozilla.org/en-US/docs/Web/API/HTMLMediaElement/setSinkId).

No new database migration, package, service credential or environment variable is
needed for these controls. Existing default-off flags and prepared service topology
remain unchanged. Per user instruction, do not start Docker/services, deploy or
modify the LLM server until the next setup discussion.

- Typed answers and voice drafts require consent. Local STT fills an editable draft; it is not an automatically submitted answer. The fixed 120-second voice-processing/review allowance is anchored to the first voice request and never extends the overall scheduled end.
- AI questions come from the immutable selected bank. Follow-ups are at most three explicit approved question lines; Qwen may select a relevant allowed line, not invent another topic. Initial introduction/bank flow is labelled configured flow, not model-generated output.
- Hybrid handoff may select a skill section from that frozen bank. Unknown sections are rejected. Returning to another section does not repeat already asked questions or bypass the overall question limit.
- Reviews are durable per-answer draft observations with exact answer quotes and host-bound question/answer IDs. Failed inference waits for an explicit panel retry. No browser events enter the model, and no score/hiring recommendation is saved into the recruitment workflow.
- The AI queue admits actionable live turns only: introduction, submitted answer or an elapsed response window. Waiting questions and current-turn failures do not occupy the first worker page. The voice grace matches answer admission's bounded deadline; human retry or a new control section may make a failed turn eligible again. This is tested with actual MySQL, not a claimed production concurrency benchmark.
- A separate combined review uses the complete submitted question/answer set, never browser observations or AI-written per-answer prose as source evidence. At most two short highlights may include directly supported cross-answer clarification, with quotes from at least two distinct answers. Host-bound answer/question IDs and timestamps link back to the original timeline. Unanswered questions/counts are computed by the host, not invented by the model. Human notes remain attributed separately.
- Combined input must fit the current 12,000-byte content budget (including system instructions) and 256-output-token local profile. Oversized transcripts fail visibly; no silent truncation, partial-input summary or automatic cloud fallback. This is not exhaustive analysis. Larger-session context/chunking and real-model accuracy still require validation. Failed combined analysis does not erase per-answer drafts and waits for an explicit human retry; successful work is idempotent. Recovered failures remain as history, not current warnings.
- Media startup/closure is persisted and retried by the server. Closure failures remain visible; do not assume users disconnected until closure is confirmed. There is a bounded maintenance cadence, not a promise of immediate closure if the media server/network is unavailable.
- Egress metadata is reconciled separately; Ready means a finalized local file was verified. Missing/failed output is not displayed as a recording. Current implementation expects one composite file; unexpected multiple segments fail visibly rather than silently replacing an old file.
- Replay uses the existing HttpOnly API login cookie and private range responses, not a public/token-bearing recording link or a full-file blob. Verify portal/API cookie and CORS behavior through the real proxy; bearer-only legacy sessions must sign in again. Each HTTP range request is freshly authorized. Long-session seeking still requires real validation. Timeline alignment uses explicitly UTC server event timestamps, **not word-level STT alignment**.
- Interviewer TTS is published as a distinct named audio track when the candidate is connected, using the already permitted microphone source. The real microphone's controls exclude that track. Playback begins only after publication acknowledgement; question changes/disconnection stop and release it. Without a connected room, the UI explicitly labels local-only playback. Real panel reception and Egress capture still need verification; graph mocks do not prove recorded speech.
- Live room participants are checked against fresh active-user/client/location/role/panel access on the maintenance cadence. Removal failures remain visible; do not promise immediate revocation while media/network is unavailable. Room empty/departure timeouts cover the remaining bounded schedule so a five-minute outage alone does not recreate a room/overwrite a recording.
- When an unstarted original interview is rescheduled, Use updated schedule revokes old links/consent and preserves the question snapshot/audit. A started session cannot be reset. Question-bank edits affect future configuration, not the consented snapshot.
- Retention removes only the known interview file, transcript, notes and AI drafts after confirmed room closure; it does not delete the candidate or original hiring record. Downloaded copies cannot be revoked retroactively.
- Prefer `Enabled=true`, `DrainMode=true` for planned maintenance: admission is blocked in repository methods as well as UI; live sessions can reconnect, save answers, complete and produce draft reviews. Leave `BackgroundWorkers__Enabled=true`. After rooms close, use `Enabled=false`, `MaintenanceEnabled=true` to continue retention/private-media finalization without exposing the feature or accepting new work. If disabled while a session is still active, maintenance cancels only that internal session and requests room closure; the original hiring result is untouched. The server records the reason. Closure can still fail and retry, so verify `MediaState=Closed` before shutting down the media service.
- All-off defaults remain inert on installations without these tables. `BackgroundWorkers__Enabled=false` remains the global stop/isolation switch and disables maintenance too; at least one correctly configured worker instance must remain running until room closure and retention responsibilities are transferred. Configuration changes across API replicas need coordinated deployment, not a promise of instantaneous global drain.
- If a waiting/unstarted session reaches its deadline while admission is paused, maintenance cancels that internal session with the maintenance reason instead of blaming the candidate with No Show. Reschedule through the existing HR workflow.
- Payload-free candidate browser events use a bounded, credential-fingerprint-partitioned sessionStorage retry queue. No bearer credential or clipboard content is persisted. Offline/reload retries retain the original event ID; expired/revoked/closed sessions stop accepting evidence and show unconfirmed counts rather than bypassing security.

## Verification commands and release checklist

```powershell
node tools/test-internal-interview-backend.mjs
$env:HRMS_TEST_UI_URL='http://127.0.0.1:5184'
node tools/test-internal-interview-ui.mjs
# Separate opt-in: actual Program/auth/recruitment API with a disposable full local database.
# Requires built UI preview and permission to create/drop its GUID test database.
node tools/test-internal-interview-backend.mjs --portal
```

Speech HTTP contract tests: `python deploy/internal-interviews/speech/test_server.py` (mock voices/STT, no downloads). A temporary signed portable Python was used for these checks; this does not validate actual Whisper/Piper packages/models.

Before enabling real candidates: clean isolated `npm ci && npm run build`; backend tests; API-route negative tests; real HR login/scheduling/invites with approved recipients; two-browser real video/TURN/reconnect; actual microphone STT/TTS in both configured languages; recorded media/replay; actual saved local-model questions/review; original schedule cancel/reschedule/delete; late model/transport responses; retention failures/recovery; load/concurrency/disk tests; inspect screenshots and logs. Record mocks and actual service checks separately. Keep the feature off if any required service/privacy boundary fails.

Current measured results and unresolved failures are in [the implementation checkpoint](internal-interviews-implementation.md). In particular, the real local-gateway follow-up passed in 18.173 seconds, but answer review returned 503; gateway admission subsequently reported unavailable and WinRM was unreachable. This is not a passing full AI interview.

References: [LiveKit deployment](https://docs.livekit.io/transport/self-hosting/deployment/), [self-hosted Egress](https://docs.livekit.io/transport/self-hosting/egress/), [LiveKit room protocol](https://github.com/livekit/protocol/blob/main/protobufs/livekit_room.proto), [Piper Python API](https://github.com/OHF-Voice/piper1-gpl/blob/main/docs/API_PYTHON.md), [faster-whisper](https://github.com/SYSTRAN/faster-whisper).
