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
const helper = load('utils/selectedPayRunDraft.ts')
const sourceAdjustment = (patch = {}) => ({ id: 17, clientId: 33, employeeId: 21, employeeName: 'Synthetic', employeeCode: 'SYN21', componentId: 0, componentCode: 'TA_DA', componentName: 'Travel', adjustmentType: 'Reimbursement', amount: 22680, payPeriod: '2026-09', payRunType: 'Regular', reasonCode: 'EXCEL_SETUP', notes: 'Synthetic saved source receipt', taxable: false, status: 'Approved', payRunId: null, ...patch })
const scope = patch => ({ clientId: 33, period: '2026-09', workingDays: 30, policyBatchId: 'synthetic-policy', policyLabel: 'Synthetic policy', groupIds: [1], employeeIds: [21], eligibleEmployeeIds: [21, 22], adjustments: [sourceAdjustment()], ...patch })

test('default none blocks an applicable adjustment; explicit exact ID produces one held-draft request', () => {
  assert.equal(helper.selectedPayRunDraftRequest(scope()).request, null)
  const value = scope({ selectedAdjustmentIds: [17] }), before = JSON.stringify(value)
  const result = helper.selectedPayRunDraftRequest(value)
  assert.deepEqual(result.request.adjustmentIds, [17]); assert.deepEqual(result.request.includedEmployeeIds, [21])
  assert.equal(result.request.clientId, 33); assert.equal(result.request.payPeriod, '2026-09')
  assert.equal('selectedAdjustmentReviews' in result.request, false)
  assert.equal(JSON.stringify(value), before)
})
test('no-adjustment requests preserve the existing empty explicit ID list', () => {
  assert.deepEqual(helper.selectedPayRunDraftRequest(scope({ adjustments: [] })).request.adjustmentIds, [])
})
test('unrelated client/month/employee/off-cycle IDs cannot be explicitly selected', () => {
  for (const patch of [{ clientId: 44 }, { payPeriod: '2026-08' }, { employeeId: 22 }, { payRunType: 'Off Cycle' }]) {
    const data = scope({ adjustments: [sourceAdjustment(patch)] })
    assert.deepEqual(helper.selectedPayRunDraftRequest(data).request.adjustmentIds, [])
    assert.equal(helper.selectedPayRunDraftRequest({ ...data, selectedAdjustmentIds: [17] }).request, null)
  }
})
test('duplicate, arbitrary and incomplete adjustment selections fail closed', () => {
  for (const selectedAdjustmentIds of [[17, 17], [99], [-1], [1.5]]) assert.equal(helper.selectedPayRunDraftRequest(scope({ selectedAdjustmentIds })).request, null)
  const adjustments = [sourceAdjustment(), sourceAdjustment({ id: 18, componentCode: 'OTHER' })]
  assert.equal(helper.selectedPayRunDraftRequest(scope({ adjustments, selectedAdjustmentIds: [17] })).request, null)
  assert.deepEqual(helper.selectedPayRunDraftRequest(scope({ adjustments, selectedAdjustmentIds: [18, 17] })).request.adjustmentIds, [17, 18])
})
test('manual, already-bound or invalid approved adjustments are not admitted by a source-looking label', () => {
  for (const patch of [{ reasonCode: 'Manual' }, { payRunId: 70 }, { amount: 0 }, { amount: -1 }, { amount: NaN }, { componentCode: '' }, { adjustmentType: 'Benefit' }, { taxable: null }])
    assert.equal(helper.selectedPayRunDraftRequest(scope({ adjustments: [sourceAdjustment(patch)], selectedAdjustmentIds: [17] })).request, null)
})
test('any reviewed context or adjustment business field change invalidates local selection', () => {
  const current = scope(), key = helper.selectedDraftAdjustmentReviewKey(current)
  for (const patch of [{ clientId: 44 }, { period: '2026-08' }, { policyBatchId: 'other-policy' }, { employeeIds: [22] }]) assert.notEqual(helper.selectedDraftAdjustmentReviewKey({ ...current, ...patch }), key)
  for (const patch of [{ amount: 22681 }, { taxable: true }, { componentCode: 'OTHER' }, { adjustmentType: 'Earning' }, { notes: 'Changed source receipt' }, { payRunId: 70 }]) assert.notEqual(helper.selectedDraftAdjustmentReviewKey({ ...current, adjustments: [sourceAdjustment(patch)] }), key)
  assert.equal(helper.selectedDraftAdjustmentReviewKey({ ...current, employeeIds: [21] }), key)
})
test('held creation permission retains active unrestricted super-admin payroll requirements', () => {
  const actor = { isActive: true, clientId: null, roles: ['super_admin'], permissions: ['payroll.run'] }
  assert.equal(helper.canCreateSelectedPayRunDraft(actor), true)
  for (const patch of [{ isActive: false }, { clientId: 33 }, { roles: [] }, { permissions: [] }]) assert.equal(helper.canCreateSelectedPayRunDraft({ ...actor, ...patch }), false)
})
const calls = []
const service = load('services/selectedPayRunService.ts', { './apiClient': { postJson: (...args) => { calls.push(args); return Promise.resolve({ ok: true }) } } })
const review = patch => ({ adjustmentId: 17, employeeId: 21, componentCode: 'TA_DA', category: 'Reimbursement', amount: 22680, taxable: false, sourceBatchId: 'synthetic-batch', sourceImportId: 'synthetic-receipt', sourceRowId: 'synthetic-source-row', sourceRow: 19, sourceHash: 'a'.repeat(64), receiptFingerprint: 'b'.repeat(64), businessFingerprint: 'c'.repeat(64), ...patch })
const preview = patch => ({ payRunId: 70, clientId: 33, payPeriod: '2026-09', employees: [{ employeeId: 21, employeeCode: 'SYN21' }], adjustments: [review()], ...patch })
test('complete immutable server adjustment review is accepted, including legacy no-adjustment runs', () => {
  assert.equal(service.selectedAdjustmentPreviewError(preview()), '')
  assert.equal(service.selectedAdjustmentPreviewError(preview({ adjustments: [] })), '')
  assert.equal(service.selectedAdjustmentPreviewError(preview({ adjustments: undefined })), '')
})
test('wrong employee, duplicate ID, invalid money/category or incomplete source fingerprint blocks preflight', () => {
  for (const patch of [{ employeeId: 22 }, { amount: 0 }, { amount: NaN }, { category: 'Benefit' }, { sourceBatchId: '' }, { sourceImportId: '' }, { sourceRowId: '' }, { sourceRow: 0 }, { sourceHash: '' }, { receiptFingerprint: 'x'.repeat(64) }, { businessFingerprint: '' }]) assert.match(service.selectedAdjustmentPreviewError(preview({ adjustments: [review(patch)] })), /does not match/)
  assert.match(service.selectedAdjustmentPreviewError(preview({ adjustments: [review(), review()] })), /does not match/)
})
test('processing submits only the existing exact-run command, never caller snapshots or global queue', async () => {
  const body = { clientId: 33, payPeriod: '2026-09', expectedFingerprint: 'reviewed-server-fingerprint', confirmed: true }
  await service.processSelectedPayRun(70, body)
  assert.equal(calls.length, 1); assert.equal(calls[0][0], '/api/pay-runs/70/process-selected')
  assert.deepEqual(calls[0][1], body); assert.equal(calls[0][3].timeoutMs, 300000)
})
