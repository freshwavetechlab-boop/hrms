import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const access = {}
const compile = file => ts.transpileModule(readFileSync(new URL(file, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', compile('../src/utils/clientAccess.ts'))(access)
const { assignedClients, chooseAssignedClient, isSelectedClientUser, isUnrestrictedClientAdmin, projectClientAccess } = access
const selected = { id: 7, clientId: null, clientAccessMode: 'Selected', assignedClientIds: [55, 20, 55, 0, -1, 1.5], permissions: ['settings.manage', 'security.manage', 'employees.manage', 'workflow.manage'], roles: ['super_admin', 'payroll_executive'] }
test('selected assignments are unique positive IDs and revoked saved choices reset to the first allowed client', () => {
  assert.deepEqual(assignedClients(selected), [55, 20])
  assert.equal(chooseAssignedClient(selected, 20), 20)
  assert.equal(chooseAssignedClient(selected, 999), 55)
  assert.equal(chooseAssignedClient({ assignedClientIds: [] }, 55), 0)
})
test('a selected scope never becomes unrestricted admin even when its role carries admin permission codes', () => {
  assert.ok(isSelectedClientUser(selected)); assert.equal(isUnrestrictedClientAdmin(selected), false)
  const projected = projectClientAccess(selected, 20)
  assert.equal(projected.clientId, 20)
  assert.deepEqual(projected.permissions, ['settings.manage', 'employees.manage'])
  assert.deepEqual(projected.roles, ['payroll_executive'])
  assert.equal(selected.clientId, null)
  assert.equal(projectClientAccess(selected, 999).clientId, 0)
})
test('legacy single-client and All clients identities retain their established shape and grants', () => {
  for (const user of [{ id: 1, clientId: 55, permissions: ['security.manage'] }, { id: 2, permissions: ['settings.manage'] }]) assert.equal(projectClientAccess(user, 20), user)
  assert.equal(isUnrestrictedClientAdmin({ clientId: 55, permissions: ['settings.manage'] }), false)
  assert.equal(isUnrestrictedClientAdmin({ permissions: ['reports.view'] }), false)
  assert.ok(isUnrestrictedClientAdmin({ roles: ['super_admin'] }))
  assert.ok(isUnrestrictedClientAdmin({ permissions: ['settings.manage'] }))
})
test('request context follows active assigned client, remains absent for legacy and auth, and blocks switches during writes', () => {
  const memory = new Map()
  const sessionStorage = { getItem: key => memory.get(key) ?? null, setItem: (key, value) => memory.set(key, value) }
  const requests = {}
  new Function('exports', 'require', 'sessionStorage', compile('../src/services/clientRequestScope.ts'))(requests, () => access, sessionStorage)
  const { configureClientRequestScope, activeClientHeader, setActiveClientRequestScope, beginClientScopedWrite, filterActiveClientOptions } = requests
  assert.equal(configureClientRequestScope(selected), 55)
  assert.equal(activeClientHeader('/api/employees'), '55')
  assert.deepEqual(filterActiveClientOptions([{ id: 20 }, { id: 55 }, { id: 999 }]), [{ id: 55 }])
  assert.deepEqual(filterActiveClientOptions([{ id: 20 }, { id: 55 }, { id: 999 }], true), [{ id: 20 }, { id: 55 }])
  assert.equal(activeClientHeader('/api/auth/me'), undefined)
  assert.equal(activeClientHeader('/api/public/careers'), undefined)
  assert.equal(setActiveClientRequestScope(999), false)
  const finish = beginClientScopedWrite('/api/employees', 'POST')
  assert.equal(setActiveClientRequestScope(20), false)
  finish()
  assert.equal(setActiveClientRequestScope(20), true)
  assert.equal(activeClientHeader('/api/reports'), '20')
  assert.equal(configureClientRequestScope(selected, 55), 55)
  assert.equal(configureClientRequestScope(selected, 999), 55)
  assert.equal(configureClientRequestScope({ ...selected, assignedClientIds: [55] }), 55)
  assert.equal(configureClientRequestScope({ id: 1, clientId: 55 }), 0)
  assert.equal(activeClientHeader('/api/employees'), undefined)
  assert.deepEqual(filterActiveClientOptions([{ id: 20 }, { id: 55 }]), [{ id: 20 }, { id: 55 }])
  configureClientRequestScope(null)
  assert.equal(activeClientHeader('/api/employees'), undefined)
})
