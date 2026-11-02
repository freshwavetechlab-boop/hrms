import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/excelPayrollRoundingReview.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { explainedRoundingReviewRows, explainedRoundingReviewChange } = exports
const request = { batchId: 'batch', rowIds: ['one'], roundingReviews: {} }
const proof = version => ({ rowId: 'one', sourceRow: 3, version, status: 'Explained rounding', proofHash: 'b'.repeat(64) })
const transfer = row => ({ version: 1, sourceBatchId: 'batch', sourceHash: 'a'.repeat(64), amountComparisons: [row] })

test('currency and arithmetic proofs require the same bounded saved-row review contract', () => {
  for (const version of ['source-core-currency-v1', 'source-core-arithmetic-v2']) {
    assert.equal(explainedRoundingReviewRows(request, transfer(proof(version))).length, 1)
    assert.deepEqual(explainedRoundingReviewChange(request, transfer(proof(version))), { roundingReviews: { one: 'b'.repeat(64) } })
  }
  assert.deepEqual(explainedRoundingReviewRows(request, transfer(proof('unknown-version'))), [])
  const row = proof('source-core-arithmetic-v2')
  assert.deepEqual(explainedRoundingReviewRows(request, { ...transfer(row), sourceBatchId: 'other' }), [])
  assert.deepEqual(explainedRoundingReviewRows(request, { ...transfer(row), amountComparisons: [row, row] }), [])
  assert.deepEqual(explainedRoundingReviewRows(request, transfer({ ...row, rowId: 'unselected' })), [])
  assert.deepEqual(explainedRoundingReviewRows(request, transfer({ ...row, status: 'Mismatch' })), [])
})
