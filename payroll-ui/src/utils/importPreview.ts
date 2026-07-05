export type ImportPreviewIssue = { rowNumber: number; column?: string; message: string }
export type ImportPreviewRow = Record<string, string>
export type ImportPreviewData = { headers: string[]; rows: string[][] }
export type ImportPreviewSheet = ImportPreviewData & { name: string }
export type ImportPreviewRules = {
  required?: string[]
  unique?: string[][]
  booleans?: string[]
  numbers?: string[]
  dates?: string[]
  enums?: Record<string, string[]>
  custom?: (row: ImportPreviewRow, rowNumber: number) => ImportPreviewIssue[]
}

const textDecoder = new TextDecoder()
const norm = (value: string) => value.replace(/[\s_-]/g, '').toLowerCase()

export async function parseImportPreviewFile(file: File): Promise<ImportPreviewData> {
  const rows = file.name.toLowerCase().endsWith('.xlsx') ? (await parseXlsxSheets(file))[0]?.rows ?? [] : parseCsv(await file.text())
  return normalizeRows(rows)
}

export async function parseImportPreviewSheets(file: File): Promise<ImportPreviewSheet[]> {
  if (!file.name.toLowerCase().endsWith('.xlsx')) return [{ name: 'CSV', ...normalizeRows(parseCsv(await file.text())) }]
  return (await parseXlsxSheets(file)).map(sheet => ({ name: sheet.name, ...normalizeRows(sheet.rows) })).filter(sheet => sheet.headers.length)
}

function normalizeRows(rows: string[][]): ImportPreviewData {
  const firstNonEmpty = rows.findIndex(row => row.some(cell => cell.trim()))
  if (firstNonEmpty < 0) return { headers: [], rows: [] }
  const headers = rows[firstNonEmpty].map(cell => cell.trim())
  return { headers, rows: rows.slice(firstNonEmpty + 1).filter(row => row.some(cell => cell.trim())) }
}

export function validateImportPreview(data: ImportPreviewData, rules: ImportPreviewRules): ImportPreviewIssue[] {
  const issues: ImportPreviewIssue[] = []
  const headerIndex = (name: string) => data.headers.findIndex(header => norm(header) === norm(name))
  const value = (row: string[], name: string) => {
    const index = headerIndex(name)
    return index >= 0 && index < row.length ? row[index].trim() : ''
  }
  for (const field of [...(rules.required ?? []), ...(rules.booleans ?? []), ...(rules.numbers ?? []), ...(rules.dates ?? []), ...Object.keys(rules.enums ?? {})]) {
    if (headerIndex(field) < 0) issues.push({ rowNumber: 1, column: field, message: `Missing column: ${field}` })
  }
  const validFlag = (text: string) => !text || ['true', 'yes', 'active', '1', 'false', 'no', 'inactive', '0'].includes(text.toLowerCase())
  data.rows.forEach((row, rowIndex) => {
    const rowNumber = rowIndex + 2
    const rowMap = Object.fromEntries(data.headers.map((header, index) => [header, row[index]?.trim() ?? '']))
    for (const field of rules.required ?? []) if (!value(row, field)) issues.push({ rowNumber, column: field, message: `${field} is required.` })
    for (const field of rules.booleans ?? []) if (!validFlag(value(row, field))) issues.push({ rowNumber, column: field, message: `${field} must be TRUE/FALSE.` })
    for (const field of rules.numbers ?? []) {
      const text = value(row, field)
      if (text && Number.isNaN(Number(text))) issues.push({ rowNumber, column: field, message: `${field} must be numeric.` })
    }
    for (const field of rules.dates ?? []) {
      const text = value(row, field)
      if (text && Number.isNaN(Date.parse(text)) && Number.isNaN(Number(text))) issues.push({ rowNumber, column: field, message: `${field} must be a valid date.` })
    }
    for (const [field, options] of Object.entries(rules.enums ?? {})) {
      const text = value(row, field)
      if (text && !options.some(option => option.toLowerCase() === text.toLowerCase())) issues.push({ rowNumber, column: field, message: `${field} has invalid value "${text}".` })
    }
    issues.push(...(rules.custom?.(rowMap, rowNumber) ?? []))
  })
  for (const fields of rules.unique ?? []) {
    const seen = new Map<string, number>()
    data.rows.forEach((row, rowIndex) => {
      const key = fields.map(field => value(row, field).toLowerCase()).join('|')
      if (!key.replace(/\|/g, '')) return
      const rowNumber = rowIndex + 2
      const first = seen.get(key)
      if (first) issues.push({ rowNumber, column: fields[fields.length - 1], message: `${fields.join(' + ')} duplicates row ${first}.` })
      else seen.set(key, rowNumber)
    })
  }
  return issues
}

export function hasCellIssue(issues: ImportPreviewIssue[], rowNumber: number, header: string) {
  return issues.some(issue => issue.rowNumber === rowNumber && (!issue.column || norm(issue.column) === norm(header)))
}

function parseCsv(text: string) {
  const rows: string[][] = []
  let row: string[] = []
  let cell = ''
  let quoted = false
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (quoted && ch === '"' && text[i + 1] === '"') { cell += '"'; i++ }
    else if (ch === '"') quoted = !quoted
    else if (!quoted && ch === ',') { row.push(cell); cell = '' }
    else if (!quoted && (ch === '\n' || ch === '\r')) {
      if (ch === '\r' && text[i + 1] === '\n') i++
      row.push(cell); rows.push(row); row = []; cell = ''
    } else cell += ch
  }
  row.push(cell)
  if (row.some(value => value.length)) rows.push(row)
  return rows
}

async function parseXlsxSheets(file: File) {
  const bytes = new Uint8Array(await file.arrayBuffer())
  const entries = await unzip(bytes)
  const shared = parseSharedStrings(entries.get('xl/sharedStrings.xml'))
  const workbookSheets = parseWorkbookSheets(entries.get('xl/workbook.xml'), entries.get('xl/_rels/workbook.xml.rels'))
  const targets = workbookSheets.length ? workbookSheets : Array.from(entries.keys()).filter(key => /^xl\/worksheets\/sheet\d+\.xml$/i.test(key)).sort().map((path, index) => ({ name: `Sheet ${index + 1}`, path }))
  const sheets = targets.map(sheet => {
    const xml = entries.get(sheet.path)
    return xml ? { name: sheet.name, rows: parseSheet(xml, shared) } : null
  }).filter((sheet): sheet is { name: string; rows: string[][] } => Boolean(sheet))
  if (!sheets.length) throw new Error('No worksheets found.')
  return sheets
}

async function unzip(bytes: Uint8Array) {
  const entries = new Map<string, string>()
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const eocd = findSignature(view, 0x06054b50, Math.max(0, bytes.length - 65558), bytes.length - 4)
  if (eocd < 0) throw new Error('Invalid XLSX file.')
  const count = view.getUint16(eocd + 10, true)
  let offset = view.getUint32(eocd + 16, true)
  for (let i = 0; i < count; i++) {
    if (view.getUint32(offset, true) !== 0x02014b50) break
    const method = view.getUint16(offset + 10, true)
    const compressedSize = view.getUint32(offset + 20, true)
    const nameLength = view.getUint16(offset + 28, true)
    const extraLength = view.getUint16(offset + 30, true)
    const commentLength = view.getUint16(offset + 32, true)
    const localOffset = view.getUint32(offset + 42, true)
    const name = textDecoder.decode(bytes.slice(offset + 46, offset + 46 + nameLength))
    const localNameLength = view.getUint16(localOffset + 26, true)
    const localExtraLength = view.getUint16(localOffset + 28, true)
    const dataStart = localOffset + 30 + localNameLength + localExtraLength
    const compressed = bytes.slice(dataStart, dataStart + compressedSize)
    const data = method === 0 ? compressed : method === 8 ? await inflateRaw(compressed) : new Uint8Array()
    if (data.length) entries.set(name, textDecoder.decode(data))
    offset += 46 + nameLength + extraLength + commentLength
  }
  return entries
}

function findSignature(view: DataView, signature: number, start: number, end: number) {
  for (let i = end; i >= start; i--) if (view.getUint32(i, true) === signature) return i
  return -1
}

async function inflateRaw(data: Uint8Array) {
  const buffer = new ArrayBuffer(data.byteLength)
  new Uint8Array(buffer).set(data)
  const stream = new Blob([buffer]).stream().pipeThrough(new DecompressionStream('deflate-raw'))
  return new Uint8Array(await new Response(stream).arrayBuffer())
}

function parseSharedStrings(xml = '') {
  if (!xml) return []
  const doc = new DOMParser().parseFromString(xml, 'application/xml')
  return Array.from(doc.getElementsByTagName('si')).map(item => Array.from(item.getElementsByTagName('t')).map(text => text.textContent ?? '').join(''))
}

function parseWorkbookSheets(workbookXml = '', relsXml = '') {
  if (!workbookXml) return []
  const workbook = new DOMParser().parseFromString(workbookXml, 'application/xml')
  const rels = relsXml ? new DOMParser().parseFromString(relsXml, 'application/xml') : null
  const relMap = new Map(Array.from(rels?.getElementsByTagName('Relationship') ?? []).map(rel => [rel.getAttribute('Id') ?? '', rel.getAttribute('Target') ?? '']))
  return Array.from(workbook.getElementsByTagName('sheet')).map((sheet, index) => {
    const name = sheet.getAttribute('name') || `Sheet ${index + 1}`
    const relId = sheet.getAttribute('r:id') || sheet.getAttribute('id') || `rId${index + 1}`
    const target = relMap.get(relId) || `worksheets/sheet${index + 1}.xml`
    const path = target.startsWith('/') ? target.slice(1) : target.startsWith('xl/') ? target : `xl/${target}`
    return { name, path }
  })
}

function parseSheet(xml: string, shared: string[]) {
  const doc = new DOMParser().parseFromString(xml, 'application/xml')
  return Array.from(doc.getElementsByTagName('row')).map(row => {
    const values: string[] = []
    Array.from(row.getElementsByTagName('c')).forEach(cell => {
      const index = cellIndex(cell.getAttribute('r') ?? 'A1')
      while (values.length < index) values.push('')
      const type = cell.getAttribute('t') ?? ''
      const raw = type === 'inlineStr' ? cell.getElementsByTagName('t')[0]?.textContent ?? '' : cell.getElementsByTagName('v')[0]?.textContent ?? ''
      values.push(type === 's' ? shared[Number(raw)] ?? '' : raw)
    })
    return values
  })
}

function cellIndex(reference: string) {
  let n = 0
  for (const ch of reference.match(/^[A-Z]+/i)?.[0] ?? '') n = n * 26 + ch.toUpperCase().charCodeAt(0) - 64
  return Math.max(0, n - 1)
}
