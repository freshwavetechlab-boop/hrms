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
- JD and communication templates

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
