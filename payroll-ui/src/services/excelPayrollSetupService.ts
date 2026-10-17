import { getJsonResult, postJson } from './apiClient'
import type { ExcelPayslipCalculationSource } from './excelPayslipService'
import type { SimulationResult } from './payrollCalculationService'

export type PayrollSetupComponent = {
  key: string; existingComponentId?: string; code: string; name: string; category: string; componentRole: string; statutoryType: string
  calculationType: string; value: string; formula: string; baseComponent: string; proRata: boolean; taxable: boolean; priority: number
  sourceColumnIndex?: number; expectedColumnIndex?: number; isPeriodAdjustment: boolean; exemptWhenSourceZero: boolean
  rowFormulas?: Record<string, string>; blankInputAsZero?: boolean
}
export type PayrollSetupRequest = {
  batchId: string; rowIds: string[]; templateName: string; effectiveFrom: string; payrollDaysColumnIndex: number; payableDaysColumnIndex: number
  presentDaysColumnIndex?: number; workWeek: string; attendanceCycleStartDay: number; attendanceCycleEndDay: number; payrollReportGenerationDay: number
  defaultDepartment: string; defaultDesignation: string; defaultGrade: string; defaultWorkLocationId?: number; defaultWorkLocationName?: string
  sourceWorkLocationColumnIndex?: number; createMissingWorkLocations?: boolean; components: PayrollSetupComponent[]
  employeeDecisions: { rowId: string; matchedEmployeeId?: number; allowCreate: boolean }[]; editableDefaults: Record<string, unknown>
}
export type PayrollSetupContext = {
  migrationRequired?: boolean
  source: ExcelPayslipCalculationSource
  components: (PayrollSetupComponent & { id: string })[]
  workWeeks: { value: string; label: string }[]; workLocations: { id: number; name: string }[]
  employees: { id: number; employeeCode: string; employeeName: string; workLocationId?: number; workLocation?: string; designation?: string }[]
  lastImport?: { importId: string; requestId: string; status: string; savedRequest: PayrollSetupRequest; result?: PayrollSetupResult; editableDefaultsDraft?: Record<string, unknown> | null; defaultsRevision?: number } | null
}
export type PayrollSetupPreview = {
  planHash: string; canApply: boolean; migrationRequired: boolean; coverage: string
  issues: { severity: string; message: string; rowId?: string; code?: string }[]
  rows: { rowId: string; employeeId?: number; employeeCode: string; employeeName: string; action: string; payrollDays: number; presentDays: number; payableDays: number
    annualCtc: number; gross?: number; deductions?: number; benefits?: number; net?: number
    lines: { code: string; sourceAmount?: number; calculatedAmount: number; variance?: number }[] }[]
}
export type PayrollSetupResult = { importId: string; templateId: string; attendanceGroupId: number; employeeIds: number[]; createdCount: number; updatedCount: number; replayed: boolean }
export type PayrollSetupVerification = { employeeId: number; data: SimulationResult | null; error: string; ok: boolean }
export const verifyPayrollSetupEmployees = async (clientId: number, payPeriod: string, employeeIds: number[]): Promise<PayrollSetupVerification[]> => Promise.all(employeeIds.map(async employeeId => {
  const result = await postJson('/api/payroll-calculation/simulate', { mode: 'Employee', clientId, employeeId, payPeriod, payrollDays: null, presentDays: null, payableDays: null, annualCtc: null, amountOverrides: {} }, null as SimulationResult | null, { toast: false, loader: false, timeoutMs: 120000 })
  return { employeeId, data: result.data, error: result.error, ok: result.ok }
}))
const path = '/api/excel-payroll-setup'
export const getPayrollSetupContext = (clientId: number, batchId: string, rowIds?: string[]) => getJsonResult<PayrollSetupContext | null>(`${path}/context?clientId=${clientId}&batchId=${encodeURIComponent(batchId)}${rowIds?.length ? `&rowIds=${encodeURIComponent(rowIds.join(','))}` : ''}`, null, { toast: false })
export const previewPayrollSetup = (clientId: number, request: PayrollSetupRequest) => postJson<PayrollSetupRequest, PayrollSetupPreview | null>(`${path}/preview?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
export const savePayrollSetupDraft = (clientId: number, request: PayrollSetupRequest, requestId: string) => postJson(`${path}/draft?clientId=${clientId}`, { request, requestId }, null, { toast: false, timeoutMs: 120000 })
export const savePayrollDefaultsDraft = (clientId: number, batchId: string, importId: string, expectedRevision: number, editableDefaults: Record<string, unknown>) => postJson(`${path}/defaults-draft?clientId=${clientId}`, { batchId, importId, expectedRevision, editableDefaults }, null as { defaultsRevision: number; editableDefaultsDraft: Record<string, unknown> } | null, { toast: false })
export const applyPayrollSetup = (clientId: number, request: PayrollSetupRequest, planHash: string, requestId: string) => postJson<{ request: PayrollSetupRequest; planHash: string; requestId: string }, PayrollSetupResult | null>(`${path}/apply?clientId=${clientId}`, { request, planHash, requestId }, null, { toast: false, timeoutMs: 120000 })
