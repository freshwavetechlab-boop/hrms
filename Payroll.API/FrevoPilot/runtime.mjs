// No database credentials, network listener, DB bootstrap or portal write actions.
// The authenticated .NET host owns authorization, provider calls and read-only SQL.
import { createInterface } from 'node:readline'
import { createQueryPlan, repairQueryPlan, createExecutiveInsights, createVerifiedDataInsights, deriveValidatedLocalPresentation } from './analytics/planner.mjs'
import { validateAnalyticsSql, applyClientScope } from './analytics/validator.mjs'
import { validateLocalSimpleCountPlan } from './analytics/local-count-contract.mjs'
import { selectRelevantCatalog, relevantRelationshipHints } from './analytics/retrieval.mjs'
import { relevantKnowledge } from './analytics/knowledge.mjs'
import { buildChart } from './analytics/chart.mjs'

const lines = createInterface({ input: process.stdin })
const pending = new Map()
let sequence = 0
let initialized = false
const emit = (data, exitCode) => process.stdout.write(JSON.stringify(data) + '\n', () => { if (exitCode !== undefined) process.exit(exitCode) })
const rpc = (kind, data) => new Promise((resolve, reject) => {
  const id = ++sequence
  pending.set(id, { resolve, reject })
  emit({ kind, id, ...data })
})
globalThis.frevoPilotProvider = (prompt, systemInstruction, schema) => rpc('ai', { prompt, systemInstruction, schema })
const stage = (progress, message) => emit({ kind: 'progress', progress, message })

lines.on('line', line => {
  try {
    const data = JSON.parse(line)
    if (!initialized) { initialized = true; void run(data); return }
    const request = pending.get(data.id)
    if (!request) return
    pending.delete(data.id)
    if (data.error) request.reject(new Error(data.error))
    else request.resolve(data.result)
  } catch { emit({ kind: 'error', message: 'Analytics runtime received an invalid response.' }); process.exit(1) }
})

async function run(input) {
  try {
    stage(10, 'Reading the current authorized HRMS schema')
    const catalog = selectRelevantCatalog(input.question, input.catalog)
    const knowledge = await relevantKnowledge(input.question)
    const semanticContext = { relationships: relevantRelationshipHints(catalog), rules: input.rules || [], scope: input.clientName || 'All clients' }
    stage(30, 'Matching business rules and planning the dashboard')
    const context = { question: input.question, catalog, knowledge, semanticContext, sourceDatabase: input.sourceDatabase }
    let plan = await createQueryPlan(context)
    let rows, sql, validatedPlanAst
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        // Never silently substitute a canned metric for the actual question.
        if (plan.model === 'FrevoPilot deterministic') throw new Error('The AI planner could not produce a reliable plan for this question. Retry or clarify the requested metric.')
        stage(55, 'Checking SELECT-only access, protected fields and client scope')
        const options = { sourceDatabase: input.sourceDatabase, allowedTables: catalog.map(table => table.name) }
        const validated = validateAnalyticsSql(plan.sql, options)
        // This host-derived, local-only contract guards metric meaning as well
        // as SQL safety. Apply before the host adds authorized client predicates.
        validateLocalSimpleCountPlan(validated.ast, plan.simpleCountContract, plan.parameters)
        validatedPlanAst = validated.ast
        sql = validated.sql
        const clientAliases = (validated.ast.from || []).filter(source => source.table?.toLowerCase() === 'clients').map(source => source.as || source.table)
        if (!/\bactive\s+clients?\b/i.test(input.question) && clientAliases.some(alias => new RegExp('\\b' + alias + '\\s*\\.\\s*`?IsActive`?\\b', 'i').test(sql))) {
          throw new Error('Do not filter clients.IsActive: the question did not request active clients. Include every client with matching business records.')
        }
        if (input.clientId) sql = applyClientScope(sql, input.clientId, catalog)
        sql = validateAnalyticsSql(sql, options).sql
        stage(70, 'Reading live totals in a read-only database transaction')
        rows = await rpc('query', { sql, parameters: plan.parameters || [],
          ...(plan.planReceipt ? { planReceipt: plan.planReceipt } : {}),
        })
        break
      } catch (error) {
        // Host-compiled exact metrics cannot be improved by speculative model
        // repair. Report a validation/query failure without starting inference.
        const compiledMetric = String(plan.providerCode || '').toLowerCase() === 'localopenaicompatible'
          && plan.planSource === 'verified-contract' && Boolean(plan.planReceipt)
        if (attempt === 1 || compiledMetric) throw error
        stage(60, 'Correcting the query against the live schema')
        plan = await repairQueryPlan({ ...context, failedPlan: plan, validationError: error })
      }
    }
    stage(85, 'Building charts and supporting data table')
    const presentation = deriveValidatedLocalPresentation(validatedPlanAst, plan.simpleCountContract, plan.providerCode, rows)
    const chart = buildChart(rows, { ...plan, ...presentation, presentationMode: 'dashboard' })
    stage(92, 'Checking the result summary against returned data')
    const localProvider = String(plan.providerCode || '').toLowerCase() === 'localopenaicompatible'
    const verifiedPlan = localProvider && ['validated-memory', 'verified-contract'].includes(plan.planSource) && Boolean(plan.planReceipt)
    const narrative = verifiedPlan ? createVerifiedDataInsights({ rows, chart })
      : await createExecutiveInsights({ question: input.question, rows, chart, knowledge, localProvider })
    emit({ kind: 'result', result: {
      title: plan.title, question: input.question, model: plan.model, explanation: plan.explanation,
      sql, parameters: plan.parameters || [], rows, chart, ...narrative,
      ...(verifiedPlan ? { planSource: plan.planSource, planSourceNote: plan.planSource === 'validated-memory'
        ? 'Reused a previously validated query plan and queried current authorized data.'
        : 'Compiled the explicitly requested metric from the current schema and queried current authorized data.' } : {}),
      generatedAtUtc: new Date().toISOString(), knowledgeSources: [...new Set(knowledge.map(item => item.source))],
      scope: input.clientName || 'All clients', isReadOnly: true,
    } }, 0)
  } catch (error) {
    // Error messages contain governed SQL/schema only; credentials never enter this process.
    emit({ kind: 'error', message: String(error.message || 'Analytics could not complete.').slice(0, 1200) }, 1)
  }
}
