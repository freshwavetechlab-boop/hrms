import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { validateAnalyticsSql } from '../analytics/validator.mjs'
import { validateLocalSimpleCountPlan } from '../analytics/local-count-contract.mjs'
import { createQueryPlan, repairQueryPlan } from '../analytics/planner.mjs'

const options = { sourceDatabase: 'payroll', allowedTables: ['essleaverequests', 'employees', 'clients'] }
const sumContract = {
  version: 'simple-count-v1', recordTable: 'essleaverequests', measure: 'sum_values',
  measureColumn: { table: 'essleaverequests', column: 'Days' },
  grouping: { table: 'essleaverequests', column: 'Status' },
  requestedFilters: [], recordDefinitionFilters: [], clientScope: 'All clients',
  timeScope: 'All dates; no date filter requested', noOtherFiltersRequested: true,
  allowedTables: ['essleaverequests'], requiredRelationships: [], scopeTables: [],
}
const averageContract = { ...sumContract, measure: 'average_values' }
const roundedAverageContract = { ...averageContract, roundDigits: 2 }
const sumSql = 'SELECT l.Status AS status, SUM(l.Days) AS total_days FROM payroll.essleaverequests l GROUP BY l.Status'
const averageSql = sumSql.replace('SUM(l.Days) AS total_days', 'AVG(l.Days) AS average_days')
const roundedAverageSql = averageSql.replace('AVG(l.Days)', 'ROUND(AVG(l.Days), 2)')
const astFor = sql => validateAnalyticsSql(sql, options).ast
const check = (sql, contract = sumContract, parameters = []) => validateLocalSimpleCountPlan(astFor(sql), contract, parameters)

test('accepts exact host-requested SUM and AVG, with grouping retained in SELECT and GROUP BY', () => {
  for (const [sql, contract] of [[sumSql, sumContract], [averageSql, averageContract], [roundedAverageSql, roundedAverageContract]]) {
    assert.doesNotThrow(() => check(sql, contract))
    assert.doesNotThrow(() => check(sql.replace(/\bl\./g, '`l`.'), contract))
    assert.doesNotThrow(() => check(`${sql} ORDER BY 2 DESC LIMIT 500`, contract))
    assert.doesNotThrow(() => check(sql.replace(/\bl\./g, ''), contract))
  }
  assert.doesNotThrow(() => check(`${sumSql} ORDER BY total_days DESC`))
  assert.doesNotThrow(() => check(`${sumSql} ORDER BY SUM(l.Days) DESC`))
})

test('wrong record table supplies a host-grounded repair hint without echoing generated SQL', () => {
  const wrong = 'SELECT e.Status AS status, ROUND(AVG(e.Days), 2) AS average_days FROM employees e GROUP BY e.Status'
  assert.throws(() => check(wrong, roundedAverageContract), error => {
    assert.match(error.message, /Start FROM essleaverequests/)
    assert.doesNotMatch(error.message, /FROM employees|e\.Status|e\.Days/)
    return true
  })
})

test('optional client parent is not a requested join and repair explicitly removes it', () => {
  const contract = { ...roundedAverageContract, allowedTables: ['essleaverequests', 'clients'],
    requiredRelationships: [{ leftTable: 'essleaverequests', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' }] }
  const extraJoin = roundedAverageSql.replace('GROUP BY', 'LEFT JOIN clients c ON c.Id=l.ClientId GROUP BY')
  assert.throws(() => check(extraJoin, contract), /Remove every JOIN: this metric uses only essleaverequests/)
})

for (const [reason, sql] of [
  ['dropped grouping returns a combined total', 'SELECT SUM(l.Days) AS total_days FROM payroll.essleaverequests l'],
  ['group omitted from SELECT', sumSql.replace('l.Status AS status, ', '')],
  ['GROUP BY omitted', sumSql.replace(' GROUP BY l.Status', '')],
  ['wrong grouping column', sumSql.replaceAll('l.Status', 'l.LeaveType')],
  ['extra grouping', `${sumSql}, l.LeaveType`],
  ['invented status filter', sumSql.replace('GROUP BY', "WHERE l.Status='Approved' GROUP BY")],
  ['invented active filter', sumSql.replace('GROUP BY', 'WHERE l.IsActive=1 GROUP BY')],
  ['invented date filter', sumSql.replace('GROUP BY', "WHERE l.StartDate='2026-01-01' GROUP BY")],
  ['COUNT replaces SUM', sumSql.replace('SUM(l.Days)', 'COUNT(*)')],
  ['AVG replaces SUM', sumSql.replace('SUM(l.Days)', 'AVG(l.Days)')],
  ['wrong aggregate column', sumSql.replace('SUM(l.Days)', 'SUM(l.Id)')],
  ['transformed aggregate column', sumSql.replace('SUM(l.Days)', 'SUM(l.Days + 1)')],
  ['conditional aggregate column', sumSql.replace('SUM(l.Days)', "SUM(CASE WHEN l.Status='Approved' THEN l.Days ELSE 0 END)")],
  ['unrequested rounding', sumSql.replace('SUM(l.Days)', 'ROUND(SUM(l.Days), 2)')],
  ['extra output', sumSql.replace('SUM(l.Days) AS total_days', 'SUM(l.Days) AS total_days, COUNT(*) AS total')],
  ['colliding output aliases', sumSql.replace('AS total_days', 'AS status')],
  ['unrequested join', sumSql.replace('GROUP BY', 'LEFT JOIN payroll.employees e ON e.Id=l.EmployeeId GROUP BY')],
  ['HAVING removes groups', `${sumSql} HAVING SUM(l.Days)>1`],
  ['early limit removes groups', `${sumSql} LIMIT 1`],
  ['offset removes groups', `${sumSql} LIMIT 1, 500`],
  ['unrequested ordering measure', `${sumSql} ORDER BY COUNT(*)`],
]) test(`rejects local aggregate semantic drift: ${reason}`, () => assert.throws(() => check(sql), /requested metric/))

test('AVG requires the exact column and permits ROUND only at explicitly requested precision', () => {
  assert.throws(() => check(roundedAverageSql, averageContract), /requested metric/)
  assert.throws(() => check(averageSql, roundedAverageContract), /requested metric/)
  assert.throws(() => check(sumSql, averageContract), /requested metric/)
  for (const changed of [
    roundedAverageSql.replace('AVG(l.Days)', 'COUNT(*)'),
    roundedAverageSql.replace('AVG(l.Days)', 'AVG(l.Id)'),
    roundedAverageSql.replace(', 2)', ', 1)'),
    roundedAverageSql.replace(', 2)', ', 3)'),
    roundedAverageSql.replace(', 2)', ', ?)'),
    roundedAverageSql.replace('ROUND(AVG(l.Days), 2)', 'AVG(ROUND(l.Days, 2))'),
    roundedAverageSql.replace('ROUND(AVG(l.Days), 2)', 'ROUND(AVG(l.Days) + 1, 2)'),
  ]) assert.throws(() => check(changed, roundedAverageContract), /requested metric/)
})

test('rejects aggregate distinct/filter/window modifiers structurally', () => {
  for (const change of [
    expr => { expr.args.distinct = 'DISTINCT' },
    expr => { expr.filter = { where: { type: 'bool', value: true } } },
    expr => { expr.over = { type: 'window' } },
  ]) {
    const ast = astFor(averageSql)
    change(ast.columns[1].expr)
    assert.throws(() => validateLocalSimpleCountPlan(ast, averageContract), /requested metric/)
  }
})

test('malformed aggregate host metadata fails closed', () => {
  for (const contract of [
    { ...sumContract, measureColumn: undefined },
    { ...sumContract, measureColumn: { table: 'employees', column: 'Days' } },
    { ...sumContract, roundDigits: 2 },
    { ...averageContract, roundDigits: 1 },
    { ...averageContract, roundDigits: '2' },
  ]) assert.throws(() => check(sumSql, contract), /unsupported host aggregate contract/)
})

test('aggregate filters and scope relationships retain the existing host-only rules', () => {
  const filtered = { ...sumContract, requestedFilters: [{ table: 'essleaverequests', column: 'Status', operator: '=', value: 'Approved' }] }
  const filteredSql = sumSql.replace('GROUP BY', 'WHERE l.Status=? GROUP BY')
  assert.doesNotThrow(() => check(filteredSql, filtered, ['Approved']))
  assert.throws(() => check(filteredSql, filtered, ['Pending']), /WHERE must match exactly/)
  assert.throws(() => check(filteredSql, sumContract, ['Approved']), /WHERE must match exactly/)
  const scoped = { ...sumContract, allowedTables: ['essleaverequests', 'employees'], scopeTables: ['employees'],
    requiredRelationships: [{ leftTable: 'essleaverequests', leftColumn: 'EmployeeId', rightTable: 'employees', rightColumn: 'Id' }] }
  const scopedSql = sumSql.replace('GROUP BY', 'LEFT JOIN payroll.employees e ON e.Id=l.EmployeeId GROUP BY')
  assert.doesNotThrow(() => check(scopedSql, scoped))
  assert.throws(() => check(sumSql, scoped), /relationship required/)
  assert.throws(() => check(scopedSql.replace('GROUP BY', 'WHERE e.ClientId=20 GROUP BY'), scoped), /WHERE must match exactly/)
})

test('aggregate contract remains host-derived and cannot be replaced during repair', async () => {
  const original = globalThis.frevoPilotProvider
  const context = { question: 'Show total leave days by status. All dates. All clients.', catalog: [], knowledge: [], semanticContext: {}, sourceDatabase: 'payroll' }
  try {
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: sumSql, simpleCountContract: averageContract }, model: 'local', simpleCountContract: sumContract })
    const initial = await createQueryPlan(context)
    assert.deepEqual(initial.simpleCountContract, sumContract)
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: averageSql, simpleCountContract: averageContract }, model: 'local', simpleCountContract: averageContract })
    const repaired = await repairQueryPlan({ ...context, failedPlan: initial, validationError: new Error('retry') })
    assert.deepEqual(repaired.simpleCountContract, sumContract)
    assert.throws(() => check(repaired.sql, repaired.simpleCountContract), /requested metric/)
  } finally { globalThis.frevoPilotProvider = original }
})

async function runOffline(sqls, contract, rows = []) {
  // Actual runtime protocol with an in-memory host; no provider or database calls.
  const child = spawn(process.execPath, ['runtime.mjs'], { cwd: new URL('..', import.meta.url), stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  const observed = { aiCalls: 0, queries: [], error: '', result: null }
  const lines = createInterface({ input: child.stdout })
  child.stderr.resume()
  lines.on('line', line => {
    const message = JSON.parse(line)
    if (message.kind === 'ai') {
      const index = observed.aiCalls++
      child.stdin.write(JSON.stringify({ id: message.id, result: {
        payload: index < sqls.length
          ? { sql: sqls[index], parameters: [], labelField: 'status', valueFields: [contract.measure === 'sum_values' ? 'total_days' : 'average_days'] }
          : { summary: 'Offline fixture summary.', recommendations: [], risks: [] },
        model: 'local', providerCode: 'LocalOpenAICompatible',
        ...(index === 0 ? { simpleCountContract: contract } : {}),
      } }) + '\n')
    }
    if (message.kind === 'query') {
      observed.queries.push(message.sql)
      child.stdin.write(JSON.stringify({ id: message.id, result: rows }) + '\n')
    }
    if (message.kind === 'error') observed.error = message.message
    if (message.kind === 'result') observed.result = message.result
  })
  const completion = new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('Offline aggregate runtime test exceeded 5 seconds.')) }, 5000)
    child.on('error', error => { clearTimeout(timer); reject(error) })
    child.on('close', code => { clearTimeout(timer); resolve(code) })
  })
  child.stdin.write(JSON.stringify({ question: 'Show leave days by status. All dates. All clients.', sourceDatabase: 'payroll',
    catalog: [{ name: 'essleaverequests', columns: ['Id', 'Status', 'Days'].map(name => ({ name, type: 'int' })) }], rules: [],
  }) + '\n')
  observed.exitCode = await completion
  return observed
}

test('runtime rejects invalid initial and repaired SUM/AVG plans before any query RPC', async () => {
  for (const [contract, invalidInitial, invalidRepair] of [
    [sumContract, 'SELECT SUM(l.Days) AS total_days FROM payroll.essleaverequests l', sumSql.replace('GROUP BY', "WHERE l.Status='Approved' GROUP BY")],
    [sumContract, sumSql.replace('SUM(l.Days)', 'COUNT(*)'), sumSql.replace('SUM(l.Days)', 'SUM(l.Id)')],
    [roundedAverageContract, averageSql, roundedAverageSql.replace(', 2)', ', 1)')],
  ]) {
    const observed = await runOffline([invalidInitial, invalidRepair], contract)
    assert.equal(observed.exitCode, 1)
    assert.equal(observed.aiCalls, 2)
    assert.deepEqual(observed.queries, [])
    assert.equal(observed.result, null)
    assert.match(observed.error, /requested metric/)
  }
})

test('runtime executes the corrected grouped SUM once and preserves host result rows', async () => {
  const fixtureRows = [{ status: 'Approved', total_days: 6 }, { status: 'Pending', total_days: 2 }]
  const observed = await runOffline(['SELECT SUM(l.Days) AS total_days FROM payroll.essleaverequests l', sumSql], sumContract, fixtureRows)
  assert.equal(observed.exitCode, 0)
  assert.equal(observed.queries.length, 1)
  assert.match(observed.queries[0], /GROUP BY l.Status/)
  assert.deepEqual(observed.result.rows, fixtureRows)
})
