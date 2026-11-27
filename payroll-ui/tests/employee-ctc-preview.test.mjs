import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
import * as jsx from 'react/jsx-runtime'

function compile(file, require) {
  const exports = {}
  const source = readFileSync(new URL(file, import.meta.url), 'utf8')
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS,
    target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX } }).outputText
  new Function('exports', 'require', compiled)(exports, require)
  return exports
}

const ctc = compile('../src/utils/employeeCtc.ts', () => { throw new Error('Unexpected CTC dependency') })

function previewDrawer(preview, syncEmployeeCtc = true, extra = {}) {
  const state = []
  let cursor, tree, submitted
  const react = {
    useMemo: build => build(),
    useState: initial => {
      const slot = cursor++
      if (!(slot in state)) state[slot] = initial
      return [state[slot], next => { state[slot] = typeof next === 'function' ? next(state[slot]) : next }]
    },
    useEffect: () => {},
  }
  const modal = compile('../src/components/BulkUploadPreviewModal.tsx', name => {
    if (name === 'react') return react
    if (name === 'react/jsx-runtime') return jsx
    if (name === '../utils/employeeCtc') return ctc
    if (name === '../utils/importPreview') return { hasCellIssue: () => false }
    if (name === 'antd') return Object.fromEntries(['Alert', 'Button', 'Drawer', 'Pagination', 'Space', 'Tag'].map(key => [key, key]))
    throw new Error(`Unexpected modal dependency ${name}`)
  }).default
  const render = () => {
    cursor = 0
    tree = modal({ preview, syncEmployeeCtc, onCancel() {}, onConfirm: draft => { submitted = draft }, ...extra })
  }
  function find(matches, node) {
    if (Array.isArray(node)) return node.map(child => find(matches, child)).find(Boolean)
    if (!node?.props) return
    if (matches(node)) return node.props
    return find(matches, node.props.children ?? []) || find(matches, node.props.footer ?? [])
  }
  render()
  return {
    control: id => find(node => node.props['data-testid'] === id, tree),
    render,
    edit(header, value) {
      find(node => node.props['aria-label'] === `${header} row 2`, tree).onChange({ target: { value } })
      render()
    },
    blur(header, value) {
      find(node => node.props['aria-label'] === `${header} row 2`, tree).onBlur({ target: { value } })
      render()
    },
    sheet(name) {
      find(node => node.type === 'button' && node.props.children?.[0] === name, tree).onClick()
      render()
    },
    save() { find(node => node.props['data-testid'] === 'bulk-preview-import', tree).onClick(); return submitted },
  }
}

const headers = ['Employee Code', 'Annual CTC', 'Monthly CTC', 'Bank Account No']
const preview = () => ({ open: true, title: 'Employee bulk upload preview', fileName: 'synthetic.xlsx',
  headers, rows: [['TEST-1', '120000', '10000', '001234']], issues: [] })

test('employee preview edits monthly and annual CTC in both directions and preserves source identity', () => {
  const source = preview(), before = structuredClone(source)
  const drawer = previewDrawer(source)
  drawer.edit('Monthly CTC', '25000.01')
  assert.deepEqual(drawer.save().rows, [['TEST-1', '300000.12', '25000.01', '001234']])
  drawer.edit('Annual CTC', '100000')
  assert.deepEqual(drawer.save().rows, [['TEST-1', '100000', '8333.33', '001234']])
  drawer.edit('Monthly CTC', '0')
  assert.deepEqual(drawer.save().rows, [['TEST-1', '0', '0', '001234']])
  drawer.edit('Annual CTC', '')
  assert.deepEqual(drawer.save().rows, [['TEST-1', '', '', '001234']])
  assert.deepEqual(source, before)
})

test('employee preview updates only the active worksheet and row', () => {
  const first = { name: 'Employees', headers, rows: [['TEST-1', '120000', '10000', '001234']], issues: [] }
  const second = { name: 'Other', headers, rows: [['TEST-2', '240000', '20000', '004321']], issues: [] }
  const drawer = previewDrawer({ ...preview(), sheets: [first, second], rows: [...first.rows, ...second.rows] })
  drawer.sheet('Other')
  drawer.edit('Monthly CTC', '1000')
  assert.deepEqual(drawer.save().sheets[0], first)
  assert.deepEqual(drawer.save().sheets[1].rows, [['TEST-2', '12000', '1000', '004321']])
  assert.deepEqual(drawer.save().rows, [...first.rows, ['TEST-2', '12000', '1000', '004321']])
})

test('other bulk previews retain their independent field editing behavior', () => {
  const drawer = previewDrawer(preview(), false)
  drawer.edit('Monthly CTC', '20000')
  assert.deepEqual(drawer.save().rows, [['TEST-1', '120000', '20000', '001234']])
})


test('employee salary input keeps raw typing until blur then rounds the draft to four decimals', () => {
  const amount = 'Special Allowance [SALARY_INPUT:33]'
  const source = { ...preview(), headers: ['Employee Code', amount], rows: [['TEST-1', '0']] }
  const drawer = previewDrawer(source)
  drawer.edit(amount, '123.45675')
  assert.equal(drawer.save().rows[0][1], '123.45675')
  drawer.blur(amount, '123.45675')
  assert.equal(drawer.save().rows[0][1], '123.4568')
  assert.equal(source.rows[0][1], '0')
})

test('choosing an existing employee code through a cell accessory edits only that draft cell and reruns import validation', () => {
  const required = { column: 'Employee Code', rowNumber: 2, message: 'Employee Code is required.' }
  const first = { name: 'Employees', headers, rows: [['', '120000', '10000', '001234']], issues: [required] }
  const second = { name: 'Other', headers, rows: [['', '240000', '20000', '004321']], issues: [required] }
  const source = { ...preview(), sheets: [first, second], rows: [...first.rows, ...second.rows], issues: [required, required] }
  const before = structuredClone(source)
  const drawer = previewDrawer(source, true, {
    validateSheet: data => data.rows.flatMap((row, index) => row[0] ? [] : [{ ...required, rowNumber: index + 2 }]),
    renderCellExtra: (sheet, rowIndex, colIndex, change) => colIndex === 0 ? jsx.jsx('button', {
      'data-testid': `choose-code-${sheet.name}`, onClick: () => change(sheet.name === 'Employees' ? 'CURRENT10' : 'CURRENT20'),
    }) : null,
  })
  assert.equal(drawer.control('bulk-preview-import').disabled, true)
  drawer.control('choose-code-Employees').onClick()
  drawer.render()
  assert.equal(drawer.control('bulk-preview-import').disabled, true, 'another worksheet still has a missing code')
  assert.deepEqual(drawer.save().sheets[0].rows, [['CURRENT10', '120000', '10000', '001234']])
  assert.deepEqual(drawer.save().sheets[1], second)
  drawer.sheet('Other')
  drawer.control('choose-code-Other').onClick()
  drawer.render()
  assert.equal(drawer.control('bulk-preview-import').disabled, false)
  assert.deepEqual(drawer.save().rows, [['CURRENT10', '120000', '10000', '001234'], ['CURRENT20', '240000', '20000', '004321']])
  assert.deepEqual(source, before, 'a candidate selection cannot mutate the uploaded source')
})
