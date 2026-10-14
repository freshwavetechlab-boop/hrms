# Incoming email

Open **Security → Apps → Notifications → Channels**. Email settings include SMTP delivery and incoming IMAP. The old `/settings/notifications` and `/settings/ai-integration` routes redirect to Apps.

- Enable incoming mail; set `imap.hostinger.com`, port `993`, folder `INBOX`.
- The saved SMTP username/password are reused. Incoming mail requires TLS on connect, normally port 993.
- Save email settings, then use **Test saved IMAP connection**. This only opens the mailbox read-only.
- **Incoming mail** lists the latest 200 imported messages and offers manual sync, refresh and message preview.
- Background workers sync every minute in batches of up to 50 messages, independently of SMTP being disabled or paused. The first sync starts at the beginning of the folder; remaining history imports on subsequent syncs.

## Migration and startup

Run from the repository root against the configured database:

```powershell
dotnet run --project .\Payroll.API\Payroll.API.csproj -- --migrate-notification-imap
```

This adds IMAP settings/cursor columns to `notification_smtp_settings`, the dedicated `notification_incoming_mail` table, and a Message-ID lookup index on `notification_logs`. It preserves the SMTP credentials, queues, templates and rules. Full initialization also includes the same idempotent migration.

Deploy/restart the API with `BackgroundWorkers:Enabled=true` for automatic polling. When workers are disabled, manual sync still works.

## Processing and acknowledgements

The cursor is scoped to mailbox/account/folder and IMAP UID validity. A SHA-256 identity of the RFC Message-ID has a unique database constraint per mailbox. A distributed database lock serializes sync across API instances. Incoming insert, reply acknowledgement and cursor advancement commit together; failures do not skip the failed message. UID validity changes reset the cursor but previously recorded Message-IDs still prevent duplicate processing. Messages without a Message-ID use UID validity plus UID as the fallback identity.

Incoming `In-Reply-To` / `References` IDs are matched to an outgoing notification's actual RFC Message-ID and original recipient. Matches show **Reply received** in both incoming mail and notification logs. The sent queue remains Sent; receipt does not imply a business approval. Outgoing messages sent before RFC Message-ID logging was added cannot be automatically linked retroactively.

Mailbox Seen flags are unchanged. Attachments are not downloaded. Text bodies up to 1 MB are fetched, with previews capped at 100,000 characters; oversized messages retain envelope details. Previews render as plain text. No automatic acknowledgement email is sent.

## Payslip delivery reports

Incoming `message/delivery-status` reports update Excel payslip delivery details and email job counts. SMTP acceptance remains distinct from recipient delivery. DSN action `failed` marks Not delivered, `delayed` marks Delayed, and `delivered` marks Delivered. A failure action is authoritative even when its diagnostic contains a temporary error code. No receipt means recipient delivery is unconfirmed.

The returned original Message-ID and recipient identify a sent message. Legacy sends without a saved ID require a unique exact subject/sender/recipient match in the original send-time window. Chronology excludes completed sends before the original message existed. Ambiguous matches remain for review. Verified original IDs are backfilled onto their sent logs; delivery outcomes are appended, preserving the SMTP send history. New reports are processed during normal incoming sync. **Check payslip delivery reports** handles already imported/historical messages for the selected date range; processed reports are skipped. It does not resend mail or change salary/employee data.

Global admins can also call `POST /api/notifications/imap/reconcile-delivery?since=2026-10-12`; `&dryRun=true` checks matches without saving them.

Apps and incoming mail require a global user with `settings.manage`; moving the menu does not grant user/role administration permissions.
