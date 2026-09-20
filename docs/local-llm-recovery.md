# Local LLM manual recovery (opt-in)

Status (2026-09-20): **server bridge installed and control activated** after explicit
approval. The user subsequently reported configuring Coolify and redeploying,
but portal recovery returned HTTP 401/403. The server's original control key
passed an authenticated invalid-body probe without dispatching status/start;
the effective Coolify credential/network path was not independently verified.
The new DB-settings UI/API below is implemented locally, not yet deployed.

AI Integration > saved Local LLM > Test now has adjacent Check/Refresh LLM status
and Start local LLM controls, restricted to an exact global `super_admin` in UI
and API. Start requires confirmation. Starting is not Running: Running requires
the scheduled task, gateway admission marker and authenticated model health.
Refresh status, then use the existing Test button to verify inference.

No schema/migration, inference provider switch, model auto-enable, watchdog limit
change or automatic restart. Existing Gemini/Groq/parser/ATS behavior is unchanged.
Backend records actor/model/request ID, requested and resulting states in existing
audit storage; it refuses to dispatch Start when the requested audit cannot save.
Timeout/unknown outcomes never trigger a retry. A five-minute shared server
cooldown and fixed task identity protect against repeated/concurrent starts.

## Portal configuration (preferred; no new migration)

After deploying **both API and UI**, open **AI Integrations > saved Local LLM >
Recovery settings** as an exact global super-admin. Enter the HTTPS control URL,
the separate 64-hex control key and the enabled switch, then save. The inference
URL is bound to the saved model, and control must use the same HTTPS authority.
Saving refreshes status; it does **not** start the model or change its provider.
Use the existing explicitly confirmed Start button separately.

One global row in existing `modulesettings` (`client_id=0`,
`ModuleCode=local_llm_recovery:0`) stores the configuration. The control key uses
the existing portable integration encryption helper with a separate authenticated
purpose (`llm-control-credential:v1:`), never inference/storage ciphertext. GET
returns credential status, not plaintext or ciphertext. Blank key preserves the
saved key; changed endpoints require re-entry. Version checks prevent stale
editors overwriting settings. Configuration and a secret-free audit commit in
one transaction. No new table, schema migration or model-key rewrite is needed.

Every status/start resolves the current DB row; no API restart is needed after
editing it. A saved row overrides all legacy `LocalLlmRecovery__*` variables,
including when explicitly disabled. Missing row alone permits legacy fallback;
DB outages, corrupt records and unreadable credentials fail closed instead.
Once saved, the old recovery-specific Coolify variables are not required by the
updated API. All API instances must run the updated code before relying on this.

### Localhost / production portability

Both APIs must point at the same logical DB to share settings. The existing
protector ignores DB host/port and machine-local Data Protection key rings. By
default it binds to logical database name, DB user and password; these must
match. If using an explicit `IntegrationCredentialEncryption:MasterKey` (or
supported legacy alias), use the **same effective value** on all instances.
Different DB credentials require a shared explicit integration master key.
Do not rotate or introduce that master key casually: existing AI/storage
credentials also depend on it and need a separately planned migration.
An unreadable key is reported clearly and is not overwritten automatically.

Verification: 136 backend checks passed, including an isolated GUID-named
loopback MySQL persistence test (two instances/key rings, different DB routes,
DB-over-env precedence, blank-key preservation, stale-edit rejection, atomic
audit rollback and unrelated-module preservation). The temporary database was
removed. 33 UI/contract checks and fresh isolated `npm ci` + production build
passed with the unchanged lockfile. No browser, production DB write, deployment
or actual LLM start was performed for this DB-settings change. Existing MailKit,
React/AntD peer and bundle-size warnings remain.

## Legacy API configuration (used only before a DB row is saved)

Set on the HRMS **backend**, not Vite/browser configuration:

```text
LocalLlmRecovery__Enabled=true
LocalLlmRecovery__ControlEndpointUrl=https://eeslindia.org/llm-control.php
LocalLlmRecovery__InferenceEndpointUrl=https://eeslindia.org/llm-api.php
LocalLlmRecovery__ApiKey=<separate 64-hex control secret; never commit>
```

Store the key in private deployment secret settings. It is different from both
inference credentials. Only the saved global `LocalOpenAICompatible` model whose
exact endpoint matches the configured inference URL can use recovery. Control
and inference must have the same HTTPS authority; redirects are disabled. The
browser never receives the control secret or a shell command.

## Approved server installation — completed 2026-09-20

Candidate sources live in
`D:\EESL-Server100-LLM\candidates\local-llm-recovery`, following that workspace's
operating boundary. HRMS deploy alone does not install this server bridge.

The following approved steps were applied on verified DCDB1 / 10.10.91.100;
step 5 was later reported completed by the user, but portal authentication still
needed investigation. Do not blindly repeat installation:

1. Add these three new files (refuse silent replacement):
   - `E:\Datacopy\wordpress\llm-control.php` from `public/llm-control.php`.
   - `E:\LLM\api\control.php` from `api/control.php`.
   - `E:\LLM\scripts\Invoke-LLMControl.ps1` from `scripts/Invoke-LLMControl.ps1`.
2. Generate a cryptographically random 32-byte/64-hex control key into
   `E:\LLM\secrets\control-api-key.txt`. Restrict it to SYSTEM, Administrators and
   the existing deployment identity. Do not give it to Local Service or print it.
3. Create `E:\LLM\api\control-enabled`, initially `disabled`, and initialize
   `E:\LLM\logs\control-state.json`. Restrict control code/marker/state writes to
   SYSTEM/Administrators/deployment identity; Local Service cannot enable control
   or clear cooldown. Apache's verified LocalSystem identity executes this bridge.
4. Lint candidates and verify task action/principal/hash, PHP `proc_open`
   availability, Apache status/config and website baseline. Enable only the new
   control marker after checks pass. Check public missing/wrong-key rejection,
   authenticated status, bad action/oversized request rejection and page health.
   Control requests append bounded metadata to `E:\LLM\logs\control-audit.jsonl`
   (one 1-MiB rotation); no tokens, prompts, credentials or raw exception bodies.
5. Configure the HRMS backend secret settings separately. **No task start is part
   of installation**; test Start only after explicit operator approval/button
   confirmation. Validate task/model readiness and public inference/website health.

Preserve existing `llm-api.php`, `gateway.php`, runtime/model, Host/Start scripts,
task definition/principal, admission policy, WordPress, Apache/PHP config, WAF,
firewall and all databases. No service restart or new scheduled task.

Rollback: disable HRMS recovery flag and the new control marker. Move only the
three new, hash-verified deployment files into a private recovery backup if
necessary, retaining key/state/audit securely. Do not remove/stop the existing
task/model or alter inference gateway behavior as incidental rollback. Recheck
English/Hindi pages, Social Media marker, Apache health and new errors.

## Verification boundaries

Server activation verified at 2026-09-20 05:58 UTC: three deployed file hashes
match their reviewed candidates, separate key/state/code ACLs are restricted,
and nine public HTTPS checks pass (missing/wrong credentials, wrong method,
malformed JSON, unsupported action, extra command field, oversized body, wrong
content type and authenticated stopped status). Public and direct-origin English/
Hindi pages return 200 with unchanged Social Media markers. Apache PID 17544,
protected runtime/gateway/config hashes and parent ACLs are unchanged; no new
Apache/PHP error markers. Task last-run time is unchanged, with zero Start requests.
An initial installer-only stdout-capture failure safely rolled back the new code;
the corrected verifier reapplied the same candidates. Recoverable backup:
`E:\LLM\logs\backups\recovery-control-20260920T055422283Z`.

Full evidence stays in the server-operations workspace under
`candidates/local-llm-recovery/deployed-verification-20260920.json` and the website
reports. Start execution and portal-to-server recovery remain untested until
backend secrets are configured and the operator confirms Start. No model was
started merely to pass installation tests.

2026-09-20 verification: 110 backend tests passed (28 recovery cases including
six direct HTTP denials; 82 existing local-provider regressions); 28 UI/contract
tests passed. Fresh isolated `npm ci` + production UI build passed against the
unchanged lockfile/AntD 5.5.0. PHP syntax and 10 pure validation checks passed on
the installed server PHP via stdin, without copying files or executing Task.Run;
candidate PowerShell syntax passed locally. Existing MailKit NU1902, React/AntD
peer compatibility and large-bundle warnings remain; this change does not fix
or suppress those unrelated warnings. No browser/live task-start test run.

Tests use synthetic HTTP and authorization cases; no production task execution.
PHP/PowerShell syntax and pure contract checks do not establish WAF routing,
scheduled-task execution from Apache, or full end-to-end recovery. Those remain
post-approval deployment checks. Browser testing is not part of this change yet.

The PHP runner uses a fixed executable and parameter array, no caller command or
path, following the [PHP proc_open contract](https://www.php.net/manual/en/function.proc-open.php).
