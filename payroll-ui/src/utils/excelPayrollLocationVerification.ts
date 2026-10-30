import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupContext, PayrollSetupResult, PayrollSetupVerification } from '../services/excelPayrollSetupService'
import { formatExcelPayslipAmount, isVerifiedPayslipLocationSplit } from './excelPayslipImport'

type AllocationPart = {
  rowId: string; sourceRow: number; partKey: string; assignmentId: string; workLocationId: number; templateId: string
  payrollDays: number; presentDays: number; payableDays: number; rulesHash: string; sourceHash: string
}
type AllocationGroup = { employeeId: number; referenceAssignmentId?: string | null; parts: AllocationPart[] }
type AssignmentReceipt = Pick<AllocationPart, 'rowId' | 'partKey' | 'assignmentId' | 'workLocationId' | 'templateId' | 'rulesHash' | 'sourceHash'> & {
  employeeId: number; payPeriod: string; configurationEffectiveFrom: string
}
export type LocationVerificationResult = PayrollSetupResult & {
  locationAllocations?: { version: number; payrollPeriod: string; configurationEffectiveFrom: string; groups: AllocationGroup[] } | null
  locationAssignmentReceipts?: AssignmentReceipt[]
}
const normalize = (value?: string) => (value || '').normalize('NFKC').trim().toUpperCase()
const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value)
const precise = (value: number) => Number(value.toPrecision(15))
const unique = (values: unknown[]) => new Set(values).size === values.length
const sameIds = (left: string[], right: string[]) => [...left].sort().join('|') === [...right].sort().join('|')
const currency = (value: number) => Number(formatExcelPayslipAmount(value, 2).replace(/,/g, ''))
const sum = (values: number[]) => precise(values.reduce((total, value) => total + value, 0))
// Decimal G15 input, then native engine midpoint-to-even at the currency stage.
// BigInt keeps exact midpoint decisions independent of JavaScript binary tails.
function engineCurrency(value: number) {
  const text = String(precise(Math.abs(value))), [mantissa, exponentText] = text.toLowerCase().split('e')
  const [whole, fraction = ''] = mantissa.split('.'), exponent = Number(exponentText || 0)
  const digits = BigInt(whole + fraction), scale = fraction.length - exponent
  let paise: bigint
  if (scale <= 2) paise = digits * 10n ** BigInt(2 - scale)
  else {
    const divisor = 10n ** BigInt(scale - 2), quotient = digits / divisor, remainder = digits % divisor
    paise = quotient + (remainder * 2n > divisor || remainder * 2n === divisor && quotient % 2n !== 0n ? 1n : 0n)
  }
  return Number(paise) / 100 * Math.sign(value)
}

/** Versioned allocation receipts permit several source parts for ONE verified employee.
 * Legacy single-source-row verification remains unchanged. No names or row order resolve identity. */
export function locationPayrollVerificationTargets(clientId: number, batch: ExcelPayslipBatch, rowIds: string[], context: PayrollSetupContext, result: LocationVerificationResult) {
  const saved = context.lastImport, allocation = result.locationAllocations, common: string[] = []
  if (batch.clientId !== clientId || allocation?.version !== 1 || allocation.payrollPeriod !== batch.month) common.push('Allocation receipt client, version or source payroll period differs.')
  if (!rowIds.length || rowIds.length > 100 || !unique(rowIds)) common.push('Select a complete unique source cohort of 1–100 rows.')
  if (!saved || saved.status !== 'Applied' || saved.importId !== result.importId || saved.result?.importId !== result.importId
    || saved.savedRequest.batchId !== batch.id || !sameIds(saved.savedRequest.rowIds, rowIds)) common.push('Applied receipt does not bind this exact saved batch and cohort.')
  if (context.calculationOnlySimulationSupported !== true) common.push('Calculation-only runtime capability is unavailable.')
  const groups = allocation?.groups || [], allParts = groups.flatMap(group => group.parts)
  if (!sameIds(allParts.map(part => part.rowId), rowIds) || !unique(allParts.map(part => part.rowId)) || !unique(groups.map(group => group.employeeId))) common.push('Allocation group coverage is incomplete or repeated.')
  return groups.map(group => {
    const reasons = [...common], parts = group.parts, sources = parts.map(part => batch.rows.filter(row => row.id === part.rowId))
    const rows = sources.flat(), master = context.employees.filter(employee => employee.id === group.employeeId)
    if (sources.some(matches => matches.length !== 1) || !unique(rows.map(row => row.sourceRow))) reasons.push('A saved original source part is missing or repeated.')
    if (rows.length > 1 && !isVerifiedPayslipLocationSplit(rows, batch.month)) reasons.push('Source parts do not have verified shared identity and location/unit evidence.')
    if (!group.employeeId || master.length !== 1 || result.employeeIds.filter(id => id === group.employeeId).length !== 1) reasons.push('The unique existing Master employee is missing or repeated in this receipt.')
    for (const row of rows) {
      const employment = row.employment, matches = context.roster?.rows.filter(match => match.rowId === row.id) || []
      if (!employment || employment.employeeId !== group.employeeId || employment.identityReviewRequired || employment.blockedForMonth
        || !normalize(employment.employeeCode) || normalize(employment.employeeCode) !== normalize(master[0]?.employeeCode)
        || matches.length !== 1 || matches[0].employeeId !== group.employeeId || !['Existing', 'Applied'].includes(matches[0].status)) reasons.push(`Source row ${row.sourceRow}: current saved employment and Master identities require review.`)
      const otherParts = batch.rows.filter(other => normalize(other.employeeCode) === normalize(row.employeeCode))
      if (!normalize(row.employeeCode) || !sameIds(otherParts.map(other => other.id), rows.map(source => source.id))) reasons.push('Select every source part for this employee; partial-group verification is unavailable.')
    }
    if (!parts.length || !unique(parts.map(part => part.partKey)) || parts.some(part => !part.partKey || !part.assignmentId || !part.rulesHash || !part.sourceHash || part.workLocationId <= 0 || !part.templateId)) reasons.push('Location assignment, rules or immutable source provenance is incomplete.')
    if (group.referenceAssignmentId && !parts.some(part => part.assignmentId === group.referenceAssignmentId)) reasons.push('Reference assignment is outside this employee allocation.')
    for (const part of parts) {
      if (batch.rows.filter(row => row.id === part.rowId && row.sourceRow === part.sourceRow).length !== 1) reasons.push('The original source-row number changed after allocation review.')
      const receipts = result.locationAssignmentReceipts?.filter(receipt => receipt.rowId === part.rowId) || []
      const receipt = receipts[0]
      if (receipts.length !== 1 || receipt.employeeId !== group.employeeId || receipt.payPeriod !== batch.month
        || receipt.configurationEffectiveFrom !== allocation?.configurationEffectiveFrom
        || (['partKey', 'assignmentId', 'workLocationId', 'templateId', 'rulesHash', 'sourceHash'] as const).some(key => receipt[key] !== part[key])) reasons.push(`Source row ${part.sourceRow}: assignment receipt no longer matches reviewed allocation.`)
      const transfer = result.transferRows?.filter(line => line.rowId === part.rowId) || []
      if (transfer.length !== 1 || transfer[0].employeeId !== group.employeeId) reasons.push(`Source row ${part.sourceRow}: component transfer proof is missing or ambiguous.`)
    }
    const payrollDays = parts[0]?.payrollDays, presentDays = sum(parts.map(part => part.presentDays)), payableDays = sum(parts.map(part => part.payableDays))
    if (!finite(payrollDays) || payrollDays < 1 || payrollDays > 31 || parts.some(part => part.payrollDays !== payrollDays || !finite(part.presentDays) || !finite(part.payableDays) || part.presentDays < 0 || part.payableDays < 0)
      || presentDays > payrollDays || payableDays > payrollDays) reasons.push('Location attendance allocations do not form valid employee-month totals.')
    return { employeeId: group.employeeId, rowIds: parts.map(part => part.rowId), sourceRows: parts.map(part => part.sourceRow), payrollDays, presentDays, payableDays, eligible: reasons.length === 0, reasons }
  })
}

type ComparisonStatus = 'EXACT' | 'ROUNDING_ONLY' | 'REAL_MISMATCH' | 'BLOCKED'
/** Compare aggregate component amounts; a penny is never an automatic tolerance.
 * Currency equality is explained only by the source's actual shared G15/2dp formatter. */
export function compareLocationTransferPayroll(result: LocationVerificationResult, observations: PayrollSetupVerification[], clientId: number) {
  return (result.locationAllocations?.groups || []).map(group => {
    const reasons: string[] = [], matches = observations.filter(observation => observation.employeeId === group.employeeId)
    const observed = matches.length === 1 ? matches[0] : undefined, data = observed?.data
    if (result.locationAllocations?.version !== 1 || !result.transfer || result.transfer.version !== 1) reasons.push('Versioned allocation and component provenance are required.')
    if (!observed?.ok || !data?.complete || data.mode !== 'Employee' || data.issues.some(issue => issue.severity === 'Error')) reasons.push(observed?.error || 'A complete actual Employee calculation is required.')
    const actualScope = data?.locationAllocations
    if (!actualScope || actualScope.version !== 1 || actualScope.clientId !== clientId || actualScope.clientId !== data?.clientId || actualScope.employeeId !== group.employeeId
      || data?.employeeId !== group.employeeId || actualScope.payPeriod !== result.locationAllocations?.payrollPeriod || data?.payPeriod !== actualScope.payPeriod
      || (actualScope.referenceAssignmentId || '') !== (group.referenceAssignmentId || '') || actualScope.parts.length !== group.parts.length
      || !unique(actualScope.parts.map(part => part.partKey)) || group.parts.some(part => {
        const bound = actualScope.parts.filter(actual => actual.partKey === part.partKey)
        return bound.length !== 1 || bound[0].assignmentId !== part.assignmentId || bound[0].workLocationId !== part.workLocationId || bound[0].salaryStructureId !== part.templateId
          || bound[0].payrollDays !== part.payrollDays || bound[0].presentDays !== part.presentDays || bound[0].payableDays !== part.payableDays
      })) reasons.push('Actual calculation scope or location assignments differ from the saved employee/month receipt.')
    const parts = group.parts.map(part => result.transferRows?.filter(row => row.rowId === part.rowId && row.employeeId === group.employeeId) || [])
    if (parts.some(rows => rows.length !== 1)) reasons.push('Every original part requires an unambiguous component receipt.')
    const sourceRows = parts.flat(), sourceLines = sourceRows.flatMap(row => row.lines), codes = [...new Set(sourceLines.map(line => normalize(line.code)))]
    if (!codes.length || sourceRows.some(row => !row.lines.length || !unique(row.lines.map(line => normalize(line.code))) || row.lines.some(line => !finite(line.sourceAmount)))) reasons.push('Source component proof is incomplete or ambiguous.')
    const lines = codes.map(code => {
      const sources = sourceLines.filter(line => normalize(line.code) === code), actual = data?.lines.filter(line => normalize(line.code) === code) || []
      const classification = result.transfer?.components.filter(component => normalize(component.code) === code) || []
      const sourceRaw = sources.every(line => finite(line.sourceAmount)) ? sum(sources.map(line => line.sourceAmount!)) : null
      const sourceDisplay = sourceRaw === null ? null : sum(sources.map(line => currency(line.sourceAmount!)))
      const amount = actual.length === 1 && finite(actual[0].earned) ? actual[0].earned : null
      let status: ComparisonStatus = 'BLOCKED'
      const coreUnrounded = actual.length === 1 ? actual[0].unroundedEarned : null
      const explainedAggregateStage = sourceRaw !== null && finite(coreUnrounded) && precise(coreUnrounded) === sourceRaw && amount === engineCurrency(sourceRaw)
      if (sourceRaw !== null && amount !== null && precise(amount) === currency(amount) && classification.length === 1 && classification[0].category === actual[0].category) {
        status = precise(amount) === sourceRaw ? 'EXACT' : currency(amount) === sourceDisplay || explainedAggregateStage ? 'ROUNDING_ONLY' : 'REAL_MISMATCH'
      }
      if (status === 'BLOCKED') reasons.push(`${code}: missing amount or component classification proof.`)
      else if (status === 'REAL_MISMATCH') reasons.push(`${code}: actual currency amount differs from retained source parts.`)
      return { code, sourceRaw, sourceDisplay, actual: amount, rawDelta: amount !== null && sourceRaw !== null ? precise(amount - sourceRaw) : null, displayDelta: amount !== null && sourceDisplay !== null ? precise(currency(amount) - sourceDisplay) : null, status,
        roundingBasis: status !== 'ROUNDING_ONLY' ? '' : explainedAggregateStage ? 'Same G15 raw component basis; source part displays versus native aggregate midpoint-to-even.' : 'Same currency value under the retained source G15/two-decimal formatter.' }
    })
    for (const actual of data?.lines || []) if (!codes.includes(normalize(actual.code))) reasons.push(`${actual.code}: actual monthly component has no source comparison.`)
    for (const [name, expected] of [['PAYROLL_DAYS', group.parts[0]?.payrollDays], ['PRESENT_DAYS', sum(group.parts.map(part => part.presentDays))], ['PAYABLE_DAYS', sum(group.parts.map(part => part.payableDays))]] as const) {
      const actual = data?.inputs.filter(input => input.name === name) || []
      if (!finite(expected) || actual.length !== 1 || !finite(actual[0].value) || precise(actual[0].value) !== precise(expected)) reasons.push(`${name}: stored employee-month attendance differs or is unavailable.`)
    }
    const sourceNetRaw = sourceRows.length && sourceRows.every(row => finite(row.sourceNet)) ? sum(sourceRows.map(row => row.sourceNet)) : null
    const sourceNetDisplay = sourceNetRaw === null ? null : sum(sourceRows.map(row => currency(row.sourceNet)))
    // Reconcile actual summary fields against actual classified component lines.
    // Neither a matching net nor a matching source total can conceal employer/category differences.
    const classified = (category: string) => lines.filter(line => result.transfer?.components.find(component => normalize(component.code) === line.code)?.category === category)
    const actualGross = sum([...classified('Earning'), ...classified('Reimbursement')].map(line => line.actual ?? 0))
    const actualDeductions = sum(classified('Deduction').map(line => line.actual ?? 0))
    const actualBenefits = sum(classified('Benefit').map(line => line.actual ?? 0))
    for (const [name, expected] of [['gross', actualGross], ['deductions', actualDeductions], ['benefits', actualBenefits]] as const) {
      if (!finite(data?.[name]) || precise(data[name]) !== expected) reasons.push(`${name}: actual summary does not reconcile to classified component lines.`)
    }
    const netDelta = sourceNetDisplay !== null && finite(data?.net) ? precise(currency(data.net) - sourceNetDisplay) : null
    const sourceNetFromComponents = sum([...classified('Earning'), ...classified('Reimbursement')].map(line => line.sourceRaw ?? 0)) - sum(classified('Deduction').map(line => line.sourceRaw ?? 0))
    const explainedNetStage = sourceNetRaw !== null && precise(sourceNetRaw) === precise(Math.max(0, sourceNetFromComponents))
      && finite(data?.net) && precise(data.net) === precise(Math.max(0, actualGross - actualDeductions))
      && lines.every(line => line.status === 'EXACT' || line.status === 'ROUNDING_ONLY')
    if (netDelta === null || netDelta !== 0 && !explainedNetStage) reasons.push('Actual monthly net differs from source; the component-versus-aggregate rounding stage does not explain it.')
    const status: ComparisonStatus = reasons.length ? lines.some(line => line.status === 'REAL_MISMATCH') || netDelta !== null && netDelta !== 0 ? 'REAL_MISMATCH' : 'BLOCKED'
      : lines.some(line => line.status === 'ROUNDING_ONLY') || precise(data!.net!) !== sourceNetRaw || netDelta !== 0 ? 'ROUNDING_ONLY' : 'EXACT'
    return { employeeId: group.employeeId, rowIds: group.parts.map(part => part.rowId), status, reasons, lines, sourceNetRaw, sourceNetDisplay, actualNet: data?.net ?? null, rawNetDelta: sourceNetRaw !== null && finite(data?.net) ? precise(data.net - sourceNetRaw) : null, displayNetDelta: netDelta }
  })
}
