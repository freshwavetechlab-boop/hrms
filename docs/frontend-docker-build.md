# Frontend Docker builds

Payroll UI and ESS each build from their own application folder, matching the original Coolify setup. Their Dockerfiles copy local package files, source and `nginx.conf`; they do not require the repository root in the build context.

In Coolify's Dockerfile build configuration:

| Setting | Payroll UI | ESS |
| --- | --- | --- |
| Base Directory | `/payroll-ui` | `/ess-mss` |
| Dockerfile Location | `/Dockerfile` | `/Dockerfile` |

Keep these existing settings and redeploy after committing/pushing the code. If the preceding repository-root workaround was applied, restore the app-folder settings above. The Dockerfile-specific ignore files exclude local dependencies/build output without removing application source or `nginx.conf`.

Equivalent commands from the repository root:

```powershell
docker build -t frevo-payroll-ui ./payroll-ui
docker build -t frevo-ess ./ess-mss
```

Both images still serve static files through Nginx on port 80 using their existing Nginx configuration. No API, database or attendance calculation changes are required.

## Shared attendance helper

`shared/attendanceShift.ts` remains the canonical source. Each frontend imports a committed generated copy at `src/shared/attendanceShift.ts`, so an isolated application folder contains every source dependency. Edit the canonical file only, then regenerate and commit both copies:

```powershell
node shared/sync-attendance-shift.mjs
node shared/sync-attendance-shift.mjs --check
node --test payroll-ui/tests/attendance-shift.test.mjs
```

The attendance tests exercise the app-local helper and check that both generated copies match the canonical source, including line-ending normalization for Windows/Linux. Build does not fetch code from GitHub or require a parent folder at deployment time.
