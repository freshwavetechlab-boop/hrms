# Local LLM manual recovery (opt-in)

Status: implementation/test candidate, **not activated on the LLM server**.

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

## API configuration (disabled when absent)

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

## Server approval proposal — pending

Candidate sources live in
`D:\EESL-Server100-LLM\candidates\local-llm-recovery`, following that workspace's
operating boundary. HRMS deploy alone does not install this server bridge.

After explicit approval only, on the verified DCDB1 / 10.10.91.100 host:

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
