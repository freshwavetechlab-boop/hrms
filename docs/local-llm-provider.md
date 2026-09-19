# Local LLM provider: integration and rollout

## What changes

`LocalOpenAICompatible` is an opt-in alternative primary/fallback provider for existing resume parsing, JD extraction, ATS AI analysis and FrevoPilot. Ordinary cloud-provider payloads and defaults remain unchanged. Existing deterministic parsing/scoring, encrypted credential storage, provider ordering, quotas and access controls are reused. No new schema migration is required for this provider.

In **App Settings > AI Integrations**, choose **Local LLM (OpenAI-compatible pilot)**, model `hrms-local`, and exact endpoint `https://eeslindia.org/llm-api.php`. Enter the gateway key privately; never put it in source control. Save and test before enabling. To use it automatically, enable availability and choose **Make active**. Cloud fallback occurs only if Auto Switch and the relevant cloud models are enabled.

For isolated verification, global super admins can select the saved local model in FrevoPilot's preview selector without changing the active provider. Disabled saved models are intentionally available for this per-run preview. Client users cannot access the analytics control plane.

## Current capacity is a pilot limit, not model capability

| Layer | Current behavior |
| --- | --- |
| Runtime | CPU-only Qwen3.5-4B Q4_K_M; 2 threads, one generation slot, 4,096 context |
| Public gateway | 256 output tokens, 12,000 content bytes, 16 KiB request, 10 requests/minute, 180-second deadline (approved 19 September 2026); PHP budget 190 seconds |
| HRMS local profile | Default 210-second request window, configurable 60–600; bounded admission; no inline retry storm |
| Browser AI document operations | Scoped 660-second window; ordinary API defaults unchanged |

After a gateway/request timeout or in-flight cancellation, the HRMS local transport waits out a 90-second settlement cooldown by rejecting new local sends promptly, not by holding callers asleep. This is shared across engines **within one API process**, not a distributed lock; the gateway remains the cross-instance admission boundary. Server logs showed that cancelled CPU prompt processing can finish later than the public 504 response.

Increasing an HRMS timeout does not change the gateway deadline, speed up CPU inference or enlarge context. Oversized requests and truncated/invalid JSON are errors, not valid partial results. Small local resume/JD suggestions are source-grounded and merged through the existing parser; they are not full cloud-sized extractions. Scanned PDFs need an existing OCR path or manual review.

Local FrevoPilot uses compact SQL/summary contracts. For recognized count and leave-day aggregate questions it supplies the required identity, scope, grouping, measure, filter and relationship columns, plus a host-derived metric contract. Unrelated optional knowledge chunks are omitted as whole sections. Complex/unknown/repair requests retain the full authorized schema. Security and business rules remain mandatory. Results still pass the existing SQL validator and read-only execution limits; the host metric contract is checked before execution and retained during repair.

## Fast exact metrics and controlled learning

**Fast verified metrics** is enabled by default in FrevoPilot and applies only when the first selected provider is local and the server recognizes an exact supported metric contract. The server compiles that contract into one parameterized SELECT, then runs the same SQL, semantic and client-scope checks plus read-only database execution. It does not ask the small model to invent SQL for an already-known count/SUM/AVG request. The UI labels this **Verified metric · fresh data**, not an AI-generated answer. Unknown/complex questions still use the selected model, and cloud-first provider behavior is unchanged.

Turn the portal switch off to disable new exact-metric compilation for a run, or set `FrevoPilot__FastMetricsEnabled=false` globally. Previously validated query reuse is a separate mechanism; disable it independently with the setting below when benchmarking fresh model inference. The shortcut does not retrain the model or make arbitrary new questions instant.

Verified query memory applies only to recognized local-provider metric contracts. A plan becomes eligible after SQL/semantic/client-scope validation and successful read-only execution. Failed plans are not learned. Every reuse is revalidated and reruns the database query; results, rows and AI narratives are never cached. Repeated dashboards show **Verified query · fresh data**, with a computed current-row description instead of a second AI narrative request.

Memory is isolated by actor/roles, selected client, database, current schema, executable/security-policy fingerprints and provider configuration. It retains at most 128 entries for seven days, capped at 1 MiB plaintext memory and a 2 MiB encrypted file. Query plans are encrypted at `App_Data/FrevoPilot/validated-plans.enc` using a separate purpose under the existing portable protector; no new key ring or migration is needed. Preserve this private directory in the deployment volume for restart persistence. Code/schema/policy changes intentionally cause cache misses. Storage/key failures cannot fail a dashboard. Set `FrevoPilot__VerifiedPlanMemoryEnabled=false` to disable reuse; per-run receipts still support exact metric compilation without retaining another run's plans.

This is validated query reuse, not model-weight training or a guarantee against future mistakes. Unsupported new/complex questions still require CPU inference. Measure their latency separately. FrevoPilot monitoring now counts dashboard workflows rather than status polls; workflow busy time includes AI/database waits and is not CPU usage.

## Safe deployment

- The verification model was saved disabled. Existing active providers and Auto Switch were not changed. Do not enable it globally merely because the connection test passes.
- Deploy the HRMS code through the normal process. Coordinate/drain old ATS workers before enabling long local-provider jobs: updated workers honor a dedicated MySQL advisory lock, but old recovery code does not.
- Verification APIs sharing the production database use `BackgroundWorkers__Enabled=false`. This is a test-process override, not a new production default.
- Gateway changes require the Server100 workspace's exact preview, backup, approval and rollback workflow. After specific approval, the 45→180-second gateway / 50→190-second PHP change was activated on 19 September 2026. Backup, deployed hash, original ACL and unrelated configuration hashes were verified. No Apache/runtime restart, output/context increase or model replacement was performed.
- Roll back availability by disabling the local model and retaining the previous cloud primary. Do not delete application data or reset ATS jobs to switch providers.

## Evidence and remaining limits

Final localhost verification, 19 September 2026: **14 exact-metric dashboards and 14 repeats passed** headed Playwright checks against independently authored read-only SQL, with zero local-model requests. First backend workflows took 0.87–2.04s; the monitoring screen measured 28 actual dashboards, average 1.4s and p95 2.0s. Two additional API-restart checks reused persisted plans in 1.40–1.56s with fresh DB results and zero inference.

Latest focused checks: 313 backend tests, 128 Node tests, 13 actual C# compiler fixtures through Node guards, and 5 UI behavior tests passed. Backend/UI builds passed. Screenshots were inspected for scoped counts, job grouping, category comparisons, averages and empty results. The final review and named run archives are in `playwright-e2e/artifacts/local-llm-review/REVIEW.md`; the latest JSON alone is not the whole test history.

The final retrieval regression matters: a real CurrentStage question pushed its required clients table to rank 15, outside the top-12 shortlist. The schema selector now retains an existing authorized client dependency within that cap. Invalid host-compiled queries fail closed without speculative AI repair. Client-scoped interviews must match their application parent rather than retain other-client rows through a LEFT join.

**These are fast verified metrics, not faster model generation.** Before this path, actual AI interview/gender requests took 80.94s/51.23s; an offer-plan repair failed after 184.60s. Unknown/complex questions still use CPU inference and can be slow or fail. Full-document resume/JD/ATS and broad natural-language accuracy remain separate validation work. Earlier compact synthetic extractions are not full-workload certification.

The approved public gateway timeout update was verified through a cold 79.683s synthetic extraction and security/concurrent-busy checks. Public/origin English/Hindi baseline checks passed. At 08:21:53 UTC, watchdog READY, unchanged Apache/model identities, original ACL and unrelated hashes, and no new monitored server errors were confirmed. No further server mutation or HRMS production deployment was performed.

There is no model training, zero-bottleneck guarantee, or claim that mistakes can never recur. Roll out the opt-in provider deliberately; preserve the existing cloud fallback and deployment rollback.
