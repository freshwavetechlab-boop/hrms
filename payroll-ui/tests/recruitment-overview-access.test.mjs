import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

function load(name) {
  const context = vm.createContext({ exports: {} })
  vm.runInContext(ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText, context)
  return context.exports
}
const { recruitmentDashboardAccess: access } = load('recruitmentDashboardAccess')
const { positionsReadyToPublish: ready } = load('recruitmentPublishQueue')

test('narrow permissions do not grant candidate, offer or consolidated dashboard access', () => {
  for (const permission of ['recruitment.rfr.create', 'recruitment.candidate.view', 'recruitment.interview.panel', 'client.roles.assign']) {
    assert.equal(Object.values(access([permission])).some(Boolean), false)
  }
  assert.equal(access(['recruitment.position.view']).positions, true)
  assert.equal(access(['recruitment.position.view']).manage, false)
  assert.equal(access(['recruitment.hiring-case.view']).cases, true)
  assert.equal(access(['recruitment.hiring-case.view']).positions, false)
  assert.equal(Object.values(access(['recruitment.manage'])).every(Boolean), true)
  assert.equal(Object.values(access(['settings.manage'])).every(Boolean), true)
})

const position = (overrides = {}) => ({ id: 16, clientId: 20, status: 'Open', jobDescriptionStatus: 'Approved', remainingPositions: 1, ...overrides })
test('already published positions do not become publishing tasks without a posting row', () => {
  assert.equal(ready([position({ status: 'Published' })], []).length, 0)
})
test('published posting suppresses its own position only; source data stays unchanged', () => {
  const positions = [position(), position({ id: 17 }), position({ clientId: 30 })]
  const postings = [{ clientId: 20, positionId: 16, status: 'Published' }]
  const snapshot = JSON.stringify({ positions, postings })
  assert.equal(ready(positions, postings).length, 2)
  assert.equal(JSON.stringify({ positions, postings }), snapshot)
})
test('only approved, open demand with vacancies needs publishing', () => {
  assert.equal(ready([position()], []).length, 1)
  assert.equal(ready([position({ status: 'Recruiter Assigned' })], []).length, 1)
  for (const overrides of [{ status: 'Closed' }, { status: 'On Hold' }, { status: 'Filled' }, { status: 'Interview In Progress' }, { remainingPositions: 0 }, { jobDescriptionStatus: 'Pending Approval' }]) {
    assert.equal(ready([position(overrides)], []).length, 0)
  }
})
