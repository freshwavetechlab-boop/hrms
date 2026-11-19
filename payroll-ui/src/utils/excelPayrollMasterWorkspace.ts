import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupPreview, PayrollSetupRequest, TemplateApplicability, TransferTemplateDecision } from '../services/excelPayrollSetupService'
import { attendanceProofBinding } from './excelPayrollPayableOnlyAttendance'

const normalized = (value: string) => value.trim().toLocaleLowerCase().replace(/\s+/g, ' ')
export const sourceMonthEnd = (month: string) => `${month}-${new Date(Number(month.slice(0, 4)), Number(month.slice(5, 7)), 0).getDate()}`

// These are visible proposals, not approvals. Only the explicit Check & preview
// action acknowledges them; Apply still uses the server's source-bound plan.
export function masterWorkspaceDefaults(batch: ExcelPayslipBatch, context: PayrollSetupContext, request: PayrollSetupRequest): PayrollSetupRequest {
  if (request.locationAllocations || request.correctionOfImportId) return request
  const employeeDecisions = request.employeeDecisions.map(decision => {
    const row = context.roster?.rows.find(row => row.rowId === decision.rowId)
    if (decision.matchedEmployeeId && !decision.allowCreate && context.employees.some(employee => employee.id === decision.matchedEmployeeId)) return decision
    if (row?.status === 'Review') return { ...decision, matchedEmployeeId: undefined, allowCreate: false }
    if (row?.employeeId && ['Existing', 'Applied'].includes(row.status)) return { ...decision, matchedEmployeeId: row.employeeId, allowCreate: false }
    return row?.status === 'New' ? { ...decision, allowCreate: true } : decision
  })
  const locationNames = [...new Set(batch.rows.filter(row => request.rowIds.includes(row.id)).map(row => {
    if (request.sourceWorkLocationColumnIndex != null) {
      const cells = context.source.rows.filter(source => source.sourceRow === row.sourceRow)
      const cell = cells.length === 1 ? cells[0].cells[request.sourceWorkLocationColumnIndex] : undefined
      if (cell && !cell.error && !cell.missingCachedValue && !cell.calculationError) return cell.value.trim()
      return ''
    }
    const match = employeeDecisions.find(decision => decision.rowId === row.id)?.matchedEmployeeId
    const employee = context.employees.find(employee => employee.id === match)
    return context.workLocations.find(location => location.id === (request.defaultWorkLocationId || employee?.workLocationId))?.name || employee?.workLocation || ''
  }).filter(name => !!name && !/^(?:n\/?a|nil|none|0|-+)$/i.test(name)))]
  const ids: number[] = [], names: string[] = []
  for (const name of locationNames) {
    const matches = context.workLocations.filter(location => normalized(location.name) === normalized(name))
    if (matches.length === 1) ids.push(matches[0].id)
    else if (!matches.length && request.createMissingWorkLocations) names.push(name)
  }
  const monthDays = Number(sourceMonthEnd(batch.month).slice(-2))
  const calendarColumn = request.payrollDaysColumnIndex >= 0 && batch.rows.filter(row => request.rowIds.includes(row.id)).every(row => {
    const source = context.source.rows.filter(source => source.sourceRow === row.sourceRow)
    const cell = source.length === 1 ? source[0].cells[request.payrollDaysColumnIndex] : undefined
    return !!cell && !cell.error && !cell.missingCachedValue && !cell.calculationError && Number(cell.value) === monthDays
  })
  const continuingSavedSelection = context.lastImport?.savedRequest.batchId === batch.id
    && [...context.lastImport.savedRequest.rowIds].sort().join('|') === [...request.rowIds].sort().join('|')
  const continuingNewMonth = !continuingSavedSelection && !!context.clientConfiguration && context.clientConfiguration.month !== batch.month
  const scope: TemplateApplicability = request.templateScope ? {
    ...request.templateScope,
    ...(continuingNewMonth ? { reviewed: false, ...(!request.templateScope.openEnded ? {effectiveTo: sourceMonthEnd(batch.month)} : {}),
      ...(ids.length + names.length ? {locationMode:'Listed' as const,workLocationIds:[...new Set(ids)],newLocationNames:names} : {}) } : {}),
  } : {
    reviewed: false, effectiveTo: sourceMonthEnd(batch.month), openEnded: false,
    locationMode: ids.length + names.length ? 'Listed' : '', workLocationIds: [...new Set(ids)], newLocationNames: names,
    categoryKind: 'AllClientCategories', categories: [],
    divisorMeaning: request.fixedPayrollDays === monthDays || calendarColumn ? 'CalendarDays' : request.fixedPayrollDays ? 'ContractFixedDays' : '',
    payableMeaning: 'AlreadyPayable', policyBasis: 'SavedSourceRules', policyVersion: '',
  }
  // Without a reviewed saved choice, leave the native attendance ledger alone.
  // A salary worksheet's payable units are never fabricated as daily presence.
  const knownWorkWeek = context.workWeeks.find(week => normalized(week.value) === normalized(request.workWeek))?.value
    || (continuingSavedSelection ? request.workWeek : '') || context.defaults?.workWeek
  return { ...request, employeeDecisions, templateScopeReviewVersion: 1, templateScope: scope, workWeek: knownWorkWeek || '', importAttendance: request.importAttendance ?? false }
}

export function masterWorkspaceTemplateDecisions(request: PayrollSetupRequest, preview: PayrollSetupPreview): TransferTemplateDecision[] {
  return (preview.transfer?.templates || []).flatMap(template => {
    const previous = request.templateDecisions?.find(decision => decision.variantKey === template.variantKey)
    if (previous) return [previous]
    const exact = template.candidates.filter(candidate => candidate.exact)
    const sameRules = template.candidates.filter(candidate => candidate.configurationExact && candidate.scopeStatus !== 'Different policy/basis')
    const candidates = exact.length ? exact : sameRules
    if (candidates.length === 1) {
      const candidate = candidates[0]
      return [{ variantKey: template.variantKey, existingTemplateId: candidate.id, createVariant: false,
        ...(candidate.exact ? {} : { scopeAction: candidate.recordedScopes?.length ? 'ExtendReviewedScope' : 'AttestLegacyScope' }) }]
    }
    if (!candidates.length && !template.existingResults?.length) return [{ variantKey: template.variantKey, existingTemplateId: '', createVariant: true }]
    return [] // Existing salary conflicts and several possible templates require a choice.
  })
}

export function reviewedMasterWorkspace(request: PayrollSetupRequest, context: PayrollSetupContext, batch: ExcelPayslipBatch, preview: PayrollSetupPreview): PayrollSetupRequest {
  if (request.batchId !== batch.id || !preview.transfer || preview.transfer.sourceBatchId !== request.batchId || preview.transfer.sourceMonth !== batch.month || !/^[a-f0-9]{64}$/i.test(preview.transfer.sourceHash)
    || context.quickPreparation && (context.quickPreparation.month !== batch.month || context.quickPreparation.sourceHash !== preview.transfer.sourceHash)) throw Error('The saved Excel changed or its source is unavailable. Refresh the setup before checking it.')
  const calendarCycle = request.attendanceCycleStartDay === 1 && request.attendanceCycleEndDay === Number(sourceMonthEnd(batch.month).slice(-2))
  const cycleStart = context.quickPreparation?.cycleStart || (calendarCycle ? `${batch.month}-01` : '')
  const cycleEnd = context.quickPreparation?.cycleEnd || (calendarCycle ? sourceMonthEnd(batch.month) : '')
  const prior = request.attendanceReview
  // Keep unclear day meanings visibly unresolved. The single acknowledgement
  // replaces repeated checkboxes; it does not select a missing source meaning.
  const review = prior || { mode: 'SourceSeed' as const, sourceHash: preview.transfer.sourceHash, sourceMonth: batch.month,
    cycleStart, cycleEnd, payableMeaning: 'AlreadyPayable' as const,
    presentMeaning: request.presentDaysColumnIndex == null ? 'NotMapped' as const : 'ActualWorked' as const,
    lwpBasis: '' as const, reviewed: false }
  const next: PayrollSetupRequest = { ...request, reviewedFixedInputs: true, reviewedPolicyScope: true,
    templateScope: request.templateScope ? { ...request.templateScope, reviewed: true } : undefined,
    attendanceReview: { ...review, sourceHash: preview.transfer.sourceHash, sourceMonth: batch.month, reviewed: true },
    templateDecisions: masterWorkspaceTemplateDecisions(request, preview) }
  next.attendanceReview!.boundSourceInputs = attendanceProofBinding(next)
  return next
}

export function masterWorkspaceIssues(preview: PayrollSetupPreview) {
  const groups = new Map<string, { severity: string; message: string; rowIds: string[]; count: number }>()
  for (const issue of preview.issues) {
    const key = `${issue.severity}:${issue.code || ''}:${issue.message}`
    const group = groups.get(key) || { severity: issue.severity, message: issue.message, rowIds: [], count: 0 }
    group.count++; if (issue.rowId) group.rowIds.push(issue.rowId)
    groups.set(key, group)
  }
  return [...groups.values()]
}
