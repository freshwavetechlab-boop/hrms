import { getJsonResult, postJson } from './apiClient'
import type { PayRun } from '../types/payroll'
import type { CreatePayRunRequest } from './payrollService'
import type { AttendanceInputEvidence } from './payrollCalculationService'

export type SelectedPayRunProcessingPreview = {
  payRunId: number; clientId: number; clientName: string; payPeriod: string; runCode: string
  status: string; employeeCount: number; fingerprint: string; canProcess: boolean; blockers: string[]; effects: string[]
  employees: { employeeId: number; employeeCode: string; employeeName: string }[]
  adjustments?: SelectedPayRunAdjustmentReview[]
}
export type SelectedAppliedSourceWitness = { kind: 'ReviewedAppliedSourceV1'; historicalTransferVersion: 0; originalReceiptFingerprint: string; sourceHash: string; mappingFingerprint: string; identityFingerprint: string; sourceFormulaFingerprint: string; nativeInputFingerprint: string; salaryPolicyFingerprint: string; coreCalculationFingerprint: string; comparisonFingerprint: string; witnessFingerprint: string }
export type SelectedPayRunAdjustmentReview = { adjustmentId: number; employeeId: number; componentCode: string; category: string; amount: number; taxable: boolean; sourceBatchId: string; sourceImportId: string; sourceRowId: string; sourceRow: number; sourceHash: string; receiptFingerprint: string; businessFingerprint: string; sourceWitness?: SelectedAppliedSourceWitness }
export type SelectedSourceFinancialLine = { code: string; category: string; componentRole: string; statutoryType: string; taxable: boolean; proRata: boolean; sourceFormula: string; nativeFormula: string; sourceRaw: number; sourceDisplay: number; coreAmount: number; coreUnrounded: number | null; formulaStatus: string }
export type SelectedDraftSourceReviewPreview = { kind: 'ReviewedAppliedSourceV1'; historicalTransferVersion: 0; canConfirm: boolean; fingerprint: string; blockers: string[]; reviews: SelectedPayRunAdjustmentReview[]; lines: SelectedSourceFinancialLine[]; nativeAttendance: AttendanceInputEvidence | null; sourceActualWorkedMeaning: 'NotMapped' | 'ActualWorked'; sourceNet: number | null; coreNet: number | null }
const digest = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value)
const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value)
function sourceWitnessError(review: SelectedPayRunAdjustmentReview) {
  const witness = review.sourceWitness
  return witness !== undefined && (!witness || witness.kind !== 'ReviewedAppliedSourceV1' || witness.historicalTransferVersion !== 0
    || [witness.originalReceiptFingerprint, witness.sourceHash, witness.mappingFingerprint, witness.identityFingerprint, witness.sourceFormulaFingerprint, witness.nativeInputFingerprint, witness.salaryPolicyFingerprint, witness.coreCalculationFingerprint, witness.comparisonFingerprint, witness.witnessFingerprint].some(value => !digest(value))
    || witness.sourceHash !== review.sourceHash || witness.originalReceiptFingerprint !== review.receiptFingerprint)
}
export function selectedAdjustmentPreviewError(preview: SelectedPayRunProcessingPreview) {
  const rows = preview.adjustments ?? [], ids = rows.map(row => row.adjustmentId)
  if (ids.length !== new Set(ids).size || rows.some(row => !Number.isSafeInteger(row.adjustmentId) || row.adjustmentId <= 0 || !preview.employees.some(employee => employee.employeeId === row.employeeId)
    || !row.componentCode?.trim() || !['Earning', 'Reimbursement', 'Deduction'].includes(row.category) || !Number.isFinite(row.amount) || row.amount <= 0 || typeof row.taxable !== 'boolean'
    || !row.sourceBatchId || !row.sourceImportId || !row.sourceRowId || !Number.isSafeInteger(row.sourceRow) || row.sourceRow <= 0
    || [row.sourceHash, row.receiptFingerprint, row.businessFingerprint].some(value => !digest(value)) || sourceWitnessError(row))) return 'Adjustment review does not match the selected employees or complete saved-source evidence. Refresh this exact run before processing.'
  return ''
}
export function selectedDraftSourcePreviewError(preview: SelectedDraftSourceReviewPreview, request: CreatePayRunRequest, adjustment: { id: number; employeeId: number; componentCode: string; adjustmentType: string; amount: number; taxable: boolean } | undefined) {
  const message = 'Current-source review does not match this exact employee, adjustment, period or complete server evidence. Review the current selection again.'
  if (preview.kind !== 'ReviewedAppliedSourceV1' || preview.historicalTransferVersion !== 0 || typeof preview.canConfirm !== 'boolean'
    || !Array.isArray(preview.blockers) || preview.blockers.some(value => typeof value !== 'string') || !Array.isArray(preview.reviews) || !Array.isArray(preview.lines)) return message
  // A blocked server response may have no evidence/fingerprint. It remains visible but can never be acknowledged.
  if (!preview.canConfirm) return preview.blockers.length ? '' : message
  const row = preview.reviews[0], native = preview.nativeAttendance
  if (preview.blockers.length || !digest(preview.fingerprint) || preview.reviews.length !== 1 || !adjustment || !row
    || request.includedEmployeeIds?.length !== 1 || request.adjustmentIds?.length !== 1 || request.includedEmployeeIds[0] !== row.employeeId || request.adjustmentIds[0] !== row.adjustmentId
    || adjustment.id !== row.adjustmentId || adjustment.employeeId !== row.employeeId || adjustment.componentCode !== row.componentCode || adjustment.adjustmentType !== row.category || adjustment.amount !== row.amount || adjustment.taxable !== row.taxable
    || selectedAdjustmentPreviewError({ employees: [{ employeeId: row.employeeId }], adjustments: [row] } as SelectedPayRunProcessingPreview)
    || !row.sourceWitness || row.sourceWitness.witnessFingerprint !== preview.fingerprint
    || !preview.lines.length || preview.lines.some(line => !line || typeof line.code !== 'string' || !line.code.trim() || !['Earning', 'Reimbursement', 'Deduction', 'Benefit'].includes(line.category)
      || !line.componentRole?.trim() || !line.statutoryType?.trim() || typeof line.taxable !== 'boolean' || typeof line.proRata !== 'boolean' || typeof line.sourceFormula !== 'string' || typeof line.nativeFormula !== 'string'
      || ![line.sourceRaw, line.sourceDisplay, line.coreAmount].every(finite) || line.coreUnrounded !== null && !finite(line.coreUnrounded) || !line.formulaStatus?.trim())
    || new Set(preview.lines.map(line => line.code.toUpperCase())).size !== preview.lines.length
    || !finite(preview.sourceNet) || !finite(preview.coreNet) || preview.sourceNet < 0 || preview.coreNet < 0
    || !['NotMapped', 'ActualWorked'].includes(preview.sourceActualWorkedMeaning) || !native || native.payPeriod !== request.payPeriod || native.temporaryDayOverrides !== false || native.policyValidationError !== ''
    || !native.sourceKind?.trim() || !finite(native.workingDays) || native.workingDays !== request.totalWorkingDays || native.workingDays <= 0 || native.workingDays > 31
    || !finite(native.payableDays) || native.payableDays < 0 || native.payableDays > native.workingDays || native.presentDays !== null && (!finite(native.presentDays) || native.presentDays < 0 || native.presentDays > native.workingDays)
    || native.actualPresenceKnown !== undefined && native.actualPresenceKnown !== (native.presentDays !== null) || preview.sourceActualWorkedMeaning === 'ActualWorked' && native.presentDays === null) return message
  return ''
}
export const reviewSelectedPayRunDraftSource = (request: CreatePayRunRequest) =>
  postJson<CreatePayRunRequest, SelectedDraftSourceReviewPreview | null>('/api/pay-runs/selected-draft-source-review', request, null, { timeoutMs: 120000, toast: false })
export const getSelectedPayRunProcessingPreview = (id: number, clientId: number, payPeriod: string) =>
  getJsonResult<SelectedPayRunProcessingPreview | null>(`/api/pay-runs/${id}/processing-preview?${new URLSearchParams({ clientId: String(clientId), payPeriod })}`, null, { toast: false })
// A selected 100-employee calculation can finish after the old three-minute response window.
// Keep this wait scoped to processing; timeout handling still consumes the preview and requires explicit state review.
export const processSelectedPayRun = (id: number, body: { clientId: number; payPeriod: string; expectedFingerprint: string; confirmed: boolean }) =>
  postJson<typeof body, PayRun | null>(`/api/pay-runs/${id}/process-selected`, body, null, { timeoutMs: 300000, toast: false, timeoutMessage: 'Processing response timed out. Check the current run before retrying.' })
