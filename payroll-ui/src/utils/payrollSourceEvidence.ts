import type { ExcelPayslipBatch, ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import type { PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { excelColumnLetter, parsePayslipAmount } from './excelPayslipImport'
import { payslipAttendanceBasis } from './excelPayslipAttendance'
export type PayrollSourceEvidence = { key: string; sourceRow: number; cell: string; heading: string; formula: string; cached: string; state: string }
const financial = new Set(['earning', 'deduction', 'employer', 'netPay', 'grossTotal', 'deductionTotal'])
const privateHeading = /aadha?r|aadhar|national|social|\buan\b|\bpan\b|bank|account|ifsc|name|phone|mobile|email|employee|address|birth|\bdob\b/i
export const redactSourceFormula = (value: string) => value.replace(/"(?:[^"]|"")*"/g, '"[text]"').replace(/\b[A-Z]{5}\d{4}[A-Z]\b/gi, '[identifier]').replace(/\b\d{8,}\b/g, '[identifier]').slice(0, 2000)
export function sharedPayrollDivisor(source: ExcelPayslipCalculationSource, attendanceColumn: number) {
  if (attendanceColumn < 0) return null
  const basis = payslipAttendanceBasis({ ...source, monthDaysCell: undefined }, attendanceColumn), match = basis.cell.replace(/\$/g, '').toUpperCase().match(/^([A-Z]{1,3})([1-9]\d*)$/)
  if (!match || Number(match[2]) > source.headerRow) return null
  const index = [...match[1]].reduce((value, letter) => value * 26 + letter.charCodeAt(0) - 64, 0) - 1
  const cell = source.rows.find(row => row.sourceRow === Number(match[2]))?.cells[index]
  const value = cell?.value?.trim() || '', days = Number(value)
  if (!cell || cell.formulaText || cell.error || cell.calculationError || cell.missingCachedValue || !value || !Number.isInteger(days) || days < 1 || days > 31) return null
  return { cell: match[1] + match[2], days, recordedCell: source.monthDaysCell || '' }
}
export function payrollSourceEvidence(batch: ExcelPayslipBatch, source: ExcelPayslipCalculationSource, request: PayrollSetupRequest): PayrollSourceEvidence[] {
  if (request.batchId !== batch.id || !request.rowIds.length) return []
  const indexes = new Set<number>([request.payrollDaysColumnIndex, request.payableDaysColumnIndex, request.presentDaysColumnIndex ?? -1,
    ...request.components.flatMap(c => [c.sourceColumnIndex ?? -1, c.expectedColumnIndex ?? -1]),
    ...source.columns.filter(c => financial.has(request.sourceColumnRoles?.[c.columnIndex] || c.kind)).map(c => c.columnIndex)])
  const columns = source.columns.filter(c => indexes.has(c.columnIndex) && !privateHeading.test(c.sourceHeader + ' ' + c.label))
  const selected = new Set(request.rowIds), rows = new Map(source.rows.map(row => [row.sourceRow, row]))
  const evidence = batch.rows.filter(row => selected.has(row.id)).flatMap(row => columns.map(column => {
    const cell = rows.get(row.sourceRow)?.cells[column.columnIndex], raw = cell?.value?.trim() || '', amount = raw ? parsePayslipAmount(raw) : null
    return { key: `${row.id}:${column.columnIndex}`, sourceRow: row.sourceRow, cell: `${excelColumnLetter(column.columnIndex)}${row.sourceRow}`,
      heading: column.sourceHeader || column.label, formula: cell?.formulaText ? redactSourceFormula(cell.formulaText) : 'No captured formula',
      cached: !/^\d{8,}$/.test(raw.replace(/[,\s-]/g, '')) && amount != null && Number.isFinite(amount) && Math.abs(amount) < 100_000_000 ? String(amount) : raw ? 'Non-numeric/large value withheld' : 'Blank',
      state: !cell ? 'Missing cell' : cell.error || cell.calculationError ? 'Excel error — resolve original source' : cell.missingCachedValue ? 'Formula cache missing' : cell.formulaText ? 'Captured formula + saved cache; not recalculated here' : 'Saved input; no formula' }
  }))
  const shared = sharedPayrollDivisor(source, request.payableDaysColumnIndex)
  if (shared) evidence.unshift({ key: 'shared-divisor:' + shared.cell, sourceRow: Number(shared.cell.match(/\d+$/)![0]), cell: shared.cell, heading: 'Referenced header divisor input', formula: 'No captured formula', cached: String(shared.days), state: 'Numeric source header input; review day basis. Captured divisor binding: ' + (shared.recordedCell || 'not recorded') })
  return [...evidence, ...referencedPayrollInputs(source, evidence)]
}



// Read-only local precedent tracing. This never chooses salary/day semantics,
// evaluates a formula, follows another workbook, or changes a saved source.
function referencedPayrollInputs(source: ExcelPayslipCalculationSource, evidence: PayrollSourceEvidence[]): PayrollSourceEvidence[] {
  const byRow = new Map<number, typeof source.rows>()
  for (const row of source.rows) byRow.set(row.sourceRow, [...(byRow.get(row.sourceRow) || []), row])
  const address = (text: string) => {
    const match = /^\$?([A-Z]{1,3})\$?([1-9]\d{0,6})$/i.exec(text)
    return match ? { row: Number(match[2]), column: [...match[1].toUpperCase()].reduce((n, c) => n * 26 + c.charCodeAt(0) - 64, 0) - 1 } : null
  }
  const known = new Set(evidence.map(row => row.cell)), queued = evidence.map(row => row.cell), seen = new Set<string>(), result: PayrollSourceEvidence[] = []
  const financialHeading = /rate|basic|wage|salary|pay|earning|deduction|allow|bonus|gross|total|\bpf\b|\beps\b|\besi[c]?\b|\bpt\b|day|month|attendance|present|lwp|worked|leave|^da$|^hra$|t\s*&\s*p/i
  while (queued.length && seen.size < 2000 && result.length < 1000) {
    const from = queued.shift()!, position = address(from)
    if (!position || seen.has(from)) continue
    seen.add(from)
    const rows = byRow.get(position.row), formula = rows?.length === 1 ? rows[0].cells[position.column]?.formulaText : ''
    if (!formula || formula.includes('!') || formula.includes('[') || formula.includes(']')) continue // Separate reviewed dependency capture owns cross-sheet/external paths.
    const text = formula.replace(/"(?:[^"]|"")*"/g, '')
    for (const match of text.matchAll(/(?<![A-Z0-9_.])\$?([A-Z]{1,3})\$?([1-9]\d{0,6})(?![A-Z0-9_(])/gi)) {
      const cellAddress = match[1].toUpperCase() + match[2], p = address(cellAddress)!
      if (seen.has(cellAddress)) continue
      const columns = source.columns.filter(column => column.columnIndex === p.column), column = columns.length === 1 ? columns[0] : undefined
      const heading = column ? column.sourceHeader || column.label : ''
      if (privateHeading.test(heading) || /uan|aadh|^pan(?:no|number)?$|employee.?id|serial|^id$/i.test(heading.replace(/[^a-z0-9]/gi, ''))) continue
      const targetRows = byRow.get(p.row), cell = targetRows?.length === 1 ? targetRows[0].cells[p.column] : undefined
      if (known.has(cellAddress)) { queued.push(cellAddress); continue }
      known.add(cellAddress)
      const raw = cell?.value?.trim() || '', amount = raw ? parsePayslipAmount(raw) : null
      const safe = !!column && financialHeading.test(heading) && !/^\d{8,}$/.test(raw.replace(/[,\s-]/g, '')) && amount != null && Number.isFinite(amount) && Math.abs(amount) < 100_000_000
      const invalid = !cell || !!cell.error || !!cell.calculationError || !!cell.missingCachedValue
      result.push({ key: 'reference:' + cellAddress, sourceRow: p.row, cell: cellAddress,
        heading: 'Referenced input · ' + (heading || 'Column meaning unreviewed'),
        formula: cell?.formulaText ? redactSourceFormula(cell.formulaText) : 'No captured formula',
        cached: invalid ? 'Unavailable' : safe ? String(amount) : raw ? 'Value withheld; review input meaning' : 'Blank',
        state: !targetRows?.length ? 'Referenced source row missing; re-capture the exact source. No period default is substituted.'
          : targetRows.length !== 1 ? 'Referenced source row is ambiguous; source review required.'
          : !cell ? 'Referenced source cell missing; source review required.'
          : invalid ? 'Referenced formula cache missing/error; resolve the saved source.'
          : 'Referenced by ' + from + '; salary, divisor and worked/payable meaning are NOT certified. Saved value only; not recalculated.' })
      if (cell && safe && !invalid) queued.push(cellAddress)
      if (result.length >= 1000) break
    }
  }
  if (queued.length) result.push({ key: 'reference-limit', sourceRow: 0, cell: 'Review limit', heading: 'Local reference tracing', formula: '', cached: 'Unavailable', state: 'Reference review limit reached; inspect a smaller source cohort. No completeness or payroll parity is certified.' })
  return result
}
