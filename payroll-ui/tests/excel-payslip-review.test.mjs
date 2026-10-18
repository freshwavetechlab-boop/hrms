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
const { correctPayslipAmounts, payslipReconciliation } = load('excelPayslipReview')
const { buildExcelPayslipRows, buildPayslipCalculationSource } = load('excelPayslipImport')
function fixture() {
  const headers = ['Employee Name', 'Account No', 'Basic', 'PF', 'Net Pay', 'Ignored raw']
  const columns = headers.map((label, columnIndex) => ({ columnIndex, sourceHeader: label, label, kind: ['employeeName', 'info', 'earning', 'deduction', 'netPay', 'ignore'][columnIndex] }))
  const sheet = { name: 'Salary', columnCount: 6, rows: [
    { sourceRow: 1, cells: headers.map(value => ({ value })) },
    { sourceRow: 2, cells: [{ value: 'Person' }, { value: '000123456' }, { value: '1000.25', formula: true, formulaText: 'G2*30' }, { value: '120' }, { value: '880', formula: true, formulaText: 'ROUND(C2-D2,0)' }, { value: '#REF!', error: '#REF!' }] },
    { sourceRow: 3, cells: [{ value: 'Pending person' }, { value: '' }, { value: '500' }, { value: '0' }, { value: '500' }, { value: '' }] }
  ], template: { version: 1, clientId: 42, sourceBatchId: 'previous', month: '2026-09', headerRow: 1, columns, excludedSourceRows: [3], warnings: [] } }
  return { sheet, columns }
}
test('warning details expose the actual difference without rewriting a rounded source net', () => {
  const { sheet, columns } = fixture(), row = buildExcelPayslipRows(sheet, 1, columns, [3]).rows[0], before = structuredClone(row)
  const result = payslipReconciliation(row)
  assert.equal(result.earnings, 1000.25); assert.equal(result.deductions, 120)
  assert.equal(result.calculatedNet, 880.25); assert.equal(result.difference, .25); assert.equal(result.wholeRupeeMatch, true)
  assert.deepEqual(row, before)
  row.netPay = 800; row.declaredGross = 1100; row.declaredDeductions = 100
  const wrong = payslipReconciliation(row)
  assert.equal(wrong.wholeRupeeMatch, false); assert.equal(wrong.grossDifference, -99.75); assert.equal(wrong.deductionDifference, 20)
})
test('manual correction changes only the requested source cell and keeps the previous snapshot and other formulas intact', () => {
  const { sheet, columns } = fixture(), before = structuredClone(sheet)
  const result = correctPayslipAmounts(sheet, 2, columns, { 4: '880.25' })
  assert.equal(result.changed, 1); assert.deepEqual(sheet, before)
  assert.equal(result.sheet.rows[1].cells[4].value, '880.25')
  assert.equal(result.sheet.rows[1].cells[4].formulaText, 'ROUND(C2-D2,0)')
  assert.match(result.sheet.rows[1].cells[4].calculationError, /manually corrected.*reviewed salary formula/)
  assert.deepEqual(result.sheet.rows[1].cells[2], before.rows[1].cells[2])
  assert.equal(result.sheet.rows[1].cells[1].value, '000123456')
  assert.deepEqual(result.sheet.rows[2], before.rows[2]); assert.deepEqual(result.sheet.template, before.template)
  const imported = buildExcelPayslipRows(result.sheet, 1, columns, [3])
  assert.deepEqual(imported.issues, []); assert.equal(imported.rows.length, 1)
  assert.equal(imported.rows[0].netPay, 880.25); assert.equal(payslipReconciliation(imported.rows[0]).difference, 0)
  const source = buildPayslipCalculationSource(result.sheet, 1, columns, [3])
  assert.equal(source.rows[1].cells[4].value, '880.25'); assert.equal(source.rows[1].cells[4].formulaText, 'ROUND(C2-D2,0)')
  assert.match(source.rows[1].cells[4].calculationError, /manually corrected/); assert.equal(source.rows[1].cells[2].formulaText, 'G2*30')
  assert.deepEqual(source.rows[1].cells[5], { value: '#REF!', error: '#REF!' })
})
test('unchanged amounts preserve saved formulas and zero corrections are explicit, never inferred from blank input', () => {
  const { sheet, columns } = fixture()
  assert.equal(correctPayslipAmounts(sheet, 2, columns, { 4: '880.00' }).sheet, sheet)
  assert.deepEqual(correctPayslipAmounts(sheet, 2, columns, { 3: '0' }).sheet.rows[1].cells[3], { value: '0' })
  for (const value of [null, '', '#REF!', 'NaN', 'Infinity', '10000000001']) assert.throws(() => correctPayslipAmounts(sheet, 2, columns, { 3: value }), /valid amount/)
  assert.throws(() => correctPayslipAmounts(sheet, 2, columns, { 1: '555' }), /Only mapped salary/)
  assert.throws(() => correctPayslipAmounts(sheet, 2, columns, { 99: '555' }), /Only mapped salary/)
  for (const row of [1, 3, 99]) assert.throws(() => correctPayslipAmounts(sheet, row, columns, { 3: '0' }), /included employee/)
})
