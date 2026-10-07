# Excel Payslips

Open **Reports > Payroll Reports > Excel Payslips** (`/reports/payroll-reports?report=excel-payslips`). This workspace accepts salary amounts already calculated in a spreadsheet for any active client the operator is authorized to access.

## Import and mapping

1. Select the client and import an `.xlsx` or `.csv` file.
2. Check the worksheet, header row and salary month. Review excluded totals, missing-name rows and any cell errors.
3. Select an existing client salary template. Map spreadsheet columns to its components, or use manual mapping. Template components supply the payslip label and earning/deduction/employer-contribution category; template formulas and rates do not recalculate the spreadsheet amounts.
4. Map employee name, at least one earning and the explicit source net-pay column. Employee code and email are optional. Preserve account numbers, UAN and other identifiers as text in the source file. An Excel serial number is not an employee code.
5. Inspect the employee preview and save the batch. Optionally save the column mapping for the same client and column-heading format next month.
6. Preview one payslip or select rows and export one combined PDF, with one employee per page. Amounts display in whole rupees by default, as requested for the source workbook; the PDF/email dialog also offers two decimal places. This changes display only, including amount-in-words, and never rewrites source amounts. The company seal is optional in the PDF action dialog. Reopen saved batches through Batch history.

Formula cells use Excel's saved results. A mapped Excel error or formula without a cached value blocks import until the file is corrected/recalculated in Excel or the column/row is excluded. External workbook links and formulas are never executed. A blank mapped numeric cell is not silently treated as zero. Negative amounts and inconsistent financial totals require explicit review before PDF export or email; the source net pay remains unchanged.

Currency display uses Excel's 15-significant-digit precision before the selected display rounding. For example, a cached `14129.499999999998` displays as `14,130`, matching the workbook. This formatting step does not replace the imported value.

The PLRS September layout has a mapping suggestion: wages are earnings, employee PF/ESI are deductions, employer PF/ESI are separate employer contributions, and bonus is information because the source net-pay formula excludes it. Monthly rate is labeled as monthly rate. These suggestions are editable and do not restrict other clients or formats.

## Email

Email is required only when sending. Individual delivery attaches each selected employee's own PDF; a combined delivery attaches all selected payslips to one explicitly entered address. Missing/invalid recipients return errors without affecting PDF export. Recipient overrides belong to that send action and do not edit Employee Master or the saved batch.

The existing notification queue and SMTP settings handle delivery, including paused/suppressed delivery. Queue status is not proof of inbox delivery; inspect notification delivery logs. Retrying the same send request retains its request reference to prevent duplicate queue entries. Opening a new send action allows an intentional resend.

Only the recipient, selected source rows, seal/rounding options and request receipt are stored for delivery. The worker checks the saved batch and queue reference, then generates the attachment in memory. Preview, download and email do not save generated PDFs to disk, attachment storage or the database. The static logo/seal assets remain part of the API deployment.

## Isolation and limits

- No Employee Master import, attendance calculation, pay run, payroll report or existing payslip format is updated by this workspace.
- One dedicated table, `excel_payslip_batches`, stores client-scoped batch snapshots and delivery metadata. Saved batches retain source amounts and labels even if the salary template changes later. Only reusable column-mapping configuration stays in `modulesettings`; normal payroll tables are unaffected.
- Viewing/exporting reuses the Payslip Register read permissions; importing, saving mappings and sending reuse `payroll.run`, `payroll.approve` or `payroll.payments`. Every route checks client scope. Reports navigation retains the application's existing Reports access rules.
- Upload limit: 30 MB; batch limit: 1,000 employee rows / 8 MiB snapshot; email PDF attachment limit: 10 MiB. Individual mail requests are grouped in sets of 25. One client can retain up to 50 different column-heading mappings.
- Logo and the user-supplied signature/seal are private API deployment assets. PDF generation validates that mapped content fits a readable single page before saving a batch.

## Verification without live delivery

Backend tests cover source-value preservation, required amounts, template/client scope, row selection, warnings, optional emails, send idempotency, PDF layout and attachment integrity. Route tests use an isolated host with no application configuration, database connection or notification worker. Browser checks intercept API traffic; no production batches or mail queue entries are created. PDF rendering can be checked offline using parsed spreadsheet snapshots.

Storage tests cover source snapshot preservation, client/ID validation, repeated migration comparisons, conflicting snapshots and unsupported legacy delivery guards. Attachment tests regenerate PDFs from saved data and verify that delivery metadata contains no PDF bytes, and that wrong clients, recipients, rows or render options are rejected.

The supplied September workbook was checked read-only: 830 employee rows, 4,980 imported monetary values and 3,320 identifier fields matched the source (only insignificant floating-point serialization differences below 0.00000001). Totals/check rows 836 and 838 were excluded. The offline PDF check covers all 830 names, net amounts and 4,150 individual component displays. The half-rupee edge case at K135 was also verified against the open workbook's displayed text. Responsive browser checks cover 1366, 768 and 390 px widths, mapping/history, access scope, PDF export, optional email, chunked sending and duplicate-send retry behavior using mocked APIs.

## Dedicated storage migration

Stop API instances using this database before migrating so the earlier implementation cannot write more batches into settings. From the repository root, run:

```powershell
dotnet run --project .\Payroll.API\Payroll.API.csproj --launch-profile http -- --migrate-excel-payslips
```

The command uses the selected API configuration's database connection, creates the one table from `Payroll.API/Database/ExcelPayslips.sql`, transfers existing `excel_payslip:<batch-id>` snapshots with their original IDs and values, verifies each copy, then removes those exact legacy settings rows in the same transaction. Mapping profiles and unrelated settings remain unchanged. Repeating the command is safe; conflicting destination snapshots stop it rather than overwrite data. The command exits without starting the API or notification workers and does not send mail. The script alone creates the schema; use the command for the verified data transfer.

If earlier `excel_payslip_mail:` or `excel_payslip_send:` records exist, migration stops for conversion review: that earlier format omitted the seal/rounding settings or batch linkage needed to reproduce delivery safely. Do not delete those records to bypass the guard. Any data transfer rolls back on failure; the empty table created by MySQL DDL may remain. No legacy PDF options are guessed.

After successful migration, start the updated API normally. Before deployment, build the API and UI. An authorized operator can then save a reviewed batch and export it. Actual SMTP delivery requires a separate intentional send; neither live migration nor SMTP delivery was exercised during this storage change.
