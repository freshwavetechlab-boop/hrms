import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/engineActivityChart.ts', import.meta.url), 'utf8')
const context = vm.createContext({ exports: {}, Date })
vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText, context)
const build = context.exports.engineActivityData
const engine = { name: 'Resume Parser', trend: [
  { timeUtc: '2026-09-19T09:00:00Z', loadPercent: 0, requests: 1, failures: 0, averageDurationMs: 104 },
  { timeUtc: '2026-09-19T09:00:30Z', loadPercent: 100, requests: 0, failures: 0, averageDurationMs: 0 },
] }
test('104ms completed work is visible without manufacturing busy load', () => {
  assert.deepEqual(Array.from(build([engine], 'requests').datasets[0].data), [1, 0])
  assert.deepEqual(Array.from(build([engine], 'busy').datasets[0].data), [0, 100])
  assert.equal(engine.trend[0].loadPercent, 0)
  assert.equal(build([engine], 'requests').datasets[0].pointRadius, 3)
})
test('empty telemetry stays empty; queued work is not invented as completed', () => {
  assert.equal(build([], 'requests').datasets.length, 0)
  assert.equal(build([], 'busy').labels.length, 0)
  assert.equal(build([{ ...engine, queuedRequests: 5, activeRequests: 1 }], 'requests').datasets[0].data[1], 0)
})
test('saved history gaps remain null, never connected as zero work', () => {
  const chart=build([{name:'ATS',trend:[{timeUtc:'2026-09-19T00:00:00Z',requests:null,loadPercent:null}]}],'requests',true)
  assert.equal(chart.datasets[0].data[0],null)
  assert.equal(chart.datasets[0].spanGaps,false)
})
test('separate engine series preserve server counts and failed attempts', () => {
  const result = build([engine, { name: 'JD / Hiring Parser', trend: [{ ...engine.trend[0], requests: 3, failures: 1 }] }], 'requests')
  assert.equal(result.datasets[0].data[0], 1)
  assert.equal(result.datasets[1].data[0], 3)
  assert.notEqual(result.datasets[0].borderColor, result.datasets[1].borderColor)
  assert.notEqual(result.labels[0], result.labels[1])
})
