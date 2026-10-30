import { getJsonResult, postJson } from './apiClient'

export type ConsolidationSelection = { canonicalBatchId: string; sourceBatchIds: string[] }
export type ExcelPayslipConsolidationPreview = ConsolidationSelection & {
  token: string; canConsolidate: boolean; blockers: string[]; canonicalRows: number; matchedRows: number
  sources: { batchId: string; rows: number; matchedRows: number; financialMatch: boolean; identityMatch: boolean; formulaSourcePresent: boolean; formulaSourceUnchanged: boolean; batchHash: string; sourceHash: string }[]
  differences: { sourceBatchId: string; sourceRow: number; field: string; reason: string }[]
  evidence: string
  canonicalSnapshotHash?: string
}
export type ExcelPayslipConsolidationStatus = ConsolidationSelection & {
  groupId: string; active: boolean; canUndo: boolean; token: string; canonicalRows: number; createdAtUtc: string
  historicalDelivery: { rowId: string; sourceRowId: string; sourceBatchId: string; fromConsolidatedVersion: true; status: string; mode: string; queuedAtUtc?: string | null; sentAtUtc?: string | null; message: string }[]
  history: { action: string; requestId: string; atUtc: string }[]
  warnings?: string[]
}

const root = '/api/excel-payslips'
export const previewExcelPayslipConsolidation = (clientId: number, request: ConsolidationSelection) =>
  postJson<ConsolidationSelection, ExcelPayslipConsolidationPreview | null>(`${root}/consolidation-preview?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
export const applyExcelPayslipConsolidation = (clientId: number, request: ConsolidationSelection & { requestId: string; token: string; acknowledged: boolean }) =>
  postJson<typeof request, ExcelPayslipConsolidationStatus | null>(`${root}/consolidations?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
export const getExcelPayslipConsolidation = (clientId: number, batchId: string) =>
  getJsonResult<ExcelPayslipConsolidationStatus | null>(`${root}/batches/${encodeURIComponent(batchId)}/consolidation?clientId=${clientId}`, null, { toast: false, loader: false })
export const undoExcelPayslipConsolidation = (clientId: number, groupId: string, request: { requestId: string; token: string }) =>
  postJson<typeof request, ExcelPayslipConsolidationStatus | null>(`${root}/consolidations/${encodeURIComponent(groupId)}/undo?clientId=${clientId}`, request, null, { toast: false, timeoutMs: 120000 })
