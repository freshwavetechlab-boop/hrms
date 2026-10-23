import type { ExcelPayslipBatch, ExcelPayslipEmailUpdate, ExcelPayslipRow } from '../services/excelPayslipService'
import { attendanceLocation } from './excelPayslipAttendance'
import { payslipPhoneValue, type PayslipSheet } from './excelPayslipImport'

const key = (text: string) => text.trim().replace(/[\s_-]+/g, ' ').toLowerCase()
const validEmail = (value: string) => value.length <= 254 && /^[^\s@<>;,]+@[^\s@<>;,]+\.[^\s@<>;,]+$/.test(value)
export const isPayslipEmailList = (sheets: PayslipSheet[]) => sheets.some(sheet => sheet.rows.some(row =>
  ['batch id', 'payslip id', 'email'].every(label => row.cells.some(cell => key(cell?.value || '') === label))))

// Keep the normal report useful, with stable identity columns for an email-only round trip.
export function payslipEmailExport(batch: ExcelPayslipBatch, rows: ExcelPayslipRow[]) {
  const sum = (items: { amount: number }[]) => items.reduce((total, item) => total + item.amount, 0)
  return [['Batch ID', 'Payslip ID', 'Source row', 'Employee code', 'Employee', 'Location', 'Email', 'Phone', 'Salary month', 'Earnings', 'Deductions', 'In Hand Salary'],
    ...rows.map(row => [batch.id, row.id, String(row.sourceRow), row.employeeCode, row.employeeName, attendanceLocation(row), row.email,
      payslipPhoneValue(row), batch.month,
      String(sum(row.earnings)), String(sum(row.deductions)), String(row.netPay)])]
}

export function readPayslipEmailUpdates(sheets: PayslipSheet[], batch: ExcelPayslipBatch) {
  const candidates = sheets.flatMap(sheet => {
    const header = sheet.rows.find(row => row.cells.some(cell => key(cell?.value || '') === 'email'))
    return header ? [{ sheet, header }] : []
  })
  if (candidates.length !== 1) throw new Error('Upload one exported employee list with an Email column.')
  const { sheet, header } = candidates[0]
  const index = (name: string) => {
    const matches = header.cells.flatMap((cell, i) => key(cell?.value || '') === key(name) ? [i] : [])
    if (matches.length > 1) throw new Error(`Duplicate ${name} columns. Keep one column for each field.`)
    return matches[0] ?? -1
  }
  const emailColumn = index('Email'), batchColumn = index('Batch ID'), idColumn = index('Payslip ID'), codeColumn = index('Employee code')
  const employeeColumn = index('Employee'), locationColumn = index('Location'), monthColumn = index('Salary month'), sourceColumn = index('Source row')
  if (idColumn < 0 && codeColumn < 0) throw new Error('Employee code / Payslip ID is missing. Export the employee list again; names alone cannot identify a payslip.')
  if (idColumn >= 0 && batchColumn < 0) throw new Error('Keep Batch ID with Payslip ID. Export the employee list again.')
  const updates: ExcelPayslipEmailUpdate[] = [], seen = new Set<string>()
  let blank = 0, unchanged = 0
  for (const row of sheet.rows.filter(row => row.sourceRow > header.sourceRow && row.cells.some(cell => cell?.value.trim()))) {
    const value = (column: number) => column >= 0 ? row.cells[column]?.value.trim() || '' : ''
    const email = value(emailColumn)
    if (!email) { blank++; continue }
    const fail = (message: string): never => { throw new Error(`Row ${row.sourceRow}: ${message}`) }
    const cell = row.cells[emailColumn]
    if (cell?.error || cell?.calculationError || cell?.missingCachedValue || cell?.formulaText || !validEmail(email)) fail('enter one valid email address as text.')
    if (batchColumn >= 0 && value(batchColumn) !== batch.id || monthColumn >= 0 && value(monthColumn) !== batch.month) fail('this list belongs to another batch or month.')
    let matches = idColumn >= 0 ? batch.rows.filter(r => r.id === value(idColumn)) : batch.rows.filter(r => r.employeeCode === value(codeColumn))
    if (idColumn < 0 && locationColumn >= 0) matches = matches.filter(r => attendanceLocation(r).trim() === value(locationColumn))
    if (matches.length !== 1) fail('employee is unknown or has multiple location slips. Export this batch again with Payslip ID.')
    const employee = matches[0]
    if (seen.has(employee.id)) fail('duplicate payslip row; keep one email per slip.')
    seen.add(employee.id)
    for (const [column, expected, label] of [[codeColumn, employee.employeeCode, 'Employee code'], [employeeColumn, employee.employeeName, 'Employee'],
      [locationColumn, attendanceLocation(employee), 'Location'], [sourceColumn, String(employee.sourceRow), 'Source row']] as const)
      if (column >= 0 && value(column) !== expected.trim()) fail(`${label} does not match the saved payslip. Keep identity columns unchanged.`)
    if (email === employee.email) { unchanged++; continue }
    updates.push({ rowId: employee.id, employeeCode: employee.employeeCode, employeeName: employee.employeeName,
      location: attendanceLocation(employee), previousEmail: employee.email, email })
  }
  if (!updates.length && !unchanged && !blank) throw new Error('The email list has no employee rows.')
  return { updates, blank, unchanged }
}
