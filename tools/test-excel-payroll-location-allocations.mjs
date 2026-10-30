import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url), ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const cache = new Map()
function load(relative) {
  if (cache.has(relative)) return cache.get(relative)
  const module = { exports: {} }, code = ts.transpileModule(fs.readFileSync(path.join(root, 'payroll-ui/src', relative), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('require', 'module', 'exports', code)(name => { assert.ok(name.startsWith('./')); return load(path.posix.normalize(path.posix.join(path.posix.dirname(relative), name + '.ts'))) }, module, module.exports)
  cache.set(relative, module.exports); return module.exports
}
const { newLocationAllocationReview, patchLocationAllocationGroup, patchLocationAllocationPart, isLocationAllocationReviewOnly } = load('utils/excelPayrollLocationAllocations.ts')
const batch = { id: 'synthetic-source', clientId: 33, month: '2026-09', rows: [17, 13].map((days, index) => ({ id: 'part-' + index, sourceRow: 53 + index, employeeName: 'Synthetic person', employeeCode: 'SYN01', information: [{ label: 'PAN', value: 'AAAAA1111A' }, { label: 'Payroll unit', value: 'Source unit ' + index }, { label: 'Work Location', value: 'Source location ' + index }, { label: 'Attendance', value: String(days) }], earnings: [{ label: 'Basic', amount: index ? 33860 : 42749 }], deductions: [], employerContributions: [], netPay: index ? 33860 : 42749, employment: { identityReviewRequired: true } })) }
const context = { locationAllocationSupported: true, locationAllocationImportDate: '2026-10-28' }, ids = batch.rows.map(row => row.id)
const original = JSON.stringify({ batch, context })
const review = newLocationAllocationReview(batch, context, ids)
assert.ok(review)
assert.deepEqual([review.payrollPeriod, review.configurationEffectiveFrom, review.groups.length, review.groups[0].employeeId, review.reviewed, review.reviewedSettingsApplicability], ['2026-09', '2026-10-28', 1, 0, false, false])
assert.deepEqual(review.groups[0].parts.map(part => [part.rowId, part.workLocationId, part.templateId, part.componentScopes]), [['part-0', 0, '', {}], ['part-1', 0, '', {}]])
assert.ok(!JSON.stringify(review).includes('fromDate'), 'No historical date ranges are invented')
for (const invalid of [[ids[0]], [...ids, 'unknown'], [...ids, ids[0]]]) assert.equal(newLocationAllocationReview(batch, context, invalid), null)
assert.equal(newLocationAllocationReview(batch, { ...context, locationAllocationSupported: false }, ids), null)
assert.equal(newLocationAllocationReview(batch, { ...context, locationAllocationImportDate: '' }, ids), null)
const conflict = structuredClone(batch); conflict.rows[1].employeeName = 'Another source name'
assert.equal(newLocationAllocationReview(conflict, context, ids), null)
const acknowledged = { ...review, reviewed: true, reviewedSettingsApplicability: true, groups: [{ ...review.groups[0], employeeId: 100, referenceAssignmentId: 'reviewed-reference' }] }
const changed = patchLocationAllocationPart(acknowledged, 0, ids[0], { workLocationId: 5, templateId: 'existing-template', componentScopes: { 'master-basic': 'SourceFormulaPerPart' } })
assert.equal(changed.reviewed, false); assert.equal(changed.reviewedSettingsApplicability, false); assert.equal(changed.groups[0].referenceAssignmentId, undefined)
assert.equal(changed.groups[0].employeeId, 100); assert.equal(changed.groups[0].parts.length, 2)
assert.equal(acknowledged.groups[0].referenceAssignmentId, 'reviewed-reference', 'Never mutate the retained request')
const selectedReference = patchLocationAllocationGroup(changed, 0, { referenceAssignmentId: 'explicit-preview-candidate' })
assert.equal(selectedReference.groups[0].referenceAssignmentId, 'explicit-preview-candidate')
assert.equal(isLocationAllocationReviewOnly(changed, selectedReference), true)
assert.equal(isLocationAllocationReviewOnly(changed, { ...changed, reviewed: true }), true)
assert.equal(isLocationAllocationReviewOnly(review, changed), false)
assert.equal(isLocationAllocationReviewOnly(undefined, review), false)
assert.equal(JSON.stringify({ batch, context }), original, 'All original attendance, amounts and source identity fields stay unchanged')
console.log('PASS: 7 monthly-location review groups: separate saved period/server import date, no guessed IDs/dates, complete source-part selection, unsupported runtime, conflicting source groups, scope/reference review invalidation, immutable source/request.')
