# Enterprise Payroll Engine Audit

## Implemented in this pass

- Added persistent payroll diagnostics keyed by `PayRunId`: step logs, validation issues, calculation traces, and reconciliation results.
- Added a 59-step payroll pipeline log aligned to enterprise payroll processing, including statutory, approval, freeze, report population, ESS, and reconciliation stages.
- Added blocking pre-run validation for regular payroll so critical master, salary, attendance, and bank failures stop payroll before employee results are committed.
- Connected regular payroll to the existing income tax engine for monthly TDS projection when active client tax settings and rule versions exist.
- Linked payroll-driven tax computation snapshots back to `PayRunId`.
- Added validation against existing salary component formula masters, EPF/ESI switches, normalized personal details, and normalized payment details.
- Added component-level calculation trace rows from the generated pay-run lines, including base amount, factor, formula label, calculated amount, employee, and component.
- Added reconciliation checks for net salary, payroll cost, bank advice total, attendance payable bounds, GL debit/credit balance, PF, ESI, PT/LWF/WC, TDS, and linked income-tax snapshots.
- Added `GET /api/pay-runs/{id}/diagnostics` for payroll administrators and future UI panels.
- Added a payroll diagnostics panel in the pay-run review screen for reconciliation, validation, and calculation trace inspection.

## Existing masters and engines found

- Salary components, salary structures, employee salary components, payslip templates, professional tax slabs, client pay schedules, employee personal details, employee payment details, and pay-run employee line tables already exist.
- Income tax has FY masters, rule versions, regimes, slabs, surcharges, final adjustments, declaration sections/options, standard deductions, rebate rules, exemption rules, HRA rules, source references, audit logs, client settings, employee regime selection, declaration/proof tables, projection runs, and computation snapshots.
- Reporting already reads payroll, attendance, leave, PF, ESI, PT, TDS, employee, department, and location datasets.
- Adjustment import already supports scheduled earnings and arrears-style amount import.

## Remaining payroll module gaps

- Shift, holiday, weekly-off and LWP calculations exist as attendance data concepts, but they are not yet independent payroll calculation modules with their own step output.
- Overtime, night shift allowance, attendance bonus, incentives, sales commission, reimbursements, claims, loan recovery, salary advance recovery, notice recovery, and other recovery processes can currently flow through salary components or payroll adjustments, but need dedicated source tables/workflows if they must be audited separately.
- Arrears can be imported as adjustments, but there is not yet a replayable arrear engine for increment arrears, promotion arrears, retrospective salary revisions, formula-change arrears, attendance corrections, leave corrections, and statutory/tax deltas.
- Full and final settlement, gratuity, and leave encashment need separate settlement run support.
- Employer contribution calculations need distinct output lines and reports instead of being blended with employee deductions or CTC components.

## Remaining statutory and tax integration gaps

- Professional Tax is calculated from existing PT setup; PF can be calculated through formula-based salary components; ESI/LWF can be stored in existing employee personal statutory fields. The next improvement is to standardize PF/ESI/LWF rule resolution and trace selected rule inputs.
- Income tax projection is now invoked by regular payroll, but projected salary should later use actual till date plus future projection by remaining months, bonus, arrears, perquisites, previous employer income, and other income.
- Tax computation snapshots are linked back to `PayRunId`; linking them to employee payroll line IDs remains a future drill-down enhancement.
- Statutory return population tables need explicit generation checkpoints and reconciliation totals.

## Validation gaps

- Current validation covers missing attendance, salary setup, bank details, PAN presence, EPF/UAN presence, ESI/ESIC presence, PT state, and missing formula references.
- Add stricter validations for PAN format, IFSC format, duplicate bank accounts, duplicate payroll, previous payroll closure, payroll lock, negative salary, circular formulas, missing statutory rule applicability, missing cost centers, and tax regime selection.
- Convert warning/critical severity to configurable policy by client and payroll group.
- Store rejected validation runs as draft run attempts if business users need audit history for failed starts.

## Audit and logging gaps

- Add employee-level step logs for every stage, not only run-level stage logs.
- Add database execution logs and query timings for heavy stages.
- Add rule execution logs that include selected rule version, applicability criteria, and effective dates.
- Include old/new values for manual pay-run edits and approvals.

## Reconciliation gaps

- Current reconciliation covers payroll totals, PF line totals, ESI source-to-line totals, PT/LWF/WC source-to-line totals, TDS payroll-to-line totals, and TDS payroll-to-tax-snapshot totals.
- Add deeper statutory reconciliations: PF wage to PF formula, ESI wage to ESI eligibility, PT slab to PT amount, LWF cycle eligibility, and projected annual income to actual plus projected future salary.
- Add report population reconciliations for salary register, payslip, bank advice, statutory reports, GL posting tables, audit reports, and variance reports.
- Add department, location, cost center and component-wise control totals.

## Performance and database recommendations

- Move payroll calculation into restartable checkpoints by stage and employee batch.
- Partition high-volume logs and traces by pay period or run date when the installation crosses tens of millions of rows.
- Add covering indexes for reporting tables by `(ClientId, PayPeriod)`, `(PayRunId, EmployeeId)`, `(PayRunId, ComponentCode)`, and statutory report dimensions.
- Avoid storing only JSON for final payroll facts; keep JSON for trace snapshots but persist normalized component result rows for reporting and reconciliation.
- Use background jobs for large runs and expose progress by stage.
- Process employees in safe parallel batches after run-level freeze and validation, with idempotent writes per employee.

## Transaction and recovery recommendations

- Keep run creation, validation, and checkpoint metadata in a short transaction.
- Process employee batches in independent transactions so one failed employee can be isolated and resumed.
- Add status values such as `Processing`, `Failed`, `Partially Processed`, `Ready for Validation`, `Validated`, `Frozen`, and `Posted`.
- Add resume APIs that restart from the last successful checkpoint.

## Security recommendations

- Restrict diagnostics to payroll makers, approvers, auditors, and statutory administrators.
- Mask bank account, PAN, UAN, ESIC, and tax proof data in diagnostics unless the user has a privileged permission.
- Add immutable audit for approval, freeze, unfreeze, recall, payment, GL posting, and statutory filing actions.

## Recommended target architecture

- `PayrollRunOrchestrator`: owns stage sequencing, checkpointing, restart, and status transitions.
- `PayrollValidationService`: validates all prerequisites and generates blocking/non-blocking reports.
- `RuleEngine`: resolves versioned rule masters and evaluates formulas with dependency and circular-reference checks.
- `PayrollCalculationService`: runs earnings, deductions, statutory, tax, arrears, and net pay modules.
- `PayrollTraceService`: records component-level calculation trees.
- `PayrollReconciliationService`: writes control totals and failure details.
- `PayrollReportingPublisher`: populates reporting tables after validation, before approval/freeze.
- `PayrollPostingService`: creates bank advice and GL posting batches.
