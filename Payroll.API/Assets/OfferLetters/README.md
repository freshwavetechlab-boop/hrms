# UIDAI offer-letter rollout

The renderer is opt-in using `<!-- gad-uidai-offer:v1 -->` in a client-scoped offer template. Generic templates and already issued PDFs are unchanged. No database migration is required.

1. Deploy UI/API together. The API uses MIT-licensed PDFsharp 6.2.2 and bundled Noto Serif fonts under the adjacent OFL license.
2. Run `tools/Configure-UidaiJoiningPack.ps1` with the API base, verified UIDAI client ID and `-ConfigureDocuments`. It previews unless an action switch is provided and prompts for credentials; credentials are never persisted. It reuses existing attachment/checklist/form APIs.
3. Identify the actual authorized signatory's user ID. Configure the final offer-approval workflow stage for that user. Set `OfferSigning__Uidai__ClientId` and `OfferSigning__Uidai__FinalApproverUserId` on the API. The explicitly supplied original seal is bundled in the API-only `PrivateAssets/OfferSigning` directory and copied into build/publish output. Keep repository/container access private. `OfferSigning__Uidai__SignaturePath` is an optional private mount override; remove any obsolete Windows-only override on Linux. No frontend or public URL serves the raw asset.
4. After deployment, use the setup script with `-InstallOfferTemplate -TemplateId <UIDAI offer template ID>`. A capability probe prevents activating it against an old API. Keep a copy of the prior template for rollback.
5. A draft displays a signature placeholder. Final workflow approval generates the signed PDF. Release revalidates the workflow/signatory and approved template/terms, and refuses issuance if signing is unavailable or terms have changed. Never backdate an approval or alter an accepted offer for testing.
6. The new `[[AUTHORIZED_SIGNATORY_BLOCK]]` places the intact original seal behind the printed company/signatory labels. Legacy signature markers remain supported. Updating an approved template requires fresh approval; previously issued PDFs are not rewritten. Offer preview opens inside the portal; Export PDF opens the browser's normal PDF viewer.

The reference document has 11 joining-document requirements; PAN and Aadhaar are collected separately, giving 12 mandatory upload fields. Newly generated document/preboarding links use the dedicated client-scoped form. Previously issued links remain unchanged and must be reissued deliberately if needed. HR must verify uploaded files and originals; no automatic verification is asserted.

Tests: `dotnet test Payroll.API.Tests --filter FullyQualifiedName~EngineAndOfferTests`. Headed browser tests are in the local `playwright-e2e/tests/engine-monitor-live.spec.ts`; run only with authorized test credentials/data. Monitoring is measured busy time, not a fabricated per-engine CPU percentage.

## Candidate acceptance → departmental approval → final signed copy

This additive flow is **off by default**. In the super-admin portal, open
**Offers & Pre-boarding → Final offer signatory**, select the intended client, enable
post-acceptance signing and choose the authorized active user. The searchable list
reuses budget-approver lookup and includes only that client's/global users. Client-bound
admins cannot configure this authority, including through direct API requests.

The audited policy reuses `modulesettings` with a client-suffixed reserved key;
no schema migration is required. Explicit portal off overrides legacy server settings.
When no portal row exists, `OfferSigning__Uidai__PostAcceptanceEnabled`, `ClientId` and
`FinalApproverUserId` remain backward-compatible fallback. Existing pre-acceptance
signing still uses its original server configuration. Pending final approvals or
approved-but-unissued letters must be resolved before changing the signatory.

Confirm the organization logo and private asset above. Keep an active
`OFFER_LETTER` / `RECRUITMENT` / `PRE_ONBOARDING` attachment field with `allow_multiple=true`
in that client/global scope. This prevents the signed copy from retiring the original.

- Candidate acceptance automatically requests `RecruitmentFinalOffer` approval through
  the existing workflow engine. The assigned user sees it in the normal approval bell.
- Existing Accepted offers can use **Request final approval** in Offers & Pre-boarding.
  There is no startup mass-signing or retroactive approval of old offers.
- Approval uses a snapshot of the bundled branded wording, candidate/job identity and
  financial terms. A mismatch refuses signing. The original offer template, workflow,
  Accepted status and original PDF remain unchanged.
- Successful approval stores a separate final PDF receipt in workflow history. A
  rendering/storage failure leaves approval saved and exposes **Retry final PDF**.
  Repeated successful calls reuse the same receipt/file rather than creating copies.
- **Final signed letter** previews inside the portal; Export PDF uses the browser PDF
  viewer. The candidate's valid, unrevoked offer link can read the final copy even after
  acceptance consumed its write action. Expired/revoked links do not gain access.
- This path uses existing workflow/history/attachment tables; no new signing migration.
  It does not install or override the generic template used for the original offer.

Real API + disposable DB test: `node tools/test-recruitment-workflow-portal.mjs`.
It uses synthetic identities and local mail delivery, not production signing authority.
