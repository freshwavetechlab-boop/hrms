import type { PayRun, PayRunDiagnostics, PayRunStepLog } from '../types/payroll'

export type RegularRunJourneyState = 'done' | 'active' | 'blocked' | 'warning' | 'pending'
export type RegularRunJourneyStep = { id: string; title: string; detail: string; state: RegularRunJourneyState }
export type RegularRunJourneyInput = {
  run: PayRun | null
  diagnostics: PayRunDiagnostics | null
  clientId: number
  policySelected: boolean
  includedCount: number
  attendanceReady: boolean
  attendanceIssues: number
  locationLoading: boolean
  adjustmentCount: number
  workingDays: number
  policyName: string
}
export type RegularRunJourney = {
  status: 'ready' | 'running' | 'blocked' | 'review' | 'approval' | 'approved'
  steps: RegularRunJourneyStep[]
  title: string
  subtitle: string
  blockers: Array<{ employeeCode?: string; message: string; stepName?: string }>
}

const completedCalculation = new Set(['Draft', 'Pending Approval', 'Approved', 'Partially Paid', 'Paid'])
const approvedCalculation = new Set(['Approved', 'Partially Paid', 'Paid'])
const running = new Set(['Queued', 'Processing'])
const range = (start: number, end: number) => Array.from({ length: end - start + 1 }, (_, i) => start + i)
const failedLog = (log: PayRunStepLog) => ['failed', 'error', 'blocked'].includes(log.status.toLowerCase())

/** Presentation only: never substitutes a diagnostic acknowledgment for a saved approval. */
export function buildRegularRunJourney(input: RegularRunJourneyInput): RegularRunJourney {
  // A transient selection from another client must never supply this workspace's checks.
  const run = input.run?.clientId === input.clientId ? input.run : null
  const diagnostics = run && input.diagnostics?.payRunId === run.id ? input.diagnostics : null
  const logs = diagnostics?.stepLogs ?? []
  const issues = [...(diagnostics?.validationIssues ?? []), ...(diagnostics?.exceptions ?? [])]
  const blockers: RegularRunJourney['blockers'] = []
  const blockerKeys = new Set<string>()
  const addBlocker = (message: string, employeeCode?: string, stepName?: string) => {
    if (!message.trim()) return
    const key = `${employeeCode ?? ''}|${stepName ?? ''}|${message}`
    if (blockerKeys.has(key)) return
    blockerKeys.add(key)
    blockers.push({ message, ...(employeeCode ? { employeeCode } : {}), ...(stepName ? { stepName } : {}) })
  }
  for (const issue of issues) if (issue.isBlocking) addBlocker(issue.message, issue.employeeCode, issue.stepName)
  for (const log of logs) if (failedLog(log)) addBlocker(log.errorMessage || `${log.stepName} failed.`, undefined, log.stepName)
  for (const result of diagnostics?.reconciliationResults ?? []) {
    if (['failed', 'error', 'blocked'].includes(result.status.toLowerCase())) {
      addBlocker(`${result.checkName}: reconciliation failed. Difference ${result.differenceAmount.toLocaleString('en-IN', { maximumFractionDigits: 2 })}.`, undefined, 'Reconciliation')
    }
  }
  if (run?.status === 'Failed') addBlocker(run.processingError || (blockers.length ? '' : 'Payroll calculation failed. Open the saved diagnostics.'), undefined, 'Payroll calculation')

  const hasResult = !!run && completedCalculation.has(run.status)
  const isRunning = !!run && running.has(run.status)
  const isLocked = !!run && (run.status === 'Pending Approval' || approvedCalculation.has(run.status))
  const isApproved = !!run && approvedCalculation.has(run.status)
  const canPrepare = input.clientId > 0 && input.policySelected && input.includedCount > 0
  if (!run) {
    if (!input.clientId) addBlocker('Select a client to prepare payroll.', undefined, 'Run scope')
    else if (!input.policySelected) addBlocker('Select an attendance policy for this client.', undefined, 'Run scope')
    else if (!input.includedCount) addBlocker('Select at least one employee.', undefined, 'Run scope')
    else if (!input.locationLoading && !input.attendanceReady) addBlocker(input.attendanceIssues > 0
      ? `Resolve ${input.attendanceIssues} attendance issue${input.attendanceIssues === 1 ? '' : 's'} before calculation.`
      : 'Review the selected employees’ attendance before calculation.', undefined, 'Attendance & leave')
  }

  const groupState = (numbers: number[], extraNames: string[] = []): RegularRunJourneyState => {
    const names = new Set(extraNames.map(name => name.toLowerCase()))
    const rows = logs.filter(row => numbers.includes(row.stepNumber))
    rows.forEach(row => names.add(row.stepName.toLowerCase()))
    const groupIssues = issues.filter(issue => names.has(issue.stepName.toLowerCase()))
    if (rows.some(failedLog) || groupIssues.some(issue => issue.isBlocking)) return 'blocked'
    if (rows.some(row => !!row.warning) || groupIssues.length) return 'warning'
    // These final reports are written together, not streamed while the worker calculates.
    if (!hasResult || !diagnostics || !numbers.every(number => rows.some(row => row.stepNumber === number && row.status === 'Success'))) return 'pending'
    return 'done'
  }
  const attendanceState = run ? groupState(range(5, 11), ['Attendance', 'Attendance & leave'])
    : input.locationLoading && canPrepare ? 'pending'
      : !canPrepare ? 'pending' : input.attendanceReady ? 'done' : 'blocked'
  const salaryState = groupState([3], ['Salary Structure Validation', 'Formula Validation'])
  const adjustmentState = groupState(range(13, 32))
  const validationNumbers = [...range(33, 51), 59]
  let validationState = groupState(validationNumbers, ['Payroll Validation', 'Reconciliation'])
  if (blockers.length && run) validationState = 'blocked'
  const scopeDetail = run
    ? `${run.clientName || 'Selected client'} · ${run.payPeriod} · ${run.employeeCount > 0 ? run.employeeCount + ' saved employees' : 'saved employee selection'} · Run #${run.id}`
    : canPrepare ? `${input.includedCount} employees · ${input.policyName} · ${input.workingDays}-day basis`
      : 'Choose client, policy and employees'
  const steps: RegularRunJourneyStep[] = [
    { id: 'scope', title: 'Payroll scope', detail: scopeDetail, state: run || canPrepare ? 'done' : 'blocked' },
    { id: 'attendance', title: 'Attendance & leave', detail: run
      ? attendanceState === 'done' ? 'Attendance and payable-day checks recorded for this calculation' : 'Saved calculation attendance checks'
      : input.locationLoading ? 'Loading selected attendance inputs' : input.attendanceReady ? 'Selected attendance inputs are ready' : 'Review payable days and attendance issues', state: attendanceState },
    { id: 'salary', title: 'Salary structures & formulas', detail: salaryState === 'done' ? 'Salary checks recorded for this calculation' : 'Validated by the payroll engine during calculation', state: salaryState },
    { id: 'adjustments', title: 'Variable pay & adjustments', detail: run ? 'Period earnings, arrears and recoveries in this calculation' : `${input.adjustmentCount} approved period adjustment${input.adjustmentCount === 1 ? '' : 's'} available for review`, state: adjustmentState },
    { id: 'calculation', title: 'Calculate payroll', detail: isRunning ? 'Engine is processing this saved run; awaiting its result' : hasResult ? `${run!.employeeCount} saved employee results` : run?.status === 'AwaitingProcessing' ? 'Review saved employee selection, then process this run' : 'Calculate earnings, deductions and net pay', state: isRunning ? 'active' : run?.status === 'Failed' ? 'blocked' : hasResult ? 'done' : 'pending' },
    { id: 'validation', title: 'PF, ESI, tax & reconciliation', detail: !diagnostics ? 'Saved validation results are not available yet' : validationState === 'blocked' ? `${blockers.length} blocking issue${blockers.length === 1 ? '' : 's'} need review` : validationState === 'warning' ? 'Calculation checks include warnings to review' : validationState === 'done' ? 'Statutory and reconciliation checks recorded' : 'Waiting for completed calculation checks', state: validationState },
    { id: 'lock', title: 'Review & lock payroll', detail: isLocked ? 'Payroll submitted for approval' : hasResult ? 'Review employee amounts before locking' : 'Available after calculation', state: isLocked ? 'done' : run?.status === 'Draft' ? 'active' : 'pending' },
    { id: 'approval', title: 'Approve payroll', detail: isApproved ? 'Payroll approved; payslips available in the register' : run?.status === 'Pending Approval' ? 'Waiting for the authorized approver' : 'Approval remains an explicit action', state: isApproved ? 'done' : run?.status === 'Pending Approval' ? 'active' : 'pending' },
  ]

  if (blockers.length) return { status: 'blocked', steps, title: 'Payroll needs attention', subtitle: `${blockers.length} blocker${blockers.length === 1 ? '' : 's'} to review`, blockers }
  if (isApproved) return { status: 'approved', steps, title: run!.status === 'Approved' ? 'Payroll approved' : `Payroll ${run!.status.toLowerCase()}`, subtitle: `${run!.employeeCount} employees · ${run!.payPeriod} · Run #${run!.id}`, blockers }
  if (isRunning) return { status: 'running', steps, title: run!.status === 'Queued' ? 'Payroll queued' : 'Calculating payroll', subtitle: 'The saved run refreshes automatically. Calculation progress is indeterminate.', blockers }
  if (run?.status === 'Pending Approval') return { status: 'approval', steps, title: 'Waiting for approval', subtitle: 'Payroll is locked. An authorized approver can review and approve.', blockers }
  if (run?.status === 'Draft') return { status: 'review', steps, title: 'Payroll ready for review', subtitle: 'Review employee amounts, then lock this payroll for approval.', blockers }
  if (run?.status === 'AwaitingProcessing') return { status: 'ready', steps, title: 'Draft ready to process', subtitle: 'Review the saved employee selection before calculating this run.', blockers }
  if (!run && input.locationLoading && canPrepare) return { status: 'ready', steps, title: 'Loading payroll inputs', subtitle: 'Loading saved inputs only. Use Run payroll to start calculation.', blockers }
  if (run) return { status: 'review', steps, title: 'Saved payroll run', subtitle: `Current state: ${run.status}`, blockers }
  return { status: 'ready', steps, title: 'Ready to run payroll', subtitle: `${input.includedCount} selected employees · ${input.workingDays}-day basis. Use Run payroll to start calculation.`, blockers }
}
