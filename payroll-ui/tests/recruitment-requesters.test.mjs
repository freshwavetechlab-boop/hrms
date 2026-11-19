import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const helpers = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentRequesters.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText)(helpers)
const { recruitmentRequesterSelection: choose, eligibleRecruitmentRequesters: eligible } = helpers
const person = (userId, clientId = 55) => ({ userId, displayName: `Requester ${userId}`, email: `requester${userId}@example.invalid`, clientId })
const snapshot = (rows, clientId = 55, status = 'ready') => ({ rows, clientId, status, error: '' })

test('ICMR sole employer account replaces an invalid saved requester user', () => {
  assert.equal(choose(snapshot([person(5174)]), 55, 9001, 9002), 5174)
})

test('multiple eligible contacts require selection and never choose the first arbitrary account', () => {
  const state = snapshot([person(5174), person(5175)])
  assert.equal(choose(state, 55, null, null), null)
  assert.equal(choose(state, 55, 9001, 9002), null)
  assert.equal(choose(state, 55, null, 5175), 5175)
})

test('a valid explicit choice survives refresh even when the logged-in contact is also eligible', () => {
  assert.equal(choose(snapshot([person(5174), person(5175)]), 55, 5175, 5174), 5175)
})

test('stale client results, pending results and errors never select an account', () => {
  const rows = [person(5174), person(888, 20)]
  assert.equal(choose(snapshot(rows), 20, 888, 888), null)
  assert.deepEqual(eligible(snapshot(rows), 55).map(row => row.userId), [5174])
  for (const status of ['idle', 'loading', 'error']) assert.equal(choose(snapshot(rows, 55, status), 55, 5174, 5174), null)
})

test('only eligible auth-user accounts can supply a sole requester default', () => {
  const row = person(11, 20)
  assert.equal(choose(snapshot([row], 20), 20), 11)
  assert.equal(choose(snapshot([], 20), 20), null)
  assert.equal(choose(snapshot([row], 20), 0), null)
  assert.equal(choose(snapshot([{ employeeId: 5174, clientId: 55 }]), 55, null, 5174), null, 'Employee Master IDs are never treated as auth-user IDs')
})

test('new payloads clear old employee defaults and preserve auth requester separately from the creator', () => {
  const source = readFileSync(new URL('../src/components/RecruitmentRequisitionManager.tsx', import.meta.url), 'utf8')
  const ast = ts.createSourceFile('RecruitmentRequisitionManager.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const declaration = ast.statements.find(node => ts.isFunctionDeclaration(node) && node.name?.text === 'normalize')
  assert.ok(declaration)
  const compiled = ts.transpileModule(declaration.getText(ast), { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText
  const normalize = new Function(`${compiled}; return normalize;`)()
  const stale = normalize({ id: 0, requestedByEmployeeId: 5174, clientId: 55 })
  assert.equal(stale.requesterUserId, null)
  assert.equal(stale.requestedByEmployeeId, null)
  const auth = normalize({ id: 0, requestedByEmployeeId: 5174, requesterUserId: 119, requestedByUserId: 7, clientId: 55 })
  assert.equal(auth.requesterUserId, 119)
  assert.equal(auth.requestedByEmployeeId, null)
  assert.equal(auth.requestedByUserId, 7)
  const legacy = normalize({ id: 88, requestedByEmployeeId: 5174, requesterUserId: null, clientId: 55 })
  assert.equal(legacy.requestedByEmployeeId, 5174)
  assert.equal(legacy.requesterUserId, null)
})

test('requester fetch carries explicit client and cancellation signal', async () => {
  const service = {}, calls = [], controller = new AbortController()
  const api = { getJsonResult: (...args) => { calls.push(args); return Promise.resolve({ ok: true, data: [] }) } }
  const compiled = ts.transpileModule(readFileSync(new URL('../src/services/recruitmentService.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', compiled)(service, path => { assert.equal(path, './apiClient'); return api })
  await service.getRecruitmentRequesters(55, controller.signal)
  assert.equal(calls[0][0], '/api/recruitment/requesters?clientId=55')
  assert.equal(calls[0][2].signal, controller.signal)
})
