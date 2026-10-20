# ATS source and local-model review

Review date: 20 October 2026.

## Scope and findings

The connected database contained 12 JD versions, 275 saved resume text versions (223 primary resumes), 221 applications and 221 current ATS results. All saved JD structures and full saved resume texts were included in the read-only corpus audit. This is not a claim that every original file was available or that every result has been manually labelled as correct.

All 221 current results recorded AI analysis `NotEnabled`; all existing ATS scoring profiles also had AI scoring disabled. The global local provider was enabled separately. The existing results therefore used deterministic/semantic ATS, not a generative Qwen assessment.

The source review found parenthesized professional-experience headings and decimal hands-on claims missed by the parser, degree alternatives treated as literal strings, education-mode words appearing in unrelated prose, damaged JD extraction bytes, and an experience section copied into a certification field. Replay reported 87 differences between saved experience months and current parsing. Some historical facts were extracted by different versions or manually reviewed; differences are candidates for review, not proof that the saved facts are wrong.

Original JD attachment checks found 12 retrievable files and 9 unavailable links among 21 retained attachment references, including references for older/deleted requests. A supplied local storage-architect DOCX was also available for source review. Missing links were not repaired or replaced. Scanned/poor-text PDFs still require OCR or original-document review.

## Implemented behaviour

- Parser 2.7 recognizes professional/overall/work-experience headings, decimal hands-on/domain claims, career-summary work verbs and law degrees. Ordinary `me`/`be` prose is no longer interpreted as a degree abbreviation. Course duration and age do not supply work experience.
- Explicit degree alternatives within one requirement support their listed degrees. Separate mandatory rows and combined requirements retain review. Regular/full-time education mode needs degree-context evidence, not hobbies. Planned, expired and negated certification claims remain unverified.
- Numbered document sections terminate certification blocks. Required named-skill durations are captured from their own mandatory clauses, rather than the total-career number. New request previews retain extracted source text (up to 100,000 characters, with a truncation marker) in existing source JSON.
- Damaged JD source and certification/experience contamination create review reasons. Damaged or contaminated documents are excluded from retrieval examples.
- Optional AI input includes selected approved JD summary, role purpose and responsibilities. Long local scoring requests retain verbatim evidence from across the resume, including project/negative evidence alongside a skills-list mention. The full original still drives deterministic ATS. Existing gateway limits and explicit oversized-input fallback remain.
- Local Qwen requests a flat response with numeric ratios. Validation maps it to the existing canonical ATS contract without guessing missing fields. Free-text summaries are not requested; if returned, they must quote the actual source. Review flags must identify supplied requirements or canonical criteria. Ungrounded claims, invalid output and low confidence are not applied. The verified total-experience ratio also caps AI blending.
- Local document prompts now receive bounded same-client terminology. Source-grounding remains mandatory; retrieved data cannot supply a candidate fact. Generic evidence policy is indexed by FrevoPilot as well.
- Interview cards, tables and the new-interview application selector share eligibility/cutoff checks. Backend scheduling context and new linked interview saves enforce the same requirement. Existing interview updates/decisions remain available for audit and operational completion.

## Validation and operational boundary

Offline regression tests cover source-derived experience patterns, degree alternatives and mode, named-skill years, numbered section boundaries, local response validation/grounding and scheduling eligibility. Playwright uses intercepted APIs for card/table scheduling, high-score ineligible/review exclusions, round configuration, history and desktop/tablet/mobile operation.

Final validation passed: 207 targeted backend tests, six frontend eligibility tests, 11 Playwright checks, the UI production build and the FrevoPilot policy-index loading check.

The updated repository was exercised read-only against all 221 connected applications; scheduling context rejected the two reported ineligible applications. The local provider was explicitly selected for previews, without cloud failover. Earlier model samples invented degree/employer explanations; the final simplified contract produced accepted responses on three real-resume samples, including a source exceeding the previous input budget. These samples validate integration and rejection boundaries, not statistically measured hiring accuracy. Normal AI usage/health accounting was retained; application scores, approved JDs, profile settings, candidates, stages and decisions were not changed.

Deploy/restart both API and UI to use these changes. No schema migration or server-model replacement is required. Existing data is not automatically reparsed or rescored. Review identified differences, reparse the original resume or use verified evidence, then intentionally reassess ATS. AI must be enabled on the intended ATS profile before that profile will use the configured provider pool; this review did not enable it or backfill scores.

## Eligibility exceptions and pipeline counts, 20 October 2026

Applications now counts Needs review and Not eligible separately. In pipeline requires current eligibility and the configured ATS cutoff, rather than merely a stage other than New/Rejected. Pipeline buttons and both candidate-board projections use that gate; new transitions and their application recheck it. Historical stage rows, interview decisions and rejected/withdrawn disposition operations remain intact.

Review eligibility supports an explicit administrator-approved exception for each total-experience minimum, named mandatory skill (including its minimum duration), qualification or certification. Reasons, approver identity, time and the resume/JD requirement hash are stored in the existing score-evidence records. Withdrawing an exception restores the original check. Candidate facts and raw component scores are preserved; an exception is shown with an Eligibility exception badge. A score override alone cannot relax mandatory eligibility. Review/recalculate eligibility first, then apply an ATS score override if still needed. No schema migration is required.

Read-only checks against the connected 221 applications returned 141 NeedsReview, 79 Ineligible and one Eligible; that eligible application met the cutoff. These are current saved-evidence assessments, not a new model accuracy measurement. The two previously reported applications expose their mandatory checks and experience-minimum exception item. No application, score, exception, stage or message was written during verification.

Validation: 83 targeted backend tests, 14 frontend policy tests and 38 Playwright checks passed. Browser checks used mocked writes, included exception approval/revocation and existing interview rounds, and exercised desktop/tablet/mobile layouts. API and UI builds passed. Production exception saves and deployment were not performed.

## Repeat the read-only corpus audit

From the repository root in PowerShell:

```powershell
dotnet build .\tools\RecruitmentAtsCorpusAudit\RecruitmentAtsCorpusAudit.csproj --output "$env:TEMP\frevo-ats-audit-bin"
dotnet "$env:TEMP\frevo-ats-audit-bin\RecruitmentAtsCorpusAudit.dll" .\Payroll.API\appsettings.json "$env:TEMP\frevo-ats-review.json"
```

The tool uses a database read-only transaction, starts no application host/worker, calls no AI provider and makes no schema or business-data changes. Its report contains record IDs, source hashes, discrepancies and review reasons; full resumes and credentials are not exported.
