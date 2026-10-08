import type { ExcelPayslipRow } from '../services/excelPayslipService'

export type PayslipColumnKind = 'employeeName' | 'employeeCode' | 'email' | 'info' | 'earning' | 'deduction' | 'employer' | 'netPay' | 'grossTotal' | 'deductionTotal' | 'ignore'
export type PayslipColumn = { columnIndex: number; sourceHeader: string; label: string; kind: PayslipColumnKind; componentId?: string }
export type PayslipCell = { value: string; formula?: boolean; formulaText?: string; calculationError?: string; error?: string; missingCachedValue?: boolean }
export type PayslipSourceRow = { sourceRow: number; cells: PayslipCell[] }
export type PayslipTemplateMetadata = { version: 1; clientId: number; sourceBatchId: string; month: string; headerRow: number; columns: PayslipColumn[]; excludedSourceRows: number[]; salaryTemplateId?: string; simpleLayout?: boolean; warnings: string[] }
export type PayslipSheet = { name: string; rows: PayslipSourceRow[]; columnCount: number; dateSystem?: '1900' | '1904'; template?: PayslipTemplateMetadata }
export const payslipTemplateMarker = 'Frevo Excel Payslip Template v1'
export type PayslipImportIssue = { sourceRow: number; column: string; message: string }
export type PayslipImportResult = { rows: ExcelPayslipRow[]; issues: PayslipImportIssue[]; excludedRows: number[]; excludedDetails: { sourceRow: number; reason: string; amounts: string }[] }

// Keep the complete sheet: unmapped cells, headers and excluded summary rows can
// still be referenced by a salary formula. Cached imports remain independent.
export function buildPayslipCalculationSource(sheet: PayslipSheet, headerRow: number, columns: PayslipColumn[], excludedSourceRows: number[] = []) {
  return {
    sheetName: sheet.name, headerRow, columnCount: sheet.columnCount, dateSystem: sheet.dateSystem || '1900',
    columns: columns.map(column => ({ ...column })),
    rows: sheet.rows.map(row => ({ sourceRow: row.sourceRow, cells: Array.from({ length: row.cells.length }, (_, index) => {
      const cell = row.cells[index]
      if (!cell) return { value: '' }
      const { formula, ...source } = cell
      return { ...source, ...(formula && !source.formulaText && !source.calculationError ? { calculationError: 'Formula expression is missing; its saved value cannot be recalculated.' } : {}) }
    }) })),
    excludedSourceRows: [...excludedSourceRows],
  }
}

const decoder = new TextDecoder()
const numericKinds = new Set<PayslipColumnKind>(['earning', 'deduction', 'employer', 'netPay', 'grossTotal', 'deductionTotal'])
const oneColumnKinds: PayslipColumnKind[] = ['employeeName', 'employeeCode', 'email', 'netPay', 'grossTotal', 'deductionTotal']
// Excel displays numbers at 15 significant digits. Normalize only the display copy,
// so a binary cache value such as 14129.499999999998 displays as 14,130 at zero decimals.
export function formatExcelPayslipAmount(value: number, decimals: 0 | 2 = 0) {
  return Number(value.toPrecision(15)).toLocaleString('en-IN', { minimumFractionDigits: decimals, maximumFractionDigits: decimals })
}
export const payslipColumnKinds: { value: PayslipColumnKind; label: string }[] = [
  { value: 'employeeName', label: 'Employee name' }, { value: 'employeeCode', label: 'Employee code (optional)' }, { value: 'email', label: 'Email (optional)' },
  { value: 'info', label: 'Information' }, { value: 'earning', label: 'Earning' }, { value: 'deduction', label: 'Deduction' },
  { value: 'employer', label: 'Employer contribution' }, { value: 'netPay', label: 'Net pay as stated' },
  { value: 'grossTotal', label: 'Declared gross total' }, { value: 'deductionTotal', label: 'Declared deduction total' }, { value: 'ignore', label: 'Ignore' },
]

export function excelColumnLetter(index: number) {
  let result = ''
  for (let value = index + 1; value > 0; value = Math.floor((value - 1) / 26)) result = String.fromCharCode(65 + (value - 1) % 26) + result
  return result
}
export function payslipHeaders(sheet: PayslipSheet, headerRow: number) {
  const row = sheet.rows.find(item => item.sourceRow === headerRow)
  return Array.from({ length: sheet.columnCount }, (_, index) => row?.cells[index]?.value.trim() || '')
}
export async function payslipHeaderSignature(headers: string[]) {
  const data = new TextEncoder().encode(JSON.stringify(headers.map((header, index) => [index, header.trim().replace(/\s+/g, ' ')])))
  return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', data)), byte => byte.toString(16).padStart(2, '0')).join('')
}
export function isPayslipPhoneLabel(label: string) {
  const key = label.normalize('NFKC').replace(/\s+\([A-Z]{1,3}\)$/, '').toLowerCase().replace(/[^a-z0-9]/g, '')
  return /^(?:(?:employee|emp|personal|work|official|primary)?(?:(?:mobile|phone|telephone)(?:no|number)?|contact(?:no|number)))$/.test(key)
}
export function payslipPhoneValue(row: Pick<ExcelPayslipRow, 'information'>) {
  return row.information.find(item => isPayslipPhoneLabel(item.label) && item.value.trim())?.value.trim() || ''
}
export function defaultPayslipMapping(headers: string[]): PayslipColumn[] {
  const norm = (value: string) => value.normalize('NFKC').toLowerCase().replace(/\b(?:inr|rs)\b\.?/g, '').replace(/[^a-z0-9]/g, '')
  const keys = headers.map(norm)
  const plrs = norm(headers[3] || '') === 'nameoftheperson' && norm(headers[10] || '') === 'wages' && norm(headers[13] || '') === 'netpay'
  const plrsKinds: Record<number, PayslipColumnKind> = { 2: 'info', 3: 'employeeName', 7: 'info', 8: 'info', 9: 'info', 10: 'earning', 11: 'deduction', 12: 'deduction', 13: 'netPay', 14: 'info', 15: 'employer', 16: 'employer', 21: 'info', 22: 'info', 25: 'info', 26: 'info' }
  const netPattern = /^(?:net(?:pay|salary|wages|payable|amount)(?:able|amount|payable)?|takehome(?:pay|salary)?|inhand(?:pay|salary)?|salaryinhand|amountpayable)$/
  const grossPattern = /^(?:gross(?:pay|salary|wages|total|earnings|amount)?|total(?:earnings|income|salary|gross|grosspay|grosssalary))$/
  const netIndex = keys.findIndex(key => netPattern.test(key))
  const ctcIndex = keys.findIndex(key => /^(?:ctc|costtocompany)$/.test(key))
  const billingSheet = ctcIndex >= 0 && keys.some(key => /^(?:invoice|invoiceamount|totalinvoice|servicecharge|sc\d|i?gst)/.test(key))
  const suggest = (key: string, sourceHeader: string, columnIndex: number): PayslipColumnKind => {
    if (/^(?:(?:employee|emp|staff|worker|personnel)(?:code|id|number|no)|codeofemployee)$/.test(key)) return 'employeeCode'
    if (isPayslipPhoneLabel(sourceHeader)) return 'info'
    if (plrs && columnIndex < 30) return plrsKinds[columnIndex] || 'ignore'
    if (/^(?:(?:employees?|emp|staff|worker|person|candidate)(?:full)?name|nameof(?:the)?(?:employee|person|staff|worker)|fullname|name)$/.test(key)) return 'employeeName'
    if (/^(?:(?:employee|emp|personal|official|work)?email(?:address|id)?)$/.test(key)) return 'email'
    if (netPattern.test(key)) return 'netPay'
    if (grossPattern.test(key)) return 'grossTotal'
    if (/^(?:totaldeductions?|deductions?total|totaldeducted|totaldeductionamount)$/.test(key)) return 'deductionTotal'

    // Identifiers, attendance and rates must never be treated as monetary deductions.
    if (/^(?:uan(?:number|no|reference)?|(?:pf|epf|esi|esic)(?:account)?(?:number|no|id)|(?:bank)?account(?:number|no)|bankname|ifsccode|ifsc)$/.test(key)
      || /^(?:department|designation|designationmanpowercategory|worklocation|location(?:name.*)?|branch|attendance|(?:total)?(?:working|worked|paid|payable|present|attended|month)days|days(?:worked|paid|attended)|rate|(?:monthly|daily|hourly|wage|salary|basic|overtime)rate|rate(?:per)?(?:day|hour)|rateof.*|.*(?:rate|percentage))$/.test(key)) return 'info'
    if (/^(?:ot|overtime|lop|lossofpay)$/.test(key)) return 'info' // May be hours/days, not amounts.
    // Billing-side bonus in these sheets is outside the stated take-home salary.
    if (billingSheet && /^bonus/.test(key) && columnIndex > netIndex && columnIndex < ctcIndex) return 'info'
    if (/^(?:ctc|costtocompany|invoice.*|.*gst.*|servicecharges?.*|sc\d.*)$/.test(key)) return 'ignore'

    const employer = /employer|company|ershare|ercontribution/.test(key) || /^(?:er)(?:pf|epf|esi|esic|nps)/.test(key) || /(?:pf|epf|esi|esic|nps)er$/.test(key)
    const employee = /employee|eeshare|eecontribution/.test(key) || /^(?:ee)(?:pf|epf|esi|esic|nps)/.test(key)
    const contribution = key.replace(/employers?|employees?|company|contributions?|deductions?|amount|share|empr|empl|ershare|eeshare/g, '').replace(/^(?:er|ee)|(?:er|ee)$/, '').replace(/\d/g, '').replace(/at$/, '')
    if (/^(?:epf|pf|providentfund|esi|esic|nps|professionaltax|pt|tds|incometax|lwf|labourwelfarefund)$/.test(contribution)) {
      // Bare percentages are configuration inputs; explicit PF/ESI amounts stay editable.
      if (/%/.test(sourceHeader) && !/\d/.test(sourceHeader)) return 'info'
      const rate = Number(sourceHeader.match(/(\d+(?:\.\d+)?)\s*%/)?.[1])
      if (employer || (!employee && ((/^(?:epf|pf)$/.test(contribution) && rate === 13) || (/^esi[c]?$/.test(contribution) && rate === 3.25)))) return 'employer'
      if (/%/.test(sourceHeader) && !employee && ![12, 0.75].includes(rate)) return 'info'
      return 'deduction'
    }
    if (employer && /pension|gratuity|insurance|admincharges/.test(key)) return 'employer'
    if (/^(?:(?:salary|loan)advance|advance(?:recovery|deduction)?|loan(?:recovery|deduction|instalments?|installments?|emi)|recovery(?:ifany)?|otherdeductions?|medicaldeduction|professionaltax|incometax|tds|pt|lwf|lop(?:deduction|amount)?|lossofpay)$/.test(key)) return 'deduction'
    if (/^(?:basic(?:pay|salary|wages)?|earnedbasic|hra|houserentallowance|da|dearnessallowance|conveyance(?:allowance)?|specialallowance|medicalallowance|otherallowances?|allowance|incentives?|bonus(?:\d+)?|overtime(?:pay|amount)?|ot(?:pay|amount)?|(?:salary)?arrears?|leaveencashment|shiftallowance|transport(?:allowance)?|travelallowance|lta|foodallowance|washingallowance|uniformallowance)$/.test(key)) return 'earning'
    return 'ignore'
  }
  const columns = headers.map((sourceHeader, columnIndex) => {
    const kind = suggest(keys[columnIndex], sourceHeader, columnIndex)
    const duplicate = sourceHeader && headers.filter(header => header === sourceHeader).length > 1
    const label = plrs && columnIndex === 8 ? 'Monthly rate' : plrs && columnIndex === 14 ? 'Bonus (as stated)' : `${sourceHeader || 'Column'}${duplicate || !sourceHeader ? ` (${excelColumnLetter(columnIndex)})` : ''}`
    return { columnIndex, sourceHeader, label, kind }
  })
  const wages = columns.filter(column => /^(?:wages|salary|earned(?:salary|wages))$/.test(keys[column.columnIndex]))
  const itemized = columns.some(column => column.kind === 'earning' && !wages.includes(column))
  const gross = columns.filter(column => column.kind === 'grossTotal')
  for (const column of wages) {
    column.kind = itemized ? (gross.length ? 'info' : 'grossTotal') : 'earning'
  }
  // A sheet with only gross pay is still usable, but never add gross to itemized earnings.
  if (!columns.some(column => column.kind === 'earning') && gross.length === 1) gross[0].kind = 'earning'
  return columns
}

export function inferPayslipMonth(sheet: PayslipSheet): string {
  if (sheet.dateSystem === '1904') return ''
  for (const row of sheet.rows.slice(0, 10)) {
    const labelIndex = row.cells.findIndex(cell => cell?.value.trim().toLowerCase() === 'month')
    if (labelIndex < 0) continue
    const cell = row.cells.slice(labelIndex + 1, labelIndex + 4).find(item => item?.value.trim())
    if (!cell || cell.error || cell.missingCachedValue) return ''
    const value = cell.value.trim()
    if (/^\d{4}-(0[1-9]|1[0-2])(?:-\d{2})?$/.test(value)) return value.slice(0, 7)
    if (/^\d+(?:\.\d+)?$/.test(value) && Number(value) > 20000 && Number(value) < 100000) return new Date((Number(value) - 25569) * 86400000).toISOString().slice(0, 7)
    return ''
  }
  return ''
}

export function buildExcelPayslipRows(sheet: PayslipSheet, headerRow: number, columns: PayslipColumn[], excludedSourceRows: number[] = []): PayslipImportResult {
  const issues: PayslipImportIssue[] = [], rows: ExcelPayslipRow[] = [], excludedRows: number[] = [], excludedDetails: PayslipImportResult['excludedDetails'] = []
  const issue = (sourceRow: number, column: string, message: string) => issues.push({ sourceRow, column, message })
  for (const kind of oneColumnKinds) {
    const count = columns.filter(column => column.kind === kind).length
    if (count > 1) issue(headerRow, '', `Map ${payslipColumnKinds.find(item => item.value === kind)?.label} only once.`)
    if (count === 0 && ['employeeName', 'netPay'].includes(kind)) issue(headerRow, '', `Map an ${kind === 'employeeName' ? 'employee name' : 'explicit net pay'} column.`)
  }
  if (!columns.some(column => column.kind === 'earning')) issue(headerRow, '', 'Map at least one earning column.')
  for (const column of columns) {
    if (column.kind !== 'ignore' && !column.label.trim()) issue(headerRow, excelColumnLetter(column.columnIndex), 'Enter a payslip label.')
    if (column.label.length > 80) issue(headerRow, excelColumnLetter(column.columnIndex), 'Shorten the payslip label to 80 characters or fewer.')
  }
  if (issues.length) return { rows, issues, excludedRows, excludedDetails }
  const nameColumn = columns.find(column => column.kind === 'employeeName')!
  for (const source of sheet.rows.filter(row => row.sourceRow > headerRow)) {
    if (!source.cells.some(cell => cell?.value || cell?.formula)) continue
    const name = source.cells[nameColumn.columnIndex]?.value.trim() || ''
    if (excludedSourceRows.includes(source.sourceRow) || !name || /^(grand\s*)?total(?:s|\s|:|$)/i.test(name)) {
      excludedRows.push(source.sourceRow)
      excludedDetails.push({ sourceRow: source.sourceRow, reason: excludedSourceRows.includes(source.sourceRow) ? 'Manually excluded' : !name ? 'No employee name in the mapped column; review totals or missing names' : 'Total / summary label', amounts: columns.filter(column => numericKinds.has(column.kind)).map(column => `${column.label}: ${source.cells[column.columnIndex]?.value || '(blank)'}`).join('; ') })
      continue
    }
    const row: ExcelPayslipRow = { id: `row-${source.sourceRow}`, sourceRow: source.sourceRow, employeeCode: '', employeeName: name, email: '', information: [], earnings: [], deductions: [], employerContributions: [], netPay: 0, declaredGross: null, declaredDeductions: null, warnings: [] }
    for (const column of columns.filter(item => item.kind !== 'ignore')) {
      const cell = source.cells[column.columnIndex], value = cell?.value ?? '', label = column.label.trim(), letter = excelColumnLetter(column.columnIndex)
      if (cell?.error || cell?.missingCachedValue) {
        issue(source.sourceRow, letter, cell.error ? `Saved Excel error ${cell.error}. Correct the source or ignore this column.` : 'Formula has no saved result. Recalculate and save the workbook in Excel before importing.')
        continue
      }
      if (numericKinds.has(column.kind)) {
        const amount = parsePayslipAmount(value)
        if (amount === null) { issue(source.sourceRow, letter, 'A saved numeric amount is required; blank amounts are not zero.'); continue }
        if (column.kind === 'earning') row.earnings.push({ label, amount })
        if (column.kind === 'deduction') row.deductions.push({ label, amount })
        if (column.kind === 'employer') row.employerContributions.push({ label, amount })
        if (column.kind === 'netPay') row.netPay = amount
        if (column.kind === 'grossTotal') row.declaredGross = amount
        if (column.kind === 'deductionTotal') row.declaredDeductions = amount
      } else if (column.kind === 'employeeCode') row.employeeCode = value.trim()
      else if (column.kind === 'email') row.email = value.trim()
      else if (column.kind === 'info') row.information.push({ label, value })
    }
    rows.push(row)
  }
  if (!rows.length) issue(headerRow, '', 'No employee rows found. Check the worksheet, header row and employee name mapping.')
  const codeCounts = new Map<string, number>()
  for (const row of rows) { const code = row.employeeCode.normalize('NFKC').trim().replace(/\s+/g, ' ').toUpperCase(); if (code) codeCounts.set(code, (codeCounts.get(code) || 0) + 1) }
  for (const row of rows) {
    const code = row.employeeCode.normalize('NFKC').trim().replace(/\s+/g, ' ').toUpperCase()
    if (code && codeCounts.get(code)! > 1) issue(row.sourceRow, 'Employee code', `Employee code ${row.employeeCode} is repeated. Give each employee a unique permanent code.`)
  }
  return { rows, issues, excludedRows, excludedDetails }
}

export function parsePayslipAmount(value: string): number | null {
  const text = value.trim()
  if (!text || !/^[+-]?(?:\d+(?:,\d{2,3})*(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(text)) return null
  const number = Number(text.replace(/,/g, ''))
  return Number.isFinite(number) ? number : null
}

export function parsePayslipCsv(text: string): PayslipSheet {
  const rows: PayslipSourceRow[] = []
  let cells: PayslipCell[] = [], value = '', quoted = false, physicalLine = 1, recordLine = 1
  const finish = () => { cells.push({ value }); rows.push({ sourceRow: recordLine, cells }); cells = []; value = '' }
  const input = text.replace(/^\uFEFF/, '')
  for (let index = 0; index < input.length; index++) {
    const char = input[index]
    if (quoted && char === '"' && input[index + 1] === '"') { value += '"'; index++ }
    else if (char === '"' && (quoted || !value)) quoted = !quoted
    else if (!quoted && char === ',') { cells.push({ value }); value = '' }
    else if (char === '\r' || char === '\n') {
      const newline = char === '\r' && input[index + 1] === '\n' ? '\r\n' : char
      if (newline.length === 2) index++
      if (quoted) value += newline
      else finish()
      physicalLine++
      if (!quoted) recordLine = physicalLine
    } else value += char
  }
  if (quoted) throw new Error('CSV contains an unclosed quoted field.')
  if (value.length || cells.length) finish()
  return { name: 'CSV', rows, columnCount: Math.max(0, ...rows.map(row => row.cells.length)) }
}

export async function parseExcelPayslipFile(file: File): Promise<PayslipSheet[]> {
  if (!/\.(xlsx|csv)$/i.test(file.name)) throw new Error('Choose an XLSX or CSV file.')
  if (file.size > 30 * 1024 * 1024) throw new Error('Choose a file smaller than 30 MB.')
  if (/\.csv$/i.test(file.name)) return [parsePayslipCsv(await file.text())]
  const entries = await readXlsxEntries(new Uint8Array(await file.arrayBuffer()))
  const parse = (path: string) => xmlDocument(entries.get(path) || '')
  // OOXML prefixes are arbitrary: <sheet> and <x:sheet> represent the same element.
  const shared = Array.from(parse('xl/sharedStrings.xml').getElementsByTagNameNS('*', 'si')).map(item => Array.from(item.getElementsByTagNameNS('*', 't')).map(node => node.textContent || '').join(''))
  const styles = parse('xl/styles.xml'), formats = new Map(Array.from(styles.getElementsByTagNameNS('*', 'numFmt')).map(node => [node.getAttribute('numFmtId'), node.getAttribute('formatCode') || '']))
  const zeroFormats = Array.from(styles.getElementsByTagNameNS('*', 'cellXfs')[0]?.getElementsByTagNameNS('*', 'xf') || []).map(node => formats.get(node.getAttribute('numFmtId')) || '')
  const rels = new Map(Array.from(parse('xl/_rels/workbook.xml.rels').getElementsByTagNameNS('*', 'Relationship')).filter(node => node.getAttribute('TargetMode') !== 'External').map(node => [node.getAttribute('Id'), node.getAttribute('Target') || '']))
  const workbook = parse('xl/workbook.xml'), dateSystem = ['1', 'true'].includes(workbook.getElementsByTagNameNS('*', 'workbookPr')[0]?.getAttribute('date1904') || '') ? '1904' : '1900'
  const sheets = Array.from(workbook.getElementsByTagNameNS('*', 'sheet')).map((node, index) => {
    const target = rels.get(node.getAttributeNS('http://schemas.openxmlformats.org/officeDocument/2006/relationships', 'id') || node.getAttribute('r:id'))
    if (!target) throw new Error('Worksheet relationship is missing.')
    const path = target.startsWith('/') ? target.slice(1) : target.startsWith('xl/') ? target : `xl/${target}`
    if (!entries.has(path)) throw new Error('Worksheet data is missing.')
    return { ...parsePayslipSheetXml(entries.get(path)!, shared, node.getAttribute('name') || `Sheet ${index + 1}`, zeroFormats), dateSystem } as PayslipSheet
  })
  if (!sheets.length) throw new Error('No worksheets found.')
  return restorePayslipTemplateMetadata(sheets)
}

// Export metadata is a convenience for mapping, never an authority for client access.
// Changed header positions and shifted excluded rows require explicit review.
export function restorePayslipTemplateMetadata(sheets: PayslipSheet[]): PayslipSheet[] {
  const metadata = sheets.find(sheet => sheet.rows[0]?.cells[0]?.value === payslipTemplateMarker)
  if (!metadata) return sheets
  const serialized = metadata.rows.slice(1).map(row => row.cells[0]?.value || '').join('')
  if (serialized.length > 1024 * 1024) throw new Error('Payslip template metadata is too large.')
  let saved: { version: number; clientId: number; sourceBatchId: string; sheetName: string; notesSheetName?: string; month: string; headerRow: number; columns: PayslipColumn[]; headers: string[]; excludedRows?: { sourceRow: number; values: string[] }[]; salaryTemplateId?: string; simpleLayout?: boolean; warnings?: string[] }
  try { saved = JSON.parse(serialized) } catch { throw new Error('Payslip template metadata is invalid. Import the original workbook instead.') }
  if (!saved || saved.version !== 1 || !Number.isInteger(saved.clientId) || saved.clientId < 1 || typeof saved.sourceBatchId !== 'string' || typeof saved.sheetName !== 'string' || !/^\d{4}-(0[1-9]|1[0-2])$/.test(saved.month) || !Number.isInteger(saved.headerRow) || saved.headerRow < 1 || saved.headerRow > 100000 || !Array.isArray(saved.headers) || saved.headers.length > 512 || saved.headers.some(header => typeof header !== 'string') || !Array.isArray(saved.columns) || saved.columns.length > 512) throw new Error('Payslip template metadata is invalid. Import the original workbook instead.')
  if (saved.simpleLayout !== undefined && typeof saved.simpleLayout !== 'boolean') throw new Error('Payslip template layout option is invalid. Import the original workbook instead.')
  const sheet = sheets.find(item => item.name === saved.sheetName)
  if (!sheet) throw new Error('The exported salary worksheet was renamed or removed. Restore its original sheet name before importing.')
  const warnings = (Array.isArray(saved.warnings) ? saved.warnings : []).filter(value => typeof value === 'string').slice(0, 1000)
  const headers = payslipHeaders(sheet, saved.headerRow), positionsMatch = saved.headers.every((header, index) => headers[index] === header)
  const defaults = defaultPayslipMapping(headers)
  const validColumns = saved.columns.every(column => column && Number.isInteger(column.columnIndex) && column.columnIndex >= 0 && column.columnIndex < saved.headers.length && typeof column.sourceHeader === 'string' && typeof column.label === 'string' && column.label.length <= 80 && payslipColumnKinds.some(kind => kind.value === column.kind) && (column.componentId == null || typeof column.componentId === 'string')) && new Set(saved.columns.map(column => column.columnIndex)).size === saved.columns.length
  const columns = positionsMatch && validColumns ? defaults.map(column => { const previous = saved.columns.find(item => item.columnIndex === column.columnIndex); return previous ? { ...previous, sourceHeader: column.sourceHeader } : column }) : defaults
  if (!positionsMatch || !validColumns) warnings.unshift('Column headings or positions changed. Review every column mapping before saving; previous mappings were not applied.')
  const excludedSourceRows: number[] = []
  if (Array.isArray(saved.excludedRows)) for (const excluded of saved.excludedRows) {
    const row = sheet.rows.find(item => item.sourceRow === excluded?.sourceRow)
    if (Number.isInteger(excluded?.sourceRow) && Array.isArray(excluded?.values) && excluded.values.every(value => typeof value === 'string') && row && row.cells.length === excluded.values.length && excluded.values.every((value, index) => (row.cells[index]?.value || '') === value) && positionsMatch) excludedSourceRows.push(excluded.sourceRow)
    else if (!warnings.includes('Previously excluded rows changed position or values. Review employee rows and exclusions before saving.')) warnings.unshift('Previously excluded rows changed position or values. Review employee rows and exclusions before saving.')
  }
  sheet.template = { version: 1, clientId: saved.clientId, sourceBatchId: saved.sourceBatchId, month: saved.month, headerRow: saved.headerRow, columns, excludedSourceRows, salaryTemplateId: typeof saved.salaryTemplateId === 'string' ? saved.salaryTemplateId : undefined, ...(saved.simpleLayout === true ? { simpleLayout: true } : {}), warnings }
  return sheets.filter(item => item !== metadata && item.name !== saved.notesSheetName)
}

function xmlDocument(xml: string) {
  if (!xml) return new DOMParser().parseFromString('<empty/>', 'application/xml')
  const document = new DOMParser().parseFromString(xml, 'application/xml')
  if (document.getElementsByTagNameNS('*', 'parsererror').length) throw new Error('The workbook contains invalid XML.')
  return document
}

const a1Reference = /^(\$?)([A-Z]{1,3})(\$?)([1-9]\d*)$/i
function referencePosition(reference: string) {
  const match = a1Reference.exec(reference)
  if (!match) return null
  const column = Array.from(match[2].toUpperCase()).reduce((total, char) => total * 26 + char.charCodeAt(0) - 64, 0)
  const row = Number(match[4])
  return column <= 16384 && row <= 1048576 ? { column, row, columnAbsolute: !!match[1], rowAbsolute: !!match[3] } : null
}

// Shared-formula expressions occur only on their anchor cell in XLSX. Translate
// reference tokens, never text literals, sheet qualifiers or workbook names.
export function translatePayslipSharedFormula(expression: string, anchor: string, destination: string) {
  const from = referencePosition(anchor), to = referencePosition(destination)
  if (!from || !to) throw new Error('Shared formula has an invalid cell reference.')
  const columnDelta = to.column - from.column, rowDelta = to.row - from.row
  const shift = (reference: string) => {
    const position = referencePosition(reference)
    if (!position) return reference
    const column = position.column + (position.columnAbsolute ? 0 : columnDelta), row = position.row + (position.rowAbsolute ? 0 : rowDelta)
    return column < 1 || column > 16384 || row < 1 || row > 1048576 ? '#REF!' : `${position.columnAbsolute ? '$' : ''}${excelColumnLetter(column - 1)}${position.rowAbsolute ? '$' : ''}${row}`
  }
  let result = '', index = 0
  while (index < expression.length) {
    const char = expression[index]
    if (char === '"' || char === "'") {
      const start = index++
      while (index < expression.length) {
        if (expression[index++] !== char) continue
        if (expression[index] === char) index++
        else break
      }
      result += expression.slice(start, index)
      continue
    }
    if (char === '[') {
      const start = index++
      let depth = 1
      while (index < expression.length && depth) {
        if (expression[index] === '[') depth++
        if (expression[index] === ']') depth--
        index++
      }
      result += expression.slice(start, index)
      continue
    }
    const rest = expression.slice(index), boundary = index === 0 || !/[\w.\\]/.test(expression[index - 1])
    const qualifier = boundary ? /^[\w.\\]+(?::[\w.\\]+)?!/.exec(rest) : null
    if (qualifier) { result += qualifier[0]; index += qualifier[0].length; continue }
    const wholeRange = boundary ? /^(\$?[A-Z]{1,3}\s*:\s*\$?[A-Z]{1,3}|\$?[1-9]\d*\s*:\s*\$?[1-9]\d*)(?![\w.\\])/i.exec(rest) : null
    if (wholeRange) {
      result += wholeRange[0].replace(/\$?[A-Z]{1,3}|\$?[1-9]\d*/gi, token => {
        const isColumn = /[A-Z]/i.test(token), position = referencePosition(isColumn ? `${token}$1` : `$A${token}`)
        if (!position) return token
        const shifted = shift(isColumn ? `${token}$1` : `$A${token}`)
        return shifted === '#REF!' ? shifted : isColumn ? shifted.replace(/\$1$/, '') : shifted.replace(/^\$A/, '')
      })
      index += wholeRange[0].length; continue
    }
    const reference = boundary ? /^\$?[A-Z]{1,3}\$?[1-9]\d*/i.exec(rest) : null
    const after = reference ? rest.slice(reference[0].length) : ''
    if (reference && !/^[\w.\\]/.test(after) && !/^\s*\(/.test(after)) {
      result += shift(reference[0]); index += reference[0].length; continue
    }
    result += char; index++
  }
  return result
}

export type PayslipFormulaCell = { reference: string; cell: PayslipCell; expression: string; type: string; sharedIndex?: string | null; range?: string | null }
export function resolvePayslipSharedFormulas(formulas: PayslipFormulaCell[]) {
  const anchors = new Map<string, PayslipFormulaCell>(), duplicate = new Set<string>()
  for (const item of formulas) {
    if (item.type !== 'shared' || !item.expression || item.sharedIndex == null) continue
    if (anchors.has(item.sharedIndex)) duplicate.add(item.sharedIndex)
    anchors.set(item.sharedIndex, item)
  }
  for (const item of formulas) {
    if (item.expression) item.cell.formulaText = item.expression
    if (!['', 'normal', 'shared'].includes(item.type)) {
      item.cell.calculationError = `Excel ${item.type} formulas cannot be recalculated. Replace this with an ordinary cell formula to calculate another month.`
      continue
    }
    if (item.type === 'shared') {
      const anchor = item.sharedIndex == null ? undefined : anchors.get(item.sharedIndex)
      if (!anchor || duplicate.has(item.sharedIndex!)) {
        item.cell.calculationError = 'Shared formula expression is missing or ambiguous; its saved value cannot be recalculated.'
        continue
      }
      try { item.cell.formulaText = translatePayslipSharedFormula(anchor.expression, anchor.reference, item.reference) }
      catch { item.cell.calculationError = 'Shared formula has an invalid reference; its saved value cannot be recalculated.' }
    }
    if (!item.cell.formulaText) item.cell.calculationError = 'Formula expression is missing; its saved value cannot be recalculated.'
  }
}

export function parsePayslipSheetXml(xml: string, shared: string[], name: string, zeroFormats: string[] = []): PayslipSheet {
  const document = xmlDocument(xml), rows: PayslipSourceRow[] = [], formulas: PayslipFormulaCell[] = []
  let columnCount = 0
  for (const row of Array.from(document.getElementsByTagNameNS('*', 'row'))) {
    const cells: PayslipCell[] = []
    const sourceRow = Number(row.getAttribute('r')) || rows.length + 1
    if (sourceRow > 100000) throw new Error('Worksheet exceeds the supported row range (100,000).')
    for (const node of Array.from(row.getElementsByTagNameNS('*', 'c'))) {
      const reference = node.getAttribute('r') || '', letters = reference.match(/^[A-Z]+/i)?.[0] || ''
      const index = letters ? Array.from(letters.toUpperCase()).reduce((total, char) => total * 26 + char.charCodeAt(0) - 64, 0) - 1 : cells.length
      if (index >= 512) throw new Error('Worksheet exceeds the supported column range (512).')
      const type = node.getAttribute('t'), valueNode = node.getElementsByTagNameNS('*', 'v')[0], formulaNode = node.getElementsByTagNameNS('*', 'f')[0], formula = !!formulaNode
      const raw = type === 'inlineStr' ? Array.from(node.getElementsByTagNameNS('*', 't')).map(item => item.textContent || '').join('') : valueNode?.textContent || ''
      let value = type === 's' ? shared[Number(raw)] || '' : raw
      const format = zeroFormats[Number(node.getAttribute('s') || 0)] || ''
      if ((!type || type === 'n') && /^0{2,}$/.test(format) && /^\d+$/.test(raw)) value = raw.padStart(format.length, '0')
      cells[index] = { value, ...(formula ? { formula: true } : {}), ...(type === 'e' ? { error: raw || 'Unknown Excel error' } : {}), ...(formula && (!valueNode || raw === '') ? { missingCachedValue: true } : {}) }
      if (formulaNode) formulas.push({ reference: reference || `${excelColumnLetter(index)}${sourceRow}`, cell: cells[index], expression: formulaNode.textContent || '', type: formulaNode.getAttribute('t') || '', sharedIndex: formulaNode.getAttribute('si'), range: formulaNode.getAttribute('ref') })
      columnCount = Math.max(columnCount, index + 1)
    }
    rows.push({ sourceRow, cells })
  }
  resolvePayslipSharedFormulas(formulas)
  for (const formula of formulas.filter(item => item.cell.calculationError && item.range)) {
    const [start, end = start] = formula.range!.split(':').map(referencePosition)
    if (!start || !end) continue
    for (const row of rows.filter(item => item.sourceRow >= start.row && item.sourceRow <= end.row)) {
      for (let index = start.column - 1; index < Math.min(end.column, row.cells.length); index++) {
        if (row.cells[index]) row.cells[index].calculationError = formula.cell.calculationError
      }
    }
  }
  return { name, rows, columnCount }
}

// Read only workbook XML entries. No formula, relationship target or external link is executed.
async function readXlsxEntries(bytes: Uint8Array) {
  if (bytes.length < 22) throw new Error('Invalid XLSX file.')
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  let end = -1
  for (let offset = bytes.length - 22; offset >= Math.max(0, bytes.length - 65558); offset--) if (view.getUint32(offset, true) === 0x06054b50) { end = offset; break }
  if (end < 0) throw new Error('Invalid XLSX file.')
  const count = view.getUint16(end + 10, true), entries = new Map<string, string>()
  let offset = view.getUint32(end + 16, true), expanded = 0
  for (let index = 0; index < count; index++) {
    if (offset + 46 > bytes.length || view.getUint32(offset, true) !== 0x02014b50) throw new Error('Invalid XLSX archive directory.')
    const method = view.getUint16(offset + 10, true), compressedSize = view.getUint32(offset + 20, true), expandedSize = view.getUint32(offset + 24, true)
    const nameLength = view.getUint16(offset + 28, true), extraLength = view.getUint16(offset + 30, true), commentLength = view.getUint16(offset + 32, true), localOffset = view.getUint32(offset + 42, true)
    const name = decoder.decode(bytes.slice(offset + 46, offset + 46 + nameLength))
    offset += 46 + nameLength + extraLength + commentLength
    if (!/^xl\/(?:workbook\.xml|_rels\/workbook\.xml\.rels|sharedStrings\.xml|styles\.xml|worksheets\/[^/]+\.xml)$/.test(name)) continue
    expanded += expandedSize
    if (expandedSize > 50 * 1024 * 1024 || expanded > 100 * 1024 * 1024) throw new Error('Workbook data is too large to import safely.')
    if (localOffset + 30 > bytes.length || view.getUint32(localOffset, true) !== 0x04034b50 || (view.getUint16(localOffset + 6, true) & 1)) throw new Error('Encrypted or invalid XLSX file.')
    const dataStart = localOffset + 30 + view.getUint16(localOffset + 26, true) + view.getUint16(localOffset + 28, true)
    if (dataStart + compressedSize > bytes.length) throw new Error('Incomplete XLSX file.')
    const compressed = bytes.slice(dataStart, dataStart + compressedSize)
    if (![0, 8].includes(method)) throw new Error('Unsupported XLSX compression.')
    const data = method === 0 ? compressed : new Uint8Array(await new Response(new Blob([compressed]).stream().pipeThrough(new DecompressionStream('deflate-raw'))).arrayBuffer())
    if (data.length !== expandedSize) throw new Error('The XLSX archive size is inconsistent.')
    entries.set(name, decoder.decode(data))
  }
  return entries
}
