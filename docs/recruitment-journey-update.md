# Recruitment journey update: manual setup

The code builds without starting the API. No migration or configuration update has been executed by the coding agent. Run the setup below yourself against the database/environment used by your backend.

## Schema reuse

Existing candidates, applications, interviews, process documents, signatures, attachments, tracking sessions, job links, templates and workflows are reused. No new table is created.

The targeted migration adds only missing columns:

| Existing table | New columns | Purpose |
| --- | --- | --- |
| `recruitment_interviews` | `DirectEmail`, `DirectName`, `DirectClientId` | Interview before a candidate/application/job exists. |
| `recruitment_candidate_applications` | `NegotiationOverride`, `AgreedCtc`, `TermsConfirmedAtUtc`, `TermsVersion` | Per-job terms and explicit ON/OFF override, preserving the candidate's global expected CTC. |
| `recruitment_process_documents` | `TermsVersion`, `BodySnapshot` | Bind the MoM to exact confirmed terms and preserve the content presented for signing. Existing `VersionNumber` remains the document revision number. |
| `recruitment_process_document_signatures` | `CandidateId` | Identify the authenticated candidate independently of an internal staff user. |

There are 10 additive columns. No table/column/data is dropped. The migration checks each column before adding it, so it can be rerun. Run it before using the updated API, because the new queries reference these fields.

From the repository root, using the same `http` launch profile previously used locally:

```powershell
dotnet run --project Payroll.API --launch-profile http -- --migrate-recruitment-journey
```

This command performs the targeted migration and exits without starting the HTTP server or hosted workers. The existing full `--migrate` command also includes these columns, but runs the other repository migrations too. Appsettings files have not been edited.

## UIDAI configuration using the existing designer

Create new draft revisions from the currently published UIDAI pipelines. Preserve stage codes, templates, ATS settings, required documents and rejection routes; publish the revisions using the existing designer so its version synchronization preserves prior history.

**Position pipeline `UIDAI_VACANCY_HIRING`:** keep the current stages and configure their successful route as:

`Order → Sharing profiles → Panel assessment → Negotiation / terms → Signing MoM → HR Division approval → Offer issuance → Joining`

Move `NEGOTIATION_AND_MOM_TO_HR` before `SIGNING_MOM` and update the successful transition destinations as well as display order. Keep the configured MoM template on `SIGNING_MOM`. Position auto-movement counts candidates; HR approval comes from each candidate's signed MoM. Keep position-level approval disabled unless a separate aggregate approval is intentionally required. Position terminal joining waits for actual employee conversion.

**Candidate pipeline `UIDAI_CANDIDATE_JOURNEYDN`:** retain the existing selection/HR stage code, rename its display label to `Negotiation, MoM & HR approval`, and select the existing `UIDAI_HR_APPROVAL` workflow in Advanced stage controls. Its approval is started when the candidate signs the MoM. The same approved workflow is reused for the transition into Offer; it is not requested twice.

Replace the successful `Offer → Joined/Hired` route with:

`Offer → Pre-boarding (type PreOnboarding) → Joining (type Joining) → Joined/Hired (type Completed, terminal)`

Keep one successful outgoing route per stage, with the existing reject/withdraw routes as needed. Keep accepted-offer-to-advance enabled and candidate response validity at 7 days. Preserve the job's configured budget basis and offer template. The existing mandatory document checklist is reused; documents may be uploaded while final approval is pending.

**Final signed offer:** in Offers & Pre-boarding → Final offer settings, enable post-acceptance signing for UIDAI and select its existing HR approver as the final signatory. The final PDF still depends on the configured signing assets/template. Check those assets in your environment before testing issuance.

Use the existing role editor: scheduling uses `recruitment.interview.schedule`; profile/resume access uses `recruitment.candidate.view`; negotiation uses `recruitment.proposal.manage` or `recruitment.offer.manage`; offer release uses `recruitment.offer.issue`; employee creation uses `employees.manage`. Existing administration/recruitment management permissions continue to apply. Panel membership and the saved final decision approver are checked separately.

## Checks after you start the backend

- An ATS-qualified candidate remains visible when round defaults are missing. The scheduling row/form accepts the missing round/type, panel, duration and decision approver. Feedback is required before the final decision.
- ATS scores at or above the configured cutoff qualify even when skill evidence is missing or unverified. Evidence remains available for review. Existing passing scores are rechecked by the existing automation worker after the updated API starts; no extra migration or rescore is required. Explicit manual-confirmation settings and interview-stage checks still apply.
- A direct email interview can be saved with no job and linked later to an application with the same email. A job-linked direct interview can advance an early candidate pipeline with an audited screening override.
- Negotiation uses the configured budget basis unless explicitly overridden ON/OFF for the application. Confirming selected-candidate terms prepares the candidate-specific MoM.
- The candidate opens the existing published job link, signs in using the existing APP reference/PIN, reviews the prepared MoM and signs it. HR receives a normal My Tasks approval. Revised or returned terms require confirmation and a fresh MoM signature.
- Offer acceptance leaves the candidate in pre-boarding. Final departmental approval, generated final offer, mandatory checklist completion and HR-confirmed employee creation precede Joined/Hired.
- Individual candidates continue even if a position lacks enough candidates. Position auto-movement holds; admins can explicitly move a position with a reason without manufacturing document signatures or candidate approvals.

Build/unit checks do not verify these database-backed scenarios. No candidate emails, signatures, approvals or sample hiring records were created during local verification.
