import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupResult } from '../services/excelPayrollSetupService'

export type AppliedContextScope = {
  clientId: number
  batch: Pick<ExcelPayslipBatch, 'id' | 'clientId' | 'month'>
  rowIds: string[]
  requestId: string
  applied: PayrollSetupResult
}
type ContextResponse = { ok: boolean; data: PayrollSetupContext | null; error?: string }
type ContextLoader = (clientId: number, batchId: string, rowIds: string[]) => Promise<ContextResponse>

const sameUniqueRows = (left: string[], right: string[]) => {
  if (!left.length || left.length !== right.length || new Set(left).size !== left.length || new Set(right).size !== right.length) return false
  const sortedRight = [...right].sort()
  return [...left].sort().every((id, index) => id === sortedRight[index])
}
const sameEmployees = (left: number[], right: number[]) => new Set(left).size === left.length
  && new Set(right).size === right.length && [...left].sort((a, b) => a - b).join('|') === [...right].sort((a, b) => a - b).join('|')

// This confirms receipt/scope freshness only. Existing identity and runtime
// verification helpers still decide which employees may be calculated.
export function matchesAppliedPayrollSetupContext(scope: AppliedContextScope, context: PayrollSetupContext) {
  const saved = context.lastImport
  if (scope.clientId !== scope.batch.clientId || !scope.applied.importId || !scope.requestId
    || !/^\d{4}-(0[1-9]|1[0-2])$/.test(scope.batch.month)
    || !saved || saved.status !== 'Applied' || saved.requestId !== scope.requestId
    || saved.importId !== scope.applied.importId || saved.result?.importId !== scope.applied.importId
    || saved.savedRequest.batchId !== scope.batch.id
    || !sameUniqueRows(scope.rowIds, saved.savedRequest.rowIds)
    || !sameEmployees(scope.applied.employeeIds, saved.result.employeeIds)) return false
  for (const transfer of [scope.applied.transfer, saved.result.transfer]) {
    if (transfer && (transfer.sourceBatchId !== scope.batch.id || transfer.sourceMonth !== scope.batch.month)) return false
  }
  for (const allocation of [saved.savedRequest.locationAllocations, scope.applied.locationAllocations, saved.result.locationAllocations]) {
    if (allocation && allocation.payrollPeriod !== scope.batch.month) return false
  }
  return true
}

export async function refreshAppliedPayrollSetupContext(scope: AppliedContextScope, load: ContextLoader): Promise<{ context: PayrollSetupContext | null; warning: string }> {
  const warning = `Setup was saved as import ${scope.applied.importId}, but its current verification context is unavailable. Reopen this exact saved setup before verification. Do not Apply the configuration again.`
  try {
    const result = await load(scope.clientId, scope.batch.id, [...scope.rowIds])
    if (!result.ok || !result.data || !matchesAppliedPayrollSetupContext(scope, result.data)) return { context: null, warning }
    return { context: result.data, warning: '' }
  } catch {
    // A follow-up GET failure does not undo or invalidate a confirmed Apply.
    return { context: null, warning }
  }
}
