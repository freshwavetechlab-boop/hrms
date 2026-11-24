import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentOfferDeletion.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { canHardDeleteRecruitmentOffers, offerDeletionPath } = exports
const user = { id: 1, isActive: true, mustChangePassword: false, clientId: null, clientAccessMode: 'Legacy', roles: ['super_admin'] }

test('hard deletion requires an active unrestricted Superadmin, independently of ordinary permissions', () => {
  assert.equal(canHardDeleteRecruitmentOffers(user), true)
  assert.equal(canHardDeleteRecruitmentOffers({ ...user, roles: ['SUPER_ADMIN'] }), true)
  for (const actor of [null, { ...user, id: 0 }, { ...user, isActive: false }, { ...user, mustChangePassword: true },
    { ...user, clientId: 42 }, { ...user, clientId: 0 }, { ...user, clientAccessMode: 'Selected', assignedClientIds: [42] },
    { ...user, roles: ['admin'], permissions: ['settings.manage', 'recruitment.manage'] }])
    assert.equal(canHardDeleteRecruitmentOffers(actor), false)
})

test('equal numeric offer and employee-list IDs use their own endpoints', () => {
  assert.equal(offerDeletionPath({ sourceType: 'Roster', sourceId: 7, clientId: 42 }), '/api/recruitment/document-roster/rows/7?clientId=42')
  assert.equal(offerDeletionPath({ sourceType: 'Offer', sourceId: 7, clientId: 42 }), '/api/recruitment/offers/7')
  for (const target of [{ sourceType: 'Other', sourceId: 7, clientId: 42 }, { sourceType: 'Roster', sourceId: -1, clientId: 42 },
    { sourceType: 'Offer', sourceId: 7, clientId: 0 }, { sourceType: 'Offer', sourceId: NaN, clientId: 42 }])
    assert.throws(() => offerDeletionPath(target))
})
