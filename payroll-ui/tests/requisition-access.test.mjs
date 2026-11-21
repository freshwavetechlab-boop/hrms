import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/requisitionAccess.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { canCreateHiringRequests, canEditHiringRequest, canMaintainApprovedRequisitions, hiringRequestDeleteBlock } = exports
const user = permissions => ({ id: 81, clientId: 55, roles: ['super_admin_copy'], permissions })
const request = (status, extra = {}) => ({ status, clientId: 55, requestedByUserId: 3, ...extra })

test('copied settings administrator has the same approved-edit rights without granting rights by role name', () => {
  assert.equal(canCreateHiringRequests(user(['settings.manage'])), true)
  assert.equal(canEditHiringRequest(user(['settings.manage']), request('Approved')), true)
  assert.equal(canMaintainApprovedRequisitions(user(['settings.manage'])), true)
  assert.equal(canEditHiringRequest(user([]), request('Approved')), false)
  assert.equal(canMaintainApprovedRequisitions(user(['recruitment.manage'])), false)
})

test('read-only accounts and other clients never get editing while request creators may edit their own drafts', () => {
  assert.equal(canEditHiringRequest(user(['recruitment.rfr.view']), request('Draft')), false)
  assert.equal(canEditHiringRequest(user(['recruitment.rfr.create']), request('Draft')), false)
  assert.equal(canEditHiringRequest(user(['recruitment.rfr.create']), request('Draft', { requesterUserId: 81 })), true)
  assert.equal(canEditHiringRequest(user(['recruitment.rfr.create']), request('Approved', { requesterUserId: 81 })), false)
  assert.equal(canEditHiringRequest(user(['settings.manage']), request('Approved', { clientId: 91 })), false)
  assert.equal(canEditHiringRequest(user(['settings.manage']), request('Pending Approval')), false)
})

test('deletion explains the same open-position and JD dependencies protected by the API', () => {
  assert.match(hiringRequestDeleteBlock(request('Approved', { hasLinkedOpenPosition: true })), /open position/)
  assert.match(hiringRequestDeleteBlock(request('Draft', { hasLinkedJobDescription: true })), /JD/)
  assert.equal(hiringRequestDeleteBlock(request('Draft')), '')
})

test('selected client assignments continue to restrict settings administrators', () => {
  const selected = { ...user(['settings.manage']), clientId: null, clientAccessMode: 'Selected', assignedClientIds: [55] }
  assert.equal(canEditHiringRequest(selected, request('Approved')), true)
  assert.equal(canEditHiringRequest(selected, request('Approved', { clientId: 91 })), false)
  assert.equal(canEditHiringRequest({ ...selected, assignedClientIds: [] }, request('Approved')), false)
})
