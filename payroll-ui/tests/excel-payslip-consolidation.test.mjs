import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const compile = name => ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const dataUrl = code => 'data:text/javascript;base64,' + Buffer.from(code).toString('base64')
const importsUrl = dataUrl(compile('excelPayslipImport'))
const { buildExcelPayslipRows, isVerifiedPayslipLocationSplit } = await import(importsUrl)
const { consolidatePayslipWorkbooks, suggestPayslipWorksheet, reviewPayslipTableBoundary } = await import(dataUrl(compile('excelPayslipConsolidation').replace("'./excelPayslipImport'", JSON.stringify(importsUrl))))
const headers = ['Employee Name', 'Attendance', 'Rate', 'Basic', 'PF', 'Net Pay', 'UAN']
const kinds = ['employeeName', 'info', 'ignore', 'earning', 'deduction', 'netPay', 'info']

test('transport salary table headers are selected after a merged title instead of sending the title to AI', () => {
  const sheet = { name: 'billing', columnCount: 5, rows: [
    { sourceRow: 1, cells: [{ value: 'TMT SALARY SHEET SEP' }] },
    { sourceRow: 2, cells: ['ID NO', 'CONDUCTOR NAME', 'Basic', 'EPF 12%', 'net paid'].map(value => ({ value })) },
    { sourceRow: 3, cells: ['888', 'Example Employee', '10000', '1200', '8800'].map(value => ({ value })) },
  ] }
  const selected = suggestPayslipWorksheet([sheet])
  assert.equal(selected.sheet, sheet)
  assert.equal(selected.headerRow, 2)
})
function part(fileName, name, headerRow = 1) {
  return { fileName, headerRow, columns: headers.map((sourceHeader, columnIndex) => ({ columnIndex, sourceHeader, label: sourceHeader, kind: kinds[columnIndex] })), sheet: { name: 'Salary', columnCount: headers.length, rows: [
    { sourceRow: headerRow, cells: headers.map(value => ({ value })) },
    { sourceRow: headerRow + 1, cells: [{ value: name }, { value: '15' }, { value: '100' }, { value: '1500', formula: true, formulaText: `B${headerRow + 1}*$C$${headerRow + 1}` }, { value: '180' }, { value: '1320', formula: true, formulaText: `D${headerRow + 1}-E${headerRow + 1}` }, { value: '101123456789' }] }
  ] } }
}
test('combines different header rows without editing source; moves absolute and relative references with each raw block', () => {
  const inputs = [part('One.xlsx', 'One'), part('Two.xlsx', 'Two', 4)], before = structuredClone(inputs)
  const combined = consolidatePayslipWorkbooks(inputs, 42, '2026-09'), result = buildExcelPayslipRows(combined, 1, combined.template.columns, combined.template.excludedSourceRows)
  assert.deepEqual(inputs, before); assert.deepEqual(result.issues, [])
  assert.deepEqual(result.rows.map(r => r.netPay), [1320, 1320])
  for (const r of result.rows) {
    const raw = combined.rows.find(row => row.sourceRow === r.sourceRow)
    assert.equal(raw.cells[3].formulaText, `B${r.sourceRow}*$C$${r.sourceRow}`)
    assert.equal(raw.cells[5].formulaText, `D${r.sourceRow}-E${r.sourceRow}`)
    assert.equal(r.information.find(i => i.label === 'Source Excel row').value, r.employeeName === 'One' ? '2' : '5')
    const attendance = combined.template.columns.find(c => c.label === 'Attendance')
    assert.equal(raw.cells[1].formulaText.endsWith(String(r.sourceRow)), true)
    assert.equal(raw.cells[attendance.columnIndex].formulaText, undefined)
    assert.equal(raw.cells[attendance.columnIndex].value, '15')
  }
})
test('blank amounts require each source approval; absent components are zero without changing present blanks', () => {
  const a = part('A.xlsx', 'One'), b = part('B.xlsx', 'Two')
  a.columns[4].blankAsZero = true; a.sheet.rows[1].cells[4] = { value: '' }; b.sheet.rows[1].cells[4] = { value: '' }
  let sheet = consolidatePayslipWorkbooks([a, b], 42, '2026-09')
  let result = buildExcelPayslipRows(sheet, 1, sheet.template.columns, sheet.template.excludedSourceRows)
  assert.equal(result.issues.length, 1)
  b.columns[4].kind = 'ignore'
  sheet = consolidatePayslipWorkbooks([a, b], 42, '2026-09'); result = buildExcelPayslipRows(sheet, 1, sheet.template.columns, sheet.template.excludedSourceRows)
  assert.deepEqual(result.issues, []); assert.deepEqual(result.rows.map(r => r.deductions[0].amount), [0, 0])
})
test('keeps financial errors blocking, optional errors visible in review warning and originals retained', () => {
  const a = part('A.xlsx', 'One'), b = part('B.xlsx', 'Two')
  a.sheet.rows[1].cells[6] = { value: '#N/A', error: '#N/A' }
  b.sheet.rows[1].cells[5] = { value: '#REF!', error: '#REF!', formulaText: '#REF!' }
  const sheet = consolidatePayslipWorkbooks([a, b], 42, '2026-09'), result = buildExcelPayslipRows(sheet, 1, sheet.template.columns, sheet.template.excludedSourceRows)
  assert.ok(result.issues.some(i => i.message.includes('#REF!')))
  assert.ok(sheet.template.warnings.some(w => w.includes('1 information/contact cells')))
  assert.equal(sheet.rows.find(r => r.cells[0].value === 'One').cells[6].value, '#N/A')
  b.excludedRows = [2]
  const held = consolidatePayslipWorkbooks([a, b], 42, '2026-09'), reviewed = buildExcelPayslipRows(held, 1, held.template.columns, held.template.excludedSourceRows)
  assert.deepEqual(reviewed.issues, []); assert.equal(reviewed.rows.length, 1)
  const nameCol = held.template.columns.find(c => c.kind === 'employeeName').columnIndex
  assert.ok(held.rows.some(r => held.template.excludedSourceRows.includes(r.sourceRow) && r.cells[nameCol].value === 'Two'), 'pending identity stays in saved source so it cannot imply resignation')
})
test('requires one client and month, all sources with a net column, and unique mapped labels', () => {
  const a = part('A.xlsx', 'One'), b = part('B.xlsx', 'Two')
  assert.throws(() => consolidatePayslipWorkbooks([a, b], 0, '2026-09'), /client/)
  assert.throws(() => consolidatePayslipWorkbooks([a, b], 42, '2026-13'), /month/)
  b.sheet.template = { clientId: 7 }
  assert.throws(() => consolidatePayslipWorkbooks([a, b], 42, '2026-09'), /another client/)
  delete b.sheet.template; b.columns[5].kind = 'ignore'
  assert.throws(() => consolidatePayslipWorkbooks([a, b], 42, '2026-09'), /net pay/)
})
test('finds salary sheet after title rows instead of selecting an invoice tab', () => {
  const salary = part('Source.xlsx', 'One', 5).sheet
  const picked = suggestPayslipWorksheet([{ name: 'Invoice', columnCount: 2, rows: [{ sourceRow: 1, cells: [{ value: 'Invoice total' }, { value: '10000' }] }] }, salary])
  assert.equal(picked.sheet, salary); assert.equal(picked.headerRow, 5)
})
test('a closing net total separates copied reference rows; cached values alone and partial totals never infer a boundary', () => {
  const a = part('A.xlsx', 'Current'), b = part('B.xlsx', 'Other')
  a.sheet.rows.push({ sourceRow: 3, cells: Array.from({ length: 7 }, (_, i) => i === 5 ? { value: '1320', formula: true, formulaText: 'SUM(F2:F2)' } : { value: '' }) }, { sourceRow: 8, cells: [{ value: 'Old reference' }, { value: '31' }, { value: '100' }, { value: '#REF!', error: '#REF!' }, { value: '0' }, { value: '#REF!', error: '#REF!' }, { value: '101123456789' }] })
  assert.deepEqual(reviewPayslipTableBoundary(a), { totalRow: 3, referenceRows: [8] })
  const combined = consolidatePayslipWorkbooks([a, b], 42, '2026-09'), result = buildExcelPayslipRows(combined, 1, combined.template.columns, combined.template.excludedSourceRows)
  assert.deepEqual(result.issues, []); assert.deepEqual(result.rows.map(r => r.employeeName), ['Current', 'Other'])
  assert.ok(combined.template.warnings.some(w => w.includes('closing net-pay total')))
  a.sheet.rows[2].cells[5].formulaText = 'SUM(F1:F2)'
  assert.equal(reviewPayslipTableBoundary(a), null)
  delete a.sheet.rows[2].cells[5].formulaText
  assert.equal(reviewPayslipTableBoundary(a), null)
})
test('reviewed source units support copied work locations without weakening identity or month attendance checks', () => {
  const a = part('Region A.xlsx', 'Same person'), b = part('Region B.xlsx', 'Same person')
  for (const p of [a, b]) {
    p.columns.push({ columnIndex: 7, sourceHeader: 'Work location', label: 'Work location', kind: 'info' })
    p.sheet.columnCount = 8; p.sheet.rows[0].cells.push({ value: 'Work location' }); p.sheet.rows[1].cells.push({ value: 'Old location' })
  }
  const parse = approved => { const s = consolidatePayslipWorkbooks([a, b], 42, '2026-09', approved); return buildExcelPayslipRows(s, 1, s.template.columns, s.template.excludedSourceRows).rows }
  assert.equal(isVerifiedPayslipLocationSplit(parse(false), '2026-09'), false)
  const rows = parse(true)
  assert.equal(isVerifiedPayslipLocationSplit(rows, '2026-09'), true)
  rows[1].information.find(i => i.label === 'Attendance').value = '16'
  assert.equal(isVerifiedPayslipLocationSplit(rows, '2026-09'), false)
  rows[1].information.find(i => i.label === 'Attendance').value = '15'
  rows[1].information.find(i => i.label === 'UAN').value = '101123456780'
  assert.equal(isVerifiedPayslipLocationSplit(rows, '2026-09'), false)
})
