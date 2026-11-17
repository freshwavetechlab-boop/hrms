import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/regularPayrollLocations.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { regularPayrollLocations } = exports
const locations = [{ id: 1, clientId: 7, isActive: true, name: 'Mumbai' }, { id: 2, clientId: 7, isActive: true, name: 'Delhi' }, { id: 3, clientId: 8, isActive: true, name: 'Other client' }]
const context = { clientId: 7, month: '2026-10', rows: [{ employeeId: 30, workLocationId: 1, workLocationName: 'Mumbai', lockedPayRunId: 12 }, { employeeId: 30, workLocationId: 1, workLocationName: 'Mumbai', lockedPayRunId: 12 }] }
const run = { id: 12, clientId: 7, payPeriod: '2026-10', runType: 'Regular', requestJson: '{"includedEmployeeIds":[30]}' }
test('counts unique employees, retains empty locations and ties saved runs to the selected month', () => {
  const rows = regularPayrollLocations(7, '2026-10', locations, [], context, [run])
  assert.equal(rows.length, 2)
  assert.equal(rows.find(row => row.id === 1).count, 1)
  assert.deepEqual(rows.find(row => row.id === 1).runIds, [12])
  assert.equal(rows.find(row => row.id === 2).count, 0)
})
test('other month/client/off-cycle runs cannot mark a location as having this payroll', () => {
  for (const patch of [{ clientId: 8 }, { payPeriod: '2026-09' }, { runType: 'Off Cycle' }]) assert.deepEqual(regularPayrollLocations(7, '2026-10', locations, [], context, [{ ...run, ...patch }]).find(row => row.id === 1).runIds, [])
})
test('outdated location context is not treated as this month attendance', () => {
  const rows = regularPayrollLocations(7, '2026-11', locations, [], context, [run])
  assert.equal(rows.find(row => row.id === 1).count, 0)
  assert.deepEqual(rows.find(row => row.id === 1).runIds, [])
})
test('legacy request text does not guess membership from title or transfer', () => {
  const legacy = { ...run, requestJson: 'invalid', runName: 'Mumbai payroll' }
  assert.deepEqual(regularPayrollLocations(7, '2026-10', locations, [], { ...context, rows: context.rows.map(row => ({ ...row, lockedPayRunId: null })) }, [legacy]).find(row => row.id === 1).runIds, [])
})
