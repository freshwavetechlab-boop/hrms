import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { proposeExcelPayrollSetup, restoreExcelPayrollMasterMetadata } from './excelPayrollSetup'

// A client configuration is a starting basis, not another month's approvals or
// copied constants. New formulas are always translated from the current source.
export function extendClientPayrollConfiguration(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[]): PayrollSetupRequest | null {
  const saved = context.clientConfiguration
  if (!saved || saved.clientId !== batch.clientId || saved.savedRequest.locationAllocations || saved.savedRequest.correctionOfImportId) return null
  const columns = [...context.source.columns].sort((a,b)=>a.columnIndex-b.columnIndex)
  const previousColumns = [...saved.sourceColumns].sort((a,b)=>a.columnIndex-b.columnIndex)
  const header = (text: string) => text.replace(/[^a-z0-9]/gi,'').toUpperCase()
  if (columns.length !== previousColumns.length || columns.some((column,i)=>column.columnIndex!==previousColumns[i].columnIndex
    || header(column.sourceHeader)!==header(previousColumns[i].sourceHeader) || column.kind!==previousColumns[i].kind
    || !!column.blankAsZero!==!!previousColumns[i].blankAsZero || column.componentId!==previousColumns[i].componentId)) return null
  const previous = saved.savedRequest
  const proposed = proposeExcelPayrollSetup(batch, context, rowIds, previous.sourceColumnRoles).request
  const fields: (keyof PayrollSetupRequest)[] = ['templateName','payrollDaysColumnIndex','fixedPayrollDays','payrollDaysCell','monthlyCtcColumnIndex','payableDaysColumnIndex','presentDaysColumnIndex','workWeek','attendanceCycleStartDay','attendanceCycleEndDay','payrollReportGenerationDay','defaultDepartment','defaultDesignation','defaultGrade','defaultWorkLocationId','defaultWorkLocationName','defaultWorkLocationState','sourceWorkLocationColumnIndex','preserveExistingAttendancePolicies']
  const basis = Object.fromEntries(fields.map(key=>[key,previous[key]]))
  const components = proposed.components.map(component=>{
    const prior=previous.components.find(item=>item.expectedColumnIndex===component.expectedColumnIndex && item.code===component.code)
    if (!prior || component.existingComponentId && prior.existingComponentId && component.existingComponentId!==prior.existingComponentId) return component
    const financialFormula = component.expectedColumnIndex != null && batch.rows.filter(row=>rowIds.includes(row.id)).some(row=>context.source.rows.find(item=>item.sourceRow===row.sourceRow)?.cells[component.expectedColumnIndex!]?.formulaText)
    return {...component,existingComponentId:component.existingComponentId || prior.existingComponentId,
      ...(!financialFormula && prior.isPeriodAdjustment ? {isPeriodAdjustment:true,calculationType:'Manual / Variable',sourceColumnIndex:component.expectedColumnIndex,proRata:false,formula:'',rowFormulas:{}} : {})}
  })
  return restoreExcelPayrollMasterMetadata({...proposed,...basis,components,effectiveFrom:`${batch.month}-01`,updateExistingConfiguration:true,
    transferReviewVersion:1,templateScopeReviewVersion:1,templateDecisions:[],reviewedFixedInputs:false,reviewedPolicyScope:false,roundingReviews:{},reviewedExtraDutyUnits:false,
    importAttendance:false,importPayableOnlyAttendance:false,overrideSourcePayableAttendance:false,
    templateScope:previous.templateScope?{...previous.templateScope,workLocationIds:[...previous.templateScope.workLocationIds],newLocationNames:[],categories:[...previous.templateScope.categories],reviewed:false}:undefined,
    attendanceReview:undefined,
  },context)
}

// Copy configuration, never another cohort's identities, amounts, approvals or receipts.
export function reuseExcelPayrollBasis(batch: ExcelPayslipBatch, context: PayrollSetupContext, rowIds: string[], previous: PayrollSetupRequest): PayrollSetupRequest {
  if (previous.batchId !== batch.id || rowIds.some(id => previous.rowIds.includes(id))) throw new Error('Choose a different cohort from the same saved batch.')
  const proposed = proposeExcelPayrollSetup(batch, context, rowIds, previous.sourceColumnRoles).request
  const fields: (keyof PayrollSetupRequest)[] = ['effectiveFrom', 'payrollDaysColumnIndex', 'fixedPayrollDays', 'payrollDaysCell', 'monthlyCtcColumnIndex', 'reviewedExtraDutyUnits', 'payableDaysColumnIndex', 'presentDaysColumnIndex', 'workWeek', 'attendanceCycleStartDay', 'attendanceCycleEndDay', 'payrollReportGenerationDay', 'defaultDepartment', 'defaultDesignation', 'defaultGrade', 'defaultWorkLocationId', 'defaultWorkLocationName', 'defaultWorkLocationState', 'sourceWorkLocationColumnIndex', 'createMissingWorkLocations', 'createMissingDropdownValues', 'preserveExistingAttendancePolicies', 'importAttendance', 'importPayableOnlyAttendance']
  const basis = Object.fromEntries(fields.map(key => [key, previous[key]]))
  const selected = batch.rows.filter(row => rowIds.includes(row.id))
  const components = proposed.components.map(component => {
    const prior = previous.components.find(item => item.expectedColumnIndex === component.expectedColumnIndex && item.code === component.code)
    if (!prior) return component
    const captured = component.expectedColumnIndex != null && selected.every(row => context.source.rows.find(r => r.sourceRow === row.sourceRow)?.cells[component.expectedColumnIndex!]?.formulaText)
    const common = captured && new Set(Object.values(prior.rowFormulas || {})).size <= 1
    return { ...component, ...prior, existingComponentId: component.existingComponentId || prior.existingComponentId,
      ...(!prior.isPeriodAdjustment && !common ? { calculationType: component.calculationType, sourceColumnIndex: component.sourceColumnIndex, proRata: component.proRata, formula: component.formula, rowFormulas: component.rowFormulas || {} } : { rowFormulas: {} }) }
  })
  return restoreExcelPayrollMasterMetadata({ ...proposed, ...basis, components, transferReviewVersion: 1, templateScopeReviewVersion: 1,
    templateScope: previous.templateScope ? { ...previous.templateScope, workLocationIds: [...previous.templateScope.workLocationIds], newLocationNames: [...previous.templateScope.newLocationNames], categories: [...previous.templateScope.categories], reviewed: false } : undefined,
    attendanceReview: previous.attendanceReview ? { ...previous.attendanceReview, reviewed: false, boundSourceInputs: undefined } : undefined,
    reviewedFixedInputs: false, reviewedPolicyScope: false, roundingReviews: {}, templateDecisions: [],
  }, context)
}
