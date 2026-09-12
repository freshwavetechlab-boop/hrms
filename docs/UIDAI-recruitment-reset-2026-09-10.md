# UIDAI recruitment reset — 10 September 2026

## Scope and current result

The user authorized resetting UIDAI (client ID 20) recruitment transaction data in the production database through the local administrator UI. Pipelines, configuration, the client, users, employees/payroll and other clients were excluded.

Deleted using actual Playwright browser clicks and the existing administrator DELETE endpoints:

| Records | Removed | Remaining for UIDAI |
| --- | ---: | ---: |
| Work orders | 6, including 12 role lines | 0 |
| Hiring journeys | All original journeys and automatically recreated journeys on those same old role lines | 0 |
| Job description versions | 6 | 0 |
| Open positions | 9 | 0 |
| Applications, including their ATS evidence | 8 | 0 |
| Client-owned candidate profiles | 6 | 0 |
| Interviews | 6 | 0 |
| Hiring requests | 11 | 0 |
| Process document records | 8 | 0 |
| Profile forwarding batches | 1 | 0 |
| Work-order source PDFs | 7 soft-deleted through the attachment service | 0 active attachments |

The final request, ID 9 (RFR-UIDAI-2026-00007, Auth Fraud Supervisor), was deleted after the user confirmed completing the cleanup. The updated API removed its workflow transactions in the same transaction. Live Playwright verification confirmed zero UIDAI requisitions, no requester-progress entry for workflow instance 35, and no corresponding pending approval task.

## Preserved

- UIDAI client and setup/configuration masters; no schema or migration changes.
- Pipeline definition 5; published version 6 (v1) and draft version 7 (v2, including Rejected). Full definition/version API snapshots compared unchanged after each successful phase.
- Global candidate profiles 21 and 22 and their resumes (3 and 1 respectively). Only their UIDAI applications were removed.
- Other-client recruitment record IDs checked unchanged. No employee/payroll deletion endpoints were invoked.
- Existing deletion audit entries; attachment files remain soft-deleted under the existing storage policy.

## Small admin UI fixes

- Hiring Requests exposes **Manage vacancies** when approval workflow tabs are disabled, reusing the existing global vacancy table and delete controls.
- Administrators can access saved JD version history for pending/rejected requests; preparation stays read-only in those request states.
- Client-owned candidate profiles, previously hidden behind the global-only resume bank, are accessible through the existing profile drawer and its interview/application/candidate delete controls.

## Backend fix deployed

`RecruitmentRepository.DeleteRequisitionAsAdminAsync` now calls the shared `WorkflowRepository.DeleteResourceTransactionsAsync` inside its transaction. It removes only workflow transaction rows for the exact `RecruitmentRequisition` resource being deleted, preserving workflow masters/stages and the recruitment deletion audit.

After the user confirmed deleting the final pending request, the local production API was restarted with the updated build on port 5062 and automatic migration explicitly disabled. The normal `Payroll.API/bin/Release/net8.0` build was also updated and its DLL hash verified identical to the tested staged build. The reset is complete; no pending request was left behind and no data was restored.

No migration is required. Do not use `--no-build` with an old Release output and assume this fix is loaded.

## Verification and artifacts

- UI production build and staged .NET Release build passed. Existing MailKit/nullable/bundle-size warnings remain unrelated to this cleanup.
- Successful live browser phases: orders (`uidai-reset-orders-20260910-e`), talent (`uidai-reset-talent-20260910-b`), catalog (`uidai-reset-catalog-20260910-b`), requests (`uidai-reset-requests-20260910-a`), final read-only verification (`uidai-reset-verify-20260910-a`). Reports are under `playwright-e2e/artifacts/`, with matching `-report` folders.
- Final pending-request deletion and workflow/pipeline preservation checks passed in `uidai-reset-pending-final-20260910`; matching `-report` folder contains the live browser report and screenshot of the empty request register.
- Test: `playwright-e2e/tests/recruitment/uidai-authorized-reset-live.spec.ts`. It requires an explicit dated confirmation, fixes the old target IDs, rejects new work orders/requests, scopes automatically recreated journeys to the verified old role lines, and blocks every browser write except the currently confirmed DELETE and authentication. It never mocks successful responses.
- Earlier failed runs made some successful deletions before stopping; their traces and scoped pre-delete snapshots were retained. Runs resume only against still-existing authorized targets.
- Final screenshots show zero Work Orders/hiring journeys/candidate applications and the preserved global resume bank.

## Recovery limits

Scoped API snapshots are attached to the Playwright reports. **They are not a full, restorable database backup.** Recruitment transaction rows were permanently deleted by the existing APIs; full restoration would require an appropriate database backup. The seven source PDF records were soft-deleted and their stored files were not physically purged.
