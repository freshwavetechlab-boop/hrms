import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/interviewParticipantHistory.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { interviewParticipantHistory } = context.exports
const event = (id, time, eventType = 'participant_joined') => ({ id, kind: 'media-presence', source: 'livekit-webhook', createdAtUtc: '2026-09-20T12:01:00Z',
  text: JSON.stringify({ identity: 'panel-4', participantSessionId: 'PA_test', role: 'Panel user', eventType, occurredAtUtc: time }) })
test('late delivery sorts by original event time, preserving receipt time and connection identity', () => {
  const rows = interviewParticipantHistory([event(1, '2026-09-20T12:00:30Z', 'participant_left'), event(2, '2026-09-20T12:00:00Z')])
  assert.equal(rows[0].id, 2); assert.equal(rows[1].eventType, 'participant_left'); assert.equal(rows[0].receivedAtUtc, '2026-09-20T12:01:00Z')
  assert.equal(rows[0].participantSessionId, 'PA_test')
})
test('browser claims, assigned-panel notes and malformed metadata do not become attendance history', () => {
  const valid = event(1, '2026-09-20T12:00:00Z')
  assert.equal(interviewParticipantHistory([{ ...valid, source: 'candidate-reported' }, { ...valid, kind: 'note' }, { ...valid, text: 'null' },
    event(2, 'invalid'), event(3, '2026-09-20T12:00:00Z', 'hire')]).length, 0)
})
