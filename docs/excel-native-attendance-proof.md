# Saved-batch attendance / LWP input proof

## Already-applied legacy lookup inputs (2026-10-29)

An already-Applied legacy source can use its explicit day-column mapping as a native period-input boundary during current-source payroll review. The server first runs the actual Employee calculator and binds the same employee/client/month/cycle and native snapshot. Divisor/payable caches must equal those independent recorded inputs; actual presence remains separately mapped. An LWP column needs its existing reviewed divisor-minus-payable basis before it represents `LOP_DAYS`.

Financial formulas are then recalculated relative to those inputs, and the existing source-to-native formula translation is still checked. No financial output is substituted and no VLOOKUP is installed in the core salary template. Preserve the original formula, source hash and input binding in the new review fingerprint; never rewrite the Applied receipt or claim that missing original lookup closure was reconstructed.

Closed day arithmetic must still reproduce its saved value. Only a direct exact cross-sheet VLOOKUP input, or its nonzero `IFERROR(...,0)` result, can terminate at this reviewed native boundary. Approximate lookup, arbitrary fallback, zero-versus-missing uncertainty, malformed/missing caches, financial cells, changed attendance and wrong periods remain blocked. Ordinary workbook recalculation and new source-seeding validation retain their complete dependency requirements.

Current-source financial recalculation uses the existing source formatter's 15-significant-digit representation boundary, retaining exact recalculated values in the formula fingerprint. This handles decimal division residue without a blanket money tolerance. An explicit existing `BlankAsZero` review is honored only for a captured, error-free literal blank; absent cells and missing formula caches do not become zero. A matching amount still cannot clear an unreviewed literal salary basis or a source/native conditional-rule difference.

Transfer review now has **Source-month attendance / LWP mapping**. It operates only on a saved Excel Payslip batch and its captured source. Raw files/dependency recapture remain inside the Excel Payslip version lifecycle.

## Explicit meanings and modes

- Review the exact source month/hash, cycle dates and existing native cycle start/end configuration. Dates must agree with the regular engine's cycle resolver; no filename-based cycle inference occurs.
- Payable must mean **already payable**. Worked-only/unknown totals block proof: no CL, holiday or weekly-off credit is invented or added again. Actual present/worked is a separate optional source column; a formula using PRESENT_DAYS requires that column explicitly.
- An optional distinct LWP column maps to native **LOP_DAYS only if its reviewed basis is salary divisor minus payable** and the captured formula/cache proves that amount. Calendar eligibility LWP with a different divisor is not silently relabelled. Use the verified payable input directly or resolve a genuinely different rule; there is no force-pass.
- **Reviewed source seed** compares typed source calculations and caches, retaining exact source identity and dependency provenance. It can prepare components/templates and explicitly selected source-month attendance through the existing confirmed setup flow. Native ledger/Employee verification remains pending. It grants no leave entitlement and is not statutory certification.
- **Existing native proof** requires a verified existing employee, source attendance import OFF, and matching client/month/dated cycle. It reads the same monthly/daily/published weekly-off attendance resolver and employment eligibility validator used by regular payroll/Employee simulation. It never rewrites native attendance. Missing/ambiguous employees/groups, unresolved policy, absent dated snapshots or different divisor/day totals block this mode with a specific reason. Reviewed source-seed mode remains a separate honest onboarding choice, not a native pass.

Captured originals, source formulas and dependency graphs are not mutated. Exact lookups with duplicate/missing/blank results, approximate employee lookups and IFERROR fallback cannot establish verified attendance identity. Their standalone Excel behavior remains preserved; correct/supply/map the exact source, save a new reviewed Excel Payslip version, then repreview.

Proof requires CaptureVersion 1 with Reviewed status and retained-original bindings. Preview reads original references through the existing client-and-batch-scoped attachment joins; fingerprint/key/version/alias validation uses those available linked originals. Apply locks attachment/link metadata before rechecking it and includes that state in the full plan hash. Missing/deleted/unlinked originals, another original ID with the same filename/hash, changed retained hashes, mismatched dependency versions and legacy/reference-only snapshots block certification. This validates retained metadata provenance, not a fresh reparse of original blob bytes; actual provider availability remains an integration check.

## Proof and concurrency

Proof metadata binds client, saved batch/version/hash, row/code identity, source month/cycle, reviewed semantic mapping, day values and native snapshot hash. Native DTOs project aggregate days/dates/status/hash, not employee personal records or raw policy/day documents. Same-employee source duplicates/splits need explicit aggregation resolution before whole-month native proof.

Apply retains existing plan-hash/request-receipt guards and rebuilds native proof. Native snapshots participate in the full plan hash. Existing native mode also locks relevant native daily date ranges, attendance memberships/groups and the existing employee/monthly/settings inputs before rebuilding; the client date-range lock can cover more than the selected rows and needs staging DB profiling. No new migration, automatic cleanup or database operation was executed by the agent.

Template parity still compares every configured component, employer amount and net using the existing main calculator. Attendance match is not full Employee tax/statutory parity. Partial months/half-days are supported when divisor, cycle and source/native units agree; unsupported calendar-LWP/divisor transformations remain explicit. Legacy totals without dated provenance do not become a native certificate.

Keep-existing decisions preserve verified current assignments and can record an unresolved/reference-only source choice without source seeding. They do not clear identity errors or mark parity passed. Mixed batches still block affected source transfers.

## Verification

`ExcelNativeAttendanceProofTests` uses synthetic IIT external LWP chains, NHDC same-workbook attendance/IFERROR, source cache and native snapshot models. It covers exact matches, missing/duplicate/blank/error-fallback lookups, identities, periods/cycles, half-days, divisor/basis differences, worked-versus-payable and changed hashes. No live employee rows or attendance queries are used by tests.

The existing CL/LWP Playwright harness mocks the template snapshot and pure simulation contracts, including stale inputs/template hash and salary-impact requests. The existing bootstrap harness also has a synthetic native Transfer review fixture covering source-seed/native modes, changed column/import/hash acknowledgement, keep/new template choices and desktop/mobile capture. All API calls are intercepted; unknown writes and non-local network requests are blocked. Both are syntax-checked and browser-executed with synthetic fixtures (10 CL/LWP and 8 bootstrap/native/overview/lifecycle groups including final responsive rerun). See local-browser-validation.md for final results. Actual DB lock/provider integration remains unverified.

## Worked-day evidence versus draft fallback

Preview now projects `presentDaysEvidence` for each calculated row: mapped physical source cell/value, draft calculation input, review status and required action. Without an actual-worked column, the draft calculator retains its payable-days fallback for arithmetic compatibility, but the source worked value is **unknown**, not equal to payable. The Employee comparison shows that distinction even when actual saved present days differ. An older API/preview without this evidence also stays unresolved; no missing day is converted to zero or silently certified.

Server-side Apply rebuilds the plan and blocks attendance import unless the actual-worked mapping is reviewed for the exact saved source/client/month/cycle and current column/import binding. Missing, blank, changed or stale mapped values do not authorize a write. This applies to legacy setup requests too. The user can map/review a genuine source worked column or turn off **Import source-month attendance** and use regular attendance. A reviewed **Keep existing** choice bypasses source attendance import because it preserves salary, adjustments and attendance. This safeguard does not infer CL, fix existing attendance or change payroll amounts, and it does not replace captured-original/native attendance certification.

Synthetic verification: `ExcelPayrollPresentDaysEvidenceTests` covers unmapped fallback, reviewed zero/half-days, stale client/hash/month/cycle/bindings, missing/blank/changed values, salary-only and keep choices, repeat finalization, and source immutability. `tools/test-excel-payroll-current-comparison.mjs` covers unresolved fallback/legacy evidence without falsely reporting a 30-versus-26 source-worked mismatch. Browser fixtures intercept application APIs and never save real attendance.


## Current Employee input origin

Calculation-only Employee simulation now projects the input branch already selected by the regular engine: stored monthly snapshot, legacy calendar-month daily aggregate, dated daily resolver, or monthly totals checked against dated attendance. The projection includes resolved day totals, known WO/holiday payable units and an existing dated snapshot's cycle. It adds no query, schema change, leave credit, attendance write or second resolver. The monthly/daily selection and all financial calculations remain unchanged.

Monthly-only records do not expose or infer WO/holiday units: those fields stay null/Unknown. A stored four-day payable total is not evidence of four CL days. Legacy daily mode explains its existing rule that each recorded WO/H row contributes one payable day. Dated mode reports actual resolved units and preserves validation errors. Temporary day overrides are labelled and never replace the projected native values. The DTO contains no names, government/bank identifiers, free-text attendance remarks or raw policy documents. Template mode has no native attendance observation.

This is immediate calculation-input provenance, not the original edit/import audit trail or a new payroll certificate. The schema-changing monthly-attendance GET is not used. Synthetic projection tests cover missing-versus-zero, monthly totals without inferred WO/CL, explicit daily units, dated cycles/errors, overrides and observation immutability; live Employee comparison remains necessary to establish a particular employee's source branch.

## Correcting already-transferred conditional salary rules

An explicit single-employee salary correction names the original Applied receipt and retains its saved employee/source-row decision. It cannot create an employee, import attendance, change locations/shared component masters, or replace existing payroll. A recorded Monthly snapshot may provide financial period inputs for this correction when the one active Monthly group's configured cycle and divisor agree. This establishes recorded calculation inputs, not newly certified dated presence or leave entitlement.

Cross-sheet exact-lookup payable/LWP cells can terminate at these explicitly reviewed native input boundaries; original lookup formulas and the missing closure limitation remain visible. Recurring financial formulas still need exact source translation. A current zero must not freeze a conditional PF/ESIC/bonus rule or employee-specific formula override for later months.

The correction can append salary-history versions only where configured entries are owned by the exact named original import reference. It retains the old rows, values, authors and effective dates. Corrected versions also preserve later original source dates because core period payroll chooses effective date before record ID, including historical records. Unrelated configured history blocks correction. Existing approved source adjustments are reused only with an exact source/amount/category/tax/receipt proof; any other approved amount blocks this mode. Apply rechecks locked source/history/adjustment state and the full preview hash. It does not duplicate a reimbursement or process/approve payroll.
