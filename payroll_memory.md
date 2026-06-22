# Payroll Project Memory

## Working Style
- User is building an enterprise HRMS/payroll product, not a prototype.
- Work implementation-first: make the change, validate it, then give a concise outcome.
- Hinglish is welcome where natural. User values clarity, compact UI, client-scoped data, and scalable module boundaries.
- Do not revert unrelated work in the dirty workspace.

## Environment
- Repository: `D:\Personal\payroll`
- Shell: Windows PowerShell
- Database: MySQL database `payroll`
- API: .NET 8 minimal API in `Payroll.API`; default URL `http://localhost:5062`
- Admin frontend: React, TypeScript, Vite in `payroll-ui`
- ESS/MSS frontend: React, TypeScript, Vite in `ess-mss`
- API Debug executable may be locked by a running process. Use `dotnet build Payroll.API/Payroll.API.csproj -c Release` for dependable validation.
- Build commands:
  - `dotnet build Payroll.API/Payroll.API.csproj -c Release --no-restore`
  - `cd payroll-ui; npm run build`
  - `cd ess-mss; npm run build`
- Always restart the API after backend/startup DDL changes. A stale Debug process has caused several misleading tests in the past.

## Authentication / Security
- Auth, RBAC, and audit exist in `Payroll.API/Models/Auth.cs`, `Repositories/AuthRepository.cs`, and `Program.cs` endpoints.
- Bootstrap admin:
  - `admin@paymint.local`
  - `Admin@12345`
- Frontend bearer injection is in `payroll-ui/src/main.tsx`.
- `/api/*` requires auth except `/api/auth/login`.
- Security is a top-level admin module with `Users`, `Roles`, and `Audit` menus.

## Admin Application Architecture
- Main shell: `payroll-ui/src/SettingsApp.tsx`; routes: `payroll-ui/src/AppRoutes.tsx`.
- Module switcher is a drawer. The left panel shows the selected module's contextual submenus.
- Profile/logout belongs to the top-right user menu; it must not be a floating bottom-right button.
- The module switcher control belongs at the lower-right of the left panel.
- `My Tasks` is a permanent, top-level shortcut in the left panel. In the admin portal it opens only the current user's task list, not the complete Workflow module.
- Use compact, scannable pages. Avoid oversized cards, unexplained controls, redundant button grids, and high padding.
- Existing shared UI/services/types are under `payroll-ui/src/components`, `src/services`, `src/types`, and `src/utils`.

## Core Modules

### Payroll
- Submenus: `Pay Run`, `Pay History`.
- Pay-run flow supports client selection, employee selection, draft creation, prior-run comparison, variance highlights, lock/send for approval, approve/recall/export.
- Statuses: `Draft`, `Pending Approval`, `Approved`.
- Backend: `Models/PayRun.cs`, `Repositories/PayRunRepository.cs`, `Program.cs` endpoints.

### Settings
- Menus: Organization, Clients, Work Locations, Dropdown Masters, Tax, Pay Schedule, Statutory, Salary Components, Salary Templates, Payslip Templates.
- Client-specific settings should always be filtered and saved by client where relevant.
- Services: `payroll-ui/src/services/settingsService.ts`, `services/payrollService.ts`.

### Employees
- Tabs: Basics, Salary, Personal, Payment.
- Employee master and salary assignment: `payroll-ui/src/pages/EmployeePage.tsx`.
- Salary calculation: `payroll-ui/src/utils/salary.ts` (`calculateSalaryJson`).

## RECL Client Data
- RECL is configured as client ID `6`.
- About 93 employees were imported from `RECL DATA - Copy.xlsx`; duplicate employee codes were intentionally skipped.
- RECL salary template was created and should be assigned to imported employees with Excel-provided values. Verify template linkage and deductions if working further in this area.
- Test ESS employee:
  - Employee code: `REC135`
  - Name: Surjeet Kumar
  - Email: `surjeetpdcl123@gmail.com`
  - Password: `Welcome@12345`
  - Auth user ID `2`, employee ID `243`, role `employee`
- Uploaded leave balances for this employee (as of 21-Jun-2026): `CL 10`, `EL 18`, `SL 12`, `LWP 0`.

## Leave & Attendance (Client Scoped)
- Top-level admin module; route/page: `payroll-ui/src/pages/LeaveAttendancePage.tsx`.
- Every implemented area is intended to be client scoped: the CRUD form selects a client and data tables filter by selected client.
- Menus: `Preferences`, `Leave Types`, `Holiday`, `Attendance`, `Import Balance`.
- The prior "module enablement" UI was removed because it confused the actual configuration flow. Backend setup/progress tables may still exist for compatibility, but are not a primary UX concept.
- Backend repository: `Payroll.API/Repositories/LeaveAttendanceRepository.cs`.
- Key migration caution: MySQL does not support `ADD COLUMN IF NOT EXISTS` in the target server version. Column creation/indexing must check metadata first. Existing schema can contain `client_id` while C# maps it as `ClientId`.

### Preferences
- Component: `payroll-ui/src/components/LeaveAttendancePreferencesForm.tsx`.
- Stores attendance cycle days, payroll report day, leave encashment inclusion and component.
- Enforces valid calendar days and a 3-7 day buffer after attendance-cycle end.
- Endpoints: `GET/POST /api/leave-attendance/preferences`.
- Table: `leave_attendance_preferences`.

### Leave Types
- Component: `payroll-ui/src/components/LeaveTypesManager.tsx`.
- Full client-scoped CRUD, active state, duplicate code validation, policies/applicability.
- Fields include paid/unpaid, entitlement, prorating, reset/carry-forward/encashment, request preferences, applicability and validity.
- Endpoints:
  - `GET /api/leave-attendance/leave-types`
  - `POST /api/leave-attendance/leave-types`
  - `POST /api/leave-attendance/leave-types/{id}/status`
  - `DELETE /api/leave-attendance/leave-types/{id}`
- Tables: `leave_types`, `leave_type_policies`, `leave_type_applicability`.
- Duplicate code should return a clean 400: `Leave type code already exists. Use a unique code.`

### Holidays
- Component: `payroll-ui/src/components/HolidayManager.tsx`.
- Client-scoped table/calendar, year and work-location filtering, multi-location applicability, overlap validation.
- Endpoints: `GET/POST /api/leave-attendance/holidays`, `DELETE /api/leave-attendance/holidays/{id}`.
- Tables: `holidays`, `holiday_locations`.

### Attendance Settings
- Component: `payroll-ui/src/components/AttendanceSettingsForm.tsx`.
- Stores shift timings, hours calculations, half/full-day thresholds, and regularization policies client-wise.
- Endpoints: `GET/POST /api/leave-attendance/attendance-settings`.
- Table: `attendance_settings`.

### Leave Balance Import
- Component: `payroll-ui/src/components/LeaveBalanceImportManager.tsx`.
- Supports CSV/XLS/XLSX preview, mapping, errors/skips, final import and sample-format download.
- Required input columns: `Employee Number`, `Leave Type`, `Date`, `Count`.
- Use leave **code**, not leave name, in RECL upload files.
- Backend: `Payroll.API/Repositories/LeaveBalanceImportRepository.cs`.
- Endpoints:
  - `GET /api/leave-attendance/import-balances/sample`
  - `POST /api/leave-attendance/import-balances/preview`
  - `POST /api/leave-attendance/import-balances/finalize`
- Tables: `employee_leave_balances`, `leave_balance_import_logs`, `leave_balance_import_errors`.
- `row_number` was renamed to `row_no` because of MySQL `ROW_NUMBER` syntax conflict.
- A legacy/live API mismatch was handled by adding `BalanceCount` and `BalanceDate` alias columns to `employee_leave_balances`; source queries should continue to use correct snake_case aliases.

## Reporting & Analytics
- Reports is a top-level module with a side-menu catalogue, not buttons scattered across a page.
- The reporting structure must be future-ready: category submenus in the left panel; page content opens a report catalogue/list and selected live report.
- Categories include Payroll, Employee, Attendance, Leave, Recruitment, Onboarding, Separation, Compliance, Tax, Loan & Advance, Cost Center, Contractor, MIS, Executive Dashboards, Scheduled Reports, and Report Builder.
- Existing live examples: Payroll Summary, Department Payroll Cost, Location Payroll Cost, Employee Master, Department Headcount, and Leave Balance.
- Unimplemented source-dependent reports should have their menu/submenu/page and clearly show `Planned`; do not fake data.
- Reporting is client scoped. Use selected client data and export CSV where available.
- Avoid old-fashioned button groups. A clear report list with status badges and a selected report surface is the established direction.

## Generic Workflow Engine
- Workflow is a separate top-level module in admin navigation with contextual menus/submenus. It is generic, not tied to Leave/Attendance/Payroll.
- Foundation components:
  - Workflow Master
  - Workflow Stages
  - Workflow Instance
  - Workflow Tasks
  - Workflow History
- Supported now: sequential approval, approve, reject, send back; approver types Reporting Manager, Department Head, HR Manager, Specific User.
- Deliberately not built: escalation, delegation, SLA, parallel approval, analytics, visual workflow designer. Schema must allow adding these later.
- Models: `Payroll.API/Models/Workflow.cs`
- Repository: `Payroll.API/Repositories/WorkflowRepository.cs`
- Main APIs:
  - create/list/update workflow configuration under `/api/workflows`
  - `POST /api/workflows/start`
  - `GET /api/workflows/tasks/pending`
  - task action endpoint `/api/workflows/tasks/{taskId}/{action}`
  - `GET /api/workflows/{instanceId}/history`
- Tables: `WorkflowMasters`, `WorkflowStages`, `WorkflowInstances`, `WorkflowTasks`, `WorkflowHistory`.
- A workflow is matched to a future form using `ResourceType`, not its display name:
  - Workflow code: unique stable identifier for integrations/configuration, e.g. `LEAVE_REQUEST`.
  - Workflow name: human-readable admin label.
  - Resource type: the generic business object identifier, e.g. `LeaveRequest`; the leave submit API finds an active workflow for this value/client and starts it.
- Seeded examples include `LEAVE_REQUEST` (resource type `LeaveRequest`), `PAYROLL_APPROVAL`, and `SALARY_CHANGE`.
- The seeded Leave Request workflow is two sequential HR/admin approvals. A demo task exists for Surjeet using `DEMO-001`.
- Current send-back behavior marks the current task/instance `Sent Back`; re-opening prior stages is a future refinement if requested.

## ESS / MSS Portal
- Separate application: `ess-mss`, sharing the same API/auth rules but not the admin frontend.
- It has its own compact login and employee/manager workspace. Keep hints useful but short, and avoid excess padding.
- Core pages currently include Dashboard, My Tasks, My Profile, Leave, Attendance, Pay, and manager-aware Team/Approvals views.
- ESS profile uses the authenticated user's linked employee; lack of linkage should show a clear HR-contact message.
- ESS API models/repository are in `Payroll.API/Models/EssMss.cs` and `Payroll.API/Repositories/EssMssRepository.cs`.

### ESS Leave Balances and Application
- ESS `Leave` page now shows uploaded employee leave balances, compact leave application form, and the employee's own request history.
- Frontend: `ess-mss/src/App.tsx` (`LeaveBalances`) and `ess-mss/src/index.css`.
- Form fields: leave type/code, from date, to date, reason.
- It validates selected active leave type/client and balance on the server. Requests above available balance are rejected except `LWP`.
- Endpoint: `GET /api/ess/leave/balances`.
- New endpoints:
  - `GET /api/ess/leave/requests`
  - `POST /api/ess/leave/requests`
- `POST` creates an `EssLeaveRequests` record client/employee-wise and starts the active client workflow with resource type `LeaveRequest`, if configured.
- Startup creates table `EssLeaveRequests` with employee, client, leave type, date range, days, reason, status and created timestamp.
- Test sequence:
  1. Restart API after this addition.
  2. Run ESS and sign in as Surjeet.
  3. Open Leave, submit CL/EL for a valid date range and reason.
  4. Sign in as admin and use permanent `My Tasks`; approve twice for the seeded two-stage leave workflow.
- Current implementation counts calendar days inclusively; no half-day/holiday/weekend calculation yet.

## Backend / Database Notes
- Important repositories:
  - `OrganizationRepository`: Organizations, clients, work locations, dropdowns, employees.
  - `SettingsRepository`: setup JSON.
  - `EmployeeRepository`: employee master.
  - `PayRunRepository`: pay runs.
  - `AuthRepository`: auth/RBAC/audit.
  - `LeaveAttendanceRepository`: client-scoped leave/attendance settings.
  - `LeaveBalanceImportRepository`: import parsing and upserts.
  - `WorkflowRepository`: reusable workflow lifecycle.
  - `EssMssRepository`: ESS profile, balances, leave requests.
- `Payroll.API/Program.cs` contains endpoint registration and several runtime table initialization blocks.
- Keep `Payroll.API/Database/Init.sql` aligned with repository/startup DDL as the schema evolves.
- `using Dapper;` is required in Program for the ESS leave request initialization/endpoints.

## Known Cautions / Technical Debt
- `payroll-ui/src/OrganizationSetup.css` is large and has accumulated overrides; split module CSS only when doing a focused cleanup.
- Some existing JSX/CSS is compressed into long lines. Do not propagate that style in new code.
- Browser 400s in console can be expected for intentionally rejected validation requests; UI should show meaningful messages.
- Avoid direct assumptions about a running API binary: build source, restart API, then test endpoint behavior.
- There was a prior anonymous `/api/ess/leave/balances` diagnostic response; correct behavior is authenticated ESS access only.
- Never use module enablement as a primary leave/attendance UX control again; client picker + scoped CRUD is clearer.

## Latest Verification
- After ESS leave apply implementation:
  - `npm run build` in `ess-mss`: passed.
  - `dotnet build Payroll.API/Payroll.API.csproj -c Release --no-restore`: passed, 0 warnings/errors.

## Recent Development (22-Jun-2026)

### Workflow
- Workflow Setup supports editing existing workflows, client scope (`All clients` global fallback or a client override), active state, and ordered approval stages.
- Approver resolution:
  - Reporting Manager uses the requester employee's reporting-manager link.
  - HR Manager uses an active client HR Manager, falling back to a global one.
  - Specific User has a client-filtered user picker.
  - Department Head uses explicit `Client + Department -> User` assignments; it does not depend on designation.
- `Department Head Assignments` is its own Workflow menu item.
- Workflow History is live: request list, payload/request details, and approval trail with remarks.
- Admin My Tasks and ESS My Tasks review request payload details and capture approver remarks.
- Leave workflow completion now synchronizes `EssLeaveRequests.Status`. Startup reconciliation also corrects old approved/rejected/sent-back LeaveRequest workflow instances. Restart API to apply this reconciliation.

### Payroll / Reports
- RECL Apr-2026 run exists as Pay Run #6, is `Approved`, contains 93 employees and gross payroll cost ₹46,07,823.40, but has ₹0 net pay because imported/static deductions were excessive. Do not mark it paid; recall/recreate only after reviewing the corrected formula impact.
- Payroll Summary report was fixed (the old query referenced a nonexistent `PayRuns.EmployeeCount` column). Salary Register now reads actual `PayRunEmployees` records.
- Payslip Register is live under Reports > Payroll Reports: client/run filters, employee preview modal, and HTML payslip download.
- Pay History now provides a payment-recording panel for approved runs: select unpaid employees, set payment date, and mark full/partial payments. It prevents recording when selected net pay is zero.
- RECL-only component catalogue and `RECL Formula Payroll` template were saved via `/api/setup` (template ID 602; component IDs 601-626).
- Future/draft RECL pay runs use the RECL runtime calculation in `PayRunRepository.BuildReclEmployee`:
  - derives source `GROSS` from employee salary breakup;
  - calculates specified Basic/HRA/fixed allowances/bonus/earned values/PF/PT/net pay/employer cost;
  - records TA/DA, TDS, and Recovery as RECL draft manual inputs.
- `ManualTds` was added to `PayRunEmployees`; API restart is required to add the column. The April approved run is intentionally unchanged.
- Salary Template Designer canvas now has fixed columns and horizontal scrolling, preventing long RECL codes/formulas from overlapping.

### Security / Employee Provisioning
- Security > Users now has a prominent client filter, client-scoped user counts, searchable directory, and client-wise searchable employee provisioning control.
- `Employees without login` selects active client employees with work email but no linked auth user, then prefills an ESS user form.
- Successful new employee-user creation shows a one-time credentials handoff (login ID + temporary password); user must change password on first login.
- Existing users can be opened from the directory to update access, roles, active state, linked employee/client, or reset password.

### ESS / MSS
- My Profile and Logout are in the top-right employee account dropdown, not in the sidebar.
- Pay page is live: employee-specific approved-pay-run history, payslip preview, and HTML download.
- Dashboard now includes live leave balance/request/task data plus month-filtered payroll-based Present Days, Payable Days, Working Days, upcoming client holidays, client birthdays today, and a leave/holiday-marked calendar.
- Attendance daily records, Team/reportee data, HR announcements, and published jobs still need their own source/admin publishing modules; their ESS areas should not use fabricated data.

### Current Validation
- `dotnet build Payroll.API/Payroll.API.csproj -c Release --no-restore`: passed after workflow/ESS/payroll changes.
- `npm run build` in `payroll-ui`: passed after RECL payroll and template table updates.
- `npm run build` in `ess-mss`: passed after dashboard, pay, and account-menu updates.
