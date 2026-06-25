# Production Runbook

Required runtime configuration:

```text
ConnectionStrings__Default=Server=...;Database=payroll;User ID=...;Password=...;SslMode=Required;
CORS_ALLOWED_ORIGINS=https://your-hr-domain.example
Auth__CookieSecure=true
Database__InitializeOnStartup=false
Database__CommandTimeoutSeconds=30
```

Checks:

```text
GET /health
GET /ready
```

Expected deployment flow:

1. Apply reviewed SQL migrations before starting app instances.
2. Start API with production env vars.
3. Confirm `/ready` returns `ready`.
4. Start frontend with `VITE_API_URL` set to the API origin.
5. Monitor `Payroll.API/logs/payroll-api-*.log`.

Local script:

```powershell
$env:PAYROLL_MYSQL_PASSWORD="your-local-password"
.\Start-Payroll.ps1
```
