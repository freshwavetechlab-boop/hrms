# UIDAI PDF-based hiring configuration and seed

This package converts the supplied UIDAI source documents into auditable HRMS hiring drafts. It does not treat missing facts as approved facts, does not overwrite the existing Centre Manager case, and does not bypass HRMS approval workflows.

## Source-to-record mapping

| Source | HRMS record | Facts preserved |
|---|---|---|
| `JD_Chief Data Scientist and Chief Architect (1).pdf` + `Approval Chief Architect & Data Scientist.pdf` | Chief Data Scientist - AI, Data & Biometrics | `UIDAI_BTC_0_25`, 1 vacancy, Bengaluru, INR 80-100 LPA, 30% approval flexibility, full JD |
| Same combined JD and approval | Chief Architect | `UIDAI_BTC_0_84`, 1 vacancy, Bengaluru, INR 80-100 LPA, 30% approval flexibility, 15+ years / 5+ architecture leadership, full JD |
| `Hiring for the position of Assistant Manager at RO, Mumbai.pdf` + `JD AM AB.pdf` | Assistant Manager | Email date/time, RO Mumbai, Band A-B, up to INR 9 lakh, 2+ years, below 50, 60-70% travel, full available JD |
| `Hiring for the position of Project Manager-State, RO,Mumbai.pdf` + `Project Mgr ST JD (3).pdf` | Project Manager - State | Email date/time, RO Mumbai, Band C, INR 7-12 lakh, two alternative eligibility routes, modified JD |

The partial **Manager - IEC Campaign Implementation** fragment is not seeded because the supplied pages do not contain a complete role profile or active hiring instruction.

## Configuration created or reused

- Existing active UIDAI client (`UIDAI`) is required and reused.
- Existing published `UIDAI_GAD_50_DAY_HIRING` pipeline is required and verified as exactly 50 days.
- UIDAI Technology Centre Bengaluru and UIDAI Regional Office Mumbai work locations are created only when missing.
- UIDAI-scoped Department, Business Unit, Employment Type, Experience Range and INR 10,000,000 Budget Amount dropdown values are created only when missing.
- Four source-linked requisition drafts are created or updated idempotently.
- A versioned JD with responsibilities, qualifications and ATS-oriented skill requirements is created for each requisition.
- Assistant Manager and Project Manager initiation emails become separate draft work orders with their original email timestamps and a 50-day SLA.
- The two formal approval roles can optionally be submitted to the existing HRMS workflow. External client approval remains a separate visible field and never bypasses the HRMS workflow.

## Source lineage added to Hiring Requests

The Hiring Request drawer now keeps these fields under Advanced details:

- client position code;
- source type, reference, original filename and date;
- source/requesting authority;
- client approval state;
- CTC flexibility percentage;
- source notes and unresolved ambiguities.

These fields require the normal repository migration/initialization once because they add columns to `recruitment_requisitions`.

## Safety and rerun rules

- The seed key is the exact external position code when supplied; otherwise it is the `[SOURCE:...]` marker stored in hiring notes.
- Re-running updates only Draft or Sent Back records.
- Pending, Approved, Rejected or other immutable business states are reported and left unchanged.
- Source-confirmed Chief roles use one opening each.
- Assistant Manager and Project Manager use one **provisional draft placeholder** because the PDFs do not confirm headcount. They are never auto-submitted.
- Unknown approval day, AM/PM position codes, target dates, recruiter and role-specific interview panel are not invented.
- The old Centre Manager work order, MoM, candidates and panel are not reused.
- Actual PDF binary files are not uploaded from a text summary. When originals are available they must go through the secured global attachment component against the corresponding work order/requisition.

## Run commands

First run the application migration yourself after taking the normal database backup:

```powershell
cd D:\NewHrms\hrms\Payroll.API
dotnet run -- --migrate
```

Set credentials only in the current terminal. They are not written to the report:

```powershell
cd D:\NewHrms\hrms\playwright-e2e
$env:HRMS_ORG_ADMIN_PASSWORD='<admin password>'
```

Validate UIDAI foundation, pipeline and current seed state without writes:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-uidai-pdf-hiring-seed.ps1 -UseRunningServices
```

Apply configuration and source-linked drafts. Use an actual active UIDAI requester employee code; if an active employee named Bashisth Gupt already exists, the code parameter can be omitted:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-uidai-pdf-hiring-seed.ps1 `
  -Apply `
  -UseRunningServices `
  -RequesterEmployeeCode '<UIDAI employee code>'
```

If UIDAI has no employee record yet, the supplied documents identify Bashisth Gupt as Deputy Director HR. The explicit option below creates a non-login requester profile with no portal access, email, salary or invented employment facts:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-uidai-pdf-hiring-seed.ps1 `
  -Apply `
  -ProvisionSourceRequester `
  -UseRunningServices
```

Only after review, submit the two formally approved roles to the configured HRMS approval workflow:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-uidai-pdf-hiring-seed.ps1 `
  -Apply `
  -SubmitFormalApprovals `
  -UseRunningServices `
  -RequesterEmployeeCode '<UIDAI employee code>'
```

The HTML report, screenshots, trace, video, JSON seed audit and Markdown seed audit are written under:

```text
playwright-e2e\artifacts\uidai-pdf-hiring-seed\<timestamp>
```

The latest folder is recorded in `playwright-e2e\artifacts\uidai-pdf-hiring-seed-latest.txt`.

## Still awaiting UIDAI confirmation

- exact day of the August 2026 formal approval letter;
- confirmed AM/PM headcount and client position codes;
- AM/PM formal hiring approval reference;
- whether the same 50-day pipeline applies unchanged to every role;
- role-specific ATS profile, interview panel and scorecard;
- original PDF file paths for secured attachment upload.
