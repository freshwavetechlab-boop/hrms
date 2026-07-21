# Recruitment Test Data

Use this dataset to test Recruitment & Hiring without touching live client data.

## Seed Command

```powershell
powershell -ExecutionPolicy Bypass -File scripts\seed-recruitment-test-data.ps1
```

Optional password override:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\seed-recruitment-test-data.ps1 -Password "Test@12345"
```

The seed is idempotent. Running it again updates the same `TAT` test records instead of creating duplicates.

## Test Client

- Code: `TAT`
- Name: `TA Test Client Pvt Ltd`
- Work locations:
  - `TAT Corporate Office`
  - `TAT Delivery Center`

## Test Users

All users use password: `Test@12345`

| Purpose | Email |
|---|---|
| ESS RFR requester | `tat.requester@frevo.local` |
| Workflow approver / manager | `tat.approver@frevo.local` |
| Recruiter / HR operations | `tat.recruiter@frevo.local` |
| Admin setup user | `tat.admin@frevo.local` |

## Test Employees

| Code | Name | Department | Role |
|---|---|---|---|
| `TAT100` | Anita Requester | Engineering | Creates RFR from ESS |
| `TAT101` | Mohan Approver | Engineering | Approves RFR |
| `TAT102` | Rekha Recruiter | Human Resources | Recruitment operations |

## Seeded Recruitment Setup

- Recruitment enabled for `TAT`
- Employee RFR creation enabled
- Vendor, consultant, internal hiring, referral hiring enabled
- RFR approval workflow: `TAT_RFR_APPROVAL`
- Approval mapping: `RFR_APPROVAL`
- Masters for position category, hiring type, employment type, source, priority, interview, candidate, and offer status
- Vendor and consultant sample records
- Recruiter assignment rule
- SLA rule
- Document checklist
- JD, communication, and Offer Letter templates
- Published, versioned application and pre-onboarding forms
- Published hiring pipeline with ATS, document, interview, HR, offer, and joining stages
- Configured stage transitions, SLA clocks, interview competencies, external candidate actions, and offer policy

## Suggested Test Flow

1. Login ESS as `tat.requester@frevo.local`.
2. Open Recruitment and create a new RFR.
3. Submit the RFR.
4. Login as `tat.approver@frevo.local`.
5. Open My Tasks and approve the RFR.
6. Login admin/payroll portal as `tat.admin@frevo.local` or `tat.recruiter@frevo.local`.
7. Open Recruitment and verify:
   - dashboard counts
   - requisition status
   - open position creation
   - position timeline
   - checklist
   - recruiter/vendor/consultant operations
8. From ESS, check internal openings and submit a referral if a referral campaign is created.

## Full Orchestration Test Flow

1. In Recruitment, create and submit a JD version for workflow approval, then approve it from My Tasks.
2. Open Job Postings, select the approved JD, published application form, and published pipeline; publish the posting.
3. Copy the generated `/careers/{slug}` URL and submit a candidate application without logging in. Upload the resume through the configured upload field.
4. Open Hiring Pipeline and confirm the candidate is in the first stage with a live SLA clock.
5. Move the candidate to the ATS stage. Confirm the normalized score, criterion evidence, matched/missing skills, and configured manual/automatic outcome.
6. Schedule each configured interview round from its calendar picker, assign the panel, and submit competency feedback.
7. Move the candidate to the Offer stage, create a Draft offer, then choose **Generate letter**. Preview it through **View letter**; it must open through a short-lived attachment ticket rather than a storage path.
8. Choose **Submit / release**. If the stage/global policy requires approval, approve it from My Tasks and then choose **Release**.
9. Open the generated candidate action link and verify that the candidate can securely view the offer and accept, reject, or request negotiation.
10. Complete the configured pre-onboarding form/documents through the candidate link. Verify required global documents before advancing.
11. Convert the accepted candidate to an employee and confirm that candidate activity and secured recruitment documents remain visible from the employee 360 profile.

Generated offer letters use the global attachment system. Re-generating one offer retires only that offer's prior letter; other offers for the same candidate remain independently accessible.
