import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const cache = new Map()
function load(name) {
  if (cache.has(name)) return cache.get(name)
  const exports = {}; cache.set(name, exports)
  const code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', '')))
  return exports
}
const { parsePayslipUpdateInput: parse, suggestPayslipUpdateMapping: mapping, payslipUpdateTemplate: template, selectedPayslipUpdateRows: selectedRows, unmappedPayslipUpdateColumns: unmapped, updateSelectionKey } = load('excelPayslipUpdates')
const fields = [
  { id: 'source:2', label: 'UAN', type: 'text', destination: 'Batch + Master', editable: true, reason: 'Matched Master identity.' },
  { id: 'source:3', label: 'Payable days', type: 'number', destination: 'Batch only', editable: true, reason: 'Monthly source attendance only.' },
  { id: 'source:4', label: 'Net pay', type: 'number', destination: 'Batch only', editable: false, reason: 'Formula output; change payable days.' },
  { id: 'master:mobile', label: 'Phone', type: 'text', destination: 'Batch + Master', editable: true, reason: '' },
]
const catalog = { clientId: 907, batchId: 'batch-oct', month: '2026-10', fields, blockers: [], rows: [
  { employeeCode: '00001-A', employeeName: 'First', values: { 'source:2': '001234567890', 'source:3': '26.00', 'source:4': '25000', 'master:mobile': '9999999999' } },
  { employeeCode: '00002', employeeName: 'Second', values: { 'source:2': '', 'source:3': '25', 'source:4': '21000', 'master:mobile': '' } },
] }
const sheet = rows => ({ name: 'Updates', columnCount: Math.max(...rows.map(row => row.length)), rows: rows.map((cells, i) => ({ sourceRow: i + 1, cells: cells.map(value => typeof value === 'object' ? value : { value: String(value) }) })) })
const read = (rows, selectedCatalog = catalog, excluded = []) => parse(sheet(rows), 1, 0, mapping(rows[0], fields), selectedCatalog, excluded)

test('exact trimmed case-insensitive code matching preserves leading zeros/hyphens and ignores names', () => {
  const before = JSON.stringify(catalog)
  const result = read([['Employee code', 'Employee name', 'UAN', 'Payable days'], [' 00001-a ', 'Wrong name is not identity', '001234567891', 24]])
  assert.deepEqual(result.rows, [{ employeeCode: '00001-A', values: { 'source:2': '001234567891', 'source:3': '24' } }]); assert.equal(result.issues.length, 0)
  assert.equal(JSON.stringify(catalog), before)
  for (const code of ['1-A', '00001A', '99999']) assert.match(read([['Employee code', 'UAN'], [code, '001234567891']]).issues[0].message, /not in the selected/)
})

test('blank keeps current, zero is an explicit input, unchanged formatted numbers do not create changes', () => {
  const result = read([['Employee code', 'UAN', 'Payable days', 'Net pay'], ['00001-A', '', '26', '25,000.000'], ['00002', '', '0', '21,000']])
  assert.deepEqual(result.rows, [{ employeeCode: '00002', values: { 'source:3': '0' } }]); assert.equal(result.blankCells, 2); assert.equal(result.unchangedCells, 3); assert.equal(result.issues.length, 0)
})

test('upload duplicates, saved ambiguity, wrong month/batch and changed read-only formula results block review', () => {
  assert.match(read([['Employee code', 'UAN'], ['00001-A', '1'], ['00001-a', '2']]).issues[0].message, /more than once/)
  const ambiguous = { ...catalog, rows: [...catalog.rows, { ...catalog.rows[0], employeeCode: '00001-a' }] }
  assert.match(read([['Employee code', 'UAN'], ['00001-A', '1']], ambiguous).issues[0].message, /multiple saved/)
  const wrong = read([['Employee code', 'Batch ID', 'Salary month', 'UAN'], ['00001-A', 'other', '2026-09', '1']])
  assert.equal(wrong.rows.length, 0); assert.equal(wrong.issues.length, 2)
  const formula = read([['Employee code', 'Net pay'], ['00001-A', '25001']])
  assert.equal(formula.rows.length, 0); assert.match(formula.issues[0].message, /Formula output/)
})

test('explicit row exclusion removes only reviewed source row; formula errors never become blank or zero', () => {
  const rows = [['Employee code', 'UAN'], ['00001-A', '001234567891'], ['Total', '1']]
  assert.equal(read(rows).issues.length, 1)
  const allowed = read(rows, catalog, [3]); assert.equal(allowed.rows.length, 1); assert.equal(allowed.issues.length, 0); assert.equal(allowed.excludedRows, 1)
  for (const cell of [{ value: '#VALUE!', error: '#VALUE!' }, { value: '', formula: true, missingCachedValue: true }, { value: '', calculationError: 'Failed' }]) assert.match(read([['Employee code', 'UAN'], ['00001-A', cell]]).issues[0].message, /Excel contains an error/)
})

test('changed numeric formula inputs are blocked rather than silently replacing their formulas with cached values', () => {
  for (const cell of [{ value: '24', formula: true }, { value: '24', formulaText: '=30-6' }]) {
    const result = read([['Employee code', 'Payable days'], ['00001-A', cell]])
    assert.equal(result.rows.length, 0); assert.match(result.issues[0].message, /changed numeric input contains an Excel formula/)
  }
  const unchanged = read([['Employee code', 'Payable days', 'Net pay'], ['00001-A', { value: '26', formulaText: '=30-4' }, { value: '25000', formulaText: '=30000-5000' }]])
  assert.equal(unchanged.rows.length, 0); assert.equal(unchanged.issues.length, 0)
  const profile = read([['Employee code', 'UAN'], ['00001-A', { value: '001234567891', formulaText: '=Other!A1', formula: true }]])
  assert.equal(profile.issues.length, 0); assert.equal(profile.rows[0].values['source:2'], '001234567891')
})

test('unmapped data columns are exposed for explicit confirmation; exported context/reference columns are exempt', () => {
  const input = sheet([['Employee code', 'UAN', 'Unrecognized allowance', 'Batch ID', 'Salary month', 'Employee name (reference)', 'Empty notes', 'Client ID'], ['00001-A', '001234567891', '500', 'batch-oct', '2026-10', 'First', '', '907']])
  assert.deepEqual(unmapped(input, 1, 0, mapping(input.rows[0].cells.map(cell => cell.value), fields)), ['Unrecognized allowance'])
})

test('Client ID/ClientId context blocks a wrong-client upload even when employee codes match', () => {
  for (const header of ['Client ID', 'ClientId']) {
    const wrong = read([['Employee code', 'UAN', header], ['00001-A', '001234567891', '42']])
    assert.equal(wrong.rows.length, 0); assert.match(wrong.issues[0].message, /another client/)
    const right = read([['Employee code', 'UAN', header], ['00001-A', '001234567891', '907']])
    assert.equal(right.rows.length, 1); assert.equal(right.issues.length, 0)
  }
})

test('authoritative stable field IDs resolve duplicate labels; duplicate mapping targets cannot slip through', () => {
  const duplicateLabels = [...fields, { ...fields[0], id: 'master:uan' }]
  assert.deepEqual(mapping(['UAN', '[source:2] UAN', '[master:uan] UAN', 'mobile number'], duplicateLabels), { 1: 'source:2', 2: 'master:uan', 3: 'master:mobile' })
  const input = sheet([['Employee code', 'UAN', 'UAN'], ['00001-A', '1', '2']])
  assert.match(parse(input, 1, 0, { 1: 'source:2', 2: 'source:2' }, catalog).issues[0].message, /only one/)
})

test('template export preserves identifier text and batch/month context; an unchanged round trip produces no patches', () => {
  const workbook = template(catalog), main = workbook[0]
  assert.equal(main.freezeRows, 1); assert.equal(main.freezeColumns, 4)
  const data = main.rows.map(row => row.map(value => typeof value === 'object' ? value.value : value))
  assert.equal(data[1][2], '907'); assert.equal(data[1][3], '00001-A'); assert.equal(data[1][5], '001234567890')
  for (const row of main.rows.slice(1)) for (const cell of row) { assert.equal(cell.type, 'text'); assert.equal(cell.style, 'text') }
  assert.equal(parse(sheet(data), 1, 3, mapping(data[0], fields), catalog).rows.length, 0)
  assert.equal(parse(sheet(data), 1, 3, mapping(data[0], fields), catalog).issues.length, 0)
})

test('re-preview partial selection sends only selected editable inputs, never computed outputs or blocked fields', () => {
  const base = { employeeCode: '00001-A', employeeName: 'First', oldValue: '1', newValue: '0', destination: 'Batch only', canApply: true }
  const preview = { token: 'old', blockers: [], changes: [{ ...base, id: 'days', fieldId: 'source:3' }, { ...base, id: 'uan', fieldId: 'source:2', newValue: '001234567891' }, { ...base, id: 'net', fieldId: 'source:4', canApply: false, computed: true }, { ...base, id: 'blocked', fieldId: 'master:salary', canApply: false }] }
  assert.deepEqual(selectedRows(preview, ['days', 'net', 'blocked']), [{ employeeCode: '00001-A', values: { 'source:3': '0' } }])
  assert.equal(updateSelectionKey(['a', 'b']), updateSelectionKey(['b', 'a'])); assert.notEqual(updateSelectionKey(['a', 'b']), updateSelectionKey(['a']))
})
