import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function load(path) {
  const exports = {}
  new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/' + path + '.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
  return exports
}
const { requisitionOperationError: describe } = load('requisitionOperationError')
const { embeddedJdAutosaveKey: autoKey } = load('embeddedJdAutosave')
const record = { id: 123, rfrNumber: 'RFR-SYN-2026-00065' }

test('a missing save response is an uncertain write, with action and safe endpoint rather than raw Failed to fetch', () => {
  const result = describe('save', { status: 0, error: 'Failed to fetch' })
  assert.match(result.title, /not confirmed/)
  assert.match(result.message, /server may have saved/)
  assert.match(result.message, /Do not create another request/)
  assert.doesNotMatch(result.message, /Failed to fetch/)
  assert.match(result.diagnostics, /POST \/api\/recruitment\/requisitions.*No HTTP response/)
})

test('real validation and permission errors retain the API reason/status and correlation reference', () => {
  const result = describe('save', { status: 400, error: 'Openings must be at least 1.', requestId: '00-synthetic-reference' })
  assert.match(result.message, /Openings must be at least 1/)
  assert.match(result.diagnostics, /HTTP 400.*00-synthetic-reference/)
  assert.match(describe('submit', { status: 403, error: 'Approval permission is required.' }, record).message, /Approval permission is required/)
  assert.doesNotMatch(describe('save', { status: 500, requestId: '<script>private payload</script>' }).diagnostics, /script|private/)
})

test('unreadable successful responses and server failures never claim a mutation definitely failed', () => {
  for (const status of [200, 502]) {
    const result = describe('save', { status, error: 'The response did not confirm the result.' })
    assert.match(result.title, /not confirmed/)
    assert.match(result.message, /server may have saved/)
    assert.match(result.diagnostics, new RegExp(`HTTP ${status}`))
  }
})

test('attachment and submission failures identify the already saved request without implying a failed draft save', () => {
  const upload = describe('source-upload', { status: 400, error: 'File exceeds 10 MB.' }, record)
  assert.match(upload.title, /Request saved/)
  assert.match(upload.message, /RFR-SYN-2026-00065 is saved/)
  assert.match(upload.message, /File exceeds 10 MB/)
  assert.match(upload.message, /source file is still selected/)
  assert.match(upload.message, /Submission has not started/)
  const submit = describe('submit', { status: 0, error: 'Failed to fetch' }, record)
  assert.match(submit.title, /submission was not confirmed/)
  assert.match(submit.message, /current approval status/)
  assert.match(submit.diagnostics, /requisitions\/123\/submit/)
})

test('linked JD validation and list refresh failures describe the actual phase', () => {
  const validation = describe('linked-jd', { error: 'Add a required skill.', requestSent: false })
  assert.match(validation.title, /needs correction/)
  assert.match(validation.message, /hiring-request save has not started/)
  assert.match(validation.diagnostics, /Request not sent/)
  const refresh = describe('refresh', { status: 0, error: 'NetworkError' }, record)
  assert.match(refresh.title, /Request saved; list refresh/)
  assert.match(refresh.message, /do not need to save another copy/)
})

test('opening a generated valid JD does not send an automatic save', () => {
  assert.equal(autoKey('generated baseline', 'generated baseline', null, ''), '')
})

test('failed autosave is not automatically retried unchanged, but a later real edit can be saved', () => {
  const first = autoKey('changed content', 'baseline', null, '')
  assert.ok(first)
  assert.equal(autoKey('changed content', 'baseline', null, first), '')
  assert.ok(autoKey('another edit', 'baseline', null, first))
  const file = { name: 'synthetic.pdf', size: 1234, lastModified: 9 }
  const sourceAttempt = autoKey('baseline', 'baseline', file, '')
  assert.ok(sourceAttempt)
  assert.equal(autoKey('baseline', 'baseline', file, sourceAttempt), '')
})
