import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = process.env.HRMS_SOURCE_ROOT || fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const code = ts.transpileModule(fs.readFileSync(path.join(root, 'payroll-ui/src/services/excelPayslipLifecycleService.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const hash = 'a'.repeat(64), hash2 = 'b'.repeat(64), id = 'c'.repeat(32), id2 = 'd'.repeat(32)
const ok = data => ({ ok: true, data, status: 200 })
const source = () => ({ captureVersion: 1, workbookFiles: [{ workbookKey: 'wb_' + hash, sha256: hash, fileName: 'synthetic.csv', originalId: '' }] })
function fixture(upload) {
  const calls = [], mod = { exports: {} }
  const api = { postForm: async (url, form) => { calls.push({ kind: 'upload', url, file: form.get('file') }); return upload ? upload(form.get('file')) : ok({ id, fileName: 'synthetic.csv', sha256: hash }) } }
  new Function('require', 'module', 'exports', code)(name => name === './apiClient' ? api : { saveExcelPayslipBatch: async (clientId, batch) => { calls.push({ kind: 'save', clientId, batch }); return ok(batch) } }, mod, mod.exports)
  return { service: mod.exports, calls }
}
const file = new File(['Code,Days\nSynthetic-1,28\n'], 'synthetic.csv', { type: 'text/csv' }), checks = []
{
  const f = fixture(), result = await f.service.saveBatchWithOriginalWorkbooks(42, { calculationSource: source() }, [file], new Map(), () => true)
  assert.equal(result.ok, true); assert.equal(f.calls[1].batch.calculationSource.workbookFiles[0].originalId, id)
  assert.equal(await f.calls[0].file.text(), await file.text())
  checks.push('Retained hash binds exact supplied bytes through existing attachment API before saving')
}
{
  const f = fixture(() => ok({ id, fileName: file.name, sha256: hash2 })), result = await f.service.saveBatchWithOriginalWorkbooks(42, { calculationSource: source() }, [file], new Map(), () => true)
  assert.equal(result.ok, false); assert.equal(f.calls.filter(c => c.kind === 'save').length, 0)
  checks.push('Attachment hash mismatch prevents batch save; filename alone cannot establish provenance')
}
{
  const f = fixture(() => ({ ok: false, error: 'Synthetic storage failure' })), result = await f.service.saveBatchWithOriginalWorkbooks(42, { calculationSource: source() }, [file], new Map(), () => true)
  assert.equal(result.ok, false); assert.equal(f.calls.filter(c => c.kind === 'save').length, 0)
  checks.push('Retention failure never produces successful untracked batch')
}
{
  const f = fixture(), s = source(); s.workbookFiles[0].originalId = id
  s.workbookFiles.push({ workbookKey: 'wb_' + hash2, sha256: hash2, fileName: 'dependency.csv', originalId: '' })
  const dependency = new File(['Synthetic,27'], 'dependency.csv'), cache = new Map([[dependency, { clientId: 42, reference: { id: id2, fileName: dependency.name }, sha256: hash2 }]])
  const result = await f.service.saveBatchWithOriginalWorkbooks(42, { parentBatchId: 'e'.repeat(32), calculationSource: s }, [dependency], cache, () => true)
  assert.equal(result.ok, true); assert.equal(f.calls[0].kind, 'save'); assert.equal(f.calls[0].batch.calculationSource.workbookFiles[1].originalId, id2)
  assert.deepEqual(f.calls[0].batch.originalWorkbooks, [{ id: id2, fileName: dependency.name }])
  checks.push('Derived version inherits prior originals and supplies new explicit dependency references without duplicate upload')
}
{
  const f = fixture(), s = source(); s.workbookFiles[0].originalId = id
  assert.equal((await f.service.saveBatchWithOriginalWorkbooks(42, { parentBatchId: id2, calculationSource: s }, [], new Map(), () => true)).ok, true)
  assert.equal(f.calls[0].kind, 'save')
  const other = fixture(), cache = new Map([[file, { clientId: 99, reference: { id: id2, fileName: file.name }, sha256: hash }]])
  await other.service.saveBatchWithOriginalWorkbooks(42, { calculationSource: source() }, [file], cache, () => true)
  assert.equal(other.calls[0].kind, 'upload'); assert.equal(other.calls[1].batch.calculationSource.workbookFiles[0].originalId, id)
  checks.push('Reopen uses inherited bindings; cache is scoped to current client; server remains ownership authority')
}
{
  let current = true
  const f = fixture(() => { current = false; return ok({ id, fileName: file.name, sha256: hash }) })
  assert.equal((await f.service.saveBatchWithOriginalWorkbooks(42, { calculationSource: source() }, [file], new Map(), () => current)).ok, false)
  assert.equal(f.calls.filter(c => c.kind === 'save').length, 0)
  checks.push('Scope change during retention prevents stale batch save')
}
console.log(JSON.stringify({ passed: checks.length, checks }, null, 2))
