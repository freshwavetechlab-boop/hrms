import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const modules = new Map()
function load(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}, code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  modules.set(name, exports)
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', '')))
  return exports
}
const { buildExcelPayslipTemplate } = load('excelPayslipTemplate')
const { buildXlsxBlob } = load('xlsx')
const { restorePayslipTemplateMetadata, buildExcelPayslipRows, defaultPayslipMapping, payslipTemplateMarker } = load('excelPayslipImport')
const decodeXml = value => value.replace(/&(?:amp|lt|gt|quot|apos);/g, code => ({ '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'" })[code])
async function entries(blob) {
  const data = new Uint8Array(await blob.arrayBuffer()), view = new DataView(data.buffer), entries = new Map()
  let offset = 0
  while (view.getUint32(offset, true) === 0x04034b50) {
    const length = view.getUint32(offset + 18, true), nameLength = view.getUint16(offset + 26, true), extra = view.getUint16(offset + 28, true)
    const start = offset + 30 + nameLength + extra
    entries.set(new TextDecoder().decode(data.slice(offset + 30, offset + 30 + nameLength)), new TextDecoder().decode(data.slice(start, start + length)))
    offset = start + length
  }
  return entries
}
function fixture() {
  const headers = ['Employee Name', 'UAN', 'Bank Account Number', 'Monthly rate', 'Days', 'Basic', 'PF', 'Net Pay', 'Employee Code']
  const kinds = ['employeeName', 'info', 'info', 'info', 'info', 'earning', 'deduction', 'netPay', 'employeeCode']
  const cells = values => values.map(value => ({ value }))
  const source = { sheetName: 'Salary', headerRow: 3, columnCount: 9, columns: headers.map((sourceHeader, columnIndex) => ({ sourceHeader, columnIndex, label: sourceHeader, kind: kinds[columnIndex] })), rows: [
    { sourceRow: 1, cells: cells(['Month', '2026-09', '', '30']) },
    { sourceRow: 3, cells: cells(headers) },
    { sourceRow: 5, cells: cells(['Same Name', '001234567890', '0000123456789', '30000', '15', '15000', '1800', '13200', '']) },
    { sourceRow: 7, cells: cells(['Grand Total', '', '', '', '', '15000', '1800', '13200', '']) },
  ], excludedSourceRows: [7] }
  source.rows[2].cells[5].formulaText = 'D5/$D$1*E5'
  source.rows[2].cells[7].formulaText = 'F5-G5'
  const batch = { id: 'saved-batch-1', clientId: 20, clientName: 'PLRS', month: '2026-09', salaryTemplateId: 'template-1', rows: [{ sourceRow: 5, employeeCode: 'PLRS001' }] }
  return { source, batch }
}
function metadataSheet(metadata) { return { name: '_FrevoPayslip', rows: [{ sourceRow: 1, cells: [{ value: payslipTemplateMarker }] }, { sourceRow: 2, cells: [{ value: JSON.stringify(metadata) }] }], columnCount: 1 } }
async function exportParts(batch, source) {
  const result = buildExcelPayslipTemplate(batch, source), files = await entries(result.blob)
  const chunks = [...files.get('xl/worksheets/sheet3.xml').matchAll(/<t[^>]*>(.*?)<\/t>/gs)].map(match => decodeXml(match[1]))
  return { ...result, files, metadata: JSON.parse(chunks.slice(1).join('')) }
}

test('export keeps salary cell references, formula caches, precise amounts and persistent employee codes', async () => {
  const { source, batch } = fixture()
  source.rows[2].cells[5].value = '14129.499999999998'
  const { files, warnings } = await exportParts(batch, source), salary = files.get('xl/worksheets/sheet1.xml')
  assert.match(salary, /<row r="5">/)
  assert.doesNotMatch(salary, /<row r="2">/)
  assert.match(salary, /<c r="F5"><f>D5\/\$D\$1\*E5<\/f><v>14129\.499999999998<\/v>/)
  assert.match(salary, /<c r="H5"><f>F5-G5<\/f><v>13200<\/v>/)
  assert.match(salary, /<c r="I5" t="inlineStr"><is><t xml:space="preserve">PLRS001<\/t>/)
  assert.deepEqual(warnings, [])
  assert.equal(source.rows[2].cells[8].value, '', 'export does not mutate saved source')
})

test('identifier info stays text while attendance, rate and info PF amounts remain numeric', async () => {
  const { source, batch } = fixture(); source.columns[6].kind = 'info'
  const { files } = await exportParts(batch, source), salary = files.get('xl/worksheets/sheet1.xml')
  assert.match(salary, /<c r="B5" t="inlineStr"><is><t xml:space="preserve">001234567890/)
  assert.match(salary, /<c r="C5" t="inlineStr"><is><t xml:space="preserve">0000123456789/)
  assert.match(salary, /<c r="D5"><v>30000<\/v>/)
  assert.match(salary, /<c r="E5"><v>15<\/v>/)
  assert.match(salary, /<c r="G5"><v>1800<\/v>/)
})

test('missing code column is appended without moving formulas; legacy blank codes remain blank', async () => {
  const { source, batch } = fixture(); source.columns.pop(); source.columnCount = 8; source.rows.forEach(row => row.cells.splice(8))
  batch.rows[0].employeeCode = ''
  const { files, metadata } = await exportParts(batch, source), salary = files.get('xl/worksheets/sheet1.xml')
  assert.match(salary, /r="I3"[^>]*><is><t xml:space="preserve">Employee Code/)
  assert.match(salary, /<c r="F5"><f>D5\/\$D\$1\*E5<\/f>/)
  assert.equal(metadata.columns.at(-1).kind, 'employeeCode')
  assert.equal(metadata.columns.at(-1).columnIndex, 8)
  assert.doesNotMatch(salary, /PLRS001/)
})

test('same-sheet qualified references, strings and mixed absolute references retain formulas', async () => {
  const { source, batch } = fixture()
  source.rows[2].cells[5].formulaText = `IF($D5>0,'Salary'!D5+SUM(D$1:E5),"Other!A1")`
  const { files, warnings } = await exportParts(batch, source)
  assert.match(files.get('xl/worksheets/sheet1.xml'), /<f>IF\(/)
  assert.deepEqual(warnings, [])
})

test('missing sheets, workbook-defined names and structured tables freeze explicitly, including ignored columns', async () => {
  for (const formula of ["'Other sheet'!A1", 'AnnualRate*E5', 'Table1[Rate]', '[Other.xlsx]Salary!D5']) {
    const { source, batch } = fixture(); source.rows[2].cells[5].formulaText = formula; source.columns[5].kind = 'ignore'
    const { files, warnings, metadata } = await exportParts(batch, source)
    assert.match(files.get('xl/worksheets/sheet1.xml'), /<c r="F5"><v>15000<\/v>/)
    assert.equal(warnings.length, 1)
    assert.match(warnings[0], /F5.*saved value/)
    assert.deepEqual(metadata.warnings, warnings)
    assert.match(files.get('xl/worksheets/sheet2.xml'), /saved value/)
  }
})

test('workbook date system and hidden mapping are emitted; normal string exports remain plain text', async () => {
  const { source, batch } = fixture(); source.dateSystem = '1904'
  const { files } = await exportParts(batch, source)
  assert.match(files.get('xl/workbook.xml'), /date1904="1"/)
  assert.match(files.get('xl/workbook.xml'), /name="_FrevoPayslip"[^>]*state="hidden"/)
  const regular = await entries(buildXlsxBlob([{ name: 'Existing', rows: [['000123', '=SUM(A1:A2)']] }]))
  assert.doesNotMatch(regular.get('xl/worksheets/sheet1.xml'), /<f>/)
  assert.match(regular.get('xl/worksheets/sheet1.xml'), /000123/)
})

test('an unavailable formula without a cache stays an explicit Excel error instead of turning into blank or zero', async () => {
  const { source, batch } = fixture()
  source.rows[2].cells[5] = { value: '', formulaText: 'Other!A1', missingCachedValue: true }
  const { files } = await exportParts(batch, source)
  assert.match(files.get('xl/worksheets/sheet1.xml'), /<c r="F5" t="e"><v>#N\/A<\/v>/)
})

test('matching metadata restores complete mapping, template/client context and verified excluded rows', async () => {
  const { source, batch } = fixture(), { metadata } = await exportParts(batch, source)
  const sheet = { ...source, name: source.sheetName, rows: source.rows.map(row => ({ ...row, cells: Array.from({ length: 9 }, (_, index) => row.cells[index] || { value: '' }) })) }
  const restored = restorePayslipTemplateMetadata([sheet, { name: metadata.notesSheetName, rows: [], columnCount: 1 }, metadataSheet(metadata)])
  assert.equal(restored.length, 1)
  assert.equal(restored[0].template.clientId, 20)
  assert.equal(restored[0].template.salaryTemplateId, 'template-1')
  assert.equal(restored[0].template.sourceBatchId, batch.id)
  assert.deepEqual(restored[0].template.columns, source.columns)
  assert.deepEqual(restored[0].template.excludedSourceRows, [7])
})

test('appended component maps as a new column; moved headers and shifted exclusions do not retain wrong mappings', async () => {
  const { source, batch } = fixture(), { metadata } = await exportParts(batch, source)
  const sheet = { ...source, name: source.sheetName, columnCount: 10, rows: source.rows.map(row => ({ ...row, cells: Array.from({ length: 10 }, (_, index) => row.cells[index] || { value: '' }) })) }
  sheet.rows[1].cells[9] = { value: 'New allowance' }
  sheet.rows[3].cells[0] = { value: 'New Employee' }
  const restored = restorePayslipTemplateMetadata([sheet, metadataSheet(metadata)])[0]
  assert.equal(restored.template.columns.length, 10)
  assert.equal(restored.template.columns[9].kind, 'ignore', 'new columns require mapping review')
  assert.deepEqual(restored.template.excludedSourceRows, [])
  assert.match(restored.template.warnings.join(' '), /excluded rows changed/)
  sheet.rows[1].cells[0] = { value: 'Basic' }
  const moved = restorePayslipTemplateMetadata([sheet, metadataSheet(metadata)])[0]
  assert.equal(moved.template.columns[0].kind, 'earning')
  assert.match(moved.template.warnings.join(' '), /previous mappings were not applied/)
})

test('PLRS preset recognizes an appended permanent employee code and repeated codes need review', () => {
  const headers = ['', '', '', 'Name of the person', '', '', '', '', '', '', 'Wages', '', '', 'Net Pay', 'Employee Code']
  assert.equal(defaultPayslipMapping(headers).at(-1).kind, 'employeeCode')
  const { source } = fixture()
  source.rows[2].cells[8] = { value: 'plrs  001' }
  source.rows.push({ sourceRow: 6, cells: source.rows[2].cells.map(cell => ({ ...cell })) })
  source.rows.at(-1).cells[8].value = 'PLRS 001'
  const result = buildExcelPayslipRows(source, 3, source.columns)
  assert.equal(result.issues.filter(issue => /repeated/.test(issue.message)).length, 2)
})

test('invalid and renamed metadata fail clearly instead of changing client or salary mapping silently', () => {
  assert.throws(() => restorePayslipTemplateMetadata([metadataSheet({ version: 1, clientId: -1 })]), /metadata is invalid/)
  const saved = { version: 1, clientId: 20, sourceBatchId: 'batch', sheetName: 'Salary', month: '2026-09', headerRow: 1, headers: [], columns: [] }
  assert.throws(() => restorePayslipTemplateMetadata([metadataSheet(saved)]), /renamed or removed/)
})
