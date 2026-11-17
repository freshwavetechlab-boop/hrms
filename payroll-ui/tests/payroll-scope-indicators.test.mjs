import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/payrollScopeIndicators.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { payrollClientIndicators: indicators } = exports
const employee = (id, clientId, isActive = true) => ({ id, clientId, isActive })
const run = (clientId, employeeCount, overrides = {}) => ({ clientId, employeeCount, payPeriod: '2026-10', status: 'Approved', ...overrides })

test('empty sources produce no invented client or payroll indicator', () => {
  assert.equal(indicators([], []).size, 0)
})
test('roster counts only unique active employee IDs without counting inactive aliases', () => {
  const value = indicators([employee(1, 7), employee(1, 7), employee(2, 7, false), employee(3, 7), employee(3, 7, false)], [])
  assert.deepEqual(value.get(7), { count: 2, hasPayroll: false })
})
test('employee availability and payroll presence stay isolated by client', () => {
  const value = indicators([employee(1, 7), employee(2, 8), employee(3, 8)], [run(7, 1)])
  assert.deepEqual(value.get(7), { count: 1, hasPayroll: true })
  assert.deepEqual(value.get(8), { count: 2, hasPayroll: false })
})
test('historical payroll in another month or location marks the client independently of current filters', () => {
  const value = indicators([employee(1, 7)], [run(7, 4, { payPeriod: '2025-04', workLocationId: 99, status: 'Paid' })])
  assert.deepEqual(value.get(7), { count: 1, hasPayroll: true })
})
test('saved payroll without any current active employees retains the pink indicator with zero count', () => {
  assert.deepEqual(indicators([], [run(7, 8)]).get(7), { count: 0, hasPayroll: true })
  assert.deepEqual(indicators([employee(1, 7, false)], [run(7, 8)]).get(7), { count: 0, hasPayroll: true })
})
test('held or queued requests without a saved employee cohort do not mark payroll as existing', () => {
  for (const status of ['AwaitingProcessing', 'Queued', 'Processing']) {
    assert.deepEqual(indicators([employee(1, 7)], [run(7, 0, { status })]).get(7), { count: 1, hasPayroll: false })
  }
})
test('a positive saved cohort marks payroll even when its status is draft rather than approved', () => {
  assert.deepEqual(indicators([], [run(7, 3, { status: 'Draft' })]).get(7), { count: 0, hasPayroll: true })
})
test('invalid IDs/counts cannot create incorrect availability or payroll presence', () => {
  const value = indicators([employee(0, 7), employee(1, 0), employee(2, -1)], [run(0, 2), run(7, Number.NaN), run(7, -2)])
  assert.deepEqual([...value.entries()], [[7, { count: 0, hasPayroll: false }]])
})
test('indicator derivation leaves original roster and saved payroll objects unchanged', () => {
  const people = [employee(1, 7), employee(2, 7, false)], runs = [run(7, 3)]
  const before = JSON.stringify({ people, runs })
  indicators(people, runs)
  assert.equal(JSON.stringify({ people, runs }), before)
})
