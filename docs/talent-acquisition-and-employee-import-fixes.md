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

### Annual remuneration base policy — 23 November 2026

The owner authorized monthly remuneration multiplied by 12 as the initial Annual CTC base, with contributions and salary-template breakup to follow. This is explicitly enabled per client through `RecruitmentSalarySync:AnnualizeMonthlyRemunerationClientIds`; the production configuration currently enables ICMR (55) only. IRCTC (70) remains disabled because the owner confirmed its earlier Salary column contained net paid amounts and will re-upload corrected gross amounts. Do not enable IRCTC annualization from the old list.

The existing worker can initialize a missing annual base without inventing Basic/HRA or statutory contributions. Existing component amounts are preserved, including previously synchronized ICMR Basic/HRA. Explicit annual offer terms take precedence over the monthly fallback. Client, employee, latest-release hash, active posting, all-assignment agreement, existing CTC/template/history and payroll-result protections remain enforced. Only missing components with no configured component history may be initialized from a complete, reconciled source breakup. The salary-only save records IT0008 history and the roster receipt records the remuneration, annual base and saved components. Existing records are scanned automatically after deployment; no manual production backfill was run.

When Employee Master changes the gross amount in a layout with a separate `salaryInWords` input, its projected words now follow that same amount, including paise. Equivalent unchanged amounts retain their original wording/format to avoid invalidating an unchanged release. Released PDF bytes and delivery history remain immutable.

Corrections now use the standard, Profile Code-based flow below. The old name/contact-based bulk insertion endpoint no longer accepts saves. Salary in words follows the corrected gross; it does not need a separate source column.

## Single-sheet standard insertion and corrected versions - 23 November 2026

Owner requirement: **one Employees sheet only**, with the same standard columns for new profiles and corrections. No Instructions, Fields, Banking or salary worksheets. No source wage-sheet values are selected, copied or inferred. The user enters the authoritative values, including any joining date, monthly components, gross remuneration and annual CTC.

The employee-list drawer has two actions:

1. **Import employee list**: download the blank standard workbook, fill it, upload, preview and insert the valid selected rows. New Profile Codes are generated.
2. **Update Corrected Version**: choose the saved import batch, download its current data in the same single-sheet format, edit, upload and preview. Matching uses Profile Code within the selected client/template/batch. Unknown, blank or duplicate codes are skipped and other valid rows continue. This mode never inserts a profile.

Columns cover core IT0000/0001/0002/0006/0008/0009, configured custom fields, bank/account/IFSC, offer details and salary components from the existing catalog. Components carry INR/month or INR/day in their headers; Annual CTC is INR/year. Identifiers stay text. Blank retains existing data; numeric 0 sets zero. Monthly Gross does not create a Basic/HRA/CTC allocation. Existing component/gross contradictions are reported per row. No old/enhanced/earned/net values from the supplied wage workbook were chosen.

Workbook scope and current-record versions travel in hidden columns in the same sheet. They prevent an old download from overwriting a later change without adding user-facing setup or a second sheet. Bracketed stable field codes remain in the headers, so column order does not require manual mapping. Portal access, active status and inactive/summary salary components are reference-only.

Corrections to linked employees use existing scoped infotype writers and add dated Master history plus roster audit in the same per-row transaction. Bank and personal saves retain unrelated data; work email changes use IT0001. Candidate contact changes retain the exact profile anchor and still check duplicate identities. New profiles retain pending custom/core/pay values until the existing explicit joining import creates their Master. Work-location creation reuses existing client/WPI permission rules. Released PDFs, approvals, acceptance and delivery history are retained; changed letters need separate review/release. The automatic legacy salary initializer excludes profiles using explicit standard-template values.

Verification: 65 targeted .NET checks passed; one dedicated database integration test was skipped because its isolated database was not configured. The single-sheet frontend check and TypeScript/Vite build passed. Read-only template generation found 104 unique IRCTC Profile Codes with stable versions. A real browser-generated single-sheet file with 104 synthetic rows roundtripped through the backend reader; the blank artifact template passed scope/header checks and contained no employee values. Local browser tests used synthetic in-memory APIs for the download, known/unknown profile preview, partial success, insertion template, retained HTTP 400 error and mobile layout. No production correction, insertion, letter release, email or deployment was performed.

## Employee-list preview visibility — 23 November 2026

A live, anonymous single-row workbook produced a valid employee-list preview, but the drawer remained scrolled to the upload form and the results were below the visible area. The sample was cancelled without saving profiles, locations or letters. The user's corrected workbook has not yet been supplied, so a file-specific error or actual drawer closure remains unconfirmed.

The import drawer now scrolls directly to its completed preview. Progress and errors remain beside the footer actions, where they stay visible while the form is scrolled. The uploaded file and settings remain available after a preview failure. Background employee-list refreshes are deferred while its import drawer is open; client and permission scope reset behavior is unchanged.

Verification: the live sample preview succeeded; the changed UI was then exercised against an isolated in-memory API for success and HTTP 400 failure. The preview became visible automatically; the failure retained the file/batch and disabled save. Footer feedback/actions remained reachable at default desktop, 768×1024 and 390×844 sizes. Fourteen roster operation/template checks and the TypeScript/Vite production build passed. No real import/save/release/mail or deployment was performed.

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
