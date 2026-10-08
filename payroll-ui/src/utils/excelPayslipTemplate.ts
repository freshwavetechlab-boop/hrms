import type { ExcelPayslipBatch, ExcelPayslipCalculationSource } from '../services/excelPayslipService'
import { excelColumnLetter, isPayslipPhoneLabel, parsePayslipAmount, payslipTemplateMarker, type PayslipCell, type PayslipColumn, type PayslipSheet, type PayslipTemplateMetadata } from './excelPayslipImport'
import { buildXlsxBlob, type XlsxCell, type XlsxSheet } from './xlsx'

const identifier = /(?:\b(?:uan|pan|aadhaar|aadhar|adhaar|ifsc|phone|mobile|passport)\b|account|\b(?:code|identifier|identity|id)\b|\b(?:pf|esi|esic)\s*(?:no\b|number\b))/i

// The saved source contains one worksheet, not workbook-level tables, defined names
// or other sheets. Keep their cached values explicitly instead of exporting broken links.
function unavailableFormulaDependency(formula: string, sheetName: string): string | null {
  let expression = formula.replace(/"(?:[^"]|"")*"/g, '""')
  if (/[\[\]]/.test(expression)) return 'external workbook or table reference'
  let missingSheet = false
  expression = expression.replace(/(?:'((?:[^']|'')+)'|([A-Za-z_\\][\w.\\]*))!/g, (_, quoted: string | undefined, bare: string | undefined) => {
    const name = quoted?.replace(/''/g, "'") ?? bare ?? ''
    if (name.toLocaleLowerCase() !== sheetName.toLocaleLowerCase()) missingSheet = true
    return ''
  })
  if (missingSheet) return 'another worksheet'
  expression = expression.replace(/#(?:REF!|VALUE!|DIV\/0!|N\/A|NAME\?|NUM!|NULL!)/g, '').replace(/\$?[A-Z]{1,3}\s*:\s*\$?[A-Z]{1,3}/gi, '').replace(/(?<![\w.\\])\$?[A-Z]{1,3}\$?\d+(?![\w.\\])/gi, '0').replace(/\b\d+(?:\.\d*)?(?:E[+-]?\d+)?\b/gi, '0')
  for (const match of expression.matchAll(/[A-Za-z_\\][\w.\\]*/g)) {
    const name = match[0], after = expression.slice(match.index! + name.length)
    if (/^\s*\(/.test(after) || /^(TRUE|FALSE)$/i.test(name)) continue
    return 'a workbook-defined name'
  }
  return null
}

function sourceCell(cell: PayslipCell | undefined, column: PayslipColumn | undefined, reference: string, sheetName: string, warnings: string[]): XlsxCell {
  if (!cell) return ''
  const value = cell.value ?? '', numeric = parsePayslipAmount(value)
  const text = ['employeeCode', 'employeeName', 'email'].includes(column?.kind || '') || (column?.kind === 'info' && (identifier.test(`${column.label} ${column.sourceHeader}`) || isPayslipPhoneLabel(column.label) || isPayslipPhoneLabel(column.sourceHeader))) || /^0\d+$/.test(value) || /^\d{16,}$/.test(value)
  const type = cell.error ? 'error' : numeric !== null && !text ? 'number' : 'text'
  const data: Exclude<XlsxCell, string | number> = { value: type === 'number' ? String(numeric) : value, type }
  if (cell.formulaText || cell.formula || cell.calculationError) {
    const unavailable = cell.calculationError || (cell.formulaText ? unavailableFormulaDependency(cell.formulaText, sheetName) : 'missing formula expression')
    if (unavailable) {
      warnings.push(`${reference}: formula exported as its saved value (${unavailable}). Review this value when changing inputs.`)
      // An unresolved blank cache must stay visibly invalid rather than becoming
      // an empty input that Excel could silently treat as zero on recalculation.
      if (cell.missingCachedValue) { data.type = 'error'; data.value = '#N/A' }
    }
    else { data.formula = cell.formulaText; data.missingCachedValue = cell.missingCachedValue }
  }
  return data
}

export function buildEditablePayslipSheet(batch: ExcelPayslipBatch, source: ExcelPayslipCalculationSource): PayslipSheet & { template: PayslipTemplateMetadata } {
  if (!source?.rows.length || !source.columns.length) throw new Error('This batch has no saved worksheet. Import the original salary workbook once before editing its mapping or exporting a template.')
  if (!source.rows.some(row => row.sourceRow === source.headerRow)) throw new Error('The saved worksheet header is missing. Import the original workbook again.')
  const columns = source.columns.map(column => ({ ...column })), codeColumns = columns.filter(column => column.kind === 'employeeCode')
  if (codeColumns.length > 1) throw new Error('Employee code mapping is ambiguous. Correct the original import before exporting a template.')
  const savedWidth = source.rows.reduce((width, row) => Math.max(width, row.cells.length), source.columnCount), codeIndex = codeColumns[0]?.columnIndex ?? savedWidth
  const columnCount = Math.max(savedWidth, codeIndex + 1)
  if (columnCount > 512) throw new Error('The worksheet has no room for an employee code column within the supported 512-column limit.')
  if (!codeColumns.length) columns.push({ columnIndex: codeIndex, sourceHeader: 'Employee Code', label: 'Employee code', kind: 'employeeCode' })
  const employeeRows = new Map(batch.rows.map(row => [row.sourceRow, row]))
  const rows = [...source.rows].sort((left, right) => left.sourceRow - right.sourceRow).map(row => {
    const cells = Array.from({ length: columnCount }, (_, index) => {
      const cell = row.cells[index]
      return cell ? { ...cell, value: cell.value ?? '', ...(cell.formulaText || cell.calculationError ? { formula: true } : {}) } : { value: '' }
    })
    if (row.sourceRow === source.headerRow && !codeColumns.length) cells[codeIndex] = { value: 'Employee Code' }
    const employee = employeeRows.get(row.sourceRow)
    if (employee) cells[codeIndex] = { value: employee.employeeCode || cells[codeIndex].value || '' }
    return { sourceRow: row.sourceRow, cells }
  })
  return {
    name: source.sheetName || batch.sheetName || 'Salary', rows, columnCount, dateSystem: source.dateSystem === '1904' ? '1904' : '1900',
    template: { version: 1, clientId: batch.clientId, sourceBatchId: batch.id, month: batch.month, headerRow: source.headerRow, columns, excludedSourceRows: [...source.excludedSourceRows], salaryTemplateId: batch.salaryTemplateId, ...(batch.simpleLayout === true ? { simpleLayout: true } : {}), warnings: [] },
  }
}

export function buildExcelPayslipTemplate(batch: ExcelPayslipBatch, source: ExcelPayslipCalculationSource) {
  const sheet = buildEditablePayslipSheet(batch, source), { rows, name } = sheet, { columns, headerRow, excludedSourceRows } = sheet.template, warnings: string[] = []
  const columnByIndex = new Map(columns.map(column => [column.columnIndex, column]))
  const exportedRows = rows.map(row => row.cells.map((cell, index) => sourceCell(cell, row.sourceRow > headerRow ? columnByIndex.get(index) : undefined, `${excelColumnLetter(index)}${row.sourceRow}`, name, warnings)))
  const uniqueName = (label: string) => name.toLowerCase() === label.toLowerCase() ? `${label} 2` : label
  const notesSheetName = uniqueName('Template notes')
  const metadata = {
    version: 1, clientId: batch.clientId, sourceBatchId: batch.id, sheetName: name, notesSheetName,
    month: batch.month, headerRow, salaryTemplateId: batch.salaryTemplateId, ...(sheet.template.simpleLayout ? { simpleLayout: true } : {}),
    headers: rows.find(row => row.sourceRow === headerRow)!.cells.map(cell => cell.value.trim()), columns,
    excludedRows: rows.filter(row => excludedSourceRows.includes(row.sourceRow)).map(row => ({ sourceRow: row.sourceRow, values: row.cells.map(cell => cell.value) })),
    warnings: warnings.length > 100 ? [...warnings.slice(0, 100), `${warnings.length - 100} additional formula notes are in the exported Template notes worksheet.`] : warnings,
  }
  const serialized = JSON.stringify(metadata)
  const sheets: XlsxSheet[] = [
    { name, rows: exportedRows, rowNumbers: rows.map(row => row.sourceRow), dateSystem: sheet.dateSystem },
    { name: notesSheetName, rows: [
      ['Excel Payslips — editable salary template'], ['Client', batch.clientName || String(batch.clientId)], ['Source month', batch.month], ['Source batch', batch.id],
      ['Keep employee codes unchanged for existing employees. Leave the code blank only for a new employee; Save batch will assign one.'],
      ['Edit attendance, inputs or components; copy salary formulas into new employee rows. Recalculate and save this workbook in Excel before uploading.'],
      ['Choose the salary month and review column mapping, included employees and amounts during import. Added components need an earning/deduction/information mapping.'],
      ['Only the saved salary worksheet is reconstructed. Original workbook formatting, other worksheets, tables and defined names are not included.'],
      ['Import and Save batch creates a separate batch. Existing batches, Employee Master and regular payroll remain unchanged.'],
      ...(warnings.length ? [['Formula notes — these values need manual review when inputs change'], ...warnings.map(warning => [warning])] : []),
    ] },
    { name: uniqueName('_FrevoPayslip'), hidden: true, rows: [[payslipTemplateMarker], ...(serialized.match(/[\s\S]{1,30000}/g) || []).map(chunk => [chunk])] },
  ]
  return { blob: buildXlsxBlob(sheets), warnings }
}

export function buildExcelPayslipTemplateBlob(batch: ExcelPayslipBatch, source: ExcelPayslipCalculationSource) { return buildExcelPayslipTemplate(batch, source).blob }

export function downloadExcelPayslipTemplate(batch: ExcelPayslipBatch, source: ExcelPayslipCalculationSource) {
  const { blob, warnings } = buildExcelPayslipTemplate(batch, source), link = document.createElement('a')
  link.href = URL.createObjectURL(blob)
  link.download = `${batch.clientName || `client-${batch.clientId}`}-${batch.month}-salary-template.xlsx`.replace(/[<>:"/\\|?*]/g, '-')
  link.style.display = 'none'; document.body.appendChild(link); link.click()
  window.setTimeout(() => { URL.revokeObjectURL(link.href); link.remove() }, 500)
  return warnings
}
