import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

// Actual TypeScript service with an in-memory transport. No HTTP, employee data or writes.
const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const source = fs.readFileSync(path.join(root, 'payroll-ui/src/services/excelPayslipOverviewService.ts'), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const calls = [], module = { exports: {} }
let response = { ok: true, data: [], status: 200, error: '' }
const api = Object.freeze({
  getJsonResult: async (...args) => { calls.push(['GET', ...args]); return response },
  postJson: async (...args) => { calls.push(['POST', ...args]); return response },
})
new Function('require', 'module', 'exports', compiled)(name => {
  assert.equal(name, './apiClient'); return api
}, module, module.exports)
const service = module.exports
await service.getOverviewScopes()
assert.deepEqual(calls.pop(), ['GET', '/api/excel-payslips/overview-scopes', [], { toast: false, timeoutMessage: 'Saved batch list took too long to load. Retry the client/batch list.' }])
const catalogAbort = new AbortController()
await service.getOverviewScopes(catalogAbort.signal)
assert.equal(calls.pop()[3].signal, catalogAbort.signal, 'Closing the catalog must be able to cancel its own request')
const request = Object.freeze({ scopes: Object.freeze([{ clientId: 42, batchId: 'a'.repeat(32) }, { clientId: 43, batchId: 'b'.repeat(32) }]), search: 'Synthetic', readiness: 'Review', masterStatus: 'Conflict', page: 3, pageSize: 25 })
const abort = new AbortController()
await service.previewOverview(request, abort.signal)
let call = calls.pop()
assert.equal(call[1], '/api/excel-payslips/overview-preview')
assert.equal(call[2], request)
assert.equal(call[4].signal, abort.signal)
assert.equal(call[4].loader, false)
assert.equal(call[4].toast, false)
response = { ok: false, data: null, status: 403, error: 'Selected client is forbidden' }
assert.equal(await service.previewOverview(request), response)
assert.equal(calls.length, 1, 'A failure must not retry using a broader client/history scope')
calls.length = 0
const empty = { ...request, scopes: [] }
await service.previewOverview(empty)
assert.equal(calls[0][2], empty, 'An empty selection must not invent active/latest versions')
assert.equal(calls.length, 1)
assert.deepEqual(Object.keys(service).sort(), ['getOverviewScopes', 'previewOverview'])
console.log('PASS: 6 overview transport checks — catalog cancellation, explicit scope/filter/page fidelity, cancellation, access failure without broader retry, empty scope without defaults, read-only API surface.')
