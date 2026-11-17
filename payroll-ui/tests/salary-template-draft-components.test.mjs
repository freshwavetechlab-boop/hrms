import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/salaryTemplateDraftComponents.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { mergeSalaryTemplateDraftComponents } = exports
const saved = [{ id: 1, code: 'BASIC', name: 'Basic', taxable: true, priority: '10', formula: 'CTC*40%', value: '', proRata: true }]
test('extracted masters append atomically while every shared master retains its identity and metadata', () => {
  const proposed = [{ id: 2, code: 'SPECIAL_ALLOWANCE', name: 'Special allowance', value: '', formula: '', proRata: true }]
  const merged = mergeSalaryTemplateDraftComponents(saved, proposed)
  assert.equal(merged[0], saved[0]); assert.equal(merged[1], proposed[0])
  assert.equal(saved.length, 1); assert.equal(merged[0].formula, 'CTC*40%')
  assert.equal(merged[1].value, '', 'employee-specific source amount is not copied into a shared default')
})
test('code, ID and within-draft collisions never replace an existing component or partially mutate arrays', () => {
  for (const proposed of [
    [{ id: 2, code: ' basic ', name: 'Changed master' }],
    [{ id: 1, code: 'OTHER', name: 'Conflicting ID' }],
    [{ id: 2, code: 'OTHER', name: 'One' }, { id: 3, code: 'other', name: 'Two' }],
    [{ id: 2, code: 'OTHER', name: 'One' }, { id: 2, code: 'THIRD', name: 'Two' }],
  ]) assert.throws(() => mergeSalaryTemplateDraftComponents(saved, proposed), /already exists/)
  assert.deepEqual(saved, [{ id: 1, code: 'BASIC', name: 'Basic', taxable: true, priority: '10', formula: 'CTC*40%', value: '', proRata: true }])
})
test('empty additions preserve the existing path and invalid IDs or unnamed masters are refused', () => {
  assert.equal(mergeSalaryTemplateDraftComponents(saved), saved)
  for (const row of [{ id: 0, code: 'X', name: 'X' }, { id: 2.5, code: 'X', name: 'X' }, { id: 2, code: '', name: 'X' }, { id: 2, code: 'X', name: '' }]) assert.throws(() => mergeSalaryTemplateDraftComponents(saved, [row]), /missing its ID/)
})
