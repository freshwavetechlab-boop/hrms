# Local CL/LWP policy simulator milestone

> Current runtime update: existing Playwright 1.62.1 was found and imported successfully without installation. The earlier missing-module error was runner resolution plus a sandbox browser-cache path mismatch. Mocked browser E2E passed 10 scenario groups after user-started frontend readiness; see [local browser validation](local-browser-validation.md). Historical missing-dependency notes below are superseded.

Open Leave & Attendance → Attendance Policies → **CL / LWP simulator**. The entry is in the persistent page header. This is an interim/user-proposed scenario, not a published client rule. CL and LWP are the only leave categories in this milestone. No allowance, period, available balance or approval is preselected as fact.

## Behavior

- Choose whether attendance means actual worked days or already-payable days. For worked-day scenarios, enter non-overlapping paid weekly offs/holidays, reviewed eligibility, proposed CL limit/period, already-used days, available balance, requested allocation, approval assumption and remaining-absence treatment. Unknown inputs remain unknown.
- A fully specified hypothetical 30-day cycle with 20 worked, 4 paid weekly offs, 0 paid holidays, 4 available/reviewed proposed CL and remaining absence as LWP yields 4 scenario CL, 2 LWP and 28 payable days. This example is not a grant, approved leave or annual client policy.
- Already-payable attendance is never increased again by CL/off/holiday credit. Included CL is only an annotation. Extra requested CL is blocked until cleared. Source day labels alone do not establish worked-day semantics.
- Half-days and explicitly reviewed partial-cycle eligibility are supported. Effective dates inside the cycle, unknown weekly offs/eligibility, invalid divisors and unresolved calendar-to-payroll unit conversion block a resolved result rather than guessing a split or cap.
- Enter file/sheet/cell references and source payable/LWP/divisor to see day variances and reasons. Original workbooks, VLOOKUP formulas and cached amounts are not changed or loaded automatically by this simulator.
- Salary impact now selects a saved client template snapshot and calls the actual server Template simulator for source and scenario. Component/earnings/deduction/employer/net deltas come from those results; no proportional net estimate is used. See [coverage and limits](native-conditional-salary-impact.md).

## Draft and activation boundary

**Save browser draft** stores only scenario inputs in this device's local storage, scoped to signed-in user and client. It is not a server policy draft or published policy version. Identical saves are a no-op; stale revisions are rejected. Reopen restores the explicit draft. Manage-settings permission is required to save. No employee data import is part of this interface; use only non-personal source references.

**Activate policy is disabled and there is no activation API.** No leave balance, accrual, approval, ledger, master, attendance or payroll record is changed. An actual November policy still needs authorized approval of client/cohort, effective date/cycle, entitlement/grant/accrual/opening balance, approval/LWP rules, weekly-off/holiday treatment and salary divisor. Existing history/payruns must be preserved.

## Verification status and next user action

Backend and frontend regression results are recorded in the latest task checkpoint. Shared main-calculator changes and their synthetic tests are included in the native conditional milestone; no migration was applied.

`tools/test-cl-lwp-simulator-ui.mjs` reuses the repository bootstrap harness's Playwright discovery, Vite fixture and strict network-mocking pattern. It is syntax-checked and passed **10 mocked browser scenario groups** at desktop/mobile sizes. It uses a fresh browser context, synthetic auth and scenarios, rejects non-local requests and all non-mocked API calls, and tests desktop/mobile, repeated draft save, close/reopen, insufficient balance, no double credit and stale revision. No production Apply is part of it.

Playwright 1.62.1 and Chromium1234 are resolved without installation. The user-started frontend was tested with a fresh isolated mocked context. Follow [local browser validation](local-browser-validation.md); do not install another dependency or start/restart services automatically.

The user starts services, after verifying the API's configured database is an isolated test target; localhost/Development does not prove that database choice. From `D:\FrevOHRMS\hrms`, the existing API profile command is `dotnet run --project Payroll.API/Payroll.API.csproj --launch-profile http` (profile URL `http://localhost:5062`). In `payroll-ui`, the existing dev command is `npm.cmd run dev`; use the local URL printed by Vite. The agent has not started/restarted either service, installed packages, applied migrations, committed, pushed or deployed.

After approval and user readiness, run the mocked CL/LWP harness using `HRMS_TEST_UI_URL` set to the verified local Vite URL and `HRMS_PLAYWRIGHT_MODULE` when using a supplied installed module. Inspect its desktop/mobile screenshots and fix/retest any browser bugs within the agreed feature scope. Do not treat syntax/unit checks as completed E2E.
