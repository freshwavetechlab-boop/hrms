# Location attendance and payslips

Select the client and attendance policy, then a Location (or All locations). Download the attendance template, enter the explicit PayrollDays, PresentDays and PayableDays, upload it and Apply attendance after review. The formula workbook also includes an Attendance worksheet linked to its editable Payroll day cells. Recalculate and save that workbook in Excel before uploading it.

The formula download respects the selected location, includes its name in the filename and rejects selected employees without a verified part there. Its complete employee calculation is reconciled before filtering the exported rows; downloading one location never drops the employee's other parts from core payroll.

Existing reviewed native location assignments supply their salary templates and rates. Each part retains its own wages and days; the employee's monthly attendance is published only after every part is recorded. A blank is pending, while an explicit zero is recorded. Uploading another location cannot overwrite a concurrently changed row. Replaying the same import ID returns its receipt without duplicate publication. Existing source allocations require their source-correction review, and saved/processing payroll locks attendance for the covered employee/month.

Clients without native split assignments retain their effective salary history/Master template and ordinary attendance calculation. PLRS uses these existing location/policy defaults; the selector does not create transfers or rewrite its templates. The reviewed native cohort is selected by default where present, while other policy members remain available for explicit review. Location selection chooses employees: core payroll still processes **all recorded parts of each included employee once**, rather than paying a shared employee separately in multiple runs. Use All locations for a complete monthly draft.

Open a saved payroll in Pay History / Payslip Register and choose **Location payslips**. Filter a location or download one PDF containing all slips. A split employee receives one slip per saved part with the portal's employee code. These documents project the saved component breakdown; they do not recalculate old payroll from current Settings. Employee-only/monthly amounts appear once under the saved reference assignment. Draft documents are marked DRAFT. Existing consolidated documents, approval, payment and delivery actions retain their behavior.

Reusing prior assignments does not establish new transfer dates or prove that arrears are recurring. Nonzero arrear/refund/one-time components from an older source month require current-month salary review. Existing formulas are neither zeroed nor edited by that check. Review the explicit divisor for the new month, especially fixed-day and mixed-rate sources.

## Deployment

The additive migration is `Payroll.API/Database/Migrations/20261103_payroll_location_attendance.sql`. It creates only the input and import-receipt tables. Apply it through the owner's database deployment process, then start/reload the updated API and frontend. No automatic DDL runs in a GET, attendance save or application startup. Before migration, ordinary attendance retains its legacy fallback; native location saves show a migration-needed message. Deploy API and frontend together so new independent Excel batch requests are supported.

The Excel client lookup advertises `locationBatchesSupported`. Until the updated API supplies it, new uploads/calculated-month saves stay disabled and existing saved payslips remain accessible. An older API's missing location attendance endpoint falls back to the existing attendance workspace. This prevents a partially updated frontend from passing a new location import to an older client/month replacement endpoint.

No salary formula, assignment, existing pay run, source batch or workbook is rewritten by this migration. Roll back the application release without dropping the new tables or deleting receipts. Existing source/canonical native allocation tables remain the payroll engine's input contract.

## Verification

Synthetic tests cover partial/complete split publication, explicit zero versus blank, client/policy/location/template binding, independent files, stale same-row uploads, duplicate requests and saved-run locks. Saved-payslip projection tests cover each location's component sum, monthly amounts applied once, employer contributions outside net and invalid saved totals. Formula export retains its previous aggregate path unless location metadata is explicitly requested.

`tools/test-payroll-location-flow-ui.mjs` uses synthetic API transport for browser upload/layout/retry scenarios; it can display/download real isolated-test payroll documents when `HRMS_LOCATION_E2E_RESULTS` is provided. It cannot connect to a non-loopback origin. Run financial repository integration against an isolated database, never reset or rerun a saved production payroll to test this flow.
