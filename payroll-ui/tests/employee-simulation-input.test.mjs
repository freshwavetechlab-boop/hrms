import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeeSimulationInput.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const parse = exports.employeeSimulationInput
const saved = { clientId: 12, employeeId: 34, payPeriod: '2026-09', payrollDays: '', presentDays: '', payableDays: '', annualCtc: '' }
test('saved employee salary and attendance remain native inputs rather than zero overrides', () => {
  assert.deepEqual(parse(saved), { mode: 'Employee', clientId: 12, employeeId: 34, payPeriod: '2026-09', payrollDays: null, presentDays: null, payableDays: null, annualCtc: null, amountOverrides: {} })
  assert.equal(parse({ ...saved, payableDays: '0' }).payableDays, 0)
  assert.equal(parse({ ...saved, annualCtc: '0' }).annualCtc, 0)
})
test('lower fractional payable days remain what-if inputs without freezing formula components', () => {
  const input = parse({ ...saved, payrollDays: '26', payableDays: '10.5' })
  assert.equal(input.payableDays, 10.5); assert.equal(input.presentDays, null); assert.deepEqual(input.amountOverrides, {})
  for (const invalid of [{ employeeId: undefined }, { clientId: undefined }, { payPeriod: '2026-13' }, { payableDays: '-1' }, { payrollDays: '26', payableDays: '27' }, { payrollDays: '26.5' }, { payableDays: 'abc' }]) assert.equal(parse({ ...saved, ...invalid }), null)
})
