import { apiRequest, getJsonResult, postForm, postJson, readError, type ApiResult } from './apiClient'
import { saveExcelPayslipBatch, type ExcelPayslipBatch, type ExcelPayslipBatchInput } from './excelPayslipService'

export type OriginalWorkbookReference = { id: string; fileName: string }
export type OriginalWorkbook = OriginalWorkbookReference & { sizeBytes: number; sha256: string; uploadedAtUtc: string }
export type RetainedWorkbookCache = Map<File, { clientId: number; reference: OriginalWorkbookReference; sha256?: string }>
export type BatchDeletePreview = {
  batchId: string; token: string; payslipRows: number; calculationSources: number; deliveryRecords: number
  mailJobs: number; requestReceipts: number; notifications: number; notificationLogs: number
  masterSetupReceipts: number; derivedBatches: number; originalLinks: number; otherBatchOriginalLinks: number; blockers: string[]; canDelete: boolean
  canDiscardUnusedLegacy?: boolean
  legacyStorageRecords: number; cleanupAuditRecords: number; isLegacy: boolean; legacyReviewValid: boolean; legacyReviewStatus: string; recordedParentId: string
  objects: { kind: string; id: string; status: string; reason: string; action: string; count: number; label: string }[]
  candidates: { id: string; month: string; fileName: string; sheetName: string; rowCount: number; createdAtUtc: string }[]; candidateCount: number
}
export type LegacyVersionReview = { batchId: string; keeperBatchId: string; token: string; evidence: string; oldRows: number; keeperRows: number; changedRows: number; exactSnapshot: boolean; sameCalculationSource: boolean; blockers: string[]; eligible: boolean }
export type CleanupActionRequest = { requestId: string; action: string; token: string; confirmBatchId: string; objectId: string; confirmObjectId: string; keeperBatchId: string; acknowledged: boolean }
const path = (batchId: string) => `/api/excel-payslips/batches/${encodeURIComponent(batchId)}`
export async function retainOriginalWorkbook(clientId: number, file: File) {
  const form = new FormData(); form.append('file', file)
  return postForm<OriginalWorkbook | null>(`/api/excel-payslips/originals?clientId=${clientId}`, form, null, { toast: false, timeoutMs: 120000 })
}
export async function saveBatchWithOriginalWorkbooks(clientId: number, batch: ExcelPayslipBatchInput, files: File[], retained: RetainedWorkbookCache, isCurrent: () => boolean, onProgress?: (stage:string)=>void): Promise<ApiResult<ExcelPayslipBatch | null>> {
  const cancelled = (): ApiResult<ExcelPayslipBatch | null> => ({ ok: false, data: null, status: 0, error: 'Import scope changed. No batch save was requested.' })
  try {
    if (!isCurrent()) return cancelled()
    let originals: OriginalWorkbookReference[] | undefined
    if (!batch.parentBatchId && !batch.calculationSource?.sourceBatchId && !files.length) throw new Error('Choose the original workbook again before saving this import.')
    if (files.length) {
      originals = []
      for (const [index,file] of files.entries()) {
        if (!isCurrent()) return cancelled()
        let cached = retained.get(file)
        if (cached?.clientId !== clientId) {
          onProgress?.(`Saving original Excel file ${index+1} of ${files.length}...`)
          const upload = await retainOriginalWorkbook(clientId, file)
          if (!isCurrent()) return cancelled()
          if (!upload.ok || !upload.data) throw new Error(upload.error || 'Original workbook could not be retained. No batch was saved.')
          cached = { clientId, reference: { id: upload.data.id, fileName: upload.data.fileName }, sha256: upload.data.sha256 }; retained.set(file, cached)
        }
        originals.push(cached.reference)
      }
    }
    let calculationSource = batch.calculationSource
    if (calculationSource?.captureVersion === 1) {
      const workbookFiles = (calculationSource.workbookFiles || []).map(binding => {
        const cached = [...retained.values()].find(item => item.clientId === clientId && item.sha256 === binding.sha256)
        const originalId = cached?.reference.id || binding.originalId
        if (!originalId) throw new Error('Retain the exact dependency workbook before saving. File names alone cannot bind its version.')
        return { ...binding, originalId }
      })
      if (!workbookFiles.length) throw new Error('Captured workbook bindings are unavailable. Re-supply the original source.')
      calculationSource = { ...calculationSource, workbookFiles }
    }
    if (!isCurrent()) return cancelled()
    onProgress?.('Saving payslip amounts, formulas and linked sheets...')
    return await saveExcelPayslipBatch(clientId, { ...batch, calculationSource, originalWorkbooks: originals })
  } catch (failure) {
    return { ok: false, data: null, status: 0, error: failure instanceof Error ? failure.message : 'Unable to retain original workbook. No batch was saved.' }
  }
}
export const getOriginalWorkbooks = (clientId: number, batchId: string) => getJsonResult<OriginalWorkbook[]>(`${path(batchId)}/originals?clientId=${clientId}`, [], { toast: false })
export const previewBatchDelete = (clientId: number, batchId: string) => getJsonResult<BatchDeletePreview | null>(`${path(batchId)}/delete-preview?clientId=${clientId}`, null, { toast: false })
export const deleteUnusedBatch = (clientId: number, batchId: string, token: string, confirmBatchId: string, discardUnusedLegacy = false) => postJson(`${path(batchId)}/delete?clientId=${clientId}`, { token, confirmBatchId, ...(discardUnusedLegacy ? { discardUnusedLegacy: true } : {}) }, { deleted: false, alreadyDeleted: false }, { toast: false })
export const reviewLegacyVersion = (clientId: number, batchId: string, keeperBatchId: string) => postJson<{ keeperBatchId: string }, LegacyVersionReview | null>(`${path(batchId)}/cleanup-review?clientId=${clientId}`, { keeperBatchId }, null, { toast: false })
export const resolveCleanupBlocker = (clientId: number, batchId: string, request: CleanupActionRequest) => postJson<CleanupActionRequest, { applied: boolean; alreadyApplied: boolean; preview: BatchDeletePreview | null }>(`${path(batchId)}/cleanup-action?clientId=${clientId}`, request, { applied: false, alreadyApplied: false, preview: null }, { toast: false })
export async function downloadOriginalWorkbook(clientId: number, batchId: string, original: OriginalWorkbook) {
  const response = await apiRequest(`${path(batchId)}/originals/${encodeURIComponent(original.id)}?clientId=${clientId}`, { toast: false })
  if (!response.ok) throw new Error(await readError(response))
  const url = URL.createObjectURL(await response.blob()), anchor = document.createElement('a')
  anchor.href = url; anchor.download = original.fileName; document.body.append(anchor); anchor.click(); anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
