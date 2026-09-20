import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/recruitmentJobVersions.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const current = context.exports.currentRecruitmentJobPostings
const currentCases = context.exports.currentRecruitmentHiringCases
const job = (id, clientId = 20, positionId = 16, status = 'Published') => ({ id, clientId, positionId, status, publicTitle: 'Architect', publicSlug: `slug-${id}` })
test('one current card per position, older records and slugs stay untouched', () => {
  const rows = [job(7), job(8)]; const snapshot = JSON.stringify(rows)
  assert.equal(current(rows).length, 1); assert.equal(current(rows)[0].id, 8)
  assert.equal(JSON.stringify(rows), snapshot)
})
test('different positions and clients with identical titles stay separate', () => {
  assert.equal(current([job(7), job(8, 20, 17), job(9, 30, 16)]).length, 3)
})
test('a later draft or closed posting never hides a live published job', () => {
  assert.equal(current([job(7), job(8, 20, 16, 'Draft'), job(9, 20, 16, 'Closed')])[0].id, 7)
})
test('updating an older posting does not accidentally make it the latest version', () => {
  const older = { ...job(7), updatedAtUtc: '2030-01-01', publishedAtUtc: '2026-01-01' }
  const latest = { ...job(8), publishedAtUtc: '2026-02-01' }
  assert.equal(current([latest, older])[0].id, 8)
})
test('Chief Architect keeps the position-linked journey; the detached older row remains in history', () => {
  const rows = [{ id: 20, clientId: 20, requisitionId: 21, workOrderLineId: 16, positionId: null, status: 'Active' },
    { id: 21, clientId: 20, requisitionId: 21, workOrderLineId: 17, positionId: 15, status: 'Active' }]
  assert.equal(currentCases(rows).length, 1); assert.equal(currentCases(rows)[0].id, 21); assert.equal(rows.length, 2)
})
test('unassigned lines remain independent; explicit archive never returns to the current list', () => {
  const rows = [{ id: 1, clientId: 20, workOrderLineId: 16, status: 'Active' },
    { id: 2, clientId: 20, workOrderLineId: 17, status: 'Active' },
    { id: 3, clientId: 20, workOrderLineId: 18, status: 'Superseded' }]
  assert.equal(currentCases(rows).length, 2)
})
