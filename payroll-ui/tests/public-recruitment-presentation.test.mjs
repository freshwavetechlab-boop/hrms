import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const compiled = ts.transpileModule(readFileSync(new URL('../src/utils/publicRecruitmentPresentation.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const exports = {}
new Function('exports', compiled)(exports)
const { publicRecruitmentJobView, publicRecruitmentEmployer } = exports

test('public view removes present and future client identity while preserving source data, eligibility and exact routing', () => {
  for (const [clientName, code, jobLocation, summary] of [
    ['ICMR', 'ICMR', 'ICMR-NITVAR - Pune', 'ICMR-NITVAR: Graduate + 3 years OR PG. Monthly salary Rs. 36,400.'],
    ['Unique Identification Authority of India (UIDAI)', 'UIDAI', 'UIDAI - Bengaluru', 'UIDAI requires 3 years of relevant experience.'],
    ['Future Research (FR)', 'FUTURE', 'Future Research (FR) - Delhi', 'Future Research (FR) or FUTURE project staff.'],
    ['A+B [Research]', 'ABR', 'A+B [Research] - Mumbai', 'A+B [Research] requires a degree.'],
  ]) {
    const source = {
      clientName, positionCode: `POS-${code}-000001`, publicSlug: '0123456789abcdef', publicUrl: 'https://jobs.example.invalid/careers/0123456789abcdef',
      publicTitle: `${clientName} Researcher`, positionTitle: `${code} Researcher`, jobLocation, summary, salaryMin: 36400, remainingPositions: 2,
      responsibilities: [`Coordinate with ${clientName}.`], qualifications: [{ id: 23, qualificationName: 'Graduate + 3 years OR PG', isMandatory: true }],
      applicationForm: { id: 81, sections: [{ id: 82, sectionLabel: `${code} application`, fields: [{ id: 83, label: `Experience for ${code}`, stableFieldCode: `${code}_EXPERIENCE`, semanticCodes: ['TOTAL_EXPERIENCE_YEARS'], options: [{ id: 84, optionLabel: `${code} research`, optionValue: `${code}_RESEARCH` }] }] }] },
    }
    const before = structuredClone(source)
    const result = publicRecruitmentJobView(source)
    assert.equal(result.clientName, publicRecruitmentEmployer)
    assert.equal(result.positionCode, 'JOB-0123456789ABCDEF')
    assert.equal(result.publicUrl, source.publicUrl)
    assert.equal(result.publicSlug, source.publicSlug)
    assert.equal(result.salaryMin, 36400)
    assert.equal(result.remainingPositions, 2)
    assert.deepEqual(result.qualifications, source.qualifications)
    assert.equal(result.applicationForm.sections[0].fields[0].id, 83)
    assert.equal(result.applicationForm.sections[0].fields[0].stableFieldCode, `${code}_EXPERIENCE`)
    assert.equal(result.applicationForm.sections[0].fields[0].options[0].optionValue, `${code}_RESEARCH`)
    for (const visible of [result.publicTitle, result.positionTitle, result.summary, result.jobLocation, ...result.responsibilities, result.applicationForm.sections[0].sectionLabel, result.applicationForm.sections[0].fields[0].label, result.applicationForm.sections[0].fields[0].options[0].optionLabel]) {
      assert.equal(visible.includes(clientName), false, visible)
      assert.equal(new RegExp(`\\b${code}\\b`, 'i').test(visible), false, visible)
    }
    assert.deepEqual(source, before)
  }
})

test('public site labels retain geography and replacements do not damage unrelated words', () => {
  const job = { clientName: 'ICMR', positionCode: 'POS-ICMR-000001', publicSlug: 'opaque' }
  assert.equal(publicRecruitmentJobView({ ...job, jobLocation: 'ICMR-NITVAR - Goa survey sites' }).jobLocation, 'Goa survey sites')
  assert.equal(publicRecruitmentJobView({ ...job, jobLocation: 'ICMR - Pune' }).jobLocation, 'Pune')
  assert.equal(publicRecruitmentJobView({ ...job, jobLocation: 'ICMR' }).jobLocation, 'Location shared during recruitment')
  assert.equal(publicRecruitmentJobView({ ...job, summary: 'ICMR study. ICMRology is a separate word.' }).summary, 'GA Digital study. ICMRology is a separate word.')
})
