# Offer roster actions and JD drafts

## Permanent offer deletion

The same **Hard delete** confirmation is used in All offers (cards and table), Released letters, Selected for joining, and Employee lists & offer letters. Both the API and UI require an active, unrestricted `super_admin`; ordinary `settings.manage` and client-scoped or Selected-client accounts do not grant this action.

For an employee-list source, deletion removes the selected roster row, its source/corrected inputs, released PDF versions, and its JD link in one transaction. It disappears from offer/joining lists and subsequent corrected-version downloads. A minimal actor receipt and sent-mail history remain. Other batches, shared JD/template/location masters, candidate profiles, Employee Master and payroll are independent records and are retained. Pending emails are cancelled; an actively sending email must finish before deletion. A completed document collection stays completed; a sole open upload link expires. Pipeline offers keep their existing joined-application protection.

## Selection while searching

Select all, search for one employee, and deselect that row. Clearing the search restores the other selections (104 → 103). The selected count shows the full selection; release/download/email action counts still apply to visible eligible rows. Clear selection explicitly clears everyone.

## One-click JD preparation

Simple JDs prepare actual `Draft` Hiring Requests and native `Draft` JD versions, reused per client, normalized role title and work-location ID. Requests contain the generated responsibilities/skills and original actor reference. Unknown salary, qualifications and experience are not inferred. No request is approved, vacancy opened, job published or email sent by this operation. Existing approved-JD reuse remains available.

Preparing these drafts requires the existing hiring-request creation grant as well as employee-list editing access. The UI and API enforce the same combination; existing read access is retained.

Jobs shows these request/JD preparation rows as **Draft JD · Unpublished**, with **Open hiring request**. These rows have no public slug or application URL and cannot call posting publish/edit endpoints. A real posting replaces its preparation row. Existing edited or approved requests are never overwritten by repeated linking.

Normal database setup / AutoMigrate backfills existing Simple JDs idempotently. For a focused upgrade against an already initialized recruitment database:

```powershell
dotnet run --project "D:\FrevOHRMS\hrms\Payroll.API\Payroll.API.csproj" --no-launch-profile -- prod --migrate-recruitment-roster-jds
```

Deploy the updated API and frontend together. Run the upgrade with the updated binary/configuration; it exits before starting the host or workers. This change has not been applied to production data during implementation.

## Verification

Frontend utility tests cover global Superadmin gating, separate offer/roster IDs, 104-person search deselection and unpublished job projections. Isolated MySQL tests exercise scoped transactional deletion, rollback, queued-mail cancellation, completed-upload retention, per-client/location hiring/JD drafts, and idempotence after manual approval/edits. Local browser checks use the actual components with disposable in-memory API fixtures; they do not delete real employees or send messages.
