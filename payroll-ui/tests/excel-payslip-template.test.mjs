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
const { buildExcelPayslipTemplate, buildEditablePayslipSheet } = load('excelPayslipTemplate')
const { buildXlsxBlob } = load('xlsx')
const { restorePayslipTemplateMetadata, buildExcelPayslipRows, buildPayslipCalculationSource, defaultPayslipMapping, payslipTemplateMarker, payslipPhoneValue } = load('excelPayslipImport')
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

test('editing saved mapping clones the sheet and restores context, exclusions and permanent employee codes', () => {
  const { source, batch } = fixture(); source.dateSystem = '1904'
  const original = JSON.stringify({ source, batch })
  const sheet = buildEditablePayslipSheet(batch, source)
  assert.equal(sheet.name, 'Salary'); assert.equal(sheet.dateSystem, '1904'); assert.equal(sheet.columnCount, 9)
  assert.deepEqual(sheet.template, { version: 1, clientId: 20, sourceBatchId: batch.id, month: '2026-09', headerRow: 3, columns: source.columns, excludedSourceRows: [7], salaryTemplateId: 'template-1', warnings: [] })
  assert.equal(sheet.rows[2].cells[8].value, 'PLRS001')
  assert.equal(sheet.rows[2].cells[5].formulaText, 'D5/$D$1*E5')
  assert.equal(sheet.rows[2].cells[5].formula, true)
  assert.equal(sheet.rows[2].cells[7].value, '13200')
  const preview = buildExcelPayslipRows(sheet, sheet.template.headerRow, sheet.template.columns, sheet.template.excludedSourceRows)
  assert.deepEqual(preview.issues, []); assert.equal(preview.rows[0].employeeCode, 'PLRS001'); assert.equal(preview.rows[0].netPay, 13200)
  sheet.rows[2].cells[5].value = '5'; sheet.template.columns[5].label = 'Changed'; sheet.template.excludedSourceRows.push(5)
  assert.equal(JSON.stringify({ source, batch }), original, 'editing/cancelling must not mutate the saved snapshot')
})

test('simple salary slip is opt-in and survives edit mapping and workbook export/import', async () => {
  for (const simpleLayout of [undefined, false, true]) {
    const { source, batch } = fixture()
    if (simpleLayout !== undefined) batch.simpleLayout = simpleLayout
    const editable = buildEditablePayslipSheet(batch, source)
    assert.equal(editable.template.simpleLayout === true, simpleLayout === true)
    const { metadata } = await exportParts(batch, source)
    assert.equal(metadata.simpleLayout === true, simpleLayout === true)
    const [restored] = restorePayslipTemplateMetadata([{ ...source, name: source.sheetName }, metadataSheet(metadata)])
    assert.equal(restored.template.simpleLayout === true, simpleLayout === true)
    assert.equal(restored.template.clientId, batch.clientId)
    assert.equal(restored.template.month, batch.month)
    assert.deepEqual(restored.template.columns, source.columns)
    const invalid = { ...metadata, simpleLayout: 'true' }
    assert.throws(() => restorePayslipTemplateMetadata([{ ...source, name: source.sheetName }, metadataSheet(invalid)]), /layout option is invalid/)
  }
})

test('editing ignored phone mapping reuses saved cells without freezing or discarding formula/error provenance', () => {
  const { source, batch } = fixture()
  source.columnCount = 12
  for (const [index, label] of [[9, 'Mobile'], [10, 'External lookup'], [11, 'Missing expression']]) {
    source.columns.push({ columnIndex: index, sourceHeader: label, label, kind: 'ignore' })
    source.rows[1].cells[index] = { value: label }
  }
  source.rows[2].cells[9] = { value: '+919876543210' }
  source.rows[2].cells[10] = { value: '', formulaText: 'Other!A1', error: '#REF!', missingCachedValue: true, calculationError: 'Another worksheet is unavailable.' }
  source.rows[2].cells[11] = { value: '9', formula: true }
  const sheet = buildEditablePayslipSheet(batch, source)
  sheet.template.columns[9].kind = 'info'
  const preview = buildExcelPayslipRows(sheet, sheet.template.headerRow, sheet.template.columns, sheet.template.excludedSourceRows)
  assert.deepEqual(preview.issues, []); assert.equal(payslipPhoneValue(preview.rows[0]), '+919876543210')
  assert.equal(preview.rows[0].netPay, 13200)
  const reconstructed = buildPayslipCalculationSource(sheet, sheet.template.headerRow, sheet.template.columns, sheet.template.excludedSourceRows)
  assert.equal(reconstructed.rows[2].cells[5].formulaText, 'D5/$D$1*E5')
  assert.equal(reconstructed.rows[2].cells[10].formulaText, 'Other!A1')
  assert.equal(reconstructed.rows[2].cells[10].calculationError, 'Another worksheet is unavailable.')
  assert.equal(reconstructed.rows[2].cells[10].error, '#REF!'); assert.equal(reconstructed.rows[2].cells[10].missingCachedValue, true)
  assert.match(reconstructed.rows[2].cells[11].calculationError, /Formula expression is missing/)
  assert.equal(source.columns[9].kind, 'ignore')
})

test('editing and template export share appended code-column preparation without shifting source formulas', async () => {
  const { source, batch } = fixture(); source.columns.pop(); source.columnCount = 8; source.rows.forEach(row => row.cells.splice(8))
  const original = JSON.stringify(source), sheet = buildEditablePayslipSheet(batch, source)
  assert.equal(sheet.columnCount, 9); assert.equal(sheet.template.columns.at(-1).kind, 'employeeCode')
  assert.equal(sheet.rows[1].cells[8].value, 'Employee Code'); assert.equal(sheet.rows[2].cells[8].value, 'PLRS001')
  assert.equal(sheet.rows[2].cells[5].formulaText, 'D5/$D$1*E5'); assert.equal(sheet.rows[2].cells[7].formulaText, 'F5-G5')
  const { metadata } = await exportParts(batch, source)
  assert.deepEqual(metadata.columns, sheet.template.columns)
  assert.equal(JSON.stringify(source), original)
})

test('editable sheet handles sparse/null cells and rejects unusable saved sources with existing limits', () => {
  const { source, batch } = fixture()
  source.rows[0].cells[2] = null; delete source.rows[0].cells[3]
  const sheet = buildEditablePayslipSheet(batch, source)
  assert.deepEqual(sheet.rows[0].cells[2], { value: '' }); assert.deepEqual(sheet.rows[0].cells[3], { value: '' })
  assert.throws(() => buildEditablePayslipSheet(batch, { ...source, rows: [] }), /no saved worksheet/)
  assert.throws(() => buildEditablePayslipSheet(batch, { ...source, headerRow: 2 }), /header is missing/)
  assert.throws(() => buildEditablePayslipSheet(batch, { ...source, columns: [...source.columns, { columnIndex: 9, sourceHeader: 'Other code', label: 'Other code', kind: 'employeeCode' }] }), /ambiguous/)
  assert.throws(() => buildEditablePayslipSheet(batch, { ...source, columnCount: 512, columns: source.columns.filter(column => column.kind !== 'employeeCode') }), /512-column limit/)
})

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

test('phone and contact-number template columns stay text and restore their information mapping', async () => {
  for (const [label, value] of [['Mobile', '09876543210'], ['Contact Number', '+919876543210'], ['Employee Phone No.', '+91 98765 43210']]) {
    const { source, batch } = fixture()
    source.columnCount = 10
    source.columns.push({ ...defaultPayslipMapping([label])[0], columnIndex: 9 })
    source.rows[1].cells.push({ value: label })
    source.rows[2].cells.push({ value })
    const { files, metadata } = await exportParts(batch, source), salary = files.get('xl/worksheets/sheet1.xml')
    assert.ok(salary.includes(`<c r="J5" t="inlineStr"><is><t xml:space="preserve">${value}</t>`), label)
    const sheet = { ...source, name: source.sheetName }
    const [restored] = restorePayslipTemplateMetadata([sheet, { name: metadata.notesSheetName, rows: [], columnCount: 1 }, metadataSheet(metadata)])
    const result = buildExcelPayslipRows(restored, source.headerRow, restored.template.columns, restored.template.excludedSourceRows)
    assert.deepEqual(result.issues, [], label)
    assert.equal(payslipPhoneValue(result.rows[0]), value, label)
    assert.equal(result.rows[0].netPay, 13200, 'phone is never a salary amount')
  }
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
