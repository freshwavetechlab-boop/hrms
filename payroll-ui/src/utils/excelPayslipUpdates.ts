import type { PayslipCell, PayslipSheet } from './excelPayslipImport'
import { formatPayslipSourceCell } from './excelPayslipImport'
import type { PayslipUpdateCatalog, PayslipUpdateField, PayslipUpdatePatch, PayslipUpdatePreview } from '../services/excelPayslipUpdateService'
import type { XlsxCell, XlsxSheet } from './xlsx'

const key = (value: string) => value.normalize('NFKC').toLowerCase().replace(/[^a-z0-9]/g, '')
const codeKey = (value: string) => value.trim().toUpperCase()
const aliases: Record<string, string> = { employeeno: 'employeecode', employeenumber: 'employeecode', empcode: 'employeecode', employeename: 'name', fullname: 'name', mobilenumber: 'phone', mobileno: 'phone', mobile: 'phone', contactnumber: 'phone', contactno: 'phone', phonenumber: 'phone', phoneno: 'phone', uannumber: 'uan', uanno: 'uan', universalaccountnumber: 'uan', aadhar: 'aadhaar', adhaar: 'aadhaar', aadhaarnumber: 'aadhaar', aadhaarno: 'aadhaar', emailid: 'email', emailaddress: 'email', workemail: 'email', panno: 'pan', pannumber: 'pan', esicnumber: 'esic', esicno: 'esic', esinumber: 'esic', bankaccountnumber: 'accountno', bankaccountno: 'accountno', accountnumber: 'accountno', acno: 'accountno', ifsccode: 'ifsc', location: 'worklocation', designationmanpowercategory: 'designation', annualctc: 'annualctc' }
const headerKey = (value: string) => aliases[key(value)] || key(value)
export const isPayslipUpdateCodeHeader = (value: string) => headerKey(value) === 'employeecode'
export const updateSelectionKey = (ids: string[]) => JSON.stringify([...ids].sort())
export type PayslipUpdateMapping = Record<number, string>
export type PayslipUpdateInputIssue = { id: string; sourceRow: number; column: string; message: string }

export function unmappedPayslipUpdateColumns(sheet: PayslipSheet, headerRow: number, codeColumn: number | undefined, mapping: PayslipUpdateMapping) {
  const header = sheet.rows.find(row => row.sourceRow === headerRow)
  return Array.from({ length: sheet.columnCount }, (_, index) => ({ index, label: header?.cells[index]?.value.trim() || `Column ${index + 1}` }))
    .filter(column => column.index !== codeColumn && !mapping[column.index] && !['clientid', 'batchid', 'salarymonth', 'employeenamereference'].includes(key(column.label)) && sheet.rows.some(row => row.sourceRow > headerRow && row.cells[column.index]?.value.trim()))
    .map(column => column.label)
}

export function suggestPayslipUpdateMapping(headers: string[], fields: PayslipUpdateField[]) {
  const mapping: PayslipUpdateMapping = {}
  headers.forEach((header, index) => {
    const stable = /^\[([^\]]+)\]/.exec(header.trim())?.[1]
    const exact = stable ? fields.filter(field => field.id === stable) : fields.filter(field => headerKey(field.label) === headerKey(header))
    if (exact.length === 1) mapping[index] = exact[0].id
  })
  return mapping
}

export function payslipUpdateTemplate(catalog: PayslipUpdateCatalog): XlsxSheet[] {
  const text = (value: string): XlsxCell => ({ value, type: 'text', style: 'text' })
  const headers = ['Batch ID', 'Salary month', 'Client ID', 'Employee code', 'Employee name (reference)', ...catalog.fields.map(field => `[${field.id}] ${field.label}`)]
  return [{ name: 'Update data', freezeRows: 1, freezeColumns: 4, columnWidths: [34, 16, 16, 20, 32, ...catalog.fields.map(() => 24)], rows: [headers.map(value => ({ value, style: 'header' })), ...catalog.rows.map(row => [text(catalog.batchId), text(catalog.month), text(String(catalog.clientId)), text(row.employeeCode), text(row.employeeName), ...catalog.fields.map(field => text(row.values[field.id] || ''))])] },
    { name: 'Instructions', columnWidths: [28, 105], rows: [['Update existing payslips', 'Keep Employee code, Client ID, Batch ID and Salary month unchanged. This file never creates employees or another batch.'], ['Blank cells', 'Keep current values. Enter 0 only when an actual numeric value should become zero.'], ['Destinations', 'Profile/contact/bank fields marked Batch + Master update the same matched Employee Master. Monthly amounts and source attendance inputs are Batch only.'], ['Formula outputs', 'Read-only. Change supported input cells and review server-calculated dependant totals before saving.'], ...catalog.fields.map(field => [`[${field.id}] ${field.label}`, `${field.destination}. ${field.editable ? 'Editable.' : 'Read-only.'} ${field.reason || ''}`])] }]
}

const sameValue = (field: PayslipUpdateField, left: string, right: string) => {
  if (field.type !== 'number') return left.trim() === right.trim()
  const normalize = (value: string) => {
    const compact = value.trim().replace(/,/g, '')
    if (!/^[+-]?\d+(?:\.\d*)?$/.test(compact)) return compact
    let [whole, decimal = ''] = compact.replace(/^\+/, '').split('.')
    const negative = whole.startsWith('-'); whole = whole.replace(/^-/, '').replace(/^0+(?=\d)/, '')
    decimal = decimal.replace(/0+$/, '')
    return `${negative && (whole !== '0' || decimal) ? '-' : ''}${whole}${decimal ? '.' + decimal : ''}`
  }
  return normalize(left) === normalize(right)
}

export function parsePayslipUpdateInput(sheet: PayslipSheet, headerRow: number, codeColumn: number | undefined, mapping: PayslipUpdateMapping, catalog: PayslipUpdateCatalog, excludedRows: number[] = []) {
  const header = sheet.rows.find(row => row.sourceRow === headerRow)
  const headers = Array.from({ length: sheet.columnCount }, (_, index) => header?.cells[index]?.value.trim() || '')
  const issues: PayslipUpdateInputIssue[] = [], rows: PayslipUpdatePatch[] = []
  const issue = (sourceRow: number, column: string, message: string) => issues.push({ id: `${sourceRow}:${column}:${issues.length}`, sourceRow, column, message })
  if (codeColumn === undefined || codeColumn < 0 || codeColumn >= sheet.columnCount) return { rows, issues: [{ id: 'code', sourceRow: headerRow, column: 'Employee code', message: 'Choose the employee code column. Names are not used to identify employees.' }], blankCells: 0, unchangedCells: 0, excludedRows: 0 }
  const fields = new Map(catalog.fields.map(field => [field.id, field]))
  const used = Object.entries(mapping).filter(([column, field]) => Number(column) !== codeColumn && field)
  for (const id of new Set(used.map(([, field]) => field))) if (used.filter(([, field]) => field === id).length > 1) issue(headerRow, fields.get(id)?.label || id, 'Map only one uploaded column to this field.')
  if (!used.length) issue(headerRow, 'Columns', 'Map at least one update field.')
  const saved = new Map<string, typeof catalog.rows>()
  for (const row of catalog.rows) saved.set(codeKey(row.employeeCode), [...saved.get(codeKey(row.employeeCode)) || [], row])
  const excluded = new Set(excludedRows), seen = new Set<string>()
  const batchColumn = headers.findIndex(value => key(value) === 'batchid')
  const clientColumn = headers.findIndex(value => key(value) === 'clientid')
  const monthColumn = headers.findIndex(value => key(value) === 'salarymonth')
  let blankCells = 0, unchangedCells = 0, excludedCount = 0
  for (const row of sheet.rows.filter(row => row.sourceRow > headerRow && row.cells.some(cell => cell?.value.trim()))) {
    if (excluded.has(row.sourceRow)) { excludedCount++; continue }
    const before = issues.length
    const code = formatPayslipSourceCell(row.cells[codeColumn], 'Employee code').trim()
    if (!code) { issue(row.sourceRow, 'Employee code', 'Employee code is missing. Exclude a total/footer row or supply the existing code.'); continue }
    if (seen.has(codeKey(code))) { issue(row.sourceRow, 'Employee code', 'This employee code appears more than once in the upload. Keep one row per employee.'); continue }
    seen.add(codeKey(code))
    const matches = saved.get(codeKey(code)) || []
    if (matches.length !== 1) { issue(row.sourceRow, 'Employee code', matches.length ? 'This code matches multiple saved payslips. Resolve the source identity before updating.' : 'This employee code is not in the selected saved batch. New employees are not created here.'); continue }
    if (batchColumn >= 0 && row.cells[batchColumn]?.value.trim() !== catalog.batchId) issue(row.sourceRow, 'Batch ID', 'This row belongs to another batch. Keep the exported Batch ID unchanged.')
    if (clientColumn >= 0 && row.cells[clientColumn]?.value.trim() !== String(catalog.clientId)) issue(row.sourceRow, 'Client ID', 'This row belongs to another client. Keep the exported Client ID unchanged.')
    if (monthColumn >= 0 && row.cells[monthColumn]?.value.trim() !== catalog.month) issue(row.sourceRow, 'Salary month', 'This row belongs to another month. Keep the exported Salary month unchanged.')
    const values: Record<string, string> = {}
    for (const [column, id] of used) {
      const field = fields.get(id)
      if (!field) { issue(row.sourceRow, headers[Number(column)] || column, 'The mapped field is unavailable in this batch.'); continue }
      const cell: PayslipCell | undefined = row.cells[Number(column)]
      if (cell?.error || cell?.calculationError || cell?.missingCachedValue) { issue(row.sourceRow, field.label, 'Excel contains an error or a missing formula result. Recalculate and save the workbook before uploading.'); continue }
      const value = formatPayslipSourceCell(cell, field.label).trim()
      if (!value) { blankCells++; continue }
      if (sameValue(field, value, matches[0].values[id] || '')) { unchangedCells++; continue }
      if (!field.editable) { issue(row.sourceRow, field.label, field.reason || 'This field is read-only. Change supported input cells instead.'); continue }
      if (field.type === 'number' && (cell?.formula || cell?.formulaText)) { issue(row.sourceRow, field.label, 'A changed numeric input contains an Excel formula. Enter the intended input value directly; existing saved formulas are retained and their outputs are recalculated by the server.'); continue }
      values[id] = value
    }
    if (issues.length === before && Object.keys(values).length) rows.push({ employeeCode: matches[0].employeeCode, values })
  }
  return { rows, issues, blankCells, unchangedCells, excludedRows: excludedCount }
}

export function selectedPayslipUpdateRows(preview: PayslipUpdatePreview, selectedIds: string[]): PayslipUpdatePatch[] {
  const selected = new Set(selectedIds), rows = new Map<string, PayslipUpdatePatch>()
  for (const change of preview.changes.filter(change => selected.has(change.id) && change.canApply && !change.computed)) {
    const id = codeKey(change.employeeCode)
    const row = rows.get(id) || { employeeCode: change.employeeCode, values: {} }
    row.values[change.fieldId] = change.newValue; rows.set(id, row)
  }
  return [...rows.values()]
}
