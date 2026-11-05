# Scoped payroll and billing workspaces

The existing `reports.view` and `settings.manage` grants retain their full scope. The following additive grants support preparation and finance staff without opening unrelated modules:

| Grant | Workspace |
| --- | --- |
| `reports.payroll.view` | Payroll reports and Payslip Register; Excel Payslips / Excel Dashboard additionally require unrestricted administrator access |
| `reports.compliance.view` | PF/ECR, ESIC, PT, statutory and tax preparation reports |
| `reports.billing.view` | Client billing basis and payroll cost reports |
| `billing.manage` | Client Billing Configuration, without other Settings pages; unrestricted users or the active client of a Selected account |

Frontend report navigation and backend report endpoints both enforce the report families. An unimplemented report is visible only with the original full-report grant. Payroll preparation still requires `payroll.run`; payroll approval and payment remain separate permissions.

Payroll and report employee selectors use `/api/payroll/employee-options`, which returns employee identity, department, annual CTC and active status with the remaining employee fields empty. It does not grant access to Employee Master. Payroll operators can read attendance groups and monthly attendance for preparation without receiving attendance management rights.

All-client HQ users have `ClientAccessMode = Legacy` and `ClientId = null`. Selected-client business users also have null `ClientId` in their original identity, but must use their assigned active client for business requests; see [Selected-client user access](selected-client-access.md). An employee or specific-client association must not be set on these standalone accounts. Accounts provisioned with `MustChangePassword` use the existing authenticated change-password API before the admin workspace is shown.

No passwords or personal contact lists belong in this document or the repository. Verify new accounts using login, exact effective grants, all-client lookup scope and forbidden endpoint checks. Existing accounts and standard non-admin role assignments must remain unchanged.
