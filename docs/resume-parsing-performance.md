# Resume preview and final-upload performance

Authenticated bulk/single intake can reuse an AI-reviewed preview during Final upload. The UI sends an opaque `previewToken` with each reviewed draft; parsed source facts stay in the API process. Final upload rechecks client/job access, open-position policy, candidate identity/consent and attachment validation as before.

The receipt binds the authenticated user and actor client, selected client/position/posting, Talent Pool mode, parsing/AI switches, job context, parser version, provider configuration, current approved RAG reference/vocabulary inputs, exact filename/content type and SHA-256 of the complete file. A browser `DraftReviewed` value, parser-name string or another user's token cannot substitute for a server receipt. Changed input, missing/expired/evicted token or API restart uses normal parsing and AI review.

Receipts are process-local, bounded to 32 MiB, at most 1 MiB per entry, with four-hour absolute expiry so early rows of a bulk preview can still be uploaded. No resume is persisted to a new disk cache. Only locally readable `Parsed` sources with completed AI review are cached when AI is enabled. Timeout, busy, low-confidence, unsupported, truncated and invalid AI responses are retried through the existing path, rather than being cached as successful reviews.

Final upload overlays the authenticated recruiter's reviewed values and preserves explicit zero values. It retains original local facts separately as the correction/learning baseline. Learning from one finalized candidate does not invalidate the other already reviewed receipts in that batch; another preview requires the current learning revision. Intake alone cannot bypass the existing verified-learning boundary. Published, pipeline, direct replacement and email entrypoints continue to run their shared parser/AI policy.

RAG ranking and vocabulary ranking use an additional bounded 8 MiB, 15-minute computation cache. Current database inputs are read on each retrieval and hashed with the full query, client and document kind; a changed reference or vocabulary value changes the key immediately. Existing BGE embedding caching remains in place. API logs report preview duration, cache reuse and AI status without resume text or contact details.

Validation: `ResumePreviewCacheTests`, existing resume parser/AI-review/learning/entrypoint/RAG tests, and `payroll-ui/tests/recruitment-preview-reuse.test.mjs`. These cover genuine completed-review reuse, source/settings/client/user isolation, expiry/restart, mutable-copy isolation, failed-AI retries, manual corrections, explicit zero values and legacy drafts without tokens.

On localhost, the actual UIDAI job's bulk-preview UI parsed a synthetic TXT resume with AI status `Completed`: first request 71,071 ms, unchanged repeated preview 695 ms with server-confirmed reuse. Company, current role, completed qualification and skills remained populated. Final-upload reuse and edit preservation were exercised in regression tests; the live UI test stopped at preview and created no candidate/application/resume records.

## CPU investigation on 3 November 2026

DCDB1 exposes four logical CPUs. Serving Qwen uses two CPU threads, two batch threads and one generation slot with the existing evaluated adapter. A synthetic authenticated gateway request completed in 59.63 seconds (581 prompt tokens, 77 completion tokens); this is a sample, not a bound for arbitrary resumes.

A separate low-priority, resource-guarded base-model benchmark used synthetic tokens and left serving weights, adapter, launch arguments, gateway limits and production-site configuration unchanged. For 128-token prefill, two versus three threads measured 14.68 versus 23.44 tokens/s; 16-token generation measured 4.52 versus 6.96 tokens/s. Host CPU reached 77% during the three-thread test. Keep the two-thread serving profile for shared-server headroom. The temporary authenticated diagnostic route/scripts were removed afterwards. Benchmark figures are base-model CPU throughput, not end-to-end resume timings or an accuracy evaluation of a changed serving profile.

## First preview of a different resume

Local Qwen resume requests put the unchanged extraction rules/schema in a stable system message. Candidate text, job terminology and verified client layout hints remain exclusively in the untrusted user message. The existing llama.cpp b10964 runtime checkpoints the shared prefix at the user-message boundary, allowing computation to be reused across *different* resumes, without substituting another candidate's facts. This uses the running engine's existing checkpoint/cache limits; no serving CPU count, model, adapter, output/context budget or resource guard changed. See the [pinned engine implementation](https://github.com/ggml-org/llama.cpp/blob/b10964/tools/server/server-context.cpp) around user-message checkpoints.

Resume requests retain every original chronology/education/identity/evidence rule and use minified JSON. Unchanged full-text extraction/validation and the same 7,500-byte evidence-selection budget remain in force. The model receives at most eight terminology hints that are actually present in the selected resume evidence; empty/irrelevant vocabulary adds no empty instruction block. Provider-settings and reference-snapshot probes run concurrently on separate database connections. Preview receipts include the prompt version, so a stale request shape cannot be reused after a policy change. Logs separate extraction, RAG preparation, provider review and total preview duration without logging source text or contacts.

Direct authenticated model comparisons used three synthetic sources, with a fresh AI request on each call (not the HRMS same-file receipt cache):

| Case | Previous request | Stable system prefix | Final source-grounded facts |
| --- | ---: | ---: | --- |
| Simple current role, first cold prefix | 61.15 s | 67.55 s | Employer, role, completed degree and skills preserved |
| Different resume with independent skills sidebar | 60.06 s | 43.38 s | Correct current employer/role, completed MBA and source skills |
| Another different resume, ended job and pursuing PhD | 48.84 s | 30.98 s | Latest employer/role; parser's completed B.Tech preserved |

The two subsequent different-resume cases improved by about 28% and 37%. The cold-prefix case did not improve: after engine restart or cache eviction, shared instructions still need computation. These small-sample timings are observations, not a deadline for every resume or an accuracy guarantee. Both request shapes proposed the pursuing PhD in the third case; the unchanged completed-qualification guard rejected that replacement. A regression now explicitly exercises this observed failure using anonymized source evidence.

The real UIDAI bulk-preview UI was also checked with two different synthetic TXT files against the previous and updated API builds. Both updated requests logged `reused review: False` and `AI status: Completed`: 59,060 → 41,090 ms and 56,792 → 36,968 ms (about 30% and 35% faster). Updated extraction took 528/19 ms, RAG preparation 877/259 ms and provider review 38,892/36,214 ms. Each draft's own employer, current role, completed qualification and four source skills were checked in its UI editor, then discarded; no candidate/application/resume was stored. The comparison excludes a failed local port-bind restart attempt; the final running build was verified by PID, build path and HTTP 200.

Professional-only JSON reviews may omit optional name/email/phone. The local protocol accepts its explicitly requested professional fields, then applies the same confidence, exact-source, latest-employment and completed-degree checks; empty/invented responses are still rejected. Source-verified regressions for that protocol case and pursuing qualifications were accepted by the existing learning gateway: dataset revision 5, 35 examples. This receipt is dataset intake, not new trained/activated weights; the serving adapter remained `dd2b4537-31a8-4ce5-b592-34f8188dc9e8`.

Validation: 473 selected backend tests passed, one existing opt-in test skipped; seven frontend timeout/preview-reuse tests passed; API publish passed. Shared provider behavior applies to admin/bulk, published forms, pipeline and replacement parsing.
