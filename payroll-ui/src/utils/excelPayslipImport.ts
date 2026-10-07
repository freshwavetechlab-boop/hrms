import type { ExcelPayslipRow } from '../services/excelPayslipService'

export type PayslipColumnKind = 'employeeName' | 'employeeCode' | 'email' | 'info' | 'earning' | 'deduction' | 'employer' | 'netPay' | 'grossTotal' | 'deductionTotal' | 'ignore'
export type PayslipColumn = { columnIndex: number; sourceHeader: string; label: string; kind: PayslipColumnKind; componentId?: string }
export type PayslipCell = { value: string; formula?: boolean; error?: string; missingCachedValue?: boolean }
export type PayslipSourceRow = { sourceRow: number; cells: PayslipCell[] }
export type PayslipSheet = { name: string; rows: PayslipSourceRow[]; columnCount: number; dateSystem?: '1900' | '1904' }
export type PayslipImportIssue = { sourceRow: number; column: string; message: string }
export type PayslipImportResult = { rows: ExcelPayslipRow[]; issues: PayslipImportIssue[]; excludedRows: number[]; excludedDetails: { sourceRow: number; reason: string; amounts: string }[] }

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
export function defaultPayslipMapping(headers: string[]): PayslipColumn[] {
  const norm = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, '')
  const plrs = norm(headers[3] || '') === 'nameoftheperson' && norm(headers[10] || '') === 'wages' && norm(headers[13] || '') === 'netpay'
  const plrsKinds: Record<number, PayslipColumnKind> = { 2: 'info', 3: 'employeeName', 7: 'info', 8: 'info', 9: 'info', 10: 'earning', 11: 'deduction', 12: 'deduction', 13: 'netPay', 14: 'info', 15: 'employer', 16: 'employer', 21: 'info', 22: 'info', 25: 'info', 26: 'info' }
  return headers.map((sourceHeader, columnIndex) => {
    const key = norm(sourceHeader)
    let kind: PayslipColumnKind = 'ignore'
    if (plrs) kind = plrsKinds[columnIndex] || 'ignore'
    else if (['employeename', 'nameofemployee', 'name'].includes(key)) kind = 'employeeName'
    else if (['employeecode', 'empcode', 'employeeid'].includes(key)) kind = 'employeeCode'
    else if (['email', 'emailaddress', 'employeeemail'].includes(key)) kind = 'email'
    else if (['netpay', 'netsalary', 'takehome'].includes(key)) kind = 'netPay'
    else if (['grosspay', 'grosssalary', 'grosstotal'].includes(key)) kind = 'grossTotal'
    else if (['totaldeduction', 'totaldeductions'].includes(key)) kind = 'deductionTotal'
    else if (['basic', 'basicpay', 'basicsalary', 'wages', 'hra', 'allowance'].includes(key)) kind = 'earning'
    const duplicate = sourceHeader && headers.filter(header => header === sourceHeader).length > 1
    const label = plrs && columnIndex === 8 ? 'Monthly rate' : plrs && columnIndex === 14 ? 'Bonus (as stated)' : `${sourceHeader || 'Column'}${duplicate || !sourceHeader ? ` (${excelColumnLetter(columnIndex)})` : ''}`
    return { columnIndex, sourceHeader, label, kind }
  })
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
  const shared = Array.from(parse('xl/sharedStrings.xml').getElementsByTagName('si')).map(item => Array.from(item.getElementsByTagName('t')).map(node => node.textContent || '').join(''))
  const styles = parse('xl/styles.xml'), formats = new Map(Array.from(styles.getElementsByTagName('numFmt')).map(node => [node.getAttribute('numFmtId'), node.getAttribute('formatCode') || '']))
  const zeroFormats = Array.from(styles.getElementsByTagName('cellXfs')[0]?.getElementsByTagName('xf') || []).map(node => formats.get(node.getAttribute('numFmtId')) || '')
  const rels = new Map(Array.from(parse('xl/_rels/workbook.xml.rels').getElementsByTagName('Relationship')).filter(node => node.getAttribute('TargetMode') !== 'External').map(node => [node.getAttribute('Id'), node.getAttribute('Target') || '']))
  const workbook = parse('xl/workbook.xml'), dateSystem = ['1', 'true'].includes(workbook.getElementsByTagName('workbookPr')[0]?.getAttribute('date1904') || '') ? '1904' : '1900'
  const sheets = Array.from(workbook.getElementsByTagName('sheet')).map((node, index) => {
    const target = rels.get(node.getAttribute('r:id'))
    if (!target) throw new Error('Worksheet relationship is missing.')
    const path = target.startsWith('/') ? target.slice(1) : target.startsWith('xl/') ? target : `xl/${target}`
    if (!entries.has(path)) throw new Error('Worksheet data is missing.')
    return { ...parsePayslipSheetXml(entries.get(path)!, shared, node.getAttribute('name') || `Sheet ${index + 1}`, zeroFormats), dateSystem } as PayslipSheet
  })
  if (!sheets.length) throw new Error('No worksheets found.')
  return sheets
}

function xmlDocument(xml: string) {
  if (!xml) return new DOMParser().parseFromString('<empty/>', 'application/xml')
  const document = new DOMParser().parseFromString(xml, 'application/xml')
  if (document.getElementsByTagName('parsererror').length) throw new Error('The workbook contains invalid XML.')
  return document
}
export function parsePayslipSheetXml(xml: string, shared: string[], name: string, zeroFormats: string[] = []): PayslipSheet {
  const document = xmlDocument(xml), rows: PayslipSourceRow[] = []
  let columnCount = 0
  for (const row of Array.from(document.getElementsByTagName('row'))) {
    const cells: PayslipCell[] = []
    const sourceRow = Number(row.getAttribute('r')) || rows.length + 1
    if (sourceRow > 100000) throw new Error('Worksheet exceeds the supported row range (100,000).')
    for (const node of Array.from(row.getElementsByTagName('c'))) {
      const reference = node.getAttribute('r') || '', letters = reference.match(/^[A-Z]+/i)?.[0] || ''
      const index = letters ? Array.from(letters.toUpperCase()).reduce((total, char) => total * 26 + char.charCodeAt(0) - 64, 0) - 1 : cells.length
      if (index >= 512) throw new Error('Worksheet exceeds the supported column range (512).')
      const type = node.getAttribute('t'), valueNode = node.getElementsByTagName('v')[0], formula = node.getElementsByTagName('f').length > 0
      const raw = type === 'inlineStr' ? Array.from(node.getElementsByTagName('t')).map(item => item.textContent || '').join('') : valueNode?.textContent || ''
      let value = type === 's' ? shared[Number(raw)] || '' : raw
      const format = zeroFormats[Number(node.getAttribute('s') || 0)] || ''
      if ((!type || type === 'n') && /^0{2,}$/.test(format) && /^\d+$/.test(raw)) value = raw.padStart(format.length, '0')
      cells[index] = { value, ...(formula ? { formula: true } : {}), ...(type === 'e' ? { error: raw || 'Unknown Excel error' } : {}), ...(formula && (!valueNode || raw === '') ? { missingCachedValue: true } : {}) }
      columnCount = Math.max(columnCount, index + 1)
    }
    rows.push({ sourceRow, cells })
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
