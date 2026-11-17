import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const api = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/excelPayslipHistoryScope.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText)(api)
const { savedPayslipHistoryScope: scope, savedPayslipLocationKey: locationKey, savedPayslipMonthLabel: monthLabel } = api
const batch = (id, clientId, month, locationLabel) => Object.freeze({ id, clientId, month, locationLabel, sourceFileName: 'Original workbook.xlsx', rows: [{ netPay: 123.456789, formula: 'ROUND(A2/B2*C2,2)' }] })
const history = Object.freeze([
  batch('oct-delhi-v2', 10, '2026-10', 'Delhi'), batch('oct-delhi-v1', 10, '2026-10', 'Delhi'),
  batch('oct-legacy', 10, '2026-10'), batch('sep-raipur', 10, '2026-09', 'Raipur'),
  batch('sep-delhi', 10, '2026-09', 'Delhi'), batch('other-client', 20, '2026-11', 'Delhi'),
])

test('client → latest available salary month → locations retains every saved version without changing salary/formulas', () => {
  const before = JSON.stringify(history)
  const result = scope(history, 10, '', '')
  assert.deepEqual(result.months, ['2026-10', '2026-09'])
  assert.equal(result.month, '2026-10')
  assert.deepEqual(result.locations.map(option => option.value).sort(), ['legacy', 'location:Delhi'])
  assert.deepEqual(result.rows.map(row => row.id), ['oct-delhi-v2', 'oct-delhi-v1', 'oct-legacy'])
  assert.equal(JSON.stringify(history), before)
})

test('month then location includes only that month/client; unlabelled legacy batches remain selectable', () => {
  const september = scope(history, 10, '2026-09', 'location:Raipur')
  assert.deepEqual(september.rows.map(row => row.id), ['sep-raipur'])
  assert.equal(september.locations.some(option => option.value === 'legacy'), false)
  assert.deepEqual(scope(history, 10, '2026-10', 'legacy').rows.map(row => row.id), ['oct-legacy'])
  assert.equal(scope(history, 10, '2026-10', 'location:Delhi').rows.length, 2, 'do not drop older saved versions')
})

test('changing client or month discards unavailable preferences, including a previous client active batch', () => {
  const result = scope(history, 20, '2026-09', 'location:Raipur', history[0])
  assert.equal(result.month, '2026-11'); assert.equal(result.location, '')
  assert.deepEqual(result.rows.map(row => row.id), ['other-client'])
  assert.equal(scope(history, 10, '2026-10', 'location:Raipur').location, '')
  assert.deepEqual(scope(history, 30, '2026-10', 'location:Delhi').rows, [])
  assert.deepEqual(scope(history, 30, '', '').months, [])
})

test('opening a linked/saved batch exposes its actual month/location rather than a stale preference or filename', () => {
  const active = { ...history[3], sourceFileName: 'October misleading filename.xlsx' }
  const result = scope(history, 10, '2026-10', 'location:Delhi', active)
  assert.equal(result.month, '2026-09'); assert.equal(result.location, 'location:Raipur')
  assert.deepEqual(result.rows.map(row => row.id), ['sep-raipur'])
  assert.equal(monthLabel(result.month), 'September 2026')
})

test('named locations do not collide with unlabelled legacy scope and valid old plain-label preferences upgrade', () => {
  const rows = [...history, batch('named-legacy', 10, '2026-10', 'legacy')]
  assert.equal(locationKey(rows.at(-1)), 'location:legacy')
  assert.deepEqual(scope(rows, 10, '2026-10', 'location:legacy').rows.map(row => row.id), ['named-legacy'])
  assert.deepEqual(scope(history, 10, '2026-10', 'Delhi').rows.map(row => row.id), ['oct-delhi-v2', 'oct-delhi-v1'])
})
