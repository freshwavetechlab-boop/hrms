import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const requests = [], responses = []
const api = {
  postJson: async (path, body) => { requests.push({ path, body }); return responses.shift() || { ok: true, data: { ...body, clientId: 7 }, status: 200, error: '' } },
  postForm: async (path) => { requests.push({ path }); return { ok: true, data: { id: 'original', fileName: 'Synthetic.xlsx', sha256: 'hash' } } },
}
function load(path, dependencies) {
  const exports = {}
  const code = ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, dependencies)
  return exports
}
const service = load('../src/services/excelPayslipService.ts', () => api)
const lifecycle = load('../src/services/excelPayslipLifecycleService.ts', path => path === './excelPayslipService' ? service : api)
const columns = [{ columnIndex: 0, sourceHeader: 'CTC', label: 'CTC', kind: 'info' }]
const input = () => ({ id: 'selected', parentBatchId: 'selected', month: '2026-10', rows: [{ id: 'row', netPay: 9000.1234 }], calculationSource: { columns }, mappingProfile: { headerSignature: 'a'.repeat(64), columns } })

test('one batch request carries the selected snapshot and reviewed mapping in the same client scope', async () => {
  requests.length = 0
  const batch = input(), before = structuredClone(batch)
  const result = await lifecycle.saveBatchWithOriginalWorkbooks(7, batch, [], new Map(), () => true)
  assert.equal(result.ok, true)
  assert.equal(requests.length, 1)
  assert.equal(requests[0].path, '/api/excel-payslips/batches?clientId=7')
  assert.deepEqual(requests[0].body.mappingProfile, batch.mappingProfile)
  assert.deepEqual(requests[0].body.calculationSource.columns, batch.mappingProfile.columns)
  assert.equal(requests[0].body.rows[0].netPay, before.rows[0].netPay)
  assert.deepEqual(batch, before)
})

test('failed atomic save reports failure and a retry retains the exact edited mapping and amounts', async () => {
  requests.length = 0
  const batch = input()
  responses.push({ ok: false, data: null, status: 400, error: 'Mapping storage failed' })
  const result = await lifecycle.saveBatchWithOriginalWorkbooks(7, batch, [], new Map(), () => true)
  assert.equal(result.ok, false)
  assert.equal(result.error, 'Mapping storage failed')
  assert.equal(requests.length, 1)
  assert.equal((await lifecycle.saveBatchWithOriginalWorkbooks(7, batch, [], new Map(), () => true)).ok, true)
  assert.deepEqual(requests[1].body, requests[0].body)
  assert.ok(requests.every(request => !request.path.includes('/profile')))
})

test('new import retains the original once and sends both mapping and captured workbook binding on save', async () => {
  requests.length = 0
  const batch = { ...input(), parentBatchId: undefined, createLocationBatch: true }
  batch.calculationSource = { ...batch.calculationSource, captureVersion: 1, workbookFiles: [{ key: 'book', sha256: 'hash' }] }
  const file = new File(['synthetic only'], 'Synthetic.xlsx'), cache = new Map()
  await lifecycle.saveBatchWithOriginalWorkbooks(7, batch, [file], cache, () => true)
  await lifecycle.saveBatchWithOriginalWorkbooks(7, batch, [file], cache, () => true)
  assert.equal(requests.filter(request => request.path.includes('/originals?')).length, 1)
  const saves = requests.filter(request => request.path.includes('/batches?'))
  assert.equal(saves.length, 2)
  assert.equal(saves[0].body.calculationSource.workbookFiles[0].originalId, 'original')
  assert.deepEqual(saves[0].body.mappingProfile, batch.mappingProfile)
})

test('a changed client or batch scope does not issue a save for retained edits', async () => {
  requests.length = 0
  const result = await lifecycle.saveBatchWithOriginalWorkbooks(8, input(), [], new Map(), () => false)
  assert.equal(result.ok, false)
  assert.equal(requests.length, 0)
})
