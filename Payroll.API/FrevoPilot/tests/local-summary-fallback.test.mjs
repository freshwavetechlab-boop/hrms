import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createExecutiveInsights, createQueryPlan, repairQueryPlan } from '../analytics/planner.mjs'

const context = { question: 'Count interviews by status', rows: [{ status: 'Completed', total: 1 }],
  chart: { type: 'bar', valueFields: ['total'] }, knowledge: [] }

test('local failed insights retain factual rows without fabricated advice or provider secrets', async () => {
  const previous = globalThis.frevoPilotProvider
  try {
    globalThis.frevoPilotProvider = async () => { throw new Error('HTTP 504 secret-key private-gateway') }
    const result = await createExecutiveInsights({ ...context, localProvider: true })
    assert.match(result.summary, /returned 1 result group/)
    assert.deepEqual(result.insights, [])
    assert.equal(result.summarySource, 'database-only')
    assert.equal(result.summaryWarning, 'AI summary unavailable; live database totals are shown')
    assert.doesNotMatch(JSON.stringify(result), /secret-key|private-gateway|confidence|Recommendation|Risk/)
    assert.deepEqual(context.rows, [{ status: 'Completed', total: 1 }])
    const empty = await createExecutiveInsights({ ...context, rows: [], localProvider: true })
    assert.match(empty.summary, /does not establish zero activity/)
    assert.deepEqual(empty.insights, [])
  } finally { globalThis.frevoPilotProvider = previous }
})

test('local absent or malformed summary is transparently database-only', async () => {
  const previous = globalThis.frevoPilotProvider
  try {
    for (const response of [null, { payload: {} }, { payload: { summary: ' ' } }]) {
      globalThis.frevoPilotProvider = async () => response
      const result = await createExecutiveInsights({ ...context, localProvider: true })
      assert.equal(result.summarySource, 'database-only')
      assert.deepEqual(result.insights, [])
    }
  } finally { globalThis.frevoPilotProvider = previous }
})

test('successful local AI summary remains AI sourced; cloud success and fallback shapes unchanged', async () => {
  const previous = globalThis.frevoPilotProvider
  try {
    globalThis.frevoPilotProvider = async () => ({ payload: { summary: 'Completed interviews: 1.', recommendations: [], risks: [] } })
    const local = await createExecutiveInsights({ ...context, localProvider: true })
    assert.equal(local.summarySource, 'ai')
    assert.equal(local.summaryWarning, undefined)
    const cloud = await createExecutiveInsights(context)
    assert.deepEqual(cloud, { summary: 'Completed interviews: 1.', insights: [] })
    globalThis.frevoPilotProvider = async () => { throw new Error('cloud failure') }
    const fallback = await createExecutiveInsights(context)
    assert.equal(fallback.insights.length, 2)
    assert.equal(fallback.insights[0].confidence, 0.76)
    assert.equal(fallback.summarySource, undefined)
    assert.equal(fallback.summaryWarning, undefined)
  } finally { globalThis.frevoPilotProvider = previous }
})

test('trusted local provider marker survives repair and ignores model payload marker', async () => {
  const previous = globalThis.frevoPilotProvider
  const planning = { question: 'Count interviews by status', catalog: [], knowledge: [], semanticContext: {}, sourceDatabase: 'payroll' }
  try {
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: 'SELECT Status, COUNT(*) FROM recruitment_interviews GROUP BY Status', providerCode: 'FakeCloud' }, model: 'local', providerCode: 'LocalOpenAICompatible' })
    const plan = await createQueryPlan(planning)
    assert.equal(plan.providerCode, 'LocalOpenAICompatible')
    globalThis.frevoPilotProvider = async () => ({ payload: { sql: plan.sql, providerCode: 'FakeCloud' }, model: 'local' })
    const repaired = await repairQueryPlan({ ...planning, failedPlan: plan, validationError: new Error('retry') })
    assert.equal(repaired.providerCode, 'LocalOpenAICompatible')
    assert.equal((await createQueryPlan(planning)).providerCode, undefined)
  } finally { globalThis.frevoPilotProvider = previous }
})
