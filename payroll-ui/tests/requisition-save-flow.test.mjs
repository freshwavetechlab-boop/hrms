import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/RecruitmentRequisitionManager.tsx', import.meta.url), 'utf8')
const helpers = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/requisitionOperationError.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(helpers)
const functions = source.slice(source.indexOf('  function reportSaveFailure('), source.indexOf('  async function openSubmissionReview('))
  + source.slice(source.indexOf('  async function saveRequest('), source.indexOf('  async function submitSelectedRequest('))

function harness(overrides = {}) {
  let values = { id: 123, clientId: 55, positionTitle: 'Synthetic role', sourceParsedJson: 'PRIVATE_SOURCE_SENTINEL', workOrderReview: { clientId: 55, receivedAtUtc: '2026-11-23T10:00:00Z', workOrderNumber: 'SYNTHETIC-WO' } }
  const state = { dialog: true }, calls = { save: 0, upload: 0, submit: 0, snapshots: [], changed: [] }
  const record = { ...values, workOrderReview: undefined, rfrNumber: 'RFR-SYN-2026-00065', isReplacement: false, budgetAvailable: false, status: 'Draft' }
  const setters = ['SaveError', 'SaveFailure', 'Saving', 'ActiveRequest', 'WatchedForm', 'ReviewDraft', 'JdOpen', 'EmbeddedJdRefreshKey', 'SourceUploadProgress', 'SourceFile', 'AttachmentRefresh', 'DialogOpen']
  const context = vm.createContext({
    Error, Number, Date, ...helpers,
    saving: false, sourceParsing: false, sourcePrefillLoading: false, requesterUnresolved: false,
    saveInFlight: { current: false }, watchedForm: { id: 123 }, embeddedJdRef: { current: null }, activeRequest: record,
    session: { user: { id: 81 } }, activeBrowserDraftKey: { current: 'old-browser-draft' }, sourceFileRef: { current: null },
    window: { localStorage: { removeItem() {} } }, message: { error() {}, warning() {}, success() {} },
    canCreateHiringRequests: () => true, canEditHiringRequest: () => true,
    form: { validateFields: async () => {}, getFieldsValue: () => values, setFieldsValue: next => { values = { ...values, ...next } } },
    persistBrowserDraft: snapshot => { calls.snapshots.push(JSON.parse(JSON.stringify(snapshot || values))); return true },
    checkTextLengths: () => true, normalize: value => value, fromRow: row => row,
    saveRecruitmentRequisition: async () => { calls.save++; return { ok: true, data: record, status: 200, error: '' } },
    getEffectiveAttachmentConfigurationsResult: async () => ({ ok: true, data: [{ id: 47, attributeCode: 'RECRUITMENT_REQUEST_SOURCE' }], status: 200 }),
    uploadEntityAttachment: async () => { calls.upload++; return { ok: true, status: 200 } },
    submitRecruitmentRequisition: async () => { calls.submit++; return { ok: true, data: { ...record, status: 'Pending Approval' }, status: 200 } },
    onChanged: row => calls.changed.push(row), refreshRows: async () => ({ ok: true, data: [], status: 200 }), applyDraft: next => { values = { ...values, ...next } },
    clearBrowserDraft() {}, submissionSuccess: () => 'Submitted',
    ...Object.fromEntries(setters.map(name => ['set' + name, value => { state[name] = typeof value === 'function' ? value(state[name]) : value }])),
    ...overrides,
  })
  vm.runInContext(ts.transpileModule(functions, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, context)
  return { context, state, calls, record, values: () => values }
}

test('an unconfirmed request save retains fields/source and never proceeds to upload or submission', async () => {
  const h = harness({ saveRecruitmentRequisition: async () => ({ ok: false, data: null, status: 0, error: 'Failed to fetch' }) })
  h.context.sourceFileRef.current = { name: 'synthetic.pdf' }
  await h.context.saveRequest(true)
  assert.match(h.state.SaveFailure.title, /save was not confirmed/)
  assert.equal(h.values().sourceParsedJson, 'PRIVATE_SOURCE_SENTINEL')
  assert.equal(h.context.sourceFileRef.current.name, 'synthetic.pdf')
  assert.equal(h.calls.upload, 0); assert.equal(h.calls.submit, 0)
  assert.equal(h.context.saveInFlight.current, false)
  assert.doesNotMatch(JSON.stringify(h.state.SaveFailure), /PRIVATE_SOURCE_SENTINEL/)
})

test('an attachment failure keeps the confirmed request ID and reviewed fields without a submission', async () => {
  const h = harness({ uploadEntityAttachment: async () => ({ ok: false, status: 400, error: 'File exceeds 10 MB.' }) })
  h.context.sourceFileRef.current = { name: 'synthetic.pdf' }
  await h.context.saveRequest(true)
  assert.equal(h.values().id, 123)
  assert.equal(h.values().workOrderReview.workOrderNumber, 'SYNTHETIC-WO')
  assert.equal(h.context.sourceFileRef.current.name, 'synthetic.pdf')
  assert.match(h.state.SaveFailure.title, /Request saved; source/)
  assert.match(h.state.SaveError, /File exceeds 10 MB/)
  assert.equal(h.calls.submit, 0)
  assert.match(h.context.activeBrowserDraftKey.current, /request:81:123$/)
})

test('a source-configuration network failure is not misreported as missing configuration', async () => {
  const h = harness({ getEffectiveAttachmentConfigurationsResult: async () => ({ ok: false, status: 0, error: 'Failed to fetch', data: [] }) })
  h.context.sourceFileRef.current = { name: 'synthetic.pdf' }
  await h.context.saveRequest(true)
  assert.match(h.state.SaveFailure.diagnostics, /Source attachment setup.*No HTTP response/)
  assert.doesNotMatch(h.state.SaveError, /not configured/)
  assert.equal(h.calls.submit, 0)
})

test('linked JD failure stops the request mutation and keeps its actual status/reference', async () => {
  const h = harness({ embeddedJdRef: { current: { saveDraftIfNeeded: async () => false, getLastSaveFailure: () => ({ status: 0, error: 'Failed to fetch', requestId: 'syn-reference' }) } } })
  await h.context.saveRequest(false)
  assert.equal(h.calls.save, 0)
  assert.match(h.state.SaveFailure.title, /Linked JD/)
  assert.match(h.state.SaveFailure.diagnostics, /job-descriptions.*syn-reference/)
  assert.equal(h.context.saveInFlight.current, false)
})

test('submission failure keeps the saved draft and does not clear the browser snapshot', async () => {
  const h = harness({ submitRecruitmentRequisition: async () => ({ ok: false, status: 0, error: 'Failed to fetch' }) })
  await h.context.saveRequest(true)
  assert.equal(h.values().id, 123)
  assert.match(h.state.SaveFailure.title, /submission was not confirmed/)
  assert.equal(h.state.DialogOpen, undefined)
  assert.ok(h.calls.snapshots.length)
})

test('a failed post-save list refresh never says the confirmed request failed to save', async () => {
  const h = harness({ refreshRows: async () => { throw new Error('Failed to fetch') } })
  await h.context.saveRequest(false)
  assert.match(h.state.SaveFailure.title, /Request saved; list refresh/)
  assert.equal(h.calls.changed[0].id, 123)
  assert.equal(h.context.saveInFlight.current, false)
})

test('a second Save click cannot start another mutation while the first is waiting for the API', async () => {
  let finish, calls = 0
  const pending = new Promise(resolve => { finish = resolve })
  const h = harness({ saveRecruitmentRequisition: async () => { calls++; return pending } })
  const first = h.context.saveRequest(false)
  await Promise.resolve(); await Promise.resolve()
  await h.context.saveRequest(false)
  assert.equal(calls, 1)
  finish({ ok: true, data: h.record, status: 200 })
  await first
  assert.equal(h.context.saveInFlight.current, false)
})
