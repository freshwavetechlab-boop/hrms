import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { validateAnalyticsSql } from '../analytics/validator.mjs'
import { validateLocalSimpleCountPlan } from '../analytics/local-count-contract.mjs'

const options = { sourceDatabase: 'payroll', allowedTables: ['recruitment_candidate_applications', 'recruitment_open_positions', 'clients'] }
const contract = {
  version: 'simple-count-v1', recordTable: 'recruitment_candidate_applications', measure: 'count_records',
  grouping: { table: 'recruitment_open_positions', column: 'PositionTitle' },
  requestedFilters: [], recordDefinitionFilters: [{ table: 'recruitment_candidate_applications', column: 'ApplicationType', operator: '=', value: 'Application' }],
  noOtherFiltersRequested: true, allowedTables: options.allowedTables, scopeTables: [],
  requiredRelationships: [
    { leftTable: 'recruitment_candidate_applications', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' },
    { leftTable: 'recruitment_candidate_applications', leftColumn: 'PositionId', rightTable: 'recruitment_open_positions', rightColumn: 'Id', requiredJoinType: 'INNER JOIN' },
  ],
}
const valid = "SELECT p.PositionTitle AS job_title, COUNT(a.Id) AS applications FROM payroll.recruitment_candidate_applications a INNER JOIN payroll.recruitment_open_positions p ON p.Id=a.PositionId WHERE a.ApplicationType='Application' GROUP BY p.PositionTitle"
const check = (sql, focus = contract, parameters = []) => validateLocalSimpleCountPlan(validateAnalyticsSql(sql, options).ast, focus, parameters)

test('explicit jobs having applications contract accepts exact INNER and equivalent JOIN counts', () => {
  for (const sql of [valid, valid.replace('INNER JOIN', 'JOIN'), valid.replace('COUNT(a.Id)', 'COUNT(*)'), valid.replace('COUNT(a.Id)', 'COUNT(DISTINCT a.Id)')]) {
    assert.doesNotThrow(() => check(sql))
  }
  assert.doesNotThrow(() => check(valid.replace("='Application'", '=?'), contract, ['Application']))
  assert.doesNotThrow(() => check(valid.replace(/\b([ap])\./g, '`$1`.')))
})

for (const [reason, sql] of [
  ['missing application definition', valid.replace(" WHERE a.ApplicationType='Application'", '')],
  ['wrong SourceType definition', valid.replace('a.ApplicationType', 'a.SourceType')],
  ['wrong application definition value', valid.replace("='Application'", "='GlobalTalentPool'")],
  ['invented application status filter', valid.replace('GROUP BY', "AND a.CurrentStatus='Applied' GROUP BY")],
  ['invented job status filter', valid.replace('GROUP BY', "AND p.Status='Open' GROUP BY")],
  ['LEFT adds unassigned application group', valid.replace('INNER JOIN', 'LEFT JOIN')],
  ['wrong join column', valid.replace('a.PositionId', 'a.Id')],
  ['join includes a filter', valid.replace('WHERE', "AND p.Status='Open' WHERE")],
  ['wrong grouping', valid.replaceAll('p.PositionTitle', 'p.Status')],
  ['extra selected count', valid.replace('COUNT(a.Id) AS applications', 'COUNT(a.Id) AS applications, COUNT(p.Id) AS jobs')],
]) test(`rejects application-by-job drift: ${reason}`, () => assert.throws(() => check(sql), /requested metric/))

test('bare title grouping preserves the existing optional-parent LEFT JOIN rule', () => {
  const focus = { ...contract, requiredRelationships: contract.requiredRelationships.map(({ requiredJoinType, ...relationship }) => relationship) }
  assert.doesNotThrow(() => check(valid.replace('INNER JOIN', 'LEFT JOIN'), focus))
  assert.throws(() => check(valid, focus), /preserve records/)
  const clientFilter = { ...focus, requestedFilters: [{ table: 'clients', column: 'Code', operator: '=', value: 'RRU' }] }
  const clientSql = valid.replace('INNER JOIN', 'LEFT JOIN').replace('WHERE', 'INNER JOIN payroll.clients c ON c.Id=a.ClientId WHERE').replace('GROUP BY', "AND c.Code='RRU' GROUP BY")
  assert.doesNotThrow(() => check(clientSql, clientFilter))
  assert.throws(() => check(clientSql.replace('LEFT JOIN', 'INNER JOIN'), clientFilter), /preserve records/)
})

test('INNER metadata cannot relax client-parent scope or an unrelated relationship', () => {
  const scoped = { ...contract, scopeTables: ['clients'] }
  const scopedSql = valid.replace('WHERE', 'LEFT JOIN payroll.clients c ON c.Id=a.ClientId WHERE')
  assert.doesNotThrow(() => check(scopedSql, scoped))
  assert.throws(() => check(scopedSql.replace('LEFT JOIN payroll.clients', 'INNER JOIN payroll.clients'), scoped), /preserve records/)
  assert.throws(() => check(scopedSql, { ...scoped, requiredRelationships: scoped.requiredRelationships.map(value => ({ ...value, requiredJoinType: 'INNER JOIN' })) }), /unsupported host relationship join type/)
  assert.throws(() => check(valid, { ...contract, requiredRelationships: contract.requiredRelationships.map(value => value.requiredJoinType ? { ...value, requiredJoinType: 'LEFT JOIN' } : value) }), /unsupported host relationship join type/)
})

test('runtime rejects wrong SourceType then invented status during repair before query RPC', async () => {
  const child = spawn(process.execPath, ['runtime.mjs'], { cwd: new URL('..', import.meta.url), stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  let aiCalls = 0, queries = 0, error = ''
  child.stderr.resume()
  const lines = createInterface({ input: child.stdout })
  lines.on('line', line => {
    const message = JSON.parse(line)
    if (message.kind === 'ai') {
      aiCalls++
      child.stdin.write(JSON.stringify({ id: message.id, result: {
        payload: { sql: aiCalls === 1 ? valid.replace('a.ApplicationType', 'a.SourceType') : valid.replace('GROUP BY', "AND a.CurrentStatus='Applied' GROUP BY"), parameters: [] },
        model: 'local', ...(aiCalls === 1 ? { simpleCountContract: contract } : {}),
      } }) + '\n')
    }
    if (message.kind === 'query') { queries++; child.stdin.write(JSON.stringify({ id: message.id, result: [] }) + '\n') }
    if (message.kind === 'error') error = message.message
  })
  const completion = new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('Offline application job runtime test exceeded 5 seconds.')) }, 5000)
    child.on('error', error => { clearTimeout(timer); reject(error) })
    child.on('close', code => { clearTimeout(timer); resolve(code) })
  })
  child.stdin.write(JSON.stringify({ question: 'Show application counts by job title. Include jobs having applications only. All dates. All clients.', sourceDatabase: 'payroll',
    catalog: options.allowedTables.map(name => ({ name, columns: ['Id', 'PositionId', 'PositionTitle', 'ApplicationType', 'SourceType', 'CurrentStatus'].map(name => ({ name, type: 'varchar' })) })), rules: [],
  }) + '\n')
  assert.equal(await completion, 1)
  assert.equal(aiCalls, 2)
  assert.equal(queries, 0)
  assert.match(error, /WHERE must match exactly/)
})
