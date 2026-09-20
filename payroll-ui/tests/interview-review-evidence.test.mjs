import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/interviewReviewEvidence.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { interviewAiErrors, readInterviewSummary } = context.exports
const event = (id, kind, eventKey, text = '') => ({ id, kind, eventKey, text })

test('retry alone does not hide failed work; durable recovery resolves only its own answer', () => {
  const rows = [event(1, 'ai-error', 'review:7:failed:0'), event(2, 'ai-error', 'review:8:failed:0'), event(3, 'ai-retry', 'retry')]
  assert.equal(interviewAiErrors(rows).active.length, 2)
  rows.push(event(4, 'ai-review', 'review:7'))
  const status = interviewAiErrors(rows)
  assert.equal(status.active[0].eventKey, 'review:8:failed:0'); assert.equal(status.recovered[0].id, 1)
})
test('a combined summary resolves only the summary failure, not individual drafts', () => {
  const status = interviewAiErrors([event(1, 'ai-error', 'review:summary:failed:0'), event(2, 'ai-error', 'review:7:failed:0'), event(3, 'ai-summary', 'review:summary')])
  assert.equal(status.active.length, 1); assert.equal(status.active[0].id, 2); assert.equal(status.recovered[0].id, 1)
})
test('malformed or wrong-shaped summary does not crash the review screen', () => {
  for (const body of ['null', '[]', '{', '{}', '{"questionCount":1,"answerCount":1,"unansweredQuestionEventIds":[],"observations":[null]}'])
    assert.equal(readInterviewSummary([event(1, 'ai-summary', 'review:summary', body)]), undefined)
})
test('summary preserves evidence links and zero-answer metadata without inventing an observation', () => {
  const payload = { questionCount: 1, answerCount: 0, unansweredQuestionEventIds: [7], observations: [] }
  const result = readInterviewSummary([event(1, 'ai-summary', 'review:summary', JSON.stringify(payload))])
  assert.equal(result.answerCount, 0); assert.equal(result.unansweredQuestionEventIds[0], 7); assert.equal(result.observations.length, 0)
})
