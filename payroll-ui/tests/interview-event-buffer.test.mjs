import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
const context = vm.createContext({ exports: {}, Date, Error, Number, String, JSON, Set })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/interviewEventBuffer.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { InterviewEventBuffer } = context.exports
const id = '00000000-0000-4000-8000-000000000001'
function fixture(send = async () => {}) {
  let now = 1000, message = ''
  const values = new Map(), storage = { getItem: k => values.get(k), setItem: (k,v) => values.set(k,v), removeItem: k => values.delete(k) }
  const options = { key: 'fingerprint', storage, send, now: () => now, uuid: () => id, onStatus: m => { message = m } }
  return { queue: new InterviewEventBuffer(options), options, values, advance: ms => { now += ms }, message: () => message }
}
test('network failure and reload retry the same saved id without answer or token data', async () => {
  const sent = []; let fail = true
  const f = fixture(async e => { sent.push(e); if (fail) throw Error('offline') })
  f.queue.enqueue('blur', 200); f.queue.enqueue('answer'); await f.queue.flush()
  assert.equal(sent.length, 1); assert.match(f.message(), /pending sync/)
  const persisted = f.values.get('fingerprint')
  assert(!/answer|token|text/i.test(persisted))
  f.queue.dispose(); fail = false; f.advance(6000)
  const reloaded = new InterviewEventBuffer(f.options); await reloaded.flush()
  assert.equal(sent[1].eventKey, sent[0].eventKey); assert.equal(f.values.size, 0)
})
test('429 backs off a full minute and duplicate flush calls cannot race', async () => {
  let calls = 0
  const f = fixture(async () => { calls++; throw Object.assign(Error(), { status: 429 }) })
  f.queue.enqueue('focus'); await Promise.all([f.queue.flush(), f.queue.flush()])
  f.advance(59000); await f.queue.flush(); assert.equal(calls, 1)
  f.advance(1001); await f.queue.flush(); assert.equal(calls, 2)
})
test('revoked links and closed sessions drop pending events and stop retrying visibly', async () => {
  for (const status of [401, 403, 404, 409, 410]) {
    let calls = 0; const f = fixture(async () => { calls++; throw { status } })
    f.queue.enqueue('blur'); await f.queue.flush(); f.advance(120000); await f.queue.flush()
    assert.equal(calls, 1); assert.equal(f.values.size, 0); assert.match(f.message(), /could not be confirmed/)
  }
})
test('queue and lifetime are bounded and storage errors preserve memory-only warnings', async () => {
  const f = fixture(); for (let i = 0; i < 101; i++) f.queue.enqueue('paste')
  assert.equal(JSON.parse(f.values.get('fingerprint')).length, 100); assert.match(f.message(), /could not be confirmed/)
  f.advance(4 * 60 * 60 * 1000 + 1); new InterviewEventBuffer(f.options); assert.equal(f.values.size, 0)
  const q = new InterviewEventBuffer({ ...f.options, storage: undefined }); q.enqueue('copy')
  assert.match(f.message(), /Temporary memory only/)
})
test('persisted browser storage is untrusted and arbitrary extra payloads are stripped', () => {
  const f = fixture()
  f.values.set('fingerprint', JSON.stringify([{ eventKey: id, kind: 'blur', capturedAt: 1000, token: 'secret', text: 'clipboard' }, { eventKey: id, kind: 'answer', capturedAt: 1000 }]))
  new InterviewEventBuffer(f.options)
  const persisted = f.values.get('fingerprint'); assert(!/secret|clipboard|answer/.test(persisted)); assert.equal(JSON.parse(persisted).length, 1)
})
