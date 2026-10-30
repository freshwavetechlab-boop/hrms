import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupResult } from '../services/excelPayrollSetupService'

const normalized = (value?: string) => (value || '').trim().toUpperCase()
// Revalidate current identities against the saved receipt. Array order is never identity evidence.
// This only selects calculation targets; it cannot create, repair or update an employee.
export function savedPayrollVerificationTargets(clientId: number, batch: ExcelPayslipBatch, rowIds: string[], context: PayrollSetupContext, result: PayrollSetupResult) {
  const saved = context.lastImport, common: string[] = []
  if (batch.clientId !== clientId || !/^\d{4}-(0[1-9]|1[0-2])$/.test(batch.month)) common.push('Client or source month is invalid.')
  if (context.calculationOnlySimulationSupported !== true) common.push('Calculation-only runtime guard is unavailable; refresh the API context before simulation.')
  if (!rowIds.length || rowIds.length > 100 || new Set(rowIds).size !== rowIds.length) common.push('Select a unique saved cohort of 1–100 source rows.')
  if (!saved || saved.status !== 'Applied' || saved.importId !== result.importId || saved.result?.importId !== result.importId || saved.savedRequest.batchId !== batch.id
    || [...saved.savedRequest.rowIds].sort().join('|') !== [...rowIds].sort().join('|')) common.push('Applied receipt does not match this saved batch and exact selected cohort; reopen its saved setup.')
  const roster = context.roster?.rows || []
  return rowIds.map(rowId => {
    const reasons = [...common], sources = batch.rows.filter(row => row.id === rowId), source = sources.length === 1 ? sources[0] : undefined
    const matches = roster.filter(row => row.rowId === rowId), match = matches.length === 1 ? matches[0] : undefined
    const id = match?.employeeId || null, masters = context.employees.filter(employee => employee.id === id), master = masters.length === 1 ? masters[0] : undefined
    if (!source || batch.rows.filter(row => row.sourceRow === source.sourceRow).length !== 1) reasons.push('Missing or repeated saved source row; review source identity.')
    if (!match || !['Existing', 'Applied'].includes(match.status) || !id) reasons.push('Current Master Setup identity requires review; no employee will be created.')
    if (!source?.employment?.employeeId || source.employment.identityReviewRequired || source.employment.blockedForMonth) reasons.push('Current saved-batch employment link is missing, ambiguous or blocked for this period.')
    if (source?.employment?.employeeId !== id || !master) reasons.push('Saved-batch, setup and current master identities do not agree.')
    if (!normalized(source?.employment?.employeeCode) || normalized(source?.employment?.employeeCode) !== normalized(master?.employeeCode)) reasons.push('The current master-code alias is missing or changed; recheck the employment link.')
    if (!normalized(source?.employeeCode) || batch.rows.filter(row => normalized(row.employeeCode) === normalized(source?.employeeCode)).length !== 1) reasons.push('Missing or repeated source employee code requires review.')
    if (id && (roster.filter(row => row.employeeId === id).length > 1 || batch.rows.filter(row => row.employment?.employeeId === id).length > 1)) reasons.push('Several source rows refer to the same current master employee.')
    if (!id || result.employeeIds.filter(value => value === id).length !== 1) reasons.push('This employee is missing or repeated in the saved import receipt.')
    const transferRows = result.transferRows?.filter(row => row.rowId === rowId) || []
    const decisions = saved?.savedRequest.employeeDecisions.filter(row => row.rowId === rowId) || []
    const receiptBound = transferRows.length ? transferRows.length === 1 && transferRows[0].employeeId === id
      : decisions.length === 1 && decisions[0].matchedEmployeeId === id
    if (!receiptBound || (transferRows.length && decisions.some(row => row.matchedEmployeeId && row.matchedEmployeeId !== id))) reasons.push('No unambiguous source-row-to-employee binding exists in the saved receipt; re-review this row before comparison.')
    return { rowId, sourceRow: source?.sourceRow, employeeCode: source?.employeeCode || '', employeeId: id, source, eligible: reasons.length === 0, reasons }
  })
}
