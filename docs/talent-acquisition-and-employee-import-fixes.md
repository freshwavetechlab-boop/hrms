# Talent Acquisition and Employee bulk import fixes

Status: implemented and locally verified on 21 November 2026. API/UI deployment and the resulting automatic salary writes have not been performed by this task.

## Access consistency

Hiring-request reads and saves now use the same effective recruitment permissions as the API routes. A copied administrative role carrying `settings.manage` can maintain approved requests without acquiring a specifically named super-admin role. Client assignments and recruitment location restrictions remain independent checks. Draft creators still edit only their permitted requests; ordinary viewers receive View actions. Approved editing and deletion retain their existing Manage settings requirement. Linked vacancy/JD dependencies disable deletion with an explanation.

The hiring-request editor retains validation/permission errors in the drawer. Seed imports, request creation, native offer editing, letter generation and offer decisions are gated by their corresponding API grants. No role assignments were changed.

## Complete offer list

All offers includes native pipeline offers and released Employee List letters. Namespaced IDs prevent collisions, records are deduplicated within the selected client, and roster actions open the existing Employee List workspace rather than native offer mutation endpoints. Released and joining views keep their existing eligibility rules; their counts overlap and must not be summed as unique offers. Missing annual CTC is displayed as unavailable rather than zero, and monthly gross is not relabelled as annual CTC.

## Current employee details and letter revisions

Employee List reads, edits, drafts, releases and mail previews use current Employee Master details. Explicit same-client employee links are preferred. A legacy match requires a unique employee code, or a unique same-client email plus matching name; ambiguous identities are not merged. Only configured document fields receive private master values. Cleared linked email/mobile details cannot leave an old mail recipient active. Synced fields are identified in the editor and maintained through Employee Master; contract-specific inputs remain editable.

Master name/contact/code corrections also retain existing roster identity links and update linked candidate contact fields in the save transaction. This creates no employee or login. Source revisions remain separate from current Master projections so existing simple-JD/location operations retain their concurrency checks.

To issue a corrected letter: edit Employee Master or the offer-specific inputs, open the Employee List, release the corrected letter, then use Email selected. Existing PDF releases, delivery logs and acceptance receipts are retained. Automatic synchronization does not send email or attest candidate acceptance.

## Automatic initialization of missing monthly pay

`RecruitmentEmployeeSalarySyncWorker` scans up to 50 existing employees of active clients every 30 seconds after API startup. It initializes wholly empty pay setups from explicitly linked, currently released employee-list inputs using the existing salary-only Employee Master save and IT0008 history. Exact monthly Basic plus explicit HRA/rate must reconcile to Gross, and active uniquely identified salary components must exist. It does not apply a shared component formula in place of source amounts.

Client/employee/roster locks and repeated empty-state checks protect concurrent saves and make repeated scans idempotent. Existing CTC, templates, nonempty components, configured salary history, payroll results, conflicting identities/assignments and exited employees are protected. No annual CTC, statutory deduction, salary template, attendance or bank data is inferred. Employee Master displays stored components even without a selected salary template. A dated salary entry/audit is added; issued offers are not rewritten.

A read-only preflight against the reported client found 257 linked roster records and 257 empty pay setups. Current projected inputs reconcile to saved releases for 253 rows; 4 rows have details that differ from their saved release. Those rows require release of the corrected letter before the automatic initialization can use them. Changed issued letters are marked Release again in the Employee List, with the same status in table export. No manual backfill was run.

## Employee Master bulk upload dates

The employee preview and save parser now recognize common slash/dash/dot numeric dates, single-digit day/month, English month names, short years, date-time text and native Excel serial dates. They normalize to `yyyy-MM-dd` independently of browser/server locale. Ambiguous numeric text is day/month/year; unambiguous month/day text also resolves. Two-digit text years follow Excel's 1930–2029 window and the resolved date is visible in preview.

Impossible dates are not rolled into another month. Blank sparse updates, employee identifiers, account strings, salary amounts and duplicate review retain their existing behavior. Date conversion applies only to date fields. The original supplied evidence was screenshots; verification used an anonymous equivalent workbook, not a production import. The 1904 workbook calendar is outside this specific fix.

Regression evidence is shared between browser/native date tests. Mapping knowledge v79 retains the anonymous date-format diagnostic boundary, with a retrieval regression; this is curated guidance, not model-weight training or authorization to change payroll.

## Verification

- 90 frontend checks passed, including bulk dates, identity/amount preservation, roster operations, offer aggregation, access and browser draft persistence.
- 158 selected .NET tests passed, including recruitment access, salary/projection guards, date parity under en-US/en-IN/hi-IN and mapping retrieval. One existing isolated database integration test was skipped because its dedicated test database was not configured.
- TypeScript/Vite production build passed; the .NET test build passed. Output was redirected to the task's C: temporary folder because D: was full.
- The new repository readback was exercised read-only using the reported copied administrator's actual grants. Request 00045 remained at 3 openings. Current Employee Master projection SQL was exercised in a read-only transaction for 282 roster rows; 257 resolved existing employee links.
- Local browser fixture: zero native offers plus 24 roster letters populated All offers; card/table views, employee-list navigation, synced versus editable fields, copied-admin editing, protected deletion, ordinary-viewer action hiding and a persistent synthetic save error were observed. A synthetic 16-row XLSX with mixed dates showed Ready to import and an enabled Import button. No import was submitted.
- Desktop 1366×768, tablet 768×1024 and mobile 390×844 views were checked for bounded scrolling and page horizontal overflow. The export click produced a real XLSX; ZIP/XML inspection confirmed one filtered roster record plus the header, the correct source label and no neighboring record. The browser download event itself timed out.
- No production openings, role grants, employees, salaries, letters or email deliveries were modified by the investigation. No API server, migration or deployment was started.

Reproduce the local browser fixture with `node tools/serve-talent-access-offers-fixture.mjs` and open `http://127.0.0.1:5198/ta-verification`. It serves synthetic responses locally, has no real database and sends no email.
