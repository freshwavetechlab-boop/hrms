# Payroll Project Memory

## User / Working Style
- User wants concise, direct, implementation-first help.
- Tone can be Hinglish/Hindi-friendly; user often says “bhai”.
- User is building an enterprise-grade HRMS/payroll product, not a demo.
- Prefer scalable, future-ready architecture over quick hacks.
- Do not ask for approval for normal code changes; proceed, validate, and summarize.
- User likes proactive improvements wherever future issues are likely.

## Environment
- Repo path: `D:\Personal\payroll`.
- Shell: Windows PowerShell.
- Frontend: React + TypeScript + Vite in `payroll-ui`.
- Backend: .NET 8 minimal API in `Payroll.API`.
- Database: MySQL database named `payroll`.
- Central stylesheet: `payroll-ui/src/OrganizationSetup.css`.
- API default URL: `http://localhost:5062`.
- If Debug build is locked by running API, use `dotnet build Payroll.API/Payroll.API.csproj -c Release`.
- Running API process can lock `Payroll.API/bin/Debug/net8.0/Payroll.API.exe`; release DLL smoke tests work around this.

## Auth / Security
- Auth + RBAC + Audit implemented.
- Bootstrap admin:
  - Email: `admin@paymint.local`
  - Password: `Admin@12345`
- Login screen: `payroll-ui/src/components/AuthGate.tsx`.
- Frontend injects bearer token by patching `window.fetch` in `payroll-ui/src/main.tsx`.
- Backend auth middleware protects `/api/*` except `/api/auth/login`.
- Security is a top-level module, not under Settings.
- Security submenus: `Users`, `Roles`, `Audit`.
- Security UI: `payroll-ui/src/components/SecurityPanel.tsx`.
- Backend auth files:
  - `Payroll.API/Models/Auth.cs`
  - `Payroll.API/Repositories/AuthRepository.cs`
  - endpoints in `Payroll.API/Program.cs`.

## App Shell / Navigation
- `SettingsApp.tsx` is now an app shell.
- Routes moved to `payroll-ui/src/AppRoutes.tsx`.
- `main.tsx` mounts `<AppRoutes />` inside `StrictMode` and injects auth token into fetch.
- App module selector opens as a right-side drawer from topbar icon; it is not always visible.
- Left sidebar shows contextual submenu for selected module.
- Current app drawer modules:
  - `Payroll`
  - `Leave & Attendance`
  - `Employees`
  - `Security`
  - `Settings`
  - `Reports` disabled / coming soon
- This drawer + contextual submenu pattern should be used for future HRMS modules.

## Frontend Architecture / Refactor
- Requirement was to keep components under 300 lines where possible.
- API calls moved to services under `payroll-ui/src/services/`.
- Salary calculation moved out of React into `payroll-ui/src/utils/salary.ts`.
- Shared types live in `payroll-ui/src/types/payroll.ts`.
- Shared defaults live in `payroll-ui/src/data/payrollDefaults.ts`.
- Shared primitives/components include:
  - `payroll-ui/src/components/AppIcon.tsx`
  - `payroll-ui/src/components/FormPrimitives.tsx`
  - `payroll-ui/src/components/SetupCard.tsx`
- Compatibility wrapper files exist and re-export newer components/pages:
  - `payroll-ui/src/App.tsx`
  - `payroll-ui/src/OrganizationSetup.tsx`
  - `payroll-ui/src/PayrollSetup.tsx`

## Current Page Files
- `payroll-ui/src/pages/PayrollPage.tsx` wraps PayRunsPanel.
- `payroll-ui/src/pages/PayHistoryPage.tsx` wraps PayHistory.
- `payroll-ui/src/pages/EmployeePage.tsx` contains employee master and salary assignment logic.
- `payroll-ui/src/pages/SettingsPage.tsx` contains settings screens.
- `payroll-ui/src/pages/LeaveAttendancePage.tsx` contains Leave & Attendance module routing.
- `payroll-ui/src/SettingsApp.tsx` renders shell, topbar, sidebar, drawer, and selected page.

## Payroll Module
- Payroll submenus:
  - `Pay Run`
  - `Pay History`
- Pay History was made standalone and opens inside normal app shell with sidebar/topbar.
- `PayRunsPanel.tsx` no longer contains embedded pay history UI.
- Pay run workflow includes:
  - select client
  - show active employees
  - allow deselecting employees before draft
  - create draft payroll
  - compare with previous payroll
  - show variance highlights
  - lock/send for approval
  - approve/recall/export
- Backend pay-run files:
  - `Payroll.API/Models/PayRun.cs`
  - `Payroll.API/Repositories/PayRunRepository.cs`
  - endpoints in `Payroll.API/Program.cs`
- PayRun status flow includes `Draft`, `Pending Approval`, `Approved`.

## Settings Module
- Settings submenus:
  - `Organization`
  - `Clients`
  - `Work Locations`
  - `Dropdown Masters`
  - `Tax`
  - `Pay Schedule`
  - `Statutory`
  - `Salary Components`
  - `Salary Templates`
  - `Payslip Templates`
- Services:
  - `payroll-ui/src/services/settingsService.ts`
  - `payroll-ui/src/services/payrollService.ts`
- Client pay schedule editor: `payroll-ui/src/components/ClientPayScheduleManager.tsx`.

## Employee Module
- Employee tabs:
  - `Basics`
  - `Salary`
  - `Personal`
  - `Payment`
- Employee salary calculation uses `calculateSalaryJson` from `payroll-ui/src/utils/salary.ts`.
- Employee save uses settings/payroll services.

## Leave & Attendance Module Overview
- Dedicated top-level module: `Leave & Attendance`.
- Left submenus:
  - `Preferences`
  - `Leave Types`
  - `Holiday`
  - `Attendance`
  - `Import Balance`
- Original dashboard tiles/cards were removed from main content because they consumed too much vertical space.
- Current behavior:
  - Left menu is primary navigation.
  - Actual page/form opens directly.
  - Compact page header shows current page title/status/actions.
  - `Mark completed`, `Disable page`, and `Disable module` are compact controls in the page header.
- Page file: `payroll-ui/src/pages/LeaveAttendancePage.tsx`.
- Setup progress still exists in backend.
- General Settings / Preferences is mandatory and cannot be disabled.
- Backend setup endpoints:
  - `GET /api/leave-attendance/setup`
  - `POST /api/leave-attendance/module`
  - `PUT /api/leave-attendance/setup/{stepCode}`
- Setup DB tables:
  - `ModuleSettings`
  - `ModuleSetupProgress`

## Leave & Attendance: Preferences
- Preferences page implemented.
- Component: `payroll-ui/src/components/LeaveAttendancePreferencesForm.tsx`.
- Fields:
  - attendance cycle start day
  - attendance cycle end day
  - payroll report generation day
  - include leave encashment in pay run
  - leave encashment salary component
- Validation:
  - days 1–31
  - payroll report generation day must have 3–7 day buffer after attendance cycle end day
  - leave encashment only allowed with formula-based salary component
- Backend model in `Payroll.API/Models/LeaveAttendance.cs`.
- Repository methods in `Payroll.API/Repositories/LeaveAttendanceRepository.cs`.
- Endpoints:
  - `GET /api/leave-attendance/preferences`
  - `POST /api/leave-attendance/preferences`
- MySQL table:
  - `leave_attendance_preferences`

## Leave & Attendance: Leave Types
- Leave Types module implemented.
- Frontend component: `payroll-ui/src/components/LeaveTypesManager.tsx`.
- Features:
  - list leave types in table
  - add/edit leave type
  - enable/disable
  - delete with confirmation
  - client-side duplicate code validation before API call
  - server-side duplicate code validation with clean error
- Fields include:
  - leave type name
  - code
  - paid/unpaid
  - description
  - entitlement number and period
  - pro-rate for new joinees
  - reset balance settings
  - carry forward settings
  - encashment settings
  - leave request preferences
  - applicability criteria
  - validity dates
  - postpone credits for new employees
- Backend model:
  - `LeaveType`
  - `SaveLeaveTypeRequest`
- Backend repository methods:
  - `GetLeaveTypesAsync`
  - `SaveLeaveTypeAsync`
  - `SetLeaveTypeActiveAsync`
  - `DeleteLeaveTypeAsync`
- Endpoints:
  - `GET /api/leave-attendance/leave-types`
  - `POST /api/leave-attendance/leave-types`
  - `POST /api/leave-attendance/leave-types/{id:int}/status?isActive=true|false`
  - `DELETE /api/leave-attendance/leave-types/{id:int}`
- MySQL tables:
  - `leave_types`
  - `leave_type_policies`
  - `leave_type_applicability`
- Important fix:
  - Duplicate `code` now returns: `Leave type code already exists. Use a unique code.`
  - Browser console may still show 400 for backend validation failures, but UI now catches duplicate code first.

## Leave & Attendance: Holiday Management
- Holiday Management module implemented.
- Frontend component: `payroll-ui/src/components/HolidayManager.tsx`.
- Features:
  - table view
  - calendar view
  - filters by year and work location
  - add/edit/delete holiday
  - all locations or multiple selected work locations
  - single-day holidays supported by same start/end date
  - duplicate holiday blocked for overlapping date range and same applicable location
- Backend model:
  - `Holiday`
  - `SaveHolidayRequest`
- Endpoints:
  - `GET /api/leave-attendance/holidays?year=&workLocationId=`
  - `POST /api/leave-attendance/holidays`
  - `DELETE /api/leave-attendance/holidays/{id:int}`
- MySQL tables:
  - `holidays`
  - `holiday_locations`

## Leave & Attendance: Import Employee Leave Balance
- Import Balance module implemented.
- Frontend component: `payroll-ui/src/components/LeaveBalanceImportManager.tsx`.
- Features:
  - upload CSV/XLS/XLSX
  - select file
  - select encoding, default UTF-8
  - download sample file button
  - auto-map columns
  - mapping screen with manual adjustment
  - preview valid and skipped/error records
  - highlight unmapped fields
  - final import button
  - successful import updates employee leave balances
- Required columns:
  - `Employee Number`
  - `Leave Type`
  - `Date`
  - `Count`
- Backend parser/repository:
  - `Payroll.API/Repositories/LeaveBalanceImportRepository.cs`
- Packages added:
  - `ExcelDataReader` 3.8.0
  - `System.Text.Encoding.CodePages` 8.0.0
- Backend endpoints:
  - `GET /api/leave-attendance/import-balances/sample`
  - `POST /api/leave-attendance/import-balances/preview`
  - `POST /api/leave-attendance/import-balances/finalize`
- Preview endpoint is excluded from Swagger description because multipart `[FromForm]` metadata caused Swashbuckle errors.
- MySQL tables:
  - `employee_leave_balances`
  - `leave_balance_import_logs`
  - `leave_balance_import_errors`
- Important fix:
  - `row_number` column caused MySQL syntax error due to `ROW_NUMBER`; renamed to `row_no`.

## Leave & Attendance: Attendance Management
- Attendance Management module implemented.
- Frontend component: `payroll-ui/src/components/AttendanceSettingsForm.tsx`.
- Sections:
  - Work Shift Time
    - check-in time
    - check-out time
  - Working Hours Calculation
    - first check-in and last check-out
    - every valid check-in and check-out
  - Workday Duration
    - minimum hours for half-day
    - minimum hours for full-day
    - maximum hours allowed for full-day
  - Regularization Settings
    - allow regularization requests
    - allow anytime OR limit by days before current date
    - number of past days allowed
    - restrict regularization requests per month
    - max regularization requests per month
- Validation:
  - check-out must be after check-in
  - half-day <= full-day <= max full-day
  - hours must be greater than zero
  - past days cannot be negative
  - max monthly regularization requests must be > 0 when restriction enabled
- Backend model:
  - `AttendanceSettings`
  - `SaveAttendanceSettingsRequest`
- Endpoints:
  - `GET /api/leave-attendance/attendance-settings`
  - `POST /api/leave-attendance/attendance-settings`
- MySQL table:
  - `attendance_settings`
- Smoke test for saving attendance settings passed.

## Leave & Attendance Services / Types
- Service file: `payroll-ui/src/services/leaveAttendanceService.ts`.
- Types file: `payroll-ui/src/types/payroll.ts`.
- Important service functions include:
  - `getLeaveAttendanceSetup`
  - `setLeaveAttendanceEnabled`
  - `updateLeaveAttendanceStep`
  - `getLeaveAttendancePreferences`
  - `saveLeaveAttendancePreferences`
  - `getLeaveTypes`
  - `saveLeaveType`
  - `setLeaveTypeStatus`
  - `deleteLeaveType`
  - `getHolidays`
  - `saveHoliday`
  - `deleteHoliday`
  - `getAttendanceSettings`
  - `saveAttendanceSettings`
  - `previewLeaveBalanceImport`
  - `finalizeLeaveBalanceImport`

## Backend Repositories / Tables
- `OrganizationRepository` initializes core tables like Organizations, PayrollSetups, Clients, WorkLocations, DropdownMasters, Employees.
- `SettingsRepository` reads/saves setup JSON from `PayrollSetups`.
- `EmployeeRepository` handles employee master.
- `PayRunRepository` handles payroll runs.
- `AuthRepository` handles auth/RBAC/audit.
- `LeaveAttendanceRepository` handles setup, preferences, leave types, holidays, attendance settings.
- `LeaveBalanceImportRepository` handles CSV/XLS/XLSX parsing, validation, preview, import logs, and balance upsert.
- Keep `Payroll.API/Database/Init.sql` aligned with repository initializer DDL.

## Important Runtime / Error Fixes
- API startup error due to MySQL reserved-ish `row_number` column fixed by renaming to `row_no`.
- Swagger 500 for multipart preview endpoint fixed by excluding preview endpoint from OpenAPI description.
- Holiday endpoint 404/401 confusion:
  - route exists when API is restarted with latest code
  - unauthenticated request returns 401, which confirms route reaches auth middleware
- Leave Type duplicate code:
  - backend returns clean 400 JSON
  - frontend now prevents duplicate code request when current table has the code
- Always restart API after backend changes.
- Hard refresh frontend after TS/UI changes if dev server cache behaves oddly.

## Validation Commands
- Frontend build:
  - `cd payroll-ui; npm run build`
- Backend build:
  - `dotnet build Payroll.API/Payroll.API.csproj -c Release`
- Recent builds passed after:
  - Leave Types
  - Holiday Management
  - Import Balance
  - Attendance Management
  - removing setup dashboard tiles
- Smoke tests performed:
  - API startup
  - Swagger JSON returns 200 after fixes
  - attendance settings save works
  - duplicate leave type returns clean 400 JSON

## Known Cautions
- `OrganizationSetup.css` is large/compressed and has multiple appended overrides. Future cleanup should split CSS by module.
- Some JSX files are still compressed into long lines due earlier style; avoid making them worse. New code can be cleaner.
- If running API locks Debug output, build Release or run Release DLL on another port.
- `SetupCard.tsx` still exists but dashboard tiles are no longer rendered in `LeaveAttendancePage`.
- Setup progress still exists in backend, but visual tiles are removed.
- `PayHistory.tsx` still exists as component wrapped by `PayHistoryPage`.

## Recommended Next Work
1. Build actual leave request workflow:
   - employee applies leave
   - manager approval
   - leave balance deduction
   - LOP impact
2. Connect leave/attendance outputs into payroll draft calculation.
3. Build attendance import/capture module:
   - daily attendance records
   - biometric/manual import
   - regularization requests
4. Add approval workflow engine reusable across payroll, leave, hiring, salary changes.
5. Add audit entries for all Leave & Attendance writes.
6. Build ESS portal for employee self-service.
7. Split `OrganizationSetup.css` into modular CSS files.
