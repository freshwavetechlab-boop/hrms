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
