import { apiRequest, getJsonResult, postJson, readError, type ApiOptions, type ApiResult } from './apiClient'
import type { PayRun } from '../types/payroll'

export type PayslipDocument = { payRunId: number; payPeriod: string; employeeCode: string; fileName: string; html: string; pdfPreferred?: boolean }
export type PayslipDeliveryItem = { employeeId: number; employeeCode: string; status: 'Queued' | 'Already queued' | 'Excluded'; message: string }
export type PayslipDeliveryResult = { items: PayslipDeliveryItem[]; queuedCount: number; alreadyQueuedCount: number; excludedCount: number }
export const getRegisterPayRun = (payRunId: number, options: ApiOptions = {}) => getJsonResult<PayRun | null>('/api/pay-runs/' + payRunId, null, { loader: false, toast: false, ...options })

export const getRegisterPayslip = (payRunId: number, employeeId: number, options: ApiOptions = {}) => getJsonResult<PayslipDocument | null>(`/api/pay-runs/${payRunId}/payslips/${employeeId}`, null, { loader: false, ...options })
export const sendRegisterPayslips = (payRunId: number, employeeIds: number[]) => postJson(`/api/pay-runs/${payRunId}/payslips/send`, { employeeIds }, { items: [], queuedCount: 0, alreadyQueuedCount: 0, excludedCount: 0 } as PayslipDeliveryResult, { loader: false, toast: false, timeoutMs: 120000 })

export type PayslipDesignKind = 'info' | 'earning' | 'deduction' | 'employer'
export type PayslipDesignField = { sourceColumn: number; label: string; kind: PayslipDesignKind; nativeCode: string; infoToken: string; visible: boolean }
export type PayslipDesignTemplate = { id: number; clientId: number; name: string; theme: 'Excel Payslip'; showLogo: boolean; showClient: boolean; showYtd: boolean; showBank: boolean; note: string; active: boolean }
export type PayslipDesignSource = { id: string; month: string; name: string }
export type PayslipDesignCode = { code: string; name: string; category: string }
export type PayslipDesignCatalog = { clientId: number; sourceBatchId: string; sourceHash: string; simpleLayout: boolean; fields: PayslipDesignField[]; nativeCodes: PayslipDesignCode[]; infoTokens: string[]; supportsPayrollMonthScope?: boolean }
export type SavedPayslipDesign = { template: PayslipDesignTemplate; sourceBatchId: string; sourceHash: string; profileHash: string; amountDecimalPlaces: 0 | 2; payPeriods?: string[] | null; fields: PayslipDesignField[] }
export type SavePayslipDesignRequest = { requestId: string; template: PayslipDesignTemplate; sourceBatchId: string; sourceHash: string; existingProfileHash?: string; amountDecimalPlaces: 0 | 2; payPeriods?: string[] | null; allPayPeriods?: boolean; fields: PayslipDesignField[]; reviewed: boolean }
export type PayslipPdf = { blob: Blob; fileName: string }

export const getPayslipDesignSources = (clientId: number, options: ApiOptions = {}) => getJsonResult<PayslipDesignSource[]>(`/api/payslip-designs/sources?clientId=${clientId}`, [], { toast: false, loader: false, ...options })
export const getPayslipDesign = (clientId: number, templateId: number, options: ApiOptions = {}) => getJsonResult<SavedPayslipDesign | null>(`/api/payslip-designs/${templateId}?clientId=${clientId}`, null, { toast: false, loader: false, ...options })
export const previewPayslipDesign = (clientId: number, sourceBatchId: string, options: ApiOptions = {}) => postJson<{ clientId: number; sourceBatchId: string }, PayslipDesignCatalog | null>('/api/payslip-designs/preview', { clientId, sourceBatchId }, null, { toast: false, loader: false, timeoutMs: 120000, ...options })
export const savePayslipDesign = (request: SavePayslipDesignRequest, options: ApiOptions = {}) => postJson<SavePayslipDesignRequest, PayslipDesignTemplate | null>('/api/payslip-designs/save', request, null, { toast: false, loader: false, timeoutMs: 120000, ...options })

export async function getRegisterPayslipPdf(payRunId: number, employeeId: number, options: ApiOptions = {}): Promise<ApiResult<PayslipPdf | null>> {
  return requestPayslipPdf('/api/pay-runs/' + payRunId + '/payslips/' + employeeId + '/pdf', options, 'payslip-' + payRunId + '-' + employeeId + '.pdf')
}
export async function previewPayslipDesignPdf(request: SavePayslipDesignRequest, options: ApiOptions = {}): Promise<ApiResult<PayslipPdf | null>> {
  return requestPayslipPdf('/api/payslip-designs/preview-pdf', { ...options, method: 'POST', body: JSON.stringify({ ...request, reviewed: false }) }, 'payslip-design-sample.pdf')
}
async function requestPayslipPdf(path: string, options: ApiOptions, fallbackFileName: string): Promise<ApiResult<PayslipPdf | null>> {
  try {
    const response = await apiRequest(path, { loader: false, toast: false, timeoutMs: 120000, ...options })
    if (!response.ok) return { ok: false, data: null, error: await readError(response), status: response.status }
    if (!response.headers.get('content-type')?.toLowerCase().includes('application/pdf')) return { ok: false, data: null, error: 'The API did not return a PDF. Refresh the page or update the API.', status: response.status }
    const blob = await response.blob()
    if (!blob.size) return { ok: false, data: null, error: 'The payslip PDF is empty.', status: response.status }
    const disposition = response.headers.get('content-disposition') || ''
    let name = /filename="?([^";]+)/i.exec(disposition)?.[1] || fallbackFileName
    const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1]
    if (encoded) { try { name = decodeURIComponent(encoded) } catch { /* Retain the safe fallback filename. */ } }
    name = Array.from(name.split(/[\\/]/).pop() || '').filter(character => character.charCodeAt(0) >= 32 && character.charCodeAt(0) !== 127).join('').slice(0, 180) || fallbackFileName
    if (!name.toLowerCase().endsWith('.pdf')) name += '.pdf'
    return { ok: true, data: { blob, fileName: name }, error: '', status: response.status }
  } catch (error) {
    return { ok: false, data: null, error: error instanceof Error ? error.message : 'Unable to load the payslip PDF.', status: 0 }
  }
}
