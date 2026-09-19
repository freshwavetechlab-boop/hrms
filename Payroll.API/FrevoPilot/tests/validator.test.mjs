import { test } from 'node:test'
import assert from 'node:assert/strict'
import { validateAnalyticsSql, applyClientScope } from '../analytics/validator.mjs'
import { buildChart } from '../analytics/chart.mjs'
const options = { sourceDatabase: 'payroll', allowedTables: ['employees', 'clients'] }
for (const sql of [
  'SELECT * FROM payroll.employees', 'SELECT e.* FROM payroll.employees e',
  'SELECT PasswordHash FROM payroll.employees', 'SELECT ApiKeyCipherText FROM payroll.employees',
  'SELECT email FROM payroll.employees', 'SELECT COUNT(*) FROM other.employees',
  'DELETE FROM payroll.employees', 'SELECT COUNT(*) FROM payroll.employees; DROP TABLE clients',
  'SELECT SLEEP(5) FROM payroll.employees', 'SELECT dangerous_function(Id) FROM payroll.employees',
  'SELECT Id FROM payroll.employees LIMIT 501', 'SELECT Id FROM payroll.employees FOR UPDATE',
  'SELECT Id INTO OUTFILE \'output\' FROM payroll.employees', 'SELECT COUNT(*) FROM payroll.users',
  'SELECT PersonalJson FROM payroll.employees', 'SELECT EmployeeCode FROM payroll.employees',
  'SELECT COUNT(*) FROM payroll.employees UNION SELECT COUNT(*) FROM payroll.clients',
]) test(`blocks unsafe query: ${sql}`, () => assert.throws(() => validateAnalyticsSql(sql, options)))
test('allows bounded aggregate, not credentials', () => {
  const result = validateAnalyticsSql('SELECT c.Name client, COUNT(e.Id) total FROM payroll.clients c LEFT JOIN payroll.employees e ON e.ClientId=c.Id GROUP BY c.Id,c.Name', options)
  assert.match(result.sql, /LIMIT 500/)
})
test('client scope is enforced before execution', () => {
  const scoped = applyClientScope('SELECT COUNT(*) total FROM payroll.employees e', 20, [{ name: 'employees', columns: [{ name: 'ClientId' }] }])
  assert.match(scoped, /ClientId.*20/i)
  assert.doesNotThrow(() => validateAnalyticsSql(scoped, options))
})
test('empty results do not fabricate chart points', () => assert.deepEqual(buildChart([], { presentationMode: 'dashboard' }).datasets, []))

test('missing metrics remain missing, not zero', () => {
  const chart = buildChart([{ status: 'A', score: 70 }, { status: 'B', score: null }], { labelField: 'status', valueFields: ['score'], chartType: 'bar' })
  assert.deepEqual(chart.datasets[0].data, [70, null])
})

test('optional joins keep unassigned rows when client scope is enforced', () => {
  const result = applyClientScope('SELECT COUNT(*) total FROM payroll.employees e LEFT JOIN payroll.worklocations w ON w.Id=e.WorkLocationId', 28,
    [{ name: 'employees', columns: [{ name: 'ClientId' }] }, { name: 'worklocations', columns: [{ name: 'ClientId' }] }])
  assert.match(result, /ON.*`w`.`ClientId` = 28.*WHERE.*`e`.`ClientId` = 28/)
  assert.doesNotMatch(result, /WHERE.*`w`.`ClientId`/)
})

test('nested aggregates are scoped, and unscopable children fail closed', () => {
  const catalog = [{ name: 'employees', columns: [{ name: 'ClientId' }] }]
  const result = applyClientScope('SELECT SUM(x.n) total FROM (SELECT COUNT(*) n FROM payroll.employees e) x', 28, catalog)
  assert.match(result, /WHERE.*ClientId.*28/)
  assert.throws(() => applyClientScope('SELECT (SELECT COUNT(*) FROM payroll.interviews) total FROM payroll.employees e', 28, catalog))
})

test('a LEFT scoped parent cannot authorize an unscoped base record count', () => {
  const catalog = [
    { name: 'recruitment_interviews', columns: [{ name: 'ApplicationId' }, { name: 'Status' }] },
    { name: 'recruitment_candidate_applications', columns: [{ name: 'Id' }, { name: 'ClientId' }] },
  ]
  const sql = 'SELECT i.Status, COUNT(*) total FROM payroll.recruitment_interviews i LEFT JOIN payroll.recruitment_candidate_applications a ON a.Id=i.ApplicationId GROUP BY i.Status'
  assert.throws(() => applyClientScope(sql, 28, catalog), /Use INNER JOIN for the required client-owned parent/)
  const scoped = applyClientScope(sql.replace('LEFT JOIN', 'INNER JOIN'), 28, catalog)
  assert.match(scoped, /INNER JOIN.*WHERE `a`.`ClientId` = 28.*GROUP BY/)
  assert.doesNotMatch(scoped, /ON.*ClientId.*WHERE/)
  assert.doesNotThrow(() => validateAnalyticsSql(scoped, { sourceDatabase: 'payroll', allowedTables: catalog.map(table => table.name) }))
})

test('nested unscoped base records cannot hide behind a LEFT scoped parent', () => {
  const catalog = [{ name: 'employees', columns: [{ name: 'ClientId' }] },
    { name: 'recruitment_interviews', columns: [{ name: 'ApplicationId' }] },
    { name: 'recruitment_candidate_applications', columns: [{ name: 'Id' }, { name: 'ClientId' }] }]
  const inner = 'SELECT COUNT(*) n FROM payroll.recruitment_interviews i LEFT JOIN payroll.recruitment_candidate_applications a ON a.Id=i.ApplicationId'
  assert.throws(() => applyClientScope(`SELECT SUM(x.n) total FROM (${inner}) x`, 28, catalog), /Use INNER JOIN/)
  assert.doesNotThrow(() => applyClientScope(`SELECT SUM(x.n) total FROM (${inner.replace('LEFT JOIN', 'INNER JOIN')}) x`, 28, catalog))
})
