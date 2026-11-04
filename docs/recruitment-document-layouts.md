# Client document layouts and existing-manpower lists

Offer Letter templates may opt into a reusable PDF layout by beginning their body
with `<!-- frevo-document-layout:v1 -->` followed by layout JSON. Plain templates
and the existing UIDAI branded offer path retain their existing behavior.

## Portal workflow

1. Select the client in Talent Acquisition. Open Pipeline > Manage pipeline >
   Hiring templates. Add an Offer Letter template and upload its layout JSON.
2. Open Offers & Pre-boarding (or Client candidate profiles), then
   **Employee lists & draft letters**. This action appears only when the selected
   client has an active structured Offer Letter template.
3. Import the original `.xlsx` employee list. Select its configured input preset,
   actual header row and worksheet. Review source column mapping, defaults,
   identity matches and missing document details before saving.
4. Complete pending fields using **Details / Edit**. A missing required value,
   inconsistent salary calculation or layout overflow prevents draft generation.
5. Use **Draft PDF** for one person, or select ready rows and download drafts as a
   ZIP. These are unsigned drafts; generation does not email, approve, sign,
   select, hire, convert to Employee Master or create a payroll record.

Monthly basic, HRA and gross remain monthly document inputs. They are not inferred
to be annual CTC. Source workbook cells, cached amounts and formulas are read-only.
Formula cells without cached results require review rather than becoming zero.
Numeric identifiers use the original XLSX numeric cache to avoid scientific
notation or premature floating-point rounding. Unrecoverable zeros absent from a
numeric source cell are not invented.

## Identity, updates and scope

The roster uses existing recruitment candidate profiles. Email and phone identify
existing records; names alone never merge candidates. Conflicting contacts,
another client's identity, archived profiles and multiple possible matches must
be reviewed. Same client/template/source-group/person imports update their saved
row. Incoming blanks retain details completed later. A preview fingerprint is
rechecked at save and imports serialize roster intake. The existing candidate
save performs its normal identity check again; legacy concurrent upload paths
are not redesigned by this feature.

Document editing cannot change linked profile identity. Candidate name/email/
phone come from the linked active profile at generation. A revision check prevents
stale edits. Referenced candidates and templates cannot be hard deleted; the
normal archive/inactive workflow remains available.

The authenticated active user needs `recruitment.candidate.manage`; client-bound
users remain restricted to their client. Neither the layout nor a source row can
grant approval/signature authority or change client scope.

## Layout contract

Models in `RecruitmentDocumentLayout.cs` define version 1. Supported primitives:
explicit page dimensions, flow sections with reserved page counts, paragraphs,
styled runs, bordered tables, positioned text/lines/rectangles, and a configured
organization logo. The only permitted image asset is `organizationLogo`; arbitrary
remote/local images and signing assets are not accepted. Fonts are embedded under
`Assets/OfferLetters/Layout` with their license files. Existing font face names
continue to resolve to their original font bytes.

Inputs define type, required status, blank display text, source header aliases,
empty markers and defaults. Import presets configure source-group names, header
rows and defaults. Ordered numeric calculations support addition, subtraction,
multiplication and division, with optional tolerance checks. Monetary formatting
and dates are explicit. Values expand once as data and cannot inject layout
directives, HTML, recursive placeholders or page breaks. Overflow produces an
error; fixed text may use an explicitly configured minimum font size.

The client's full contract layout and private example lists are configuration
data, not shared training examples. They should not be committed to the public
repository or mapping/resume RAG.

## Persistence and deployment

`RecruitmentDocumentRepository` lazily ensures the additive tables
`recruitment_document_roster` and `recruitment_document_roster_audit`, following the
existing recruitment repository pattern. The runtime DB user needs CREATE TABLE
on first use. The roster's unique key is client/template/candidate/source-group.
Audit records retain input changes and generation hashes. A generation receipt
does not represent offer approval or issue.

Deploy both API and UI together. Older deployments do not expose the roster
launcher or endpoints; saving configuration alone does not deploy the feature.
Rolling back the binaries leaves the additive tables and existing recruitment
records in place. Archive the template to disable new roster access for a client.

Regression coverage: `RecruitmentDocumentTests`, existing `EngineAndOfferTests`
and `ExcelPayslipPdfServiceTests`, plus the frontend
`recruitment-document-layout.test.mjs`. Real source import, identity reuse and
eight-page output need an authenticated portal check in the target environment.
