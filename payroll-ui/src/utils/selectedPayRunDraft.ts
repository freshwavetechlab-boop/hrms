import type { AuthUser, PayrollAdjustment, PayRun } from '../types/payroll'
import type { CreatePayRunRequest } from '../services/payrollService'

export const canCreateSelectedPayRunDraft = (user: Pick<AuthUser, 'isActive' | 'clientId' | 'roles' | 'permissions'> | null | undefined) => user?.isActive === true && user.clientId == null && user.roles.includes('super_admin') && user.permissions.includes('payroll.run')
type Selection = { clientId: number; period: string; workingDays: number; policyBatchId: string; policyLabel: string; groupIds: number[]; employeeIds: number[]; eligibleEmployeeIds: number[]; adjustments: PayrollAdjustment[]; selectedAdjustmentIds?: number[] }
const uniquePositive = (values: number[]) => values.length > 0 && values.length === new Set(values).size && values.every(value => Number.isSafeInteger(value) && value > 0)
type AdjustmentScope = Pick<Selection, 'clientId' | 'period' | 'policyBatchId' | 'employeeIds' | 'adjustments'>
export const selectedDraftApplicableAdjustments = (scope: AdjustmentScope) => scope.adjustments.filter(item => item.clientId === scope.clientId && item.payPeriod === scope.period && item.status === 'Approved' && item.payRunType !== 'Off Cycle' && scope.employeeIds.includes(item.employeeId))
export function selectedDraftAdjustmentIssue(item: PayrollAdjustment) {
  return !Number.isSafeInteger(item.id) || item.id <= 0 || item.status !== 'Approved' || item.payRunType !== 'Regular' || item.payRunId != null
    || item.reasonCode !== 'EXCEL_SETUP' || !item.componentCode?.trim() || !['Earning', 'Reimbursement', 'Deduction'].includes(item.adjustmentType)
    || !Number.isFinite(item.amount) || item.amount <= 0 || typeof item.taxable !== 'boolean'
    ? 'Requires an existing approved, unbound Regular adjustment with reviewed saved-Excel provenance.' : ''
}
export function selectedDraftAdjustmentReviewKey(scope: AdjustmentScope) {
  return JSON.stringify({ clientId: scope.clientId, period: scope.period, policyBatchId: scope.policyBatchId, employeeIds: [...scope.employeeIds].sort((a, b) => a - b),
    adjustments: selectedDraftApplicableAdjustments(scope).map(item => ({ id: item.id, clientId: item.clientId, employeeId: item.employeeId, payPeriod: item.payPeriod, payRunType: item.payRunType, status: item.status, payRunId: item.payRunId ?? null, componentCode: item.componentCode, adjustmentType: item.adjustmentType, amount: item.amount, taxable: item.taxable, reasonCode: item.reasonCode, notes: item.notes })).sort((a, b) => a.id - b.id) })
}
export function selectedPayRunDraftRequest(scope: Selection): { request: CreatePayRunRequest | null; error: string } {
  if (!Number.isSafeInteger(scope.clientId) || scope.clientId <= 0 || !/^\d{4}-(0[1-9]|1[0-2])$/.test(scope.period) || !scope.policyBatchId.trim() || !uniquePositive(scope.groupIds)) return { request: null, error: 'Select the exact client, payroll month and attendance policy before creating a selected draft.' }
  if (!uniquePositive(scope.employeeIds) || scope.employeeIds.some(id => !scope.eligibleEmployeeIds.includes(id))) return { request: null, error: 'Select a unique set of active employees inside the current attendance policy.' }
  if (!Number.isFinite(scope.workingDays) || scope.workingDays <= 0 || scope.workingDays > 31) return { request: null, error: 'Review the current payroll divisor before creating a selected draft.' }
  const applicable = selectedDraftApplicableAdjustments(scope), selected = scope.selectedAdjustmentIds ?? []
  if (selected.length && (!uniquePositive(selected) || selected.some(id => applicable.filter(item => item.id === id).length !== 1))) return { request: null, error: 'Selected adjustment IDs are outside the current employee, client, month or approved scope. Refresh and review them again.' }
  if (applicable.some(item => selectedDraftAdjustmentIssue(item))) return { request: null, error: 'An approved adjustment needs saved-source review. Selected drafts accept only existing approved, unbound Regular Excel source adjustments.' }
  if (applicable.length !== new Set(applicable.map(item => item.id)).size || applicable.length !== selected.length || applicable.some(item => !selected.includes(item.id))) return { request: null, error: 'Explicitly review and select every existing approved Regular adjustment for these selected employees. Nothing is selected automatically.' }
  return { request: { clientId: scope.clientId, payPeriod: scope.period, payDate: `${scope.period}-28`, totalWorkingDays: scope.workingDays, runType: 'Regular', runName: `Selected draft - ${scope.policyLabel}`, reason: scope.policyLabel, attendanceGroupId: scope.groupIds[0], attendanceGroupIds: [...scope.groupIds], attendancePolicyBatchId: scope.policyBatchId, attendanceGroupName: scope.policyLabel, includedEmployeeIds: [...scope.employeeIds], excludedEmployeeIds: [], adjustmentIds: [...selected].sort((a, b) => a - b) }, error: '' }
}
export function matchesSelectedPayRunDraftResponse(run: PayRun | null, request: CreatePayRunRequest) {
  return !!run && Number.isSafeInteger(run.id) && run.id > 0 && run.clientId === request.clientId && run.payPeriod === request.payPeriod && run.runType === 'Regular' && run.status === 'AwaitingProcessing'
}

export type SelectedDraftSourceReviewChoice = { requestKey: string; started: boolean; acknowledgement?: { expectedFingerprint: string; confirmed: true } }
// Include the local business snapshot as well as the exact server command. No source values are invented or submitted.
export const selectedDraftSourceReviewKey = (request: CreatePayRunRequest | null, businessKey: string) => JSON.stringify({ request, businessKey })
export function withSelectedDraftSourceReview(request: CreatePayRunRequest | null, requestKey: string, choice?: SelectedDraftSourceReviewChoice): { request: CreatePayRunRequest | null; error: string } {
  if (!request) return { request: null, error: '' }
  const current = { ...request }
  delete current.adjustmentSourceReviewAcknowledgement
  if (!choice || choice.requestKey !== requestKey || !choice.started) return { request: current, error: '' }
  const acknowledgement = choice.acknowledgement
  if (!acknowledgement?.confirmed || !/^[a-f0-9]{64}$/i.test(acknowledgement.expectedFingerprint)) return { request: null, error: 'Review and acknowledge the current saved-source evidence before creating this selected draft.' }
  if (request.includedEmployeeIds?.length !== 1 || request.adjustmentIds?.length !== 1) return { request: null, error: 'Current-source review belongs to one exact employee and one existing adjustment.' }
  return { request: { ...current, adjustmentSourceReviewAcknowledgement: { expectedFingerprint: acknowledgement.expectedFingerprint, confirmed: true } }, error: '' }
}
