import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const calls = []
const expected = [{ id: 8, clientId: 55 }]
const api = { getJson: async (path, fallback) => { calls.push({ path, fallback }); return expected } }
const service = {}
const code = ts.transpileModule(readFileSync(new URL('../src/services/recruitmentOrchestrationService.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
new Function('exports', 'require', code)(service, path => {
  if (path === './apiClient') return api
  if (path === './recruitmentJobVersions' || path === './publicCareerUrl') return {}
  throw new Error(`Unexpected service dependency: ${path}`)
})

test('All permitted pipeline library requests omit the client query instead of filtering to client zero', async () => {
  for (const clientId of [undefined, 0]) {
    assert.equal(await service.getRecruitmentPipelines(clientId), expected)
    assert.deepEqual(calls.pop(), { path: '/api/recruitment-orchestration/pipelines', fallback: [] })
  }
})

test('explicit pipeline library selection retains its client query', async () => {
  assert.equal(await service.getRecruitmentPipelines(55), expected)
  assert.deepEqual(calls.pop(), { path: '/api/recruitment-orchestration/pipelines?clientId=55', fallback: [] })
})
