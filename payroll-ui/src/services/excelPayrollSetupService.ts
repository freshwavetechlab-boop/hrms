import { getJsonResult, postJson } from './apiClient'
import type { ExcelPayslipCalculationSource } from './excelPayslipService'
import type { SimulationResult, SourceSalaryScenario } from './payrollCalculationService'

export type PayrollSetupComponent = {
  key: string; existingComponentId?: string; code: string; name: string; category: string; componentRole: string; statutoryType: string
  calculationType: string; value: string; formula: string; baseComponent: string; proRata: boolean; taxable: boolean; priority: number; payType?: string
  sourceColumnIndex?: number; expectedColumnIndex?: number; isPeriodAdjustment: boolean; exemptWhenSourceZero: boolean
  rowFormulas?: Record<string, string>; blankInputAsZero?: boolean
}
export type PayrollSetupMasterComponent = Omit<PayrollSetupComponent, 'priority'> & { id: string; priority: number | string }
export type PayrollLocationComponentScope = 'SourceFormulaPerPart' | 'Monthly'
export type PayrollLocationPart = { rowId: string; workLocationId: number; templateId: string; componentScopes: Record<string, PayrollLocationComponentScope> }
export type PayrollLocationGroup = { employeeId: number; referenceAssignmentId?: string; parts: PayrollLocationPart[] }
export type PayrollLocationAllocations = { version: 1; payrollPeriod: string; configurationEffectiveFrom: string; reviewed: boolean; reviewedSettingsApplicability: boolean; groups: PayrollLocationGroup[] }
export type PayrollLocationTemplate = { id: string; name: string; clientId: string | number; active: boolean; lines: { componentId: string; value: string; calculationType: string; formula: string; baseComponent: string; proRataOverride: string; roundingMode?: string }[] }
export type PayrollLocationPreviewPart = PayrollLocationPart & { sourceRow: number; partKey: string; assignmentId: string; workLocationName: string; workState: string; payrollDays: number; presentDays: number; payableDays: number; annualCtc: number; salaryInputs: Record<string, number>; rulesHash: string; sourceHash: string }
export type PayrollLocationPreview = { version: 1; payrollPeriod: string; configurationEffectiveFrom: string; groups: { employeeId: number; referenceAssignmentId?: string; parts: PayrollLocationPreviewPart[] }[] }
export type PayrollLocationAssignmentReceipt = { rowId: string; employeeId: number; partKey: string; assignmentId: string; workLocationId: number; templateId: string; payPeriod: string; configurationEffectiveFrom: string; rulesHash: string; sourceHash: string }
export type AttendanceInputReview = { boundSourceInputs?: string; mode: 'SourceSeed' | 'VerifyExistingNative'; sourceHash: string; sourceMonth: string; cycleStart: string; cycleEnd: string; payableMeaning: '' | 'AlreadyPayable' | 'WorkedOnly'; presentMeaning: '' | 'ActualWorked' | 'NotMapped'; lwpColumnIndex?: number; lwpBasis: '' | 'PayrollDivisorMinusPayable'; reviewed: boolean }
export type AttendanceInputProof = { rowId: string; sourceRow: number; mode: string; status: string; reason: string; sourceHash: string; inputHash: string; payrollDays: number; presentDays: number | null; payableDays: number; sourceLwp: number | null; nativeHash: string | null; nativeInput?: { payrollDays: number | null; presentDays: number | null; actualPresenceKnown?: boolean; payableDays: number; month: string; cycleStart: string | null; cycleEnd: string | null; snapshotHash: string; error: string } | null }
export type TemplateApplicability = {reviewed:boolean;effectiveTo:string;openEnded:boolean;locationMode:''|'AllClientLocations'|'Listed';workLocationIds:number[];newLocationNames:string[];categoryKind:''|'AllClientCategories'|'SkillCategory'|'EmploymentType';categories:string[];divisorMeaning:''|'CalendarDays'|'ContractFixedDays'|'WorkingDays';payableMeaning:''|'AlreadyPayable';policyBasis:''|'SavedSourceRules'|'DatedPolicy';policyVersion:string}
export type PayrollSetupRequest = {
  correctionOfImportId?: string
  overrideSourcePayableAttendance?: boolean
  identityColumnReview?: { version: 1; role: 'UAN'; columnIndex: number; sourceHash: string; selectionFingerprint: string; reason: string; reviewed: boolean }
  locationAllocations?: PayrollLocationAllocations
  templateScopeReviewVersion?:number;templateScope?:TemplateApplicability
  attendanceReview?: AttendanceInputReview
  roundingReviews?: Record<string, string>
  transferReviewVersion?: number; reviewedFixedInputs?: boolean; reviewedPolicyScope?: boolean; templateDecisions?: TransferTemplateDecision[]
  payrollDaysCell?: string
  batchId: string; rowIds: string[]; templateName: string; effectiveFrom: string; payrollDaysColumnIndex: number; payableDaysColumnIndex: number
  fixedPayrollDays?: number; sourceColumnRoles?: Record<number, string>
  presentDaysColumnIndex?: number; workWeek: string; attendanceCycleStartDay: number; attendanceCycleEndDay: number; payrollReportGenerationDay: number
  defaultDepartment: string; defaultDesignation: string; defaultGrade: string; defaultWorkLocationId?: number; defaultWorkLocationName?: string; defaultWorkLocationState?: string
  sourceWorkLocationColumnIndex?: number; createMissingWorkLocations?: boolean; createMissingDropdownValues?: boolean; preserveExistingAttendancePolicies?: boolean; importAttendance?: boolean; importPayableOnlyAttendance?: boolean; components: PayrollSetupComponent[]
  employeeDecisions: { rowId: string; matchedEmployeeId?: number; allowCreate: boolean }[]; editableDefaults: Record<string, unknown>
}
export type PayrollSetupContext = {
  salaryCorrectionSupported?: boolean
  sourcePayableOverrideSupported?: boolean
  identityColumnReviewSupported?: boolean
  identityColumnCandidates?: { columnIndex: number; columnLetter: string; sourceHeader: string; selectedRows: number; canonicalLiteralRows: number; invalidOrMissingRows: number; sourceHash: string; selectionFingerprint: string }[]
  payableOnlyAttendanceSupported?: boolean
  locationAllocationSupported?: boolean
  locationAllocationImportDate?: string
  salaryTemplates?: PayrollLocationTemplate[]
  calculationOnlySimulationSupported?: boolean
  defaults?: { workLocationState?: string; department: string; designation: string; grade: string; workWeek: string; attendanceCycleStartDay: number; attendanceCycleEndDay: number; payrollReportGenerationDay: number; attendanceSource: string; notes: string[] }
  templateApplicabilitySupported?: boolean
  sharedHeaderDivisorSupported?: boolean
  migrationRequired?: boolean
  appliedSetups?: string[][]
  roster?: { rows: { rowId: string; sourceRow: number; employeeName: string; employeeCode: string; status: 'Existing' | 'New' | 'Review' | 'Applied'; employeeId: number | null; message: string }[]; missingEmployees: { employeeId: number; employeeCode: string; employeeName: string; workLocation: string }[] }
  source: ExcelPayslipCalculationSource
  components: PayrollSetupMasterComponent[]
  workWeeks: { value: string; label: string }[]; workLocations: { id: number; name: string }[]
  employees: { id: number; employeeCode: string; employeeName: string; workLocationId?: number; workLocation?: string; designation?: string }[]
  lastImport?: { importId: string; requestId: string; status: string; savedRequest: PayrollSetupRequest; result?: PayrollSetupResult; editableDefaultsDraft?: Record<string, unknown> | null; defaultsRevision?: number } | null
}
export type PresentDaysEvidence = { sourceColumnIndex: number | null; sourceCell: string | null; sourceValue: number | null; calculationValue: number; actualWorkedReviewed: boolean; status: string; message: string }
export type PayrollSetupPreview = {
  locationAllocations?: PayrollLocationPreview | null
  selectedSourceRows?: { rowId: string; sourceRow: number | null; sourceFound: boolean }[]
  transfer?: TransferReview | null
  planHash: string; canApply: boolean; migrationRequired: boolean; coverage: string
  issues: { severity: string; message: string; rowId?: string; code?: string }[]
  rows: { whatIfSimulation?: SourceSalaryScenario | null; rowId: string; employeeId?: number; employeeCode: string; employeeName: string; action: string; payrollDays: number; presentDays: number; payableDays: number; presentDaysEvidence?: PresentDaysEvidence | null
    annualCtc: number; gross?: number; deductions?: number; benefits?: number; net?: number
    lines: { code: string; sourceAmount?: number; calculatedAmount: number; variance?: number }[] }[]
}
export type PayrollSetupResult = { correctionOfImportId?: string; reusedAdjustments?: { adjustmentId: number; employeeId: number; rowId: string; sourceRow: number; componentCode: string; amount: number; originalImportId: string }[]; locationAllocations?: PayrollLocationPreview | null; locationAssignmentReceipts?: PayrollLocationAssignmentReceipt[]; dataQualityReviews?: { severity: string; message: string; rowId?: string; code?: string }[]; transfer?: TransferReview | null; transferRows?: TransferRowReceipt[]; keptRows?: { rowId: string; employeeId: number; templateId: string; reason: string }[]; importId: string; templateId: string; attendanceGroupId: number; employeeIds: number[]; createdCount: number; updatedCount: number; replayed: boolean }
export type PayrollSetupVerificationRequest = {
  mode: 'Employee'; clientId: number; employeeId: number; payPeriod: string
  payrollDays: null; presentDays: null; payableDays: null; annualCtc: null; amountOverrides: Record<string, number>
}
export type PayrollSetupVerification = { employeeId: number; data: SimulationResult | null; error: string; ok: boolean; request?: PayrollSetupVerificationRequest }
export const verifyPayrollSetupEmployees = async (clientId: number, payPeriod: string, employeeIds: number[]): Promise<PayrollSetupVerification[]> => {
  const results: PayrollSetupVerification[] = []
  // Keep simulations bounded instead of issuing up to 100 concurrent DB workspaces.
  for (let offset = 0; offset < employeeIds.length; offset += 4) results.push(...await Promise.all(employeeIds.slice(offset, offset + 4).map(async employeeId => {
  const request: PayrollSetupVerificationRequest = Object.freeze({ mode: 'Employee', clientId, employeeId, payPeriod, payrollDays: null, presentDays: null, payableDays: null, annualCtc: null, amountOverrides: Object.freeze({}) })
  const result = await postJson('/api/payroll-calculation/simulate', request, null as SimulationResult | null, { toast: false, loader: false, timeoutMs: 120000 })
  return { employeeId, request, data: result.data, error: result.error, ok: result.ok }
  })))
  return results
}
const path = '/api/excel-payroll-setup'
// Loading the preserved source, effective Settings and receipt history can exceed
// the shared 30-second read window. This scoped read never submits configuration.
export const getPayrollSetupContext = (clientId: number, batchId: string, rowIds?: string[]) => getJsonResult<PayrollSetupContext | null>(`${path}/context?clientId=${clientId}&batchId=${encodeURIComponent(batchId)}${rowIds?.length ? `&rowIds=${encodeURIComponent(rowIds.join(','))}` : ''}`, null, { toast: false, timeoutMs: 120000, timeoutMessage: 'Payroll setup context took too long to load. No configuration Apply was submitted. Close this review and reopen the same source selection.' })
export const previewPayrollSetup = (clientId: number, request: PayrollSetupRequest) => postJson<PayrollSetupRequest, PayrollSetupPreview | null>(`${path}/preview?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
export const savePayrollSetupDraft = (clientId: number, request: PayrollSetupRequest, requestId: string) => postJson(`${path}/draft?clientId=${clientId}`, { request, requestId }, null as { importId: string; requestId: string; status: string } | null, { toast: false, timeoutMs: 120000 })
export const savePayrollDefaultsDraft = (clientId: number, batchId: string, importId: string, expectedRevision: number, editableDefaults: Record<string, unknown>) => postJson(`${path}/defaults-draft?clientId=${clientId}`, { batchId, importId, expectedRevision, editableDefaults }, null as { defaultsRevision: number; editableDefaultsDraft: Record<string, unknown> } | null, { toast: false })
// Reviewed cohorts contain up to 100 salary/history writes and can take over two minutes.
// Keep this longer wait scoped to Apply; an uncertain timeout still requires a receipt check, never an automatic retry.
export const applyPayrollSetup = (clientId: number, request: PayrollSetupRequest, planHash: string, requestId: string) => postJson<{ request: PayrollSetupRequest; planHash: string; requestId: string }, PayrollSetupResult | null>(`${path}/apply?clientId=${clientId}`, { request, planHash, requestId }, null, { toast: false, timeoutMs: 240000 })

export type TransferTemplateDecision = { variantKey: string; existingTemplateId: string; createVariant: boolean; keepExisting?: boolean;scopeAction?:string;scopeReason?:string }
export type TransferAmountReview = { version: string; rowId: string; sourceRow: number; status: string; reason: string; proofHash: string; acknowledged: boolean; sourceNetRaw: number; sourceNetDisplay: number; coreNet: number | null; rawNetDelta: number | null; displayNetDelta: number | null; lines: { code: string; category: string; sourceRaw: number; sourceDisplay: number; coreUnrounded: number; calculated: number; rawDelta: number; displayDelta: number }[] }
export type TransferReview = {
  amountComparisons?: TransferAmountReview[]
  sourceMonth: string; attendanceProofs?: AttendanceInputProof[]
  version: number; sourceBatchId: string; sourceHash: string; effectiveFrom: string; preparationStatus: string; parityStatus: string; tolerance: number
  components: { code: string; category: string; action: string; masterId: string }[]
  formulas: { rowId: string; code: string; sourceRow: number; sourceColumn: number | null; sourceHash: string; status: string; reason: string }[]
  templates: { variantKey: string; templateId: string; name: string; action: string; rowIds: string[]; existingResults: { rowId: string; templateId: string; complete: boolean; net: number | null; sourceNet: number; variance: number | null; coverage: string; lines: { code: string; sourceAmount: number | null; calculatedAmount: number; variance: number | null }[] }[]; keptAssignments: { rowId: string; employeeId: number; templateId: string; reason: string }[]; candidates: { id: string; name: string; exact: boolean; configurationExact?:boolean;recordedScopes?:{clientId:number;templateId:string;rulesSignature:string;effectiveFrom:string;policyKey:string;scope:TemplateApplicability;sourceBatchId:string;sourceMonth:string}[];scopeStatus?:string;scopeReason?:string; differences: string[]; details: { component: string; field: string; existing: string; excelDerived: string }[] }[] }[]
}
export type TransferRowReceipt = { rowId: string; employeeId: number; payrollDays: number; presentDays: number | null; payableDays: number; sourceNet: number; lines: { code: string; sourceAmount: number | null; calculatedAmount: number; variance: number | null }[] }
