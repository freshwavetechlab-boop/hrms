import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createQueryPlan, deriveValidatedLocalPresentation } from '../analytics/planner.mjs'
import { validateAnalyticsSql } from '../analytics/validator.mjs'
import { validateLocalSimpleCountPlan } from '../analytics/local-count-contract.mjs'
import { buildChart } from '../analytics/chart.mjs'

const countContract = {
  version: 'simple-count-v1', recordTable: 'employees', measure: 'count_records',
  grouping: { table: 'employees', column: 'IsActive' }, requestedFilters: [], recordDefinitionFilters: [],
  noOtherFiltersRequested: true, allowedTables: ['employees'], requiredRelationships: [], scopeTables: [],
}
function chartFor(sql, rows, contract = countContract) {
  const { ast } = validateAnalyticsSql(sql, { sourceDatabase: 'payroll', allowedTables: contract.allowedTables })
  validateLocalSimpleCountPlan(ast, contract)
  const presentation = deriveValidatedLocalPresentation(ast, contract, 'LocalOpenAICompatible', rows)
  return buildChart(rows, { ...presentation, presentationMode: 'dashboard' })
}

for (const [name, sql] of [
  ['group first', 'SELECT e.IsActive AS active_status, COUNT(*) AS total FROM payroll.employees e GROUP BY e.IsActive'],
  ['count first', 'SELECT COUNT(*) AS total, e.IsActive AS active_status FROM payroll.employees e GROUP BY e.IsActive'],
  ['aliased parenthesized grouping', 'SELECT COUNT(*) AS total, (e.IsActive) AS active_status FROM payroll.employees e GROUP BY e.IsActive'],
]) test(`validated numeric grouping remains a label: ${name}`, () => {
  const rows = [{ active_status: 0, total: 2 }, { active_status: 1, total: 20 }]
  const chart = chartFor(sql, rows)
  assert.equal(chart.labelField, 'active_status')
  assert.deepEqual(chart.valueFields, ['total'])
  assert.deepEqual(chart.labels, ['0', '1'])
  assert.deepEqual(chart.datasets.map(dataset => dataset.data), [[2, 20]])
  for (const widget of chart.widgets) assert.deepEqual(widget.valueFields, ['total'])
})

test('unaliased COUNT binds the live output name and excludes its unaliased numeric grouping', () => {
  const chart = chartFor('SELECT COUNT(*), e.IsActive FROM payroll.employees e GROUP BY e.IsActive',
    [{ 'COUNT(*)': 2, IsActive: 0 }, { 'COUNT(*)': 20, IsActive: 1 }])
  assert.equal(chart.labelField, 'IsActive')
  assert.deepEqual(chart.valueFields, ['COUNT(*)'])
  assert.deepEqual(chart.datasets[0].data, [2, 20])
})

test('all-null validated metrics stay null even when the group is numeric', () => {
  const chart = chartFor('SELECT e.IsActive AS active_status, COUNT(*) AS total FROM payroll.employees e GROUP BY e.IsActive',
    [{ active_status: 0, total: null }, { active_status: 1, total: null }])
  assert.deepEqual(chart.valueFields, ['total'])
  assert.deepEqual(chart.datasets[0].data, [null, null])
  assert.deepEqual(chart.labels, ['0', '1'])
})

test('rounded AVG uses its selected output alias and unaliased AVG uses the returned expression name', () => {
  const contract = { ...countContract, recordTable: 'essleaverequests', measure: 'average_values',
    measureColumn: { table: 'essleaverequests', column: 'Days' }, grouping: { table: 'essleaverequests', column: 'Status' }, allowedTables: ['essleaverequests'], roundDigits: 2 }
  const rounded = chartFor('SELECT ROUND(AVG(l.Days), 2) AS average_days, l.Status AS status FROM payroll.essleaverequests l GROUP BY l.Status',
    [{ average_days: 1.5, status: 1 }, { average_days: null, status: 2 }], contract)
  assert.equal(rounded.labelField, 'status')
  assert.equal(rounded.type, 'bar')
  assert.deepEqual(rounded.valueFields, ['average_days'])
  assert.deepEqual(rounded.datasets[0].data, [1.5, null])
  const unaliased = chartFor('SELECT l.Status, AVG(l.Days) FROM payroll.essleaverequests l GROUP BY l.Status',
    [{ Status: 'Approved', 'AVG(l.Days)': 1.5 }], { ...contract, roundDigits: undefined })
  assert.equal(unaliased.labelField, 'Status')
  assert.deepEqual(unaliased.valueFields, ['AVG(l.Days)'])
})

test('empty current results do not invent metric fields or chart points', () => {
  const chart = chartFor('SELECT e.IsActive AS active_status, COUNT(*) AS total FROM payroll.employees e GROUP BY e.IsActive', [])
  assert.deepEqual(chart.valueFields, [])
  assert.deepEqual(chart.datasets, [])
  assert.deepEqual(chart.labels, [])
})

test('cloud and absent-host-contract presentation retain existing behavior', () => {
  const rows = [{ active_status: 0, total: 2 }, { active_status: 1, total: 20 }]
  const original = { labelField: 'active_status', valueFields: ['total'], chartType: 'bar' }
  for (const [contract, provider] of [[countContract, 'Cloud'], [null, 'LocalOpenAICompatible']]) {
    const derived = deriveValidatedLocalPresentation(undefined, contract, provider, rows)
    assert.equal(derived, null)
    assert.deepEqual(buildChart(rows, { ...original, ...derived }).valueFields, ['total'])
    assert.equal(buildChart(rows, { ...original, ...derived }).type, 'bar')
  }
})

test('trusted local contracts preserve exact employee metrics despite generic aliases or all-clients wording', async () => {
  const previous = globalThis.frevoPilotProvider
  const catalog = [{ name: 'employees', columns: [] }, { name: 'clients', columns: [] }]
  const context = { catalog, knowledge: [], semanticContext: {}, sourceDatabase: 'payroll' }
  const byClient = { ...countContract, grouping: { table: 'clients', column: 'Code' }, allowedTables: ['employees', 'clients'],
    requestedFilters: [{ table: 'employees', column: 'IsActive', operator: '=', value: 1 }],
    requiredRelationships: [{ leftTable: 'employees', leftColumn: 'ClientId', rightTable: 'clients', rightColumn: 'Id' }] }
  const byGender = { ...countContract, grouping: { table: 'employees', column: 'Gender' },
    requestedFilters: [{ table: 'employees', column: 'IsActive', operator: '=', value: 0 }] }
  try {
    for (const [question, sql, parameters, contract] of [
      ['Count active employees by client', 'SELECT c.Code AS label, COUNT(*) AS value FROM payroll.employees e LEFT JOIN payroll.clients c ON e.ClientId=c.Id WHERE e.IsActive=? GROUP BY c.Code', ['1'], byClient],
      ['Count inactive employees by Gender. All clients.', 'SELECT e.Gender AS label, COUNT(*) AS value FROM payroll.employees e WHERE e.IsActive=? GROUP BY e.Gender', ['0'], byGender],
    ]) {
      globalThis.frevoPilotProvider = async () => ({ payload: { sql, parameters }, providerCode: 'LocalOpenAICompatible', model: 'host-local', simpleCountContract: contract })
      const plan = await createQueryPlan({ ...context, question })
      assert.equal(plan.sql, sql)
      assert.equal(plan.model, 'host-local')
      validateLocalSimpleCountPlan(validateAnalyticsSql(plan.sql, { sourceDatabase: 'payroll', allowedTables: contract.allowedTables }).ast, plan.simpleCountContract, parameters)
      globalThis.frevoPilotProvider = async () => ({ payload: { sql, parameters, providerCode: 'LocalOpenAICompatible', simpleCountContract: contract }, model: 'cloud' })
      assert.equal((await createQueryPlan({ ...context, question })).model, 'FrevoPilot deterministic')
      globalThis.frevoPilotProvider = async () => ({ payload: { sql, parameters }, providerCode: 'LocalOpenAICompatible', model: 'host-local', simpleCountContract: { ...contract, version: 'unknown' } })
      assert.equal((await createQueryPlan({ ...context, question })).model, 'FrevoPilot deterministic')
    }
  } finally { globalThis.frevoPilotProvider = previous }
})
