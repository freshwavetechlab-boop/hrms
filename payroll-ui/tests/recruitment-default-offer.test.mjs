import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function service(read, ensure) {
  const api = { getJsonResult: read, postEmpty: ensure }
  const exports = {}
  const code = ts.transpileModule(readFileSync(new URL('../src/services/recruitmentDocumentService.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, () => api)
  return exports
}
const ok = data => ({ ok: true, data, error: '', status: 200 })

test('opening an unconfigured client prepares its default using a scoped POST', async () => {
  const calls = []
  const templates = [{ id: 9, templateName: 'Default offer', clientId: 81 }]
  const api = service(async path => { calls.push(['GET', path]); return ok([]) }, async (path, fallback, options) => { calls.push(['POST', path]); assert.deepEqual(fallback, []); assert.equal(options.loader, false); return ok(templates) })
  assert.deepEqual((await api.prepareDocumentTemplates(81, 'user-a')).data, templates)
  assert.deepEqual(calls, [['GET', '/api/recruitment/document-roster/templates?clientId=81'], ['POST', '/api/recruitment/document-roster/templates/ensure?clientId=81']])
})

test('an existing client-specific or global layout is reused without provisioning', async () => {
  for (const clientId of [55, 0]) {
    const templates = [{ id: 30, clientId, templateName: 'Existing approved layout' }]
    const api = service(async () => ok(templates), () => assert.fail('must preserve the existing template'))
    assert.deepEqual((await api.prepareDocumentTemplates(55, 'user-a')).data, templates)
  }
})

test('an unauthorized or failed read is surfaced and never treated as a missing template', async () => {
  const denied = { ok: false, data: [], error: 'Not permitted', status: 403 }
  const api = service(async () => denied, () => assert.fail('must not provision after denied read'))
  assert.equal(await api.prepareDocumentTemplates(99, 'restricted'), denied)
})

test('concurrent opens share one initialization but later opens recheck current templates', async () => {
  let reads = 0, writes = 0, finish
  const pending = new Promise(resolve => { finish = resolve })
  const api = service(async () => { reads++; return ok([]) }, async () => { writes++; return pending })
  const first = api.prepareDocumentTemplates(81, 'user-a')
  const duplicate = api.prepareDocumentTemplates(81, 'user-a')
  assert.equal(first, duplicate)
  await Promise.resolve()
  finish(ok([{ id: 9 }]))
  await first
  assert.equal(reads, 1)
  assert.equal(writes, 1)
  await api.prepareDocumentTemplates(81, 'user-a')
  assert.equal(reads, 2, 'completed results must not become a stale template cache')
})

test('different clients and access scopes do not share in-flight responses', async () => {
  const waiting = []
  const api = service(path => new Promise(resolve => waiting.push({ path, resolve })), () => assert.fail('existing templates need no write'))
  const first = api.prepareDocumentTemplates(81, 'user-a')
  const second = api.prepareDocumentTemplates(82, 'user-a')
  const changedUser = api.prepareDocumentTemplates(81, 'user-b')
  assert.notEqual(first, second)
  assert.notEqual(first, changedUser)
  waiting.forEach((entry, index) => entry.resolve(ok([{ id: index + 1 }])))
  assert.deepEqual((await Promise.all([first, second, changedUser])).map(r => r.data[0].id), [1, 2, 3])
})

test('failed provisioning can be retried instead of hiding the bulk workspace permanently', async () => {
  let attempt = 0
  const api = service(async () => ok([]), async () => ++attempt === 1 ? { ok: false, data: [], error: 'Temporarily unavailable', status: 503 } : ok([{ id: 9 }]))
  assert.equal((await api.prepareDocumentTemplates(81, 'user-a')).ok, false)
  assert.equal((await api.prepareDocumentTemplates(81, 'user-a')).data[0].id, 9)
  assert.equal(attempt, 2)
})
