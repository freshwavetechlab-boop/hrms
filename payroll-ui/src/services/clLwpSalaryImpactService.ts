import { getJsonResult, postJson } from './apiClient'
import type { ReviewSnapshot } from './payrollAdvisorService'
import type { SimulationResult } from './payrollCalculationService'

export type SalarySnapshot = { id: string; snapshotHash: string; snapshot: ReviewSnapshot }
export type SalaryDays = { payrollDays: number | null; presentDays: number | null; payableDays: number | null }
export const getSalarySnapshots = (clientId: number) => getJsonResult<SalarySnapshot[]>(`/api/payroll-calculation/template-snapshots?clientId=${clientId}`, [], { toast: false, loader: false })
export function salaryImpactDifference(source: SimulationResult, scenario: SimulationResult) {
  if (!source.complete || !scenario.complete || [source, scenario].some(r => [r.gross, r.deductions, r.benefits, r.net].some(v => v == null || !Number.isFinite(v)))) throw Error('Both main-engine simulations must complete; missing amounts cannot become zero.')
  const keys = (r: SimulationResult) => r.lines.map(l => `${l.code}|${l.category}`)
  if (new Set(keys(source)).size !== source.lines.length || new Set(keys(scenario)).size !== scenario.lines.length || keys(source).length !== keys(scenario).length || keys(source).some(k => !keys(scenario).includes(k))) throw Error('Component identities/classifications differ between simulations. Refresh the same template snapshot.')
  const lines = source.lines.map(old => { const next = scenario.lines.find(l => l.code === old.code && l.category === old.category)!; return { code: old.code, category: old.category, formula: next.formula, source: old.earned, scenario: next.earned, delta: next.earned - old.earned, reason: next.proRata ? 'Main engine attendance proration and component rounding' : 'Main engine formula/fixed component; see resolved inputs', sourceInputs: old.inputs, scenarioInputs: next.inputs } })
  const totals = (['gross', 'deductions', 'benefits', 'net'] as const).map(key => ({ key, source: source[key]!, scenario: scenario[key]!, delta: scenario[key]! - source[key]! }))
  return { lines, totals }
}
export async function compareClLwpSalary(args: { clientId: number; selected: SalarySnapshot; source: SalaryDays; scenario: SalaryDays; annualCtc: number | null; amountOverrides: Record<string, number>; isCurrent: () => boolean }) {
  if (!args.isCurrent()) throw Error('Simulation scope changed.')
  if (args.selected.snapshot.clientId !== args.clientId || args.selected.snapshot.mode !== 'Template') throw Error('The salary snapshot belongs to another client or calculation mode.')
  for (const days of [args.source, args.scenario]) if ([days.payrollDays, days.presentDays, days.payableDays].some(v => v == null || !Number.isFinite(v) || v < 0)) throw Error('Supply explicit source/scenario payroll, actual present and payable days; unknown is not zero.')
  const fresh = await getSalarySnapshots(args.clientId)
  if (!args.isCurrent()) throw Error('Simulation scope changed.')
  if (!fresh.ok) throw Error(fresh.error || 'Unable to verify the current template snapshot.')
  if (!fresh.data.some(s => s.id === args.selected.id && s.snapshotHash === args.selected.snapshotHash)) throw Error('The salary template changed. Reload snapshots and review before simulating.')
  const run = async (days: SalaryDays) => {
    const response = await postJson('/api/payroll-calculation/simulate', { mode: 'Template', clientId: args.clientId, templateId: args.selected.id, snapshot: args.selected.snapshot, annualCtc: args.annualCtc, amountOverrides: args.amountOverrides, ...days }, null as SimulationResult | null, { toast: false, loader: false, timeoutMs: 90000 })
    if (!args.isCurrent()) throw Error('Simulation scope changed.')
    if (!response.ok || !response.data) throw Error(response.error || 'Main-engine simulation returned no result.')
    return response.data
  }
  const source = await run(args.source), scenario = await run(args.scenario)
  if (!source.complete || !scenario.complete) return { source, scenario, difference: null }
  return { source, scenario, difference: salaryImpactDifference(source, scenario) }
}
