import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/regularRunJourney.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { buildRegularRunJourney: journey } = exports
const input = overrides => ({ run: null, diagnostics: null, clientId: 7, policySelected: true, includedCount: 12, attendanceReady: true, attendanceIssues: 0, locationLoading: false, adjustmentCount: 0, workingDays: 30, policyName: 'Monthly policy', ...overrides })
const run = status => ({ id: 41, clientId: 7, clientName: 'Example client', payPeriod: '2026-10', status, employeeCount: 12, employees: [], processingError: '' })
const diagnostics = overrides => ({ payRunId: 41, stepLogs: [], validationIssues: [], exceptions: [], calculationTraces: [], reconciliationResults: [], ...overrides })
const step = (number, overrides) => ({ id: number, payRunId: 41, stepNumber: number, stepName: number === 3 ? 'Salary Structure Validation' : `Check ${number}`, status: 'Success', warning: '', errorMessage: '', ...overrides })
const getStep = (value, id) => value.steps.find(row => row.id === id)

test('ready scope is not a claim that salary or statutory checks have passed', () => {
  const value = journey(input())
  assert.equal(value.status, 'ready')
  assert.equal(getStep(value, 'scope').state, 'done')
  assert.equal(getStep(value, 'attendance').state, 'done')
  assert.equal(getStep(value, 'salary').state, 'pending')
  assert.equal(getStep(value, 'validation').state, 'pending')
  assert.equal(getStep(value, 'approval').state, 'pending')
})
test('missing policy and unready attendance retain actual preconditions', () => {
  assert.match(journey(input({ policySelected: false })).blockers[0].message, /policy/)
  const value = journey(input({ attendanceReady: false, attendanceIssues: 2 }))
  assert.equal(value.status, 'blocked')
  assert.match(value.blockers[0].message, /2 attendance issues/)
  assert.equal(getStep(value, 'attendance').state, 'blocked')
})
test('loading a client or policy only prepares inputs and never claims payroll has started', () => {
  const value = journey(input({ locationLoading: true, attendanceReady: false }))
  assert.equal(value.status, 'ready')
  assert.equal(value.title, 'Loading payroll inputs')
  assert.match(value.subtitle, /Loading saved inputs only/)
  assert.match(value.subtitle, /Use Run payroll to start calculation/)
  assert.equal(getStep(value, 'attendance').state, 'pending')
  assert.equal(getStep(value, 'calculation').state, 'pending')
  assert.equal(value.steps.some(step => step.state === 'active'), false)
  assert.equal(value.blockers.length, 0)
})
test('a prepared client selection still needs an explicit Run payroll action', () => {
  const value = journey(input())
  assert.equal(value.status, 'ready')
  assert.match(value.title, /Ready to run payroll/)
  assert.match(value.subtitle, /Use Run payroll to start calculation/)
  assert.equal(getStep(value, 'calculation').state, 'pending')
})
test('queued and processing runs show indeterminate calculation, not fictional completed checks', () => {
  for (const status of ['Queued', 'Processing']) {
    const value = journey(input({ run: run(status), diagnostics: diagnostics({ stepLogs: Array.from({ length: 59 }, (_, i) => step(i + 1)) }) }))
    assert.equal(value.status, 'running')
    assert.equal(getStep(value, 'calculation').state, 'active')
    assert.equal(getStep(value, 'salary').state, 'pending')
    assert.equal(getStep(value, 'validation').state, 'pending')
    assert.equal(getStep(value, 'lock').state, 'pending')
  }
})
test('synthetic final approval/freeze logs cannot approve or lock a draft', () => {
  const value = journey(input({ run: run('Draft'), diagnostics: diagnostics({ stepLogs: Array.from({ length: 59 }, (_, i) => step(i + 1)) }) }))
  assert.equal(value.status, 'review')
  assert.equal(getStep(value, 'validation').state, 'done')
  assert.equal(getStep(value, 'lock').state, 'active')
  assert.equal(getStep(value, 'approval').state, 'pending')
})
test('unloaded or wrong-run diagnostics never make validation green', () => {
  for (const report of [null, diagnostics({ payRunId: 42, stepLogs: Array.from({ length: 59 }, (_, i) => step(i + 1)) })]) {
    const value = journey(input({ run: run('Draft'), diagnostics: report }))
    assert.equal(getStep(value, 'calculation').state, 'done')
    assert.equal(getStep(value, 'validation').state, 'pending')
  }
})
test('actual blocking evidence is shown once across duplicated validation/exception arrays', () => {
  const issue = { id: 1, employeeCode: 'E004', message: 'Salary operand missing.', stepName: 'Formula Validation', isBlocking: true }
  const value = journey(input({ run: run('Failed'), diagnostics: diagnostics({ validationIssues: [issue], exceptions: [issue] }) }))
  assert.equal(value.status, 'blocked')
  assert.equal(value.blockers.length, 1)
  assert.equal(value.blockers[0].employeeCode, 'E004')
  assert.equal(getStep(value, 'salary').state, 'blocked')
})
test('failed run and failed reconciliation have actionable reasons without invented success', () => {
  const failed = journey(input({ run: { ...run('Failed'), processingError: 'Formula result is invalid.' } }))
  assert.match(failed.blockers[0].message, /Formula result/)
  const value = journey(input({ run: run('Draft'), diagnostics: diagnostics({ reconciliationResults: [{ checkName: 'Net pay', status: 'Failed', differenceAmount: 5 }] }) }))
  assert.equal(value.status, 'blocked')
  assert.match(value.blockers[0].message, /Net pay.*5/)
})
test('nonblocking warnings remain review hints rather than new business blockers', () => {
  const value = journey(input({ run: run('Draft'), diagnostics: diagnostics({ stepLogs: [step(3, { warning: 'Check configuration.' })], validationIssues: [{ stepName: 'Salary Structure Validation', message: 'Review optional field.', isBlocking: false }] }) }))
  assert.equal(value.status, 'review')
  assert.equal(value.blockers.length, 0)
  assert.equal(getStep(value, 'salary').state, 'warning')
})
test('saved approved run remains authoritative when live policy/roster/attendance selection changes', () => {
  const value = journey(input({ run: run('Approved'), policySelected: false, includedCount: 0, attendanceReady: false, attendanceIssues: 30, workingDays: 31, policyName: 'Different live policy' }))
  assert.equal(value.status, 'approved')
  assert.equal(value.blockers.length, 0)
  assert.match(getStep(value, 'scope').detail, /12 saved employees.*41/)
  assert.equal(getStep(value, 'lock').state, 'done')
  assert.equal(getStep(value, 'approval').state, 'done')
  assert.equal(getStep(value, 'validation').state, 'pending')
})
test('lock and approval remain distinct persisted human actions', () => {
  const value = journey(input({ run: run('Pending Approval') }))
  assert.equal(value.status, 'approval')
  assert.equal(getStep(value, 'lock').state, 'done')
  assert.equal(getStep(value, 'approval').state, 'active')
})
test('paid and partly paid results retain completed approval without claiming delivery or remainder settlement', () => {
  for (const status of ['Paid', 'Partially Paid']) {
    const value = journey(input({ run: run(status), diagnostics: diagnostics({ stepLogs: Array.from({ length: 59 }, (_, i) => step(i + 1)) }), policySelected: false, includedCount: 0, attendanceReady: false }))
    assert.equal(value.status, 'approved')
    assert.equal(getStep(value, 'calculation').state, 'done')
    assert.equal(getStep(value, 'validation').state, 'done')
    assert.equal(getStep(value, 'lock').state, 'done')
    assert.equal(getStep(value, 'approval').state, 'done')
    assert.match(value.title, new RegExp(status, 'i'))
    assert.doesNotMatch(JSON.stringify(value), /email delivered|payslips sent|remainder settled/i)
  }
})
test('foreign-client run cannot supply selected-client approvals or blockers', () => {
  const value = journey(input({ run: { ...run('Approved'), clientId: 8 } }))
  assert.equal(value.status, 'ready')
  assert.equal(getStep(value, 'approval').state, 'pending')
})
test('held draft does not use changed live attendance to overwrite its saved scope', () => {
  const value = journey(input({ run: { ...run('AwaitingProcessing'), employeeCount: 0 }, includedCount: 99, attendanceReady: false, attendanceIssues: 99 }))
  assert.equal(value.status, 'ready')
  assert.equal(value.blockers.length, 0)
  assert.match(getStep(value, 'scope').detail, /saved employee selection/)
  assert.doesNotMatch(getStep(value, 'scope').detail, /99/)
  assert.equal(getStep(value, 'attendance').state, 'pending')
})
