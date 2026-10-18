import type { ExcelPayslipBatch, ExcelPayslipCalculationSource, ExcelPayslipRow } from '../services/excelPayslipService'
import { excelColumnLetter, parsePayslipAmount, type PayslipSheet } from './excelPayslipImport'

export const attendanceLocation = (row: ExcelPayslipRow) => row.information.find(item => /work\s*location|location\s*name|^location$|tehsil/i.test(item.label))?.value || ''
const headerKey = (value: string) => value.trim().replace(/\s+/g, ' ').toLowerCase()

// Blank attendance is incomplete, not zero. Validate identities and nonblank values
// before returning anything so a corrupt upload cannot partially replace inputs.
export function readPayslipAttendance(sheets: PayslipSheet[], batch: ExcelPayslipBatch, month: string) {
  const candidates = sheets.flatMap(sheet => {
    const header = sheet.rows.find(row => ['batch id', 'source row', 'days'].every(label => row.cells.some(cell => headerKey(cell?.value || '') === label)))
    return header ? [{ sheet, header }] : []
  })
  if (candidates.length !== 1) throw new Error('Use one attendance sheet exported here, with Batch ID, Source row and Days columns.')
  const { sheet, header } = candidates[0]
  const index = (label: string) => {
    const matches = header.cells.flatMap((cell, i) => headerKey(cell?.value || '') === headerKey(label) ? [i] : [])
    if (matches.length > 1) throw new Error(`The attendance sheet has duplicate ${label} columns.`)
    return matches[0] ?? -1
  }
  const batchColumn = index('Batch ID'), rowColumn = index('Source row'), dayColumn = index('Days'), monthColumn = index('Salary month')
  const identities = [['Employee', 'employeeName'], ['Employee code', 'employeeCode'], ['Location', 'location']] as const
  const identityColumns = identities.map(([label, key]) => ({ label, key, column: index(label) }))
  const employees = new Map(batch.rows.map(row => [row.sourceRow, row]))
  const values: Record<string, number | null> = {}, blankRows: number[] = []
  for (const row of sheet.rows.filter(row => row.sourceRow > header.sourceRow && row.cells.some(cell => cell?.value.trim()))) {
    const employee = employees.get(Number(row.cells[rowColumn]?.value))
    if (row.cells[batchColumn]?.value.trim() !== batch.id || !employee || employee.id in values)
      throw new Error(`Row ${row.sourceRow}: wrong batch, unknown employee row or duplicate attendance.`)
    if (monthColumn >= 0 && row.cells[monthColumn]?.value.trim() !== month)
      throw new Error(`Row ${row.sourceRow}: this attendance file is not for ${month}. Download the template for the selected salary month.`)
    for (const { label, key, column } of identityColumns) {
      const expected = key === 'location' ? attendanceLocation(employee) : employee[key]
      if (column >= 0 && (row.cells[column]?.value || '').trim() !== expected.trim())
        throw new Error(`Row ${row.sourceRow}: ${label} does not match this employee's source row. Keep identity columns unchanged.`)
    }
    const cell = row.cells[dayColumn], text = cell?.value.trim() || ''
    if (cell?.error || cell?.calculationError || cell?.missingCachedValue)
      throw new Error(`Row ${row.sourceRow}: the Days cell has an Excel error. Enter a number and save the workbook again.`)
    if (!text) { values[employee.id] = null; blankRows.push(row.sourceRow); continue }
    const value = parsePayslipAmount(text)
    if (value == null || value < 0 || value > 31)
      throw new Error(`Row ${row.sourceRow}: enter days between 0 and 31; half days are allowed.`)
    values[employee.id] = value
  }
  if (!Object.keys(values).length) throw new Error('The attendance file has no employee rows.')
  return { values, blankRows, entered: Object.values(values).filter(value => value != null).length }
}

export function payslipAttendanceTemplate(batch: ExcelPayslipBatch, month: string, days: Record<string, number | null>) {
  return [['Batch ID', 'Source row', 'Employee code', 'Employee', 'Location', 'Days', 'Salary month'],
    ...batch.rows.map(row => [batch.id, String(row.sourceRow), row.employeeCode, row.employeeName, attendanceLocation(row), days[row.id] == null ? '' : String(days[row.id]), month])]
}

// Only infer a divisor directly prorating by this row's attendance input. An
// unrelated absolute reference (an ID, rate or PF cap) is not a month-days cell.
export function payslipAttendanceBasis(source: ExcelPayslipCalculationSource, attendanceColumn: number | undefined) {
  const candidates = new Set<string>(), fixed = new Set<number>()
  if (attendanceColumn != null) {
    const column = excelColumnLetter(attendanceColumn)
    for (const row of source.rows.filter(row => row.sourceRow > source.headerRow)) {
      const pattern = new RegExp(`/\\s*(\\$?[A-Z]+\\$?[1-9]\\d*|\\d+(?:\\.\\d+)?)\\s*\\*\\s*\\$?${column}\\$?${row.sourceRow}(?![\\dA-Z_])`, 'gi')
      for (const cell of row.cells) {
        for (const match of (cell?.formulaText || '').matchAll(pattern)) {
          const divisor = match[1].toUpperCase().replace(/\$/g, '')
          if (/^\d/.test(divisor)) { const value = Number(divisor); if (value >= 1 && value <= 31) fixed.add(value); continue }
          const letters = divisor.match(/^[A-Z]+/)![0], sourceRow = Number(divisor.slice(letters.length))
          const index = [...letters].reduce((value, letter) => value * 26 + letter.charCodeAt(0) - 64, 0) - 1
          const value = Number(source.rows.find(row => row.sourceRow === sourceRow)?.cells[index]?.value)
          if (sourceRow <= source.headerRow && value >= 1 && value <= 31) candidates.add(divisor)
        }
      }
    }
  }
  return { cell: source.monthDaysCell || (candidates.size === 1 && fixed.size === 0 ? [...candidates][0] : ''), fixedDays: fixed.size === 1 && candidates.size === 0 ? [...fixed][0] : undefined }
}
