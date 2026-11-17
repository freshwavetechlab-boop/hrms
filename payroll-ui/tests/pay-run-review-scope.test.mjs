import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
const code = ts.transpileModule(readFileSync(new URL('../src/utils/payRunReviewScope.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
new Function('exports', 'require', code)(exports, () => { throw Error('Unexpected runtime dependency') })
const { matchesPayRunReviewScope } = exports
const scope = { clientId: 7, offCycle: false, policyBatchId: 'replacement-policy', regularRunCode: 'REG-B-ABCDEF12', legacyRegularRunCode: 'REG-G8' }
const legacy = { clientId: 7, status: 'Draft', runType: 'Regular', runCode: 'REGULAR-E-1234ABCD', attendancePolicyBatchId: '' }

test('an existing unbound employee-cohort draft remains reviewable after policy replacement', () => {
  assert.equal(matchesPayRunReviewScope(legacy, scope), true)
  assert.equal(matchesPayRunReviewScope({ ...legacy, status: 'Pending Approval' }, scope), true)
  assert.equal(matchesPayRunReviewScope({ ...legacy, status: 'Approved' }, scope), true)
  assert.equal(matchesPayRunReviewScope(legacy, { ...scope, policyBatchId: undefined, regularRunCode: 'REGULAR' }), true)
})

test('legacy visibility does not cross the selected client or regular/off-cycle boundaries', () => {
  assert.equal(matchesPayRunReviewScope({ ...legacy, clientId: 8 }, scope), false)
  assert.equal(matchesPayRunReviewScope({ ...legacy, runType: 'Off Cycle' }, scope), false)
  assert.equal(matchesPayRunReviewScope(legacy, { ...scope, offCycle: true }), false)
  assert.equal(matchesPayRunReviewScope({ ...legacy, runType: 'Off Cycle' }, { ...scope, offCycle: true }), true)
})

test('policy-bound runs retain the existing policy restriction, including legacy-looking codes', () => {
  assert.equal(matchesPayRunReviewScope({ ...legacy, attendancePolicyBatchId: 'previous-policy' }, scope), false)
  assert.equal(matchesPayRunReviewScope({ ...legacy, attendancePolicyBatchId: scope.policyBatchId }, scope), true)
})

test('current policy and group codes retain their existing review behavior', () => {
  assert.equal(matchesPayRunReviewScope({ ...legacy, runCode: scope.regularRunCode }, scope), true)
  assert.equal(matchesPayRunReviewScope({ ...legacy, runCode: scope.legacyRegularRunCode }, scope), true)
  assert.equal(matchesPayRunReviewScope({ ...legacy, runCode: 'REG-G9' }, scope), false)
  assert.equal(matchesPayRunReviewScope({ ...legacy, runCode: 'REGULAR' }, scope), false)
  const unboundScope = { ...scope, policyBatchId: undefined, regularRunCode: 'REGULAR' }
  assert.equal(matchesPayRunReviewScope({ ...legacy, runCode: 'REGULAR' }, unboundScope), true)
})

test('the legacy exception excludes completed payment statuses and unrelated codes', () => {
  for (const status of ['Paid', 'Partially Paid', 'Cancelled', 'Unknown']) {
    assert.equal(matchesPayRunReviewScope({ ...legacy, status }, scope), false)
  }
  for (const runCode of ['REGULAR-E-', 'REGULAR-E-1234', 'REGULAR-E-1234ABCD-extra', 'OFFCYCLE-E-1234ABCD']) {
    assert.equal(matchesPayRunReviewScope({ ...legacy, runCode }, scope), false)
  }
})
