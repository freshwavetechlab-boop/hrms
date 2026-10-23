import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const cache = new Map()
function load(name) {
  if (cache.has(name)) return cache.get(name)
  const exports = {}; cache.set(name, exports)
  const code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', ''))); return exports
}
const { payslipEmailExport, readPayslipEmailUpdates } = load('excelPayslipEmailUpdates')
const batch = { id: 'saved-batch', month: '2026-09', rows: [
  { id: 'one', sourceRow: 4, employeeCode: '00001', employeeName: 'Same Name', email: '', information: [{ label: 'Work location', value: 'A' }], earnings: [{ amount: 12345.6789 }], deductions: [], netPay: 12345.6789 },
  { id: 'two', sourceRow: 9, employeeCode: '00001', employeeName: 'Same Name', email: 'old@example.test', information: [{ label: 'Work location', value: 'B' }], earnings: [], deductions: [], netPay: 1 },
  { id: 'three', sourceRow: 10, employeeCode: '00002', employeeName: 'Other', email: '', information: [], earnings: [], deductions: [], netPay: 0 },
] }
const sheet = (rows = payslipEmailExport(batch, batch.rows)) => ({ name: 'Records', rows: rows.map((values, i) => ({ sourceRow: i + 1, cells: values.map(value => ({ value: String(value) })) })) })

test('filtered partial export retains codes and exact payslip identity; only changed emails are returned', () => {
  const before = JSON.stringify(batch), rows = payslipEmailExport(batch, batch.rows.slice(0, 2)); rows[1][6] = 'new@example.test'
  const result = readPayslipEmailUpdates([sheet(rows)], batch)
  assert.equal(result.updates.length, 1); assert.equal(result.updates[0].rowId, 'one'); assert.equal(result.unchanged, 1)
  assert.equal(rows[1][3], '00001'); assert.equal(rows[1][11], '12345.6789'); assert.equal(JSON.stringify(batch), before)
})
test('blank cells skip instead of clearing existing contacts, and row order does not affect matching', () => {
  const rows = payslipEmailExport(batch, batch.rows); rows[1][6] = 'first@example.test'; rows[2][6] = ''; rows[3][6] = 'other@example.test'
  const result = readPayslipEmailUpdates([sheet([rows[0], rows[3], rows[2], rows[1]])], batch)
  assert.deepEqual(result.updates.map(r => r.rowId), ['three', 'one']); assert.equal(result.blank, 1)
})
test('wrong batch/month, changed identity, duplicate rows and invalid addresses reject the full upload', () => {
  for (const mutate of [r => r[1][0] = 'other', r => r[1][1] = 'unknown', r => r[1][3] = '2', r => r[1][4] = 'Someone',
    r => r[1][5] = 'B', r => r[1][8] = '2026-08', r => r[1][6] = 'one@example.test and two@example.test', r => r.push([...r[1]])]) {
    const rows = payslipEmailExport(batch, batch.rows); rows[1][6] = 'new@example.test'; mutate(rows)
    assert.throws(() => readPayslipEmailUpdates([sheet(rows)], batch))
  }
})
test('legacy code/location list is supported but names alone and ambiguous transfers cannot match', () => {
  const legacy = [['Employee code', 'Employee', 'Location', 'Email'], ['00001', 'Same Name', 'B', 'new@example.test']]
  assert.equal(readPayslipEmailUpdates([sheet(legacy)], batch).updates[0].rowId, 'two')
  assert.throws(() => readPayslipEmailUpdates([sheet([['Employee', 'Email'], ['Same Name', 'a@example.test']])], batch), /missing/)
  assert.throws(() => readPayslipEmailUpdates([sheet([['Employee code', 'Email'], ['00001', 'a@example.test']])], batch), /multiple/)
})
