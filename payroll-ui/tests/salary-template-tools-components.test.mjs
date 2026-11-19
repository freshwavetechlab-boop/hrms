import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url), ts = require('typescript'), react = require('react'), jsx = require('react/jsx-runtime')
const root = fileURLToPath(new URL('../src/', import.meta.url))
const symbol = name => Object.assign(() => null, { displayName: name })
const antd = Object.fromEntries(['Alert', 'Button', 'Checkbox', 'Input', 'InputNumber', 'Modal', 'Select', 'Space', 'Spin', 'Tag'].map(name => [name, symbol(name)]))
const same = (a, b) => a && b && a.length === b.length && a.every((value, i) => Object.is(value, b[i]))

// Offline component-event tests: synthetic capture and mocked transport only.
// Real analysis, source dependency capture and workbook export utilities run unchanged.
function mount(name, props, overrides = {}) {
  const slots = [], cache = new Map(); let cursor = 0, dirty = true, output, effects = []
  const hooks = { ...react,
    useState(initial) { const i = cursor++; slots[i] ||= { value: typeof initial === 'function' ? initial() : initial }; return [slots[i].value, next => { const value = typeof next === 'function' ? next(slots[i].value) : next; if (!Object.is(value, slots[i].value)) { slots[i].value = value; dirty = true } }] },
    useRef(initial) { const i = cursor++; slots[i] ||= { value: { current: initial } }; return slots[i].value },
    useMemo(factory, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) slots[i] = { value: factory(), deps }; return slots[i].value },
    useEffect(effect, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) { const cleanup = slots[i]?.cleanup; slots[i] = { deps }; effects.push(() => { cleanup?.(); slots[i].cleanup = effect() }) } },
  }
  const load = path => {
    if (path.endsWith('.css')) return {}
    if (cache.has(path)) return cache.get(path)
    const exports = {}; cache.set(path, exports)
    const code = ts.transpileModule(readFileSync(path, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
    new Function('exports', 'require', code)(exports, dependency => {
      if (dependency === 'react') return hooks
      if (dependency === 'react/jsx-runtime') return jsx
      if (dependency === 'antd') return antd
      if (dependency === '@ant-design/icons') return new Proxy({}, { get: (_, key) => symbol(String(key)) })
      if (dependency in overrides) return overrides[dependency]
      if (!dependency.startsWith('.')) return require(dependency)
      let target = fileURLToPath(new URL(dependency, 'file:///' + path.replaceAll('\\', '/')))
      if (!/\.(css|tsx?)$/.test(target)) target += '.ts'
      return load(target)
    })
    return exports
  }
  const Component = load(root + 'components/' + name + '.tsx').default
  const render = () => {
    for (let attempts = 0; attempts < 20 && dirty; attempts++) { dirty = false; cursor = 0; effects = []; output = Component(props); for (const effect of effects) effect() }
    assert.equal(dirty, false, 'component hooks settle')
    return output
  }
  const all = predicate => {
    const result = []
    const visit = node => { if (Array.isArray(node)) node.forEach(visit); else if (react.isValidElement(node)) { if (predicate(node)) result.push(node); Object.values(node.props).forEach(visit) } }
    visit(output); return result
  }
  const get = label => { const match = all(node => node.props['aria-label'] === label); assert.equal(match.length, 1, label); return match[0] }
  const button = text => { const match = all(node => node.type === antd.Button && node.props.children === text); assert.equal(match.length, 1, text); return match[0] }
  const settle = async () => { for (let i = 0; i < 5; i++) { await new Promise(resolve => setImmediate(resolve)); render() } }
  render(); return { render, settle, get, button, all }
}

const text = value => ({ value: String(value), dataType: 'Text' }), number = value => ({ value: String(value), dataType: 'Number' })
const formula = (formulaText, value) => ({ ...number(value), formula: true, formulaText })
const headers = ['Employee Name', 'Monthly Basic', 'Payroll days', 'Actual present days', 'Payable days', 'Basic', 'Employee PF', 'Professional tax', 'Total earnings', 'Net pay', ...Array.from({ length: 26 }, (_, i) => 'Reference ' + (i + 1))]
const source = { name: 'Salary', columnCount: headers.length, rows: [
  { sourceRow: 1, cells: [text('Salary October 2026')] },
  { sourceRow: 84, cells: headers.map(text) },
  { sourceRow: 85, cells: [text('Synthetic employee'), number(10000), number(30), number(15), number(15), formula('B85/C85*E85', 5000), formula('F85*12%', 600), number(0), formula('F85', 5000), formula('I85-G85-H85', 4400), ...Array.from({ length: 26 }, () => text('Reference'))] },
] }
const capture = { key: 'wb_' + 'a'.repeat(64), sha256: 'a'.repeat(64), fileName: 'synthetic.xlsx', sheets: [source] }
const props = { clientId: 42, clientName: 'Synthetic client', components: [], locations: [], canCreateComponentMasters: true, onClose() {}, onUseDraft() {} }
function extracted(request) {
  const components = request.selectedColumns.filter(c => ['earning', 'deduction', 'employer'].includes(c.kind)).map(c => ({ id: 'extract_' + c.columnIndex, code: c.code, name: c.name, columnIndex: c.columnIndex, category: c.kind === 'deduction' ? 'Deduction' : 'Earning', componentRole: c.kind === 'deduction' ? 'Other Deduction' : 'Regular Earning', calculationType: c.kind === 'earning' || c.reviewedFixedValue ? 'Fixed Amount' : 'Formula', value: c.reviewedFixedValue ? '0' : '', formula: c.kind === 'deduction' && !c.reviewedFixedValue ? 'SITE_BASIC_EARNED*12%' : '', requiresEmployeeInput: c.kind === 'earning', priority: c.columnIndex, proRata: c.kind === 'earning' }))
  const lines = components.map(c => ({ componentId: c.id, value: c.value, formula: c.formula, calculationType: c.calculationType, proRata: String(c.proRata) }))
  return { canCreateDraft: true, templateName: request.templateName, components, lines, issues: [], comparisons: [], simulation: { gross: 5000, deductions: 600, net: 4400, benefits: 0, issues: [] }, scenario: { payPeriod: request.month, payrollDays: 30, presentDays: 15, payableDays: 15, annualCtc: 0, snapshot: { components, lines }, amountOverrides: { ['extract_5']: 10000 } } }
}
async function extractor(onExtract, extra = {}) {
  const captureModule = (() => {
    const exports = {}, memo = new Map()
    const load = file => { if (memo.has(file)) return memo.get(file); const out = {}; memo.set(file, out); const code = ts.transpileModule(readFileSync(root + 'utils/' + file + '.ts', 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText; new Function('exports', 'require', code)(out, name => name.startsWith('./') ? load(name.slice(2)) : require(name)); return out }
    Object.assign(exports, load('workbookDependencyCapture'), { captureWorkbook: async () => structuredClone(capture) }); return exports
  })()
  const host = mount('SalaryTemplateExcelExtractor', { ...props, ...extra }, {
    '../utils/workbookDependencyCapture': captureModule,
    '../services/excelPayslipService': { getExcelPayslipProfile: async () => ({ ok: false, status: 404 }) },
    '../services/salaryTemplateExcelService': { extractSalaryTemplateExcel: onExtract },
  })
  host.get('Salary workbook file').props.onChange({ target: { files: [{ name: 'synthetic.xlsx' }] }, currentTarget: { value: '' } })
  await host.settle(); return host
}

test('extractor forwards selected component codes/names and explicit common zero, including columns after Z and late headers', async () => {
  let request, draft
  const host = await extractor(async value => { request = value; return { ok: true, data: extracted(value) } }, { onUseDraft: value => { draft = value } })
  assert.equal(host.get('Excel header row').props.value, 84)
  assert.ok(host.get('Excel header row').props.options.some(option => option.value === 85))
  assert.ok(host.all(node => node.type === 'th' && node.props.children === 'AJ').length)
  host.get('New component code for column F').props.onChange({ target: { value: 'SITE_BASIC' } }); host.render()
  assert.equal(host.get('Input code for column B').props.value, 'SITE_BASIC', 'paired input follows the output code')
  host.get('New component code for column H').props.onChange({ target: { value: 'SITE_PT' } }); host.render()
  host.get('New component name for column H').props.onChange({ target: { value: 'Site professional tax' } })
  host.render(); host.get('Common fixed value for column H').props.onChange({ target: { checked: true } }); host.render()
  host.get('Extraction location label').props.onChange({ target: { value: 'Site A' } }); host.render()
  await host.settle()
  assert.equal(host.button('Extract & simulate').props.disabled, false, JSON.stringify(host.all(node => node.type === antd.Alert).map(node => node.props.message)))
  await host.button('Extract & simulate').props.onClick(); await host.settle()
  assert.equal(request.clientId, 42); assert.equal(request.sampleRow, 85)
  assert.equal(request.selectedColumns.find(c => c.columnIndex === 7).reviewedFixedValue, true)
  assert.equal(request.selectedColumns.find(c => c.columnIndex === 7).name, 'Site professional tax')
  assert.equal(request.selectedColumns.some(c => c.columnIndex === 35), false, 'ignored reference columns are not salary bindings')
  host.button('Use template draft').props.onClick(); host.render()
  assert.equal(draft.structure.clientId, '42'); assert.equal(draft.structure.excelSource.workLocationLabel, 'Site A')
  assert.equal(draft.structure.excelSource.payrollDays, 30)
  assert.equal(draft.newComponents.find(c => c.code === 'SITE_BASIC').value, '', 'employee base is not persisted as a common amount')
  assert.equal(draft.newComponents.find(c => c.code === 'SITE_PT').value, '0')
  assert.equal(draft.structure.excelSource.columns.some(c => 'value' in c), false, 'provenance stores rules, not sample employee values')
  host.get('Extraction location label').props.onChange({ target: { value: 'Site B' } }); host.render()
  assert.equal(host.button('Use template draft').props.disabled, true, 'a changed location needs its own extraction')
})

test('an in-flight extraction cannot produce a draft after the source context changes', async () => {
  let resolve
  const host = await extractor(request => new Promise(done => { resolve = () => done({ ok: true, data: extracted(request) }) }))
  host.button('Extract & simulate').props.onClick(); host.render()
  host.get('Extracted template name').props.onChange({ target: { value: 'Changed context' } }); host.render()
  resolve(); await host.settle()
  assert.equal(host.button('Use template draft').props.disabled, true)
})

test('formula download preserves chosen month/divisor and filters other clients and locations without changing saved rules', () => {
  const component = { id: 1, code: 'BASIC', name: 'Basic', category: 'Earning', active: true, componentRole: 'Regular Earning', calculationType: 'Fixed Amount', value: '', formula: '', baseComponent: '', proRata: true, priority: '1', payType: 'Fixed Pay' }
  const template = { id: 7, clientId: '42', name: 'Site A rules', active: true, annualCtc: '', lines: [{ componentId: '1', value: '' }], excelSource: { workLocationLabel: 'Site A', payrollDays: 26 } }
  const data = { clientId: 42, clientName: 'Synthetic client', components: [component], templates: [template, { ...template, id: 8, clientId: '99' }, { ...template, id: 9, excelSource: { workLocationLabel: 'Site B', payrollDays: 30 } }], locations: [], preferredTemplateId: '7', onClose() {} }
  const before = JSON.stringify(data), downloads = []
  const host = mount('SalaryTemplateExcelDownload', data, { '../utils/xlsx': { downloadXlsx: (...args) => downloads.push(args) } })
  assert.deepEqual(host.get('Download saved salary template').props.options.map(c => c.value), ['7'])
  host.get('Download template pay month').props.onChange({ target: { value: '2026-08' } }); host.render()
  host.button('Download Excel with formulas').props.onClick(); host.render()
  assert.match(downloads[0][0], /2026-08-formulas.xlsx$/)
  assert.equal(host.get('Download template pay month').props.value, '2026-08')
  assert.equal(host.get('Download template payroll divisor').props.value, 26)
  assert.equal(JSON.stringify(data), before)
  host.get('Download template location').props.onChange('Site B'); host.render()
  assert.deepEqual(host.get('Download saved salary template').props.options.map(c => c.value), ['9'])
  assert.equal(host.button('Download Excel with formulas').props.disabled, true)
})
