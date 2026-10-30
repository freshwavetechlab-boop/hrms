import type { PayrollSetupRequest, TransferReview } from '../services/excelPayrollSetupService'

export function explainedRoundingReviewRows(request: PayrollSetupRequest, transfer: TransferReview) {
  const rows = transfer.amountComparisons || []
  if (transfer.version !== 1 || transfer.sourceBatchId !== request.batchId || !/^[a-f0-9]{64}$/i.test(transfer.sourceHash)
    || !request.rowIds.length || request.rowIds.some(id => !id.trim()) || new Set(request.rowIds).size !== request.rowIds.length) return []
  const selected = new Set(request.rowIds), counts = new Map<string, number>()
  for (const row of rows) counts.set(row.rowId, (counts.get(row.rowId) || 0) + 1)
  return rows.filter(row => selected.has(row.rowId) && counts.get(row.rowId) === 1 && row.version === 'source-core-currency-v1'
    && row.status === 'Explained rounding' && Number.isSafeInteger(row.sourceRow) && row.sourceRow > 0 && /^[a-f0-9]{64}$/i.test(row.proofHash))
}

export function explainedRoundingReviewChange(request: PayrollSetupRequest, transfer: TransferReview) {
  const roundingReviews = { ...request.roundingReviews }
  for (const row of explainedRoundingReviewRows(request, transfer)) roundingReviews[row.rowId] = row.proofHash
  return { roundingReviews }
}
