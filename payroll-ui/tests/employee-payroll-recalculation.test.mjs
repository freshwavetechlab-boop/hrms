import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeePayrollRecalculation.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText)(exports)
const { recalculationPreviewError } = exports
const run = { id: 12, clientId: 7, payPeriod: '2026-10', status: 'Draft' }
const employee = { employeeId: 32 }
const totals = { gross: 12000, deductions: 1300, net: 10700, presentDays: null, payableDays: 26.5 }
const preview = { payRunId: 12, clientId: 7, payPeriod: '2026-10', employeeId: 32, status: 'Draft', expectedFingerprint: 'reviewed-native-fingerprint', canApply: true, before: totals, after: totals, blockers: [], changes: [] }
test('review keeps payable-only attendance valid without inventing present days', () => {
  assert.equal(recalculationPreviewError(preview, run, employee), '')
  assert.equal(preview.after.presentDays, null)
})
test('preview cannot cross employee, client, month or run', () => {
  for (const patch of [{ employeeId: 33 }, { clientId: 8 }, { payPeriod: '2026-11' }, { payRunId: 13 }]) assert.match(recalculationPreviewError({ ...preview, ...patch }, run, employee), /different employee, client or month/)
})
test('approved, paid and processing runs cannot be confirmed from an old draft preview', () => {
  for (const status of ['Approved', 'Pending Approval', 'Paid', 'Partially Paid', 'Processing']) {
    assert.match(recalculationPreviewError(preview, { ...run, status }, employee), /Draft/)
    assert.match(recalculationPreviewError({ ...preview, status }, run, employee), /Draft/)
  }
})
test('blocked or incomplete preview cannot authorize a save', () => {
  assert.match(recalculationPreviewError({ ...preview, canApply: false, blockers: ['Effective salary missing'] }, run, employee), /Effective salary missing/)
  for (const patch of [{ expectedFingerprint: '' }, { before: null }, { after: null }]) assert.match(recalculationPreviewError({ ...preview, ...patch }, run, employee), /complete reviewed calculation/)
})
test('non-finite amounts or known attendance are rejected', () => {
  for (const field of ['gross', 'deductions', 'net', 'payableDays', 'presentDays']) assert.match(recalculationPreviewError({ ...preview, after: { ...totals, [field]: Number.NaN } }, run, employee), /invalid amounts or attendance/)
})
