import type { ExcelPayslipBatch, ExcelPayslipRow } from '../services/excelPayslipService'
import { formatExcelPayslipAmount, isVerifiedPayslipLocationSplit } from './excelPayslipImport'

export type PayslipEmployeeGroupTotals = {
  gross: number | null
  deductions: number | null
  employerContributions: number | null
  netPay: number | null
}

export type PayslipEmployeeGroup = {
  key: string
  clientId: number
  month: string
  employeeCode: string
  employeeName: string
  rows: ExcelPayslipRow[]
  sourceRows: number[]
  sourceUnits: string[]
  locations: string[]
  states: string[]
  hasMultipleParts: boolean
  isReviewedLocationSplit: boolean
  requiresIdentityReview: boolean
  reviewReason: string
  sourceAttendanceTotal: number | null
  hasDatedCoverage: false
  totals: PayslipEmployeeGroupTotals
}

export type PayslipEmployeeGroupResult = {
  groups: PayslipEmployeeGroup[]
  sourceRowCount: number
  /** Unambiguous source code groups, not distinct verified Employee Master records. */
  employeeCount: number
  reviewedLocationSplitCount: number
  /** Source rows whose code/name/split evidence cannot safely establish a display group. */
  unresolvedRowCount: number
}

type SavedBatchScope = Pick<ExcelPayslipBatch, 'id' | 'clientId' | 'month' | 'rows'>
const normalize = (value: string) => value.normalize('NFKC').trim().replace(/\s+/g, ' ').toUpperCase()
const normalizeName = (value: string) => normalize(value).replace(/^(?:(?:SHRI|SMT|MR|MRS|MS|DR)\.?\s+)+/, '')
const labelKey = (value: string) => normalize(value).replace(/[^A-Z0-9]/g, '')
const locationLabels = new Set(['WORKLOCATION', 'WORKLOCATIONNAME', 'LOCATION', 'LOCATIONNAME', 'LOCATIONNAMETEHSILSUBTEHSIL', 'TEHSILSUBTEHSIL'])

function textValues(rows: ExcelPayslipRow[], labels: ReadonlySet<string>) {
  return [...new Set(rows.flatMap(row => row.information.filter(item => labels.has(labelKey(item.label)))
    .map(item => item.value.trim()).filter(Boolean)))]
}

function componentTotal(components: ExcelPayslipRow['earnings']): number | null {
  let total = 0
  for (const component of components) {
    if (typeof component.amount !== 'number' || !Number.isFinite(component.amount)) return null
    total += component.amount
    if (!Number.isFinite(total)) return null
  }
  return total
}

// Reuse the source display's G15/two-decimal rounding on each slip total first.
// Add integer paise, never raw binary tails or recomputed statutory bases.
function sumDisplayed(rows: ExcelPayslipRow[], read: (row: ExcelPayslipRow) => number | null | undefined): number | null {
  let paise = 0
  for (const row of rows) {
    const value = read(row)
    if (typeof value !== 'number' || !Number.isFinite(value)) return null
    const displayed = formatExcelPayslipAmount(value, 2).replace(/,/g, '')
    if (!/^-?\d+\.\d{2}$/.test(displayed)) return null
    const slipPaise = Number(displayed.replace('.', ''))
    if (!Number.isSafeInteger(slipPaise) || !Number.isSafeInteger(paise + slipPaise)) return null
    paise += slipPaise
  }
  return paise / 100
}

function attendanceTotal(rows: ExcelPayslipRow[]): number | null {
  let total = 0
  for (const row of rows) {
    const fields = row.information.filter(item => labelKey(item.label) === 'ATTENDANCE')
    if (!fields.length || fields.some(item => !/^\d+(?:\.\d+)?$/.test(item.value.trim()))) return null
    const values = [...new Set(fields.map(item => Number(item.value.trim())))]
    if (values.length !== 1 || !Number.isFinite(values[0]) || values[0] < 0 || values[0] > 31) return null
    total += values[0]
  }
  // This is only the sum of the explicitly labelled source input. No present,
  // payable, leave entitlement or dated coverage is inferred from this number.
  return Number(total.toPrecision(15))
}

/** Read-only employee/month presentation. Original source rows remain the action/delivery identity. */
export function buildPayslipEmployeeGroups(batch: SavedBatchScope): PayslipEmployeeGroupResult {
  const buckets = new Map<string, { row: ExcelPayslipRow; index: number }[]>()
  batch.rows.forEach((row, index) => {
    const code = normalize(row.employeeCode)
    const key = code ? JSON.stringify([batch.clientId, batch.month, code]) : `missing-code:${index}`
    const bucket = buckets.get(key) || []
    bucket.push({ row, index }); buckets.set(key, bucket)
  })
  const groups: PayslipEmployeeGroup[] = []
  let employeeCount = 0, reviewedLocationSplitCount = 0, unresolvedRowCount = 0
  const scopeValid = batch.clientId > 0 && /^\d{4}-(0[1-9]|1[0-2])$/.test(batch.month)
  const makeGroup = (rows: ExcelPayslipRow[], index: number, reviewedSplit: boolean, sourceIssue = ''): PayslipEmployeeGroup => {
    const masterReview = rows.some(row => row.employment?.identityReviewRequired === true)
    return {
      key: JSON.stringify([batch.clientId, batch.month, batch.id, normalize(rows[0].employeeCode), index]),
      clientId: batch.clientId, month: batch.month, employeeCode: rows[0].employeeCode, employeeName: rows[0].employeeName,
      rows, sourceRows: rows.map(row => row.sourceRow),
      sourceUnits: textValues(rows, new Set(['PAYROLLUNIT'])), locations: textValues(rows, locationLabels),
      states: textValues(rows, new Set(['STATE', 'STATENAME'])),
      hasMultipleParts: rows.length > 1, isReviewedLocationSplit: reviewedSplit,
      requiresIdentityReview: !!sourceIssue || masterReview,
      reviewReason: sourceIssue || (masterReview
        ? reviewedSplit ? 'Source parts match; existing master identity still needs review.' : 'Existing master identity still needs review.'
        : ''),
      sourceAttendanceTotal: attendanceTotal(rows), hasDatedCoverage: false,
      totals: {
        gross: sumDisplayed(rows, row => row.declaredGross ?? componentTotal(row.earnings)),
        deductions: sumDisplayed(rows, row => row.declaredDeductions ?? componentTotal(row.deductions)),
        employerContributions: sumDisplayed(rows, row => componentTotal(row.employerContributions)),
        netPay: sumDisplayed(rows, row => row.netPay),
      },
    }
  }
  for (const bucket of buckets.values()) {
    const rows = bucket.map(item => item.row), names = rows.map(row => normalizeName(row.employeeName))
    let issue = !scopeValid ? 'The saved client/month scope needs review; no source rows were merged.'
      : !normalize(rows[0].employeeCode) || names.some(name => !name) ? 'Employee code or name is missing; no source rows were merged.'
        : new Set(names).size !== 1 ? 'The same employee code has conflicting source names; no source rows were merged.' : ''
    if (!issue && rows.some(row => !row.id.trim() || !Number.isSafeInteger(row.sourceRow) || row.sourceRow < 1))
      issue = 'A source row identifier is missing or invalid; no source rows were merged.'
    const reviewedSplit = !issue && rows.length > 1 && isVerifiedPayslipLocationSplit(rows, batch.month)
    if (!issue && rows.length > 1 && !reviewedSplit) issue = 'Repeated employee code has unverified source parts; no source rows were merged.'
    if (issue) {
      unresolvedRowCount += rows.length
      bucket.forEach(item => groups.push(makeGroup([item.row], item.index, false, issue)))
    } else {
      employeeCount++
      if (reviewedSplit) reviewedLocationSplitCount++
      groups.push(makeGroup(rows, bucket[0].index, reviewedSplit))
    }
  }
  return { groups, sourceRowCount: batch.rows.length, employeeCount, reviewedLocationSplitCount, unresolvedRowCount }
}

export const buildExcelPayslipEmployeeGroups = buildPayslipEmployeeGroups
