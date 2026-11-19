import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const calls = []
const api = Object.fromEntries(['getJson', 'postJson', 'deleteJson'].map(method => [method, async (...args) => {
  calls.push({ method, args }); return { ok: true }
}]))
const service = {}
const code = ts.transpileModule(readFileSync(new URL('../src/services/recruitmentOrchestrationService.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText
new Function('exports', 'require', code)(service, path => {
  if (path === './apiClient') return api
  if (path === './recruitmentJobVersions' || path === './publicCareerUrl') return {}
  throw new Error(`Unexpected service dependency: ${path}`)
})
const recruitment = '/api/recruitment-orchestration'
const employee = '/api/employees/field-configuration'

test('All candidate forms omit client zero while employee form lookup keeps its existing URL', async () => {
  await service.getRecruitmentForms(0)
  assert.equal(calls.pop().args[0], `${recruitment}/forms`)
  await service.getRecruitmentForms(55)
  assert.equal(calls.pop().args[0], `${recruitment}/forms?clientId=55`)
  await service.getRecruitmentForms(0, employee)
  assert.equal(calls.pop().args[0], `${employee}/forms?clientId=0`)
})

test('Create Similar sends the exact source version and independent target metadata to the clone endpoint', async () => {
  const request = { sourceVersionId: 12, targetClientId: 55, formName: 'Candidate application copy', formCode: 'CANDIDATE_COPY' }
  await service.createSimilarRecruitmentForm(8, request)
  const call = calls.pop()
  assert.equal(call.method, 'postJson')
  assert.equal(call.args[0], `${recruitment}/forms/8/create-similar`)
  assert.deepEqual(call.args[1], request)
  assert.equal(calls.length, 0, 'creating a copy never invokes publishing or posting assignment')
})

test('saving and publishing a copied candidate form carry its owning client without changing employee field requests', async () => {
  const version = { id: 12, sections: [{ sectionCode: 'PROFILE', fields: [] }] }
  await service.saveRecruitmentFormVersion(8, version, recruitment, 55)
  const saved = calls.pop()
  assert.equal(saved.args[0], `${recruitment}/forms/8/versions?clientId=55`)
  assert.deepEqual(saved.args[1], { id: 12, formDefinitionId: 8, sections: version.sections })
  await service.publishRecruitmentFormVersion(12, recruitment, 8, 55)
  assert.equal(calls.pop().args[0], `${recruitment}/form-versions/12/publish?definitionId=8&clientId=55`)
  await service.saveRecruitmentFormVersion(8, version, employee, 55)
  assert.equal(calls.pop().args[0], `${employee}/forms/8/versions`)
  await service.publishRecruitmentFormVersion(12, employee, 8, 55)
  assert.equal(calls.pop().args[0], `${employee}/form-versions/12/publish?definitionId=8`)
})

test('form deletion includes explicit owning client only when supplied', async () => {
  await service.deleteRecruitmentFormDefinition(8, 55)
  assert.equal(calls.pop().args[0], `${recruitment}/forms/8?clientId=55`)
  await service.deleteRecruitmentFormDefinition(8)
  assert.equal(calls.pop().args[0], `${recruitment}/forms/8`)
})
