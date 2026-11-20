import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeeOnboardingSelection.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { saveOnboardingSelection, readOnboardingSelection, clearOnboardingSelection, releasedForEmployeeMaster, createdEmployeeCredentials } = exports
const storage = () => { const rows = new Map(); return { rows, getItem: key => rows.get(key) ?? null, setItem: (key, value) => rows.set(key, value), removeItem: key => rows.delete(key) } }

test('offer and roster identities survive handoff without cross-client, cross-user or step mixing', () => {
  const s = storage()
  saveOnboardingSelection(s, 10, 55, 'offers', ['offer:7', 'roster:7', 'offer:7', '7', 'offer:0'])
  assert.deepEqual(readOnboardingSelection(s, 10, 55, 'offers'), ['offer:7', 'roster:7'])
  assert.equal(readOnboardingSelection(s, 11, 55, 'offers'), null)
  assert.equal(readOnboardingSelection(s, 10, 56, 'offers'), null)
  assert.equal(readOnboardingSelection(s, 10, 55, 'employees'), null)
  saveOnboardingSelection(s, 10, 55, 'employees', ['19', '20', '19', 'offer:7', '-2'])
  assert.deepEqual(readOnboardingSelection(s, 10, 55, 'employees'), ['19', '20'])
  clearOnboardingSelection(s, 10, 55, 'offers')
  assert.equal(readOnboardingSelection(s, 10, 55, 'offers'), null)
  assert.deepEqual(readOnboardingSelection(s, 10, 55, 'employees'), ['19', '20'])
})
test('invalid scope, malformed storage and unavailable session storage never authorize or break an import', () => {
  const s = storage()
  for (const id of [0, -1, NaN, 1.5]) { saveOnboardingSelection(s, id, 55, 'offers', ['offer:1']); assert.equal(readOnboardingSelection(s, id, 55, 'offers'), null) }
  assert.equal(s.rows.size, 0)
  s.setItem('employee.onboarding:10:55:offers', '["offer:1",42]')
  assert.equal(readOnboardingSelection(s, 10, 55, 'offers'), null)
  const unavailable = { getItem() { throw Error() }, setItem() { throw Error() }, removeItem() { throw Error() } }
  assert.doesNotThrow(() => saveOnboardingSelection(unavailable, 10, 55, 'offers', ['offer:1']))
  assert.equal(readOnboardingSelection(unavailable, 10, 55, 'offers'), null)
})
test('released letters can be imported without recording acceptance; draft, imported and other-client rows cannot', () => {
  const row = { clientId: 55, sourceType: 'Offer', offerStatus: 'Released', acceptanceRecorded: false, importStatus: 'Ready', employeeId: null }
  assert.equal(releasedForEmployeeMaster(row, 55), true)
  assert.equal(releasedForEmployeeMaster({ ...row, sourceType: 'Roster' }, 55), true)
  assert.equal(releasedForEmployeeMaster({ ...row, offerStatus: 'Accepted', acceptanceRecorded: true }, 55), true)
  assert.equal(releasedForEmployeeMaster({ ...row, offerStatus: 'Draft' }, 55), false)
  assert.equal(releasedForEmployeeMaster({ ...row, employeeId: 3 }, 55), false)
  assert.equal(releasedForEmployeeMaster({ ...row, importStatus: 'Imported' }, 55), false)
  assert.equal(releasedForEmployeeMaster(row, 56), false)
  assert.equal(row.acceptanceRecorded, false)
})
test('credential export includes only created accounts and actual per-row passwords, never skipped-account passwords', () => {
  const response = { temporaryPassword: 'Batch-Temporary', results: [
    { status: 'Created', employeeCode: 'E1', employeeName: 'First', email: 'first@example.test', temporaryPassword: 'E1' },
    { status: 'Created', employeeCode: 'E2', employeeName: 'Second', email: 'second@example.test', temporaryPassword: '' },
    { status: 'Skipped', employeeCode: 'E3', employeeName: 'Existing', email: 'existing@example.test', temporaryPassword: 'MUST-NOT-EXPORT' },
  ] }
  const before = structuredClone(response), rows = createdEmployeeCredentials(response)
  assert.equal(rows.length, 2)
  assert.equal(rows[0].temporaryPassword, 'E1')
  assert.equal(rows[1].temporaryPassword, 'Batch-Temporary')
  assert.equal(JSON.stringify(rows).includes('MUST-NOT-EXPORT'), false)
  assert.equal(rows[0].essUrl, 'https://gad-ess.frevo.co.in/')
  assert.deepEqual(response, before)
  assert.deepEqual(createdEmployeeCredentials({ temporaryPassword: 'not-for-existing', results: [response.results[2]] }), [])
})
