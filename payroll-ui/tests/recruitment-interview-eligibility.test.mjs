import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const eligibilityContext = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/utils/recruitmentEligibility.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, eligibilityContext)
const context = vm.createContext({ exports: {}, require: name => {
  assert.equal(name, '../utils/recruitmentEligibility')
  return eligibilityContext.exports
} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/recruitmentInterviewEligibility.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { interviewReady, hasInterviewEligibility, panelRecommendationLabel } = context.exports
const ready = { id: 77, applicationType: 'Application', isInterviewReady: true, atsScore: 77.1, atsShortlistThreshold: 60, scoreStatus: 'Scored' }

test('HR review and completed rounds do not reappear because of a stage-name regex', () => {
  assert.equal(interviewReady({ ...ready, isInterviewReady: false, currentStage: 'Selection & HR Review' }, []), false)
  assert.equal(interviewReady({ ...ready, isInterviewReady: false, currentStage: 'Interview / Panel Assessment' }, []), false)
  assert.equal(interviewReady({ ...ready, isInterviewReady: undefined }, []), false)
})
test('configured new round stays eligible; scheduled or rescheduled candidates do not', () => {
  assert.equal(interviewReady(ready, [{ applicationId: 77, status: 'Completed' }]), true)
  for (const status of ['Scheduled', 'Rescheduled'])
    assert.equal(interviewReady(ready, [{ applicationId: 77, status }]), false)
  assert.equal(interviewReady(ready, [{ applicationId: 999, status: 'Scheduled' }]), true)
})
test('a passing score cannot bypass ineligible, review or unassessed evidence', () => {
  for (const changes of [{ atsScore: null }, { atsScore: 40 }, { applicationType: 'TalentPoolMatch' }])
    assert.equal(interviewReady({ ...ready, ...changes }, []), false)
  for (const scoreStatus of ['NeedsReview', 'Ineligible'])
    assert.equal(interviewReady({ ...ready, scoreStatus }, []), false)
  for (const eligibilityStatus of ['Ineligible', 'NeedsReview', 'NotAssessed'])
    assert.equal(interviewReady({ ...ready, eligibilityStatus, atsScore: 99 }, []), false)
  for (const scoreStatus of ['Scored', 'CompletedWithAiFallback']) {
    assert.equal(interviewReady({ ...ready, atsScore: 75, atsShortlistThreshold: 75, scoreStatus }, []), true)
    assert.equal(interviewReady({ ...ready, atsScore: 74.99, atsShortlistThreshold: 75, scoreStatus }, []), false)
    assert.equal(interviewReady({ ...ready, scoreStatus, isInterviewReady: false }, []), false)
    assert.equal(interviewReady({ ...ready, scoreStatus }, [{ applicationId: ready.id, status: 'Scheduled' }]), false)
  }
})

test('single-candidate scheduling uses the same eligibility and score rule without changing history', () => {
  assert.equal(hasInterviewEligibility(ready), true)
  for (const eligibilityStatus of ['Ineligible', 'NeedsReview', 'NotAssessed'])
    assert.equal(hasInterviewEligibility({ ...ready, eligibilityStatus, atsScore: 99 }), false)
  assert.equal(hasInterviewEligibility({ ...ready, atsScore: 40 }), false)
})
test('panel recommendation is not presented as final hire or rejection', () => {
  assert.equal(panelRecommendationLabel('Hire'), 'Recommend selection')
  assert.equal(panelRecommendationLabel('No Hire'), 'Recommend rejection')
  assert.equal(panelRecommendationLabel('Strong Hire'), 'Strongly recommend selection')
})
test('internal-stage link is omitted and MoM navigation reuses signing workspace', () => {
  const file = path => readFileSync(new URL(path, import.meta.url), 'utf8')
  assert.match(file('../src/components/RecruitmentCandidateActionManager.tsx'), /if \(!stageAction.enabled\) return null/)
  assert.match(file('../src/SettingsApp.tsx'), /MoM & Negotiation/)
  assert.match(file('../src/pages/RecruitmentPage.tsx'), /RecruitmentWorkOrderWorkspace[^>]*postInterview/)
  const editor = file('../src/components/RecruitmentInterviewEditor.tsx')
  assert.match(editor, /recordingDecision \|\|/)
  assert.match(editor, /interview.result === 'Pending' \? 'Selected' : interview.result/)
})
