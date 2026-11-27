import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import * as jsx from 'react/jsx-runtime'
import ts from 'typescript'

const load = (path, imports = {}) => {
  const exports = {}
  const source = readFileSync(new URL(path, import.meta.url), 'utf8')
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX } }).outputText
  new Function('exports', 'require', compiled)(exports, name => {
    assert.ok(name in imports, name)
    return imports[name]
  })
  return exports
}
const amounts = load('../src/utils/employeeCtc.ts')
const mapping = load('../src/utils/smartBulkImport.ts', { './employeeCtc': amounts, './xlsx': {} })
const hints = load('../src/utils/employeePreviewCodes.ts', { './smartBulkImport': mapping })
const suggestion = (id, code = `CURRENT${id}`) => ({ employeeId: id, employeeCode: code, employeeName: `Synthetic Employee ${id}`,
  mobile: '******0042', bankAccountNo: '******0068', matchReasons: ['Same full name; review the identifiers.'] })
const review = (rowNumber, codeSuggestions) => ({ rowNumber, sheet: 'Employees', matchStatus: 'ProbableDuplicate',
  blockingReasons: ['Confirm identity before choosing an employee.'], candidateEmployees: [], codeSuggestions })
const sheet = rows => ({ name: 'Employees', headers: ['Employee Code', 'First Name', 'Last Name', 'Mobile', 'Basic [SALARY_INPUT:12]'], rows })

test('blank code hints preserve the uploaded values and require the same client, sheet, headers and row identity', () => {
  const source = sheet([['SUPPLIED1', 'Existing', 'One', '9000000000', '0'], ['', 'Synthetic', 'Two', '9000000001', '1234.56'], ['', 'Synthetic', 'Three', '', '0']])
  const before = structuredClone(source)
  const options = [suggestion(20), suggestion(21)]
  const snapshot = hints.captureEmployeeCodeHints(70, source, [review(3, options), review(2, [suggestion(10)])])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, source, 0), [])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, source, 1), options)
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, source, 2), [])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 71, source, 1), [])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, { ...source, name: 'Other sheet' }, 1), [])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, { ...source, headers: ['Employee Code', 'Name'] }, 1), [])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, structuredClone(source), 1), [], 'a new workbook must not inherit old identity hints')
  assert.deepEqual(hints.employeeCodeHintsForRow(null, 70, source, 1), [])
  assert.deepEqual(source, before)
})

test('a changed row loses its hints while unrelated edited or filled rows preserve the untouched candidate row', () => {
  const source = sheet([['', 'First', 'Person', '', '0'], ['', 'Second', 'Person', '', '2000']])
  const option = suggestion(20)
  const snapshot = hints.captureEmployeeCodeHints(70, source, [review(2, [suggestion(10)]), review(3, [option])])
  const unrelatedEdit = { ...source, rows: [['CURRENT10', ...source.rows[0].slice(1)], source.rows[1]] }
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, unrelatedEdit, 1), [option])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, unrelatedEdit, 0), [])
  const identityEdit = { ...source, rows: [source.rows[0], ['', 'Another', 'Person', '9999999999', '2000']] }
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, identityEdit, 1), [])
  const reordered = { ...source, rows: [source.rows[1], source.rows[0]] }
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, reordered, 0), [option], 'a suggestion follows its source row instead of a previous row index')
})

test('display limits hints to three distinct valid employee targets without changing authoritative identity results', () => {
  const source = sheet([['', 'Synthetic', 'Person', '', '0']])
  const returned = [suggestion(0), suggestion(-2), suggestion(10, '  '), suggestion(11), suggestion(11), suggestion(12), suggestion(13), suggestion(14)]
  const row = review(2, returned)
  const before = structuredClone(row)
  const snapshot = hints.captureEmployeeCodeHints(70, source, [row])
  assert.deepEqual(hints.employeeCodeHintsForRow(snapshot, 70, source, 0).map(candidate => candidate.employeeId), [11, 12, 13])
  for (const mode of ['update', 'upsert']) {
    const plan = hints.resolveDraftEmployeeCodes(source, [row], mode, 'CURRENT', ['CURRENT99'])
    assert.equal(plan.rows[0][0], '', 'a name-only display hint cannot fill a code automatically')
    assert.equal(plan.generated, 0)
    assert.deepEqual(plan.unresolved, [2])
  }
  assert.deepEqual(row, before)
})

test('manual candidate use detects same-code collisions across draft rows and other sheets without treating itself as a duplicate', () => {
  const rows = [[' Current10 ', 'Synthetic'], ['', 'Other'], ['CURRENT20', 'Third']]
  assert.equal(hints.employeeCodeSuggestionUsed('current10', rows, 0, 1, []), true)
  assert.equal(hints.employeeCodeSuggestionUsed('CURRENT10', rows, 0, 0, []), false)
  assert.equal(hints.employeeCodeSuggestionUsed('CURRENT20', rows, 0, 1, []), true)
  assert.equal(hints.employeeCodeSuggestionUsed('CURRENT30', rows, 0, 1, [' current30 ']), true)
  assert.equal(hints.employeeCodeSuggestionUsed('CURRENT40', rows, 0, 1, ['CURRENT30']), false)
})

function resolver(source, preflight) {
  const slots = [], effects = []
  let cursor = 0, tree
  const react = {
    useState(initial) {
      const index = cursor++
      if (!(index in slots)) slots[index] = typeof initial === 'function' ? initial() : initial
      return [slots[index], next => { slots[index] = typeof next === 'function' ? next(slots[index]) : next }]
    },
    useRef(initial) {
      const index = cursor++
      if (!(index in slots)) slots[index] = { current: initial }
      return slots[index]
    },
    useEffect(effect, dependencies) {
      const index = cursor++
      if (!(index in slots)) { slots[index] = dependencies; effects.push(effect) }
    },
  }
  const component = load('../src/components/EmployeePreviewCodes.tsx', {
    react, 'react/jsx-runtime': jsx, antd: { Alert: 'alert', Button: 'button', Input: 'input', Space: 'space' },
    '../services/settingsService': { preflightEmployeeImport: preflight },
    '../utils/employeePreviewCodes': hints, '../utils/smartBulkImport': mapping,
    './EmployeePreviewCodes.css': {},
    '../utils/xlsx': { buildXlsxBlob: () => new Blob(['synthetic']) },
  })
  const applied = [], returnedHints = []
  const props = { sheet: source, clientId: 70, clientCode: 'CURRENT', reserved: [], operation: 'update',
    onApply: (...args) => applied.push(args), onHints: snapshot => returnedHints.push(snapshot) }
  const render = () => {
    cursor = 0
    tree = component.default(props)
    effects.splice(0).forEach(effect => effect())
  }
  const find = node => {
    if (Array.isArray(node)) return node.map(find).find(Boolean)
    if (!node?.props) return undefined
    if (node.props.children === 'Resolve missing codes') return node.props
    return find(node.props.children) || find(node.props.description)
  }
  render()
  return { props, applied, returnedHints, render, resolve: () => find(tree).onClick() }
}

test('missing-code UI returns display hints for the current source without automatically choosing a name hint', async () => {
  const source = sheet([['', 'Synthetic', 'Person', '9000000000', '0']])
  const expected = suggestion(10)
  const ui = resolver(source, async () => ({ ok: true, data: { rows: [review(2, [expected])] } }))
  ui.resolve()
  await Promise.resolve()
  assert.equal(ui.returnedHints.length, 1)
  assert.deepEqual(hints.employeeCodeHintsForRow(ui.returnedHints[0], 70, source, 0), [expected])
  assert.deepEqual(ui.applied, [])
  assert.equal(source.rows[0][0], '')
})

test('pending missing-code lookups cannot expose hints or fill codes after client, worksheet, header or operation changes', async () => {
  for (const changeScope of [
    props => { props.clientId = 71 },
    props => { props.sheet = { ...props.sheet, name: 'Another sheet' } },
    props => { props.sheet = { ...props.sheet, headers: [...props.sheet.headers] } },
    props => { props.sheet = { ...props.sheet, rows: props.sheet.rows.map(row => [...row]) } },
    props => { props.operation = 'upsert' },
  ]) {
    const source = sheet([['', 'Synthetic', 'Person', '9000000000', '0']])
    let complete
    const ui = resolver(source, () => new Promise(resolve => { complete = resolve }))
    ui.resolve()
    changeScope(ui.props)
    ui.render()
    complete({ ok: true, data: { rows: [{ ...review(2, [suggestion(10)]), matchStatus: 'MatchedByIdentifiers',
      matchedEmployeeId: 10, matchedEmployeeCode: 'CURRENT10', blockingReasons: [] }] } })
    await Promise.resolve()
    assert.deepEqual(ui.returnedHints, [])
    assert.deepEqual(ui.applied, [])
  }
})

test('candidate cards show the code, name, masked comparison details and reasons; choosing remains explicit and collision guarded', () => {
  const display = load('../src/components/EmployeePreviewCodes.tsx', {
    react: {}, 'react/jsx-runtime': jsx, antd: { Button: 'button' },
    '../services/settingsService': {}, '../utils/employeePreviewCodes': hints,
    '../utils/smartBulkImport': mapping, '../utils/xlsx': {}, './EmployeePreviewCodes.css': {},
  }).EmployeePreviewCodeCandidates
  const chosen = [], used = new Set(['CURRENT11'])
  const candidates = [suggestion(10), suggestion(11)]
  const props = { candidates, rowNumber: 44, isUsed: code => used.has(code), onSelect: code => chosen.push(code) }
  const tree = display(props)
  function collect(node, match) {
    if (Array.isArray(node)) return node.flatMap(item => collect(item, match))
    if (typeof node === 'string') return match === 'text' ? [node] : []
    if (!node?.props) return []
    return [...(match !== 'text' && node.props['data-testid'] ? [node.props] : []), ...collect(node.props.children, match)]
  }
  const text = collect(tree, 'text').join(' ')
  assert.match(text, /CURRENT10/)
  assert.match(text, /Synthetic Employee 10/)
  assert.match(text, /Mobile ·.*\*+0042/)
  assert.match(text, /Account ·.*\*+0068/)
  assert.match(text, /Same full name; review the identifiers/)
  assert.match(text, /Identity review still applies/)
  assert.deepEqual(chosen, [], 'rendering possible employees is not an identity decision')
  const controls = collect(tree, 'controls')
  const available = controls.find(button => button['data-testid'] === 'employee-code-suggestion-44-10')
  const duplicate = controls.find(button => button['data-testid'] === 'employee-code-suggestion-44-11')
  assert.equal(Boolean(available.disabled), false)
  assert.equal(duplicate.disabled, true)
  duplicate.onClick()
  assert.deepEqual(chosen, [])
  available.onClick()
  assert.deepEqual(chosen, ['CURRENT10'])
  used.add('CURRENT10')
  available.onClick()
  assert.deepEqual(chosen, ['CURRENT10'], 'a stale visible button must recheck whether another row now uses the code')
  const disabled = collect(display({ ...props, isUsed: () => false, disabled: true }), 'controls')
  disabled[0].onClick()
  assert.equal(disabled[0].disabled, true)
  assert.deepEqual(chosen, ['CURRENT10'])
  assert.equal(display({ ...props, candidates: [] }), null)
})
