import { getJsonResult, postJson } from './apiClient'
import type { ExcelPayslipBatch } from './excelPayslipService'

export type PayslipUpdateField = { id: string; label: string; type: 'text' | 'number' | 'date' | 'reference'; destination: 'Batch + Master' | 'Batch only' | 'Master only'; editable: boolean; reason: string; sourceColumnIndex?: number | null }
export type PayslipUpdateRow = { employeeCode: string; employeeName: string; values: Record<string, string> }
export type PayslipUpdateCatalog = { clientId: number; batchId: string; month: string; fields: PayslipUpdateField[]; rows: PayslipUpdateRow[]; blockers: string[] }
export type PayslipUpdatePatch = { employeeCode: string; values: Record<string, string> }
export type PayslipUpdatePreviewRequest = { month: string; sourceFileName: string; rows: PayslipUpdatePatch[] }
export type PayslipUpdateChange = { id: string; employeeCode: string; employeeName: string; fieldId: string; label: string; oldValue: string; newValue: string; destination: PayslipUpdateField['destination']; canApply: boolean; computed?: boolean; reason: string }
export type PayslipUpdatePreview = { token: string; changes: PayslipUpdateChange[]; blockers: string[] }
export type PayslipUpdateApplyRequest = { token: string; selectedChangeIds: string[]; requestId: string; acknowledged: boolean; acknowledgeZeroChanges: boolean }
export type PayslipUpdateResult = { batch: ExcelPayslipBatch; appliedChanges: number; updatedEmployees: number; replayed: boolean }

const path = (clientId: number, id: string, action: string) => `/api/excel-payslips/batches/${encodeURIComponent(id)}/${action}?clientId=${clientId}`
export const getPayslipUpdateCatalog = (clientId: number, id: string) => getJsonResult<PayslipUpdateCatalog | null>(path(clientId, id, 'update-catalog'), null, { toast: false, loader: false, timeoutMs: 120000 })
export const previewPayslipUpdate = (clientId: number, id: string, request: PayslipUpdatePreviewRequest) => postJson<PayslipUpdatePreviewRequest, PayslipUpdatePreview | null>(path(clientId, id, 'update-preview'), request, null, { toast: false, loader: false, timeoutMs: 120000 })
export const applyPayslipUpdate = (clientId: number, id: string, request: PayslipUpdateApplyRequest) => postJson<PayslipUpdateApplyRequest, PayslipUpdateResult | null>(path(clientId, id, 'update-apply'), request, null, { toast: false, loader: false, timeoutMs: 120000 })
