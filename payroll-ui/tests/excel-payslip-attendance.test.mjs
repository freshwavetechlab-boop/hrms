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
const { readPayslipAttendance, payslipAttendanceTemplate, payslipAttendanceBasis } = load('excelPayslipAttendance')
const cells = values => values.map(value => ({ value: String(value) }))
function fixture() {
  const batch = { id: 'batch-1', rows: Array.from({ length: 24 }, (_, i) => ({ id: `row-${i + 2}`, sourceRow: i + 2, employeeName: `Employee ${i + 1}`, employeeCode: `TEST${i + 1}`, information: [{ label: 'Work location', value: 'Pune' }] })) }
  const sheet = { name: 'Attendance', rows: payslipAttendanceTemplate(batch, '2026-08', Object.fromEntries(batch.rows.map((row, i) => [row.id, i >= 14 && i <= 16 ? null : i === 0 ? 0 : i === 1 ? 22.5 : 26]))).map((row, i) => ({ sourceRow: i + 1, cells: cells(row) })) }
  return { batch, sheet }
}

test('21 filled rows import while the three blank Days stay pending; legacy six-column templates work', () => {
  const { batch, sheet } = fixture(), before = JSON.stringify({ batch, sheet })
  sheet.rows.forEach(row => row.cells.pop())
  const result = readPayslipAttendance([sheet], batch, '2026-08')
  assert.equal(result.entered, 21)
  assert.deepEqual(result.blankRows, [16, 17, 18])
  assert.equal(result.values['row-2'], 0)
  assert.equal(result.values['row-3'], 22.5)
  assert.equal(result.values['row-16'], null)
  assert.equal(Object.keys(result.values).length, 24)
  assert.equal(JSON.stringify(batch), JSON.stringify(JSON.parse(before).batch))
})

test('bad nonblank attendance, identity changes, duplicate rows and wrong batch reject before any result', () => {
  for (const [mutate, message] of [
    [sheet => sheet.rows[24].cells[5].value = '32', /Row 25: enter days/],
    [sheet => sheet.rows[24].cells[5].value = 'abc', /Row 25: enter days/],
    [sheet => sheet.rows[24].cells[5].error = '#VALUE!', /Excel error/],
    [sheet => sheet.rows[24].cells[0].value = 'other', /wrong batch/],
    [sheet => sheet.rows[24].cells[3].value = 'Replacement employee', /Employee does not match/],
    [sheet => sheet.rows[24].cells[4].value = 'Mumbai', /Location does not match/],
    [sheet => sheet.rows.push(structuredClone(sheet.rows[1])), /duplicate attendance/],
  ]) {
    const { batch, sheet } = fixture(); mutate(sheet)
    assert.throws(() => readPayslipAttendance([sheet], batch, '2026-08'), message)
  }
})

test('month-specific template rejects another salary month, but selects the attendance worksheet behind a cover sheet', () => {
  const { batch, sheet } = fixture()
  sheet.rows[0].cells[1].value = ' Source   ROW '
  assert.equal(readPayslipAttendance([{ name: 'Cover', rows: [{ sourceRow: 1, cells: cells(['Instructions']) }] }, sheet], batch, '2026-08').entered, 21)
  assert.throws(() => readPayslipAttendance([sheet], batch, '2026-07'), /not for 2026-07/)
  assert.throws(() => readPayslipAttendance([sheet, sheet], batch, '2026-08'), /one attendance sheet/)
  sheet.rows[0].cells.push({ value: 'Days' })
  assert.throws(() => readPayslipAttendance([sheet], batch, '2026-08'), /duplicate Days/)
})

test('partial upload retains the distinction between absent rows and explicit blank Days; exports every saved employee', () => {
  const { batch, sheet } = fixture()
  sheet.rows = [sheet.rows[0], sheet.rows[15]]
  assert.deepEqual(readPayslipAttendance([sheet], batch, '2026-08').values, { 'row-16': null })
  const rows = payslipAttendanceTemplate(batch, '2026-08', { 'row-2': 0, 'row-3': 22.5 })
  assert.equal(rows.length, 25)
  assert.equal(rows[1][5], '0'); assert.equal(rows[2][5], '22.5'); assert.equal(rows[3][5], '')
  assert.ok(rows.slice(1).every(row => row[6] === '2026-08'))
})

test('infers only attendance proration divisors, preserving a fixed 26-day basis instead of changing it to 31', () => {
  const source = { headerRow: 1, rows: [{ sourceRow: 1, cells: cells(['30']) }, { sourceRow: 2, cells: [{ value: '', formulaText: 'H2/26*M2' }, { value: '', formulaText: 'IF(H2>15000,H2*12%,1800)/26*14+$A$1' }] }] }
  assert.deepEqual(payslipAttendanceBasis(source, 12), { cell: '', fixedDays: 26 })
  assert.deepEqual(payslipAttendanceBasis(source, 11), { cell: '', fixedDays: undefined })
  source.rows[1].cells[0].formulaText = 'H2/$A$1*M2'
  assert.deepEqual(payslipAttendanceBasis(source, 12), { cell: 'A1', fixedDays: undefined })
  source.monthDaysCell = 'B1'
  assert.equal(payslipAttendanceBasis(source, 12).cell, 'B1')
})
