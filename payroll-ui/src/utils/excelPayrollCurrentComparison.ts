import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupPreview, PayrollSetupVerification } from '../services/excelPayrollSetupService'

const code = (value: string) => value.trim().toUpperCase()
const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value)

// Both existing identity checks must agree. This never creates or repairs an identity.
export function currentPayrollTargets(clientId: number, batch: ExcelPayslipBatch, context: PayrollSetupContext, preview: PayrollSetupPreview) {
  const roster = context.roster?.rows || []
  return preview.rows.map(plan => {
    const sources = batch.rows.filter(row => row.id === plan.rowId)
    const source = sources.length === 1 ? sources[0] : undefined
    const matches = roster.filter(row => row.rowId === plan.rowId)
    const match = matches.length === 1 ? matches[0] : undefined
    const reasons: string[] = []
    if (batch.clientId !== clientId || preview.rows.length > 100) reasons.push('Client or bounded cohort scope is invalid.')
    if (!source || preview.rows.filter(row => row.rowId === plan.rowId).length !== 1) reasons.push('Missing or repeated saved source row.')
    if (!match || !['Existing', 'Applied'].includes(match.status) || !match.employeeId) reasons.push('Current Master Setup identity requires review; no employee will be created.')
    if (!source?.employment?.employeeId || source.employment.identityReviewRequired || source.employment.blockedForMonth) reasons.push('Current saved-batch employment identity is missing, ambiguous or blocked.')
    if (match?.employeeId !== source?.employment?.employeeId || plan.employeeId !== match?.employeeId) reasons.push('Overview, setup and preview employee identities do not agree.')
    if (!source?.employeeCode.trim() || batch.rows.filter(row => code(row.employeeCode) === code(source?.employeeCode || '')).length !== 1) reasons.push('Missing or duplicate source employee code requires review.')
    if (match?.employeeId && roster.filter(row => row.employeeId === match.employeeId).length !== 1) reasons.push('Several source rows refer to this master employee.')
    return { rowId: plan.rowId, sourceRow: source?.sourceRow, employeeCode: source?.employeeCode || '', employeeId: match?.employeeId || null, eligible: reasons.length === 0, reasons, plan, source }
  })
}

export function compareCurrentPayroll(targets: ReturnType<typeof currentPayrollTargets>, preview: PayrollSetupPreview, observations: PayrollSetupVerification[]) {
  return targets.map(target => {
    const matches = observations.filter(item => item.employeeId === target.employeeId)
    const observed = matches.length === 1 ? matches[0] : undefined
    const data = observed?.data
    const reasons = [...target.reasons]
    if (target.eligible && (!observed?.ok || !data?.complete || data.mode !== 'Employee' || data.issues.some(issue => issue.severity === 'Error'))) {
      reasons.push(observed?.error || data?.issues.filter(issue => issue.severity === 'Error').map(issue => `${issue.component}: ${issue.message}`).join('; ') || 'A complete current Employee simulation is unavailable.')
    }
    const days = ([['PAYROLL_DAYS', target.plan.payrollDays], ['PRESENT_DAYS', target.plan.presentDays], ['PAYABLE_DAYS', target.plan.payableDays]] as const).map(([token, source]) => {
      const values = data?.inputs.filter(input => input.name === token) || []
      const actual = values.length === 1 && finite(values[0].value) ? values[0].value : null
      const evidence = token === 'PRESENT_DAYS' ? target.plan.presentDaysEvidence : undefined
      const reviewedPresent = evidence?.actualWorkedReviewed === true && evidence.status === 'ReviewedWorkedDays'
        && Number.isInteger(evidence.sourceColumnIndex) && evidence.sourceColumnIndex! >= 0 && !!evidence.sourceCell
        && finite(evidence.sourceValue) && evidence.sourceValue === source && evidence.calculationValue === source
      const comparableSource = token === 'PRESENT_DAYS' && !reviewedPresent ? null : source
      const sourceEvidence = token === 'PRESENT_DAYS'
        ? reviewedPresent ? `Reviewed actual worked days (${evidence!.sourceCell}).` : evidence?.message || 'Actual worked-days evidence is unavailable in this preview. Refresh the API preview, map an actual worked column and review its meaning; payable fallback is not source attendance.'
        : 'Reviewed source setup mapping; see attendance proof for period and dependency verification.'
      if (comparableSource === null) reasons.push(`${token}: source actual-worked mapping is unverified; comparison remains unresolved.`)
      else if (actual === null || actual !== comparableSource) reasons.push(`${token}: current saved attendance differs or is missing.`)
      return { token, source: comparableSource, actual, calculationValue: source, sourceEvidence, origin: values.length === 1 ? values[0].source : 'Missing / ambiguous' }
    })
    const lines = target.plan.lines.map(line => {
      const actual = data?.lines.filter(item => code(item.code) === code(line.code)) || []
      const categories = preview.transfer?.components.filter(item => code(item.code) === code(line.code)) || []
      const amount = actual.length === 1 && finite(actual[0].earned) ? actual[0].earned : null
      const source = finite(line.sourceAmount) ? line.sourceAmount : null
      if (source === null || amount === null) reasons.push(`${line.code}: source/current component missing or code mapping ambiguous.`)
      if (categories.length !== 1 || actual.length !== 1 || categories[0].category !== actual[0].category) reasons.push(`${line.code}: source/current classification is unavailable or different.`)
      if (source !== null && amount !== null && source !== amount) reasons.push(`${line.code}: raw amounts differ; inspect rounding and formula.`)
      return { code: line.code, source, actual: amount, delta: source !== null && amount !== null ? amount - source : null, category: actual.length === 1 ? actual[0].category : '', formula: actual.length === 1 ? actual[0].formula : '', raw: actual.length === 1 ? actual[0].raw : null, rounding: actual.length === 1 ? actual[0].rounding : '' }
    })
    const expectedCodes = new Set(target.plan.lines.map(line => code(line.code)))
    for (const line of data?.lines || []) if (!expectedCodes.has(code(line.code))) {
      reasons.push(`${line.code}: current payroll has an additional or unmapped source component.`)
      lines.push({ code: line.code, source: null, actual: line.earned, delta: null, category: line.category, formula: line.formula, raw: line.raw, rounding: line.rounding })
    }
    if (!target.plan.lines.length || expectedCodes.size !== target.plan.lines.length) reasons.push('Source component coverage is missing or ambiguous.')
    const totals = ([['gross', ['Earning', 'Reimbursement']], ['deductions', ['Deduction']], ['benefits', ['Benefit']]] as const).map(([field, categories]) => {
      const values = target.plan.lines.filter(line => {
        const classification = preview.transfer?.components.filter(item => code(item.code) === code(line.code)) || []
        return classification.length === 1 && (categories as readonly string[]).includes(classification[0].category)
      })
      const source = values.every(line => finite(line.sourceAmount)) ? values.reduce((sum, line) => sum + line.sourceAmount!, 0) : null
      const actual = finite(data?.[field]) ? data[field] : null
      if (source === null || actual === null || source !== actual) reasons.push(`${field}: raw component aggregate differs or is unavailable.`)
      return { field, source, actual, delta: source !== null && actual !== null ? actual - source : null }
    })
    const sourceNet = finite(target.source?.netPay) ? target.source.netPay : null
    const actualNet = finite(data?.net) ? data.net : null
    if (sourceNet === null || actualNet === null || sourceNet !== actualNet) reasons.push('Net raw values differ or are unavailable; display rounding is shown separately.')
    return { ...target, data, days, lines, totals, sourceNet, actualNet, netDelta: sourceNet !== null && actualNet !== null ? actualNet - sourceNet : null, reasons: [...new Set(reasons)], status: !target.eligible || !observed?.ok || !data?.complete || data.mode !== 'Employee' || data.issues.some(issue => issue.severity === 'Error') ? 'Blocked' : reasons.length ? 'Differences to review' : 'Raw values and mapped days equal' }
  })
}

// Selection metadata survives per-row arithmetic failures. Missing calculations
// stay source/configuration blockers, never zero employees or eligible targets.
export function currentPayrollSelection(batch: ExcelPayslipBatch, preview: PayrollSetupPreview) {
  const references = preview.selectedSourceRows
  const scope = references ?? preview.rows.map(row => ({ rowId: row.rowId, sourceRow: batch.rows.find(item => item.id === row.rowId)?.sourceRow ?? null, sourceFound: true }))
  const seen = new Set<string>()
  const blocked = scope.filter(reference => !preview.rows.some(row => row.rowId === reference.rowId)).filter(reference => {
    if (seen.has(reference.rowId)) return false
    seen.add(reference.rowId); return true
  }).map(reference => {
    const issues = preview.issues?.filter(issue => issue.severity === 'Error' && issue.rowId === reference.rowId).map(issue => issue.message) || []
    return { ...reference, reasons: reference.sourceFound === false ? 'Saved source row is missing or ambiguous; refresh this batch selection.' : issues.join(' ') || 'Source arithmetic is unavailable. Resolve source input/formula mapping and repreview before current payroll comparison.' }
  })
  return { selectedCount: references !== undefined || preview.rows.length ? scope.length : null, blocked }
}
