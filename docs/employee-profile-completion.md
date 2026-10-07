# Employee profile completion

Employee Master keeps Table view as the default. In Card view, Missing information starts collapsed with its count visible. Its expanded badges open the relevant employee infotype.

## Reminders and settings

- Use the card's **Send link to fill missing details** action for one employee, or **Select all** and the header's bulk action for all eligible matching employees, including other list pages. Complete profiles, inactive employees, missing/invalid work emails and disabled ESS access cannot receive a reminder through this action.
- Preview shows recipients and a sample email with resolved employee data. Sending uses the existing Employee Communication queue and an idempotency key. Delivery status remains in Employee Communication.
- **Manage**, beside Card/Table view, controls the selected client's first-time edit setting and displays the reusable mail template. An unscoped administrator selects a client inside Manage.
- The client-specific template `EMPLOYEE_MISSING_INFORMATION` is saved when Manage or the first reminder preview is opened. Existing versions are reused without overwriting edits. A disabled template blocks sending. It also appears in the existing Mail templates settings.
- The saved `essUrl` placeholder defaults to **https://gad-ess.frevo.co.in/**. Employee name/code, client and sender placeholders resolve through the existing communication service. Employees use their existing ESS credentials.

## ESS editing and approvals

Employee Master **Manage → Configure fields** shows a field list for the selected client. Choose an Info Type, then **Add field**, **Edit** or **Delete**. **Save** makes changes available immediately; there are no manual draft/publish steps. Core fields stay unchanged. Existing form storage and employee submissions retain history internally. Saved field codes and types remain stable when labels are renamed; deleting a field hides it from forms/templates while retaining earlier values.

Configured data fields appear in the matching Employee Master tab, employee list/report columns, bulk mapping and generated templates. For a new employee, save the core record first, then complete additional fields from its Info Type tabs. Employee Master's Excel export produces the importable **Employees** worksheet with core fields, salary component JSON and configured values. Keep the `[CUSTOM:InfoType:FormCode:FieldCode]` header markers. Use **Add new + update existing** for mixed uploads; leave Employee ID blank for new employees. Blank update cells preserve existing values. Document files continue through the existing Documents upload.

Required configured fields join missing-information counts and the ESS profile form. ESS saves them within the same transaction as the profile update; failed validation does not consume edit access. TA joining uses the same employee identity checks, requires explicit confirmation to link an existing employee and fills only blank mapped values. Candidate data transfers by compatible stable field codes, never by labels; unavailable required values remain missing information. Conflicting candidate mappings stop the transfer for correction.

1. First-time profile saving defaults ON for each client. An employee with no previous successful ESS profile save can upload documents and update their profile once. Upload documents before choosing **Save profile and lock editing**.
2. A successful save locks profile editing and employee document uploads/deletions. Failed saves do not consume access. Existing document preview/download permissions still apply. Employee Master changes by authorised administrators remain available.
3. A locked employee enters a reason and selects **Request edit access**. Duplicate pending requests are rejected.
4. The assigned approver sees the request in **My Tasks** and the notification bell. The existing client-specific `EmployeeProfileEdit` workflow takes priority over a global workflow.
5. If no workflow exists, a client workflow uses the **Employee Administrator** approver: an active user with `employees.manage` in that client, with a global user holding that permission as fallback. The requester is excluded from this fallback. No available approver or a disabled configured workflow produces an actionable error.
6. Final approval permits one more successful save. The profile locks again afterwards; the employee can request another edit. ESS refreshes a pending request on window focus/every 30 seconds and offers a manual refresh button.

The first-time toggle applies only to employees who have never saved their ESS profile. Turning it back ON does not reopen profiles that have already saved. An explicit unused approval still grants its one save. Employment/salary fields maintained by HR remain HR-managed.

## Roles and client scope

| Action | Existing permission / scope |
| --- | --- |
| View completion status | `employees.view` or `employees.manage`; permitted client only |
| Preview/send reminders | Above plus `employee.communication.send`; every selected employee must belong to the requested client |
| Manage first-time access / initialise fixed template | `employees.manage`; permitted client only |
| Edit the mail template | `settings.manage`; existing mail-template endpoint and client checks |
| Edit/request own ESS profile | `ess.self` and the logged-in user's linked employee |
| Approve/reject a request | Existing workflow task assignment; other users cannot action that task |

Custom roles use these existing permissions; no new roles or permissions are required. Configure an alternative approver under Workflow Setup > Employees > Employee profile edit access. The feature does not use the generic workflow-start endpoint.

## Storage and verification

No schema migration is introduced. This reuses form definitions/versions, employee form bindings/submissions, `communication_templates`, template variables/campaigns, workflow tables, `ResourceStates`, `ess_profile_update_audit`, employee infotypes and attachments. `EmployeeProfileFirstEdit` resource state stores the client policy; `EmployeeProfileEdit` stores Pending/Approved/Consumed access. Profile saves, grant consumption and infotype updates share a transaction. Profile/document writes and approvals coordinate on the employee row.

Build the API, payroll-ui and ess-mss, then restart/deploy the updated API and both UIs together. There is no need to run a migration for this feature. Existing employees with an ESS save audit are treated as having used their initial save.

Automated checks cover initial/approved/consumed access, missing bank/document data, API permission and cross-client denials, and browser interactions with mocked APIs. Browser checks do not send real email or mutate production employee records. Actual mail delivery and database workflow execution require a deployment smoke check with an authorised test employee.
