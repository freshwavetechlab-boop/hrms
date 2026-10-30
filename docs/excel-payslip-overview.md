# Excel Payslip selected-scope overview

> Current runtime update: existing Playwright 1.62.1 was found and imported successfully without installation. The earlier missing-module error was runner resolution plus a sandbox browser-cache path mismatch. Browser E2E now awaits user-started services and verified mocked/isolation setup; see [local browser validation](local-browser-validation.md). Historical missing-dependency notes below are superseded.

Local entry: Excel Payslips → **All clients / selected batches**. Choose client, period and exact saved batch versions, then Preview. No version is assumed active or selected automatically. A changed scope clears the old result.

The overview is read-only. Source rows, distinct client/code groups and matched master records are different counts. Repeated selected versions remain separate rows and are flagged as overlap within a client/month. Conflicting names or duplicate master codes require review; unmatched does not prove that no Employee Master exists. Readiness uses saved-source PDF selection checks, not delivery or regular-payroll parity.

The list and its totals share server-side filters and pagination. Selection is limited to 100 batches and 50,000 source rows per preview. Existing Excel Payslip read permission and each selected client's access are required; a mixed unauthorized scope is rejected as a whole. Responses exclude source information fields, email and financial amounts. No selection is persisted and no schema migration is introduced by this overview.

**Review Master Setup** requires the existing settings/manage-employees/payroll permissions and a captured calculation source. It opens the existing reviewed configuration flow, which may assign salary components/templates and source-month attendance after its own confirmation. Opening the overview or review does not import anything.

Synthetic validation: `node tools/test-excel-payslip-overview.mjs`, overview backend tests, frontend production build and lint of the new files. Browser layout and live database integration remain unverified: the existing Playwright harness needs `HRMS_PLAYWRIGHT_MODULE`; neither Playwright nor Puppeteer is installed in this checkout. No installation or app restart was performed. Owner acceptance should check desktop/mobile table scrolling, scope changes while a preview is pending, cross-client navigation, empty states, and authorization with a restricted user. Test actual counts only against an explicitly verified database target.
