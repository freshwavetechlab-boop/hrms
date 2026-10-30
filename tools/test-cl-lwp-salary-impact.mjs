import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
const root = process.env.HRMS_SOURCE_ROOT || fileURLToPath(new URL('../', import.meta.url)), require = createRequire(import.meta.url), ts = require(path.join(root, 'payroll-ui/node_modules/typescript/lib/typescript.js'))
const compiled = ts.transpileModule(fs.readFileSync(path.join(root, 'payroll-ui/src/services/clLwpSalaryImpactService.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const snapshot = { id: '17', snapshotHash: 'a'.repeat(64), snapshot: { mode: 'Template', clientId: 42, annualCtc: 0, components: [], lines: [] } }
const response = (gross, deductions, benefits) => ({ complete: true, coverage: 'Synthetic main-engine response', issues: [], gross, deductions, benefits, net: gross - deductions, lines: [{ code: 'WAGE', category: 'Earning', formula: '', earned: gross, inputs: { PAYABLE_DAYS: gross / 1000 }, proRata: true }, { code: 'PF', category: 'Deduction', formula: 'WAGE_EARNED*12%', earned: deductions, inputs: {}, proRata: false }, { code: 'ER', category: 'Benefit', formula: 'WAGE_EARNED*1%', earned: benefits, inputs: {}, proRata: false }] })
const ok = data => ({ ok: true, data, status: 200 })
function fixture(change = {}) {
  const calls = [], mod = { exports: {} }, api = {
    getJsonResult: async url => { calls.push({ kind: 'read', url }); return ok([snapshot]) },
    postJson: async (url, body) => { calls.push({ kind: 'simulate', url, body }); return ok(body.payableDays === 26 ? response(26000, 3120, 260) : response(28000, 3360, 280)) }, ...change,
  }
  new Function('require', 'module', 'exports', compiled)(() => api, mod, mod.exports)
  return { service: mod.exports, calls }
}
const args = () => ({ clientId: 42, selected: structuredClone(snapshot), source: { payrollDays: 30, presentDays: 20, payableDays: 26 }, scenario: { payrollDays: 30, presentDays: 20, payableDays: 28 }, annualCtc: 0, amountOverrides: {}, isCurrent: () => true })
const checks = []
{
  const f = fixture(), input = args(), before = JSON.stringify(input.selected), result = await f.service.compareClLwpSalary(input)
  assert.deepEqual(f.calls.map(c => c.kind), ['read', 'simulate', 'simulate']); assert.ok(f.calls.every(c => c.kind === 'read' || c.url === '/api/payroll-calculation/simulate'))
  assert.equal(f.calls[1].body.snapshot, f.calls[2].body.snapshot); assert.equal(JSON.stringify(input.selected), before)
  assert.equal(result.difference.lines.find(l => l.code === 'ER').delta, 20); assert.equal(result.difference.totals.find(t => t.key === 'net').delta, 1760)
  checks.push('Source and CL/LWP scenario use identical reviewed snapshots in existing main-engine simulation; only returned amounts are subtracted')
}
{
  const f = fixture({ getJsonResult: async () => ok([{ ...snapshot, snapshotHash: 'b'.repeat(64) }]) })
  await assert.rejects(() => f.service.compareClLwpSalary(args()), /changed/); assert.equal(f.calls.length, 0)
  checks.push('Changed saved template hash blocks stale comparison before any simulation request')
}
{
  const f = fixture(), input = args(); input.clientId = 43
  await assert.rejects(() => f.service.compareClLwpSalary(input), /another client/); assert.equal(f.calls.length, 0)
  input.clientId = 42; input.source.presentDays = null
  await assert.rejects(() => f.service.compareClLwpSalary(input), /unknown is not zero/); assert.equal(f.calls.length, 0)
  checks.push('Cross-client snapshots and unknown worked/payable/divisor inputs fail without guessed values')
}
{
  const f = fixture({ postJson: async () => ok({ ...response(0, 0, 0), complete: false, net: null, issues: [{ message: 'Monthly salary amount missing', severity: 'Error' }] }) })
  const result = await f.service.compareClLwpSalary(args()); assert.equal(result.difference, null); assert.equal(result.source.net, null)
  checks.push('Missing salary inputs retain calculator issues and unresolved amount impact; no ratio-based net estimate')
}
{
  let current = true
  const f = fixture({ getJsonResult: async () => { current = false; return ok([snapshot]) } }), input = args(); input.isCurrent = () => current
  await assert.rejects(() => f.service.compareClLwpSalary(input), /scope changed/); assert.equal(f.calls.length, 0)
  checks.push('Changed client/scenario scope prevents follow-up simulator calls')
}
{
  const f = fixture(), a = response(26000, 3120, 260), b = response(28000, 3360, 280)
  b.lines[0].category = 'Benefit'; assert.throws(() => f.service.salaryImpactDifference(a, b), /classifications differ/)
  b.lines[0].category = 'Earning'; b.net = null; assert.throws(() => f.service.salaryImpactDifference(a, b), /missing amounts/)
  checks.push('Changed component classification or incomplete totals cannot be reported as parity/delta')
}
{
  const f = fixture({ postJson: async () => ({ ok: false, data: null, error: 'Synthetic calculator unavailable' }) })
  await assert.rejects(() => f.service.compareClLwpSalary(args()), /unavailable/)
  checks.push('Calculator failure remains actionable; no client-side formula fallback or storage mutation')
}
console.log(JSON.stringify({ passed: checks.length, checks, realDatabaseWrites: 0, realUploads: 0 }, null, 2))
