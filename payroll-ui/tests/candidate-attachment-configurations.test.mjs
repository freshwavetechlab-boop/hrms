import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function loadModule(path, dependencies = {}) {
  const exports = {}
  const compiled = ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', compiled)(exports, name => dependencies[name] || {})
  return exports
}
const { candidateAttachmentConfigurations: combine } = loadModule('../src/utils/candidateAttachmentConfigurations.ts')
const field = (id, clientId = 0, attributeId = id) => ({ id, clientId, attachmentAttributeId: attributeId, isActive: true, fieldLabel: `Document ${id}` })
const file = (configurationId, clientId = 55) => ({ fieldConfigurationId: configurationId, clientId, isCurrent: true, isDeleted: false })

test('candidate documents reuse the original Employee/joining configuration IDs without another empty checklist', () => {
  const resume = field(1), aadhaar = { ...field(7), moduleCode: 'EMPLOYEE', formCode: 'EMPLOYEE_CREATE_EDIT' }, unused = field(8)
  const result = combine([resume], [aadhaar, unused], [file(7)], 55)
  assert.deepEqual(result.configurations, [resume, aadhaar])
  assert.deepEqual([...result.supplementalIds], [7], 'only supplemental fields get read/review controls')
  assert.equal(result.configurations[1], aadhaar, 'keep original core configuration identity and policy')
  assert.equal(combine([resume], [aadhaar], [], 55).configurations.length, 1, 'unrequested empty documents stay hidden')
})

test('a client-scoped document definition or file from another client is never added to the candidate panel', () => {
  assert.deepEqual(combine([], [field(7, 20)], [file(7)], 55).configurations, [])
  assert.deepEqual(combine([], [field(7)], [file(7, 20)], 55).configurations, [])
  assert.deepEqual(combine([], [field(7)], [file(7, 0)], 55).configurations, [])
  assert.deepEqual(combine([], [field(7)], [file(7, 0)], 0).configurations, [])
  assert.equal(combine([], [field(7, 55)], [file(7)], 55).configurations.length, 1)
})

test('inactive, deleted and obsolete document rows cannot create supplemental fields', () => {
  assert.deepEqual(combine([], [{ ...field(7), isActive: false }], [file(7)], 55).configurations, [])
  assert.deepEqual(combine([], [field(7)], [{ ...file(7), isDeleted: true }], 55).configurations, [])
  assert.deepEqual(combine([], [field(7)], [{ ...file(7), isCurrent: false }], 55).configurations, [])
  assert.deepEqual(combine([], [field(7)], [file(8)], 55).configurations, [], 'similar attributes cannot substitute for an exact core field mapping')
})

test('existing context fields keep their controls and identity when the joining catalog refers to them too', () => {
  const existing = field(7)
  const result = combine([existing], [{ ...existing, fieldLabel: 'Another label' }, field(8, 55)], [file(7), file(8)], 55)
  assert.equal(result.configurations[0], existing)
  assert.deepEqual(result.configurations.map(item => item.id), [7, 8])
  assert.deepEqual([...result.supplementalIds], [8])
})

test('joining configurations are read from the shared attachment core using the explicit selected client', () => {
  const calls = []
  const service = loadModule('../src/services/attachmentService.ts', { './apiClient': { getJson: (...args) => { calls.push(args); return [] } } })
  service.getJoiningDocumentConfigurations(55)
  assert.deepEqual(calls, [['/api/attachments/joining-document-configurations?clientId=55', [], { loader: false, toast: false }]])
})
