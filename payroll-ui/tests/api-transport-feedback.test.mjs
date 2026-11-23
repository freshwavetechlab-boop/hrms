import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function load(file, imports = {}, transform = source => source) {
  const exports = {}
  const source = transform(readFileSync(new URL('../src/' + file + '.ts', import.meta.url), 'utf8'))
  new Function('exports', 'require', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports, name => {
    assert.ok(name in imports, `Missing dependency ${name}`)
    return imports[name]
  })
  return exports
}
const feedback = load('utils/apiTransportFeedback')
const notifications = [], successes = [], events = [], scopes = []
const api = load('services/apiClient', {
  '../components/ToastProvider': { toast: { error: value => notifications.push(value), success: value => successes.push(value) } },
  './apiBaseUrl': { resolveRuntimeApiBase: () => 'https://synthetic.invalid' },
  './recruitmentDeleteFeedback': { recruitmentDeleteActions: () => [] },
  './clientRequestScope': { activeClientHeader: () => '55', beginClientScopedWrite: () => () => {}, configureClientRequestScope: value => scopes.push(value) },
  '../utils/httpErrorMessage': load('utils/httpErrorMessage'),
  '../utils/apiTransportFeedback': feedback,
}, source => source.replaceAll('import.meta.env', '({ VITE_API_URL: "", DEV: false })'))

function browser() {
  const original = { window: globalThis.window, sessionStorage: globalThis.sessionStorage, localStorage: globalThis.localStorage, fetch: globalThis.fetch, XMLHttpRequest: globalThis.XMLHttpRequest }
  globalThis.window = { setTimeout, clearTimeout, dispatchEvent: event => events.push(event.type) }
  globalThis.sessionStorage = globalThis.localStorage = { getItem: () => null, removeItem() {} }
  notifications.length = 0; successes.length = 0; events.length = 0; scopes.length = 0
  return () => Object.assign(globalThis, original)
}

test('network failure never claims a mutation failed or blindly retries, and read failures are actionable', async () => {
  const restore = browser()
  let calls = 0
  globalThis.fetch = async () => { calls++; throw new TypeError('Failed to fetch') }
  try {
    const result = await api.postJson('/api/recruitment/requisitions', { id: 45 }, null)
    assert.equal(result.status, 0); assert.equal(result.ok, false); assert.equal(calls, 1)
    assert.match(result.error, /did not receive a readable response/)
    assert.match(result.error, /may have completed.*before retrying/)
    assert.doesNotMatch(result.error, /Failed to fetch/)
    assert.equal('requestId' in result, false, 'no diagnostic ID is invented without a response')
    assert.equal(successes.length, 0)
    globalThis.fetch = async () => { throw new TypeError('Load failed') }
    const loaded = await api.getJsonResult('/api/recruitment/requisitions', [])
    assert.equal(loaded.status, 0); assert.match(loaded.error, /connection.*loading/)
    assert.doesNotMatch(loaded.error, /may have completed/)
  } finally { restore() }
})

test('malformed successful save response retains HTTP status and server request ID without a success toast', async () => {
  const restore = browser()
  globalThis.fetch = async () => new Response('<html>unreadable confirmation</html>', { status: 200, headers: { 'X-Request-ID': 'trace-45:001' } })
  try {
    const result = await api.postJson('/api/recruitment/requisitions', { id: 45 }, null, { successMessage: 'Saved.' })
    assert.equal(result.ok, false); assert.equal(result.status, 200); assert.equal(result.requestId, 'trace-45:001')
    assert.match(result.error, /HTTP 200.*could not be read/)
    assert.match(result.error, /final status is unknown.*before retrying/)
    assert.doesNotMatch(result.error, /Unexpected token|<html>/)
    assert.equal(successes.length, 0)
    assert.equal(events.includes('hrms:actions-changed'), false)
    assert.equal(notifications.at(-1), result.error)
    const read = await api.getJsonResult('/api/recruitment/requisitions', [])
    assert.equal(read.status, 200); assert.equal(read.requestId, 'trace-45:001')
    assert.match(read.error, /loading the record again/)
  } finally { restore() }
})

test('received responses preserve exact validation, access handling, and safe correlation headers', async () => {
  const restore = browser()
  try {
    globalThis.fetch = async () => new Response(JSON.stringify({ error: 'Hiring budget cannot be negative.' }), { status: 400, headers: { 'X-Request-ID': 'bad id', 'X-Correlation-ID': 'corr_55.45' } })
    const invalid = await api.postJson('/api/recruitment/requisitions', {}, null)
    assert.equal(invalid.status, 400); assert.equal(invalid.error, 'Hiring budget cannot be negative.'); assert.equal(invalid.requestId, 'corr_55.45')
    globalThis.fetch = async () => new Response(JSON.stringify({ error: 'Outside your permitted scope.' }), { status: 403 })
    assert.equal((await api.postJson('/api/recruitment/requisitions', {}, null)).error, 'Outside your permitted scope.')
    assert.ok(events.includes('payroll:client-access-refresh'))
    globalThis.fetch = async () => new Response(JSON.stringify({ error: 'Authentication is required.' }), { status: 401 })
    await api.postJson('/api/recruitment/requisitions', {}, null)
    assert.deepEqual(scopes, [null]); assert.ok(events.includes('payroll:unauthorized'))
    globalThis.fetch = async () => new Response('{"id":45}', { status: 200, headers: { 'X-Request-ID': 'req45' } })
    assert.deepEqual(await api.getJsonResult('/api/recruitment/requisitions/45', null), { ok: true, data: { id: 45 }, error: '', status: 200, requestId: 'req45' })
  } finally { restore() }
})

test('only bounded safe request identifiers from actual headers are captured', () => {
  for (const invalid of ['a'.repeat(129), 'token/value', '<script>', 'trace\npoison', ''])
    assert.equal(feedback.serverRequestId(name => name === 'X-Request-ID' ? invalid : null), undefined)
  assert.equal(feedback.serverRequestId(name => name === 'X-Request-ID' ? 'bad value' : 'safe:correlation-1'), 'safe:correlation-1')
  assert.equal(feedback.serverRequestId(() => { throw new Error('header unavailable') }), undefined)
})

test('custom timeouts and explicit cancellation remain distinct from network failures', async () => {
  const restore = browser()
  globalThis.fetch = async () => { throw new DOMException('Aborted', 'AbortError') }
  try {
    assert.equal((await api.postJson('/api/recruitment/requisitions', {}, null, { timeoutMessage: 'Configured save timeout.' })).error, 'Configured save timeout.')
    assert.equal((await api.getJsonResult('/api/recruitment/requisitions', [], { timeoutMessage: 'Configured read timeout.' })).error, 'Configured read timeout.')
    const controller = new AbortController(); controller.abort()
    assert.equal((await api.getJsonResult('/api/recruitment/requisitions', [], { signal: controller.signal })).error, 'Request was cancelled.')
  } finally { restore() }
})

test('XHR upload malformed confirmation and network errors retain unknown outcome and never lose the selected file', async () => {
  const restore = browser()
  let event = 'load', status = 200, text = '{invalid', calls = 0
  const sent = []
  globalThis.XMLHttpRequest = class {
    upload = {}; status = status; responseText = text
    open() {} setRequestHeader() {}
    getResponseHeader(name) { return name === 'X-Correlation-ID' ? 'upload:45' : null }
    send(body) { calls++; sent.push(body); queueMicrotask(() => { this['on' + event](); this.onloadend() }) }
  }
  try {
    const form = new FormData(), file = new File(['synthetic'], 'proof.pdf'); form.append('file', file)
    const malformed = await api.postFormWithProgress('/api/attachments', form, null, () => {})
    assert.equal(malformed.status, 200); assert.equal(malformed.requestId, 'upload:45'); assert.match(malformed.error, /final status is unknown/)
    assert.equal(successes.length, 0); assert.equal(calls, 1); assert.equal(sent[0].get('file'), file)
    event = 'error'; status = 0; text = ''
    assert.match((await api.postFormWithProgress('/api/attachments', form, null, () => {})).error, /may have completed.*before retrying/)
    event = 'timeout'
    assert.equal((await api.postFormWithProgress('/api/attachments', form, null, () => {}, 5, 'Configured upload timeout.')).error, 'Configured upload timeout.')
    event = 'abort'
    assert.equal((await api.postFormWithProgress('/api/attachments', form, null, () => {})).error, 'Upload was cancelled.')
    assert.equal(sent.every(body => body.get('file') === file), true)
  } finally { restore() }
})

test('a response body read failure is not mislabeled as a missing HTTP response', async () => {
  const restore = browser()
  globalThis.fetch = async () => ({ ok: true, status: 200, headers: new Headers({ 'X-Request-ID': 'body-read-1' }), text: async () => { throw new TypeError('terminated') } })
  try {
    const result = await api.postJson('/api/recruitment/requisitions', {}, null)
    assert.equal(result.status, 200); assert.equal(result.requestId, 'body-read-1'); assert.match(result.error, /server responded/)
  } finally { restore() }
})
