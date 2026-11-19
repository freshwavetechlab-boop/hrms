import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/reviewedExtraPaidUnits.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { hasReviewedExtraPaidUnits: reviewed, monthlyAttendanceHasIssue: hasIssue } = exports
function row() {
  const record = { employeeId: 71, month: '2026-10', workingDays: 26, presentDays: null, payableDays: 40, lopDays: 0, sourceType: 'ExcelPayableOnly', remarks: '' }
  record.remarks = JSON.stringify({ version: 2, clientId: 9, employeeId: 71, payPeriod: record.month, payrollDays: 26, payableDays: 40, actualPresentDays: null, presentMeaning: 'NotMapped', payableMeaning: 'AlreadyPayable', mode: 'SourceSeed', reviewedExtraDutyUnits: true, batchId: 'a'.repeat(32), importId: 'b'.repeat(32), sourceHash: 'c'.repeat(64), rowId: 'source-row-7' })
  return record
}
test('reviewed source salary units can exceed the divisor without inventing physical attendance', () => {
  const record = row(), before = JSON.stringify(record)
  assert.equal(reviewed(record, 9), true)
  assert.equal(hasIssue(record, 9), false)
  assert.equal(record.presentDays, null)
  assert.equal(JSON.stringify(record), before)
  for (const divisor of [26, 30, 31]) {
    const item = row(), evidence = JSON.parse(item.remarks)
    item.workingDays = evidence.payrollDays = divisor
    item.remarks = JSON.stringify(evidence)
    assert.equal(hasIssue(item, 9), false)
  }
})
test('scope, provenance, extra-unit consent and balanced LOP must still match', () => {
  assert.equal(hasIssue(row(), 10), true)
  for (const change of [
    item => { item.employeeId = 72 }, item => { item.month = '2026-09' },
    item => { item.presentDays = 40 }, item => { item.lopDays = 1 },
    item => { item.sourceType = 'Manual' }, item => { item.remarks = '{}' }, item => { item.remarks = null },
    item => { const evidence = JSON.parse(item.remarks); evidence.reviewedExtraDutyUnits = false; item.remarks = JSON.stringify(evidence) },
    item => { const evidence = JSON.parse(item.remarks); evidence.sourceHash = 'invalid'; item.remarks = JSON.stringify(evidence) }
  ]) { const record = row(); change(record); assert.equal(hasIssue(record, 9), true) }
})
test('standard manual attendance keeps its existing caps and balancing checks', () => {
  const standard = { ...row(), presentDays: 20, payableDays: 20, lopDays: 6, sourceType: 'Manual', remarks: '' }
  assert.equal(hasIssue(standard, 9), false)
  assert.equal(hasIssue({ ...standard, payableDays: 40, lopDays: 0 }, 9), true)
  assert.equal(hasIssue({ ...standard, lopDays: 5 }, 9), true)
})
