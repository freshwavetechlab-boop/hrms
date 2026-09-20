import { readFileSync } from 'node:fs'
import test from 'node:test'
import assert from 'node:assert/strict'
import vm from 'node:vm'
import ts from 'typescript'
const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const ui = read('../src/components/LocalLlmRuntimeControl.tsx')
const routes = read('../../Payroll.API/Services/LocalLlmRecoveryEndpoints.cs')

test('control is separate from inference test and only rendered for a local provider', () => {
  const settings = read('../src/components/AiIntegrationSettings.tsx')
  assert.match(settings, /row.providerCode === 'LocalOpenAICompatible' && <LocalLlmRuntimeControl/)
  assert.match(ui, /user\?\.clientId == null/)
  assert.match(ui, /role.toLowerCase\(\) === 'super_admin'/)
  assert.doesNotMatch(ui, /apiKey|schtasks|eeslindia|activateAiIntegration/)
  assert.match(ui, /Popconfirm/)
  assert.match(ui, /!status\?\.canStart/)
  assert.match(ui, /outcome is unknown/)
})
test('both routes enter the same exact-global authorization before looking up models', () => {
  assert.match(routes, /MapGet\("\/api\/integrations\/ai\/\{id:long\}\/runtime"/)
  assert.match(routes, /MapPost\("\/api\/integrations\/ai\/\{id:long\}\/start-runtime"/)
  assert.ok(routes.indexOf('!LocalLlmRecoveryService.IsAllowed(user)') < routes.indexOf('models.GetGlobalPoolAsync'))
  assert.ok(routes.indexOf('await Record("requested", null)') < routes.indexOf('SendAsync(model, user, start'))
  assert.match(routes, /await Record\("result", status\)/)
})
test('control transport disallows redirects to avoid credential forwarding', () => {
  assert.match(read('../../Payroll.API/Program.cs'), /AddHttpClient\("LocalLlmControl"\).*AllowAutoRedirect = false/)
})

const tree = ts.createSourceFile('Runtime.tsx', ui, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
let startSource
const find = node => {
  if (ts.isVariableDeclaration(node) && node.name.getText(tree) === 'start') startSource = node.initializer.getText(tree)
  ts.forEachChild(node, find)
}
find(tree)
const harness = (overrides = {}) => {
  const calls = []
  const scope = vm.createContext({
    allowed: true, busy: false, status: { state: 'Stopped', canStart: true }, modelId: 6,
    setBusy: value => calls.push(['busy', value]), setError: value => calls.push(['error', value]),
    setStatus: value => calls.push(['status', value]),
    postJson: async (...args) => { calls.push(['post', ...args]); return { ok: true, data: { state: 'Starting', canStart: false } } },
    ...overrides,
  })
  vm.runInContext(ts.transpileModule(`globalThis.start = ${startSource}`, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, scope)
  return { calls, start: () => scope.start() }
}
test('confirmed Start calls fixed endpoint once and does not claim Running prematurely', async () => {
  const { calls, start } = harness()
  await start()
  const sent = calls.filter(call => call[0] === 'post')
  assert.equal(sent.length, 1)
  assert.equal(sent[0][1], '/api/integrations/ai/6/start-runtime')
  assert.equal(JSON.stringify(sent[0][2]), '{}')
  assert.equal(calls.find(call => call[0] === 'status')[1].state, 'Starting')
  assert.deepEqual(calls.at(-1), ['busy', false])
})
test('denied, busy, unknown and healthy states cannot dispatch Start', async () => {
  for (const change of [{ allowed: false }, { busy: true }, { status: null }, { status: { state: 'Running', canStart: false } }]) {
    const { calls, start } = harness(change)
    await start()
    assert.equal(calls.length, 0)
  }
})
test('network failure clears stale start eligibility and reports unknown outcome without retry', async () => {
  let count = 0
  const { calls, start } = harness({ postJson: async () => { count++; return { ok: false, error: '' } } })
  await start()
  assert.equal(count, 1)
  assert.deepEqual(calls.find(call => call[0] === 'status'), ['status', null])
  assert.match(calls.filter(call => call[0] === 'error').at(-1)[1], /outcome is unknown/)
})
