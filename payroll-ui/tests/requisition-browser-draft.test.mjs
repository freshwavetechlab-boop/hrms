import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/RecruitmentRequisitionManager.tsx', import.meta.url), 'utf8')
const functions = source.slice(source.indexOf('  function newBrowserDraftKey()'), source.indexOf('  function textRules('))
  + source.slice(source.indexOf('  async function openNew('), source.indexOf('  function openRequest('))

function harness() {
  const storage = new Map()
  const state = {}
  let values = {}
  const context = vm.createContext({
    window: { location: { search: '' }, localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key),
    } }, URLSearchParams,
    session: { user: { id: 7, employeeId: 8 } }, initialClientId: 42, clientFilter: 42,
    initialWorkOrderId: 0, initialWorkOrderLineId: 0,
    activeBrowserDraftKey: { current: '' }, sourceFileRef: { current: null }, sourceParseRef: { current: null },
    targetJoiningManual: { current: false }, readOnly: false, approvedEdit: false,
    saving: false, sourceParsing: false, clients: [], employees: [], rows: [], canDelete: false,
    editableStatuses: new Set(['Draft', 'Sent Back']),
    form: { getFieldsValue: () => values, resetFields: () => { values = {} } },
    blankRequest: clientId => ({ id: 0, clientId, positionTitle: '', requestDate: '2026-09-22' }),
    applyDraft: draft => { values = draft }, applyPipelineTarget: async () => {},
    ...Object.fromEntries(['SourceParse', 'SourceFile', 'AutoSavedAt', 'AutoSaveState', 'SourcePrefillWarning', 'AdvancedOpen', 'JdOpen', 'ReadOnly', 'ActiveRequest', 'AtsSkillWeight', 'SourceUploadProgress', 'DialogOpen'].map(key => [`set${key}`, value => { state[key] = value }])),
  })
  vm.runInContext(ts.transpileModule(functions, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, context)
  return { context, state, storage, values: () => values }
}

test('closing and reopening New restores the parsed draft and keeps the selected file', async () => {
  const h = harness()
  await h.context.openNew()
  const file = { name: 'architect.pdf' }
  h.context.sourceFileRef.current = file
  h.context.updateSourceParse({ status: 'Parsed', detectedFields: ['requiredSkills'], reviewFields: ['Department'] })
  h.context.persistBrowserDraft({ ...h.values(), positionTitle: 'Architect', requiredSkills: 'Java', sourceParsedJson: '{"skills":["Java"]}' })
  await h.context.openNew()
  assert.equal(h.values().positionTitle, 'Architect')
  assert.equal(h.values().requiredSkills, 'Java')
  assert.equal(h.context.sourceFileRef.current, file)
  assert.equal(h.state.SourceParse.status, 'Parsed')
  assert.equal(h.state.AutoSaveState, 'local')
  assert.ok(h.state.JdOpen.includes('job-description'))
})

test('refresh restores the latest parse review and warns when the file must be reselected', async () => {
  const h = harness()
  await h.context.openNew()
  h.context.sourceFileRef.current = { name: 'architect.pdf' }
  // Persist immediately, before React would render state updates.
  h.context.updateSourceParse({ status: 'NeedsReview', warnings: ['Check qualification'], reviewFields: ['Qualification'] })
  h.context.persistBrowserDraft({ ...h.values(), qualification: 'B.Tech', sourceParsedJson: '{}' })
  h.context.sourceFileRef.current = null
  h.context.activeBrowserDraftKey.current = ''
  await h.context.openNew()
  assert.equal(h.values().qualification, 'B.Tech')
  assert.equal(h.state.SourceParse.warnings[0], 'Check qualification')
  assert.match(h.state.SourcePrefillWarning, /Re-select the source document/)
})

test('only explicit Clear draft discards values; other request drafts are untouched', async () => {
  const h = harness()
  await h.context.openNew()
  h.context.persistBrowserDraft({ ...h.values(), positionTitle: 'Architect' })
  h.storage.set('frevo:hiring-request:request:7:99', 'other request')
  await h.context.openNew(true)
  assert.equal(h.values().positionTitle, '')
  assert.equal(h.storage.get('frevo:hiring-request:request:7:99'), 'other request')
  assert.equal(h.storage.has(h.context.newBrowserDraftKey()), false)
})

test('a save or parse in flight cannot reset the draft', async () => {
  const h = harness()
  await h.context.openNew()
  h.context.persistBrowserDraft({ ...h.values(), positionTitle: 'Architect' })
  for (const flag of ['saving', 'sourceParsing']) {
    h.context[flag] = true
    await h.context.openNew(true)
    assert.ok(h.storage.has(h.context.newBrowserDraftKey()))
    h.context[flag] = false
  }
})
