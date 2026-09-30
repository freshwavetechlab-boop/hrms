# Frevo HRMS desktop launcher

Install from the repository root (Windows PowerShell):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\dev-launcher\Install.ps1
```

Open **Frevo HRMS Launcher** on the desktop. Each application has its folder,
Start/Stop buttons and browser link; **Start all** launches all three and
**Stop all** stops them. Existing listeners
are left running and duplicate starts are skipped.

| Application | Default folder | Address |
| --- | --- | --- |
| Payroll UI | payroll-ui | http://localhost:5173 |
| Payroll API | Payroll.API | http://localhost:5062/swagger |
| ESS | ess-mss | http://localhost:5174 |

Use **Choose project folder** after moving the repository, or **Choose folder**
for individual projects. Locations persist in
`%LOCALAPPDATA%\FrevoHRMSLauncher\settings.json`. The launcher and icon live on C:
independently of the project drive. Closing the launcher leaves the apps running.

Node.js/npm and the project's .NET SDK must already be installed. Missing Vite
dependencies are installed using `npm ci` (or `npm install` without a lockfile).
The API builds on start and uses the existing `http` launch profile/appsettings,
with automatic database migrations, background workers and engine history/activity
disabled for local development. No appsettings files or database schemas are changed.

**Open logs** opens timestamped application output/error logs. If a port is already
occupied, use the browser link to check the listener. **Stop** shuts down the app
and its child processes. It can also stop an existing Vite instance on the app's
port or a Payroll API instance from the chosen project; other listeners are left
alone. Stop an existing app before changing its individual folder.
