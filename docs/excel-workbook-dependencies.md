# Excel workbook capture and dependency review

This local phase captures typed source cells and a bounded supplied-workbook graph for standalone Excel Payslip calculations. It does not implement all Excel functions or certify recurring native payroll parity.

## User flow

1. Import one original `.xlsx` or `.csv` salary workbook. Select its worksheet and financial column mapping.
2. Open **Workbook dependency review**. Same-workbook dependencies are captured from the selected file. For external `[slot]` references, explicitly supply the dependency file, choose its exact filename/fingerprint version and acknowledge the alias. Nothing follows a filesystem, network, UNC or workbook external link.
3. Review the complete source/dependency list. A formula graph, mapping or file-version change invalidates this review. Cached amount corrections do not authorize different formulas. The server's complete plan hash remains authoritative for Transfer Batch freshness.
4. Save the reviewed source or explicitly save cached values **for reference only** while dependencies are unresolved. Reference-only sources are blocked by server-side Master Setup/Transfer preview. Missing dependencies are not converted to zero by this workflow.
5. Retention uses the existing client-scoped attachment service. SHA-256 returned by retention must match the supplied capture; file names alone never establish version identity. A failed retention or changed client/batch scope prevents the batch-save request. Partial retention can be retried with the existing client-scoped receipt cache.

Saved versions retain the source workbook binding, dependency snapshots and reviewed alias metadata. Reopening restores that captured graph and requires a fresh review before saving changes. Derived batches inherit parent original references and may add explicitly supplied dependency originals. The repository verifies every referenced original is available in this client's attachment set and each dependency fingerprint belongs to its linked original. Shared originals are reused; lifecycle retention/deletion protections remain in effect.

## Meaning and limits

- Original uploaded bytes remain in attachment storage; capture/calculation operates on separate JSON snapshots. Formulas remain captured strings and cached values remain distinct from evaluator results. A fingerprint links a captured snapshot to a retained file version; the server does **not** reparse the blob to cryptographically prove every edited snapshot cell came from those bytes. Reviewed corrections remain derived data.
- XLSX number/text/boolean/blank types come from XML metadata. CSV cells are explicitly text; leading-zero identifiers are preserved. Re-import typed originals when a legacy lookup's numeric/text key type is unknown.
- Only dependencies reachable from mapped earning/deduction/employer/net/total formula outputs are collected. Unmapped bank/supporting lookups are not treated as payroll roots. No workbook macros or external programs run.
- At most 20 supplied workbooks/mappings, 20 dependency sheets, 250,000 combined captured cells, 10,000 cells per dependency range and 32 MiB per supplied file. Backend evaluator limits and unsupported-case errors also apply. These are bounded compatibility limits, not general Excel support.
- Legacy single-sheet and virtual combined-location imports remain available for their existing cached review flow. They cannot manufacture typed dependency provenance. Re-import each exact original with its dependencies to establish that graph. There is no automatic filename/latest-version inference or retroactive raw-file reconstruction.
- A lookup selecting the first duplicate is Excel behavior, not verified employee identity. Evaluator lookup traces retain selected-row/count/status; identity ambiguity still needs reviewed native mapping before transfer. No lookup result is automatically frozen into a future recurring payroll rule.
- Native IF translation, verified monthly attendance/leave input mapping and salary-component impact integration are separate subsequent implementation phases. This UI does not claim them complete.

## Local checks

`node tools/test-workbook-dependency-capture.mjs` exercises synthetic same/external-sheet graphs, aliases, missing/duplicate versions, fingerprint/review invalidation, range limits and unchanged CSV bytes.

`node tools/test-workbook-retention.mjs` exercises only in-memory API doubles: exact retention hash linkage, changed hash/storage failure, derived/shared originals, client scope and cancelled saves. `node tools/test-excel-payslip-lifecycle.mjs` remains the retention/lifecycle regression.

Backend `ExcelPayslipWorkbookBindingTests` uses synthetic models only. Browser interaction, actual attachment provider/transaction integration and authenticated UI behavior remain pending owner-started isolated testing. Do not upload real payroll files or apply real setup as a test.
