import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
const require = createRequire(process.env.PAYROLL_UI_TEST_DEPENDENCIES ? pathToFileURL(resolve(process.env.PAYROLL_UI_TEST_DEPENDENCIES, 'package.json')) : new URL('../package.json', import.meta.url))
const ts = require('typescript')
function load(relative, dependencies = {}) {
  const code = ts.transpileModule(readFileSync(new URL(`../src/${relative}`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  const exports = {}
  new Function('exports', 'require', code)(exports, name => dependencies[name] || {})
  return exports
}
const helper = load('utils/selectedPayRunDraft.ts'), calls = []
const service = load('services/selectedPayRunService.ts', { './apiClient': { postJson: (...args) => { calls.push(args); return Promise.resolve({ ok: true }) } } })
const h = letter => letter.repeat(64)
const adjustment = { id: 17, clientId: 33, employeeId: 21, componentCode: 'TA_DA', adjustmentType: 'Reimbursement', amount: 22680, taxable: false, status: 'Approved', payRunType: 'Regular', payPeriod: '2026-09', reasonCode: 'EXCEL_SETUP', payRunId: null }
const request = { clientId: 33, payPeriod: '2026-09', payDate: '2026-09-28', totalWorkingDays: 30, runType: 'Regular', includedEmployeeIds: [21], adjustmentIds: [17], attendanceGroupIds: [1], attendancePolicyBatchId: 'synthetic-policy' }
const witness = () => ({ kind: 'ReviewedAppliedSourceV1', historicalTransferVersion: 0, originalReceiptFingerprint: h('b'), sourceHash: h('a'), mappingFingerprint: h('c'), identityFingerprint: h('d'), sourceFormulaFingerprint: h('e'), nativeInputFingerprint: h('f'), salaryPolicyFingerprint: h('1'), coreCalculationFingerprint: h('2'), comparisonFingerprint: h('3'), witnessFingerprint: h('4') })
const review = patch => ({ adjustmentId: 17, employeeId: 21, componentCode: 'TA_DA', category: 'Reimbursement', amount: 22680, taxable: false, sourceBatchId: 'synthetic-batch', sourceImportId: 'synthetic-receipt', sourceRowId: 'synthetic-source-19', sourceRow: 19, sourceHash: h('a'), receiptFingerprint: h('b'), businessFingerprint: h('5'), sourceWitness: witness(), ...patch })
const line = patch => ({ code: 'TA_DA', category: 'Reimbursement', componentRole: 'Reimbursement', statutoryType: 'None', taxable: false, proRata: false, sourceFormula: '22680', nativeFormula: 'Approved period amount', sourceRaw: 22680, sourceDisplay: 22680, coreAmount: 22680, coreUnrounded: 22680, formulaStatus: 'Exact approved period literal', ...patch })
const native = patch => ({ sourceKind: 'Monthly snapshot', payPeriod: '2026-09', workingDays: 30, presentDays: 26, actualPresenceKnown: true, payableDays: 30, weeklyOffPayableUnits: null, holidayPayableUnits: null, cycleStart: null, cycleEnd: null, temporaryDayOverrides: false, policyValidationError: '', explanation: 'Existing monthly attendance; source worked days unmapped.', ...patch })
const preview = patch => ({ kind: 'ReviewedAppliedSourceV1', historicalTransferVersion: 0, canConfirm: true, fingerprint: h('4'), blockers: [], reviews: [review()], lines: [line()], nativeAttendance: native(), sourceActualWorkedMeaning: 'NotMapped', sourceNet: 22680, coreNet: 22680, ...patch })

test('normal version1 and no-adjustment requests keep their existing default path without source acknowledgement', () => {
  const key = helper.selectedDraftSourceReviewKey(request, 'business')
  assert.deepEqual(helper.withSelectedDraftSourceReview(request, key).request, request)
  assert.equal('adjustmentSourceReviewAcknowledgement' in helper.withSelectedDraftSourceReview(request, key).request, false)
  assert.equal('adjustmentSourceReviewAcknowledgement' in helper.withSelectedDraftSourceReview({ ...request, adjustmentSourceReviewAcknowledgement: { expectedFingerprint: h('9'), confirmed: true } }, key).request, false)
  const empty = { ...request, adjustmentIds: [] }
  assert.deepEqual(helper.withSelectedDraftSourceReview(empty, helper.selectedDraftSourceReviewKey(empty, 'zero')).request.adjustmentIds, [])
})
test('starting source review blocks creation until this exact fingerprint is explicitly acknowledged', () => {
  const requestKey = helper.selectedDraftSourceReviewKey(request, 'business')
  assert.equal(helper.withSelectedDraftSourceReview(request, requestKey, { requestKey, started: true }).request, null)
  const value = { requestKey, started: true, acknowledgement: { expectedFingerprint: h('4'), confirmed: true } }, before = JSON.stringify(request)
  const result = helper.withSelectedDraftSourceReview(request, requestKey, value)
  assert.deepEqual(result.request.adjustmentSourceReviewAcknowledgement, { expectedFingerprint: h('4'), confirmed: true })
  assert.deepEqual(result.request.adjustmentIds, [17]); assert.equal(JSON.stringify(request), before)
  assert.equal('sourceReviewAttestation' in result.request, false); assert.equal('selectedAdjustmentReviews' in result.request, false)
})
test('review keys bind exact command and adjustment business state; stale choice cannot submit acknowledgement', () => {
  const key = helper.selectedDraftSourceReviewKey(request, 'business'), choice = { requestKey: key, started: true, acknowledgement: { expectedFingerprint: h('4'), confirmed: true } }
  for (const patch of [{ clientId: 44 }, { payPeriod: '2026-08' }, { includedEmployeeIds: [22] }, { adjustmentIds: [18] }, { totalWorkingDays: 31 }, { attendancePolicyBatchId: 'other' }]) {
    const next = { ...request, ...patch }, nextKey = helper.selectedDraftSourceReviewKey(next, 'business')
    assert.notEqual(nextKey, key); assert.equal('adjustmentSourceReviewAcknowledgement' in helper.withSelectedDraftSourceReview(next, nextKey, choice).request, false)
  }
  assert.notEqual(helper.selectedDraftSourceReviewKey(request, 'changed amount/category/tax/source notes'), key)
})
test('missing/invalid confirmation and multi-employee or multi-adjustment review fail closed', () => {
  const key = 'scope'
  for (const acknowledgement of [{ expectedFingerprint: '', confirmed: true }, { expectedFingerprint: h('x'), confirmed: true }, { expectedFingerprint: h('4'), confirmed: false }]) assert.equal(helper.withSelectedDraftSourceReview(request, key, { requestKey: key, started: true, acknowledgement }).request, null)
  for (const patch of [{ includedEmployeeIds: [21, 22] }, { adjustmentIds: [17, 18] }]) assert.equal(helper.withSelectedDraftSourceReview({ ...request, ...patch }, key, { requestKey: key, started: true, acknowledgement: { expectedFingerprint: h('4'), confirmed: true } }).request, null)
})
test('complete server source review accepts known native presence separately from unmapped source worked days', () => {
  assert.equal(service.selectedDraftSourcePreviewError(preview(), request, adjustment), '')
  assert.equal(service.selectedDraftSourcePreviewError(preview({ lines: [line({ sourceRaw: 22680.005, sourceDisplay: 22680.01 })], sourceNet: 22680.005 }), request, adjustment), '')
})
test('blocked server review stays actionable without permitting a fingerprint acknowledgement', () => {
  assert.equal(service.selectedDraftSourcePreviewError(preview({ canConfirm: false, fingerprint: '', reviews: [], lines: [], nativeAttendance: null, sourceNet: null, coreNet: null, blockers: ['No complete native attendance proof.'] }), request, adjustment), '')
  assert.notEqual(service.selectedDraftSourcePreviewError(preview({ canConfirm: false, blockers: [] }), request, adjustment), '')
})
test('different scope, amount, category, tax, source or immutable proof cannot be acknowledged', () => {
  for (const patch of [{ employeeId: 22 }, { adjustmentId: 18 }, { amount: 22681 }, { category: 'Earning' }, { taxable: true }, { sourceHash: h('9') }, { sourceWitness: undefined }, { sourceWitness: { ...witness(), witnessFingerprint: h('9') } }, { sourceWitness: { ...witness(), historicalTransferVersion: 1 } }, { sourceWitness: { ...witness(), originalReceiptFingerprint: h('9') } }]) assert.notEqual(service.selectedDraftSourcePreviewError(preview({ reviews: [review(patch)] }), request, adjustment), '')
  assert.notEqual(service.selectedDraftSourcePreviewError(preview(), { ...request, includedEmployeeIds: [22] }, adjustment), '')
})
test('incomplete financial metadata or native period/known-presence contradictions fail closed', () => {
  for (const patch of [{ code: '' }, { amount: NaN, coreAmount: NaN }, { componentRole: '' }, { statutoryType: '' }, { taxable: undefined }, { proRata: undefined }, { sourceFormula: undefined }, { nativeFormula: undefined }, { formulaStatus: '' }]) assert.notEqual(service.selectedDraftSourcePreviewError(preview({ lines: [line(patch)] }), request, adjustment), '')
  for (const patch of [{ payPeriod: '2026-08' }, { workingDays: 31 }, { payableDays: 31 }, { presentDays: -1 }, { actualPresenceKnown: false }, { temporaryDayOverrides: true }, { policyValidationError: 'Conflict' }]) assert.notEqual(service.selectedDraftSourcePreviewError(preview({ nativeAttendance: native(patch) }), request, adjustment), '')
  assert.notEqual(service.selectedDraftSourcePreviewError(preview({ nativeAttendance: native({ presentDays: null, actualPresenceKnown: false }), sourceActualWorkedMeaning: 'ActualWorked' }), request, adjustment), '')
})
test('immutable witness is also validated when reopening selected processing; existing version1 remains valid', () => {
  const value = { employees: [{ employeeId: 21 }], adjustments: [review()] }
  assert.equal(service.selectedAdjustmentPreviewError(value), '')
  assert.notEqual(service.selectedAdjustmentPreviewError({ ...value, adjustments: [review({ sourceWitness: { ...witness(), comparisonFingerprint: '' } })] }), '')
  assert.equal(service.selectedAdjustmentPreviewError({ ...value, adjustments: [review({ sourceWitness: undefined })] }), '')
})
test('source-review endpoint is one read-only request, with no server snapshots, create or processing retry', async () => {
  await service.reviewSelectedPayRunDraftSource(request)
  assert.equal(calls.length, 1); assert.equal(calls[0][0], '/api/pay-runs/selected-draft-source-review'); assert.deepEqual(calls[0][1], request)
  assert.equal(calls[0][3].timeoutMs, 120000)
  assert.equal('sourceReviewAttestation' in calls[0][1], false); assert.equal('selectedAdjustmentReviews' in calls[0][1], false)
})
