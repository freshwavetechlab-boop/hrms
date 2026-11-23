# Hiring request and offer document capacity

These changes are prepared in source. The production database migration and application deployment are left to the owner. No letters are released or emailed by these migrations.

## Qualification capacity

The normal project migrator includes the qualification upgrades. For an already initialized database, the narrow command is:

```powershell
dotnet run --project Payroll.API/Payroll.API.csproj --no-launch-profile -- prod --migrate-recruitment-text-capacity
```

`prod` selects the project's Production configuration. Check that its `ConnectionStrings:Default` names the intended database before running. This entry point exits before starting the web host and background workers.

Only `recruitment_requisitions.Qualification` and `recruitment_jd_qualification_requirements.QualificationName` are widened to `TEXT`. Existing values, character encoding, collation and nullability are preserved. The request column retains its literal default and comment. Already wider text types are not narrowed. Repeated runs are safe. MySQL DDL commits independently of later data migrations.

The browser receives the request-field limits from the API. The JD editor checks Unicode characters for bounded fields and UTF-8 bytes for TEXT fields. Its counters, inline messages and save guards use the supported capacity; pasted and parsed text is retained for correction. A qualification may contain up to 65,535 UTF-8 bytes per entry. This replaces the old 180-character JD and 250-character request limits.

## Offer layout validation

Opening the release popup checks the selected profiles before enabling **Release & download**. The check renders current details with the selected signature choice in an enforced read-only database transaction. It does not allocate an offer number, store a PDF, alter employee data, create a release/audit receipt or queue email.

Errors identify the profile, template, field and page. The action stays disabled while checking, after a failed check, or when selected row revisions/signing choices change. Final release checks the details again and preserves the existing all-or-nothing transaction. A single selection still downloads a PDF; multiple selections download a ZIP.

The reported failure was an endorsement paragraph whose long institution/site name could not fit a positioned single-line box. The shared converter recognizes a supported legacy document structure independently of client names, IDs or profile codes. Its flexible version uses flowing paragraphs at the original font size, retaining the contractual wording and signature area. Custom, multi-column or rich-text formats that cannot be converted safely are skipped for review.

Existing issued PDFs and their original template remain unchanged. To apply the two qualification capacity upgrades and shared document-layout migration together, run:

```powershell
dotnet run --project Payroll.API/Payroll.API.csproj --no-launch-profile -- prod --migrate-recruitment-document-layouts
```

This explicit migration discovers active, compatible Offer Letter templates across the configured clients. Each gets a neutral **Offer letter - flexible text v2** version, including for future imports when no current profiles need rebinding. Client-owned templates keep their configured client; global templates remain global. No client names, client/template/profile IDs or source-document hashes are fixed in the migration code.

Only active candidates' never-issued rows move: they must have an empty offer number, no release receipt at all, correct client/template ownership, and the current source-template hash. Existing issued rows retain their original binding and PDF bytes. Stale/custom bindings are skipped. Source IDs and body-hash prefixes distinguish configured versions; a later source edit gets its own version. Conflicting generated versions stop the transaction without overwriting configuration.

A repeated run reuses verified versions without duplicate creation/audits, and can pick up newly added eligible rows. Template creation and row rebinding are audited and atomic. Capacity DDL may have completed before a layout guard refuses the configuration step; rerunning the capacity upgrade is safe. This correction runs only through its explicit migration command, not on page reads, ordinary saves or automatic startup. Unsupported migration flags fail before starting the web host.

For future employee lists, choose the flexible-text template version. **Update Corrected Version** continues to use template and batch: original issued profiles download under their original version, and new corrected profiles under the flexible version. Profile codes, salary data and matching rules are retained.

Deploy the matching API and UI changes together so the preflight endpoint, counters and capacity rules are available together.

## Verification

Five disposable-database/CLI tests passed with zero skips, covering unrelated clients and a global template, issued-letter preservation, ownership boundaries, idempotence and newly added rows, custom-layout exclusion and transaction rollback. The combined generic CLI ran twice successfully and widened both qualification columns; an unsupported migration flag exited before starting the host or changing the database. The actual three reported profiles, synthetic signing and a 250-character institute name also rendered as eight-page documents. Visual inspection confirmed full wording and clear signature areas. Production execution is left to the owner.
