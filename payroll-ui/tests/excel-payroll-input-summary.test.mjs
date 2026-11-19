import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const modules = new Map()
function load(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}, code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  modules.set(name, exports)
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', '')))
  return exports
}
const { reviewMissingMonthlyInputs, summarizeMissingMonthlyInputs } = load('excelPayrollSourceInputReview')

function fixture(count = 33) {
  const source = {
    headerRow: 1, columnCount: 15,
    columns: Array.from({ length: 15 }, (_, columnIndex) => ({ columnIndex, sourceHeader: columnIndex === 13 ? 'Monthly rate' : `Column ${columnIndex}`, kind: 'info', label: `Column ${columnIndex}` })),
    rows: Array.from({ length: count }, (_, i) => ({ sourceRow: i + 2, cells: Array.from({ length: 15 }, (_, columnIndex) => columnIndex === 14
      ? { value: '80', formulaText: `ROUND(N${i + 2}/B${i + 2}*C${i + 2},0)` } : { value: columnIndex === 13 ? '100' : columnIndex === 1 ? '30' : '24' }) })),
  }
  const batch = { rows: source.rows.map(row => ({ id: `row-${row.sourceRow}`, sourceRow: row.sourceRow })) }
  const request = { rowIds: batch.rows.map(row => row.id), payrollDaysColumnIndex: 1, payableDaysColumnIndex: 2,
    components: [{ key: 'basic', code: 'BASIC', category: 'Earning', calculationType: 'Formula', expectedColumnIndex: 14, isPeriodAdjustment: false }] }
  return { source, batch, request }
}

test('33 repeated BASIC references show one summary while retaining all row proof and no input mapping', () => {
  const { batch, source, request } = fixture()
  const before = JSON.stringify({ batch, source, request })
  const rows = reviewMissingMonthlyInputs(batch, source, request), originalRows = JSON.stringify(rows)
  const summary = summarizeMissingMonthlyInputs(rows)
  assert.equal(summary.length, 1)
  assert.equal(summary[0].component, 'BASIC')
  assert.equal(summary[0].employees, 33)
  assert.equal(summary[0].outputColumn, 'O')
  assert.equal(summary[0].inputColumns, 'N')
  assert.equal(summary[0].evidence.length, 33)
  assert.equal(summary[0].evidence[0].references, 'N2')
  assert.equal(summary[0].evidence[32].references, 'N34')
  assert.equal(JSON.stringify(rows), originalRows)
  assert.equal(JSON.stringify({ batch, source, request }), before)
  assert.equal(request.components[0].sourceColumnIndex, undefined)
})

test('different input columns, source errors and rounding rules remain separate groups', () => {
  const { batch, source, request } = fixture(4)
  source.rows[1].cells[14].formulaText = 'ROUND(M3/B3*C3,0)'
  source.rows[2].cells[14].missingCachedValue = true
  source.rows[3].cells[14].formulaText = 'N5/B5*C5'
  const rows = reviewMissingMonthlyInputs(batch, source, request), summary = summarizeMissingMonthlyInputs(rows)
  assert.equal(summary.length, 4)
  assert.ok(summary.some(group => group.inputColumns === 'M'))
  assert.equal(summary.filter(group => group.status === 'Source output missing/error').length, 1)
  assert.equal(summary.reduce((total, group) => total + group.evidence.length, 0), rows.length)
  assert.deepEqual(summary.flatMap(group => group.evidence).map(row => row.key).sort(), rows.map(row => row.key).sort())
})

test('protected identity dependencies stay labelled as non-salary evidence', () => {
  const { batch, source, request } = fixture(2)
  source.columns[13].sourceHeader = 'UAN number'
  source.rows[0].cells[13].value = '123456789012'
  source.rows[1].cells[13].value = '987654321012'
  const summary = summarizeMissingMonthlyInputs(reviewMissingMonthlyInputs(batch, source, request))
  assert.equal(summary.length, 1)
  assert.equal(summary[0].employees, 2)
  assert.match(summary[0].inputColumns, /Identity\/contact-like dependency/)
  assert.doesNotMatch(JSON.stringify(summary), /123456789012|987654321012/)
  assert.equal(request.components[0].sourceColumnIndex, undefined)
})

test('external formula dependency stays unresolved and is not a local salary input column', () => {
  const { batch, source, request } = fixture(2)
  for (const row of source.rows) row.cells[14].formulaText = `ROUND('[1]Rates'!N${row.sourceRow}/B${row.sourceRow}*C${row.sourceRow},0)`
  const summary = summarizeMissingMonthlyInputs(reviewMissingMonthlyInputs(batch, source, request))
  assert.equal(summary.length, 1)
  assert.equal(summary[0].inputColumns, 'Captured workbook/dependency mapping required')
  assert.equal(summary[0].employees, 2)
})

test('existing salary basis and empty selection retain existing review eligibility', () => {
  const { batch, source, request } = fixture()
  request.components[0].calculationType = 'Fixed Amount'
  request.components[0].sourceColumnIndex = 13
  assert.deepEqual(reviewMissingMonthlyInputs(batch, source, request), [])
  assert.deepEqual(summarizeMissingMonthlyInputs([]), [])
})

test('same employee evidence is counted once within a group and different components are not merged', () => {
  const { batch, source, request } = fixture(1)
  const first = reviewMissingMonthlyInputs(batch, source, request)[0]
  const summary = summarizeMissingMonthlyInputs([first, { ...first, key: 'second-evidence' }, { ...first, key: 'hra', component: 'HRA' }])
  assert.equal(summary.length, 2)
  assert.equal(summary.find(row => row.component === 'BASIC').employees, 1)
  assert.equal(summary.find(row => row.component === 'BASIC').evidence.length, 2)
  assert.equal(summary.find(row => row.component === 'HRA').employees, 1)
})
