# FrevoPilot portal analytics

The portal reuses the original FrevoPilot planner, hybrid RAG retrieval, SQL validator and chart builder from the local journey tool. It does **not** start that tool's web server, provision database users, grant privileges, enable write agents, or migrate analytics tables.

Only an active global `super_admin` can start/read an analysis. Every poll revalidates login and role; runs are owner-bound and expire after one hour. The API executes validated statements inside MySQL read-only transactions with 15-second query limits and 500-row output limits. Two runs may execute concurrently; one per user.

Provider access uses the existing global AI integration, credential protector, inference gate, request limits and usage accounting. Keys never enter the browser or Node process. A genuine provider-unavailable fallback must be labeled; unsupported questions must fail instead of silently answering a different question.

Local setup: run `npm ci --ignore-scripts` in this directory; Node 22+ must be available on PATH. Backend Docker builds install the pinned dependency tree and Node automatically. No DB migration is required. Set `FrevoPilot:Enabled=false` to disable access, or `FrevoPilot:NodeExecutable` for a custom executable path.

Verification hosts can use `BackgroundWorkers:Enabled=false` so unrelated payroll, notifications and recruitment queues are not consumed. The default remains true for normal deployments. This does not disable analytics provider usage/audit accounting.

Results are point-in-time live DB snapshots. Refresh by asking again; charts do not imply streaming business data. Run history is in memory for this first release, so API restarts invalidate old run IDs. Cross-instance polling requires sticky routing until a shared job store is added. Business records are read-only; normal API audit and AI usage telemetry are recorded.
