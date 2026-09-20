# Interview, vacancy progress and MoM corrections

Local code changes only; this change does not edit or deploy production records.

- Internal candidate stages no longer show the generic candidate action-link button. Existing external form/document/offer links remain available; Frevo One video invites are separate.
- The interview queue now uses the current configured interview-stage instance. Selection & HR Review and completed rounds do not return to the queue. A genuinely new configured interview round remains eligible.
- Schedule/update interview has a **Final decision approver**. Assigned users with interview access receive the existing bell decision action after required panel feedback. Others cannot decide; the exact global super-admin retains its override. Legacy unassigned interviews retain HR/scheduler permissions. Panel recommendations and final decisions stay distinct.
- Vacancy automation requires the active demand (`NumberOfPositions - CancelledPositions - OnHoldPositions`), not just one successful applicant. Latest rounds count once. Rejection, no-show and offer rejection reopen sourcing when necessary; candidate decisions, documents, signatures and the original SLA anchor remain intact. Manual moves remain available.
- After sourcing rework, the old signed MoM remains historical evidence. A new MoM version and signatures are required for the replacement cohort; earlier signatures cannot satisfy its document gate.
- Job/pipeline cards expose required, in-panel and selected counts beside candidate totals.
- **Interviews & Offers > MoM & Negotiation** reuses Work Orders signing and the existing offer/negotiation workspace. Open the job, prepare the configured MoM, collect required signatures and finalize it; ordinary document/approval gates still apply.

## Deployment

The only new stored field is nullable `recruitment_interviews.DecisionApproverUserId`.
The normal project migrator includes it. Alternatively, run the narrowly scoped,
repeatable `deploy/recruitment-interview-decision-approver.sql` on the intended DB
**before** starting the updated API, then deploy API and UI together. No new service,
secret, role or lookup master is needed. Do not run a migration against an assumed
local database: verify the configured target first.

Existing jobs are reconciled on their next relevant recruitment action, not by a
bulk production rewrite or a read-only page load. A job already manually moved
ahead is therefore not claimed repaired merely because the new UI was deployed.

Tests use a GUID-named disposable loopback database. Real email, production records
and live video are outside this regression run.

## Verification (2026-09-20)

- Backend build and 160 recruitment tests passed, including real disposable-DB queue,
  counter, sourcing-rework, historical-signature and workflow checks.
- 38 focused UI/service regression checks passed.
- Fresh isolated `npm ci --ignore-scripts` and production UI build passed against
  the unchanged lockfile (Ant Design 5.5.0). This was a Windows host build, not a
  Linux container build or production deployment.
- Existing warnings remain: MailKit advisory NU1902, two nullable warnings,
  React/qrcode peer compatibility and bundle-size warning. No dependency upgrade
  was folded into this recruitment fix.
- Playwright/live role-login testing and production migration were not run.
