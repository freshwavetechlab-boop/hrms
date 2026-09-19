import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/AiIntegrationSettings.tsx', import.meta.url), 'utf8')
const file = ts.createSourceFile('AiIntegrationSettings.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/components/aiModelActions.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { canMakeAiModelActive, localPrimaryModel, aiModelEndpointLabel } = context.exports
const local = { id: 6, clientId: 0, providerCode: 'LocalOpenAICompatible', modelName: 'hrms-local',
  endpointUrl: 'https://synthetic.example/chat', hasApiKey: true, credentialStatus: 'Ready', apiKey: '',
  isActive: false, enableAiScoring: false, isPrimary: false, requestTimeoutSeconds: 210,
  priority: 150, monthlyRequestLimit: 1000, accountEmail: 'test@example.com' }

test('saved local model can be explicitly activated even while availability is disabled', () => {
  assert.equal(canMakeAiModelActive(local), true)
  const request = localPrimaryModel(local)
  assert.equal(request.id, local.id)
  assert.equal(request.enableAiScoring, true)
  assert.equal(request.isActive, true)
  assert.equal(request.isPrimary, true)
  for (const field of ['endpointUrl', 'requestTimeoutSeconds', 'priority', 'monthlyRequestLimit', 'accountEmail', 'clientId'])
    assert.equal(request[field], local[field])
  assert.equal(request.apiKey, '')
  assert.equal(local.isActive, false, 'do not mutate the displayed saved row before the server succeeds')
  assert.equal(localPrimaryModel({ ...local, apiKey: 'synthetic-unsaved-value' }).apiKey, '')
})

for (const change of [{ id: 0 }, { hasApiKey: false }, { credentialStatus: 'Missing' }, { credentialStatus: 'Unreadable' }, { credentialStatus: 'Unknown' }]) {
  test(`unconfigured local activation stays blocked: ${JSON.stringify(change)}`, () => {
    assert.equal(canMakeAiModelActive({ ...local, ...change }), false)
    assert.equal(localPrimaryModel({ ...local, ...change }), null)
  })
}

test('cloud availability/activation path is unchanged', () => {
  for (const providerCode of ['Gemini', 'Groq', 'OpenAI', 'OpenAICompatible']) {
    const row = { ...local, providerCode }
    assert.equal(canMakeAiModelActive(row), false)
    assert.equal(canMakeAiModelActive({ ...row, isActive: true, enableAiScoring: true }), true)
    assert.equal(localPrimaryModel(row), null)
  }
})

test('local endpoint is hidden in the saved list but retained for connection editing', () => {
  assert.equal(aiModelEndpointLabel(local), '')
  assert.equal(local.endpointUrl, 'https://synthetic.example/chat')
  assert.equal(aiModelEndpointLabel({ ...local, providerCode: 'OpenAICompatible' }), local.endpointUrl)
  assert.match(source, /\{aiModelEndpointLabel\(row\) &&/)
  assert.doesNotMatch(source, /\{row\.endpointUrl\}/)
  assert.match(source, /value=\{editor\.endpointUrl\}/)
})

let activate
const visit = node => {
  if (ts.isVariableDeclaration(node) && node.name.getText(file) === 'activate') activate = node.initializer.getText(file)
  ts.forEachChild(node, visit)
}
visit(file)
const harness = (saveOk = true) => {
  const calls = [], editor = { ...local, modelName: 'unsaved edit stays' }
  const scope = vm.createContext({
    canMakeAiModelActive, localPrimaryModel, activatingId: 0,
    setActivatingId: value => calls.push(['busy', value]),
    saveAiIntegration: async request => { calls.push(['save', request]); return { ok: saveOk, data: saveOk ? request : null } },
    activateAiIntegration: async id => { calls.push(['activate', id]); return { ok: true, data: { models: [] } } },
    load: async () => calls.push(['load']), setPool: value => calls.push(['pool', value]),
    setEditor: updater => calls.push(['editor', updater(editor)]),
  })
  vm.runInContext(ts.transpileModule(`globalThis.run = ${activate}`, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, scope)
  return { calls, run: row => scope.run(row), scope }
}

test('local confirmation uses one audited atomic save, refreshes, and preserves unsaved editor fields', async () => {
  const { calls, run } = harness()
  await run(local)
  assert.deepEqual(calls.map(call => call[0]), ['busy', 'save', 'load', 'editor', 'busy'])
  assert.equal(calls[1][1].isPrimary, true)
  assert.equal(calls[3][1].isActive, true)
  assert.equal(calls[3][1].modelName, 'unsaved edit stays')
  assert.equal(calls.at(-1)[1], 0)
  assert.match(source, /okText="Enable & make active"/)
  assert.match(source, /onConfirm=\{\(\) => activate\(row\)\}/)
  assert.match(source, /affects every API using this database/)
})

test('failed local save never switches providers or optimistically marks active', async () => {
  const { calls, run } = harness(false)
  await run(local)
  assert.deepEqual(calls.map(call => call[0]), ['busy', 'save', 'busy'])
})

test('already enabled cloud models still use the existing activate endpoint', async () => {
  const { calls, run } = harness()
  await run({ ...local, providerCode: 'Gemini', isActive: true, enableAiScoring: true })
  assert.deepEqual(calls.map(call => call[0]), ['busy', 'activate', 'pool', 'busy'])
})

test('busy or credential-blocked activation sends no request', async () => {
  const { calls, run, scope } = harness()
  await run({ ...local, credentialStatus: 'Unreadable' })
  scope.activatingId = 6
  await run(local)
  assert.deepEqual(calls, [])
})
