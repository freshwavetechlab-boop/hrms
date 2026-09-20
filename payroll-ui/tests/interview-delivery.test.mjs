import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const read = file => readFileSync(new URL(file, import.meta.url), 'utf8')
const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(read('../src/services/interviewDelivery.ts'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { frevoInterviewDestination, isFrevoInterview, externalInterviewDestination } = context.exports

test('existing internal schedules keep their persisted marker without a schema migration', () => {
  assert.equal(frevoInterviewDestination, 'Internal HRMS interview')
  assert.equal(isFrevoInterview('Virtual', frevoInterviewDestination), true)
  assert.equal(isFrevoInterview('Face-to-Face', frevoInterviewDestination), false)
  assert.equal(externalInterviewDestination(frevoInterviewDestination), '')
})

test('external links/contact details are preserved exactly when Frevo delivery is off', () => {
  for (const destination of ['https://meet.google.com/abc-defg-hij', 'https://teams.microsoft.com/meeting', 'https://zoom.us/j/123', 'Meeting room 2', '']) {
    assert.equal(externalInterviewDestination(destination), destination)
    assert.equal(isFrevoInterview('Virtual', destination), false)
  }
})

test('schedule exposes an explicit switch; external mode retains the original invite path', () => {
  const editor = read('../src/components/RecruitmentInterviewEditor.tsx')
  assert.match(editor, /<Switch aria-label="Interview with Frevo One"/)
  assert.match(editor, /disabled=\{!canSchedule \|\| \(!internalAvailable && !internalDelivery\)\}/)
  assert.match(editor, /internalDelivery \? internalAvailable : !sendInvite/)
  assert.match(editor, /locationOrLink: internalDelivery \? frevoInterviewDestination : draft\.locationOrLink/)
  assert.match(editor, /if \(!internalDelivery && sendInvite/)
  assert.match(editor, /!internalDelivery && <Form\.Item label=/)
})

test('voice controls fail closed without explicit API capability; ordinary call controls remain', () => {
  const page = read('../src/pages/InternalInterviewPage.tsx')
  assert.match(page, /view\.speechEnabled === true[^\n]*<InterviewVoiceControls/)
  assert.match(page, /grant && <InterviewMediaRoom/)
  assert.match(page, /consent to saving my written answers/)
  const endpoints = read('../../Payroll.API/Services/InternalInterviewEndpoints.cs')
  assert.equal([...endpoints.matchAll(/speech\.RequireEnabled\(\)/g)].length, 2)
  const speech = read('../../Payroll.API/Services/InternalInterviewSpeechService.cs')
  assert.match(speech, /GetValue\("InternalInterviews:SpeechEnabled", false\)/)
  assert.match(speech, /SendAsync[^]*?RequireEnabled\(\);/)
})

test('default media deployment does not start speech or require its secrets/models', () => {
  const compose = read('../../deploy/internal-interviews/compose.example.yml')
  assert.match(compose, /speech:\s+profiles: \[speech\]/)
  assert.doesNotMatch(compose, /SPEECH_API_KEY:\?|INTERVIEW_SPEECH_MODELS_DIR:\?/)
})
