import { apiRequest, getJsonResult, postJson, readError, type ApiResult } from './apiClient'
import type { PayslipColumn } from '../utils/excelPayslipImport'

export type ExcelPayslipText = { label: string; value: string }
export type ExcelPayslipAmount = { label: string; amount: number }
export type ExcelPayslipRow = {
  id: string; sourceRow: number; employeeCode: string; employeeName: string; email: string
  information: ExcelPayslipText[]; earnings: ExcelPayslipAmount[]; deductions: ExcelPayslipAmount[]; employerContributions: ExcelPayslipAmount[]
  netPay: number; declaredGross: number | null; declaredDeductions: number | null; warnings: string[]
}
export type ExcelPayslipBatchInput = { month: string; sourceFileName: string; sheetName: string; headerRow: number; salaryTemplateId?: string; salaryTemplateName?: string; rows: ExcelPayslipRow[] }
export type ExcelPayslipBatch = ExcelPayslipBatchInput & { id: string; clientId: number; clientName?: string; createdAtUtc: string; createdBy: string }
export type ExcelPayslipSummary = Omit<ExcelPayslipBatch, 'rows'> & { rowCount: number }
export type ExcelPayslipProfile = { headerSignature: string; columns: PayslipColumn[]; salaryTemplateId?: string }
export type ExcelPayslipTemplate = { id: string; name: string; components: { id: string; code: string; name: string; category: 'Earning' | 'Deduction' | 'Employer' | 'Information' }[] }
export type ExcelPayslipPdfRequest = { rowIds: string[]; includeSeal: boolean; acknowledgeWarnings: boolean; amountDecimalPlaces: 0 | 2 }
export type ExcelPayslipSendRequest = ExcelPayslipPdfRequest & { mode: 'Individual' | 'Combined'; email: string; emailOverrides: Record<string, string>; requestId: string }
export type ExcelPayslipDelivery = { rowId: string; employeeName: string; email: string; status: 'Queued' | 'Already queued' | 'Error'; message: string }
const root = '/api/excel-payslips'
const batchPath = (id: string) => `${root}/batches/${encodeURIComponent(id)}`
export const getExcelPayslipClients = () => getJsonResult<{ id: number; name: string }[]>(`${root}/clients`, [])
export const getExcelPayslipTemplates = (clientId: number) => getJsonResult<ExcelPayslipTemplate[]>(`${root}/templates?clientId=${clientId}`, [])
export const getExcelPayslipHistory = (clientId: number) => getJsonResult<ExcelPayslipSummary[]>(`${root}/batches?clientId=${clientId}`, [])
export const getExcelPayslipBatch = (clientId: number, id: string) => getJsonResult<ExcelPayslipBatch | null>(`${batchPath(id)}?clientId=${clientId}`, null)
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
