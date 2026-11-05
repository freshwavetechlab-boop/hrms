import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
const code = ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentRosterOperations.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', code)(exports)
const { filterRosterRows, selectedRosterRows, rosterLocationName, rosterMasterLocationField, rosterPermissions, rosterReleaseReady, signingAssetFileError } = exports
const rows = [
  { id: 1, sourceGroup: 'Original Mumbai list', workLocationId: 11, workLocation: 'Old source text', documentIssues: [] },
  { id: 2, sourceGroup: 'Original Delhi list', workLocationId: 22, workLocation: 'Delhi', documentIssues: [] },
  { id: 3, sourceGroup: 'Original Mumbai list', workLocationId: null, workLocation: 'NIRWoH – Mumbai', documentIssues: ['Designation required'] },
]

test('work-location filtering uses linked master IDs while legacy source-group and source text remain distinct', () => {
  assert.deepEqual(filterRosterRows(rows, '', 11).map(row => row.id), [1])
  assert.deepEqual(filterRosterRows(rows, 'Original Mumbai list', 0).map(row => row.id), [1, 3])
  assert.deepEqual(filterRosterRows(rows, 'Original Mumbai list', -1).map(row => row.id), [3])
  assert.deepEqual(filterRosterRows(rows, 'Original Mumbai list', 22), [])
  assert.equal(rosterLocationName(rows[0], [{ id: 11, name: 'NIRWoH – Mumbai' }]), 'NIRWoH – Mumbai')
  assert.equal(rosterLocationName(rows[2], []), 'NIRWoH – Mumbai')
})

test('missing-detail profiles can be linked, but only visible ready selections can generate letters', () => {
  const visible = filterRosterRows(rows, 'Original Mumbai list', 0)
  assert.deepEqual(selectedRosterRows(visible, [1, 2, 3, 3, 999]).map(row => row.id), [1, 3])
  assert.deepEqual(selectedRosterRows(visible, [1, 2, 3, 999], true).map(row => row.id), [1])
  assert.equal(rosterReleaseReady(rows[0]), true)
  assert.equal(rosterReleaseReady(rows[2]), false)
  const legacyDraft = { ...rows[2], documentIssues: [] }
  assert.equal(selectedRosterRows([legacyDraft], [3], true).length, 1)
  assert.equal(rosterReleaseReady(legacyDraft), false, 'legacy free-text location remains draft-only until linked to a master')
})

test('signature image validation accepts PNG/JPEG up to 2 MB and rejects empty, disguised or oversized files', () => {
  assert.equal(signingAssetFileError({ name: 'synthetic.png', type: 'image/png', size: 2 * 1024 * 1024 }), '')
  assert.equal(signingAssetFileError({ name: 'synthetic.jpg', type: 'image/jpeg', size: 100 }), '')
  assert.equal(signingAssetFileError({ name: 'synthetic.JPEG', type: '', size: 100 }), '')
  assert.match(signingAssetFileError({ name: 'disguised.png', type: 'application/pdf', size: 100 }), /PNG or JPEG/)
  assert.match(signingAssetFileError({ name: 'oversized.png', type: 'image/png', size: 2 * 1024 * 1024 + 1 }), /2 MB/)
  assert.match(signingAssetFileError({ name: 'empty.png', type: 'image/png', size: 0 }), /not empty/)
})

test('offer management, issue and candidate intake remain separate grants with existing broad permission support', () => {
  assert.deepEqual(rosterPermissions(['recruitment.offer.manage']), { canImport: false, canManageOffers: true, canRelease: false })
  assert.deepEqual(rosterPermissions(['recruitment.offer.issue']), { canImport: false, canManageOffers: false, canRelease: true })
  assert.deepEqual(rosterPermissions(['recruitment.candidate.manage']), { canImport: true, canManageOffers: false, canRelease: false })
  assert.deepEqual(rosterPermissions(['recruitment.manage']), { canImport: true, canManageOffers: true, canRelease: true })
  assert.deepEqual(rosterPermissions(['settings.manage']), { canImport: true, canManageOffers: true, canRelease: true })
  assert.deepEqual(rosterPermissions(['recruitment.offer.view']), { canImport: false, canManageOffers: false, canRelease: false })
})

test('only master-resolved location inputs are marked read-only for linked profiles', () => {
  for (const key of ['jobLocation', 'workLocation', 'workLocationName', 'instituteName', 'institutionName', 'institutionShortName', 'instituteAddress', 'institutionAddress', 'workLocationAddress', 'workLocationCity', 'workLocationState', 'city', 'state', 'institutionLocation']) {
    assert.equal(rosterMasterLocationField(key), true, key)
    assert.equal(rosterMasterLocationField(key.toUpperCase()), true, `case-insensitive ${key}`)
  }
  for (const key of ['candidateEmail', 'monthlyBasic', 'positionTitle', 'joiningDate']) assert.equal(rosterMasterLocationField(key), false)
  const serverSource = readFileSync(new URL('../../Payroll.API/Repositories/RecruitmentDocumentRepository.Release.cs', import.meta.url), 'utf8')
  const aliasBlock = /LocationInputKeys\s*=\s*new\(new\[\]\s*\{([^}]+)\}/.exec(serverSource)?.[1]
  assert.ok(aliasBlock, 'authoritative backend location aliases are available')
  for (const [, alias] of aliasBlock.matchAll(/"([^"]+)"/g)) assert.equal(rosterMasterLocationField(alias), true, `backend-managed ${alias} must be read-only`)
})
