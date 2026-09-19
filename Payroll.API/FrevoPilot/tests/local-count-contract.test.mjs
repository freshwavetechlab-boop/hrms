import { test } from 'node:test'
import assert from 'node:assert/strict'
import { validateAnalyticsSql } from '../analytics/validator.mjs'
import { validateLocalSimpleCountPlan } from '../analytics/local-count-contract.mjs'
import { createQueryPlan, repairQueryPlan } from '../analytics/planner.mjs'
import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'

const options = { sourceDatabase: 'payroll', allowedTables: ['recruitment_interviews', 'recruitment_candidate_applications', 'employees', 'clients'] }
const focus = {
  version: 'simple-count-v1', recordTable: 'recruitment_interviews', measure: 'count_records',
  grouping: { table: 'recruitment_interviews', column: 'Status' },
  requestedFilters: [], recordDefinitionFilters: [], clientScope: 'All clients',
  timeScope: 'All dates; no date filter requested', noOtherFiltersRequested: true,
  allowedTables: ['recruitment_interviews'], requiredRelationships: [], scopeTables: [],
}
const valid = 'SELECT i.Status AS status, COUNT(*) AS total FROM payroll.recruitment_interviews i GROUP BY i.Status'
const check = (sql, contract = focus, parameters = []) => validateLocalSimpleCountPlan(validateAnalyticsSql(sql, options).ast, contract, parameters)

test('allows exact status record count and primary-key count variants', () => {
  for (const sql of [valid, valid.replace('COUNT(*)', 'COUNT(i.Id)'), valid.replace('COUNT(*)', 'COUNT(DISTINCT i.Id)'), `${valid} ORDER BY total DESC LIMIT 500`]) assert.doesNotThrow(() => check(sql))
  assert.doesNotThrow(() => check(valid.replace(/\bi\./g, '`i`.')))
  assert.doesNotThrow(() => check(valid.replace('COUNT(*)', 'COUNT(i.Id)').replace(/\bi\./g, '`i`.')))
})

for (const [reason, sql] of [
  ['omitted grouping', 'SELECT COUNT(*) total FROM payroll.recruitment_interviews i'],
  ['invented active filter', valid.replace('GROUP BY', 'WHERE i.IsActive=1 GROUP BY')],
  ['invented status filter', valid.replace('GROUP BY', "WHERE i.Status='Completed' GROUP BY")],
  ['wrong record count', valid.replaceAll('recruitment_interviews', 'employees')],
  ['wrong grouped field', valid.replaceAll('i.Status', 'i.Mode')],
  ['extra grouping', `${valid}, i.Mode`],
  ['nullable field count', valid.replace('COUNT(*)', 'COUNT(i.Status)')],
  ['distinct status count', valid.replace('COUNT(*)', 'COUNT(DISTINCT i.Status)')],
  ['filtered count expression', valid.replace('COUNT(*)', "COUNT(CASE WHEN i.Status='Completed' THEN 1 END)")],
  ['different measure', valid.replace('COUNT(*)', 'SUM(i.Id)')],
  ['HAVING drops groups', `${valid} HAVING COUNT(*) > 1`],
  ['early limit', `${valid} LIMIT 1`],
  ['offset drops groups', `${valid} LIMIT 1, 500`],
  ['nested record filter', valid.replace('GROUP BY', 'WHERE i.ApplicationId IN (SELECT Id FROM payroll.recruitment_candidate_applications) GROUP BY')],
  ['extra selected metric', valid.replace('COUNT(*) AS total', 'COUNT(*) AS total, MAX(i.Id) AS latest')],
  ['ambiguous output alias', valid.replace('AS total', 'AS status')],
]) test(`rejects local simple-count semantic drift: ${reason}`, () => assert.throws(() => check(sql), /requested metric/))

test('missing contract leaves cloud and unknown complex planning unchanged', () => assert.doesNotThrow(() => check('SELECT COUNT(*) FROM payroll.employees WHERE IsActive=1', null)))
test('unsupported host contract version fails closed', () => assert.throws(() => check(valid, { ...focus, version: 'future-v2' }), /unsupported host contract/))
test('quoted table qualifiers accept only identifier nodes, never arbitrary value expressions', () => {
  const ast = validateAnalyticsSql(valid, options).ast
  ast.groupby.columns[0].table = { type: 'single_quote_string', value: 'i' }
  assert.throws(() => validateLocalSimpleCountPlan(ast, focus), /requested grouping column/)
})

const applicationFocus = {
  ...focus, recordTable: 'recruitment_candidate_applications',
  grouping: { table: 'recruitment_candidate_applications', column: 'CurrentStatus' },
  allowedTables: ['recruitment_candidate_applications', 'clients'],
  recordDefinitionFilters: [{ table: 'recruitment_candidate_applications', column: 'ApplicationType', operator: '=', value: 'Application' }],
  requiredRelationships: [{ leftTable: 'recruitment_candidate_applications', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' }],
}
const applicationSql = 'SELECT a.CurrentStatus AS status, COUNT(a.Id) AS total FROM payroll.recruitment_candidate_applications a WHERE a.ApplicationType=? GROUP BY a.CurrentStatus'
test('requires exact host record-definition filter with ordered parameters', () => {
  assert.doesNotThrow(() => check(applicationSql, applicationFocus, ['Application']))
  assert.doesNotThrow(() => check(applicationSql.replace('=?', "='Application'"), applicationFocus))
  assert.throws(() => check(applicationSql.replace('WHERE a.ApplicationType=? ', ''), applicationFocus), /WHERE/)
  assert.throws(() => check(applicationSql, applicationFocus, ['GlobalTalentPool']), /WHERE/)
  assert.throws(() => check(applicationSql, applicationFocus, ['Application', 'extra']), /parameters/)
})
test('rejects unrequested client join even when its schema is available', () => {
  assert.throws(() => check(applicationSql.replace('WHERE', 'LEFT JOIN payroll.clients c ON c.Id=a.ClientId WHERE'), applicationFocus, ['Application']), /unrequested/)
})

const employeeFocus = {
  ...focus, recordTable: 'employees', grouping: { table: 'clients', column: 'Code' },
  allowedTables: ['employees', 'clients'],
  requiredRelationships: [{ leftTable: 'employees', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' }],
}
const clientSql = 'SELECT c.Code AS client_code, COUNT(e.Id) AS total FROM payroll.employees e LEFT JOIN payroll.clients c ON c.Id=e.ClientId GROUP BY c.Code'
test('allows required many-to-one grouping joins without losing unassigned records', () => {
  assert.doesNotThrow(() => check(clientSql, employeeFocus))
  assert.throws(() => check(clientSql.replace('LEFT JOIN', 'INNER JOIN'), employeeFocus), /preserve records/)
  assert.throws(() => check(clientSql.replace('c.Id=e.ClientId', 'c.Id=e.Id'), employeeFocus), /relationship/)
  assert.throws(() => check(clientSql.replace('GROUP BY', 'WHERE c.IsActive=1 GROUP BY'), employeeFocus), /WHERE/)
})
test('allows only the explicitly requested active and client filters', () => {
  const contract = { ...employeeFocus, requestedFilters: [
    { table: 'employees', column: 'IsActive', operator: '=', value: 1 },
    { table: 'clients', column: 'Code', operator: '=', value: 'RRU' },
  ] }
  const sql = clientSql.replace('LEFT JOIN', 'INNER JOIN').replace('GROUP BY', 'WHERE e.IsActive=? AND c.Code=? GROUP BY')
  assert.doesNotThrow(() => check(sql, contract, ['1', 'RRU']))
  assert.throws(() => check(sql, contract, ['RRU', '1']), /WHERE/)
})

test('planner uses only host metadata and repair cannot discard or overwrite it', async () => {
  const original = globalThis.frevoPilotProvider
  const context = { question: 'Count all interviews by status. All dates. All clients.', catalog: [], knowledge: [], semanticContext: {}, sourceDatabase: 'payroll' }
  try {
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: valid, simpleCountContract: { version: 'model-forged' } }, model: 'local', simpleCountContract: focus })
    const plan = await createQueryPlan(context)
    assert.deepEqual(plan.simpleCountContract, focus)
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: valid, simpleCountContract: null }, model: 'local', simpleCountContract: { ...focus, recordTable: 'employees' } })
    const repaired = await repairQueryPlan({ ...context, failedPlan: plan, validationError: new Error('retry') })
    assert.deepEqual(repaired.simpleCountContract, focus)
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: valid, simpleCountContract: focus }, model: 'cloud' })
    assert.equal((await createQueryPlan(context)).simpleCountContract, undefined)
  } finally { globalThis.frevoPilotProvider = original }
})

test('authorized scope parent is allowed, while its predicates remain host-owned', () => {
  const contract = { ...focus, clientScope: 'RRU', scopeTables: ['recruitment_candidate_applications'],
    allowedTables: ['recruitment_interviews', 'recruitment_candidate_applications', 'clients'],
    requiredRelationships: [
      { leftTable: 'recruitment_interviews', leftColumn: 'ApplicationId', rightTable: 'recruitment_candidate_applications', rightColumn: 'Id' },
      { leftTable: 'recruitment_candidate_applications', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' },
    ] }
  const sql = valid.replace('GROUP BY', 'INNER JOIN payroll.recruitment_candidate_applications a ON a.Id=i.ApplicationId GROUP BY')
  assert.doesNotThrow(() => check(sql, contract))
  assert.doesNotThrow(() => check(sql.replace('INNER JOIN', 'JOIN'), contract))
  assert.throws(() => check(sql.replace('INNER JOIN', 'LEFT JOIN'), contract), /INNER JOIN/)
  assert.throws(() => check(valid, contract), /relationship required/)
  assert.throws(() => check(sql.replace('GROUP BY', 'WHERE a.ClientId=20 GROUP BY'), contract), /WHERE/)
})

test('runtime refuses invalid initial and repaired counts before any query RPC', async () => {
  // Real Node runtime, but an entirely in-memory host: no provider or DB calls.
  const child = spawn(process.execPath, ['runtime.mjs'], { cwd: new URL('..', import.meta.url), stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  let queries = 0, aiCalls = 0, lastError = ''
  const lines = createInterface({ input: child.stdout })
  lines.on('line', line => {
    const message = JSON.parse(line)
    if (message.kind === 'ai') {
      aiCalls++
      // The repair response deliberately omits metadata: original contract must survive.
      child.stdin.write(JSON.stringify({ id: message.id, result: {
        payload: { sql: valid.replace('GROUP BY', 'WHERE i.IsActive=1 GROUP BY'), parameters: [] },
        model: 'local', ...(aiCalls === 1 ? { simpleCountContract: focus } : {}),
      } }) + '\n')
    }
    if (message.kind === 'query') { queries++; child.stdin.write(JSON.stringify({ id: message.id, result: [] }) + '\n') }
    if (message.kind === 'error') lastError = message.message
  })
  child.stdin.write(JSON.stringify({ question: 'Count interviews by status', sourceDatabase: 'payroll',
    catalog: [{ name: 'recruitment_interviews', columns: ['Id', 'Status', 'IsActive'].map(name => ({ name, type: 'int' })) }], rules: [],
  }) + '\n')
  const exitCode = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('Offline runtime test exceeded 5 seconds.')) }, 5000)
    child.on('error', error => { clearTimeout(timer); reject(error) })
    child.on('close', code => { clearTimeout(timer); resolve(code) })
  })
  assert.equal(exitCode, 1)
  assert.equal(aiCalls, 2)
  assert.equal(queries, 0)
  assert.match(lastError, /WHERE must match exactly/)
})
