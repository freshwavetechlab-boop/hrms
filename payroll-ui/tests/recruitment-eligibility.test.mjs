import assert from 'node:assert/strict'
import fs from 'node:fs'
import test from 'node:test'
import ts from 'typescript'

const source = fs.readFileSync(new URL('../src/utils/recruitmentEligibility.ts', import.meta.url), 'utf8')
const { recruitmentEligibility } = await import(`data:text/javascript;base64,${Buffer.from(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText).toString('base64')}`)

test('A high score and an override cannot hide a mandatory eligibility failure', () => {
  for (const score of [0, 75, 99, 100]) {
    const result = recruitmentEligibility({ eligibilityStatus: 'Ineligible', atsScore: score, eligibilityReasons: ['Minimum experience is 10 years; resume shows 4 years.'] })
    assert.equal(result.label, 'Not eligible')
    assert.equal(result.showScore, false)
    assert.match(result.reason, /10 years/)
  }
})

test('Unverified requirements show a clearly provisional score without making the candidate eligible', () => {
  const result = recruitmentEligibility({ eligibilityStatus: 'NeedsReview', totalScore: 91 })
  assert.equal(result.label, 'Needs review')
  assert.equal(result.showScore, true)
  assert.equal(result.provisional, true)
  assert.equal(result.isEligible, false)
})

test('Eligible and ATS cutoff are separate: low-scoring candidates can meet mandatory criteria', () => {
  const result = recruitmentEligibility({ eligibilityStatus: 'Eligible', atsScore: 45 })
  assert.equal(result.label, 'Eligible')
  assert.equal(result.showScore, true)
})

test('An explicit pending assessment never displays a stale weighted score', () => {
  const result = recruitmentEligibility({ eligibilityStatus: 'NotAssessed', atsScore: 85 })
  assert.equal(result.label, 'Not assessed')
  assert.equal(result.showScore, false)
})

test('Legacy API statuses hide ineligible scores and mark review scores as provisional', () => {
  assert.equal(recruitmentEligibility({ scoreStatus: 'Ineligible', atsScore: 90 }).showScore, false)
  assert.equal(recruitmentEligibility({ scoreStatus: 'NeedsReview', totalScore: 92 }).provisional, true)
  assert.equal(recruitmentEligibility({ scoreStatus: 'Completed', atsScore: 72 }).label, 'Eligible')
})

test('Unscored records remain distinguishable from a valid zero score', () => {
  assert.equal(recruitmentEligibility({ atsScore: null }).status, 'pending')
  assert.equal(recruitmentEligibility({ atsScore: 0 }).status, 'eligible')
})
