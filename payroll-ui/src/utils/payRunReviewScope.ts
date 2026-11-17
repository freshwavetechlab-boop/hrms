import type { PayRun } from '../types/payroll'

type ReviewRun = Pick<PayRun, 'clientId' | 'status' | 'runType' | 'runCode' | 'attendancePolicyBatchId'>
type ReviewScope = {
  clientId: number
  offCycle: boolean
  policyBatchId?: string
  regularRunCode: string
  legacyRegularRunCode: string
}

const reviewStatuses = new Set(['AwaitingProcessing', 'Draft', 'Queued', 'Processing', 'Failed', 'Pending Approval', 'Approved'])

export function matchesPayRunReviewScope(run: ReviewRun, scope: ReviewScope): boolean {
  if (run.clientId !== scope.clientId || !reviewStatuses.has(run.status)) return false
  if (scope.offCycle) return run.runType === 'Off Cycle'
  if (run.runType === 'Off Cycle') return false

  // Older regular drafts predate policy binding; replacing a policy must not hide them from review.
  if (!run.attendancePolicyBatchId && /^REGULAR(?:-E-[A-F0-9]{8})$/i.test(run.runCode)) return true

  return scope.policyBatchId
    ? (run.attendancePolicyBatchId
      ? run.attendancePolicyBatchId === scope.policyBatchId
      : [scope.regularRunCode, scope.legacyRegularRunCode].includes(run.runCode))
    : run.runCode === scope.regularRunCode
}
