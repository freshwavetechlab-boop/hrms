import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/RecruitmentInterviewEditor.tsx', import.meta.url), 'utf8')
const ast = ts.createSourceFile('editor.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const expressions = {}
function walk(node) {
  if (ts.isVariableDeclaration(node) && ['saveApprover', 'decisionReady'].includes(node.name.getText(ast))) expressions[node.name.getText(ast)] = node.initializer.getText(ast)
  ts.forEachChild(node, walk)
}
walk(ast)
const run = async (overrides = {}) => {
  const writes = [], busy = [], saved = []
  const original = { id: 4, applicationId: 8, decisionApproverUserId: null, status: 'Scheduled', result: 'Pending', panelUserIds: [10], locationOrLink: 'original meeting' }
  const context = vm.createContext({ savedInterview: original, draft: { decisionApproverUserId: 12, status: 'Completed', result: 'Selected', panelUserIds: [99] },
    canSchedule: true, approverChanged: true, saving: false, savingApprover: false,
    setSavingApprover: value => busy.push(value), parsePanelIds: row => row.panelUserIds,
    saveInterview: async payload => { writes.push(payload); return { ok: true, data: { ...payload, canRecordDecision: true } } },
    setSavedInterview: value => saved.push(value), window: { dispatchEvent() {} }, Event: class {}, onSaved: async () => {}, ...overrides })
  await vm.runInContext(ts.transpileModule(`(${expressions.saveApprover})()`, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText, context)
  return { writes, busy, saved, original }
}
test('saving approver preserves persisted schedule and does not submit the draft final decision', async () => {
  const { writes, saved, busy, original } = await run()
  assert.equal(writes.length, 1)
  assert.equal(writes[0].decisionApproverUserId, 12)
  assert.equal(writes[0].status, 'Scheduled')
  assert.equal(writes[0].result, 'Pending')
  assert.deepEqual(writes[0].panelUserIds, [10])
  assert.equal(original.decisionApproverUserId, null)
  assert.equal(saved[0].canRecordDecision, true)
  assert.deepEqual(busy, [true, false])
})
test('panel-only users and completed interviews cannot save an approver', async () => {
  assert.equal((await run({ canSchedule: false })).writes.length, 0)
  assert.equal((await run({ savedInterview: { id: 4, status: 'Completed' } })).writes.length, 0)
})
test('failed assignment save does not mark the draft as saved', async () => {
  const result = await run({ saveInterview: async () => ({ ok: false }) })
  assert.equal(result.saved.length, 0)
  assert.deepEqual(result.busy, [true, false])
})
test('decision requires both a persisted approver and fresh server permission', () => {
  for (const [changed, permitted, expected] of [[true, true, false], [false, false, false], [false, true, true]]) {
    assert.equal(vm.runInNewContext(expressions.decisionReady, { recordingDecision: true, approverChanged: changed, canRecordDecision: permitted }), expected)
  }
})

test('Select and Reject preserve the saved interview and prefill only the editor draft', () => {
  const workspace = readFileSync(new URL('../src/components/RecruitmentTalentWorkspace.tsx', import.meta.url), 'utf8')
  for (const decision of ['Selected', 'Rejected']) {
    assert.ok(workspace.includes(`interview: row, initialDecision: '${decision}'`))
  }
  assert.doesNotMatch(workspace, /interview: \{ \.\.\.row, status: 'Completed'/)
  const start = source.indexOf('    const next = initialSchedule(interview, initialApplicationId)')
  const end = source.indexOf('    if (!interview?.id && initialStartNow)', start)
  for (const initialDecision of ['Selected', 'Rejected']) {
    const interview = { id: 4, status: 'Scheduled', result: 'Pending', canRecordDecision: true }
    const context = vm.createContext({ interview, initialDecision, initialApplicationId: 8, initialSchedule: row => ({ ...row }) })
    vm.runInContext(source.slice(start, end) + '\nglobalThis.draftResult = next', context)
    assert.equal(context.draftResult.status, 'Completed')
    assert.equal(context.draftResult.result, initialDecision)
    assert.equal(interview.status, 'Scheduled')
    assert.equal(interview.result, 'Pending')
  }
})
