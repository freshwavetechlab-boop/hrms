import type { PayrollSetupContext, PayrollSetupRequest, PayrollSetupResult, PayrollSetupVerification, TransferAmountReview } from '../services/excelPayrollSetupService'

// Proposed additive client provenance. The service must retain the exact object
// actually passed to postJson, never reconstruct it later at the comparison call.
export type TransferVerificationRequest = {
  mode: 'Employee'; clientId: number; employeeId: number; payPeriod: string
  payrollDays: null; presentDays: null; payableDays: null; annualCtc: null
  amountOverrides: Record<string, number>
}
type CapturedVerification = PayrollSetupVerification & { request?: TransferVerificationRequest }
export type TransferComparisonContext = { clientId: number; saved: PayrollSetupContext['lastImport'] }
const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value)
const precise = (value: number) => Number(value.toPrecision(15))
const same = (left: unknown, right: unknown) => finite(left) && finite(right) && precise(left) === precise(right)
const code = (value: string) => value.trim().toUpperCase()
const hash = (value: unknown) => typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value)
const unique = (values: unknown[]) => new Set(values).size === values.length
const sameIds = (left: string[], right: string[]) => {
  if (left.length !== right.length || !unique(left) || !unique(right)) return false
  const sortedRight = [...right].sort()
  return [...left].sort().every((id, index) => id === sortedRight[index])
}
const canonical = (value: unknown): string => JSON.stringify(value, (_, item) => item && typeof item === 'object' && !Array.isArray(item) ? Object.fromEntries(Object.keys(item).sort().map(key => [key, item[key]])) : item)

// Match the backend's existing G15 representation boundary, then decimal
// arithmetic. This is used to validate a persisted proof, never as a tolerance.
function decimal(value: number, sourceBoundary = true) {
  const [mantissa, exponent = '0'] = String(sourceBoundary ? precise(value) : value).toLowerCase().split('e')
  const [whole, fraction = ''] = mantissa.split('.')
  let units = BigInt(whole + fraction), scale = fraction.length - Number(exponent)
  if (scale < 0) { units *= 10n ** BigInt(-scale); scale = 0 }
  return { units, scale }
}
function decimalSum(values: number[], sourceBoundary = true) {
  const parts = values.map(value => decimal(value, sourceBoundary)), scale = Math.max(0, ...parts.map(part => part.scale))
  const units = parts.reduce((sum, part) => sum + part.units * 10n ** BigInt(scale - part.scale), 0n)
  return Number(units) / 10 ** scale
}
// A stored JSON number may normalize an original decimal tail independently
// from its already-computed diagnostic delta. Reconstruct the source operand
// first, then apply the existing G15 source boundary. Do not round the operands
// before subtraction or treat an arbitrary small financial mismatch as rounding.
function rawDiagnosticMatches(source: unknown, calculated: unknown, delta: unknown) {
  return finite(source) && finite(calculated) && finite(delta)
    && same(source, decimalSum([calculated, -delta], false))
}
function currency(value: number, midpointToEven: boolean) {
  const part = decimal(Math.abs(value))
  if (part.scale <= 2) return Number(part.units * 10n ** BigInt(2 - part.scale)) / 100 * Math.sign(value)
  const divisor = 10n ** BigInt(part.scale - 2), quotient = part.units / divisor, remainder = part.units % divisor
  const roundUp = remainder * 2n > divisor || remainder * 2n === divisor && (!midpointToEven || quotient % 2n !== 0n)
  return Number(quotient + (roundUp ? 1n : 0n)) / 100 * Math.sign(value)
}
function cents(value: unknown): bigint | null {
  if (!finite(value) || value < 0) return null
  const part = decimal(value)
  if (part.scale <= 2) return part.units * 10n ** BigInt(2 - part.scale)
  const divisor = 10n ** BigInt(part.scale - 2)
  return part.units % divisor === 0n ? part.units / divisor : null
}

function appliedBinding(result: PayrollSetupResult, comparison: TransferComparisonContext) {
  const saved = comparison.saved, transfer = result.transfer, request = saved?.savedRequest
  return Number.isSafeInteger(comparison.clientId) && comparison.clientId > 0 && !!result.importId
    && !!saved && saved.status === 'Applied' && saved.importId === result.importId && saved.result?.importId === result.importId
    && !!request && request.transferReviewVersion === 1 && transfer?.version === 1
    && request.batchId === transfer.sourceBatchId && /^\d{4}-(0[1-9]|1[0-2])$/.test(transfer.sourceMonth)
    && request.effectiveFrom === transfer.effectiveFrom && hash(transfer.sourceHash)
    && sameIds(request.rowIds, (result.transferRows || []).map(row => row.rowId))
    && sameIds(result.employeeIds.map(String), (result.transferRows || []).map(row => String(row.employeeId)))
    && canonical(saved.result.transfer) === canonical(transfer)
    && canonical(saved.result.transferRows) === canonical(result.transferRows)
}
function simulationBinding(observed: CapturedVerification | undefined, employeeId: number, comparison: TransferComparisonContext, month: string, allowUnknownPresent = false) {
  const request = observed?.request, data = observed?.data, evidence = data?.attendanceEvidence
  return !!request && request.mode === 'Employee' && request.clientId === comparison.clientId && request.employeeId === employeeId && request.payPeriod === month
    && Object.keys(request).sort().join('|') === ['mode', 'clientId', 'employeeId', 'payPeriod', 'payrollDays', 'presentDays', 'payableDays', 'annualCtc', 'amountOverrides'].sort().join('|')
    && request.payrollDays === null && request.presentDays === null && request.payableDays === null && request.annualCtc === null
    && !!request.amountOverrides && typeof request.amountOverrides === 'object' && !Array.isArray(request.amountOverrides) && Object.keys(request.amountOverrides).length === 0
    && observed?.ok === true && data?.complete === true && data.mode === 'Employee' && data.clientId === comparison.clientId && data.employeeId === employeeId && data.payPeriod === month
    && !data.issues.some(issue => issue.severity === 'Error') && !!evidence && evidence.payPeriod === month && evidence.temporaryDayOverrides === false && !evidence.policyValidationError
    && data.locationAllocations == null
    && ([['PAYROLL_DAYS', evidence.workingDays], ['PRESENT_DAYS', evidence.presentDays], ['PAYABLE_DAYS', evidence.payableDays]] as const).every(([name, amount]) => {
      const inputs = data.inputs.filter(input => input.name === name)
      if (name === 'PRESENT_DAYS' && evidence.actualPresenceKnown === false && amount !== null) return false
      if (name === 'PRESENT_DAYS' && allowUnknownPresent && amount === null) return evidence.actualPresenceKnown === false && evidence.sourceKind === 'Reviewed source payable-only' && inputs.length === 1 && inputs[0].value === null
      return finite(amount) && inputs.length === 1 && same(inputs[0].value, amount)
    })
    && data.inputs.every(input => !/what-if/i.test(input.source || ''))
}
function unmappedPresent(request: PayrollSetupRequest) {
  return request.importAttendance === false && request.presentDaysColumnIndex == null
    && request.components.every(component => ![component.formula, ...Object.values(component.rowFormulas || {})].some(formula => /\bPRESENT_DAYS\b/i.test(formula || '')))
}

function reviewedPayableOnly(result: PayrollSetupResult, source: NonNullable<PayrollSetupResult['transferRows']>[number], request: PayrollSetupRequest) {
  const transfer = result.transfer!, review = request.attendanceReview
  const binding = [review?.mode || '', request.payrollDaysColumnIndex, request.fixedPayrollDays ?? '-', request.payableDaysColumnIndex, request.presentDaysColumnIndex ?? '-', request.attendanceCycleStartDay, request.attendanceCycleEndDay, request.importAttendance !== false ? '1' : '0'].join('|') + (request.payrollDaysCell ? '|cell:' + request.payrollDaysCell.replace(/\$/g, '').trim().toUpperCase() : '') + '|payableOnly:1'
  const proofs = transfer.attendanceProofs?.filter(proof => proof.rowId === source.rowId) || [], proof = proofs[0]
  return request.importPayableOnlyAttendance === true && request.importAttendance !== false && request.presentDaysColumnIndex == null && source.presentDays === null
    && !!review && review.mode === 'SourceSeed' && review.reviewed === true && review.boundSourceInputs === binding && review.payableMeaning === 'AlreadyPayable' && review.presentMeaning === 'NotMapped'
    && review.sourceHash === transfer.sourceHash && review.sourceMonth === transfer.sourceMonth && /^\d{4}-\d{2}-\d{2}$/.test(review.cycleStart) && /^\d{4}-\d{2}-\d{2}$/.test(review.cycleEnd) && review.cycleStart <= review.cycleEnd
    && proofs.length === 1 && proof.mode === 'SourceSeed' && proof.status === 'Reviewed payable-only source seed - native pending' && proof.sourceHash === transfer.sourceHash && hash(proof.inputHash) && proof.presentDays === null
    && Number.isSafeInteger(proof.sourceRow) && proof.sourceRow > 0 && same(proof.payrollDays, source.payrollDays) && same(proof.payableDays, source.payableDays)
    && transfer.formulas.filter(formula => formula.rowId === source.rowId).every(formula => formula.sourceRow === proof.sourceRow)
    && request.components.every(component => ![component.formula, ...Object.values(component.rowFormulas || {})].some(formula => /\bPRESENT_DAYS\b/i.test(formula || '')))
}

function roundingProof(result: PayrollSetupResult, source: NonNullable<PayrollSetupResult['transferRows']>[number], observed: CapturedVerification, request: PayrollSetupRequest): TransferAmountReview | null {
  const transfer = result.transfer!, data = observed.data!, proofs = transfer.amountComparisons?.filter(proof => proof.rowId === source.rowId) || [], proof = proofs[0]
  if (proofs.length !== 1 || proof.version !== 'source-core-currency-v1' || proof.status !== 'Explained rounding' || proof.acknowledged !== true
    || !hash(proof.proofHash) || request.roundingReviews?.[source.rowId] !== proof.proofHash || !Number.isSafeInteger(proof.sourceRow) || proof.sourceRow < 1
    || !same(proof.sourceNetRaw, source.sourceNet) || proof.sourceNetDisplay !== currency(proof.sourceNetRaw, false)
    || !source.lines.length || !unique(source.lines.map(line => code(line.code))) || !unique(proof.lines.map(line => code(line.code)))
    || proof.lines.length !== source.lines.length || data.lines.length !== source.lines.length || !unique(data.lines.map(line => code(line.code)))) return null
  for (const sourceLine of source.lines) {
    const key = code(sourceLine.code), proofLines = proof.lines.filter(line => code(line.code) === key), actualLines = data.lines.filter(line => code(line.code) === key)
    const classifications = transfer.components.filter(line => code(line.code) === key), components = request.components.filter(component => code(component.code) === key)
    const formulas = transfer.formulas.filter(formula => formula.rowId === source.rowId && code(formula.code) === key)
    const line = proofLines[0], actual = actualLines[0], component = components[0], formula = formulas[0]
    if (proofLines.length !== 1 || actualLines.length !== 1 || classifications.length !== 1 || components.length !== 1 || formulas.length !== 1
      || !['Earning', 'Reimbursement', 'Deduction', 'Benefit'].includes(line.category) || classifications[0].category !== line.category || actual.category !== line.category || component.category !== line.category
      || component.isPeriodAdjustment !== false || formula.status !== 'Exact supported translation' || formula.sourceRow !== proof.sourceRow || formula.sourceColumn !== component.expectedColumnIndex || !hash(formula.sourceHash)
      || !same(line.sourceRaw, sourceLine.sourceAmount) || !same(line.sourceRaw, line.coreUnrounded) || !same(actual.unroundedEarned, line.coreUnrounded)
      || !finite(line.sourceRaw) || line.sourceRaw < 0 || !finite(line.coreUnrounded) || line.coreUnrounded < 0
      || line.sourceDisplay !== currency(line.sourceRaw, false) || line.calculated !== currency(line.coreUnrounded, true)
      || cents(line.calculated) === null || actual.earned !== line.calculated || sourceLine.calculatedAmount !== line.calculated
      || !rawDiagnosticMatches(line.sourceRaw, line.calculated, line.rawDelta) || !same(line.displayDelta, decimalSum([line.calculated, -line.sourceDisplay]))) return null
  }
  const selected = (categories: string[]) => proof.lines.filter(line => categories.includes(line.category))
  const total = (categories: string[]) => Number(selected(categories).reduce((sum, line) => sum + cents(line.calculated)!, 0n)) / 100
  const rawGross = selected(['Earning', 'Reimbursement']).map(line => line.sourceRaw), rawDeductions = selected(['Deduction']).map(line => -line.sourceRaw)
  const gross = total(['Earning', 'Reimbursement']), deductions = total(['Deduction']), benefits = total(['Benefit'])
  const net = Number((cents(gross)! - cents(deductions)!) > 0n ? cents(gross)! - cents(deductions)! : 0n) / 100
  if (!same(source.sourceNet, Math.max(0, decimalSum([...rawGross, ...rawDeductions], false))) || data.gross !== gross || data.deductions !== deductions || data.benefits !== benefits
    || data.net !== net || proof.coreNet !== net || !rawDiagnosticMatches(proof.sourceNetRaw, net, proof.rawNetDelta) || !same(proof.displayNetDelta, decimalSum([net, -proof.sourceNetDisplay]))) return null
  return proof
}

// Current Employee observations only. Two-argument callers retain the original
// raw comparator. Exceptions require the exact same Applied server receipt and
// a no-override request envelope captured by the fresh verification call.
export function compareTransferPayroll(result: PayrollSetupResult, observations: CapturedVerification[], comparison?: TransferComparisonContext) {
  const tolerance = result.transfer?.tolerance ?? 0.000001
  return (result.transferRows || []).map(source => {
    const matches = observations.filter(value => value.employeeId === source.employeeId), observed = matches.length === 1 ? matches[0] : undefined
    const data = observed?.data, reasons: string[] = [], notes: string[] = []
    const bound = !!comparison && appliedBinding(result, comparison)
    const payableOnly = bound && reviewedPayableOnly(result, source, comparison!.saved!.savedRequest)
    const scoped = bound && simulationBinding(observed, source.employeeId, comparison!, result.transfer!.sourceMonth, payableOnly)
    if (comparison && !bound) reasons.push('Applied receipt, saved cohort or immutable transfer proof no longer matches this comparison.')
    if (comparison && !scoped) reasons.push('A fresh same-client/month/employee Employee simulation with captured no-override request provenance is required.')
    const skipPresent = scoped && (payableOnly || unmappedPresent(comparison!.saved!.savedRequest))
    const explained = scoped && observed ? roundingProof(result, source, observed, comparison!.saved!.savedRequest) : null
    if (skipPresent) notes.push('Source actual worked/present days were not mapped or compared. Native present days remain an observation; full attendance parity is not established.')
    if (explained) notes.push('The acknowledged source-bound rounding proof matches current unrounded and rounded Employee component results. Raw differences remain visible.')
    if (explained) {
      for (const line of explained.lines) {
        const recomputed = decimalSum([line.calculated, -line.sourceRaw], false)
        if (line.rawDelta !== recomputed) notes.push(`${line.code}: stored raw diagnostic ${line.rawDelta} differs from exact decimal subtraction of the stored operands (${recomputed}). Both values are retained for review; the financial comparison uses the separately verified source precision and actual currency stages. The historical cause is not certified.`)
      }
      if (finite(explained.coreNet) && finite(explained.sourceNetRaw)) {
        const recomputed = decimalSum([explained.coreNet, -explained.sourceNetRaw], false)
        if (explained.rawNetDelta !== recomputed) notes.push(`Stored raw net diagnostic ${explained.rawNetDelta} differs from exact decimal subtraction of the stored operands (${recomputed}). Its historical cause is not certified; current raw and displayed net differences remain separate.`)
      }
    }
    if (!result.transfer || result.transfer.version !== 1 || result.transferRows?.filter(row => row.employeeId === source.employeeId).length !== 1) reasons.push('Missing or ambiguous durable transfer provenance.')
    if (!observed?.ok || !data?.complete) reasons.push(observed?.error || 'A complete saved Employee simulation is required.')
    if (data?.mode !== 'Employee' || data?.issues.some(issue => issue.severity === 'Error')) reasons.push('A valid regular Employee simulation is required, not template arithmetic.')
    const expected = new Map(source.lines.map(line => [line.code.toUpperCase(), line]))
    const lines = source.lines.map(line => {
      const actual = data?.lines.filter(item => item.code.toUpperCase() === line.code.toUpperCase()) || []
      const variance = line.sourceAmount != null && actual.length === 1 ? actual[0].earned - line.sourceAmount : null
      if (!explained && (variance == null || !Number.isFinite(variance) || Math.abs(variance) > tolerance)) reasons.push(`${line.code}: missing, ambiguous or different component amount.`)
      const classification = result.transfer?.components.filter(item => item.code.toUpperCase() === line.code.toUpperCase()) || []
      if (classification.length !== 1 || actual.length !== 1 || classification[0].category !== actual[0].category) reasons.push(`${line.code}: component classification differs or is unavailable.`)
      return { code: line.code, sourceAmount: line.sourceAmount, actual: actual.length === 1 ? actual[0].earned : null, variance }
    })
    for (const line of data?.lines || []) if (!expected.has(line.code.toUpperCase())) reasons.push(`${line.code}: regular payroll component has no source comparison.`)
    for (const [token, amount] of [['PAYROLL_DAYS', source.payrollDays], ['PRESENT_DAYS', source.presentDays], ['PAYABLE_DAYS', source.payableDays]] as const) {
      if (token === 'PRESENT_DAYS' && skipPresent) continue
      const values = data?.inputs.filter(input => input.name === token) || []
      if (values.length !== 1 || !finite(amount) || values[0].value == null || !Number.isFinite(values[0].value) || Math.abs(values[0].value - amount) > tolerance) reasons.push(`${token}: source-month attendance differs or is unavailable.`)
    }
    const netVariance = data?.net != null ? data.net - source.sourceNet : null
    if (!explained && (netVariance == null || !Number.isFinite(netVariance) || Math.abs(netVariance) > tolerance)) reasons.push('Net pay differs or is unavailable.')
    if (!source.lines.length || source.lines.some(line => line.sourceAmount == null)) reasons.push('Source component comparisons are incomplete.')
    if (new Set(source.lines.map(line => line.code.toUpperCase())).size !== source.lines.length) reasons.push('Source component codes are ambiguous.')
    for (const [field, categories] of [['gross', ['Earning', 'Reimbursement']], ['deductions', ['Deduction']], ['benefits', ['Benefit']]] as const) {
      const amount = source.lines.reduce((total, line) => {
        const category = result.transfer?.components.find(item => item.code.toUpperCase() === line.code.toUpperCase())?.category
        return total + (category && (categories as readonly string[]).includes(category) ? line.sourceAmount ?? 0 : 0)
      }, 0)
      if (!explained && (data?.[field] == null || !Number.isFinite(data[field]) || Math.abs(data[field] - amount) > tolerance)) reasons.push(`${field}: source aggregate differs or is unavailable.`)
    }
    const status = reasons.length ? 'Not reconciled' : skipPresent ? 'Current simulation matches source components, divisor, payable days and net; source actual worked days were not compared'
      : explained ? 'Current simulation matches reviewed source/core rounding, attendance and net' : 'Current simulation matches source components, attendance and net'
    return { rowId: source.rowId, employeeId: source.employeeId, status, reasons, lines, netVariance, notes }
  })
}
