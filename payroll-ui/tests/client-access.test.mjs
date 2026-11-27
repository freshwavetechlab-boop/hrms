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

test('recruitment All reads and authoritative configuration commands avoid stale headers without widening other writes', () => {
  const memory = new Map()
  const sessionStorage = { getItem: key => memory.get(key) ?? null, setItem: (key, value) => memory.set(key, value) }
  const requests = {}
  new Function('exports', 'require', 'sessionStorage', compile('../src/services/clientRequestScope.ts'))(requests, () => access, sessionStorage)
  requests.configureClientRequestScope(selected, 20)
  for (const path of ['/api/recruitment/dashboard', '/api/recruitment/applications', '/api/recruitment/pipeline-workspace', '/api/recruitment/client-configuration-summary', '/api/recruitment-orchestration/pipelines', '/api/recruitment-orchestration/pipeline-versions/13']) assert.equal(requests.activeClientHeader(path, 'GET'), undefined)
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/lookups?clientId=55', 'GET'), undefined)
  assert.equal(requests.activeClientHeader('/api/recruitment/requisitions', 'POST'), '20')
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/pipeline-configurations', 'POST'), undefined)
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/pipelines/8/create-similar', 'POST'), undefined)
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/interview-competencies', 'POST'), undefined)
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/interview-competencies', 'DELETE'), '20')
  assert.equal(requests.activeClientHeader('/api/employees?clientId=55', 'GET'), '20')
  assert.equal(requests.activeClientHeader('/api/excel-payslips/batches', 'GET'), '20')
  const finish = requests.beginClientScopedWrite('/api/recruitment-orchestration/pipeline-configurations', 'POST')
  assert.equal(requests.setActiveClientRequestScope(55), false)
  finish()
  assert.equal(requests.setActiveClientRequestScope(55), true)
})

test('the email reminder bell aggregates assigned clients without changing explicit reads or writes', () => {
  const requests = {}
  new Function('exports', 'require', 'sessionStorage', compile('../src/services/clientRequestScope.ts'))(requests, () => access, { getItem: () => null, setItem() {} })
  requests.configureClientRequestScope(selected, 20)
  for (const path of ['/api/employees/email-reminders', '/api/employees/email-reminders?offset=100&limit=100']) assert.equal(requests.activeClientHeader(path), undefined)
  for (const path of ['/api/employees/email-reminders?clientId=55', '/api/employees/email-reminders?clientId=0', '/api/employees/email-reminders?client_id=55', '/api/employees/email-reminders?clientIds=55']) assert.equal(requests.activeClientHeader(path), '20')
  assert.equal(requests.activeClientHeader('/api/employees/email-reminders', 'POST'), '20')
  assert.equal(requests.activeClientHeader('/api/employees/email-reminders/1'), '20')
  assert.equal(requests.activeClientHeader('/api/employees'), '20')
})

test('deleting a pipeline from All uses its explicit owning client and retains ordinary write scoping', () => {
  const requests = {}
  const sessionStorage = { getItem: () => null, setItem: () => {} }
  new Function('exports', 'require', 'sessionStorage', compile('../src/services/clientRequestScope.ts'))(requests, () => access, sessionStorage)
  requests.configureClientRequestScope(selected, 20)
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/pipelines/8?clientId=55', 'DELETE'), undefined)
  for (const path of ['/api/recruitment-orchestration/pipelines/8', '/api/recruitment-orchestration/pipelines/8?clientId=0', '/api/recruitment-orchestration/pipelines/8?clientId=invalid', '/api/recruitment-orchestration/pipelines/8/versions?clientId=55', '/api/employees/8?clientId=55']) {
    assert.equal(requests.activeClientHeader(path, 'DELETE'), '20')
  }
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/pipelines/8?clientId=55', 'POST'), '20')
  const finish = requests.beginClientScopedWrite('/api/recruitment-orchestration/pipelines/8?clientId=55', 'DELETE')
  assert.equal(requests.setActiveClientRequestScope(55), false)
  finish()
  assert.equal(requests.setActiveClientRequestScope(55), true)
})

test('template and candidate-form copies use explicit source and target without widening ordinary routes', () => {
  const requests = {}
  new Function('exports', 'require', 'sessionStorage', compile('../src/services/clientRequestScope.ts'))(requests, () => access, { getItem: () => null, setItem: () => {} })
  requests.configureClientRequestScope(selected, 20)
  for (const path of ['/api/recruitment-admin/templates', '/api/recruitment-orchestration/forms', '/api/recruitment-orchestration/forms/17']) {
    assert.equal(requests.activeClientHeader(path, 'GET'), undefined)
  }
  for (const path of ['/api/recruitment-admin/templates/8/create-similar', '/api/recruitment-orchestration/forms/17/create-similar', '/api/recruitment-admin/templates', '/api/recruitment-orchestration/forms']) {
    assert.equal(requests.activeClientHeader(path, 'POST'), undefined)
  }
  for (const path of ['/api/recruitment-orchestration/forms/17/versions?clientId=55', '/api/recruitment-orchestration/form-versions/27/publish?definitionId=17&clientId=55']) {
    assert.equal(requests.activeClientHeader(path, 'POST'), undefined)
    assert.equal(requests.activeClientHeader(path.replace('clientId=55', 'clientId=0'), 'POST'), '20')
    assert.equal(requests.activeClientHeader(path.replace('clientId=55', 'clientId=invalid'), 'POST'), '20')
  }
  assert.equal(requests.activeClientHeader('/api/recruitment-orchestration/forms/17?clientId=55', 'DELETE'), undefined)
  for (const path of ['/api/recruitment-admin', '/api/employees/field-configuration/forms/17']) assert.equal(requests.activeClientHeader(path, 'GET'), '20')
  for (const path of ['/api/recruitment-admin/settings', '/api/recruitment-admin/templates/8', '/api/recruitment-orchestration/forms/17/versions', '/api/recruitment-orchestration/form-versions/27/publish', '/api/employees/field-configuration/forms', '/api/excel-payslips/batches']) assert.equal(requests.activeClientHeader(path, 'POST'), '20')
  const finish = requests.beginClientScopedWrite('/api/recruitment-orchestration/forms/17/create-similar', 'POST')
  assert.equal(requests.setActiveClientRequestScope(55), false)
  finish()
  assert.equal(requests.setActiveClientRequestScope(55), true)
})
