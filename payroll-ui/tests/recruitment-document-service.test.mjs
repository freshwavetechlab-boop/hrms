import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const calls = [], downloads = [], responses = []
const api = {
  apiRequest: async (path, options) => { calls.push({ path, options }); return responses.shift() || new Response('file', { headers: { 'content-disposition': 'attachment; filename="test.pdf"' } }) },
  getJsonResult: (...args) => { calls.push({ kind: 'get', args }); return Promise.resolve({ ok: true }) },
  postJson: (...args) => { calls.push({ kind: 'post', args }); return Promise.resolve({ ok: true }) },
  deleteJson: (...args) => { calls.push({ kind: 'delete', args }); return Promise.resolve({ ok: true }) },
  putJson: (...args) => { calls.push({ kind: 'put', args }); return Promise.resolve({ ok: true }) },
  readError: async response => (await response.json()).message,
}
const exports = {}
const operations = {}
const operationsCode = ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentRosterOperations.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', operationsCode)(operations)
const code = ts.transpileModule(readFileSync(new URL('../src/services/recruitmentDocumentService.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', 'require', 'document', 'window', 'URL', code)(exports, path => path.includes('recruitmentRosterOperations') ? operations : api, { createElement: () => { const link = { click: () => downloads.push({ name: link.download, href: link.href }) }; return link } }, { setTimeout: callback => callback() }, { createObjectURL: () => 'blob:fixture', revokeObjectURL: () => {} })

test('editing a linked offer date sends only configured changes and keeps the original revision', async () => {
  const row = { id: 3, revision: 'original-revision', workLocationId: 11, values: { joiningDate: '2026-11-03', monthlyBasic: '26000', candidateName: 'Test Person', jobLocation: 'Master Institute', workLocationCity: 'Master City' } }
  await exports.editDocumentRoster(55, row, { ...row.values, joiningDate: '2026-11-02', instituteAddress: 'Master Address' }, [{ key: 'joiningDate' }, { key: 'monthlyBasic' }, { key: 'candidateName' }, { key: 'jobLocation' }])
  const request = calls.pop()
  assert.equal(request.args[0], '/api/recruitment/document-roster/rows/3?clientId=55')
  assert.deepEqual(request.args[1], { values: { joiningDate: '2026-11-02' }, revision: 'original-revision' })
  assert.equal(row.values.joiningDate, '2026-11-03', 'the original read model remains unchanged until save succeeds')
})

test('bulk master linking sends selected row revisions and the explicit client scope', async () => {
  await exports.linkRosterWorkLocation(55, [{ id: 1, revision: 'a' }, { id: 3, revision: 'c' }], 11)
  const request = calls.pop()
  assert.equal(request.args[0], '/api/recruitment/document-roster/rows/work-location?clientId=55')
  assert.deepEqual(request.args[1], { ids: [1, 3], workLocationId: 11, revisions: { 1: 'a', 3: 'c' } })
})

test('release carries explicit signature choice for single PDF and selected ZIP while draft route remains unsigned', async () => {
  await exports.releaseRosterLetters(55, [1], false)
  assert.equal(calls.pop().path, '/api/recruitment/document-roster/rows/1/release?clientId=55&applySignature=false')
  await exports.releaseRosterLetters(55, [1, 2], true)
  const bulk = calls.pop()
  assert.equal(bulk.path, '/api/recruitment/document-roster/releases?clientId=55')
  assert.deepEqual(JSON.parse(bulk.options.body), { ids: [1, 2], applySignature: true })
  await exports.downloadRosterDrafts(55, [1])
  assert.equal(calls.pop().path, '/api/recruitment/document-roster/rows/1/draft?clientId=55')
  assert.equal(downloads.length, 3)
})

test('signing metadata, upload and removal remain client scoped, and rejected releases do not download', async () => {
  await exports.getRosterSigningAsset(55)
  assert.equal(calls.pop().args[0], '/api/recruitment/document-roster/signing-asset?clientId=55')
  responses.push(new Response(JSON.stringify({ available: true, fileName: 'synthetic.png' })))
  const file = new File(['synthetic'], 'synthetic.png', { type: 'image/png' })
  assert.equal((await exports.uploadRosterSigningAsset(55, file)).available, true)
  const upload = calls.pop()
  assert.equal(upload.path, '/api/recruitment/document-roster/signing-asset?clientId=55')
  assert.equal(upload.options.body.get('file').name, 'synthetic.png')
  await exports.removeRosterSigningAsset(55)
  assert.equal(calls.pop().args[0], '/api/recruitment/document-roster/signing-asset?clientId=55')
  responses.push(new Response(JSON.stringify({ message: 'Not permitted' }), { status: 403 }))
  const count = downloads.length
  await assert.rejects(() => exports.releaseRosterLetters(55, [1], true), /Not permitted/)
  assert.equal(downloads.length, count)
})
