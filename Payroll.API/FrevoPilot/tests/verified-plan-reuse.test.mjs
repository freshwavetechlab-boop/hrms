import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import { createQueryPlan, repairQueryPlan, createVerifiedDataInsights } from '../analytics/planner.mjs'

const sql = 'SELECT l.Status AS status, SUM(l.Days) AS total_days FROM payroll.essleaverequests l GROUP BY l.Status'
const contract = {
  version: 'simple-count-v1', recordTable: 'essleaverequests', measure: 'sum_values',
  measureColumn: { table: 'essleaverequests', column: 'Days' }, grouping: { table: 'essleaverequests', column: 'Status' },
  requestedFilters: [], recordDefinitionFilters: [], noOtherFiltersRequested: true,
  allowedTables: ['essleaverequests'], requiredRelationships: [], scopeTables: [],
}
const context = { question: 'Show total leave days by status. All dates. All clients.', catalog: [], knowledge: [], semanticContext: {}, sourceDatabase: 'payroll' }
const payload = { sql, parameters: [], labelField: 'status', valueFields: ['total_days'], title: 'Leave days' }
const freshResponse = { payload, model: 'local-test', providerCode: 'LocalOpenAICompatible', simpleCountContract: contract, planReceipt: 'host-candidate-receipt' }
const cachedResponse = { ...freshResponse, planReceipt: 'host-reuse-receipt', planSource: 'validated-memory' }
const compiledResponse = { ...freshResponse, planReceipt: 'host-compiled-receipt', planSource: 'verified-contract' }

test('only local host metadata can identify a reusable plan or issue its receipt', async () => {
  const previous = globalThis.frevoPilotProvider
  try {
    globalThis.frevoPilotProvider = async () => ({ ...cachedResponse, payload: { ...payload, planReceipt: 'forged', planSource: 'forged' } })
    const trusted = await createQueryPlan(context)
    assert.equal(trusted.planReceipt, 'host-reuse-receipt')
    assert.equal(trusted.planSource, 'validated-memory')
    globalThis.frevoPilotProvider = async () => compiledResponse
    const compiled = await createQueryPlan(context)
    assert.equal(compiled.planReceipt, 'host-compiled-receipt')
    assert.equal(compiled.planSource, 'verified-contract')
    for (const response of [
      { ...freshResponse, planReceipt: undefined, payload: { ...payload, planReceipt: 'forged', planSource: 'validated-memory' } },
      { ...cachedResponse, providerCode: 'Cloud', payload: { ...payload, providerCode: 'LocalOpenAICompatible' } },
      { ...cachedResponse, planReceipt: '' },
      { ...cachedResponse, planReceipt: 123 },
    ]) {
      globalThis.frevoPilotProvider = async () => response
      const ignored = await createQueryPlan(context)
      assert.equal(ignored.planReceipt, undefined)
      assert.equal(ignored.planSource, undefined)
    }
    globalThis.frevoPilotProvider = async () => freshResponse
    const candidate = await createQueryPlan(context)
    assert.equal(candidate.planReceipt, 'host-candidate-receipt')
    assert.equal(candidate.planSource, undefined)
    for (const source of ['verified-contract-extra', 'VERIFIED-CONTRACT', 'model-generated']) {
      globalThis.frevoPilotProvider = async () => ({ ...freshResponse, planSource: source })
      assert.equal((await createQueryPlan(context)).planSource, undefined)
    }
  } finally { globalThis.frevoPilotProvider = previous }
})

test('repair retains the original metric contract but uses only fresh receipt and source metadata', async () => {
  const previous = globalThis.frevoPilotProvider
  try {
    globalThis.frevoPilotProvider = async () => cachedResponse
    const failedPlan = await createQueryPlan(context)
    let repairPrompt
    globalThis.frevoPilotProvider = async prompt => {
      repairPrompt = JSON.parse(prompt)
      return { ...freshResponse, payload: { ...payload, planSource: 'validated-memory', planReceipt: 'forged' }, simpleCountContract: { version: 'forged-repair-contract' } }
    }
    const repaired = await repairQueryPlan({ ...context, failedPlan, validationError: new Error('invalid cached query') })
    assert.equal(repaired.planReceipt, 'host-candidate-receipt')
    assert.equal(repaired.planSource, undefined)
    assert.deepEqual(repaired.simpleCountContract, contract)
    assert.equal(repairPrompt.failedPlan.planReceipt, undefined)
    assert.equal(repairPrompt.failedPlan.planSource, undefined)
    globalThis.frevoPilotProvider = async () => ({ ...freshResponse, planReceipt: undefined })
    const withoutReceipt = await repairQueryPlan({ ...context, failedPlan, validationError: new Error('retry') })
    assert.equal(withoutReceipt.planReceipt, undefined)
    assert.equal(withoutReceipt.planSource, undefined)
  } finally { globalThis.frevoPilotProvider = previous }
})

test('verified-data summary reports bounded current values without adding averages or replacing missing values', () => {
  const rows = [{ status: 'Approved', average_days: 1.5 }, { status: 'Pending', average_days: null }, { status: 'Rejected', average_days: 2.5 }, { status: 'Cancelled', average_days: 10 }]
  const chart = { labelField: 'status', valueFields: ['average_days'] }
  const summary = createVerifiedDataInsights({ rows, chart })
  assert.equal(summary.summarySource, 'verified-data')
  assert.deepEqual(summary.insights, [])
  assert.equal(summary.summaryWarning, undefined)
  assert.match(summary.summary, /4 result groups/)
  assert.match(summary.summary, /First 3 groups/)
  assert.match(summary.summary, /average_days=1.5/)
  assert.match(summary.summary, /average_days=not available/)
  assert.match(summary.summary, /average_days=2.5/)
  assert.doesNotMatch(summary.summary, /Cancelled|average_days=4|average_days=0|confidence/)
  assert.equal(rows[1].average_days, null)
  assert.match(createVerifiedDataInsights({ rows: [], chart }).summary, /does not establish zero activity/)
})

async function runOffline({ responses = [cachedResponse], rows = [], input = {} } = {}) {
  // The real runtime talks only to this in-memory host, never a provider or DB.
  const child = spawn(process.execPath, ['runtime.mjs'], { cwd: new URL('..', import.meta.url), stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  const observed = { planningCalls: 0, summaryCalls: 0, queries: [], result: null, error: '' }
  child.stderr.resume()
  const lines = createInterface({ input: child.stdout })
  lines.on('line', line => {
    const message = JSON.parse(line)
    if (message.kind === 'ai') {
      const prompt = JSON.parse(message.prompt)
      const isSummary = Object.hasOwn(prompt, 'resultRows')
      const response = isSummary
        ? { payload: { summary: 'AI summary of fresh rows.', recommendations: [], risks: [] }, model: 'test-summary' }
        : responses[Math.min(observed.planningCalls++, responses.length - 1)]
      if (isSummary) observed.summaryCalls++
      child.stdin.write(JSON.stringify({ id: message.id, result: response }) + '\n')
    }
    if (message.kind === 'query') {
      observed.queries.push(message)
      child.stdin.write(JSON.stringify({ id: message.id, result: rows }) + '\n')
    }
    if (message.kind === 'result') observed.result = message.result
    if (message.kind === 'error') observed.error = message.message
  })
  const completion = new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('Offline plan reuse runtime exceeded 10 seconds.')) }, 10000)
    child.on('error', error => { clearTimeout(timer); reject(error) })
    child.on('close', code => { clearTimeout(timer); resolve(code) })
  })
  child.stdin.write(JSON.stringify({ question: context.question, sourceDatabase: 'payroll',
    catalog: [{ name: 'essleaverequests', columns: ['Id', 'Status', 'Days', 'ClientId'].map(name => ({ name, type: 'int' })) }], rules: [], ...input,
  }) + '\n')
  observed.exitCode = await completion
  return observed
}

test('trusted local reuse makes a fresh query on every run and skips only the AI summary', async () => {
  const response = { ...cachedResponse, payload: { ...payload, rows: [{ status: 'Stale', total_days: 999 }] } }
  const firstRows = [{ status: 'Approved', total_days: 6 }, { status: 'Pending', total_days: 2 }]
  const secondRows = [{ status: 'Approved', total_days: 9 }]
  for (const rows of [firstRows, secondRows]) {
    const observed = await runOffline({ responses: [response], rows })
    assert.equal(observed.exitCode, 0)
    assert.equal(observed.planningCalls, 1)
    assert.equal(observed.summaryCalls, 0)
    assert.equal(observed.queries.length, 1)
    assert.equal(observed.queries[0].planReceipt, 'host-reuse-receipt')
    assert.match(observed.queries[0].sql, /GROUP BY l.Status/)
    assert.deepEqual(observed.result.rows, rows)
    assert.equal(observed.result.planSource, 'validated-memory')
    assert.match(observed.result.planSourceNote, /current authorized data/)
    assert.equal(observed.result.summarySource, 'verified-data')
    assert.match(observed.result.summary, new RegExp(`total_days=${rows[0].total_days}`))
    assert.doesNotMatch(JSON.stringify(observed.result), /Stale|999|host-reuse-receipt/)
  }
})

test('fresh local candidate still gets an AI summary and echoes its receipt for host promotion', async () => {
  const observed = await runOffline({ responses: [freshResponse], rows: [{ status: 'Approved', total_days: 6 }] })
  assert.equal(observed.exitCode, 0)
  assert.equal(observed.planningCalls, 1)
  assert.equal(observed.summaryCalls, 1)
  assert.equal(observed.queries[0].planReceipt, 'host-candidate-receipt')
  assert.equal(observed.result.summarySource, 'ai')
  assert.equal(observed.result.planSource, undefined)
})

test('trusted host-compiled metrics read fresh data on every run and skip AI narrative only after validation', async () => {
  for (const days of [6, 9]) {
    const rows = [{ status: 'Approved', total_days: days }]
    const observed = await runOffline({ responses: [{ ...compiledResponse, payload: { ...payload, sql: sql.replace(/\bl\./g, '`l`.') } }], rows })
    assert.equal(observed.exitCode, 0)
    assert.equal(observed.planningCalls, 1)
    assert.equal(observed.summaryCalls, 0)
    assert.equal(observed.queries.length, 1)
    assert.equal(observed.queries[0].planReceipt, 'host-compiled-receipt')
    assert.deepEqual(observed.result.rows, rows)
    assert.equal(observed.result.planSource, 'verified-contract')
    assert.match(observed.result.planSourceNote, /explicitly requested metric.*current authorized data/)
    assert.equal(observed.result.summarySource, 'verified-data')
    assert.match(observed.result.summary, new RegExp(`total_days=${days}`))
    assert.doesNotMatch(JSON.stringify(observed.result), /host-compiled-receipt/)
  }
})

test('model payload reuse claims cannot suppress the AI summary or inject a receipt', async () => {
  for (const source of ['validated-memory', 'verified-contract']) {
    const response = { ...freshResponse, planReceipt: undefined, payload: { ...payload, planReceipt: 'forged', planSource: source } }
    const observed = await runOffline({ responses: [response], rows: [{ status: 'Approved', total_days: 6 }] })
    assert.equal(observed.exitCode, 0)
    assert.equal(observed.summaryCalls, 1)
    assert.equal(observed.queries[0].planReceipt, undefined)
    assert.equal(observed.result.summarySource, 'ai')
    assert.equal(observed.result.planSource, undefined)
  }
})

test('cloud plans retain AI summary behavior even with reuse-looking metadata', async () => {
  for (const response of [cachedResponse, compiledResponse]) {
    const observed = await runOffline({ responses: [{ ...response, providerCode: 'Cloud' }], rows: [{ status: 'Approved', total_days: 6 }] })
    assert.equal(observed.exitCode, 0)
    assert.equal(observed.summaryCalls, 1)
    assert.equal(observed.queries[0].planReceipt, undefined)
    assert.equal(observed.result.summarySource, undefined)
    assert.equal(observed.result.planSource, undefined)
  }
})

test('cached plans still receive current client scope before their receipt is echoed to query RPC', async () => {
  const observed = await runOffline({ input: { clientId: 20, clientName: 'Scoped fixture' }, rows: [{ status: 'Approved', total_days: 1 }] })
  assert.equal(observed.exitCode, 0)
  assert.equal(observed.queries.length, 1)
  assert.equal(observed.queries[0].planReceipt, 'host-reuse-receipt')
  assert.match(observed.queries[0].sql, /`l`\.`ClientId` = 20/)
  assert.equal(observed.result.scope, 'Scoped fixture')
})

test('scoped interview runtime requires a matching application parent and executes its client predicate in WHERE', async () => {
  const scopedContract = { ...contract, recordTable: 'recruitment_interviews', measure: 'count_records', measureColumn: undefined,
    grouping: { table: 'recruitment_interviews', column: 'Status' }, scopeTables: ['recruitment_candidate_applications'],
    allowedTables: ['recruitment_interviews', 'recruitment_candidate_applications', 'clients'], requiredRelationships: [
      { leftTable: 'recruitment_interviews', leftColumn: 'ApplicationId', rightTable: 'recruitment_candidate_applications', rightColumn: 'Id' },
      { leftTable: 'recruitment_candidate_applications', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' },
    ] }
  const input = { question: 'Count interviews by Status', clientId: 28, clientName: 'Scoped fixture', catalog: [
    { name: 'recruitment_interviews', columns: ['Id', 'ApplicationId', 'Status'].map(name => ({ name, type: 'int' })) },
    { name: 'recruitment_candidate_applications', columns: ['Id', 'ClientId'].map(name => ({ name, type: 'int' })) },
    { name: 'clients', columns: ['Id', 'Code'].map(name => ({ name, type: 'int' })) },
  ] }
  const scopedSql = 'SELECT i.Status AS label, COUNT(*) AS value FROM payroll.recruitment_interviews i INNER JOIN payroll.recruitment_candidate_applications a ON a.Id=i.ApplicationId GROUP BY i.Status'
  const response = { ...compiledResponse, simpleCountContract: scopedContract, payload: { sql: scopedSql, parameters: [] } }
  const successful = await runOffline({ responses: [response], input, rows: [{ label: 'Scheduled', value: 1 }] })
  assert.equal(successful.exitCode, 0)
  assert.equal(successful.queries.length, 1)
  assert.match(successful.queries[0].sql, /INNER JOIN.*WHERE `a`.`ClientId` = 28.*GROUP BY/)
  assert.deepEqual(successful.result.rows, [{ label: 'Scheduled', value: 1 }])
  for (const metadata of [response, { ...response, providerCode: 'Cloud', simpleCountContract: undefined }]) {
    const blocked = await runOffline({ responses: [{ ...metadata, payload: { sql: scopedSql.replace('INNER JOIN', 'LEFT JOIN'), parameters: [] } }], input })
    assert.equal(blocked.exitCode, 1)
    assert.equal(blocked.planningCalls, metadata.providerCode === 'Cloud' ? 2 : 1)
    assert.deepEqual(blocked.queries, [])
    assert.equal(blocked.result, null)
    assert.match(blocked.error, /INNER JOIN/)
  }
})

test('fresh and reused recognized plans derive the same numeric grouping presentation from validated SQL', async () => {
  const employeeContract = { ...contract, recordTable: 'employees', measure: 'count_records', measureColumn: undefined,
    grouping: { table: 'employees', column: 'IsActive' }, allowedTables: ['employees'] }
  const employeePayload = { sql: 'SELECT COUNT(*) AS total, e.IsActive AS active_status FROM payroll.employees e GROUP BY e.IsActive', parameters: [] }
  for (const response of [freshResponse, cachedResponse, compiledResponse]) {
    const observed = await runOffline({ responses: [{ ...response, simpleCountContract: employeeContract, payload: employeePayload }],
      rows: [{ total: 2, active_status: 0 }, { total: 20, active_status: 1 }],
      input: { question: 'Count employees by IsActive', catalog: [{ name: 'employees', columns: ['Id', 'IsActive'].map(name => ({ name, type: 'int' })) }] },
    })
    assert.equal(observed.exitCode, 0)
    assert.equal(observed.result.chart.labelField, 'active_status')
    assert.deepEqual(observed.result.chart.valueFields, ['total'])
    assert.deepEqual(observed.result.chart.labels, ['0', '1'])
    assert.deepEqual(observed.result.chart.datasets[0].data, [2, 20])
  }
})

test('invalid host-compiled SQL fails closed without an expensive AI repair', async () => {
  const invalid = { ...compiledResponse, payload: { ...payload, sql: sql.replace('SUM(l.Days)', 'COUNT(*)') } }
  const blocked = await runOffline({ responses: [invalid, { ...freshResponse, payload: invalid.payload }] })
  assert.equal(blocked.exitCode, 1)
  assert.equal(blocked.planningCalls, 1)
  assert.equal(blocked.summaryCalls, 0)
  assert.deepEqual(blocked.queries, [])
  assert.equal(blocked.result, null)
})

test('trusted employee contract bypasses only the old alias heuristic, never runtime metric guards', async () => {
  const employeeContract = { ...contract, recordTable: 'employees', measure: 'count_records', measureColumn: undefined,
    grouping: { table: 'employees', column: 'Gender' }, allowedTables: ['employees'],
    requestedFilters: [{ table: 'employees', column: 'IsActive', operator: '=', value: 0 }] }
  const bad = { ...compiledResponse, simpleCountContract: employeeContract,
    payload: { sql: 'SELECT e.IsActive AS label, COUNT(*) AS value FROM payroll.employees e GROUP BY e.IsActive', parameters: [],
      simpleCountContract: { ...employeeContract, grouping: { table: 'employees', column: 'IsActive' }, requestedFilters: [] } } }
  const observed = await runOffline({ responses: [bad], input: { question: 'Count inactive employees by Gender. All clients.',
    catalog: [{ name: 'employees', columns: ['Id', 'Gender', 'IsActive'].map(name => ({ name, type: 'int' })) }],
  } })
  assert.equal(observed.exitCode, 1)
  assert.equal(observed.planningCalls, 1)
  assert.deepEqual(observed.queries, [])
  assert.equal(observed.result, null)
  assert.match(observed.error, /requested grouping column/)
})

test('invalid cached grouping is repaired before query and loses its cached summary shortcut', async () => {
  const invalid = { ...cachedResponse, payload: { ...payload, sql: 'SELECT SUM(l.Days) AS total_days FROM payroll.essleaverequests l' } }
  const observed = await runOffline({ responses: [invalid, freshResponse], rows: [{ status: 'Approved', total_days: 6 }] })
  assert.equal(observed.exitCode, 0)
  assert.equal(observed.planningCalls, 2)
  assert.equal(observed.queries.length, 1)
  assert.match(observed.queries[0].sql, /GROUP BY l.Status/)
  assert.equal(observed.queries[0].planReceipt, 'host-candidate-receipt')
  assert.equal(observed.summaryCalls, 1)
  assert.equal(observed.result.summarySource, 'ai')
  assert.equal(observed.result.planSource, undefined)
})

test('invalid cached and repaired plans never issue a database query or publish a result', async () => {
  const invalid = { ...cachedResponse, payload: { ...payload, sql: sql.replace('SUM(l.Days)', 'COUNT(*)') } }
  const observed = await runOffline({ responses: [invalid, { ...freshResponse, payload: invalid.payload }] })
  assert.equal(observed.exitCode, 1)
  assert.equal(observed.planningCalls, 2)
  assert.equal(observed.summaryCalls, 0)
  assert.deepEqual(observed.queries, [])
  assert.equal(observed.result, null)
  assert.match(observed.error, /requested metric/)
})

test('reuse cannot bypass SELECT validation or fail-closed client scoping', async () => {
  const protectedSql = { ...cachedResponse, payload: { ...payload, sql: 'SELECT PasswordHash FROM payroll.essleaverequests' } }
  const blocked = await runOffline({ responses: [protectedSql] })
  assert.equal(blocked.exitCode, 1)
  assert.equal(blocked.planningCalls, 2)
  assert.deepEqual(blocked.queries, [])
  const unscopable = await runOffline({ input: { clientId: 20,
    catalog: [{ name: 'essleaverequests', columns: ['Id', 'Status', 'Days'].map(name => ({ name, type: 'int' })) }],
  } })
  assert.equal(unscopable.exitCode, 1)
  assert.equal(unscopable.planningCalls, 2)
  assert.deepEqual(unscopable.queries, [])
  assert.match(unscopable.error, /cannot be safely restricted/)
})
