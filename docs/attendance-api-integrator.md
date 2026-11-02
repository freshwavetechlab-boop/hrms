# Attendance API Integrator

Open **Security > Integrations > Attendance API Integrator** (`/security/apps/attendance-api-integrator`). The page uses the existing global `settings.manage` permission, like the other integration pages. Select an active client in the persistent page header. Each adapter belongs to that client and one registered machine/work location.

## Configure a client

1. Register its machine in **Settings > Leave & Attendance > Attendance** and assign the work location.
2. Generate that machine's HRMS API key in **Workflows > API Catalog**.
3. Choose **Add adapter**, or **Use iDMS preset**. Set the vendor endpoint, source device ID, HTTP method and authentication.
4. Enter the vendor credential and HRMS machine API key. Both are encrypted in storage and are never returned by list/save APIs. Blank password inputs retain saved values; explicit remove checkboxes clear them. A different HRMS machine requires a matching key.
5. Configure request/query/header templates, the response array path and punch field paths. Optional employee mapping translates vendor user IDs into HRMS employee codes. Unlisted IDs are used directly as employee codes, subject to the existing client/location checks.
6. Save while paused, run **Test**, and verify the mapped employee, time and IN/OUT values. Testing makes read requests to the provider and shows up to 20 mapped punches; it never invokes attendance import.
7. Enable the adapter and use **Sync**, or configure an interval of 5–1440 minutes. Interval 0 means manual only. Recent days are re-fetched using the configured timezone and lookback of 1–7 calendar days. Use a manual range for older delayed logs.

The iDMS preset fills `POST /TimeWatchAPI/GetPunchData`, `X-Api-Key`, request keys and response field names. Its documented LAN address is an example/configuration starting point. It does not include a credential or assume that numeric `InOutMode` 0 means IN. Confirm numeric IN/OUT meanings with the vendor before enabling.

## Request configuration

Supported contracts are HTTP GET/POST with JSON responses, static API-key headers, bearer tokens, Basic authentication, or no automatic authentication. For query/body authentication, select no automatic authentication and use `{{secret}}` in the desired template value. Keep credentials in the password field, rather than entering them directly into templates or the endpoint URL.

| Placeholder | Value |
| --- | --- |
| `{{fromDate}}` | Start date using the request date format |
| `{{toDate}}` | Inclusive end of the end date using the request date format (23:59:59 when time is included) |
| `{{toDateExclusive}}` | Start of the following date, for APIs with exclusive upper bounds |
| `{{deviceId}}` | Vendor's source device ID |
| `{{page}}` | Page number, starting at configured 0 or 1 |
| `{{offset}}` | Zero-based record offset |
| `{{pageSize}}` | Configured page size |
| `{{secret}}` | Decrypted vendor credential, used only while making the request |

JSON templates must be objects. Placeholders belong inside JSON strings; an entire `{{page}}`, `{{offset}}` or `{{pageSize}}` value becomes a JSON number. Other text remains safely JSON-escaped. GET uses query parameters and has no body. The request content type for POST is `application/json`. Dates/time formats follow .NET notation, such as `yyyy-MM-dd`.

Response paths are case-sensitive dot paths, e.g. `data.items`, `employee.code`, or `logs.0.value`. `$` means the entire response. A configured success field must equal the expected value before records are processed; clear that path for providers without an application success flag.

Page/offset pagination stops at a short or empty page. Put the corresponding placeholder and page-size value in the vendor's expected query/body parameters. A repeated page, reaching the configured maximum pages, exceeding 10,000 records, an invalid response, or mapping errors prevents import of the fetched set. Each response is limited to 8 MB. A manual date range covers at most 31 calendar days.

## Attendance behavior

The connector uses the existing machine punch importer, including active device credentials, client/work-location checks, dated employment eligibility, shifts, protected attendance/leave and payroll locks. Results distinguish accepted, duplicate and rejected punches. Imports can partially succeed when individual attendance rules reject records; the result and limited error list report this.

Use the vendor's stable unique log ID when available. If absent, the connector hashes source device ID, vendor user ID, timestamp and direction. Identical records under those four fields are treated as the same punch. Retries reuse the same identifier. Provider insert/sync time is not used as the punch timestamp.

Explicit timestamp offsets take precedence. Timestamps without offsets use the configured timezone; ambiguous/invalid daylight-saving times require an explicit offset. Records from another source device or outside the requested dates are skipped. Unknown direction values are rejected rather than guessed.

Manual/scheduled runs are serialized with a MySQL advisory lock across API instances. Editing/deleting an adapter during a run is rejected. A background worker checks due schedules every minute when `BackgroundWorkers:Enabled` is enabled. Failed syncs retain the last successful timestamp and wait the configured interval before retrying. Operational summaries are saved; preview employee data is not retained in the adapter settings.

## Storage and deployment

Configuration uses `modulesettings.SettingsJson.attendanceApiAdapters` in the existing `leave_attendance` client row. It does not replace attendance rules, registered devices or other module settings, and needs no schema migration. Credentials use the existing portable integration protector with a separate authenticated purpose.

Requests originate from the **HRMS API server**. A LAN endpoint therefore needs a reachable network/VPN from that server and the explicit private-network option. Redirects, loopback, link-local/metadata addresses and unsafe request headers are blocked. DNS results are checked for each request and connections are pinned to validated addresses. Provider response bodies and credentials are not included in HTTP request logs or connectivity errors.

## Validation

Backend protocol tests cover iDMS mappings, explicit IN/OUT values, timezone handling, stable retry IDs, device/date filtering, nested JSON, template escaping, numeric pagination, permissions and network policy. `tools/test-attendance-api-integrator-ui.mjs` uses synthetic API responses for route/header placement, overflow, cards/table persistence, XLSX export, configuration validation, test/sync/error states, client switching and desktop/tablet/mobile layouts. The live iDMS endpoint requires client network access for end-to-end validation.
