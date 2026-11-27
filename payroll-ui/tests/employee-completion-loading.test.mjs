import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url), ts = require('typescript'), react = require('react'), jsx = require('react/jsx-runtime')
const node = name => Object.assign(() => null, { displayName: name })
const antd = Object.fromEntries(['Alert', 'Button', 'Checkbox', 'Drawer', 'Input', 'Modal', 'Select', 'Space', 'Spin', 'Switch', 'Tag', 'Tooltip'].map(name => [name, node(name)]))
const ScopeSelect = node('PayrollScopeSelect')
const source = ts.transpileModule(readFileSync(new URL('../src/components/EmployeeProfileCompletionControls.tsx', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
const same = (a, b) => a && b && a.length === b.length && a.every((value, index) => Object.is(value, b[index]))
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done }); return { promise, resolve } }
const clientRows = [42, 99].map(id => ({ id, name: `Client ${id}`, isActive: true }))
const employee = (id, clientId) => ({ id, clientId })
const setup = id => ({ firstEditEnabled: true, template: { id, clientId: id, name: `Client ${id} reminder`, subjectTemplate: `Client ${id} subject`, bodyTemplate: '<p>Complete profile</p>', isActive: true, variables: [] } })
const ok = data => ({ ok: true, data, error: '', status: 200 })
const status = (employeeId, clientId) => ({ employeeId, clientId, canSendEmail: true, disabledReason: '', additionalMissing: [{ infotype: '0002', label: `Missing ${employeeId}` }] })

function mount(overrides = {}, initial = {}) {
  let cursor = 0, dirty = true, effects = [], output
  const slots = [], user = { id: 17, clientId: null, permissions: ['employees.manage', 'employee.communication.send', 'settings.manage'] }
  let props = { rows: [], clients: clientRows, clientId: 0, revision: 0, options: clientRows.map(row => ({ value: row.id, label: row.name, count: row.id === 42 ? 12 : 230 })), ...initial }
  const hooks = { ...react,
    useState(initial) { const i = cursor++; slots[i] ||= { value: typeof initial === 'function' ? initial() : initial }; return [slots[i].value, next => { const value = typeof next === 'function' ? next(slots[i].value) : next; if (!Object.is(slots[i].value, value)) { slots[i].value = value; dirty = true } }] },
    useRef(initial) { const i = cursor++; slots[i] ||= { value: { current: initial } }; return slots[i].value },
    useMemo(factory, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) slots[i] = { value: factory(), deps }; return slots[i].value },
    useEffect(effect, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) { const cleanup = slots[i]?.cleanup; slots[i] = { deps }; effects.push(() => { cleanup?.(); slots[i].cleanup = effect() }) } },
  }
  const services = { getEmployeeCompletion: async () => ok([]), setupEmployeeCompletion: async id => ok(setup(id)), saveEmployeeCompletionPolicy: async () => ok(null), ...overrides }
  const exported = {}, api = { postJson: async () => ok(null), ...(overrides.api || {}) }
  new Function('exports', 'require', source)(exported, name => {
    if (name === 'react') return hooks
    if (name === 'react/jsx-runtime') return jsx
    if (name === 'antd') return antd
    if (name === 'dompurify') return { __esModule: true, default: { sanitize: value => value } }
    if (name === '@ant-design/icons') return new Proxy({}, { get: (_, key) => node(String(key)) })
    if (name === './AuthGate') return { useAuthSession: () => ({ user }) }
    if (name === './ToastProvider') return { useToast: () => () => {} }
    if (name === '../hooks/useRecruitmentPreferences') return { useSessionPreference: (_key, value) => hooks.useState(value) }
    if (name === '../services/employeeProfileCompletionService') return services
    if (name === '../services/apiClient') return api
    if (name === './PayrollScopeSelect') return { __esModule: true, default: ScopeSelect }
    if (name === './ClientOption') return { clientChoice: client => ({ value: client.id, label: client.name, code: client.code ?? '' }), clientSelectProps: options => ({ options }) }
    if (name === './CommunicationRichEditor') return { __esModule: true, default: node(name) }
    throw Error('Unexpected dependency: ' + name)
  })
  const render = () => {
    dirty = true
    for (let attempt = 0; dirty && attempt < 30; attempt++) { dirty = false; cursor = 0; effects = []; output = exported.useEmployeeProfileCompletion(props.rows, props.clients, props.clientId, props.revision, () => {}, props.options); effects.forEach(effect => effect()) }
    assert.equal(dirty, false, 'hook settles')
  }
  const find = predicate => { const found = []; const walk = value => { if (Array.isArray(value)) value.forEach(walk); else if (react.isValidElement(value)) { if (predicate(value)) found.push(value); Object.values(value.props).forEach(walk) } }; walk(output.dialogs); return found }
  const settle = async () => { for (let i = 0; i < 4; i++) { await new Promise(resolve => setImmediate(resolve)); render() } }
  const button = text => { const result = find(value => value.type === antd.Button && value.props.children === text)[0]; assert.ok(result, text); return result }
  render()
  return { render, settle, output: () => output, props: next => { props = { ...props, ...next }; render() }, find, button,
    open: () => { output.manageButton.props.onClick(); render() },
    drawer: () => find(value => value.type === antd.Drawer)[0],
    picker: () => find(value => value.type === ScopeSelect)[0],
    subject: () => find(value => value.type === antd.Input && value.props['aria-label'] === 'Reminder email subject')[0],
    client: id => { const picker = find(value => value.type === ScopeSelect)[0]; assert.ok(picker); picker.props.onChange(id); render() },
  }
}

test('empty initial directory does not launch the expensive all-client completion read; Manage client choices stay available', async () => {
  let statusCalls = 0, setupCalls = 0
  const pending = deferred()
  const host = mount({ getEmployeeCompletion: async () => { statusCalls++; return ok([]) }, setupEmployeeCompletion: () => { setupCalls++; return pending.promise } })
  await host.settle()
  assert.equal(statusCalls, 0)
  host.open(); assert.equal(host.drawer().props.open, true)
  assert.deepEqual(host.picker().props.options.map(row => [row.value, row.count]), [[42, 12], [99, 230]])
  host.client(42); await host.settle()
  assert.equal(setupCalls, 1); assert.equal(host.drawer().props.open, true)
  assert.equal(host.picker().props.disabled, undefined)
  assert.equal(host.subject(), undefined)
  host.props({ rows: [employee(1, 42)] }); await host.settle()
  assert.equal(statusCalls, 1)
})

test('stable directory data does not repeat status reads; switching scope aborts the old request and ignores its late result', async () => {
  const old = deferred(), calls = [], first = employee(1, 42), next = employee(2, 99)
  const rows = [first]
  const host = mount({ getEmployeeCompletion: (client, options) => { calls.push({ client, options }); return client === 42 ? old.promise : Promise.resolve(ok([status(2, 99)])) } }, { rows, clientId: 42 })
  await host.settle(); host.render(); host.render()
  assert.equal(calls.length, 1)
  host.props({ clientId: 99, rows: [next] }); await host.settle()
  assert.equal(calls[0].options.signal.aborted, true)
  assert.deepEqual(host.output().additionalMissing(next), [{ infotype: '0002', label: 'Missing 2' }])
  old.resolve(ok([status(1, 42)])); await host.settle()
  assert.deepEqual(host.output().additionalMissing(first), [])
  assert.deepEqual(host.output().additionalMissing(next), [{ infotype: '0002', label: 'Missing 2' }])
})

test('slow previous-client Manage settings cannot overwrite the new client, and picker counts use the supplied full roster', async () => {
  const old = deferred()
  const host = mount({ setupEmployeeCompletion: id => id === 42 ? old.promise : Promise.resolve(ok(setup(id))) }, { clientId: 42, rows: [employee(1, 42)] })
  host.open(); await host.settle(); assert.equal(host.subject(), undefined)
  assert.equal(host.picker().props.options.find(row => row.value === 99).count, 230)
  host.client(99); await host.settle()
  assert.equal(host.subject().props.value, 'Client 99 subject')
  old.resolve(ok(setup(42))); await host.settle()
  assert.equal(host.subject().props.value, 'Client 99 subject')
})

test('a late template-save response cannot replace another client’s loaded settings', async () => {
  const pending = deferred(), writes = []
  const host = mount({ api: { postJson: (...args) => { writes.push(args); return pending.promise } } }, { clientId: 42, rows: [employee(1, 42)] })
  host.open(); await host.settle()
  host.button('Save mail template').props.onClick()
  host.client(99); await host.settle()
  pending.resolve(ok({ ...setup(42).template, subjectTemplate: 'Old save result' })); await host.settle()
  assert.equal(writes.length, 1); assert.equal(writes[0][1].clientId, 42)
  assert.equal(writes[0][3].loader, false)
  assert.equal(host.subject().props.value, 'Client 99 subject')
})

test('Manage setup and policy APIs use local loading indicators while status requests retain their abort signal', async () => {
  const calls = [], exported = {}, service = ts.transpileModule(readFileSync(new URL('../src/services/employeeProfileCompletionService.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', service)(exported, name => {
    assert.equal(name, './apiClient')
    return Object.fromEntries(['getJsonResult', 'postJson', 'putJson'].map(method => [method, (...args) => { calls.push({ method, args }); return Promise.resolve(ok(null)) }]))
  })
  const abort = new AbortController()
  await exported.getEmployeeCompletion(42, { signal: abort.signal })
  await exported.setupEmployeeCompletion(42)
  await exported.saveEmployeeCompletionPolicy(42, false)
  assert.equal(calls[0].args[2].loader, false); assert.equal(calls[0].args[2].signal, abort.signal)
  assert.equal(calls[1].args[3].loader, false); assert.equal(calls[1].args[1].clientId, 42)
  assert.equal(calls[2].args[3].loader, false); assert.deepEqual(calls[2].args[1], { clientId: 42, firstEditEnabled: false })
})
