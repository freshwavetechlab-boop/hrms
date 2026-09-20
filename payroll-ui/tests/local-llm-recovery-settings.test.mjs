import { readFileSync } from 'node:fs'
import test from 'node:test'
import assert from 'node:assert/strict'
import ts from 'typescript'
import vm from 'node:vm'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const ui = read('../src/components/LocalLlmRecoverySettingsModal.tsx')
const store = read('../../Payroll.API/Services/LocalLlmRecoverySettingsStore.cs')
const runtime = read('../src/components/LocalLlmRuntimeControl.tsx')

test('recovery settings editor is global-only and never uses browser credential storage', () => {
  assert.ok(runtime.indexOf('if (!allowed) return null') < runtime.indexOf('<LocalLlmRecoverySettingsModal'))
  assert.match(ui, /Input.Password autoComplete="new-password"/)
  assert.doesNotMatch(ui, /localStorage|sessionStorage|import.meta.env|console\./)
  assert.match(ui, /Saving does not start the LLM/)
  assert.match(ui, /same database and integration encryption configuration/)
  assert.doesNotMatch(ui, /start-runtime|activateAiIntegration|schtasks/)
})

test('server safe view omits credentials and stores settings with an atomic audit', () => {
  assert.match(store, /\[JsonIgnore\] public string ApiKey/)
  assert.match(store, /protector.ProtectRecovery\(request.ApiKey\)/)
  assert.match(store, /WHERE client_id=0 AND ModuleCode=@Code FOR UPDATE/)
  assert.match(store, /INSERT INTO auditlogs/)
  assert.match(store, /await tx.CommitAsync/)
  assert.doesNotMatch(store, /CREATE TABLE|ALTER TABLE/)
})

const tree = ts.createSourceFile('Settings.tsx', ui, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
let saveSource
function find(node) {
  if (ts.isVariableDeclaration(node) && node.name.getText(tree) === 'save') saveSource = node.initializer.getText(tree)
  ts.forEachChild(node, find)
}
find(tree)
function harness({ failure = false, invalid = false, settings = { version: 'v1' } } = {}) {
  const calls = []
  const context = vm.createContext({ settings, saving: false, modelId: 6, endpointUrl: 'https://example.invalid/inference',
    form: { validateFields: async () => { if (invalid) throw new Error('Invalid'); return { enabled: true,
      controlEndpointUrl: ' https://example.invalid/control ', apiKey: ' a'.trim() } },
      setFieldsValue: value => calls.push(['fields', value]) },
    setSaving: value => calls.push(['saving', value]), setError: value => calls.push(['error', value]),
    putJson: async (...args) => { calls.push(['put', ...args]); return { ok: !failure, error: 'Controlled error' } },
    onSaved: () => calls.push(['saved']), onClose: () => calls.push(['close']),
  })
  vm.runInContext(ts.transpileModule(`globalThis.save = ${saveSource}`, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, context)
  return { calls, save: () => context.save() }
}

test('save uses bound inference endpoint and expected version, clears key and refreshes parent', async () => {
  const { calls, save } = harness()
  await save()
  const sent = calls.filter(call => call[0] === 'put')
  assert.equal(sent.length, 1)
  assert.equal(sent[0][1], '/api/integrations/ai/6/recovery-settings')
  assert.equal(sent[0][2].inferenceEndpointUrl, 'https://example.invalid/inference')
  assert.equal(sent[0][2].controlEndpointUrl, 'https://example.invalid/control')
  assert.equal(sent[0][2].version, 'v1')
  assert.equal(calls.find(call => call[0] === 'fields')[1].apiKey, '')
  assert.deepEqual(calls.slice(-2), [['saved'], ['close']])
})

test('failed save clears key, shows safe error and does not claim saved or close', async () => {
  const { calls, save } = harness({ failure: true })
  await save()
  assert.equal(calls.find(call => call[0] === 'fields')[1].apiKey, '')
  assert.deepEqual(calls.at(-1), ['error', 'Controlled error'])
  assert.equal(calls.some(call => ['saved', 'close'].includes(call[0])), false)
})

test('unloaded or invalid settings cannot dispatch save', async () => {
  for (const options of [{ settings: null }, { invalid: true }]) {
    const { calls, save } = harness(options)
    await save()
    assert.equal(calls.length, 0)
  }
})
