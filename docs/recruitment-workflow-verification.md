# Recruitment workflow requirement trace

Scope: the Software Architect / Cursor Shree report, Chief Architect duplicates,
and published-pipeline revision propagation. The separate internal-interview goal
remains paused. No code deployment or live email delivery is implied. The narrowly
authorized production schema/configuration changes are recorded below; they are
not completion of the full production rollout.

| Requirement | Implementation boundary | Verification coverage / remaining |
| --- | --- | --- |
| Salary range versus approved budget; selected budget approver | Requisition budget workflow; offer policy and release gate | Assigned-user approval and over-budget refusal in portal |
| ATS-qualified candidate auto-progresses, including after v2 publication | Pipeline assignments, revision synchronization, ATS worker/actions | Current scored resume, human-confirmation toggle, genuine NeedsReview distinction |
| Schedule Interviews ready queue; manual movement retained | Qualified profile transition uses existing transition gates | Queue visibility, scheduling, hiring-case milestone |
| Panel member submits only own feedback; no repeated submission; decision after feedback | Talent repository and interview editor | Two users, negative forged request, final decision |
| Shared notification bell | Scoped workflow tasks + interview actions | Approver, panel and HR views; immediate refresh |
| MoM/negotiation before offer acceptance | Shared offer release gate on admin and public APIs | Early link refusal, approved normal path |
| Lifecycle mail at relevant stages | Existing configurable notification/outbox services | Local mail sink and stage event coverage; not live delivery |
| Duplicate jobs / work-order journeys | Stable requisition/position identity, current-card projection, history retained | Edit/relink/retry, latest-only cards, unchanged public URLs |
| OTP-free super-admin testing | Audited read-only View as candidate, no password exposure/change | Global-only access; ordinary admin denied |
| Talent Pool action only early or after unsuccessful outcome | Shared eligibility SQL + application-card flag | In-progress refused; rejected/declined/no-show allowed; joined refused |
| v2 affects existing and new applications | Assignment promotion + pending-stage policy recheck | Completed history and in-flight approval/evidence preserved |
| Auto-created skills do not require per-skill years | Default `MinimumYears=0`; separate manual duration switch; skill presence and overall experience retained | Six backend cases pass: automatic defaults, presence/missing evidence, manual duration unverified/insufficient/sufficient |
| Accepted offer needs separate departmental approval and final branded copy | Opt-in `RecruitmentFinalOffer` workflow; immutable template/terms/identity and attachment receipt | Real approval/retry/duplicate guards, original PDF retention, portal preview; production signatory mapping still needs confirmation |
| Old published URLs share settings without blocking on AI | Same-opening published policy transaction and existing ATS queue | Exact user-approved production policy; regression covers scope, stable URLs and no completed-candidate catch-up |
| Production versus current source | Read-only deployed asset and data observations | Exact deployed commit is not established by screenshots |

Test labels: repository tests use disposable loopback MySQL databases; UI fixture
tests use mocked API responses and are NOT end-to-end evidence. The visible portal
suite must use the real API and disposable test database, with outbound mail captured
locally. Record actual pass/fail and screenshots; never label an unrun case passed.

## Verified checkpoint — 20 September 2026

- Backend regression suite: **35 passed, 0 failed, 0 skipped**, including disposable
  database workflow/revision cases and optional skill-duration/scoring checks.
- Frontend regression suite: **28 passed**. Local build and isolated clean-lockfile
  host build passed; this is not a Linux image or production deployment test.
- Headed portal batch passed against the actual API and a disposable loopback DB.
  Evidence: `.codex-validation/recruitment-workflows/portal/result.json` and numbered
  screenshots. Seeded scores/versions/tasks are explicitly not live AI inference,
  the public submission flow, or proof that every form was submitted through the UI.
- The batch verified v2 catch-up and qualified/review separation, ready queue,
  scheduling API, two panel identities, forged feedback refusal, feedback/decision
  gating, completed read-only feedback, super-admin preview and ordinary-user denial,
  assigned budget approval, scoped bell navigation, current/history cards and stable
  public URLs. Three interview invites plus one seeded stage event reached the local
  SMTP sink. The separate paused interview feature returned its expected disabled
  capability response; it was not enabled or tested here.
- Candidate tracking now uses the current resume's saved score when no queue row
  exists, preserves real NeedsReview, and does not hide a queued rerun behind an old
  successful score. The corrected completed/review statuses passed the real API batch.
- Version-copy policy operation passed dry-run, content preservation, old-version
  retention, unchanged URLs and repeat-call idempotency checks before production use.

Expanded checks subsequently passed the complete configured interview → two MoM
signatures → negotiation → offer approval → separate HR-stage approval → public
acceptance → joining-date stage chain. An ATS-held application for the same opening
does not block the shortlisted cohort. The 11-lakh offer was refused against a
10-lakh approved budget despite salary maximum being higher. An already-created
public link also refused acceptance after the current budget was lowered, including
the legacy no-saved-configuration path. All fixture amounts were restored afterward.
Hiring approvals use the existing `RecruitmentPipelineTransition` resource with a
`HIRING_CASE:` resource ID, not a separate invented workflow resource type.

The current backend suite (workflow, revisions, optional skill years, engine/offer
guards) is **48 passed, zero skipped**. It includes a deferred first-page rotation
regression. The current-card and locked-UI-compatibility subset is **10 passed**.
The final **26-step headed batch passed** (2026-09-20 02:28–02:31 UTC), with zero
page errors/unexpected API 5xx and **17 synthetic deliveries** to the loopback SMTP
sink. It included fresh public TXT-resume upload/prefill, job OTP-off overriding the
form's OTP-on default, APP acknowledgement, a real completed score >=75, v2 binding,
automatic candidate movement and the actual Schedule Interviews UI. AI parsing was
disabled for this deterministic-parser test; this is not a local/cloud model benchmark.
See `.codex-validation/recruitment-workflows/portal/result.json`; earlier failed
fixtures/locators are not product failures or evidence of a passing run.
The engine's successful score status is `Completed` (legacy `Scored` also supported),
not just the wording displayed on an application card.

Remaining before full production sign-off: deployment, recipient-approved production
mail checks and final departmental-signatory activation. The user explicitly chose
Auto ATS on / OTP off for both Software Architect URLs; that update is recorded below.
Neither URL was closed. Browser tests use real handlers
and a disposable database; production business records were not used for mutations.

## Signed-letter clarification

Read-only production inspection found Trozan One (offer 1 / APP-UIDAI-000073) and
Cursor Shree (offer 2 / APP-UIDAI-000075) Accepted with `ApprovalPolicy=Direct`, no
linked final approval, and generic template 26. The user explicitly chose to leave
both records unchanged and recreate their own test candidates after deployment.
Do not retrofit approvals, overwrite issued PDFs, delete these candidates, or infer
signature authority from their Accepted status.

The existing branded renderer/private asset and approval-bound signing path are
retained. New post-acceptance signing uses a separate `RecruitmentFinalOffer` workflow
and branded template snapshot without altering template 26 or old letters. Existing
Accepted offers may explicitly request departmental approval after activation.
A deployment alone does not supply the actual signatory mapping. Follow
`Payroll.API/Assets/OfferLetters/README.md` after deployment and configure the actual
authorized final approver before claiming new signed-letter production readiness.

Deployment must include the intended UI and API changes. The three additive budget
columns were absent in the verified production DB and have now been added through
the narrow schema operation below; no broad migration was run there. The paused internal
interview feature remains off; do not enable it or run its separate migration as
part of signing off this recruitment-fix batch. Existing MailKit NU1902 and two
nullable compiler warnings remain; successful builds do not resolve those warnings.

## Explicitly approved production configuration

The user separately approved updating current jobs on production. Live inspection
found only JD **19** with a positive skill-year requirement:
`Software Architecture Design`, must-have, 5 years.

- Software Architect / position **16**: created approved JD **20**, version **2**,
  with all 10 skill durations optional. Published postings **7 and 8** now reference
  it. Both public APIs returned HTTP 200 and zero positive duration requirements.
- Old JD **19**, version **1**, all child records, previous scores and issued documents
  remain unchanged. Responsibilities, qualifications, required/preferred flags,
  weights and the overall 5+ year requirement were preserved. Public slugs and intake
  switches were preserved. `recruitment_audit` records the exact old/new policy.
- Platform Engineer / position **14**, posting **5**, JD **17** already had zero
  positive duration rules across 12 skills; overall experience remains 6+ years.
- Chief Architect / position **15**, posting **6**, JD **18** already had zero
  positive duration rules across 6 skills; overall experience remains 15+ years
  (including 5+ years architecture leadership). No unnecessary revisions were created.
- No active ATS-stage application matched the duration-only recheck selection;
  **zero jobs queued**. Completed candidate journeys were not rewound or rescored.
- This configuration uses existing tables; no schema change was needed for this
  policy. New-job defaults and other runtime/UI fixes still require deployment.
  `origin/main` was still **720673368443e8e35b136beb7276fd7933ef22a3** at inspection.

The bounded operator helper is `tools/recruitment-skill-years-policy.mjs`, with
explicit JD IDs, a global-super-admin audit actor, transaction rollback and dry-run
default. It is **not** an automatic startup migration. Rollback of this particular
policy is a reviewed pointer restore to JD 19 for position 16/postings 7–8, retaining
both versions and auditing that action; do not delete versions or rewrite scores.

Important observed data: Cursor Shree's 77.3 was `NeedsReview` because must-have
duration evidence was incomplete. A numerical pass alone does not override that.
The new optional-duration default does not silently rewrite saved job requirements;
those require a deliberate edit and a fresh score before an old duration hold changes.
Software Architect postings #7 and #8 were separate published records with different
intake switches, not two immutable JD versions. Neither public URL may be closed
without an explicit decision. Pipeline revision changes must not rewrite issued offers.

### Subsequent approved production changes / local production-DB verification

- Added only missing `recruitment_requisitions.BudgetApproverUserId` (nullable INT),
  `BudgetApprovalWorkflowInstanceId` (nullable BIGINT), and `BudgetApprovalStatus`
  (VARCHAR(30), non-null empty default). No business rows were rewritten by this DDL.
- User selected **Auto ATS on, OTP off** for both Software Architect live URLs.
  Posting 7 already matched; posting 8 was updated in a scoped, locked transaction
  with global-super-admin actor 3 and an old/new `recruitment_audit` receipt. Parsing
  switches, application links and public slugs stayed unchanged. No ATS jobs queued.
- Both live public APIs subsequently returned HTTP 200 and email verification false.
  Both DB posting rows have AutoRunAts=true. Current code applies subsequent operational
  intake changes to published sibling URLs for the same client/opening; drafts/history
  and other positions are excluded.
- Local UI :5173 / API :5062 use the production database with background workers,
  broad auto-migration and internal interviews disabled. Five-page read-only headed
  smoke passed (Jobs, Work Orders, Applications, Interview Tracker, Offers), zero
  page/API errors. Jobs and Chief Architect journey current-card counts were asserted.
  Evidence: `.codex-validation/local-production/result.json` and screenshots.
- No existing Trozan One/Cursor Shree offer, signature approval, attachment or accepted
  state was changed. No production candidates were created/deleted for these tests.

## Post-acceptance regression checkpoint

The expanded **30-step** headed real-API/disposable-DB batch passed on 20 September
2026 after adding final-signing and shared-policy coverage. It verifies:

- Acceptance creates a separate assigned departmental task. No stamp before approval.
- Changed approved terms refuse generation; approval remains saved and retry succeeds
  only after the approved terms are restored. Repeated successful retries reuse the file.
- The original accepted PDF remains current, undeleted and linked to the original offer.
  The final branded PDF is separately previewed inside the portal and exported normally.
- A second synthetic legacy Accepted offer obtains its first departmental approval.
  An actual client-bound approver with no general upload/admin rights triggers final
  PDF generation through the normal workflow callback. No persistent grants are added.
- Candidate and admin endpoints return byte-identical final PDFs. Completed action
  links remain read-only until expiry; invalid and expired links return 404.
- Both published URLs share future intake switches. Saving the toggle queues existing
  ATS-stage candidates without waiting for model inference, generating human-confirmation
  audits or rescoring accepted/interviewed applications. Other positions remain unchanged.

Latest 48-test backend regression subset and 28-test UI subset passed. The final UI
source also passed the previously fresh-npm-ci isolated build with an identical locked
manifest and Ant Design 5.5.0. A test-only fixed relative asset path was corrected to
find the repository root when `dotnet --artifacts-path` is used; the initial isolated
path failure was not hidden or a renderer failure. Host build is not a Docker deploy.

The final PDF artifact is
`.codex-validation/recruitment-workflows/portal/final-signed-offer-test.pdf`;
screenshots show both-page branding, watermark, intact signature/seal and joining
requirements. This is a synthetic test document, not an issued production offer.
The running local production-DB API deliberately leaves post-acceptance signing off
until the actual authorized departmental user is confirmed. The configured flow was
enabled only in the disposable fixture, not on historical production offers.

## Portal signatory configuration

Super admin can now select the client and final approver through **Offers & Pre-boarding
→ Final offer signatory**, including enable/disable. The existing client/global active
budget-approver lookup, shared approval bell and final-document workflow are reused.
The reserved per-client `modulesettings` record and recruitment audit store the choice;
no new table/column or signing migration. Explicit portal off overrides server fallback.
Saving does not retroactively approve/sign/email existing offers. Pending approvals or
unfinished approved document requests prevent changing the approver. Production choices
remain for the operator; feature tests never configure a real signatory on production.

Validation: **31-step headed real-API batch passed** (20 September 2026,
04:02–04:06 UTC), zero page errors/unexpected API failures. It now saves the signatory
through the actual dropdown with server signatory settings explicitly absent, tests
enable/disable, audit persistence, other-client/inactive/unauthorized refusal and pending
signatory-change refusal, then verifies new and historical Accepted offers through
departmental approval and signed PDF. Backend subset: 48 passed; UI compatibility/current
card subset: 10 passed. Workspace and isolated locked-dependency UI builds passed.
Local production-DB read-only smoke also passed all five pages plus the new settings
drawer, without saving a production mapping. Screenshots include
`portal/final-signatory-settings.png` under the recruitment-workflows artifact directory.
