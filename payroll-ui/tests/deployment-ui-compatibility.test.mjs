import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

// Keep these components compatible with the Ant Design version installed by
// package-lock.json / npm ci, not just a developer's newer node_modules folder.
const component = name => ts.createSourceFile(name,
  readFileSync(new URL(`../src/components/${name}`, import.meta.url), 'utf8'),
  ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const tags = (file, name) => {
  const found = []
  const visit = node => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && node.tagName.getText(file) === name) found.push(node)
    ts.forEachChild(node, visit)
  }
  visit(file)
  return found
}
const prop = (tag, name) => tag.attributes.properties.find(item => ts.isJsxAttribute(item) && item.name.getText() === name)

for (const name of ['EngineHistoryPanel.tsx', 'EngineMonitoring.tsx']) {
  test(`${name}: non-generic Segmented safely narrows old string/number change values`, () => {
    const file = component(name)
    const controls = tags(file, 'Segmented')
    assert.equal(controls.length, 1)
    assert.equal(controls[0].typeArguments?.length || 0, 0)
    const calls = []
    const context = vm.createContext({ setMetric: value => calls.push(value), setChartMetric: value => calls.push(value) })
    const handler = prop(controls[0], 'onChange').initializer.expression.getText(file)
    vm.runInContext(ts.transpileModule(`globalThis.change = ${handler}`, {
      compilerOptions: { target: ts.ScriptTarget.ES2022 },
    }).outputText, context)
    for (const value of ['requests', 'busy', 'invalid', 1, null]) context.change(value)
    assert.deepEqual(calls, ['requests', 'busy'])
  })
}

test('FrevoPilot retains source and history panels without the unsupported Collapse items prop', () => {
  const file = component('FrevoPilot.tsx')
  const collapses = tags(file, 'Collapse')
  assert.equal(collapses.length, 2)
  for (const collapse of collapses) assert.equal(prop(collapse, 'items'), undefined)
  const panels = tags(file, 'Collapse.Panel')
  assert.deepEqual(panels.map(panel => prop(panel, 'key').initializer.text), ['source', 'history'])
  assert.ok(panels.every(panel => prop(panel, 'header')))
  assert.match(file.text, /<pre>\{result\.sql\}<\/pre>/)
  assert.match(file.text, /onClick=\{\(\) => setRun\(item\)\}/)
})

test('offer preview keeps the same drawer body layout through the supported bodyStyle prop', () => {
  const file = component('OfferLetterPreview.tsx')
  const drawer = tags(file, 'Drawer')[0]
  assert.equal(prop(drawer, 'styles'), undefined)
  const context = vm.createContext({})
  vm.runInContext(`globalThis.body = (${prop(drawer, 'bodyStyle').initializer.expression.getText(file)})`, context)
  assert.deepEqual(JSON.parse(JSON.stringify(context.body)), { padding: 12, display: 'flex', flexDirection: 'column', gap: 12 })
  assert.ok(prop(drawer, 'open'))
  assert.ok(prop(drawer, 'onClose'))
  assert.ok(prop(drawer, 'extra'))
})
