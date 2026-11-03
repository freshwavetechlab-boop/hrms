import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupPreview, PayrollSetupRequest, PayrollSetupResult } from '../services/excelPayrollSetupService'
import { extendClientPayrollConfiguration } from './excelPayrollReuseBasis'
import { attendanceProofBinding } from './excelPayrollPayableOnlyAttendance'

export type QuickPayrollPlan = { rowIds: string[]; request?: PayrollSetupRequest; preview: PayrollSetupPreview; requestId: string; completed?: PayrollSetupResult; error?: string; uncertain?: boolean }

// Preserve existing saved cohort boundaries so repeat imports update the same configuration.
export function quickPayrollGroups(context: PayrollSetupContext, rowIds: string[]) {
  const pending = new Set(rowIds), groups: string[][] = []
  if (!rowIds.length || pending.size !== rowIds.length || rowIds.some(id => !context.roster?.rows.some(row => row.rowId === id))) throw Error('Refresh the selected employee rows before preparing payroll.')
  for (const saved of context.appliedSetups || []) {
    const overlap = saved.filter(id => pending.has(id))
    if (!overlap.length) continue
    if (overlap.length !== saved.length || saved.length > 100) throw Error('This selection contains part of a saved configuration. Review that employee scope in Advanced setup.')
    groups.push([...saved]); overlap.forEach(id => pending.delete(id))
  }
  const remaining = rowIds.filter(id => pending.has(id))
  for (let start = 0; start < remaining.length; start += 100) groups.push(remaining.slice(start, start + 100))
  return groups
}

export function quickPayrollRequest(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[], includeNew: boolean, importAttendance?: boolean): PayrollSetupRequest {
  if (batch.clientId <= 0 || !rowIds.length || rowIds.length > 100) throw Error('Select a client and 1–100 employees per preparation group.')
  const basis = context.clientConfiguration?.savedRequest
  const request = extendClientPayrollConfiguration(batch, context, rowIds)
  if (!basis || !request) throw Error('First-time client setup or a changed Excel layout needs review. Save its setup once in Advanced setup; compatible later imports will reuse it.')
  if (context.quickPreparation?.version !== 1 || context.quickPreparation.month !== batch.month) throw Error('Quick preparation needs the updated API source and attendance checks. Advanced setup remains available.')
  const snapshot = context.quickPreparation
  if (!/^[a-f0-9]{64}$/i.test(snapshot.sourceHash) || !/^[a-f0-9]{64}$/i.test(snapshot.attendanceSnapshotHash)) throw Error('Refresh the saved source before preparing payroll.')
  if (!basis.templateScope?.reviewed || !basis.reviewedFixedInputs || !basis.reviewedPolicyScope) throw Error('Review the client’s source meanings and template applicability once in Advanced setup.')
  const attendance = importAttendance ?? basis.importAttendance === true
  if (basis.locationAllocations || basis.correctionOfImportId) throw Error('Review this saved location allocation or salary correction in Advanced setup.')
  if (basis.overrideSourcePayableAttendance) throw Error('The saved setup uses a source-payable salary override. Review that period-specific change in Advanced setup; quick preparation keeps recorded attendance.')
  if (basis.importPayableOnlyAttendance && attendance) throw Error('Actual Present days were not supplied in the saved payable-only setup. Review that attendance choice in Advanced setup, or keep recorded attendance.')
  if (basis.reviewedExtraDutyUnits && attendance) throw Error('These special payable units include extra duty, not actual Present days. Keep recorded attendance to reuse the salary rules, or review attendance in Advanced setup.')
  if (request.components.some(component => !component.existingComponentId)) throw Error('New salary components need review in Advanced setup before the saved configuration can be reused.')
  const meaning = basis.attendanceReview
  if (!meaning?.reviewed || meaning.payableMeaning !== 'AlreadyPayable' || meaning.presentMeaning !== (request.presentDaysColumnIndex == null ? 'NotMapped' : 'ActualWorked'))
    throw Error('Confirm the actual Present and Payable column meanings once in Advanced setup.')
  if (attendance && request.presentDaysColumnIndex == null) throw Error('Map actual Present days before importing monthly attendance. Payable days will not be used as Present days.')
  const employees = context.roster?.rows || []
  request.employeeDecisions = rowIds.map(rowId => {
    const row = employees.find(item => item.rowId === rowId)
    if (!row || row.status === 'Review' || (row.status !== 'New' && !row.employeeId)) throw Error('Some employee identities need review in Advanced setup.')
    if (row.status === 'New' && !includeNew) throw Error('Review and include the new employees shown in the preview, or configure them separately.')
    return { rowId, matchedEmployeeId: row.employeeId ?? undefined, allowCreate: row.status === 'New' && includeNew }
  })
  // These are candidates for the visible preview. Only the final explicit button
  // submits them; no stored source hash, rounding approval or request ID is copied.
  request.quickPreparation = { version: 1, sourceHash: snapshot.sourceHash, attendanceSnapshotHash: snapshot.attendanceSnapshotHash }
  request.importAttendance = attendance
  request.importPayableOnlyAttendance = false
  // Previously reviewed salary-unit meaning can be proposed again while keeping
  // native attendance. The core still verifies this source; no presence is inferred.
  request.reviewedExtraDutyUnits = basis.reviewedExtraDutyUnits === true
  request.reviewedFixedInputs = true; request.reviewedPolicyScope = true
  request.templateScope = { ...request.templateScope!, reviewed: true }
  request.attendanceReview = { ...meaning, mode: attendance ? 'SourceSeed' : meaning.mode, sourceHash: snapshot.sourceHash, sourceMonth: batch.month,
    cycleStart: snapshot.cycleStart, cycleEnd: snapshot.cycleEnd, reviewed: true, boundSourceInputs: undefined }
  request.attendanceReview.boundSourceInputs = attendanceProofBinding(request)
  request.templateDecisions = []; request.roundingReviews = {}
  return request
}

export function selectQuickPayrollTemplates(request: PayrollSetupRequest, preview: PayrollSetupPreview): PayrollSetupRequest {
  if (!preview.transfer || preview.transfer.sourceHash !== request.quickPreparation?.sourceHash) throw Error('The source changed while preparing this preview. Refresh it before saving.')
  const templateDecisions = preview.transfer.templates.map(template => {
    const exact = template.candidates.filter(candidate => candidate.exact)
    const assigned = [...new Set(template.existingResults.map(row => row.templateId))]
    const match = exact.length === 1 ? exact[0] : exact.find(candidate => assigned.length === 1 && candidate.id === assigned[0])
    if (!match) throw Error('Salary rules or template applicability changed. Review the template comparison in Advanced setup; another template will not be created automatically.')
    return { variantKey: template.variantKey, existingTemplateId: match.id, createVariant: false }
  })
  if (!templateDecisions.length) throw Error('No reusable salary template was found. Review this source in Advanced setup.')
  return { ...request, templateDecisions }
}

export function quickAttendanceConflict(context: PayrollSetupContext, request: PayrollSetupRequest, preview: PayrollSetupPreview) {
  if (!request.importAttendance) return ''
  if (context.quickPreparation?.dailyEmployeeIds.length) return 'Dated attendance exists for this cycle. Review it in Advanced setup; monthly totals will not replace it.'
  for (const row of preview.rows) {
    const id = request.employeeDecisions.find(decision => decision.rowId === row.rowId)?.matchedEmployeeId
    const saved = context.quickPreparation?.monthlyAttendance.find(item => item.employeeId === id)
    if (saved && (saved.workingDays !== row.payrollDays || saved.presentDays !== row.presentDays || saved.payableDays !== row.payableDays))
      return 'Existing monthly attendance differs from this Excel. Review the difference in Advanced setup before replacing it.'
  }
  return ''
}

// Ignore only the concurrency hash. A changed source, amount, employee, template,
// policy, attendance proof or warning pauses the queue for a fresh visible review.
export function sameQuickPayrollPreview(before: PayrollSetupPreview, after: PayrollSetupPreview) {
  const withoutHash = ({ planHash: _hash, ...preview }: PayrollSetupPreview) => ({ ...preview, transfer: preview.transfer ? {
    ...preview.transfer, templates: preview.transfer.templates.map(({ candidates: _candidates, ...template }) => template)
  } : preview.transfer })
  return JSON.stringify(withoutHash(before)) === JSON.stringify(withoutHash(after))
}

export function completedQuickPayroll(context: PayrollSetupContext, rowIds: string[], requestId?: string) {
  const saved = context.lastImport
  return saved?.status === 'Applied' && !saved.sourceChanged && saved.result && (!requestId || saved.requestId === requestId)
    && [...saved.savedRequest.rowIds].sort().join('|') === [...rowIds].sort().join('|') ? saved.result : null
}
