import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url)
const ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const filename = path.join(root, 'payroll-ui/src/utils/excelPayrollCurrentComparison.ts'), module = { exports: {} }
new Function('module', 'exports', ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(module, module.exports)
const { currentPayrollTargets: targets, compareCurrentPayroll: compare } = module.exports
const clone = value => structuredClone(value)
const batch = { id: 'synthetic-saved-batch', clientId: 7, month: '2026-09', rows: [{ id: 'row-a', sourceRow: 6, employeeCode: 'SYN001', employeeName: 'Synthetic A', netPay: 880, employment: { employeeId: 11, identityReviewRequired: false, blockedForMonth: false } }] }
const context = { roster: { rows: [{ rowId: 'row-a', employeeId: 11, status: 'Existing' }] } }
const preview = { rows: [{ rowId: 'row-a', employeeId: 11, payrollDays: 30, presentDays: 30, payableDays: 30, presentDaysEvidence: { sourceColumnIndex: 2, sourceCell: 'C6', sourceValue: 30, calculationValue: 30, actualWorkedReviewed: true, status: 'ReviewedWorkedDays', message: 'Reviewed mapped worked days' }, lines: [{ code: 'BASIC', sourceAmount: 1000 }, { code: 'PF', sourceAmount: 120 }, { code: 'ER', sourceAmount: 130 }] }], transfer: { components: [{ code: 'BASIC', category: 'Earning' }, { code: 'PF', category: 'Deduction' }, { code: 'ER', category: 'Benefit' }] } }
const observation = { employeeId: 11, ok: true, error: '', data: { mode: 'Employee', complete: true, issues: [], net: 880, gross: 1000, deductions: 120, benefits: 130, inputs: ['PAYROLL_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS'].map(name => ({ name, value: 30, source: 'Saved attendance' })), lines: [{ code: 'BASIC', category: 'Earning', earned: 1000 }, { code: 'PF', category: 'Deduction', earned: 120 }, { code: 'ER', category: 'Benefit', earned: 130 }] } }
const eligible = targets(7, batch, context, preview)
assert.equal(eligible[0].eligible, true)
assert.equal(compare(eligible, preview, [observation])[0].status, 'Raw values and mapped days equal')
assert.equal(targets(8, batch, context, preview)[0].eligible, false)
for (const change of [b => { b.rows[0].employment.identityReviewRequired = true }, b => { b.rows[0].employment.employeeId = 12 }, b => { b.rows[0].employment.blockedForMonth = true }, b => { b.rows.push({ ...b.rows[0], id: 'row-b' }) }]) {
  const b = clone(batch); change(b); assert.equal(targets(7, b, context, preview)[0].eligible, false)
}
const duplicateContext = clone(context); duplicateContext.roster.rows.push({ rowId: 'row-b', employeeId: 11, status: 'Existing' }); assert.equal(targets(7, batch, duplicateContext, preview)[0].eligible, false)
const reviewed = clone(context); reviewed.roster.rows[0].status = 'Review'; assert.equal(targets(7, batch, reviewed, preview)[0].eligible, false)
const repeated = clone(preview); repeated.rows.push(clone(repeated.rows[0])); assert.equal(targets(7, batch, context, repeated)[0].eligible, false)
const missing = clone(observation); missing.data.inputs[1].value = null
const missingResult = compare(eligible, preview, [missing])[0]; assert.equal(missingResult.days[1].actual, null); assert.ok(missingResult.reasons.some(r => r.startsWith('PRESENT_DAYS:')))
const employer = clone(observation); employer.data.lines[2].earned = 100; employer.data.benefits = 100
const employerResult = compare(eligible, preview, [employer])[0]; assert.equal(employerResult.status, 'Differences to review'); assert.equal(employerResult.lines[2].delta, -30); assert.equal(employerResult.netDelta, 0)
const extra = clone(observation); extra.data.lines.push({ code: 'PT', category: 'Deduction', earned: 10 }); assert.ok(compare(eligible, preview, [extra])[0].reasons.some(r => r.includes('additional or unmapped')))
const rounding = clone(observation); rounding.data.net = 880.005; assert.equal(compare(eligible, preview, [rounding])[0].status, 'Differences to review')
const incomplete = clone(observation); incomplete.data.complete = false; incomplete.data.issues = [{ severity: 'Error', component: 'Attendance', message: 'No saved period attendance.' }]; assert.equal(compare(eligible, preview, [incomplete])[0].status, 'Blocked')
assert.equal(compare(eligible, preview, [observation, observation])[0].status, 'Blocked')
const before = JSON.stringify({ batch, context, preview, observation }); compare(eligible, preview, [observation]); assert.equal(JSON.stringify({ batch, context, preview, observation }), before)
console.log('PASS: current payroll comparison: exact identity intersection, client boundary, duplicates/review/exit blocks, missing-not-zero attendance, employer-only difference, unmapped components, no rounding force-pass, incomplete/duplicate observations, immutable saved source. Synthetic fixtures; no requests.')

// A payable fallback is arithmetic input, never proof of actual worked attendance.
for (const change of [p => { delete p.rows[0].presentDaysEvidence }, p => { p.rows[0].presentDaysEvidence = { sourceColumnIndex: null, sourceCell: null, sourceValue: null, calculationValue: 30, actualWorkedReviewed: false, status: 'NotMapped', message: 'Map actual worked days or turn off attendance import.' } }, p => { p.rows[0].presentDaysEvidence.actualWorkedReviewed = false }, p => { p.rows[0].presentDaysEvidence.sourceValue = 26 }]) {
  const p = clone(preview); change(p); const o = clone(observation); o.data.inputs[1].value = 26
  const result = compare(targets(7, batch, context, p), p, [o])[0]
  assert.equal(result.days[1].source, null); assert.equal(result.days[1].actual, 26); assert.equal(result.days[1].calculationValue, 30)
  assert.ok(result.reasons.some(r => r.includes('source actual-worked mapping is unverified')))
  assert.ok(!result.reasons.some(r => r === 'PRESENT_DAYS: current saved attendance differs or is missing.'))
  assert.equal(result.netDelta, 0); assert.equal(result.status, 'Differences to review')
}
const zero = clone(preview), zeroObservation = clone(observation)
zero.rows[0].presentDays = 0; zero.rows[0].presentDaysEvidence.sourceValue = 0; zero.rows[0].presentDaysEvidence.calculationValue = 0; zeroObservation.data.inputs[1].value = 0
assert.equal(compare(targets(7, batch, context, zero), zero, [zeroObservation])[0].status, 'Raw values and mapped days equal')
console.log('PASS: unmapped, legacy, stale and inconsistent source-worked evidence stays unresolved without inventing an attendance mismatch; reviewed zero remains valid. No writes.')
