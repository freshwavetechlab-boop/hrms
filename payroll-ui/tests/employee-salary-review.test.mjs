import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import * as jsx from 'react/jsx-runtime'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/EmployeeImportReviewModal.tsx', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText

// Drive the drawer's JSX controls and submitted decisions without an API or browser service.
function drawer(rows, extras = {}) {
  const state = [], effects = []
  let cursor, tree, submitted
  const react = {
    useMemo: build => build(),
    useState: initial => {
      const slot = cursor++
      if (!(slot in state)) state[slot] = initial
      return [state[slot], next => { state[slot] = typeof next === 'function' ? next(state[slot]) : next }]
    },
    useEffect: effect => { if (!tree) effects.push(effect) },
  }
  const exports = {}
  new Function('exports', 'require', compiled)(exports, path => {
    if (path === 'react') return react
    if (path === 'react/jsx-runtime') return jsx
    return {}
  })
  const props = { open: true, operation: 'update', mappedSalaryImport: true,
    review: { reviewToken: 'token', totalRows: rows.length, rows, canImport: true }, onConfirm: decisions => { submitted = decisions }, ...extras }
  const render = () => { cursor = 0; tree = exports.default(props) }
  render()
  effects.forEach(run => run())
  render()
  function find(matches, node = tree) {
    if (Array.isArray(node)) return node.map(item => find(matches, item)).find(Boolean)
    if (!node?.props) return
    if (matches(node.props)) return node.props
    return find(matches, node.props.children ?? []) || find(matches, node.props.footer ?? [])
  }
  return { render, control: id => find(p => p['data-testid'] === `employee-import-${id}`),
    row: number => find(p => p.row?.rowNumber === number), result: () => submitted }
}

function matched(id, field = 'salaryInput:12') {
  const code = 'EMP' + id, changes = [{ field, payrollImpact: true }]
  return { rowNumber: id, sheet: 'Employees', proposedEmployeeCode: code, matchedEmployeeId: id, matchedEmployeeCode: code,
    changes, matchStatus: 'MatchedByEmployeeCode', candidateEmployees: [{ employeeId: id, employeeCode: code, changes }],
    identityEvidence: [{ field: 'employeeCode', uploadedValue: code, candidates: [{ employeeId: id, employeeCode: code }] }] }
}

test('Select all shows full, partial and empty selection and toggles the actual salary rows', () => {
  const rows = [matched(2), matched(3)], d = drawer(rows)
  const select = () => d.control('select-all')
  const state = () => [select().checked, select().indeterminate, Boolean(select().disabled)]
  assert.deepEqual(state(), [true, false, false])
  select().onChange({ target: { checked: false } }); d.render()
  assert.deepEqual(state(), [false, false, false])
  assert.equal(d.control('accept-salary').disabled, true)
  d.row(2).onSelected(true); d.render()
  assert.deepEqual(state(), [false, true, false])
  select().onChange({ target: { checked: true } }); d.render()
  assert.deepEqual(state(), [true, false, false])
  assert.ok(d.row(2).selected && d.row(3).selected)
  assert.equal(d.control('accept-salary').children.join(''), 'Accept matched salary updates (2)')
  assert.equal(drawer([]).control('select-all').disabled, true)
  assert.equal(drawer(rows, { busy: true }).control('select-all').disabled, true)
})

test('bulk salary action confirms exact checked salary matches and preserves separate decisions', () => {
  const rows = [matched(2), { ...matched(3, 'annualCtc'), matchStatus: 'MatchedByEmployeeId' }, matched(4, 'salaryTemplateId'), matched(5), matched(6)]
  const d = drawer(rows)
  d.row(4).onSelected(false)
  d.row(5).onChoose('skip')
  d.row(6).onFieldChoice('salaryInput:12', 'keepExisting')
  d.render()
  assert.equal(d.control('accept-salary').children.join(''), 'Accept matched salary updates (2)')
  d.control('accept-salary').onClick()
  d.render()
  assert.equal(d.control('accept-salary').disabled, true)
  assert.equal(d.row(4).decision, undefined)
  assert.equal(d.row(5).decision, 'skip')
  assert.deepEqual(d.row(6).fieldChoices, { 'salaryInput:12': 'keepExisting' })
  assert.equal(d.control('review-confirm').disabled, true)
  d.row(4).onChoose('skip')
  d.row(6).onChoose('update')
  d.control('review-acknowledge').onChange({ target: { checked: true } })
  d.render()
  assert.equal(d.control('review-confirm').disabled, false)
  d.control('review-confirm').onClick()
  assert.deepEqual(d.result().slice(0, 2), [
    { rowNumber: 2, sheet: 'Employees', action: 'update', employeeId: 2, fieldChoices: { 'salaryInput:12': 'useImported' } },
    { rowNumber: 3, sheet: 'Employees', action: 'update', employeeId: 3, fieldChoices: { annualCtc: 'useImported' } },
  ])
})

test('salary action excludes profile changes, secondary identity matches and conflicts; other workflows retain their controls', () => {
  const mixed = matched(3)
  mixed.changes.push({ field: 'bankAccountNo', sensitive: true })
  const ambiguous = matched(4)
  ambiguous.candidateEmployees.push({ employeeId: 90, changes: [] })
  const rows = [matched(2, 'salaryTemplateId'), mixed, ambiguous, { ...matched(5), matchStatus: 'ProbableDuplicate' },
    { ...matched(6), blockingReasons: ['Conflicting source data'] }, { ...matched(7), identityEvidence: [] }, matched(8, 'salaryJson')]
  const d = drawer(rows)
  assert.equal(d.control('accept-salary').children.join(''), 'Accept matched salary updates (1)')
  d.control('accept-salary').onClick()
  d.render()
  assert.deepEqual(d.row(2).fieldChoices, { salaryTemplateId: 'useImported' })
  for (const id of [3, 4, 5, 7, 8]) assert.equal(d.row(id).decision, undefined)
  assert.equal(d.row(6).decision, 'skip')
  for (const props of [{ mappedSalaryImport: false }, { operation: 'upsert' }, { operation: 'insert' }]) {
    assert.equal(drawer(rows, props).control('accept-salary'), undefined)
  }
})
