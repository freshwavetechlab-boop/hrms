import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/RecruitmentJobDescriptionManager.tsx', import.meta.url), 'utf8')
const ast = ts.createSourceFile('manager.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const functions = []
function visit(node) {
  if (ts.isFunctionDeclaration(node) && ['loadVersions', 'deleteVersion'].includes(node.name?.text)) functions.push(node.getText(ast))
  ts.forEachChild(node, visit)
}
visit(ast)
assert.equal(functions.length, 2)
const code = ts.transpileModule(functions.join('\n'), { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText

function harness({ historyOnly = false, blocked = false, draftId = 7 } = {}) {
  const state = { draft: draftId ? { id: draftId, title: 'Unsaved title' } : null, versions: [{ id: 7 }], deletes: [], reads: 0, notifications: 0, error: '' }
  const context = vm.createContext({
    historyOnly, saving: false, sourceParsing: false, deletingId: null,
    draft: state.draft, requisitionId: 42, requisitionsRef: { current: [{ id: 42 }] },
    embeddedAutoSaveTimer: { current: 123 }, window: { clearTimeout() {} },
    versionLoad: { current: 0 }, editableStatuses: new Set(['Draft', 'Sent Back']),
    setLoading() {}, clearSourceDraft() {},
    setDeletingId(id) { context.deletingId = id },
    setDeleteError(error) { state.error = error },
    setDraftSnapshot(draft) { context.draft = state.draft = draft },
    setVersions(versions) { state.versions = versions },
    onDeleted() { state.notifications++ },
    async deleteRecruitmentJobDescription(id) { state.deletes.push(id); return { ok: !blocked, error: blocked ? 'This job description has ATS scoring evidence and must be retained for audit.' : '' } },
    async getRecruitmentJobDescriptions(id) { assert.equal(id, 42); state.reads++; return [] },
    async getRecruitmentJobDescription() { throw Error('History-only view must not load a draft') },
    blankDescription() { throw Error('Deletion must not create another JD') },
    message: { error(value) { throw Error(value) } },
  })
  vm.runInContext(code, context)
  return { state, context }
}

test('opening linked version history never creates or autosaves a blank JD', async () => {
  const { state, context } = harness({ historyOnly: true, draftId: null })
  await context.loadVersions(42)
  assert.equal(state.draft, null)
  assert.deepEqual(state.deletes, [])
  assert.equal(state.reads, 1)
})

test('deleting the last selected JD leaves an empty editor instead of a new draft', async () => {
  const { state, context } = harness()
  await context.deleteVersion(7)
  assert.deepEqual(state.deletes, [7])
  assert.equal(state.draft, null)
  assert.deepEqual(state.versions, [])
  assert.equal(state.notifications, 1)
  assert.equal(context.deletingId, null)
})

test('ATS audit blocker keeps the JD and shows the server reason', async () => {
  const { state, context } = harness({ blocked: true })
  await context.deleteVersion(7)
  assert.match(state.error, /ATS scoring evidence/)
  assert.equal(state.draft.id, 7)
  assert.equal(state.versions.length, 1)
  assert.equal(state.reads, 0)
  assert.equal(state.notifications, 0)
})

test('deleting another version preserves current edits; saving blocks deletion', async () => {
  const { state, context } = harness({ draftId: 9 })
  await context.deleteVersion(7)
  assert.deepEqual(state.draft, { id: 9, title: 'Unsaved title' })
  context.saving = true
  await context.deleteVersion(9)
  assert.deepEqual(state.deletes, [7])
})
