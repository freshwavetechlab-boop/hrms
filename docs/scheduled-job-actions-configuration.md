# Scheduled Jobs and Job Actions Configuration Guide

This module has two parts:

- **Job Action**: what the system should do.
- **Schedule**: when the system should do it.

Example:

- Job Action: `Monthly leave reminder`
- Schedule: `Run every month on day 1 at 10:00`

Go to:

`Settings > Scheduled Jobs`

You will see three tabs:

- **Job Actions**
- **Schedules**
- **Run Logs**

## 1. Job Actions

Create Job Actions first. These are reusable activities that can later be scheduled.

Click:

`Job Actions > Add action`

Common fields:

| Field | Meaning | Example |
|---|---|---|
| Action code | Unique code for the action | `MONTHLY_LEAVE_REMINDER` |
| Action name | User-friendly name | `Monthly leave reminder` |
| Description | Short purpose | `Sends leave reminder to employees` |
| Action type | What kind of action this is | `Notification Event` |
| Active | Whether this action can be scheduled | `Yes` |

## 2. Action Type: Notification Event

Use this when the schedule should trigger an email or notification rule.

This action does not directly send mail. It publishes an event. The actual email recipients/template are configured under:

`Settings > Notifications > Rules`

### Example: Monthly Leave Reminder

Job Action:

| Field | Value |
|---|---|
| Action code | `MONTHLY_LEAVE_REMINDER` |
| Action name | `Monthly leave reminder` |
| Action type | `Notification Event` |
| Event code | `MONTHLY_LEAVE_REMINDER` |
| Record type | `ScheduledJob` |
| Record reference | `LEAVE_REMINDER` |
| Client ID | client id, or blank |
| Payload JSON | `{}` |

Then create a notification rule:

| Field | Value |
|---|---|
| Event/activity | `MONTHLY_LEAVE_REMINDER` |
| Template | Leave reminder email template |
| Recipient | Static email, role, requester, manager, or lookup |

## 3. Action Type: Internal API Call

Use this when a scheduled job should call an existing API.

Example use cases:

- refresh report data
- call an internal utility endpoint
- trigger an existing system operation

### Example: Call an Internal API

Job Action:

| Field | Value |
|---|---|
| Action code | `REFRESH_DASHBOARD_DATA` |
| Action name | `Refresh dashboard data` |
| Action type | `Internal API Call` |
| Method | `POST` |
| API URL | `/api/dashboard/refresh` |
| Timeout seconds | `60` |
| Body JSON | `{}` |
| Headers JSON | `{}` |

Notes:

- Relative URLs like `/api/...` call the same API server.
- Full URLs can also be used, but should be controlled later with guardrails.
- Request/response result is visible in **Run Logs**.

## 4. Action Type: Stored Procedure

Use this when an approved database stored procedure should run on schedule.

Safety rule:

- Procedure name must start with `job_`.

This prevents accidentally running normal business procedures or unsafe SQL.

### Example: Month-End Reconciliation

Stored procedure name:

```sql
job_month_end_reconcile
```

Job Action:

| Field | Value |
|---|---|
| Action code | `MONTH_END_RECONCILE` |
| Action name | `Month-end reconciliation` |
| Action type | `Stored Procedure` |
| Procedure name | `job_month_end_reconcile` |
| Parameters JSON | `{ "month": "2026-07", "clientId": "1" }` |

## 5. Action Type: Report Email

Use this when the system should generate a report and send a notification event with report summary.

The report is generated from the existing Reports engine. The email is sent through Notification Rules.

### Example: Monthly PF Report Email

Job Action:

| Field | Value |
|---|---|
| Action code | `MONTHLY_PF_REPORT_EMAIL` |
| Action name | `Monthly PF report email` |
| Action type | `Report Email` |
| Report code | `pf-report` |
| Notification event | `MONTHLY_PF_REPORT_READY` |
| Client ID | `1` |
| Month | `2026-07` |
| Pay run ID | optional |
| Preview rows | `10` |

Then create Notification Rule:

| Field | Value |
|---|---|
| Event/activity | `MONTHLY_PF_REPORT_READY` |
| Template | PF report email template |
| Recipient | Payroll manager / statutory team |

The notification payload includes:

- report code
- report title
- row count
- columns
- preview rows

## 6. Action Type: Workflow Trigger

Use this when the scheduler should start workflow approval for configured records.

Example use cases:

- start approval for selected payruns
- trigger review workflow for pending records
- start monthly compliance approval

### Example: Trigger Workflow for PayRun

Job Action:

| Field | Value |
|---|---|
| Action code | `PAYRUN_APPROVAL_TRIGGER` |
| Action name | `Payrun approval trigger` |
| Action type | `Workflow Trigger` |
| Workflow ID | `1` |
| Resource type | `PayRun` |
| Resource IDs | `26,27` |
| Requestor user ID | `1` |
| Skip if pending | `Yes` |
| Payload JSON | `{}` |

Notes:

- Resource IDs are comma-separated.
- If `Skip if pending` is enabled, duplicate pending workflow instances are avoided.
- The approver comes from workflow configuration.

## 7. Schedules

After creating a Job Action, create a schedule.

Go to:

`Schedules > Add schedule`

Fields:

| Field | Meaning | Example |
|---|---|---|
| Schedule code | Unique schedule code | `DAILY_LEAVE_REMINDER` |
| Schedule name | User-friendly name | `Daily leave reminder` |
| Job Action | What should run | `Monthly leave reminder` |
| Enabled | Whether scheduler should execute it | `Yes` |
| Run frequency | Interval, Daily, Monthly | `Daily` |
| Repeat every | Used for interval jobs | `10 minutes` |
| Run time | Used for daily/monthly jobs | `10:00` |
| Day of month | Used for monthly jobs | `1` |

### Example Schedule: Daily Reminder

| Field | Value |
|---|---|
| Schedule code | `DAILY_PENDING_APPROVAL_REMINDER` |
| Schedule name | `Daily pending approval reminder` |
| Job Action | `Pending approval reminder` |
| Run frequency | `Daily` |
| Run time | `10:00` |
| Enabled | `Yes` |

### Example Schedule: Monthly Leave Credit

| Field | Value |
|---|---|
| Schedule code | `MONTHLY_LEAVE_CREDIT` |
| Schedule name | `Monthly leave credit` |
| Job Action | leave credit action |
| Run frequency | `Monthly` |
| Day of month | `1` |
| Run time | `01:00` |
| Enabled | `Yes` |

## 8. Run Logs

Go to:

`Run Logs`

This shows:

- start time
- completed time
- status
- success count
- failure count
- message
- triggered by
- duration

Use logs to confirm whether a job passed or failed.

## 9. Recommended Configuration Flow

1. Create Notification Template if email is needed.
2. Create Notification Rule if event-based email is needed.
3. Create Job Action.
4. Create Schedule for that Job Action.
5. Click **Run now** once for testing.
6. Check **Run Logs**.
7. If successful, keep schedule enabled.

## 10. Important Notes

- Do not use Stored Procedure action for arbitrary SQL.
- Use `job_` prefix for approved scheduled procedures.
- Use Notification Event or Report Email for reminder/reporting use cases.
- Use Workflow Trigger only when workflow configuration is already tested.
- Use Internal API Call only for known internal APIs.
