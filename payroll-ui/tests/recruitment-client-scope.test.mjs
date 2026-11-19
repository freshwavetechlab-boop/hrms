import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const clientAccess = {}, scope = {}
const compile = name => ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText
new Function('exports', compile('clientAccess'))(clientAccess)
new Function('exports', 'require', compile('recruitmentClientScope'))(scope, name => {
  assert.equal(name, './clientAccess')
  return clientAccess
})

test('Overview and Pipeline without a client URL start at all permitted clients, even after a previous selection', () => {
  assert.equal(scope.recruitmentClientSelection({ id: 3 }, null, 55, true), 0)
  assert.equal(scope.recruitmentClientSelection({ id: 3 }, '20', 55, true), 20)
  assert.equal(scope.recruitmentClientSelection({ id: 3 }, null, 55, false), 55)
})

test('a real single-client account stays locked regardless of URL or stored selection', () => {
  const user = { id: 4, clientId: 55, clientAccessMode: 'Legacy' }
  assert.equal(scope.recruitmentClientSelection(user, null, 20, true), 55)
  assert.equal(scope.recruitmentClientSelection(user, '20', 0, true), 55)
})

test('selected-client staff can browse All and assigned clients without needing settings rights', () => {
  const user = { id: 8, clientAccessMode: 'Selected', assignedClientIds: [20, 55], permissions: ['recruitment.position.view'] }
  assert.equal(scope.recruitmentClientSelection(user, null, 55, true), 0)
  assert.equal(scope.recruitmentClientSelection(user, '20', 55, true), 20)
  assert.equal(scope.recruitmentClientSelection(user, '999', 55, true), 0)
  assert.equal(scope.recruitmentClientSelection({ ...user, clientId: 55 }, '20', 0, true), 20)
})

test('invalid and stale client selections never become a first-client fallback', () => {
  const user = { clientAccessMode: 'Selected', assignedClientIds: [20, 55] }
  for (const value of ['', 'NaN', '-1', '20.5', '999']) assert.equal(scope.recruitmentClientSelection(user, value, 55, true), 0)
  assert.equal(scope.recruitmentClientSelection(user, null, 999, false), 0)
})
