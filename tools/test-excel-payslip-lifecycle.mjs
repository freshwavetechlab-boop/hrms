import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

// Run the actual TypeScript service with in-memory API doubles. No server, DB, browser or original user workbook is used.
const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const source = fs.readFileSync(path.join(root, 'payroll-ui/src/services/excelPayslipLifecycleService.ts'), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const checks = []
const ok = data => ({ ok: true, data, status: 200, error: '' })
const fail = error => ({ ok: false, data: null, status: 503, error })
function fixture(overrides = {}) {
  const calls = [], module = { exports: {} }
  const api = {
    postForm: async (url, form) => { calls.push({ kind: 'upload', url, form }); return ok({ id: (form.get('file').name === 'synthetic.csv' ? 'a' : 'd').repeat(32), fileName: form.get('file').name }) },
    postJson: async (url, body) => { calls.push({ kind: 'delete', url, body }); return ok({ deleted: true, alreadyDeleted: false }) },
    getJsonResult: async url => { calls.push({ kind: 'get', url }); return ok([]) },
    apiRequest: async url => { calls.push({ kind: 'download', url }); return { ok: false } },
    readError: async () => 'Access denied',
    ...overrides,
  }
  const save = { saveExcelPayslipBatch: async (clientId, body) => { calls.push({ kind: 'save', clientId, body }); return ok({ ...body, id: 'b'.repeat(32), clientId }) } }
  const injected = name => name === './apiClient' ? api : name === './excelPayslipService' ? save : assert.fail('Unexpected dependency: ' + name)
  new Function('require', 'module', 'exports', compiled)(injected, module, module.exports)
  return { service: module.exports, calls, api, save }
}
const input = { month: '2026-09', sourceFileName: 'synthetic.csv', sheetName: 'Sheet1', headerRow: 1, rows: [] }
const file = new File(['Employee,Net\nSynthetic Person,100\n'], 'synthetic.csv', { type: 'text/csv' })

{
  const { service, calls } = fixture()
  const result = await service.saveBatchWithOriginalWorkbooks(42, input, [file], new Map(), () => true)
  assert.equal(result.ok, true); assert.deepEqual(calls.map(c => c.kind), ['upload', 'save'])
  assert.equal(await calls[0].form.get('file').text(), await file.text())
  assert.equal(calls[0].url, '/api/excel-payslips/originals?clientId=42')
  assert.deepEqual(calls[1].body.originalWorkbooks, [{ id: 'a'.repeat(32), fileName: 'synthetic.csv' }])
  checks.push('Exact original bytes are submitted before the batch and the retained reference is attached')
}
{
  const { service, calls } = fixture({ postForm: async () => fail('Storage unavailable') })
  const result = await service.saveBatchWithOriginalWorkbooks(42, input, [file], new Map(), () => true)
  assert.equal(result.ok, false); assert.match(result.error, /Storage unavailable/); assert.equal(calls.length, 0)
  checks.push('Storage failure prevents every batch-save request')
}
{
  const { service, calls } = fixture(), cache = new Map()
  let attempted = 0
  const second = new File(['Employee,Net\nSecond Synthetic,200\n'], 'second.csv', { type: 'text/csv' })
  const first = fixture()
  const upload = first.api.postForm
  first.api.postForm = async (...args) => ++attempted === 2 ? fail('Second upload failed') : upload(...args)
  const failed = await first.service.saveBatchWithOriginalWorkbooks(42, input, [file, second], cache, () => true)
  assert.equal(failed.ok, false); assert.equal(first.calls.filter(c => c.kind === 'save').length, 0)
  const retried = await first.service.saveBatchWithOriginalWorkbooks(42, input, [file, second], cache, () => true)
  assert.equal(retried.ok, true); assert.equal(attempted, 3)
  assert.equal(first.calls.filter(c => c.kind === 'save').length, 1)
  // A cached reference is never reused across a different client scope.
  await service.saveBatchWithOriginalWorkbooks(43, input, [file], cache, () => true)
  assert.equal(calls.filter(c => c.kind === 'upload').length, 1)
  assert.equal(calls.find(c => c.kind === 'upload').url, '/api/excel-payslips/originals?clientId=43')
  checks.push('Partial multi-file failure retains retry receipts but saves no partial batch; cache is client scoped')
}
{
  const f = fixture(); let current = true
  f.api.postForm = async () => { current = false; return ok({ id: 'a'.repeat(32), fileName: file.name }) }
  const cache = new Map(), result = await f.service.saveBatchWithOriginalWorkbooks(42, input, [file], cache, () => current)
  assert.equal(result.ok, false); assert.equal(cache.size, 0); assert.equal(f.calls.length, 0)
  checks.push('Client/batch scope changing during upload blocks the subsequent save and stale cache update')
}
{
  const { service, calls, save } = fixture(), cache = new Map()
  save.saveExcelPayslipBatch = async () => fail('Batch save interrupted')
  assert.equal((await service.saveBatchWithOriginalWorkbooks(42, input, [file], cache, () => true)).ok, false)
  save.saveExcelPayslipBatch = async (_clientId, body) => ok(body)
  assert.equal((await service.saveBatchWithOriginalWorkbooks(42, input, [file], cache, () => true)).ok, true)
  assert.equal(calls.filter(c => c.kind === 'upload').length, 1)
  checks.push('Batch-save retry reuses the successfully retained original')
}
{
  const { service, calls } = fixture()
  assert.equal((await service.saveBatchWithOriginalWorkbooks(42, input, [], new Map(), () => true)).ok, false)
  assert.equal(calls.length, 0)
  const derived = { ...input, parentBatchId: 'c'.repeat(32) }
  assert.equal((await service.saveBatchWithOriginalWorkbooks(42, derived, [], new Map(), () => true)).ok, true)
  assert.deepEqual(calls.map(c => c.kind), ['save'])
  assert.equal(calls[0].body.parentBatchId, derived.parentBatchId)
  assert.equal(calls[0].body.originalWorkbooks, undefined)
  checks.push('New imports require bytes; corrections pass the parent for server-side reference inheritance')
}
{
  const { service, calls } = fixture()
  await service.deleteUnusedBatch(42, 'batch/one', 'preview-token', 'batch/one')
  assert.equal(calls[0].url, '/api/excel-payslips/batches/batch%2Fone/delete?clientId=42')
  assert.deepEqual(calls[0].body, { token: 'preview-token', confirmBatchId: 'batch/one' })
  await assert.rejects(() => service.downloadOriginalWorkbook(42, 'batch/one', { id: 'original/one', fileName: 'synthetic.csv' }), /Access denied/)
  assert.equal(calls[1].url, '/api/excel-payslips/batches/batch%2Fone/originals/original%2Fone?clientId=42')
  checks.push('Delete confirmation and raw download stay bound to their client/batch route; denied download fails')
}
{
  const { service, calls } = fixture()
  await service.deleteUnusedBatch(42, 'older-unused', 'fresh-token', 'older-unused', true)
  assert.deepEqual(calls[0].body, { token: 'fresh-token', confirmBatchId: 'older-unused', discardUnusedLegacy: true })
  checks.push('Unreviewed older-version disposal is transmitted only after explicit selection')
}
{
  const { service, calls } = fixture()
  const request = { requestId: 'fixed-synthetic-action', action: 'discard-draft', token: 'fresh-preview-token', confirmBatchId: 'batch/one', objectId: 'draft-one', confirmObjectId: 'draft-one', keeperBatchId: '', acknowledged: true }
  await service.reviewLegacyVersion(42, 'batch/one', 'retained-batch')
  assert.equal(calls[0].url, '/api/excel-payslips/batches/batch%2Fone/cleanup-review?clientId=42')
  assert.deepEqual(calls[0].body, { keeperBatchId: 'retained-batch' })
  await service.resolveCleanupBlocker(42, 'batch/one', request)
  await service.resolveCleanupBlocker(42, 'batch/one', request)
  assert.equal(calls[1].url, '/api/excel-payslips/batches/batch%2Fone/cleanup-action?clientId=42')
  assert.deepEqual(calls[1].body, request); assert.deepEqual(calls[2].body, request)
  assert.deepEqual(calls.map(c => c.kind), ['delete', 'delete', 'delete'])
  checks.push('Legacy review and separately confirmed resolution stay client/batch scoped and preserve request ID/token for explicit retries')
}
console.log(JSON.stringify({ passed: checks.length, checks, realDatabaseWrites: 0, realFileUploads: 0, realDeletes: 0 }, null, 2))
