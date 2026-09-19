# Local-code / production-settings latency review — 19 September 2026

Operator requested localhost:5173 UI with restarted localhost:5062 API in `prod`
mode, using **only hrms-local**. Production website was not deployed/restarted.
Normal business workers on the verification API remain disabled. No global model
activation or provider configuration changes were made.

## Measurements

| Test | Duration | Result |
| --- | --- | --- |
| Original same-JD/resume AI assessment baseline | 72.167 s | Prior recorded run |
| Current model-specific ATS AI assessment, run 1 | 22.648 s | Completed, applied, one local request |
| Current model-specific ATS AI assessment, run 2 | 20.743 s | Completed, applied, one local request |
| Arjun DOCX deterministic parsing | 0.540 s | Parsed; name/contact/experience/education fixture checks passed |
| Headed browser FrevoPilot average leave days by status, AI planning forced by turning fast metrics off | 231.207 s browser / 228.711 s service | Failed semantic query validation after one repair |

Both file hashes match the original resume and JD artifacts. The two ATS assessment
runs returned identical criteria, overall fit 0.65 and confidence 0.95. This is the
AI assessment service used by ATS, **not** full upload/queue/weighted scoring/save
latency. The old parsed-JD fixture's known extraction limitations were intentionally
retained for comparison; valid JSON is not proof of correct judgment on every field.
Observed assessment time improved roughly 69–71%; model cache/host load were not
controlled, so there is no concurrency or future-latency guarantee.

The browser selected saved disabled model 6 as a **per-run preview**. The planning
request took about 75.03 s; repair took 146.64 s. The host rejected the resulting
plan: `Local aggregate plan does not match the requested metric: aggregate only
the requested record table and required host relationships.` No dashboard result
was accepted as correct; at this baseline the browser regression was unresolved.
Engine activity persisted the workflow as Failed with measured duration.

Provider counters confirmed four local inference requests (32 → 36). Gemini model
1 stayed at 64, Groq model 4 at 38, and Gemini model 5 at 20: no cloud inference.
The provider configuration fingerprint stayed unchanged. Only normal local-model
usage/health and application audit/telemetry were recorded; no candidate, stage,
attachment, payroll or email was created/changed by these tests.

Evidence:

- `playwright-e2e/artifacts/local-provider-latency/20260919-112120-report.json`
- `playwright-e2e/artifacts/local-llm-review/browser-results-2026-09-19T11-22-54-668Z.json`
- `playwright-e2e/artifacts/local-llm-review/leave-average.png`
- Local LLM Playwright suite reused with `LOCAL_LLM_CASE=leave-average`, fast
  metrics/reuse/connection probes off, and screenshots visually inspected.

Conclusion: the repeated ATS AI assessment is faster in these observations;
general LLM dashboard planning is **not** yet fast/reliable for all questions.
This testing slice does not patch scoring, planner rules or the gateway.

## Follow-up: focused planning/repair fix and browser recheck

The subsequent fix is restricted to the local analytics compact prompt and bounded
validation hints. Recognized repairs now rebuild their focus from the **original
question and authorized schema**, instead of expanding back to the whole catalog.
Failed/model-supplied contracts cannot supply this focus. Unknown/complex questions
still keep their original catalog. A host-derived single-table metric explicitly
instructs the model not to join optional parents; required scope/group/filter joins
remain intact. SQL rejection conditions, protected fields, RBAC and cloud prompts
were not relaxed. No HRMS database migration or provider activation was needed.

Intermediate tests were not hidden:

- First prompt refinement reduced the failed run to 123.252 s, but the model still
  added an unrequested join. The final refinement explicitly disallows joins only
  when every grouping/measure/filter/scope dependency belongs to the record table.
- A later attempt failed with a transport ConnectionError; an unauthenticated HEAD
  check also timed out. After the operator requested another check, endpoint access
  returned and the same actual-inference browser test was rerun successfully.

Successful run: `a5b7de17-eb49-4458-90a0-0c21883e7d50`, using model 6 `hrms-local`
with **Fast verified metrics OFF**:

| Measurement | Observed result |
| --- | --- |
| Initial dashboard, including browser polling and independent DB comparison | 62.055 s |
| Local planning HTTP request | 45.802 s |
| Local narrative HTTP request | 11.900 s |
| Repair requests | None |
| Same-query repeat, fresh scoped database read | 2.349 s; zero inference requests |

The first model-generated query was accepted by the unchanged SQL and metric guards:
`SELECT ROUND(AVG(Days), 2) AS avg_days, Status FROM essleaverequests GROUP BY Status`.
The runtime added its normal 500-row cap. Both returned groups, Pending Approval and
Approved, had average requested Days = 1. Independent authored SQL agreed exactly;
the AI summary also accurately described both values. Chart and data-table screenshots
were inspected. The repeat was visibly labeled **Verified query / verified live data**,
not a second AI-generated answer or cached business results.

Evidence: `playwright-e2e/artifacts/local-llm-review/browser-results-2026-09-19T13-47-52-284Z.json`
and `leave-average-first.png`, `leave-average-reuse.png`, `leave-average-data.png` in
the same artifact directory. Reproduction uses the existing Playwright suite with
`LOCAL_LLM_CASE=leave-average`, `LOCAL_LLM_FAST_METRICS=0`,
`LOCAL_LLM_VERIFY_REUSE=1`, `LOCAL_LLM_TEST_CONNECTION=0`.

Validation: 262 local-LLM/FrevoPilot .NET tests and 130 Node runtime tests passed;
frontend production build passed. Node suites were rerun with test concurrency 1
after two five-second fixture deadlines were exceeded during concurrent builds.
The broader engine/offer filter also encountered an unrelated offer fixture's
hard-coded logo-relative path under the custom artifacts directory; it was not
reported as a full-suite pass. Existing MailKit/nullable/bundle-size warnings remain.

This resolves the reproduced query failure, not every possible model mistake.
An uncached AI request still takes about a minute on this observed run. Model/server
cache and host load were not controlled. Fast verified metrics and validated plan
reuse avoid inference only for supported, guarded queries and still read fresh data;
neither is evidence that raw model inference has become two seconds.

Additional headed-browser checks with **Fast verified metrics ON** also passed:

| Scenario | First verified dashboard | Repeat | Independent result |
| --- | --- | --- | --- |
| Active RRU employees by gender | 4.423 s | 2.218 s | Female 101, Male 273 |
| Applications by matching job title | 4.169 s | 2.118 s | Platform Engineer 1, Chief Architect 7 |
| RRU interviews by status, required scoped application-parent join | 4.535 s | 2.159 s | Empty result matched DB; not presented as fabricated zero groups |

All six fast/repeat runs sent **zero inference requests**. These are compiled or
reused, guarded live metrics, not measurements of model generation. Evidence:
`playwright-e2e/artifacts/local-llm-review/browser-results-2026-09-19T13-49-42-922Z.json`.
The local Engine Control Center was inspected afterward with the existing read-only
browser script. It persisted the successful actual-AI workflow as **59.01 seconds**
(service duration, excluding browser/polling/verification overhead), and the shorter
verified-metric workflows separately. Screenshot:
`playwright-e2e/artifacts/local-engine-preflight/2026-09-19T13-50-59-986Z/activity-log.png`.
