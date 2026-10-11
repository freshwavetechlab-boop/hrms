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

## Git push and Coolify redeploy

Click **Deploy** in the launcher. The deployment window shows the repository,
current branch/upstream and all files that `git add .` will include. Enter a commit
message and verify the three Coolify **Configuration** URLs (defaults were
transcribed from the supplied UAT screenshots). Click **Commit & push**, then
confirm the Git operation. Ignored files remain excluded. Changes after the
review require a refreshed review. A clean repository skips the commit and pushes
the current commit. No force push, automatic pull, merge or reset is performed.

After a verified Git push, Playwright opens a separate Chrome window. Log in to
Coolify there if requested; no password is collected by the launcher. **Remember
Coolify login** is off by default. If selected, the login session is stored only
in `%LOCALAPPDATA%\FrevoHRMSLauncher\coolify-browser`, separate from everyday
Chrome. App URLs persist in `deploy-settings.json` beside the launcher.

Once all three URLs, application names and Redeploy buttons are verified, the
window enables **Confirm redeploy**. This asks again before clicking any Coolify
Redeploy button. **Cancel** after a push leaves the pushed commit intact and skips
remaining redeploy requests. Closing this deployment window also cancels remaining
work; an operation already in progress may complete. Closing the main launcher
still leaves local development apps running.

Each app is clicked once. A deployment-log URL verifies the request; it does **not**
mean the build succeeded. The three log links open the actual Coolify build
results. An unverified request stops the workflow without automatic retries, so
inspect Coolify before retrying a partial deployment. Git push can itself trigger
deployments if auto-deploy webhooks are enabled in Coolify; use manual deployments
for this confirmation-based workflow. The Coolify apps must track the branch
shown in this window; the launcher does not change their Git source settings.

Re-run `Install.ps1` after editing launcher/deployment scripts. Installation uses
`npm ci` for the pinned Playwright dependency. Google Chrome, Node.js/npm and Git
must already be installed. Deployment job states and logs stay outside the repository.

Run deployment checks (temporary Git repositories and a local mock Coolify server,
no GitHub push or real Coolify deployment):

```powershell
cd tools\dev-launcher
npm ci --ignore-scripts
npm test
```
