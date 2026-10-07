# Client attendance shifts

**Settings → Leave & Attendance → Shift Master** provides client-scoped Add/Edit and Active controls, table/card views, search and export. Shift codes are unique within a client. Deactivate hides the shift from new assignments and makes existing assignments fall back; records are retained.

Assign a shift to the selected employees through **Attendance Policies → Assigned shift**. Set the client's fallback under **Attendance → Default shift**. Both assignments are optional. Resolution checks the employee's active policy shift, then the client's default, then the existing attendance settings. Inactive, not-yet-effective and expired shifts are skipped. Effective-from and effective-to are inclusive; an overnight shift's eligibility uses its start date.

## Migration and deployment

Additive, lower-case table `attendance_shifts`; nullable `attendance_settings.shift_id` and `attendance_groups.shift_id`. Composite foreign keys enforce that a reference belongs to the same client. Existing records remain NULL, preserving their existing calculation. No existing payroll/attendance rows are rewritten.

Apply [20261006_attendance_shifts.sql](../Payroll.API/Database/Migrations/20261006_attendance_shifts.sql), or run from the repository root:

```powershell
dotnet run --project Payroll.API --launch-profile http -- --migrate-attendance-shifts
```

The command upgrades only the shift schema and exits. The normal `--migrate` path also includes it. Deploy/restart the API, payroll-ui and ess-mss together after migration. Migration is prepared for manual execution; it has not been applied to the connected production database.

## Calculation

Machine and ESS/mobile punches, approved miss-punch corrections, admin/MSS Attendance Review, and synchronous/background attendance batches use the same resolver and backend calculator. Daily results flow through existing monthly rollups to payroll. Approved leave, holidays, regularization and payroll locks keep their existing protections. Previously saved records are not recalculated just by changing a shift.

- **Fixed:** count working time inside the configured start/end window. Early arrival and overtime outside it do not add payable hours. Actual punch times remain stored. Grace controls late-coming/early-going indicators; it does not invent extra worked hours or apply an additional automatic salary penalty.
- **Flexible:** use actual working duration and the shift's full/half-day thresholds; no late/early clock penalties. Optional times are needed only to define an overnight attendance boundary.
- **Break:** deduct the configured minutes once from the calculated duration, floored at zero, before applying thresholds and the client's maximum-hour cap. For pair-based calculation, this is an additional deduction from valid worked intervals; avoid configuring a deduction for a break already excluded by OUT/IN punches.
- **Overnight:** 21:00 → 06:00 belongs to the 21:00 start date. A midpoint between the night OUT and the next applicable shift IN separates attendance days. If the night shift expires and the default starts at 09:00, the 06:00 OUT remains on the previous date while the 09:00 IN belongs to the new day.
- **Manual attendance:** Present entered without punch times remains an explicit manual mark, as before. With times, configured shift rules determine payable 1 / 0.5 / 0 and the API persists insufficient hours as absent. Holidays/leave are not converted into shift attendance.

## API

| Method | Endpoint | Purpose |
| --- | --- | --- |
| GET | `/api/leave-attendance/shifts?clientId=20` | List only that client's shifts |
| POST | `/api/leave-attendance/shifts` | Create a shift |
| PUT | `/api/leave-attendance/shifts/{id}` | Edit/reactivate/deactivate a shift |
| DELETE | `/api/leave-attendance/shifts/{id}?clientId=20` | Deactivate; retain history |

Authenticated requests use existing permissions. Writes require `settings.manage` or `client.settings.manage` plus access to the client. Attendance managers can read shifts for policy selection; MSS-only users do not gain shift administration. Updating an ID under a different client is rejected. Swagger/API Catalog includes the four endpoints.

## Verification cases

| Case | Input | Expected |
| --- | --- | --- |
| Fixed | 09:00–18:00, break 60, thresholds 8/4; punches 08:00–20:00 | 8 payable hours, full day; out-of-window time excluded |
| Fixed half/absent | Same shift; OUT 14:00 / 12:00 | 4 hours, half day / 2 hours, absent |
| Grace | Grace 10; IN 09:10 vs 09:11 | First has no late flag; second has late flag |
| Flexible | Break 60, thresholds 8/4; work 9 / 5 / 4 elapsed hours | 8/full, 4/half, 3/absent; no clock flags |
| Overnight | September 1 21:00 → September 2 06:00, break 60 | September 1 attendance, 8/full; also valid when September 1 is effective-to |
| Night expiry | Night effective-to September 1; default day shift starts at 09:00 | September 2 06:00 OUT belongs to September 1; September 2 09:00 IN belongs to September 2 |
| Assignment | Employee shift differs from client default | Employee shift wins; invalid/inactive/expired assignment uses default |
| No shift | All references NULL | Existing times, thresholds, grace flags and payroll rollups continue unchanged |
| Scope/validation | Wrong client, duplicate code, missing fixed times, full ≤ half, invalid dates | Denied or clear validation error; no invalid assignment |

Validation: API, payroll-ui and ess-mss builds pass. The attendance backend suite passes 22 tests; shared admin/MSS preview tests pass 3 tests. Playwright checks use mocked APIs and cover shift CRUD/validation, activation, assignment dropdown payloads, client isolation, a real `.xlsx` export, and card/table/drawer layout at 1366×768, 768×1024 and 390×844. No production configuration or attendance was changed. The migration and real database CRUD/punch-to-payroll integration still need verification after manual migration.

```powershell
dotnet test Payroll.API.Tests/Payroll.API.Tests.csproj --no-restore --filter FullyQualifiedName~Attendance
```

From `payroll-ui`:

```powershell
node --test tests/attendance-shift.test.mjs
```
