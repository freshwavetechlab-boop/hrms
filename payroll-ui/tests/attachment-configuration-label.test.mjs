import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/attachmentConfigurationLabel.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
new Function('exports', compiled)(exports)
const { attachmentConfigurationLabel: label, attachmentFormLabel: formLabel, attachmentTargetLabel: targetLabel } = exports
const field = (patch = {}) => ({ id: 1, attributeName: 'Aadhaar Card', fieldLabel: 'Aadhaar', attributeCode: 'UIDAI_JOIN_AADHAAR', moduleCode: 'EMPLOYEE', formCode: 'EMPLOYEE_CREATE_EDIT', clientId: 0, clientName: 'Global', maximumFileSizeBytes: 1048576, ...patch })

test('the same document master keeps its distinct Employee and Joining policies identifiable', () => {
  const employee = field(), joining = field({ id: 89, moduleCode: 'RECRUITMENT', formCode: 'UIDAI_JOINING_DOCUMENTS', maximumFileSizeBytes: 10485760 })
  const selected = [employee, joining].map(row => ({ value: row.id, label: label(row) }))
  assert.deepEqual(selected, [
    { value: 1, label: 'Aadhaar Card — Employee Master · 1 MB' },
    { value: 89, label: 'Aadhaar Card — Joining documents · 10 MB' },
  ])
  assert.equal(selected.some(option => option.label.includes('UIDAI')), false)
})

test('legacy and future client form codes have generic labels without changing stored references', () => {
  const row = field({ formCode: 'FUTURE_CLIENT_JOINING_DOCUMENTS', moduleCode: 'RECRUITMENT' })
  const saved = structuredClone(row)
  assert.equal(formLabel('RECRUITMENT', 'UIDAI_JOINING_DOCUMENTS'), 'Joining documents')
  assert.equal(formLabel('RECRUITMENT', 'FUTURE_CLIENT_JOINING_DOCUMENTS'), 'Joining documents')
  assert.equal(targetLabel(row.moduleCode, row.formCode), 'Recruitment / Joining documents')
  assert.notEqual(targetLabel('RECRUITMENT', 'PRE_ONBOARDING'), targetLabel(row.moduleCode, row.formCode), 'different stored contexts must remain distinguishable')
  label(row)
  assert.deepEqual(row, saved)
})

test('client overrides remain distinguishable and no configuration IDs are dropped', () => {
  const global = field(), client = field({ id: 90, clientId: 20, clientName: 'Client A' })
  const options = [global, client].map(row => ({ value: row.id, label: label(row) }))
  assert.equal(options.length, 2)
  assert.deepEqual(options.map(option => option.value), [1, 90])
  assert.equal(options[1].label, 'Aadhaar Card — Employee Master · Client A · 1 MB')
  assert.equal(label(field({ clientId: 20, clientName: undefined })), 'Aadhaar Card — Employee Master · Client override · 1 MB')
})

test('other configured contexts and file sizes retain readable useful details', () => {
  assert.equal(targetLabel('EMPLOYEE', 'EMPLOYEE_PROFILE'), 'Employees / Employee Profile')
  assert.equal(formLabel('RECRUITMENT', 'CUSTOM_PROOF_FILES'), 'Custom Proof Files')
  assert.equal(label(field({ attributeName: '', fieldLabel: 'Other proof', maximumFileSizeBytes: 512000 })), 'Other proof — Employee Master · 500 KB')
  assert.equal(label(field({ maximumFileSizeBytes: 1572864 })), 'Aadhaar Card — Employee Master · 1.5 MB')
})
