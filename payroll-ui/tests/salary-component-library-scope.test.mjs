import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const modules = new Map()
function load(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}
  modules.set(name, exports)
  const source = readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8')
  new Function('exports', 'require', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports, dependency => {
    assert.equal(dependency, './salaryTemplateContext')
    return load('salaryTemplateContext')
  })
  return exports
}
const { salaryComponentLibraryScope } = load('salaryComponentLibraryScope')
const clients = [{ id: 10, name: 'Current client' }, { id: 20, name: 'Other client' }, { id: 30, name: 'Third client' }]
const component = (id, code = `COMPONENT_${id}`) => ({ id, code, name: code, formula: `SOURCE_${id}_EARNED * 12%`, active: true })
const template = (id, clientId, ids) => ({ id, clientId, lines: ids.map(id => ({ componentId: String(id), formula: `SOURCE_${id}_EARNED * 12%` })) })

test('browse defaults include current, default/shared and unused masters; other-client usage stays opt-in', () => {
  const components = [1, 2, 3, 4, 5].map(id => component(id))
  const templates = [template(1, '10', [1]), template(2, '20', [2]), template(3, '', [3]), template(4, '0', [4])]
  const scope = salaryComponentLibraryScope(components, templates, 10, clients)
  assert.deepEqual([...scope.primaryIds], ['1', '3', '4', '5'])
  assert.deepEqual([...scope.otherClientIds], ['2'])
  assert.equal(scope.usageLabels.get('1'), 'Used by Current client')
  assert.equal(scope.usageLabels.get('2'), 'Used by Other client')
  assert.equal(scope.usageLabels.get('3'), 'Shared template')
  assert.equal(scope.usageLabels.get('5'), 'Shared master')
})

test('switching client changes browse defaults without relying on names or component code prefixes', () => {
  const components = [component(1, 'FOREIGN_PREFIX_BASIC'), component(2, 'BASIC'), component(3, 'SHARED')]
  const templates = [template(1, '10', [1]), template(2, '20', [2])]
  assert.deepEqual([...salaryComponentLibraryScope(components, templates, 10, clients).primaryIds], ['1', '3'])
  assert.deepEqual([...salaryComponentLibraryScope(components, templates, 20, clients).primaryIds], ['2', '3'])
})

test('multi-client references and components reused across templates keep exact usage evidence', () => {
  const components = [1, 2, 3].map(id => component(id))
  const templates = [template(1, '10:First;20:Second', [1]), template(2, '20', [2, 3]), template(3, '30', [2]), template(4, '', [3])]
  const scope = salaryComponentLibraryScope(components, templates, 10, clients)
  assert.deepEqual([...scope.primaryIds], ['1', '3'])
  assert.deepEqual([...scope.otherClientIds], ['2'])
  assert.equal(scope.usageLabels.get('1'), 'Used by Current client, Other client')
  assert.equal(scope.usageLabels.get('2'), 'Used by Other client, Third client')
  assert.equal(scope.usageLabels.get('3'), 'Shared template')
})

test('legacy selected foreign lines remain browseable and every source ID/formula/object is untouched', () => {
  const components = [component(1), component(2)]
  const templates = [template(1, '20', [1, 2])]
  const before = structuredClone({ components, templates })
  const scope = salaryComponentLibraryScope(components, templates, 10, clients, ['2'])
  assert.deepEqual([...scope.primaryIds], ['2'])
  assert.deepEqual([...scope.otherClientIds], ['1', '2'])
  assert.equal(scope.usageLabels.get('2'), 'Used by Other client')
  assert.deepEqual({ components, templates }, before)
  assert.equal(components[0].id, 1)
  assert.equal(templates[0].lines[0].formula, 'SOURCE_1_EARNED * 12%')
  scope.primaryIds.clear()
  assert.deepEqual({ components, templates }, before, 'result sets never reference or mutate source arrays')
})

test('missing client labels and malformed legacy usage do not invent ownership or hide selected masters', () => {
  const components = [component(1), component(2), component(3)]
  const templates = [template(1, '99', [1]), template(2, 'unexpected', [2]), template(3, '20', [3])]
  const scope = salaryComponentLibraryScope(components, templates, 10, clients, ['3'])
  assert.deepEqual([...scope.primaryIds], ['2', '3'])
  assert.equal(scope.usageLabels.get('1'), 'Used by Client #99')
  assert.equal(scope.usageLabels.get('2'), 'Template usage needs review')
  assert.equal(scope.usageLabels.get('3'), 'Used by Other client')
})
