import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url), ts = require('typescript'), react = require('react'), jsx = require('react/jsx-runtime')
const node = name => Object.assign(() => null, { displayName: name })
const antd = Object.fromEntries(['Alert', 'Button', 'Modal', 'Space', 'Tag'].map(name => [name, node(name)])), DataTable = node('DataTable')
const same = (a, b) => a && b && a.length === b.length && a.every((value, i) => Object.is(value, b[i]))
const selections = new Map()
globalThis.sessionStorage = { getItem: key => selections.get(key) ?? null, setItem: (key, value) => selections.set(key, value), removeItem: key => selections.delete(key) }
const onboarding = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeeOnboardingSelection.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(onboarding)
let actorSequence = 1000

function mount(transport, initialProps = {}) {
  let props = { clientId: 42, clientName: 'Synthetic client', onImported: async () => {}, ...initialProps }, cursor = 0, dirty = true, effects = [], output
  const slots = [], hooks = { ...react,
    useState(initial) { const i = cursor++; slots[i] ||= { value: typeof initial === 'function' ? initial() : initial }; return [slots[i].value, next => { const value = typeof next === 'function' ? next(slots[i].value) : next; if (!Object.is(slots[i].value, value)) { slots[i].value = value; dirty = true } }] },
    useRef(initial) { const i = cursor++; slots[i] ||= { value: { current: initial } }; return slots[i].value },
    useMemo(factory, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) slots[i] = { value: factory(), deps }; return slots[i].value },
    useEffect(effect, deps) { const i = cursor++; if (!slots[i] || !same(slots[i].deps, deps)) { const cleanup = slots[i]?.cleanup; slots[i] = { deps }; effects.push(() => { cleanup?.(); slots[i].cleanup = effect() }) } },
  }
  const actorId = ++actorSequence, navigation = [], params = new URLSearchParams()
  const auth = { user: { id: actorId, permissions: ['employees.manage', 'security.manage'] } }
  const source = readFileSync(new URL('../src/components/TalentAcquisitionEmployeeImport.tsx', import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText
  const exported = {}
  new Function('exports', 'require', code)(exported, name => {
    if (name === 'react') return hooks
    if (name === 'react/jsx-runtime') return jsx
    if (name === 'antd') return antd
    if (name === '@ant-design/icons') return new Proxy({}, { get: (_, key) => node(String(key)) })
    if (name === './DataTable') return { __esModule: true, default: DataTable }
    if (name === '../services/recruitmentJoiningService') return transport
    if (name === 'react-router-dom') return { useNavigate: () => path => navigation.push(path), useSearchParams: () => [params, () => {}] }
    if (name === './AuthGate') return { useAuthSession: () => auth }
    if (name === '../utils/employeeOnboardingSelection') return onboarding
    if (name.endsWith('.css')) return {}
    throw Error('Unexpected dependency: ' + name)
  })
  const render = next => {
    if (next) { props = { ...props, ...next }; dirty = true }
    for (let attempt = 0; dirty && attempt < 20; attempt++) { dirty = false; cursor = 0; effects = []; output = exported.default(props); effects.forEach(effect => effect()) }
    assert.equal(dirty, false, 'hooks settle')
  }
  const find = predicate => {
    const matches = []
    const walk = value => { if (Array.isArray(value)) value.forEach(walk); else if (react.isValidElement(value)) { if (predicate(value)) matches.push(value); Object.values(value.props).forEach(walk) } }
    walk(output); return matches
  }
  const button = label => { const matches = find(value => value.type === antd.Button && String(Array.isArray(value.props.children) ? value.props.children.join('') : value.props.children) === label); assert.equal(matches.length, 1, label); return matches[0] }
  const table = () => find(value => value.type === DataTable)[0]
  const modal = () => find(value => value.type === antd.Modal)[0]
  const settle = async () => { for (let i = 0; i < 4; i++) { await new Promise(resolve => setImmediate(resolve)); render() } }
  render(); return { render, button, table, modal, settle, actorId, navigation, alerts: () => find(value => value.type === antd.Alert) }
}

const row = (offerId, extra = {}) => ({ sourceType: 'Offer', sourceId: offerId, rowKey: 'offer:' + offerId, acceptanceRecorded: true, offerId, applicationId: offerId * 10, candidateId: offerId, candidateCode: 'EX-' + offerId, candidateName: 'Synthetic ' + offerId, clientId: 42, clientName: 'Synthetic client', positionTitle: 'Technician', jobLocation: 'Site A', proposedJoiningDate: '2026-11-01', offeredCtc: 240000, currency: 'INR', offerStatus: 'Accepted', employeeId: null, employeeCode: '', importStatus: 'Ready', issue: '', ...extra })
const rows = [row(1), row(2), row(3, { employeeId: 300, employeeCode: 'EX-3', importStatus: 'Imported' }), row(4, { importStatus: 'Review', issue: 'Identity needs review' }), row(5, { acceptanceRecorded: false, offerStatus: 'Draft', importStatus: 'Pending acceptance' }), row(6, { clientId: 99 }), row(0, { sourceType: 'Roster', sourceId: 92, rowKey: 'roster:92', rosterRowId: 92 }), row(0, { sourceType: 'Roster', sourceId: 91, rowKey: 'roster:91', rosterRowId: 91, acceptanceRecorded: false, employeeId: 900, employeeCode: 'EX-900', importStatus: 'Imported' })]

test('accepted candidates default selected, deselection submits once, imported/roster identities cannot be resubmitted', async () => {
  const requests = []; let refreshed = 0
  const host = mount({ getRecruitmentJoiningCandidates: async () => ({ ok: true, data: structuredClone(rows) }), importRecruitmentJoiningCandidates: async selection => { requests.push(selection); return { ok: true, data: { imported: 1, alreadyImported: 0, review: 0, items: [{ offerId: 2, applicationId: 20, candidateName: 'Synthetic 2', status: 'Imported', employeeId: 200, employeeCode: 'EX-2', message: 'Created' }] } } } }, { onImported: async () => { refreshed++ } })
  host.button('Import selected from Talent Acquisition').props.onClick(); await host.settle()
  assert.deepEqual(host.table().props.rows.map(row => row.rowKey), ['offer:1', 'offer:2', 'offer:3', 'offer:4', 'roster:92', 'roster:91'])
  assert.deepEqual(host.table().props.rowSelection.selectedRowKeys, ['offer:1', 'offer:2', 'roster:92'])
  assert.equal(host.table().props.rowSelection.getCheckboxProps(rows.at(-1)).disabled, true)
  assert.equal(host.table().props.rows.at(-1).acceptanceRecorded, false, 'legacy imported employees remain visible without inventing an acceptance audit')
  host.table().props.rowSelection.onChange(['offer:2', 'roster:91', 'offer:3']); host.render()
  host.button('Import selected (1)').props.onClick(); await host.settle()
  assert.deepEqual(requests, [{ clientId: 42, importReleasedForEss: true, offerIds: [2], rosterRowIds: [] }]); assert.equal(refreshed, 1)
  assert.equal(host.table().props.rows.find(row => row.offerId === 2).employeeCode, 'EX-2')
  assert.equal(host.table().props.rows.find(row => row.offerId === 1).employeeId, null)
  assert.equal(host.button('Import selected (0)').props.disabled, true)
})

test('select all matching uses the current table result and partial success retains per-row reasons', async () => {
  const host = mount({ getRecruitmentJoiningCandidates: async () => ({ ok: true, data: structuredClone(rows) }), importRecruitmentJoiningCandidates: async () => ({ ok: true, data: { imported: 0, alreadyImported: 0, review: 1, items: [{ offerId: 2, applicationId: 20, candidateName: 'Synthetic 2', status: 'Review', message: 'Required joining document is missing.' }] } }) })
  host.button('Import selected from Talent Acquisition').props.onClick(); await host.settle()
  host.button('Clear selection').props.onClick(); host.render()
  host.table().props.onVisibleRowsChange([rows[1]]); host.render()
  host.button('Select all matching').props.onClick(); host.render()
  assert.deepEqual(host.table().props.rowSelection.selectedRowKeys, ['offer:2'])
  host.button('Import selected (1)').props.onClick(); await host.settle()
  const reviewed = host.table().props.rows.find(row => row.offerId === 2)
  assert.equal(reviewed.importStatus, 'Review'); assert.equal(reviewed.issue, 'Required joining document is missing.')
  assert.equal(host.alerts().at(-1).props.type, 'warning')
})

test('duplicate clicks are locked and late previous-client reads cannot enter the new client selection', async () => {
  let releaseRead, releaseImport, calls = 0
  const host = mount({ getRecruitmentJoiningCandidates: async () => ({ ok: true, data: structuredClone(rows) }), importRecruitmentJoiningCandidates: () => { calls++; return new Promise(resolve => { releaseImport = () => resolve({ ok: false, error: 'Unavailable' }) }) } })
  host.button('Import selected from Talent Acquisition').props.onClick(); await host.settle()
  const submit = host.button('Import selected (3)'); submit.props.onClick(); submit.props.onClick(); host.render()
  assert.equal(calls, 1); releaseImport(); await host.settle()
  assert.equal(host.button('Import selected (3)').props.disabled, false, 'failed transport preserves selection for retry')
  const stale = mount({ getRecruitmentJoiningCandidates: () => new Promise(resolve => { releaseRead = () => resolve({ ok: true, data: structuredClone(rows) }) }) })
  stale.button('Import selected from Talent Acquisition').props.onClick(); stale.render()
  stale.render({ clientId: 99, clientName: 'Other client' }); releaseRead(); await stale.settle()
  assert.equal(stale.modal().props.open, false); assert.deepEqual(stale.table().props.rows, [])
})

test('native table filtering excludes hidden selected rows and mixed offer/roster results retain composite identities', async () => {
  const requests = []
  const host = mount({ getRecruitmentJoiningCandidates: async () => ({ ok: true, data: structuredClone(rows) }), importRecruitmentJoiningCandidates: async selection => {
    requests.push(selection)
    return { ok: true, data: { imported: 2, alreadyImported: 0, review: 0, items: [
      { rowKey: 'offer:2', sourceType: 'Offer', offerId: 2, employeeId: 200, employeeCode: 'EX-2', status: 'Imported', message: 'Imported offer' },
      { rowKey: 'roster:92', sourceType: 'Roster', sourceId: 92, rosterRowId: 92, offerId: 0, employeeId: 920, employeeCode: 'EX-920', status: 'Imported', message: 'Imported roster' },
    ] } }
  } })
  host.button('Import selected from Talent Acquisition').props.onClick(); await host.settle()
  host.table().props.onVisibleRowsChange([rows[1], rows[6]]); host.render()
  host.button('Import selected (2)').props.onClick(); await host.settle()
  assert.deepEqual(requests, [{ clientId: 42, importReleasedForEss: true, offerIds: [2], rosterRowIds: [92] }])
  const saved = host.table().props.rows
  assert.equal(saved.find(row => row.rowKey === 'offer:1').employeeId, null, 'hidden selection is untouched')
  assert.equal(saved.find(row => row.rowKey === 'roster:92').employeeId, 920)
  assert.equal(saved.find(row => row.rowKey === 'roster:91').employeeId, 900, 'another roster offerId zero must not match this result')
  host.table().props.onVisibleRowsChange(saved); host.render()
  assert.equal(host.button('Import selected (1)').props.disabled, false, 'hidden eligible selection returns when filter clears')
})

test('changing client while an import finishes cannot display the old result in the new client', async () => {
  let release, refreshed = 0
  const host = mount({ getRecruitmentJoiningCandidates: async () => ({ ok: true, data: structuredClone(rows) }), importRecruitmentJoiningCandidates: () => new Promise(resolve => { release = () => resolve({ ok: true, data: { imported: 1, alreadyImported: 0, review: 0, items: [{ rowKey: 'offer:1', offerId: 1, status: 'Imported', employeeId: 100 }] } }) }) }, { onImported: async () => { refreshed++ } })
  host.button('Import selected from Talent Acquisition').props.onClick(); await host.settle()
  host.button('Import selected (3)').props.onClick(); host.render()
  host.render({ clientId: 99, clientName: 'Other client' }); release(); await host.settle()
  assert.equal(host.modal().props.open, false); assert.deepEqual(host.table().props.rows, []); assert.deepEqual(host.alerts(), [])
  assert.equal(refreshed, 1, 'committed import still refreshes Employee Master')
})

test('joining transport uses dedicated scoped paths and retains mixed-source acceptance IDs', async () => {
  const requests = [], exports = {}
  const source = readFileSync(new URL('../src/services/recruitmentJoiningService.ts', import.meta.url), 'utf8')
  new Function('exports', 'require', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports, () => ({ getJsonResult: async (...args) => { requests.push(['GET', ...args]); return { ok: true, data: [] } }, postJson: async (...args) => { requests.push(['POST', ...args]); return { ok: true, data: {} } } }))
  await exports.getRecruitmentJoiningCandidates(42)
  await exports.acceptRecruitmentJoiningOffers({ clientId: 42, offerIds: [1], rosterRowIds: [91] })
  await exports.importRecruitmentJoiningCandidates({ clientId: 42, offerIds: [1] })
  await exports.getRecruitmentJoiningCandidates(42, true)
  await exports.importRecruitmentJoiningCandidates({ clientId: 42, offerIds: [1], importReleasedForEss: true })
  assert.equal(requests[0][1], '/api/recruitment/joining-candidates?clientId=42')
  assert.equal(requests[1][1], '/api/recruitment/offers/accept-selected'); assert.deepEqual(requests[1][2].rosterRowIds, [91])
  assert.equal(requests[2][1], '/api/recruitment/joining-candidates/import')
  assert.equal(requests[3][1], '/api/recruitment/joining-candidates?clientId=42&importReleasedForEss=true')
  assert.equal(requests[4][2].importReleasedForEss, true)
})

test('current released letters import directly without an acceptance write and carry successful employees to ESS user creation', async () => {
  const calls = [], released = row(21, { offerStatus: 'Released', acceptanceRecorded: false })
  const host = mount({
    getRecruitmentJoiningCandidates: async (...args) => { calls.push(['GET', ...args]); return { ok: true, data: [released] } },
    importRecruitmentJoiningCandidates: async selection => { calls.push(['IMPORT', selection]); return { ok: true, data: { imported: 1, alreadyImported: 0, review: 0, items: [{ rowKey: 'offer:21', sourceType: 'Offer', offerId: 21, status: 'Imported', employeeId: 821, employeeCode: 'EX-21', message: 'Created for ESS' }] } } },
    acceptRecruitmentJoiningOffers: async () => { throw Error('Acceptance must remain separate') },
  })
  host.button('Import selected from Talent Acquisition').props.onClick(); await host.settle()
  assert.deepEqual(calls, [['GET', 42, true]])
  assert.deepEqual(host.table().props.rowSelection.selectedRowKeys, ['offer:21'])
  host.button('Import selected (1)').props.onClick(); await host.settle()
  assert.deepEqual(calls[1], ['IMPORT', { clientId: 42, importReleasedForEss: true, offerIds: [21], rosterRowIds: [] }])
  assert.equal(released.acceptanceRecorded, false)
  assert.deepEqual(onboarding.readOnboardingSelection(sessionStorage, host.actorId, 42, 'employees'), ['821'])
  assert.equal(onboarding.readOnboardingSelection(sessionStorage, host.actorId, 99, 'employees'), null)
  host.button('Create ESS logins for these employees').props.onClick()
  assert.deepEqual(host.navigation, ['/security/users?clientId=42&provision=1'])
})
