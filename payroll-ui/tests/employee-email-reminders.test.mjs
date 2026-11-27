import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
import * as react from 'react'
import * as jsx from 'react/jsx-runtime'

const ok = data => ({ ok: true, data, status: 200, error: '' })
const failed = status => ({ ok: false, data: { items: [], total: 0, offset: 0, limit: 100 }, status, error: 'Not available' })
const reminder = (employeeId, clientId = 10) => ({ employeeId, clientId, employeeCode: `SYN${employeeId}`, employeeName: `Example ${employeeId}`, clientName: `Client ${clientId}`, emailIssue: 'Missing' })
const page = (items, total = items.length, offset = 0) => ({ items, total, offset, limit: 100 })
const deferred = () => { let resolve; return { promise: new Promise(done => { resolve = done }), resolve: value => resolve(value) } }
const compile = (path, dependencies) => {
  const output = {}
  const source = ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX } }).outputText
  new Function('exports', 'require', source)(output, name => { assert.ok(name in dependencies, name); return dependencies[name] })
  return output
}
const same = (left, right) => left && right && left.length === right.length && left.every((item, i) => Object.is(item, right[i]))
const control = name => Object.assign(() => null, { displayName: name })
const antd = Object.fromEntries(['Alert', 'Badge', 'Button', 'Empty', 'List', 'Popover', 'Progress', 'Space', 'Spin', 'Tag'].map(name => [name, control(name)]))
antd.List.Item = control('List.Item')
antd.Typography = { Text: control('Typography.Text') }

function mount(service, initialUser = {}) {
  let session = { user: { id: 7, clientId: 10, clientAccessMode: 'Selected', assignedClientIds: [10, 20], permissions: ['employees.manage'], ...initialUser } }
  let output, cursor = 0, dirty = false, effects = [], savedCallback
  const state = [], routes = [], calls = [], listeners = new Map(), timers = new Map()
  const hooks = { ...react,
    useState(initial) { const index = cursor++; state[index] ||= { value: typeof initial === 'function' ? initial() : initial }; return [state[index].value, value => { const next = typeof value === 'function' ? value(state[index].value) : value; if (!Object.is(next, state[index].value)) { state[index].value = next; dirty = true } }] },
    useRef(initial) { const index = cursor++; state[index] ||= { value: { current: initial } }; return state[index].value },
    useCallback(callback, deps) { const index = cursor++; if (!state[index] || !same(state[index].deps, deps)) state[index] = { value: callback, deps }; return state[index].value },
    useEffect(effect, deps) { const index = cursor++; if (!state[index] || !same(state[index].deps, deps)) { const old = state[index]?.cleanup; state[index] = { deps }; effects.push(() => { old?.(); state[index].cleanup = effect() }) } },
  }
  const window = { setInterval: callback => { const id = timers.size + 1; timers.set(id, callback); return id }, clearInterval: id => timers.delete(id),
    addEventListener: (name, callback) => listeners.set(name, callback), removeEventListener: name => listeners.delete(name), dispatchEvent() {} }
  const dependencies = {
    react: hooks, 'react/jsx-runtime': jsx, antd,
    '@ant-design/icons': { BellOutlined: control('bell'), ReloadOutlined: control('reload') },
    'react-router-dom': { useNavigate: () => route => routes.push(route) },
    './AuthGate': { useAuthSession: () => session },
    '../services/apiClient': { getJsonResult: async () => ok([]) },
    '../services/attendanceIntegrationService': { getAttendanceGaps: async () => ok([]) },
    '../services/excelPayslipService': {},
    '../services/recruitmentCaseService': { getRecruitmentMomPings: async () => ok([]) },
    '../utils/clientAccess': { isUnrestrictedClientAdmin: () => false },
    '../services/recruitmentOfferMailService': { validOfferMailJobs: () => true },
    '../services/employeeImportJobs': { listEmployeeImportJobs: async () => ok({ items: service.jobs ?? [] }), employeeJobIsRunning: () => false, employeeJobTitle: () => 'Employee import complete' },
    '../services/employeeMasterChanges': { notifyEmployeeMasterSaved() {}, onEmployeeMasterSaved: (client, callback) => { assert.equal(client, 0); savedCallback = callback; return () => { savedCallback = undefined } } },
    '../services/employeeEmailReminders': { employeeEmailReminderKey: row => `${row.clientId}:${row.employeeId}`, getEmployeeEmailReminders: async (offset = 0) => { calls.push(offset); return service.read(offset, session) } },
  }
  const source = ts.transpileModule(readFileSync(new URL('../src/components/ActionNotifications.tsx', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX } }).outputText
  const exported = {}
  new Function('exports', 'require', 'window', 'document', source)(exported, name => { assert.ok(name in dependencies, name); return dependencies[name] }, window, { hidden: false })
  const render = () => {
    dirty = true
    for (let attempt = 0; dirty && attempt < 25; attempt++) { dirty = false; cursor = 0; effects = []; output = exported.default(); effects.forEach(effect => effect()) }
    assert.equal(dirty, false, 'component settles')
  }
  const settle = async () => { for (let i = 0; i < 5; i++) { await new Promise(resolve => setImmediate(resolve)); render() } }
  const find = predicate => {
    const found = []
    const walk = value => { if (Array.isArray(value)) value.forEach(walk); else if (react.isValidElement(value)) { if (predicate(value)) found.push(value); Object.values(value.props).forEach(walk) } }
    walk(output); return found
  }
  render()
  return { render, settle, calls, routes,
    user: user => { session = { user: { ...session.user, ...user } }; render() },
    noSession: () => { session = null; render() },
    list: () => find(value => value.type === antd.List)[0]?.props.dataSource ?? [],
    badge: () => find(value => value.type === antd.Badge)[0].props.count,
    button: name => find(value => value.type === antd.Button && (value.props['data-testid'] === name || value.props['aria-label'] === name))[0],
    saved: () => savedCallback?.(),
    timer: () => [...timers.values()][0]?.(),
    itemTree: item => find(value => value.type === antd.List)[0].props.renderItem(item),
    refresh: () => find(value => value.props['aria-label'] === 'Refresh pending actions')[0].props.onClick(),
  }
}

test('reminders include all authorised assigned clients, stay persistent, and route to the exact employee email editor', async () => {
  const host = mount({ read: async () => ok(page([reminder(1, 10), { ...reminder(2, 20), emailIssue: 'Invalid' }])) })
  await host.settle()
  assert.equal(host.badge(), 2)
  const [first, second] = host.list()
  assert.equal(first.title, 'Email missing — please update email')
  assert.equal(second.title, 'Email invalid — please update email')
  assert.match(second.detail, /Example 2 · SYN2 · Client 20/)
  assert.equal(second.route, '/employees/master?clientId=20&employeeId=2&edit=0001')
  assert.equal(first.mailJob, undefined); assert.equal(first.employeeJob, undefined)
  const tree = host.itemTree(second)
  tree.props.children[0].props.onClick()
  assert.deepEqual(host.routes, [second.route])
  assert.equal(tree.props.children.slice(1).filter(Boolean).length, 0, 'no dismiss action for unresolved email')
  const before = host.calls.length
  host.user({ permissions: ['employees.manage'] }); await host.settle()
  assert.equal(host.calls.length, before, 'new permission array with identical grants does not re-read')
})

test('saving employee email refreshes the bell and clears a resolved reminder without manual dismissal', async () => {
  let missing = true
  const host = mount({ read: async () => ok(page(missing ? [reminder(1)] : [])) })
  await host.settle(); assert.equal(host.badge(), 1)
  missing = false; host.saved(); await host.settle()
  assert.equal(host.badge(), 0); assert.deepEqual(host.list(), [])
})

test('missing employee permission prevents reads and revoked or changed scopes cannot display a previous feed', async () => {
  const old = deferred(), next = deferred()
  const host = mount({ read: (_offset, session) => session.user.clientId === 10 ? old.promise : next.promise })
  host.user({ clientId: 20, assignedClientIds: [20] }); assert.equal(host.badge(), 0)
  old.resolve(ok(page([reminder(1, 10)]))); await host.settle(); assert.equal(host.badge(), 0)
  next.resolve(ok(page([reminder(2, 20)]))); await host.settle(); assert.equal(host.badge(), 1)
  assert.equal(host.list()[0].route, '/employees/master?clientId=20&employeeId=2&edit=0001')
  const before = host.calls.length
  host.user({ permissions: [] }); await host.settle(); assert.equal(host.badge(), 0); assert.equal(host.calls.length, before)
  host.noSession(); await host.settle(); assert.equal(host.badge(), 0)
})

test('a failed refresh clears the missing-email feed instead of retaining stale employee details', async () => {
  let succeed = true
  const host = mount({ read: async () => succeed ? ok(page([reminder(1)])) : failed(403) })
  await host.settle(); assert.equal(host.badge(), 1)
  succeed = false; host.refresh(); await host.settle()
  assert.equal(host.badge(), 0); assert.deepEqual(host.list(), [])
})

test('the bell counts unloaded reminders once and Load more preserves depth through refresh', async () => {
  const source = Array.from({ length: 205 }, (_, index) => reminder(index + 1))
  const jobs = [{ jobId: 'job-1', clientId: 10, clientName: 'Client 10', kind: 'employee-import', operation: 'update', state: 'Completed', updated: 0, inserted: 0, completedRows: 1, totalRows: 1 }]
  const host = mount({ jobs, read: async offset => ok(page(source.slice(offset, offset + 100), source.length, offset)) })
  await host.settle(); assert.equal(host.badge(), 206); assert.equal(host.list().length, 101)
  host.button('load-more-email-reminders').props.onClick(); await host.settle()
  assert.equal(host.badge(), 206); assert.equal(host.list().length, 201)
  host.timer(); await host.settle()
  assert.equal(host.list().length, 201); assert.deepEqual(host.calls.slice(-2), [0, 100])
  host.button('load-more-email-reminders').props.onClick(); await host.settle()
  assert.equal(host.list().length, 206); assert.equal(host.badge(), 206); assert.equal(host.button('load-more-email-reminders'), undefined)
})

test('an obsolete Load more response cannot cross scope', async () => {
  const source = Array.from({ length: 102 }, (_, index) => reminder(index + 1)), pending = deferred()
  let delayed = false
  const host = mount({ read: async (offset, session) => session.user.id === 8
    ? ok(page([reminder(501, 20), reminder(502, 20)]))
    : offset && delayed ? pending.promise : ok(page(source.slice(offset, offset + 100), source.length, offset)) })
  await host.settle(); delayed = true
  host.button('load-more-email-reminders').props.onClick(); host.render()
  host.user({ id: 8, clientId: 20, assignedClientIds: [20] }); await host.settle()
  pending.resolve(ok(page(source.slice(100), 102, 100))); await host.settle()
  assert.equal(host.list().length, 2, 'old load-more result is ignored')
  assert.equal(host.badge(), 2)
  assert.deepEqual(host.list().map(item => item.route), ['/employees/master?clientId=20&employeeId=501&edit=0001', '/employees/master?clientId=20&employeeId=502&edit=0001'])
})

test('inconsistent totals across refreshed pages retain only the fresh first page and its current count', async () => {
  const source = Array.from({ length: 102 }, (_, index) => reminder(index + 1))
  let changed = false
  const host = mount({ read: async offset => ok(page(source.slice(offset, offset + 100).map(item => changed && !offset ? { ...item, employeeName: 'Fresh employee details' } : item), changed && !offset ? 101 : 102, offset)) })
  await host.settle(); host.button('load-more-email-reminders').props.onClick(); await host.settle()
  assert.equal(host.list().length, 102)
  changed = true; host.refresh(); await host.settle()
  assert.equal(host.list().length, 100); assert.equal(host.badge(), 101)
  assert.match(host.list()[0].detail, /Fresh employee details/)
  assert.ok(host.button('load-more-email-reminders'))
})

test('email reminder service validates bounded paging and response identities before exposing a quiet feed', async () => {
  const calls = []
  let result = ok(page([reminder(1)]))
  const service = compile('../src/services/employeeEmailReminders.ts', { './apiClient': { getJsonResult: async (...args) => { calls.push(args); return result } } })
  const abort = new AbortController()
  assert.equal((await service.getEmployeeEmailReminders(0, 100, { signal: abort.signal })).ok, true)
  assert.equal(calls[0][0], '/api/employees/email-reminders?offset=0&limit=100')
  assert.equal(calls[0][2].loader, false); assert.equal(calls[0][2].toast, false); assert.equal(calls[0][2].signal, abort.signal)
  for (const [offset, limit] of [[-1, 100], [0, 101], [0.5, 100], [0, 0]]) assert.equal((await service.getEmployeeEmailReminders(offset, limit)).status, 400)
  assert.equal(calls.length, 1)
  for (const invalid of [{ ...page([reminder(1)]), total: 2 }, page([reminder(0)]), page([{ ...reminder(1), emailIssue: 'Other' }]), page([reminder(1), reminder(1)])]) {
    result = ok(invalid)
    const response = await service.getEmployeeEmailReminders()
    assert.equal(response.ok, false); assert.deepEqual(response.data.items, [])
  }
  result = ok(page([{ ...reminder(1), employeeCode: '' }]))
  assert.equal((await service.getEmployeeEmailReminders()).ok, true, 'legacy missing codes still have an employee identity')
})
