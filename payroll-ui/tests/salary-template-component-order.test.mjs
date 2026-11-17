import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/salaryTemplateComponentOrder.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { moveSalaryTemplateLine, salaryComponentDropAction, salaryComponentDragPayload } = exports
const lines = [{ componentId: '1', value: '', formula: 'CTC * 0.4', proRataOverride: 'true' }, { componentId: '2', value: '', formula: 'BASIC_EARNED * 0.12' }, { componentId: '3', value: '100', calculationType: 'Fixed Amount' }]
test('moving down reaches the original target index and preserves exact line objects and formulas', () => {
  const result = moveSalaryTemplateLine(lines, '1', '2')
  assert.deepEqual(result.map(line => line.componentId), ['2', '1', '3'])
  assert.equal(result[1], lines[0]); assert.equal(result[1].formula, 'CTC * 0.4')
  assert.deepEqual(lines.map(line => line.componentId), ['1', '2', '3'])
})
test('moving up or to the end never loses components, zero values or overrides', () => {
  assert.deepEqual(moveSalaryTemplateLine(lines, '3', '1').map(line => line.componentId), ['3', '1', '2'])
  assert.deepEqual(moveSalaryTemplateLine(lines, '1', '3').map(line => line.componentId), ['2', '3', '1'])
  const zero = [{ componentId: 'a', value: '0' }, { componentId: 'b', value: '1' }]
  assert.equal(moveSalaryTemplateLine(zero, 'a', 'b')[1].value, '0')
})
test('stale, same-row or ambiguous duplicate reorder is a no-op', () => {
  assert.equal(moveSalaryTemplateLine(lines, '1', '1'), lines)
  assert.equal(moveSalaryTemplateLine(lines, 'missing', '1'), lines)
  const duplicate = [...lines, lines[0]]
  assert.equal(moveSalaryTemplateLine(duplicate, '1', '3'), duplicate)
})
test('library drop adds only an existing active not-yet-added component', () => {
  assert.deepEqual(salaryComponentDropAction(salaryComponentDragPayload('library', '4'), ['1', '4'], ['1'], '1'), { type: 'add', id: '4' })
  assert.equal(salaryComponentDropAction(salaryComponentDragPayload('library', '1'), ['1', '4'], ['1'], '1'), null)
  assert.equal(salaryComponentDropAction(salaryComponentDragPayload('library', '99'), ['1', '4'], ['1'], '1'), null)
})
test('existing-line drop never adds, including dropping on empty canvas or own row', () => {
  assert.deepEqual(salaryComponentDropAction(salaryComponentDragPayload('line', '1'), ['1', '2'], ['1', '2'], '2'), { type: 'move', sourceId: '1', targetId: '2' })
  assert.equal(salaryComponentDropAction(salaryComponentDragPayload('line', '1'), ['1', '2'], ['1', '2']), null)
  assert.equal(salaryComponentDropAction(salaryComponentDragPayload('line', '1'), ['1', '2'], ['1', '2'], '1'), null)
})
test('text, external payloads, wrong versions and removed source rows are ignored', () => {
  for (const payload of ['BASIC', '{', 'null', '[]', '{"version":2,"kind":"library","id":"4"}', '{"version":1,"kind":"unknown","id":"4"}', '{"version":1,"kind":"library","id":4}']) assert.equal(salaryComponentDropAction(payload, ['4'], ['1'], '1'), null)
  assert.equal(salaryComponentDropAction(salaryComponentDragPayload('line', 'removed'), ['1', '4'], ['1'], '1'), null)
})
