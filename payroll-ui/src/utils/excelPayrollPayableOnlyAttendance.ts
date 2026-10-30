import type { PayrollSetupRequest, TransferReview } from '../services/excelPayrollSetupService'

export function attendanceProofBinding(request: PayrollSetupRequest) {
  const review = request.attendanceReview
  return [review?.mode || '', request.payrollDaysColumnIndex, request.fixedPayrollDays ?? '-', request.payableDaysColumnIndex, request.presentDaysColumnIndex ?? '-', request.attendanceCycleStartDay, request.attendanceCycleEndDay, request.importAttendance !== false ? '1' : '0'].join('|')
    + (request.payrollDaysCell ? '|cell:' + request.payrollDaysCell.replace(/\$/g, '').trim().toUpperCase() : '')
    + (request.importPayableOnlyAttendance === true ? '|payableOnly:1' : '')
    + (request.overrideSourcePayableAttendance === true ? '|sourcePayableOverride:1' : '')
}

const validDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(Date.parse(value + 'T00:00:00Z')) && new Date(value + 'T00:00:00Z').toISOString().slice(0, 10) === value
export function payableOnlyAttendanceEligibility(request: PayrollSetupRequest, transfer: TransferReview) {
  const review = request.attendanceReview, reasons: string[] = []
  if (request.importAttendance === false) reasons.push('Turn on source-month attendance import to choose a reviewed payable-only source seed.')
  if (request.presentDaysColumnIndex != null) reasons.push('An actual present column is mapped; use its reviewed values instead of a payable-only seed.')
  if (!Number.isSafeInteger(request.payableDaysColumnIndex) || request.payableDaysColumnIndex < 0) reasons.push('Select the actual source payable-days column.')
  if (!review || review.mode !== 'SourceSeed' || review.payableMeaning !== 'AlreadyPayable' || review.presentMeaning !== 'NotMapped') reasons.push('Review Source seed, Already payable, and Actual worked days not mapped before choosing this import.')
  if (!review || !/^[a-f0-9]{64}$/i.test(review.sourceHash) || review.sourceHash !== transfer.sourceHash || review.sourceMonth !== transfer.sourceMonth || !/^\d{4}-(0[1-9]|1[0-2])$/.test(review.sourceMonth)) reasons.push('Attendance review must match this exact saved source snapshot and payroll month.')
  if (!review || !validDate(review.cycleStart) || !validDate(review.cycleEnd) || review.cycleStart > review.cycleEnd) reasons.push('Review the exact source attendance cycle start and end.')
  if (request.components.some(component => [component.formula, ...Object.values(component.rowFormulas || {})].some(formula => /\bPRESENT_DAYS\b/i.test(formula || '')))) reasons.push('A salary formula uses PRESENT_DAYS. Supply actual present attendance; payable days cannot replace it.')
  return { eligible: reasons.length === 0, reasons }
}

export function payableOnlyAttendanceChoice(request: PayrollSetupRequest, checked: boolean): Partial<PayrollSetupRequest> {
  return { importPayableOnlyAttendance: checked, ...(request.attendanceReview ? { attendanceReview: { ...request.attendanceReview, reviewed: false, boundSourceInputs: undefined } } : {}) }
}
