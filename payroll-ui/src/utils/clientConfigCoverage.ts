import type { AttendanceGroup, AttendanceSettings, AttendanceShift, Employee, Holiday, LeaveType, Setup, TravelExpenseSetup, WorkLocation } from '../types/payroll'

export type CoverageStatus = 'Covered' | 'Uncovered' | 'Review'
export type EmployeeCoverageResult = { employee: Employee; status: CoverageStatus; detail: string }
export type CoverageCheck = { key: string; title: string; description: string; route: string; results: EmployeeCoverageResult[]; unavailable: boolean; disabled: boolean }
export type CoverageInput = {
  clientId: number; asOf: string; employees: Employee[]; attendanceGroups: AttendanceGroup[]
  setup: Setup; shifts: AttendanceShift[]; attendanceSettings: AttendanceSettings | null
  leaveTypes: LeaveType[]; holidays: Holiday[]; locations: WorkLocation[]; travel: TravelExpenseSetup
  failures: Record<string, string>
}
const text = (value: unknown) => String(value ?? '').trim()
const id = (value: unknown) => Number(text(value).split(':')[0])
const clientMatches = (value: unknown, clientId: number) => text(value).split(/[;,|]/).some(value => id(value) === clientId)
const effective = (from: string | null | undefined, to: string | null | undefined, day: string) =>
  (!from || from.slice(0, 10) <= day) && (!to || to.slice(0, 10) >= day)
// ESS leave criteria use case-insensitive exact text comparisons; blanks and All are unrestricted.
const leaveMatches = (expected: string, actual: string) => !text(expected) || expected.toLowerCase() === 'all' || expected.toLowerCase() === (actual || '').toLowerCase()
const travelMatches = (expected: string, actual: string) => !expected || expected.toLowerCase() === (actual || '').toLowerCase()

export function buildEmployeeCoverage(input: CoverageInput): CoverageCheck[] {
  const { clientId, asOf, failures } = input
  const employees = input.employees.filter(row => row.clientId === clientId && row.isActive)
  const groups = input.attendanceGroups.filter(row => row.clientId === clientId && row.isActive)
  const membership = new Map<number, AttendanceGroup[]>()
  for (const group of groups) for (const employeeId of new Set(group.employeeIds || [])) {
    const current = membership.get(employeeId) || []
    current.push(group)
    membership.set(employeeId, current)
  }
  const templates = new Map(input.setup.salaryStructures.map(row => [id(row.id), row]))
  const components = new Map(input.setup.salaryComponents.map(row => [id(row.id), row]))
  const locations = new Map(input.locations.filter(row => row.clientId === clientId).map(row => [row.id, row]))
  const shifts = new Map(input.shifts.filter(row => row.clientId === clientId && row.isActive && effective(row.effectiveFrom, row.effectiveTo, asOf)).map(row => [row.id, row]))
  const leaveTypes = input.leaveTypes.filter(row => row.clientId === clientId && row.isActive && effective(row.effectiveFrom, row.expiresOn, asOf) && row.attendanceAction !== 'Mark as present')
  const result = (employee: Employee, status: CoverageStatus, detail: string): EmployeeCoverageResult => ({ employee, status, detail })
  const check = (key: string, title: string, description: string, route: string, sources: string[], resolve: (employee: Employee) => EmployeeCoverageResult, disabled = false): CoverageCheck => {
    const unavailable = ['employees', ...sources].some(source => Boolean(failures[source]))
    return { key, title, description, route, unavailable, disabled, results: unavailable || disabled ? [] : employees.map(resolve) }
  }
  const attendance = (employee: Employee) => {
    const assigned = membership.get(employee.id) || []
    return assigned.length === 0 ? result(employee, 'Uncovered', 'No active attendance policy membership. Assign an attendance policy.')
      : assigned.length > 1 ? result(employee, 'Review', `Multiple active attendance policies: ${assigned.map(row => row.name).join(', ')}. Resolve the overlap.`)
        : result(employee, 'Covered', assigned[0].name)
  }
  const salary = (employee: Employee) => {
    if (!input.setup.salaryComponents.length) return result(employee, 'Uncovered', 'No salary component masters are configured.')
    if (text(employee.salaryStructureId)) {
      const template = templates.get(id(employee.salaryStructureId))
      if (!template || !template.active || !clientMatches(template.clientId, clientId)) return result(employee, 'Review', 'Assigned salary template is missing, inactive, or belongs to another client. Confirm the payroll fallback before processing.')
      const invalid = template.lines.filter(line => !components.get(id(line.componentId))?.active)
      if (!template.lines.length || invalid.length) return result(employee, 'Review', `${template.name}: ${!template.lines.length ? 'no component lines' : `${invalid.length} missing/inactive component(s)`}. Review the template.`)
      return result(employee, 'Covered', `Salary template: ${template.name}. Amounts and formulas require simulation.`)
    }
    if (employee.annualCtc > 0 || Object.keys(employee.salaryComponents || {}).length) return result(employee, 'Covered', 'Annual CTC or individual salary components present. No explicit template assignment; confirm client fallback and simulate pay.')
    return result(employee, 'Uncovered', 'No annual CTC, individual salary components or salary template. Set a salary basis.')
  }
  const setting = input.travel.clientSettings.find(row => row.clientId === clientId)
  const travelEnabled = Boolean(setting?.isEnabled && effective(setting.effectiveFrom, setting.effectiveTo, asOf))
  const policies = new Map(input.travel.policies.filter(row => row.companyId === clientId && row.isActive && row.status === 'Active' && effective(row.effectiveFrom, row.effectiveTo, asOf)).map(row => [row.id, row]))
  const assignments = input.travel.assignments.filter(row => row.companyId === clientId && row.isActive && policies.has(row.policyId) && effective(row.effectiveFrom, row.effectiveTo, asOf)).sort((a, b) => a.priority - b.priority || b.id - a.id)

  return [
    check('payroll-coverage', 'Attendance + salary setup', 'Unique employees with one active attendance policy, a salary basis, employee code and first name. This is configuration coverage, not approval to run payroll.', '/employees/master', ['groups', 'setup'], employee => {
      const outcomes = [attendance(employee), salary(employee)]
      if (!text(employee.employeeCode) || !text(employee.firstName)) outcomes.push(result(employee, 'Uncovered', 'Employee code or first name is missing.'))
      const gaps = outcomes.filter(row => row.status !== 'Covered')
      return result(employee, gaps.some(row => row.status === 'Uncovered') ? 'Uncovered' : gaps.length ? 'Review' : 'Covered', gaps.length ? gaps.map(row => row.detail).join(' ') : `${outcomes[0].detail}; ${outcomes[1].detail}`)
    }),
    check('attendance-coverage', 'Attendance policy', 'Counts each active employee once. Membership in multiple active policies needs review; inactive policies do not count.', '/settings/leave-attendance/attendance-policies', ['groups'], attendance),
    check('salary-coverage', 'Salary basis', 'Active assigned templates and valid alternatives (CTC or individual components). Invalid template references need review.', '/settings/salary-templates', ['setup'], salary),
    check('shift-coverage', 'Shift assignment', 'Effective policy shift, then effective client default shift. Saved timing settings are shown for review when no named shift applies.', '/settings/leave-attendance/shift-master', ['groups', 'attendance', 'attendanceSettings'], employee => {
      const assigned = (membership.get(employee.id) || []).filter(row => row.shiftId != null).sort((a, b) => a.id - b.id)
      const shift = shifts.get(assigned[0]?.shiftId || 0) || shifts.get(input.attendanceSettings?.shiftId || 0)
      if (shift) return result(employee, 'Covered', `${shift.shiftName}${shift.id === assigned[0]?.shiftId ? ' (attendance policy)' : ' (client default)'}.`)
      return input.attendanceSettings?.id ? result(employee, 'Review', 'No effective named shift; client attendance timing settings will apply. Confirm that these settings are suitable.')
        : result(employee, 'Uncovered', 'No effective named shift or saved client timing settings. Review punch-based attendance; monthly payroll can use reviewed payable days.')
    }),
    check('leave-coverage', 'Leave eligibility', 'At least one effective leave type matches employee criteria. Regularization-only types are excluded. Balances and waiting periods are not checked.', '/settings/leave-attendance/leave-types', ['leave', 'locations'], employee => {
      const eligible = leaveTypes.filter(row => row.applicabilityMode === 'All employees' ||
        (leaveMatches(row.workLocation, locations.get(employee.workLocationId)?.name || '') && leaveMatches(row.department, employee.department) && leaveMatches(row.designation, employee.designation) && leaveMatches(row.gender, employee.gender)))
      return result(employee, eligible.length ? 'Covered' : 'Uncovered', eligible.length ? eligible.map(row => row.name).join(', ') : 'No effective leave type matches this employee. Review applicability and effective dates; exclusions may be intentional.')
    }),
    check('holiday-coverage', 'Holiday calendar scope', `At least one holiday for ${asOf.slice(0, 4)} applies to the employee location. This does not confirm that the calendar is complete.`, '/settings/leave-attendance/holiday', ['holidays'], employee => {
      const applicable = input.holidays.filter(row => row.clientId === clientId && row.startDate.slice(0, 4) <= asOf.slice(0, 4) && row.endDate.slice(0, 4) >= asOf.slice(0, 4) && (row.allLocations || row.workLocationIds.includes(employee.workLocationId)))
      return result(employee, applicable.length ? 'Covered' : 'Uncovered', applicable.length ? `${applicable.length} holiday(s) apply to this location.` : 'No holiday in the checked year applies to this employee location.')
    }),
    check('travel-coverage', 'Travel policy', 'Effective client/employee/location/department/grade/designation assignments, following current ESS priority. Limits and trip-specific eligibility are checked when requesting travel.', '/settings/travel-and-expense-policies', ['travel'], employee => {
      const assignment = assignments.find(row => (row.branchId == null || row.branchId === employee.workLocationId) && (row.employeeId == null || row.employeeId === employee.id) && travelMatches(row.department, employee.department) && travelMatches(row.grade, employee.grade) && travelMatches(row.designation, employee.designation))
      return result(employee, assignment ? 'Covered' : 'Uncovered', assignment ? `${policies.get(assignment.policyId)!.policyName} (assignment priority ${assignment.priority}).` : 'No effective travel policy assignment matches this employee. Assign a policy if travel is required.')
    }, !travelEnabled),
    check('location-coverage', 'Work location', 'Employee is linked to an active work location belonging to this client.', '/employees/master', ['locations'], employee => {
      const location = locations.get(employee.workLocationId)
      return result(employee, location?.isActive ? 'Covered' : 'Uncovered', location?.isActive ? location.name : 'Work location is missing, inactive, or belongs to another client.')
    }),
    check('manager-coverage', 'Reporting reference', 'Presence of employee-manager or approver-user mapping only. User status, access and workflow levels still require review.', '/employees/org-structure', [], employee => result(employee,
      employee.reportingManagerUserId || employee.reportingManagerId ? 'Covered' : 'Uncovered',
      employee.reportingManagerUserId || employee.reportingManagerId ? `Approver user: ${employee.reportingManagerUserId || 'not set'}; employee manager: ${employee.reportingManagerId || 'not set'}.` : 'No reporting reference. Review if reporting-based approvals are required.')),
    check('bank-coverage', 'Bank payment details', 'Account number and IFSC presence only; not bank verification. Missing details do not block salary calculation.', '/employees/master', [], employee => result(employee,
      text(employee.paymentDetails?.bankAccountNo) && text(employee.paymentDetails?.ifscCode) ? 'Covered' : 'Uncovered',
      text(employee.paymentDetails?.bankAccountNo) && text(employee.paymentDetails?.ifscCode) ? 'Account number and IFSC are present.' : 'Account number or IFSC is missing. Complete before bank payment.')),
    check('portal-coverage', 'ESS enablement', 'Employee portal-access flag only; this does not verify that a login user exists or has permission.', '/employees/master', [], employee => result(employee, employee.portalAccess ? 'Covered' : 'Uncovered', employee.portalAccess ? 'Employee portal access is enabled.' : 'Employee portal access is disabled. Enable if ESS is required.')),
    check('email-coverage', 'Employee email', 'Email presence for notifications; delivery and address validity are not checked.', '/employees/master', [], employee => result(employee, text(employee.workEmail) ? 'Covered' : 'Uncovered', text(employee.workEmail) ? 'Employee email is present.' : 'No employee email. Welcome emails and payslip delivery cannot reach this employee by email.'))
  ]
}
