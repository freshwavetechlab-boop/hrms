# Excel payroll setup and dated employee exits

Open **Batch history → Master setup**, or **Configure payroll** on the open batch. It covers all rows by default and respects an explicit row selection. Saving an Excel payslip batch keeps a source snapshot; **Apply reviewed setup** explicitly creates/updates the regular payroll masters. Upload alone does not apply master changes.

## Saved batch readiness

- The workspace separates Existing, New, Needs review and Applied rows using authoritative source identity/location fields. Missing rows appear in a separate review section. Employees still present in the original worksheet are retained even when only a subset was imported as payslips.
- Configure the next group of up to 100 employees, or select specific rows. New/unresolved employees can be reviewed separately while matched employees continue. Applied source rows are excluded from subsequent groups; their saved setup remains accessible for Employee simulation. Request references survive interrupted Apply responses, and older receipts accept added default fields without rewriting their audit JSON.
- Payroll column mapping is independent of the saved payslip layout. Hidden monthly and earned Basic/DA/allowance columns can populate components while gross totals stay outside earnings. Users can change column roles, add/remove components, review formulas and reuse/create component masters. Summary rows do not influence monthly/earned column detection.
- A payroll divisor may come from an employee column or a reviewed fixed number such as 26. Direct attendance-proration formulas suggest that fixed basis. Formula dependencies and supported MIN/MAX/positive-attendance condition patterns are translated without freezing calculated conditions. Unsupported expressions remain explicit review cases.
- **Import source-month attendance** can be turned off to retain a client's regular daily/weekly-off workflow. Preview still uses the source days; attendance must be provided in the regular module before payroll runs. Existing policy, tax and statutory settings remain unchanged.
- After Apply, **Verify saved payroll** calls Employee simulation with at most four concurrent requests. Component and net differences require review before payroll is queued. No email or pay run is created by setup preview/verification.

## Client defaults

- Original worksheet fields take priority, including fields hidden from the simplified payslip. Identity matching uses original location and identity data; the first source UAN column remains authoritative.
- Existing employees retain their organization, personal and banking details. New employees require an explicit creation decision. Existing employee-code generation and identity checks remain in use; ambiguous identities cannot be silently created or resigned.
- Suggested attendance settings come from the client's most-used active policy. Existing employee policy memberships remain unchanged. Unassigned employees receive a Monthly policy using the reviewed workbook days.
- Department, designation and grade use an unambiguous existing client value, otherwise `Unassigned`. Approved missing dropdown values are inserted for this client, using the existing master types, including `Employee Grade`.
- Reviewed source locations can be created. Their state comes from the source or the editable client default; a missing/conflicting state blocks Apply instead of guessing a tax jurisdiction.
- Existing statutory/tax policies remain unchanged. Unknown paid-leave entitlements, holiday dates and punch shifts remain saved, editable inactive proposals. A zero proposal does not grant leave or activate a holiday/shift rule.
- Source formula variants, component review, salary effective dates, payroll comparison and Apply idempotency continue through the existing setup bridge. The preview hash now also includes current attendance memberships/policy settings.
- Existing limits remain: 1,000 rows per Excel payslip batch and 100 reviewed employees per regular-payroll Apply. This change does not add automatic client creation or expand the formula engine's supported Excel functions.

## Mark as resigned

In Employee Master, choose a client → **Select employees** → **Mark as resigned**. The same action is available for selected Excel payslip rows that match active Employee Master records. It requires `employees.manage` and access to the selected client.

The dialog defaults each employee's **last working date** to the calendar month-end of their latest saved regular payroll or latest-version Excel payslip month. For example, September defaults to 30 September and August to 31 August. Different employees retain their own default dates. A missing/ambiguous match leaves the date blank for manual review; today's date is never invented. Dates remain editable, including an optional common date for the whole selection.

Confirm the dates and reason. Each last working date remains eligible; the next date is inactive. The operation records dated Resign actions and audit entries in existing IT0000 tables. It validates the whole selection before committing, including per-date payroll and attendance conflicts. Repeating already recorded resignation dates does not create duplicate actions.

Scheduled exits are marked red immediately and remain active until their date. Attendance entry, machine/mobile punches and new payroll calculations respect dated employment eligibility. Exit-month attendance cannot exceed eligible calendar days; existing non-exit monthly salary conventions remain unchanged. Review any existing payroll or payable attendance that conflicts with an exit before saving it.

The scheduled worker applies due exit flags and dated rehire actions. An explicit later inactive master action takes priority over a rehire. An ordinary Active checkbox cannot silently rehire someone who has left.

Employee Master retains resigned rows in red. Historical attendance, saved payroll, source batches and audit history remain available. Excel rows for months after employment ended remain visible for source history; PDF/email selection excludes them, and the mail worker rechecks before attaching a PDF. Employment badges are derived metadata and do not alter saved source amounts or invalidate existing queued-mail hashes. General table exports retain source/audit rows and their status.

Missing employees in a later workbook are **not** automatically resigned. A replacement employee must retain their own identity/code; an outgoing employee's code is not recycled.

## Storage and rollout

No new schema migration is introduced by these changes. Defaults/attendance assignments reuse the existing `excel_payroll_setup_imports` JSON receipt, and exits reuse `employee_it0000_actions` and employee audit tables. Existing Excel setup/attendance migrations must already be installed. Restart the API to load the new endpoint and scheduled worker.

## Validation

- Frontend production build and 14 Excel setup/attendance proposal and import tests passed.
- 457 related backend tests passed, covering Excel payslips/setup, employment exits, attendance, payroll calculations and simulations.
- 23 checks ran in an explicitly read-only MySQL transaction: inclusive last day, exit-month limits, future exclusion, historical eligibility, client isolation, rehire gaps, legacy receipts, preserved policy groups, and saved NHDC source/hash compatibility. No business rows were written.
- `tools/test-excel-payroll-bootstrap-ui.mjs` exercised the actual Employee Master and Excel components with intercepted synthetic APIs at 1366×768, 768×1024 and 390×844. It checked client defaults, ambiguous identity blocking, required date, rejected/successful mocked saves, history retention, red cards/rows, selection and scrolling. It did not send emails or resign actual employees.
- `tools/test-excel-payroll-readiness-ui.mjs` covered a 214-row source, separate cohorts, resuming applied groups, component editing, a fixed divisor, stable retry references, four-request simulation concurrency and mixed per-employee resignation defaults. It used synthetic intercepted API writes and the application theme at desktop/tablet/mobile sizes.
- Read-only repository checks loaded all seven latest client batches. Two verified MNGL identities defaulted to 30 September; an ambiguous identity retained a blank date. Three existing MNGL rows passed native draft calculation preview with a 26-day divisor and matched Excel at whole-rupee display; a paise-level rounding variance remains explicitly flagged. No masters, attendance, exits, payroll or emails were saved by these checks.
- The full backend suite also has five recruitment parser/offer failures; all five were reproduced from unchanged `HEAD` in a separate temporary checkout. They are outside this change.
