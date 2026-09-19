# Engine history and parser verification — 19 September 2026

## Changes

- See [engine-history.md](engine-history.md): five-minute numeric aggregates, one-minute checkpoints, 30-day retention and calendar date selection. No existing business table changes. Exact active global-super-admin access; telemetry failures do not block work.
- Resume parser 2.5 no longer scans arbitrary raw PDF/image bytes for text operators. This prevented OCR from running on the supplied scanned JD. Only supported content streams/text objects are inspected before OCR fallback.
- Windows has a packaged native OCR fallback when Poppler is unavailable. Docker's existing Poppler/Tesseract path is retained. OCR is bounded (40 seconds, two concurrent operations; Windows rejects documents over five pages). Unsupported/uncertain documents still need review, not invented facts.
- Preserve CI/CD skill category, exclude declarations from education and choose VTU rather than the preceding subject as institution. Explicit position labels beat document titles; wrapped qualification/table labels are joined conservatively; optional skills and certifications are separated from required skills and UIDAI values.
- Preserve source-grounded structured JD skills, not just the first three. Only the explicit local provider may skip redundant generation when the parser has sufficiently strong source facts. Set `LocalLlm__PreferVerifiedHiringFacts=false` to opt out. Cloud provider ordering and ATS inference are unchanged. This is deterministic parsing/RAG, not a faster model or model training.

## Evidence

- 186 focused backend tests passed, including opt-in actual scanned-PDF and isolated DEV database tests. Coverage includes role/client denial, disabled history, concurrent work, duplicate completion, long tasks, retry idempotence, restart/reload, weighted multi-writer totals and timezone boundaries.
- 11 frontend tests passed; production UI build passed (existing large-bundle warning). Existing MailKit advisory/nullable warnings were not changed in this work.
- Real DEV API: supplied scanned Platform Engineer PDF parsed in **3.781 seconds** using `LocalOCR + LocalRAG`; human-transcribed same JD in **0.199 seconds**. Both retained 2 openings, minimum 6 years, INR 13–24.8 lakh salary, qualification, 15 required skills and Terraform/Ansible/Chef as preferred. API result status was Parsed with explicit review warnings. Model status `LocalFactsSufficient`; independently checked usage counters did not change.
- Arjun's actual DOCX parsed in **0.252 seconds** with all 12 harness checks passing, including identity, 84 months experience, current employer/title, skills and one VTU education record with no declaration entry.
- DEV provider settings were restored to their prior state after previews. No candidate, requisition, attachment or mail was issued. History API totals for all 8 engines matched an independently queried DEV database (5 saved JD attempts at the verification point).
- Main local API 5062 restarted with business workers still disabled, retaining its existing database target. History endpoint returned 200, deployment `local-windows`, recording enabled and no warning. The numeric history table was initialized; production service was not deployed/restarted. Development and local-windows telemetry remain separated.

Artifacts: `playwright-e2e/artifacts/history-ocr/2026-09-19T10-27-07.039Z-report.json`, `playwright-e2e/artifacts/jd-resume-service-run/arjun-service-20260919-101342-100.json`.

## Still not claimed

- No new browser screenshot/E2E run in this slice; API, real-file, database, unit and build verification were performed.
- OCR prose still has occasional character/spacing errors. All extraction remains editable and requires review. Wider mixed-format/low-quality OCR and production Linux OCR accuracy require more fixtures.
- Actual first-time local ATS/model inference previously measured around 72 seconds. This slice does not claim to accelerate that inference: it avoids redundant **JD extraction** calls only. No remote gateway/model settings were changed.
- Yesterday's previously unsaved live graph cannot be recovered. Saved history starts with this feature; abrupt process termination may lose the last unflushed minute.
