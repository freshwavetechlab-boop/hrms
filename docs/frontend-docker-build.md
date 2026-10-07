# Frontend Docker builds

Payroll UI and ESS import the repository's `shared/attendanceShift.ts`. Both Docker images must use the repository root as their build context, preserving each app alongside `shared` during compilation.

In Coolify's Dockerfile build configuration:

| Setting | Payroll UI | ESS |
| --- | --- | --- |
| Base Directory | `/` | `/` |
| Dockerfile Location | `/payroll-ui/Dockerfile` | `/ess-mss/Dockerfile` |

If a separate build-context setting is present, use the repository root. Commit/push the Dockerfile and matching `Dockerfile.dockerignore` changes, update these settings, and redeploy. The Dockerfile-specific ignore files restrict each build context to its frontend and shared source and exclude local dependencies/build output.

Equivalent commands from the repository root:

```powershell
docker build -f payroll-ui/Dockerfile -t frevo-payroll-ui .
docker build -f ess-mss/Dockerfile -t frevo-ess .
```

Both images still serve static files through Nginx on port 80 using their existing Nginx configuration. No API, database or attendance calculation changes are required.
