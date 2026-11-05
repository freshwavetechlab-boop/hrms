import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const scope = {}
const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentWorkLocation.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', compiled)(scope)

test('work location selection distinguishes two institutes in the same city by ID', () => {
  const rows = [{ id: 11, name: 'NIRWoH', city: 'Mumbai' }, { id: 12, name: 'Another institute', city: 'Mumbai' }]
  assert.deepEqual(rows.filter(row => scope.matchesRecruitmentWorkLocation(row.id, 11)).map(row => row.id), [11])
  assert.equal(scope.matchesRecruitmentWorkLocation(null, 11), false)
  assert.equal(scope.matchesRecruitmentWorkLocation(0, 11), false)
  assert.equal(scope.matchesRecruitmentWorkLocation(null, 0), true)
})

test('labels show the actual master name and useful city without duplicating it', () => {
  assert.equal(scope.recruitmentWorkLocationLabel({ id: 11, name: 'NIRWoH', city: 'Mumbai' }), 'NIRWoH · Mumbai')
  assert.equal(scope.recruitmentWorkLocationLabel({ id: 12, name: 'NIMR – Delhi', city: 'Delhi' }), 'NIMR – Delhi')
})
