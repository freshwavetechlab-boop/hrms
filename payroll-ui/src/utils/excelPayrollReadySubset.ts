import type { PayrollSetupContext } from '../services/excelPayrollSetupService'

export type MasterReadySubset = { canContinue: boolean; readyRowIds: string[]; reviewRowIds: string[]; appliedRowIds: string[]; reason: string }

// This is an explicit UI selection proposal, never a source exclusion or an
// identity override. Applied receipt cohorts remain indivisible.
export function masterReadySubset(context: PayrollSetupContext, rowIds: string[]): MasterReadySubset {
  const result: MasterReadySubset = { canContinue: false, readyRowIds: [], reviewRowIds: [], appliedRowIds: [], reason: '' }
  const requested = new Set(rowIds)
  if (!rowIds.length || requested.size !== rowIds.length || !context.roster) return { ...result, reason: 'Refresh employee readiness before changing this selection.' }
  const rows = rowIds.map(id => context.roster!.rows.filter(row => row.rowId === id))
  if (rows.some(matches => matches.length !== 1)) return { ...result, reason: 'Some selected employees are missing or ambiguous in the current readiness check. Refresh before continuing.' }
  const applied = new Set((context.appliedSetups || []).flat())
  for (const [row] of rows) {
    if (row.status === 'Applied' || applied.has(row.rowId)) result.appliedRowIds.push(row.rowId)
    else if (row.status === 'Review') result.reviewRowIds.push(row.rowId)
    else if (row.status === 'New' || row.status === 'Existing') result.readyRowIds.push(row.rowId)
    else return { ...result, reason: 'Employee readiness changed. Refresh before continuing.' }
  }
  if (!result.reviewRowIds.length || !result.readyRowIds.length) return result
  for (const cohort of context.appliedSetups || []) {
    const included = cohort.filter(id => requested.has(id))
    if (included.length && included.length !== cohort.length) return { ...result, reason: 'This selection contains part of an already saved setup. Open its complete saved configuration before changing employee scope.' }
  }
  return { ...result, canContinue: true }
}
