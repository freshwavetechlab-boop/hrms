import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/employeePresentSimulation.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { calendarDaysForSimulation, employeePresentScenario } = exports
const native = (payroll, present, payable) => ({ inputs: [{ name: 'PAYROLL_DAYS', value: payroll }, { name: 'PRESENT_DAYS', value: present }, { name: 'PAYABLE_DAYS', value: payable }], attendanceEvidence: { sourceKind: 'Monthly snapshot' } })

test('calendar fallback uses the selected month including leap February and rejects invalid periods', () => {
  assert.equal(calendarDaysForSimulation('2026-10'), 31)
  assert.equal(calendarDaysForSimulation('2026-11'), 30)
  assert.equal(calendarDaysForSimulation('2028-02'), 29)
  assert.equal(calendarDaysForSimulation('2026-02'), 28)
  assert.equal(calendarDaysForSimulation('2026-13'), null)
})
test('present-only what-if without attendance never invents paid leave or weekly-off credits', () => {
  const scenario = employeePresentScenario({ ...native(null, null, null), attendanceEvidence: { sourceKind: 'Missing native attendance' } }, '2026-10', { PRESENT_DAYS: '27.5' })
  assert.equal(scenario.payrollDays, 31); assert.equal(scenario.presentDays, 27.5); assert.equal(scenario.payableDays, 27.5)
  assert.equal(scenario.savedCredit, 0); assert.equal(scenario.calendarDefault, true); assert.equal(scenario.hypothetical, true)
})
test('saved fixed divisor and known paid-credit delta survive a present-days edit', () => {
  const baseline = native(26, 21, 23), before = JSON.stringify(baseline)
  const scenario = employeePresentScenario(baseline, '2026-10', { PRESENT_DAYS: '20.5' })
  assert.equal(scenario.payrollDays, 26); assert.equal(scenario.presentDays, 20.5); assert.equal(scenario.payableDays, 22.5)
  assert.equal(scenario.calendarDefault, false); assert.equal(JSON.stringify(baseline), before)
})
test('reviewed extra-duty deltas are never silently clamped or converted into actual presence', () => {
  const scenario = employeePresentScenario(native(26, 26, 44), '2026-10', { PRESENT_DAYS: '25' })
  assert.equal(scenario.payableDays, 43); assert.equal(scenario.savedCredit, 18)
  const unknown = employeePresentScenario(native(26, null, 44), '2026-10', {})
  assert.equal(unknown.presentDays, null); assert.equal(unknown.payableDays, null); assert.equal(unknown.payrollDays, null)
  // Actual presence does not replace a separately reviewed payable-only salary quantity.
  const actualEntry = employeePresentScenario(native(26, null, 44), '2026-10', { PRESENT_DAYS: '25' })
  assert.equal(actualEntry.presentDays, 25); assert.equal(actualEntry.payableDays, null)
})
test('blank restores native input while explicit zero remains an intentional what-if', () => {
  const baseline = native(31, 20, 20)
  assert.equal(employeePresentScenario(baseline, '2026-10', {}).presentDays, null)
  assert.equal(employeePresentScenario(baseline, '2026-10', {}).payrollDays, null)
  const zero = employeePresentScenario(baseline, '2026-10', { PRESENT_DAYS: '0' })
  assert.equal(zero.presentDays, 0); assert.equal(zero.payableDays, 0); assert.equal(zero.payrollDays, 31)
})
test('advanced explicit values override only the temporary request and malformed inputs stay invalid', () => {
  const scenario = employeePresentScenario(native(26, 20, 22), '2026-10', { PRESENT_DAYS: '19', PAYABLE_DAYS: '21.5', PAYROLL_DAYS: '30', ANNUAL_CTC: '600000' })
  assert.equal(scenario.payrollDays, 30); assert.equal(scenario.payableDays, 21.5); assert.equal(scenario.annualCtc, 600000)
  assert.equal(Number.isNaN(employeePresentScenario(native(26, 20, 22), '2026-10', { PRESENT_DAYS: 'x' }).presentDays), true)
})
