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
const { fillMissingPayslipLocations } = load('excelPayslipLocation')
const { buildExcelPayslipRows, buildPayslipCalculationSource } = load('excelPayslipImport')
const columns = ['Name', 'Location', 'Employee Code', 'Basic', 'PF', 'Net Pay'].map((sourceHeader, columnIndex) => ({ sourceHeader, label: sourceHeader, columnIndex, kind: ['employeeName', 'info', 'employeeCode', 'earning', 'deduction', 'netPay'][columnIndex] }))
const row = (sourceRow, name, location) => ({ sourceRow, cells: [name, location, `EMP${sourceRow}`, '10000.50', '1200.00', '8800.50'].map(value => ({ value })) })

test('explicit location fills only included literal blanks and survives source/row serialization without changing pay', () => {
  const sheet = { name: 'Salary', columnCount: 6, rows: [row(1, 'Header', 'Location'), row(2, 'First', 'Pune'), row(3, 'Second', ''), row(4, 'Excluded', ''), row(5, 'Total', '')] }
  sheet.rows[2].cells[5].formulaText = 'D3-E3'
  const original = JSON.stringify(sheet), excluded = [4, 5], before = buildExcelPayslipRows(sheet, 1, columns, excluded)
  const result = fillMissingPayslipLocations(sheet, columns, before.rows.map(r => r.sourceRow), ' Mumbai ')
  assert.equal(result.changed, 1)
  assert.equal(JSON.stringify(sheet), original, 'original and cancelling the preview stay unchanged')
  assert.deepEqual(result.sheet.rows.map(r => r.cells[1].value), ['Location', 'Pune', 'Mumbai', '', ''])
  const after = buildExcelPayslipRows(result.sheet, 1, columns, excluded)
  assert.deepEqual(after.issues, before.issues)
  assert.deepEqual(after.rows.map(({ information, ...r }) => r), before.rows.map(({ information, ...r }) => r))
  assert.equal(after.rows[1].information.find(i => i.label === 'Location').value, 'Mumbai')
  const source = buildPayslipCalculationSource(result.sheet, 1, columns, excluded)
  assert.equal(source.rows[2].cells[1].value, 'Mumbai')
  assert.equal(source.rows[2].cells[5].formulaText, 'D3-E3')
  assert.deepEqual(source.excludedSourceRows, excluded)
})

test('empty formula results, missing caches and source errors remain unfilled', () => {
  for (const protectedCell of [{ value: '', formulaText: 'IF(A2="","","Pune")' }, { value: '', formula: true }, { value: '', missingCachedValue: true }, { value: '', error: '#REF!' }, { value: '', calculationError: 'Missing source' }]) {
    const sheet = { name: 'Salary', columnCount: 6, rows: [row(2, 'First', '')] }
    sheet.rows[0].cells[1] = protectedCell
    const result = fillMissingPayslipLocations(sheet, columns, [2], 'Mumbai')
    assert.equal(result.changed, 0); assert.equal(result.sheet, sheet)
  }
})

test('no implicit file-label default, missing/ignored or ambiguous location mapping is accepted', () => {
  const sheet = { name: 'Mumbai', columnCount: 6, rows: [row(2, 'First', '')] }
  assert.throws(() => fillMissingPayslipLocations(sheet, columns, [2], ''), /Enter one work location/)
  assert.throws(() => fillMissingPayslipLocations(sheet, columns.map(c => c.columnIndex === 1 ? { ...c, kind: 'ignore' } : c), [2], 'Mumbai'), /exactly one/)
  assert.throws(() => fillMissingPayslipLocations(sheet, [...columns, { ...columns[1], columnIndex: 6 }], [2], 'Mumbai'), /exactly one/)
})
