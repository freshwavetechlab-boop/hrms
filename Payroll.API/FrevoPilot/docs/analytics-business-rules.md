# HRMS governed analytics

## Workforce
Count employees where IsActive=1 for active headcount. Client scope is employees.ClientId=clients.Id. Distinguish employee count from appointments, openings and applicants. Never silently substitute all employees for active employees. Work locations belong to clients. Gender or category values may be empty: report an unmapped category, not an invented classification.
Do not filter clients.IsActive unless the question explicitly requests active clients. Employees of inactive clients still belong in a request for all active employees. Keep unassigned work locations with a LEFT JOIN; never eliminate them using a WHERE predicate on the optional location table.

## Payroll
Use payruns stored PayrollCost and NetPay. Do not recompute salaries or combine payrunemployee detail with header totals without pre-aggregation. A run is not an employee; count payrun employees at the correct grain. Clearly label the period requested; no period means all recorded data, not the current month.

## Attendance and leave
Use employee_monthly_attendance for payable days and loss-of-pay days. Leave requests use essleaverequests.Days and Status. Unavailable attendance is missing data, not zero attendance performance. Requested, approved, rejected and cancelled leave are different statuses.

## Recruitment
Candidate profiles and applications are different entities. A candidate can apply to multiple jobs. Filter ApplicationType='Application' when counting job applications, excluding talent-pool entries. Use recruitment_application_scores.IsCurrent=1 for current ATS scores. Average ATS score is non-additive and must not be summed across groups. Include the position title when grouping job applications. RemainingPositions and ApprovedPositions describe seat counts, not candidate counts.

## Workflow
Join workflowtasks.InstanceId to workflowinstances.Id, then WorkflowId to workflowmasters.Id, then workflowmasters.Code to workflowactivities.ActivityCode. Joining activities only by ResourceType duplicates tasks. Count distinct tasks, and keep pending, approved and rejected statuses separate.

## Money and interpretation
Billing rates and rules are configuration, not realized revenue. Claimed amounts and approved amounts are not interchangeable. A current snapshot does not prove a trend, cause, delay, forecast or policy violation. Show empty results explicitly. Multiple charts of the same data are alternate views, not independent evidence.

## Scope and privacy
Only authenticated, global super administrators can run this initial analytics release. Keep every query SELECT-only and bounded. Never select credentials, personal contact details, bank data, identifiers, resume text or raw attachments. Client scope is supplied by the authorized backend, not by the AI model.
