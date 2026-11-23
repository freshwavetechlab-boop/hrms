import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function load(file, imports = {}, transform = source => source) {
  const exports = {}, source = transform(readFileSync(new URL('../src/' + file + '.ts', import.meta.url), 'utf8'))
  const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', compiled)(exports, path => {
    assert.ok(path in imports, `Missing test dependency ${path}`)
    return imports[path]
  })
  return exports
}
const errors = load('utils/httpErrorMessage')
const notifications = [], scopeChanges = []
const api = load('services/apiClient', {
  '../components/ToastProvider': { toast: { error: message => notifications.push(message), success() {} } },
  './apiBaseUrl': { resolveRuntimeApiBase: () => 'https://example.invalid' },
  './recruitmentDeleteFeedback': { recruitmentDeleteActions: () => [] },
  './clientRequestScope': { activeClientHeader: () => '55', beginClientScopedWrite: () => () => {}, configureClientRequestScope: value => scopeChanges.push(value) },
  '../utils/httpErrorMessage': errors,
  '../utils/apiTransportFeedback': load('utils/apiTransportFeedback'),
}, source => source.replaceAll('import.meta.env', '({ VITE_API_URL: "", DEV: false })'))

test('HTTP 413 empty, HTML and malformed bodies use a human size message while numeric API errors remain intact', async () => {
  for (const body of ['', '<html><h1>413 Request Entity Too Large</h1></html>', '{bad JSON']) {
    assert.equal(errors.readHttpErrorText(body, 413), errors.uploadTooLargeMessage)
    assert.equal(await api.readError(new Response(body, { status: 413 })), errors.uploadTooLargeMessage)
  }
  const message = 'Aadhaar Card: maximum file size is 1 MB.'
  for (const key of ['error', 'detail', 'message']) {
    const body = JSON.stringify({ [key]: message })
    assert.equal(errors.readHttpErrorText(body, 400), message)
    assert.equal(await api.readError(new Response(body, { status: 400 })), message)
    assert.equal(await api.readError(new Response(body, { status: 413 })), message)
  }
  assert.equal(errors.readHttpErrorText('plain existing error', 400), 'plain existing error')
  assert.equal(await api.readError(new Response('plain existing error', { status: 400 })), 'Request failed with status 400.')
})

test('actual progress uploads surface friendly proxy errors and keep authentication/access behavior', async () => {
  const originals = { XMLHttpRequest: globalThis.XMLHttpRequest, window: globalThis.window, sessionStorage: globalThis.sessionStorage, localStorage: globalThis.localStorage }
  const removed = [], events = [], requests = []
  let nextStatus = 413, nextText = '<html>Request Entity Too Large</html>'
  globalThis.window = { dispatchEvent: event => events.push(event.type) }
  globalThis.sessionStorage = globalThis.localStorage = { getItem: () => null, removeItem: key => removed.push(key) }
  globalThis.XMLHttpRequest = class {
    upload = {}; headers = {}; status = nextStatus; responseText = nextText
    constructor() { requests.push(this) }
    open(method, url) { this.method = method; this.url = url }
    setRequestHeader(key, value) { this.headers[key] = value }
    send(body) { this.body = body; queueMicrotask(() => { this.onload(); this.onloadend() }) }
  }
  try {
    const form = new FormData(), file = new File(['synthetic'], 'proof.pdf', { type: 'application/pdf' })
    form.append('file', file)
    const tooLarge = await api.postFormWithProgress('/api/attachments', form, null, () => {})
    assert.equal(tooLarge.status, 413); assert.equal(tooLarge.error, errors.uploadTooLargeMessage)
    assert.equal(notifications.at(-1), errors.uploadTooLargeMessage)
    assert.equal(requests.at(-1).body.get('file').name, 'proof.pdf')
    nextStatus = 400; nextText = JSON.stringify({ error: 'Maximum file size: 10 MB.' })
    assert.equal((await api.postFormWithProgress('/api/attachments', form, null, () => {})).error, 'Maximum file size: 10 MB.')
    nextStatus = 401; nextText = ''
    await api.postFormWithProgress('/api/attachments', form, null, () => {})
    assert.deepEqual(scopeChanges, [null]); assert.equal(removed.length, 2); assert.ok(events.includes('payroll:unauthorized'))
    nextStatus = 403
    await api.postFormWithProgress('/api/attachments', form, null, () => {})
    assert.ok(events.includes('payroll:client-access-refresh'))
  } finally { Object.assign(globalThis, originals) }
})

test('the no-login external upload reports non-JSON 413 and retains the supplied files for retry', async () => {
  const service = load('services/publicCandidateDocumentService', { './apiClient': { apiUrl: path => 'https://example.invalid' + path }, '../utils/httpErrorMessage': errors })
  const originals = { fetch: globalThis.fetch, window: globalThis.window }
  const requests = []
  globalThis.window = { setTimeout, clearTimeout }
  globalThis.fetch = async (url, options) => { requests.push(options); return new Response('<html>413</html>', { status: 413 }) }
  try {
    const file = new File(['synthetic'], 'proof.pdf', { type: 'application/pdf' }), files = { proof: [file] }
    const result = await service.submitCandidateDocuments('synthetic-token', 'synthetic@example.invalid', [], files)
    assert.equal(result.error, errors.uploadTooLargeMessage); assert.equal(result.status, 413); assert.equal(result.ok, false)
    assert.equal(files.proof[0], file)
    assert.equal(requests[0].body.get('files-proof').name, 'proof.pdf')
    assert.equal(requests[0].credentials, 'omit')
    assert.deepEqual(requests[0].headers, { Accept: 'application/json' })
    globalThis.fetch = async () => new Response(JSON.stringify({ error: 'Proof: maximum file size is 1 MB.' }), { status: 400 })
    assert.equal((await service.submitCandidateDocuments('synthetic-token', 'synthetic@example.invalid', [], files)).error, 'Proof: maximum file size is 1 MB.')
    globalThis.fetch = async () => new Response(JSON.stringify({ detail: 'Proof: maximum combined size is 10 MB.' }), { status: 400 })
    assert.equal((await service.submitCandidateDocuments('synthetic-token', 'synthetic@example.invalid', [], files)).error, 'Proof: maximum combined size is 10 MB.')
  } finally { Object.assign(globalThis, originals) }
})
