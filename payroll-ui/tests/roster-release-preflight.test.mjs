import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const text = readFileSync(new URL('../src/components/RecruitmentDocumentRoster.tsx', import.meta.url), 'utf8')
const source = ts.createSourceFile('RecruitmentDocumentRoster.tsx', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const drawer = source.statements.find(node => ts.isFunctionDeclaration(node) && node.name?.text === 'RosterReleaseDrawer')
assert.ok(drawer?.body, 'exercise the actual release drawer')
const declarations = drawer.body.statements.filter(ts.isVariableStatement).flatMap(statement => [...statement.declarationList.declarations])
const variable = name => {
  const node = declarations.find(declaration => ts.isIdentifier(declaration.name) && declaration.name.text === name)
  assert.ok(node, `actual drawer declaration ${name} exists`)
  return `const ${node.getText(source)};`
}
const refs = declarations.filter(declaration => ts.isIdentifier(declaration.name) && declaration.initializer && ts.isCallExpression(declaration.initializer) && declaration.initializer.expression.getText(source) === 'useRef')
const compiled = code => ts.transpileModule(code, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText
const renderCode = compiled(`(() => { ${['checkKey', 'checkCurrent', 'releaseReady', 'release'].map(variable).join('\n')} return { checkKey, releaseReady, release }; })()`)

function harness(overrides = {}) {
  const calls = { released: [], completed: [], errors: [] }
  const ids = [697, 698, 699]
  const revisions = Object.fromEntries(ids.map(id => [String(id), 'a'.repeat(64)]))
  let context
  context = vm.createContext({
    Error, Number, String, JSON,
    clientId: 55, ids, revisions, applySignature: false, asset: { available: false, sha256: '' },
    checkAttempt: 0, loading: false, busy: false, canRelease: true,
    check: { key: '', loading: false, error: '', result: { canRelease: true, issues: [], items: ids.map(id => ({ rosterId: id, ready: true, revision: revisions[String(id)] })) } },
    useRef: initial => ({ current: initial }),
    setBusy: next => { context.busy = next }, setError: value => calls.errors.push(value),
    setCheck: next => { context.check = next },
    releaseRosterLetters: async (...args) => { calls.released.push(JSON.parse(JSON.stringify(args))) },
    onReleased: async count => calls.completed.push(count),
    ...overrides,
  })
  vm.runInContext(compiled(refs.map(declaration => `var ${declaration.getText(source)};`).join('\n')), context)
  const render = () => vm.runInContext(renderCode, context)
  context.check.key = render().checkKey
  return { context, calls, render }
}

test('pending validation and a failed check cannot call the release service', async () => {
  for (const state of [{ loading: true }, { error: 'Connection ended before validation completed.' }, { result: null }, { result: { canRelease: false, issues: ['Layout needs attention'], items: [] } }]) {
    const h = harness()
    Object.assign(h.context.check, state)
    const ui = h.render()
    assert.equal(ui.releaseReady, false)
    await ui.release()
    assert.equal(h.calls.released.length, 0)
    assert.equal(h.calls.completed.length, 0)
  }
})

test('a changed selection, row revision or signature choice invalidates a previously successful check', async () => {
  for (const change of [
    context => { context.ids = [697, 698] },
    context => { context.revisions = { ...context.revisions, 699: 'b'.repeat(64) } },
    context => { context.asset = { available: true, sha256: 'new-signature' }; context.applySignature = true },
  ]) {
    const h = harness()
    change(h.context)
    const ui = h.render()
    assert.equal(ui.releaseReady, false)
    await ui.release()
    assert.equal(h.calls.released.length, 0)
  }
})

test('even a current-key response must cover each selected row and its exact reviewed revision', async () => {
  for (const change of [
    result => { result.items[2].revision = 'stale' },
    result => { result.items[2].ready = false },
    result => { result.items.pop() },
    result => { result.items[2].rosterId = 700 },
  ]) {
    const h = harness()
    change(h.context.check.result)
    const ui = h.render()
    assert.equal(ui.releaseReady, false)
    await ui.release()
    assert.equal(h.calls.released.length, 0)
  }
})

test('a matching successful check releases the reviewed selection and revisions once', async () => {
  const h = harness()
  const ui = h.render()
  assert.equal(ui.releaseReady, true)
  await ui.release()
  assert.deepEqual(h.calls.released, [[55, [697, 698, 699], false, { 697: 'a'.repeat(64), 698: 'a'.repeat(64), 699: 'a'.repeat(64) }]])
  assert.deepEqual(h.calls.completed, [3])
  assert.equal(h.context.check.result, null, 'a completed release requires fresh validation')
  assert.equal(h.render().releaseReady, false)
})

test('rapid repeat clicks on the same ready render cannot start a second release', async () => {
  let finish
  const pending = new Promise(resolve => { finish = resolve })
  let count = 0
  const h = harness({ releaseRosterLetters: async () => { count++; await pending } })
  const ui = h.render()
  const first = ui.release()
  await ui.release()
  assert.equal(count, 1)
  finish()
  await first
  assert.deepEqual(h.calls.completed, [3])
})

test('authorization and an asset still loading block release despite a successful prior check', async () => {
  for (const change of [{ canRelease: false }, { loading: true }, { busy: true }, { asset: null }]) {
    const h = harness(change)
    await h.render().release()
    assert.equal(h.calls.released.length, 0)
  }
})
