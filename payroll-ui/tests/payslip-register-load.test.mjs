import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/utils/payslipRegister.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
const { payslipRegisterRunError } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'))
const run = { id: 9001, clientId: 907, status: 'Approved', employees: Array.from({ length: 52 }, (_, i) => ({ employeeId: i + 1 })) }
const success = data => ({ ok: true, data, error: '', status: 200 })

test('an approved list entry never hides failed detail requests behind a no-approved-run message', () => {
  assert.match(payslipRegisterRunError({ ok: false, data: null, status: 503, error: 'Synthetic detail failed' }, 907, 9001), /HTTP 503.*Synthetic detail failed/)
  assert.match(payslipRegisterRunError({ ok: false, data: null, status: 0, error: 'Request timed out.' }, 907, 9001), /Request timed out/)
})
test('retry may accept the exact approved run and all52 returned rows', () => {
  assert.notEqual(payslipRegisterRunError({ ok: false, data: null, status: 503, error: 'Retry' }, 907, 9001), '')
  assert.equal(payslipRegisterRunError(success(run), 907, 9001), '')
  assert.equal(run.employees.length, 52)
})
test('late previous-client or previous-run results cannot populate a new selection', () => {
  assert.match(payslipRegisterRunError(success(run), 908, 9002), /did not match/)
  assert.match(payslipRegisterRunError(success(run), 907, 9002), /did not match/)
})
test('empty, summary-only and newly unapproved responses require review instead of a misleading empty table', () => {
  assert.match(payslipRegisterRunError(success(null), 907, 9001), /empty pay run/)
  assert.match(payslipRegisterRunError(success({ ...run, employees: undefined }), 907, 9001), /incomplete/)
  assert.match(payslipRegisterRunError(success({ ...run, status: 'Draft' }), 907, 9001), /no longer approved/)
})
