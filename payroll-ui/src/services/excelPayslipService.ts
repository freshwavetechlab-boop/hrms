import { apiRequest, getJsonResult, postJson, readError, type ApiResult } from './apiClient'
import type { PayslipColumn, PayslipSourceRow } from '../utils/excelPayslipImport'

export type ExcelPayslipText = { label: string; value: string }
export type ExcelPayslipAmount = { label: string; amount: number }
export type ExcelPayslipRow = {
  id: string; sourceRow: number; employeeCode: string; employeeName: string; email: string
  information: ExcelPayslipText[]; earnings: ExcelPayslipAmount[]; deductions: ExcelPayslipAmount[]; employerContributions: ExcelPayslipAmount[]
  netPay: number; declaredGross: number | null; declaredDeductions: number | null; warnings: string[]
}
export type ExcelPayslipBatchInput = { month: string; sourceFileName: string; sheetName: string; headerRow: number; simpleLayout?: boolean; salaryTemplateId?: string; salaryTemplateName?: string; rows: ExcelPayslipRow[]; calculationSource?: ExcelPayslipCalculationSource | null }
export type ExcelPayslipBatch = ExcelPayslipBatchInput & { id: string; clientId: number; clientName?: string; createdAtUtc: string; createdBy: string }
export type ExcelPayslipSummary = Omit<ExcelPayslipBatch, 'rows'> & { rowCount: number }
export type ExcelPayslipProfile = { headerSignature: string; columns: PayslipColumn[]; salaryTemplateId?: string }
export type ExcelPayslipTemplate = { id: string; name: string; components: { id: string; code: string; name: string; category: 'Earning' | 'Deduction' | 'Employer' | 'Information' }[] }
export type ExcelPayslipPdfRequest = { rowIds: string[]; includeSeal: boolean; acknowledgeWarnings: boolean; amountDecimalPlaces: 0 | 2 }
export type ExcelPayslipSendRequest = ExcelPayslipPdfRequest & { mode: 'Individual' | 'Combined'; email: string; emailOverrides: Record<string, string>; requestId: string }
export type ExcelPayslipEmailRequest = { rowIds: string[]; email: string; requestId: string }
export const requestExcelPayslipEmailIds = (clientId: number, id: string, request: ExcelPayslipEmailRequest) => postJson<ExcelPayslipEmailRequest, { queueId: number; email: string; employeeCount: number; status: string } | null>(`${batchPath(id)}/email-request?clientId=${clientId}`, request, null, { toast: false })
export type ExcelPayslipDelivery = { rowId: string; employeeName: string; email: string; status: 'Queued' | 'Already queued' | 'Skipped' | 'Error'; message: string }
export type ExcelPayslipEmailStatus = { rowId: string; status: 'Pending' | 'Queued' | 'Sent' | 'Failed' | 'Unknown'; email: string; queuedAtUtc: string | null; sentAtUtc: string | null; message: string; canSend: boolean }
export type ExcelPayslipMailJob = {
  id: string; clientId: number; clientName: string; batchId: string; month: string; sourceFileName: string
  createdAtUtc: string; createdBy: string; status: 'Queued' | 'Processing' | 'Completed' | 'Needs attention'
  total: number; sent: number; queued: number; failed: number; missingEmail: number; skipped: number; unknown: number
  completed: number; percentComplete: number; isComplete: boolean; message: string; dismissedAtUtc: string | null
}
export type ExcelPayslipMailJobResult = { job: ExcelPayslipMailJob; items: ExcelPayslipDelivery[] }
export type ExcelPayslipCalculationSource = { sheetName: string; headerRow: number; columnCount: number; dateSystem?: string; columns: PayslipColumn[]; rows: PayslipSourceRow[]; excludedSourceRows: number[]; sourceBatchId?: string; attendanceColumnIndex?: number | null; monthDaysCell?: string | null }
export type ExcelPayslipCalculateRequest = { month: string; attendanceColumnIndex: number; monthDaysCell?: string; monthDays?: number; attendance: { rowId: string; days: number }[]; overrides?: Record<string, number> }
export type ExcelPayslipVarianceRow = { id: string; employeeName: string; employeeCode: string; location: string; status: string; category: string; component: string; previousAmount: number | null; currentAmount: number | null; difference: number | null; message: string }
export type ExcelPayslipVariance = { currentBatchId: string; previousBatchId: string; currentMonth: string; previousMonth: string; matchedEmployees: number; newEmployees: number; missingEmployees: number; reviewEmployees: number; rows: ExcelPayslipVarianceRow[] }
export type ExcelPayslipDashboardTotals = { batchId: string; month: string; rowCount: number; earnings: number; deductions: number; employerContributions: number; netPay: number; reviewCount: number; hasCalculationSource: boolean }
export type ExcelPayslipDashboard = { clientId: number; selectedBatch: ExcelPayslipSummary | null; summary: ExcelPayslipDashboardTotals | null; history: ExcelPayslipSummary[]; trend: ExcelPayslipDashboardTotals[] }
const root = '/api/excel-payslips'
const batchPath = (id: string) => `${root}/batches/${encodeURIComponent(id)}`
export const getExcelPayslipClients = () => getJsonResult<{ id: number; name: string }[]>(`${root}/clients`, [])
export const getExcelPayslipDashboard = (clientId: number, batchId = '') => getJsonResult<ExcelPayslipDashboard | null>(`${root}/dashboard?clientId=${clientId}${batchId ? `&batchId=${encodeURIComponent(batchId)}` : ''}`, null)
export const getExcelPayslipTemplates = (clientId: number) => getJsonResult<ExcelPayslipTemplate[]>(`${root}/templates?clientId=${clientId}`, [])
export const getExcelPayslipHistory = (clientId: number) => getJsonResult<ExcelPayslipSummary[]>(`${root}/batches?clientId=${clientId}`, [])
export const getExcelPayslipBatch = (clientId: number, id: string) => getJsonResult<ExcelPayslipBatch | null>(`${batchPath(id)}?clientId=${clientId}`, null)
export const getExcelPayslipDeliveryStatus = (clientId: number, id: string) => getJsonResult<{ items: ExcelPayslipEmailStatus[] }>(`${batchPath(id)}/delivery-status?clientId=${clientId}`, { items: [] })
export const getExcelPayslipMailJobs = () => getJsonResult<{ items: ExcelPayslipMailJob[] }>(`${root}/mail-jobs`, { items: [] }, { loader: false, toast: false })
export const getExcelPayslipBatchMailJobs = (clientId: number, id: string) => getJsonResult<{ items: ExcelPayslipMailJob[] }>(`${batchPath(id)}/mail-jobs?clientId=${clientId}`, { items: [] }, { loader: false, toast: false })
export const startExcelPayslipMailJob = (clientId: number, id: string, request: ExcelPayslipSendRequest) => postJson<ExcelPayslipSendRequest, ExcelPayslipMailJobResult | null>(`${batchPath(id)}/send-job?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
export const dismissExcelPayslipMailJob = (clientId: number, id: string, jobId: string) => postJson<Record<string, never>, { ok: boolean }>(`${batchPath(id)}/mail-jobs/${encodeURIComponent(jobId)}/dismiss?clientId=${clientId}`, {}, { ok: false }, { loader: false, toast: false })
export const getExcelPayslipCalculation = (clientId: number, id: string) => getJsonResult<ExcelPayslipCalculationSource | null>(`${batchPath(id)}/calculation?clientId=${clientId}`, null)
export const calculateExcelPayslips = (clientId: number, id: string, request: ExcelPayslipCalculateRequest) => postJson<ExcelPayslipCalculateRequest, ExcelPayslipBatch | null>(`${batchPath(id)}/calculate?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
export const getExcelPayslipVariance = (clientId: number, id: string, previousBatchId: string) => getJsonResult<ExcelPayslipVariance | null>(`${batchPath(id)}/variance?clientId=${clientId}&previousBatchId=${encodeURIComponent(previousBatchId)}`, null)
export const getExcelPayslipProfile = (clientId: number, signature: string) => getJsonResult<ExcelPayslipProfile | null>(`${root}/profile?clientId=${clientId}&headerSignature=${encodeURIComponent(signature)}`, null)
export async function saveExcelPayslipProfile(clientId: number, profile: ExcelPayslipProfile): Promise<ApiResult<ExcelPayslipProfile | null>> {
  try {
    const response = await apiRequest(`${root}/profile?clientId=${clientId}&headerSignature=${encodeURIComponent(profile.headerSignature)}`, { method: 'PUT', body: JSON.stringify(profile) })
    return { ok: response.ok, data: response.ok ? profile : null, status: response.status, error: response.ok ? '' : await readError(response) }
  } catch (error) { return { ok: false, data: null, status: 0, error: error instanceof Error ? error.message : 'Unable to save mapping.' } }
}
export const saveExcelPayslipBatch = (clientId: number, batch: ExcelPayslipBatchInput) => postJson<ExcelPayslipBatchInput, ExcelPayslipBatch | null>(`${root}/batches?clientId=${clientId}`, batch, null, { toast: false })
export async function getExcelPayslipPdf(clientId: number, id: string, request: ExcelPayslipPdfRequest): Promise<ApiResult<Blob | null>> {
  try {
    const response = await apiRequest(`${batchPath(id)}/pdf?clientId=${clientId}`, { method: 'POST', body: JSON.stringify(request), timeoutMs: 120000 })
    return { ok: response.ok, data: response.ok ? await response.blob() : null, status: response.status, error: response.ok ? '' : await readError(response) }
  } catch (error) { return { ok: false, data: null, status: 0, error: error instanceof Error ? error.message : 'Unable to generate PDF.' } }
}
export const sendExcelPayslips = (clientId: number, id: string, request: ExcelPayslipSendRequest) => postJson<ExcelPayslipSendRequest, { items: ExcelPayslipDelivery[] }>(`${batchPath(id)}/send?clientId=${clientId}`, request, { items: [] }, { toast: false, timeoutMs: 120000 })
