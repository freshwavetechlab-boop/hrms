import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const uiRoot = process.env.FREVO_UI_SOURCE_ROOT || fileURLToPath(new URL('..', import.meta.url))
const require = createRequire(`${uiRoot}/package.json`)
const ts = require('typescript')
const actualReact = require('react')
const jsxRuntime = require('react/jsx-runtime')
const operations = {}
new Function('exports', ts.transpileModule(readFileSync(`${uiRoot}/src/utils/recruitmentRosterOperations.ts`, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(operations)
const { selectedRosterRows, rosterReleaseReady } = operations

// Exercise the real DataTable component and its supplied Ant Table facet
// predicates. The small hook host replaces rendering infrastructure only.
function mountTable(initialProps) {
  const slots = []; let cursor = 0, effects = [], output, props = initialProps
  const sameDeps = (a, b) => !!a && !!b && a.length === b.length && a.every((value, index) => Object.is(value, b[index]))
  const hooks = {
    ...actualReact,
    useState(initial) {
      const index = cursor++
      if (!slots[index]) slots[index] = { value: typeof initial === 'function' ? initial() : initial }
      return [slots[index].value, next => { slots[index].value = typeof next === 'function' ? next(slots[index].value) : next }]
    },
    useMemo(factory, dependencies) {
      const index = cursor++
      if (!slots[index] || !sameDeps(slots[index].dependencies, dependencies)) slots[index] = { value: factory(), dependencies }
      return slots[index].value
    },
    useCallback(callback, dependencies) { return hooks.useMemo(() => callback, dependencies) },
    useEffect(effect, dependencies) {
      const index = cursor++
      if (!slots[index] || !sameDeps(slots[index].dependencies, dependencies)) { slots[index] = { dependencies }; effects.push(effect) }
    },
  }
  const component = name => Object.assign(() => null, { displayName: name })
  const antd = Object.fromEntries(['Button', 'Checkbox', 'Empty', 'Input', 'Pagination', 'Table'].map(name => [name, component(name)]))
  antd.Dropdown = { Button: component('DropdownButton') }
  const dependencies = { react: hooks, 'react/jsx-runtime': jsxRuntime, antd, '@ant-design/icons': new Proxy({}, { get: () => component('Icon') }), jspdf: { jsPDF: class {} }, 'jspdf-autotable': { default: () => {} }, '../utils/xlsx': { downloadXlsx: () => {} } }
  const exported = {}
  const source = readFileSync(process.env.FREVO_DATA_TABLE_SOURCE || `${uiRoot}/src/components/DataTable.tsx`, 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
  new Function('exports', 'require', code)(exported, name => { if (!(name in dependencies)) throw new Error(`Unexpected dependency ${name}`); return dependencies[name] })
  const render = (nextProps = props) => { props = nextProps; cursor = 0; effects = []; output = exported.default(props); for (const effect of effects) effect(); return output }
  const find = (node, predicate) => {
    if (Array.isArray(node)) { for (const child of node) { const match = find(child, predicate); if (match) return match } }
    else if (actualReact.isValidElement(node)) { if (predicate(node)) return node; return find(node.props.children, predicate) }
    return null
  }
  render()
  return { render, table: () => find(output, node => node.type === antd.Table), reset: () => find(output, node => node.type === antd.Button && node.props.children === 'Reset'), export: () => find(output, node => node.type === antd.Dropdown.Button) }
}

const rows = Array.from({ length: 12 }, (_, index) => ({ id: index + 1, candidateName: `Test ${index + 1}`, workLocation: index === 11 ? 'Mumbai' : 'Sikkim', workLocationId: index === 11 ? 111 : 222, sourceGroup: 'Sikkim project', documentIssues: [], revision: `r${index + 1}` }))
const columns = [{ key: 'candidateName', label: 'Employee' }, { key: 'workLocation', label: 'Work location' }, { key: 'sourceGroup', label: 'Project' }]
const applySikkimFacet = host => {
  const table = host.table(), location = table.props.columns.find(column => column.key === 'workLocation')
  const filtered = table.props.dataSource.filter(row => location.onFilter('Sikkim', row))
  table.props.onChange({ current: 1, pageSize: 100 }, { workLocation: ['Sikkim'] }, {}, { currentDataSource: filtered, action: 'filter' })
}

test('native location facet reports eleven Sikkim rows even when search/project results include a Mumbai row', () => {
  const received = [], props = { rows, columns, onVisibleRowsChange: visible => received.push(visible) }
  const host = mountTable(props)
  assert.equal(received.at(-1).length, 12)
  applySikkimFacet(host); host.render()
  const exact = received.at(-1)
  assert.equal(exact.length, 11)
  assert.equal(exact.some(row => row.id === 12), false)
  const selectedIds = rows.map(row => row.id)
  const linkRows = selectedRosterRows(exact, selectedIds)
  assert.deepEqual(linkRows.map(row => row.id), Array.from({ length: 11 }, (_, index) => index + 1))
  assert.equal(selectedRosterRows(exact, selectedIds, true).length, 11)
  assert.equal(linkRows.filter(rosterReleaseReady).length, 11)
  host.table().props.onChange({ current: 2, pageSize: 10 }, { workLocation: ['Sikkim'] }, {}, { currentDataSource: exact, action: 'paginate' })
  host.render()
  assert.equal(received.at(-1).length, 11, 'bulk actions cover all filtered rows rather than only the current page')
})

test('filtered callback stays restricted after parent rerender and refreshed rows, then reset restores all rows', () => {
  const received = [], callback = visible => received.push(visible), props = { rows, columns, onVisibleRowsChange: callback }
  const host = mountTable(props)
  applySikkimFacet(host); host.render()
  host.render({ ...props })
  assert.equal(received.at(-1).length, 11)
  const refreshed = rows.map(row => ({ ...row, revision: `${row.revision}-new` }))
  host.render({ ...props, rows: refreshed })
  assert.equal(received.at(-1).length, 11, 'refresh cannot expand a still-filtered toolbar to all search matches')
  assert.ok(received.at(-1).every(row => row.revision.endsWith('-new')), 'bulk mutation receives the fresh row revisions')
  host.reset().props.onClick(); host.render()
  assert.equal(received.at(-1).length, 12)
})

test('tables that do not opt into the callback retain their existing native-filter export behavior', () => {
  const exported = [], props = { rows, columns, onExcelExport: visible => exported.push(visible) }
  const host = mountTable(props)
  applySikkimFacet(host); host.render()
  host.export().props.onClick()
  assert.equal(exported.at(-1).length, 11)
})
