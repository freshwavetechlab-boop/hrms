import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url), ts = require('typescript'), react = require('react'), jsx = require('react/jsx-runtime')
const node = name => Object.assign(() => null, { displayName: name })
const antd = Object.fromEntries(['Alert', 'Button', 'Card', 'Checkbox', 'Input', 'Modal', 'Select', 'Space', 'Tag'].map(name => [name, node(name)]))
antd.Input.Password = node('Password')
const DataTable = node('DataTable'), SearchSelect = node('SearchSelect')
const same = (a, b) => a && b && a.length === b.length && a.every((value, i) => Object.is(value, b[i]))
const source = ts.transpileModule(readFileSync(new URL('../src/components/SecurityPanel.tsx', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done }); return { promise, resolve } }
const preview = clientId => [{ employeeId: clientId * 10, clientId, clientName: `Client ${clientId}`, employeeCode: `EX-${clientId}`, employeeName: `Synthetic ${clientId}`, workEmail: `${clientId}@example.test`, department: 'Operations', designation: 'Analyst' }]
const securityData = () => ({ users: [], roles: [{ id: 1, code: 'employee', name: 'Employee', permissions: 'ess.self', isSystem: true }], permissions: [], auditLogs: [], clients: [42, 99].map(id => ({ id, name: `Client ${id}`, isActive: true })), employees: [], workLocations: [] })
function mount(overrides = {}, initialQuery = 'clientId=42') {
  let cursor = 0, dirty = true, effects = [], output, params = new URLSearchParams(initialQuery)
  const slots = [], hooks = { ...react,
    useState(initial) { const i = cursor++; slots[i] ||= { value: typeof initial === 'function' ? initial() : initial }; return [slots[i].value, next => { const value = typeof next === 'function' ? next(slots[i].value) : next; if (!Object.is(slots[i].value, value)) { slots[i].value = value; dirty = true } }] },
    useRef(initial) { const i = cursor++; slots[i] ||= { value: { current: initial } }; return slots[i].value },
    useMemo(factory, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) slots[i] = { value: factory(), deps }; return slots[i].value },
    useEffect(effect, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) { const cleanup = slots[i]?.cleanup; slots[i] = { deps }; effects.push(() => { cleanup?.(); slots[i].cleanup = effect() }) } },
  }
  const setParams = next => { params = new URLSearchParams(next); dirty = true }
  const services = { loadSecurityData: async () => securityData(), loadEmployeeProvisionPreview: async client => preview(Number(client)), provisionEmployeeLogins: async () => ({ ok: true, data: { createdCount: 0, skippedCount: 0, results: [], temporaryPassword: '' } }), ...overrides }
  const exported = {}
  new Function('exports', 'require', source)(exported, name => {
    if (name === 'react') return hooks
    if (name === 'react/jsx-runtime') return jsx
    if (name === 'react-router-dom') return { useSearchParams: () => [params, setParams] }
    if (name === 'antd') return antd
    if (name === '@ant-design/icons') return new Proxy({}, { get: (_, key) => node(String(key)) })
    if (name === './DataTable') return { __esModule: true, default: DataTable }
    if (name === './SearchSelect') return { __esModule: true, default: SearchSelect }
    if (name === './AuthGate') return { useAuthSession: () => ({ user: { id: 17, clientId: null, clientAccessMode: 'Legacy', permissions: ['security.manage'] } }) }
    if (name === '../services/securityService') return services
    if (name === '../utils/employeeOnboardingSelection') return { readOnboardingSelection: () => null, clearOnboardingSelection() {}, createdEmployeeCredentials: response => response.results.filter(row => row.status === 'Created') }
    if (name === './BulkUploadPreviewModal') return { __esModule: true, default: node(name), emptyBulkUploadPreview: { open: false } }
    if (name === './BulkUploadProgressModal') return { __esModule: true, default: node(name) }
    if (name === '../utils/importPreview' || name === '../utils/xlsx' || name.endsWith('.css')) return {}
    throw Error('Unexpected dependency: ' + name)
  })
  const render = () => {
    for (let attempt = 0; dirty && attempt < 30; attempt++) { dirty = false; cursor = 0; effects = []; output = exported.default({ initialTab: 'Users' }); effects.forEach(effect => effect()) }
    assert.equal(dirty, false, 'hooks settle')
  }
  const find = predicate => { const found = []; const walk = value => { if (Array.isArray(value)) value.forEach(walk); else if (react.isValidElement(value)) { if (predicate(value)) found.push(value); Object.values(value.props).forEach(walk) } }; walk(output); return found }
  const buttons = text => find(value => value.type === antd.Button && String(Array.isArray(value.props.children) ? value.props.children.join('') : value.props.children) === text)
  const button = text => { const found = buttons(text); assert.ok(found.length, text); return found[0] }
  const modal = () => find(value => value.type === antd.Modal && value.props.title === 'Create ESS logins')[0]
  const table = () => find(value => value.type === DataTable && value.props.exportFileName === 'employee-login-provision-preview')[0]
  const selectClient = id => { const select = find(value => value.type === SearchSelect)[0]; assert.ok(select); select.props.onChange(String(id)); render() }
  const settle = async () => { for (let i = 0; i < 5; i++) { await new Promise(resolve => setImmediate(resolve)); render() } }
  render()
  return { render, settle, button, modal, table, selectClient, params: () => params, route: query => { setParams(query); render() }, resultTables: () => find(value => value.type === DataTable && value.props.exportFileName === 'employee-login-import-result') }
}
globalThis.sessionStorage = { getItem: () => null, setItem() {}, removeItem() {} }

test('client switch hides the open preview and ignores the previous client’s late response', async () => {
  const old = deferred(), calls = []
  const host = mount({ loadEmployeeProvisionPreview: client => { calls.push(client); return String(client) === '42' ? old.promise : Promise.resolve(preview(99)) } })
  await host.settle()
  host.button('Import Employees').props.onClick(); host.render()
  assert.equal(host.modal().props.open, true)
  host.selectClient(99)
  assert.equal(host.params().get('clientId'), '99')
  assert.equal(host.modal().props.open, false)
  host.button('Import Employees').props.onClick(); await host.settle()
  assert.equal(host.modal().props.open, true)
  assert.deepEqual(host.table().props.rows.map(row => row.clientId), [99])
  old.resolve(preview(42)); await host.settle()
  assert.deepEqual(host.table().props.rows.map(row => row.clientId), [99])
  assert.deepEqual(calls, ['42', '99'])
})
test('a late submit cannot reopen old credentials under a new client; double clicks issue one unchanged-ID request', async () => {
  const pending = deferred(), requests = []
  const host = mount({ provisionEmployeeLogins: request => { requests.push(request); return pending.promise } })
  await host.settle(); host.button('Import Employees').props.onClick(); await host.settle()
  const submit = host.button('Create logins (1)'); submit.props.onClick(); submit.props.onClick(); host.render()
  assert.equal(requests.length, 1); assert.deepEqual(requests[0].employeeIds, [420])
  host.route('clientId=99'); await host.settle()
  assert.equal(host.modal().props.open, false)
  pending.resolve({ ok: true, data: { createdCount: 1, skippedCount: 0, temporaryPassword: 'OLD-SCOPE-PASSWORD', results: [{ employeeId: 420, status: 'Created', temporaryPassword: 'OLD-SCOPE-PASSWORD' }] } })
  await host.settle()
  assert.equal(host.modal().props.open, false)
  assert.equal(host.resultTables().length, 0)
  host.button('Import Employees').props.onClick(); await host.settle()
  assert.deepEqual(host.table().props.rows.map(row => row.employeeId), [990])
})
test('manual client choice survives the post-create directory refresh and is restored from its synchronized URL', async () => {
  let loads = 0
  const host = mount({ loadSecurityData: async () => { loads++; return securityData() } })
  await host.settle(); host.selectClient(99)
  host.button('Import Employees').props.onClick(); await host.settle()
  host.button('Create logins (1)').props.onClick(); await host.settle()
  assert.equal(loads, 2)
  assert.equal(host.params().get('clientId'), '99')
  host.button('Close').props.onClick(); host.render()
  host.button('Import Employees').props.onClick(); await host.settle()
  assert.deepEqual(host.table().props.rows.map(row => row.clientId), [99])
})
