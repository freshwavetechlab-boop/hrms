# Client document layouts and existing-manpower lists

Offer Letter templates may opt into a reusable PDF layout by beginning their body
with `<!-- frevo-document-layout:v1 -->` followed by layout JSON. Plain templates
and the existing UIDAI branded offer path retain their existing behavior.

## Portal workflow

1. Select the client in Talent Acquisition. Open [Hiring pipeline](/recruitment/hiring-pipeline)
   > **Manage pipeline** > **Hiring templates**. Add an Offer Letter template and
   upload its layout JSON.
2. Open [Offers & Pre-boarding](/recruitment/offers-and-pre-onboarding) (or Client
   candidate profiles), then **Employee lists & offer letters**. This action
   appears when the client has an active structured Offer Letter template.
3. Choose **Import employee list** and the original `.xlsx`. Select the input
   preset, actual header row and worksheet. Choose an existing **Work location /
   institute (optional)** for this import, then review source column mapping,
   defaults, identity matches and missing details before saving.
4. Filter by source group and **Work location / institute**. For existing unlinked
   rows, select the appropriate people > **Set work location** > choose an
   existing location > **Link profiles**. This links the roster row; it does not
   change the linked candidate's personal current location or salary.
5. Complete pending inputs using **Details / Edit**, then use **Draft PDF** or
   **Download selected drafts**. Missing required values, inconsistent salary
   calculations or layout overflow prevent generation. Drafts remain unsigned.
6. An offer manager may open **Signature & stamp** and upload one combined image
   for this client. Select ready, location-linked rows > **Release selected** (or
   one row's **Release**) > review **Apply signature and stamp** > **Release &
   download**. A saved image makes the switch default ON; switch it OFF for plain
   released letters. With no saved image, plain release remains available.

Release is an explicit action protected by `recruitment.offer.issue`. It records
an issued document and downloads its PDF (or a ZIP for several people). It does
not automatically run an approval workflow, email the document, change a hiring
stage, select/hire a candidate, convert to Employee Master or create payroll.
It does not bypass or replace the existing native offer approval workflow.

An unsigned draft and a plain released letter are different: a draft is for
review; a plain release is an issued letter without the uploaded signature image.
Both signed and plain releases have an offer number and stored release receipt.
Every selected row needs a linked active work location and complete document
inputs before release. Client-wide users can keep unlinked rows pending and
download ready unsigned drafts while the location is resolved.

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

The active, authenticated user must have recruitment candidate, offer-management
or offer-issue access to open the workspace. Actions retain separate permissions:

| Action | Required permission |
| --- | --- |
| Import a list | `recruitment.candidate.manage` |
| Edit document inputs | `recruitment.candidate.manage` or `recruitment.offer.manage` |
| Link work locations / manage the client's signature image | `recruitment.offer.manage` |
| Release signed or plain letters | `recruitment.offer.issue` |

Existing broad recruitment/settings administrator permissions still apply. The
server checks permissions, client ownership and exact location eligibility for
reads, imports, edits, location linking, drafts and release. Single-client and
Selected Clients users cannot access another client by changing a URL or row ID.
Neither a template nor spreadsheet text grants approval or signature authority.

## Work locations and institute grouping

An institute is an existing client Work Location, not a separate Institute
entity. The roster stores nullable `WorkLocationId` referencing `worklocations`.
The dropdown contains active master locations permitted for the current client
and user. The source group remains a separate list/import label; it never grants
location access. A city string, candidate `CurrentLocation`, or worksheet name
does not establish a location link.

For one location's list, select its master location during import. For a mixed
list, import where unlinked access is permitted, filter/select each known group
and use **Set work location**. Ordinary spreadsheet input mapping is still
available, but mapping free-text institute/location columns does not create a
master location or an authoritative FK link. Reimports retain an existing link
when no replacement is selected; preview fingerprints and row revisions detect
concurrent changes.

Linked documents obtain institute name, address, city and state from that active
master at generation. The renderer supplies `jobLocation`, `workLocation`,
`workLocationName`, `instituteName`, `institutionName`, `institutionShortName`,
`instituteAddress`, `institutionAddress`, `workLocationAddress`, `city`, `state`
and `institutionLocation` inputs. These values are authoritative over imported
document text. Keep the master name/address accurate before releasing letters.

The [Hiring pipeline](/recruitment/hiring-pipeline) also has a **Work location**
filter across its views. Position/requisition master location IDs determine
membership; no city-name inference is used. Omitting the filter preserves the
client-wide view for users entitled to it. Recruitment **SelectedLocations** and
**EmployeeLocation** restrictions use exact permitted master IDs; restricted
users cannot see unlinked roster or pipeline records until an authorized user
links them. A user's employee location is valid only within the same client.

## Layout contract

Models in `RecruitmentDocumentLayout.cs` define version 1. Supported primitives:
explicit page dimensions, flow sections with reserved page counts, paragraphs,
styled runs, bordered tables, positioned text/lines/rectangles, a configured
organization logo and an explicit signature-and-stamp slot. Ordinary image
elements accept only `organizationLogo`; release receives validated private
signing bytes separately. Arbitrary remote URLs, local paths, SVG and images
embedded in template inputs are not accepted. Fonts are embedded under
`Assets/OfferLetters/Layout` with their license files. Existing font face names
continue to resolve to their original font bytes.

New layouts can use a `signature` element with asset `signatureAndStamp`, or a
flow `signature` block with a reserved height. Existing layouts can retain a text
or paragraph containing `[Signature & Stamp of ...]` (also `and` instead of `&`).
The whole text must be the placeholder. New release rendering replaces that slot
with the private image; draft rendering never receives signing bytes. Applying
an image without a recognized slot is rejected.

The legacy placeholder uses the same previously compatible JSON primitives.
Retain it in configuration shared with an older production API until deployment;
do not replace it with a new `signature` kind before that API supports the kind.
The existing UIDAI native branded-offer renderer, signing configuration and offer
approval/release flow remain separate and unchanged.

## Client signature image and release receipts

**Signature & stamp** saves one combined PNG/JPEG privately for the selected
client, not in a public attachment or the layout JSON. Upload validation checks
file bytes and actual image decoding: at most **2 MiB**, **6000 pixels per side**
and **12,000,000 pixels** in total. A filename/MIME type alone is insufficient.
Only that client's asset can be applied; another client's signatory is never
inferred. An offer manager can replace or remove the saved image. Existing
released PDF bytes remain unchanged.

Release locks and validates the selected rows, linked active locations, template
and requested image. The first release assigns a stable offer number. Receipts
store the exact PDF bytes, hashes of the template/inputs/location/image, actor
and release timestamp. Repeating the same effective content returns the saved
PDF without another receipt, number or changing its original date. A later
content change creates a new immutable receipt under the existing offer number;
it does not overwrite the earlier released PDF. Signed and plain versions have
distinct receipt keys. Batch release is transactional; invalid rows do not
produce a partial batch of issued letters.

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

`RecruitmentDocumentRepository` lazily ensures additive roster/audit tables,
`WorkLocationId`/`OfferNumber` metadata, the work-location FK,
`recruitment_document_signing_assets` and
`recruitment_document_roster_releases`, following the existing repository
pattern. First use requires the corresponding CREATE/ALTER privileges; a named
lock serializes schema initialization. Schema initialization does not assign
locations, import people or issue offers. The roster's unique key remains
client/template/candidate/source-group. Audits retain input/location changes;
draft generation hashes are distinct from explicit release receipts.

Deploy both API and UI together. Older deployments do not expose the roster
launcher or endpoints; saving configuration alone does not deploy the feature.
Local implementation and shared template configuration do not mean production
was deployed, existing ICMR rows were linked, or any letters were released.
Rolling back the binaries leaves the additive tables and existing recruitment
records in place. Archive the template to disable new roster access for a client.

Regression coverage includes `RecruitmentDocumentTests`,
`RecruitmentDocumentSigningTests`, `RecruitmentWorkLocationScopeTests`, existing
`EngineAndOfferTests` and `ExcelPayslipPdfServiceTests`, plus frontend
`recruitment-document-layout.test.mjs`, `recruitment-roster-operations.test.mjs`
and `recruitment-document-service.test.mjs`. Real source import, identity reuse,
location linking and multi-page output still need an authenticated check in the
target portal; automated checks do not establish a client's live setup status.
