# API Catalog

This catalog lists the API routes currently exposed by `Payroll.API/Program.cs`.

Use this document when configuring workflow start rules in:

`Workflow > Workflow Setup > When should workflow start?`

## Workflow Rule Cheat Sheet

For workflow configuration, you usually need these fields:

| UI field | Meaning | Example |
| --- | --- | --- |
| Screen action | Business action that needs approval | Submit payroll for approval |
| Request type | HTTP method | POST |
| Request path | API route pattern | `/api/pay-runs/{id}/submit` |
| Record type | Business object name | PayRun |
| Record number comes from | Where the ID is available | URL value |
| Record number field | Placeholder/body/query field name | `id` |
| Find client using | How client is resolved | Lookup from table |
| Table name | Table containing the record | `payruns` |
| Match this column | Column containing the record ID | `Id` |
| Client column | Column containing client ID | `ClientId` |

Recommended payroll approval rule:

| Field | Value |
| --- | --- |
| Screen action | Submit payroll for approval |
| Request type | POST |
| Request path | `/api/pay-runs/{id}/submit` |
| Record type | PayRun |
| Record number comes from | URL value |
| Record number field | `id` |
| Find client using | Lookup from table |
| Table name | `payruns` |
| Match this column | `Id` |
| Client column | `ClientId` |

## Authentication And Dashboard

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| POST | `/api/auth/login` | Sign in and create session. | No |
| GET | `/api/auth/me` | Get current signed-in user. | No |
| POST | `/api/auth/logout` | Sign out. | No |
| GET | `/api/dashboard` | Dashboard metrics. Query: `clientId`. | No |

## Workflow

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/workflows` | List workflow definitions. | No |
| POST | `/api/workflows` | Create or update workflow definition and stages. | Possible, but usually admin-only |
| GET | `/api/workflows/activities` | List available business activities. | No |
| GET | `/api/workflows/activities/catalog` | List all workflow activities for Activity Master. | No |
| POST | `/api/workflows/activities` | Create or update workflow activity master. | Possible, but usually admin-only |
| GET | `/api/workflows/action-rules` | List workflow start rules. | No |
| POST | `/api/workflows/action-rules` | Create or update workflow start rules. | Possible, but usually admin-only |
| GET | `/api/workflows/approvers` | List users who can approve. | No |
| GET | `/api/workflows/departments` | List departments for a client. Query: `clientId`. | No |
| GET | `/api/workflows/department-heads` | List department head assignments. Query: `clientId`. | No |
| POST | `/api/workflows/department-heads` | Save department head assignment. | Possible |
| POST | `/api/workflows/start` | Manually start workflow. | No, already workflow |
| GET | `/api/workflows/tasks/pending` | Current user's pending workflow tasks. | No |
| POST | `/api/workflows/tasks/{taskId}/Approved` | Approve workflow task. | No, already workflow |
| POST | `/api/workflows/tasks/{taskId}/Rejected` | Reject workflow task. | No, already workflow |
| POST | `/api/workflows/tasks/{taskId}/Sent Back` | Send workflow task back. | No, already workflow |
| GET | `/api/workflows/history` | Workflow instance history. | No |
| GET | `/api/workflows/{instanceId}/history` | Approval trail for one workflow instance. | No |

## ESS And MSS

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/ess/profile` | Employee self-service profile. | No |
| GET | `/api/ess/leave/balances` | Employee leave balances. | No |
| GET | `/api/ess/leave/requests` | Employee leave request list. | No |
| POST | `/api/ess/leave/requests` | Create leave request. Currently starts workflow directly in code. | Yes, future generic rule candidate |
| GET | `/api/ess/leave/requests/{id}/trail` | Leave request workflow trail. | No |
| GET | `/api/ess/pay/payslips` | Employee payslip list. | No |
| GET | `/api/ess/tax` | Employee tax portal. | No |
| POST | `/api/ess/tax/regime` | Save employee tax regime. | Possible |
| POST | `/api/ess/tax/declarations` | Save employee tax declarations. | Possible |
| GET | `/api/ess/dashboard/attendance` | ESS attendance summary. Query: `month`. | No |
| GET | `/api/ess/dashboard/attendance/daily` | ESS daily attendance. Query: `month`. | No |
| POST | `/api/ess/attendance/punch/validate` | Validate attendance punch. | No |
| POST | `/api/ess/attendance/punch` | Record attendance punch. | Possible |
| GET | `/api/ess/dashboard/holidays` | ESS holiday list. Query: `month`. | No |
| GET | `/api/ess/dashboard/birthdays` | Today's birthdays. | No |

## Security And Audit

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/security/users` | List application users. | No |
| POST | `/api/security/users` | Create or update user. | Possible |
| GET | `/api/security/roles` | List roles. | No |
| POST | `/api/security/roles` | Create or update role. | Possible |
| GET | `/api/security/permissions` | List permissions. | No |
| GET | `/api/audit-logs` | List audit logs. Query: `limit`. | No |
| POST | `/api/admin/database/migrate` | Run database migration. | No |

## Reports

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/reports/{code}` | Run report by code. Query can include `clientId`, `department`, `workLocationId`, `fromDate`, `toDate`, `month`. | No |

## Organization And Settings

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/organization` | Get organization setup. | No |
| POST | `/api/organization` | Save organization setup. | Possible |
| GET | `/api/setup` | Get setup data. | No |
| POST | `/api/setup` | Save setup data. | Possible |
| GET | `/api/clients` | List clients. | No |
| POST | `/api/clients` | Create or update client. | Possible |
| GET | `/api/work-locations` | List work locations. | No |
| POST | `/api/work-locations` | Create or update work location. | Possible |
| GET | `/api/dropdowns` | List dropdown masters. | No |
| POST | `/api/dropdowns` | Save dropdown master. | Possible |

## Client Billing Configuration

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/client-billing/module` | Get client billing module status. | No |
| POST | `/api/client-billing/module` | Enable or disable client billing module. | Possible |
| GET | `/api/client-billing/configurations` | List client billing configurations. | No |
| POST | `/api/client-billing/configurations` | Save client billing configuration. | Possible |

## Tax Engine

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/tax-engine` | Get tax engine setup. | No |
| POST | `/api/tax-engine/client-settings` | Save client tax setting. | Possible |
| POST | `/api/tax-engine/slabs` | Save tax slab. | Possible |
| POST | `/api/tax-engine/surcharges` | Save surcharge rule. | Possible |
| POST | `/api/tax-engine/final-adjustments` | Save final tax adjustment. | Possible |
| POST | `/api/tax-engine/sections` | Save declaration section. | Possible |
| POST | `/api/tax-engine/compute` | Compute tax. | No |
| DELETE | `/api/tax-engine/{kind}/{id}` | Delete tax engine row. | Possible |

## Leave And Attendance Setup

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/leave-attendance/setup` | Get leave and attendance setup. Query: `clientId`. | No |
| POST | `/api/leave-attendance/module` | Enable or update leave attendance module. | Possible |
| PUT | `/api/leave-attendance/setup/{stepCode}` | Update setup step status. | Possible |
| GET | `/api/leave-attendance/preferences` | Get preferences. Query: `clientId`, `workLocationId`. | No |
| POST | `/api/leave-attendance/preferences` | Save preferences. | Possible |
| GET | `/api/leave-attendance/attendance-settings` | Get attendance settings. Query: `clientId`. | No |
| POST | `/api/leave-attendance/attendance-settings` | Save attendance settings. | Possible |
| GET | `/api/leave-attendance/geo-fences` | List geo-fence rules. Query: `clientId`, `scopeType`. | No |
| GET | `/api/leave-attendance/geo-fences/applicable` | Get applicable geo-fence. Query: `clientId`, `employeeId`, `onDate`. | No |
| POST | `/api/leave-attendance/geo-fences` | Save geo-fence rule. | Possible |
| DELETE | `/api/leave-attendance/geo-fences/{id}` | Delete geo-fence rule. Query: `clientId`. | Possible |
| GET | `/api/leave-attendance/groups` | List attendance groups. Query: `clientId`. | No |
| POST | `/api/leave-attendance/groups` | Save attendance group. | Possible |
| DELETE | `/api/leave-attendance/groups/{id}` | Delete attendance group. Query: `clientId`. | Possible |

## Attendance Transactions

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/leave-attendance/attendance/monthly` | Get monthly attendance. Query: `clientId`, `month`, `workLocationId`. | No |
| POST | `/api/leave-attendance/attendance/monthly` | Save monthly attendance. | Possible |
| GET | `/api/leave-attendance/attendance/context` | Get attendance context for review. Query: `clientId`, `month`, `workLocationId`. | No |
| GET | `/api/leave-attendance/attendance/daily` | Get employee daily attendance. Query: `clientId`, `employeeId`, `month`. | No |
| GET | `/api/leave-attendance/attendance/daily-grid` | Get attendance review grid. Query: `clientId`, `month`, `workLocationId`. | No |
| POST | `/api/leave-attendance/attendance/daily` | Save one employee daily attendance. | Possible |
| POST | `/api/leave-attendance/attendance/daily/batch` | Save batch daily attendance. | Possible |

## Leave Masters And Holidays

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/leave-attendance/leave-types` | List leave types. Query: `clientId`. | No |
| POST | `/api/leave-attendance/leave-types` | Save leave type. | Possible |
| POST | `/api/leave-attendance/leave-types/{id}/status` | Activate or deactivate leave type. Query: `clientId`, `isActive`. | Possible |
| DELETE | `/api/leave-attendance/leave-types/{id}` | Delete leave type. Query: `clientId`. | Possible |
| GET | `/api/leave-attendance/holidays` | List holidays. Query: `clientId`, `year`, `workLocationId`. | No |
| POST | `/api/leave-attendance/holidays` | Save holiday. | Possible |
| DELETE | `/api/leave-attendance/holidays/{id}` | Delete holiday. Query: `clientId`. | Possible |
| GET | `/api/leave-attendance/import-balances/sample` | Download leave balance import sample. Query: `clientId`. | No |
| POST | `/api/leave-attendance/import-balances/preview` | Preview leave balance import. Form data. | No |
| POST | `/api/leave-attendance/import-balances/finalize` | Finalize leave balance import. | Possible |

## Employees

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/employees` | List employees. | No |
| POST | `/api/employees` | Create or update employee. Query can include `infotypeCode`, `changeReason`. | Possible |
| GET | `/api/employees/{id}/delete-preview` | Preview employee delete impact. | No |
| DELETE | `/api/employees/{id}` | Delete employee. | Possible |
| GET | `/api/employees/{id}/infotypes` | Get employee infotype records. Query: `activeOnly`. | No |
| GET | `/api/employees/{id}/audit` | Get employee audit trail. | No |
| GET | `/api/employees/infotypes/active` | Get latest active employee infotypes. Query: `clientId`. | No |
| POST | `/api/employees/actions` | Run employee action, such as hire, promotion, transfer, salary change, retirement. | Yes |
| GET | `/api/employees/import-template` | Download employee import template. Query: `clientId`. | No |
| POST | `/api/employees/import` | Import employees from file. Form data. | Possible |
| POST | `/api/employees/import-jobs` | Start employee import job. Form data. | Possible |
| GET | `/api/employees/import-jobs/{jobId}` | Check employee import job status. | No |

## Payroll

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/pay-runs` | List pay runs. | No |
| GET | `/api/pay-runs/{id}` | Get pay run details. | No |
| GET | `/api/pay-runs/{id}/diagnostics` | Get pay run diagnostics. | No |
| POST | `/api/pay-runs` | Create or queue draft pay run. | Possible |
| PUT | `/api/pay-runs/{payRunId}/employees/{employeeId}` | Include or exclude one employee in pay run. | Possible |
| POST | `/api/pay-runs/{id}/submit` | Submit or lock payroll for approval. | Yes |
| POST | `/api/pay-runs/{id}/approve` | Approve payroll directly, unless workflow is pending. | Usually no, approval should happen from workflow task |
| POST | `/api/pay-runs/{id}/recall` | Recall payroll. | Possible |
| POST | `/api/pay-runs/{id}/payments` | Record payroll payment. | Possible |
| DELETE | `/api/pay-runs/{id}` | Delete pay run and clean related data. | Possible |
| GET | `/api/pay-runs/{id}/export` | Export pay run. | No |

## Payroll Adjustments

| Method | Path | Purpose | Workflow candidate |
| --- | --- | --- | --- |
| GET | `/api/payroll-adjustments` | List payroll adjustments. Query: `clientId`, `payPeriod`, `status`. | No |
| POST | `/api/payroll-adjustments` | Save payroll adjustment. | Yes |
| DELETE | `/api/payroll-adjustments/{id}` | Delete payroll adjustment. | Possible |

## Notes For Workflow Configuration

- Prefer workflow only for meaningful business submissions, not every save.
- For URL values, request paths use placeholders like `/api/pay-runs/{id}/submit`.
- For body values, choose "Form/request value" and enter the JSON field name.
- For query values, choose "Query string value" and enter the query parameter name.
- For API-created records where the ID is returned by the API, choose "API response value".
- Prefer "Lookup from table" for client detection when the request only has a record ID.
- Avoid configuring workflow for the workflow approval endpoints themselves.
