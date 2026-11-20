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
| `recruitment_process_document_signatures` | `CandidateId` | Legacy candidate signature identity; retained for history. New MoM signatures use existing `SignerUserId` for panel members. |

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

Move `NEGOTIATION_AND_MOM_TO_HR` before `SIGNING_MOM` and update the successful transition destinations as well as display order. Keep the configured MoM template on `SIGNING_MOM`. Position auto-movement counts candidates; HR approval comes from one combined, panel-signed job MoM covering the selected candidates and their confirmed terms. The job HR workflow mapping is preferred; existing candidate HR mappings are reused when they identify one workflow. The matching approval is reused for position and candidate transitions. Position terminal joining waits for actual employee conversion.

**Candidate pipeline `UIDAI_CANDIDATE_JOURNEYDN`:** retain the existing selection/HR stage code, rename its display label to `Negotiation, MoM & HR approval`, and select the existing `UIDAI_HR_APPROVAL` workflow in Advanced stage controls. Its approval is started after every assigned panel member signs the MoM. The same approved workflow is reused for the transition into Offer; it is not requested twice.

Replace the successful `Offer → Joined/Hired` route with:

`Offer → Pre-boarding (type PreOnboarding) → Joining (type Joining) → Joined/Hired (type Completed, terminal)`

Keep one successful outgoing route per stage, with the existing reject/withdraw routes as needed. Keep accepted-offer-to-advance enabled and candidate response validity at 7 days. Preserve the job's configured budget basis and offer template. The existing mandatory document checklist is reused; documents may be uploaded while final approval is pending.

**Final signed offer:** in Offers & Pre-boarding → Final offer settings, enable post-acceptance signing for UIDAI and select its existing HR approver as the final signatory. The final PDF still depends on the configured signing assets/template. Check those assets in your environment before testing issuance.

Use the existing role editor: scheduling uses `recruitment.interview.schedule`; profile/resume access uses `recruitment.candidate.view`; negotiation uses `recruitment.proposal.manage` or `recruitment.offer.manage`; offer release uses `recruitment.offer.issue`; employee creation uses `employees.manage`. Existing administration/recruitment management permissions continue to apply. Panel membership and the saved final decision approver are checked separately.

## Checks after you start the backend

- An ATS-qualified candidate remains visible when round defaults are missing. The scheduling row/form accepts the missing round/type, panel, duration and decision approver. Feedback is required before the final decision.
- ATS scores at or above the configured cutoff qualify even when skill evidence is missing or unverified. Evidence remains available for review. Existing passing scores are rechecked by the existing automation worker after the updated API starts; no extra migration or rescore is required. Explicit manual-confirmation settings and interview-stage checks still apply.
- A direct email interview can be saved with no job and linked later to an application with the same email. A job-linked direct interview can advance an early candidate pipeline with an audited screening override.
- Negotiation uses the configured budget basis unless explicitly overridden ON/OFF for the application. Confirming terms saves that candidate's agreed amount only. HR then prepares one job MoM from the MoM submenu. It automatically includes the same job/client's candidates with a completed, selected latest interview and confirmed terms. A separate profile-forwarding batch is optional and is no longer a MoM prerequisite.
- MoM uses the assigned custom template, or the built-in standard format when the stage has no template assigned. Existing secure document storage is reused. **Prepare MoM** creates and generates the document in one action; an existing unprepared draft uses the same button. The Next step panel identifies missing terms/feedback, pending panel signers, and HR approval, with actions permitted by the current user's RBAC. No additional schema migration is required for these defaults. Publish the updated API and UI together before using the new readiness endpoint.
- Assigned panel members open Interview Tracker → Job MoM, review the prepared document and sign using their own login (typed, drawn or uploaded signature). All assigned members must sign before HR receives the normal My Tasks approval. Candidate portal signing is retired. The same job document is shown from each included candidate's interview. Revised terms or a returned MoM need a new job document version and fresh panel signatures. Candidate IDs, terms versions, interview IDs and panel membership are frozen in the existing recruitment audit JSON; old candidate-specific documents remain in storage as history. Existing schema is reused; this correction adds no migration.
- MoM & Negotiation has sidebar submenus for stage documents and negotiation/approvals. The negotiation popup offers rejection through configured pipeline transitions with a mandatory reason. Admin manual stage movement remains available and explicitly warns about pending documents.
- Offer acceptance leaves the candidate in pre-boarding. Final departmental approval, generated final offer, mandatory checklist completion and HR-confirmed employee creation precede Joined/Hired.
- Individual candidates continue even if a position lacks enough candidates. Position auto-movement holds; admins can explicitly move a position with a reason without manufacturing document signatures or candidate approvals.

Build/unit checks do not verify these database-backed scenarios. No candidate emails, signatures, approvals or sample hiring records were created during local verification.

## Bulk offer acceptance and Employee Master import

Offers & Pre-onboarding retains **All offers** and adds **Released letters** plus **Selected for joining**. Select the client, select/deselect released letters, and use **Mark selected accepted** once. Acceptance is recorded by HR against the current offer/release; it does not fabricate a candidate signature. Normal offers and document-roster releases use separate source identities, so coincident numeric IDs never select the wrong letter. The accepted list is read from persisted status/audit records and remains available after refresh.

New document-roster releases produce the released letter without creating or linking Employee Master. Existing employee links remain intact. For the short bulk route, select released letters and choose **Import selected into Employee Master**. The same client/user-scoped selection opens **Employees → Import selected from Talent Acquisition**. One **Import selected** action imports the current released letters; a separate acceptance screen, mandatory preboarding checklist, salary-template selection and per-candidate input form are not required by this explicit intake route. Missing optional profile information stays missing for ESS completion. This action does not invent candidate acceptance or signatures. The existing accepted/joining route and individual conversion controls remain available.

Previously imported rows show their employee code and are reused on retries. Current table filters limit submission to visible selected rows. Client permissions, source identity and latest-release ownership still apply; a conflicting row has its own result while successful imports remain saved. New employees from this route have Portal access enabled. The import result's **Create ESS logins** action carries the resulting employee IDs directly into **Security → Users → Import Employees**.

The Users modal applies one chosen ESS/MSS role set and one temporary-password pattern to all selected employees. Employee self-service remains the baseline. The default **Shared** pattern uses the entered password or generates one batch password when left blank. Employee Code, Email and Mobile patterns use the corresponding Master text; a missing optional source uses the batch fallback instead of stopping account creation. The existing Client policy option remains available. Login ID stays the employee code, created credentials can be downloaded from the result, and existing account passwords are not reset. First-login password change leads employee-linked users directly to **My Profile**. See [Employee profile completion](employee-profile-completion.md) for the one-save and HR-approved re-edit behavior.

The bulk actions do not call offer-letter email release/resend and do not change existing payroll, salary templates or source workbooks. Configured pipeline automations retain their existing behavior. Existing single-candidate offer/conversion actions remain available. No new database table is needed; roster acceptance uses the existing release/audit tables and explicit import reuses the existing employee-link routine.

Verification (20 November 2026): 112 focused backend tests and 19 frontend behavior tests passed; Payroll UI and ESS UI production builds passed. The two isolated joining database scenarios were skipped because the dedicated test database was not configured. Browser verification used the real components with a synthetic in-memory API: 13 selected released offers imported in one request without an acceptance write, the same 13 employees reached Users preselected, one selected ESS/MSS role and shared temporary password applied to all 13, and the downloaded XLSX contained 13 unique login IDs and first-login instructions. Client switching discarded the prior scope, and controls remained reachable at desktop, 390px mobile and 769px tablet widths. No production employee, password or email was changed by these browser checks.
