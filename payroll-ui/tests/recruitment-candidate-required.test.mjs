import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/recruitmentCandidateRequirements.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { candidateDraftRequirements: requirements, missingCandidateDraftFields: missing } = context.exports
const field = (code, extra = {}) => ({ stableFieldCode: code, semanticCodes: [code], label: code, fieldTypeCode: 'TEXT', isActive: true, isRequired: true, ...extra })
const form = (...fields) => ({ sections: [{ fields }] })
const draft = { firstName: 'Asha', lastName: '', email: '', phone: '9876543210', currentTitle: '', skills: [], certifications: [], currentCtc: null, expectedCtc: null, noticePeriodDays: null, totalExperienceMonths: 0 }

test('configured required email cannot be satisfied by phone, in single or bulk review', () => {
  const rules = requirements(form(field('EMAIL')), false)
  for (const row of [draft, { ...draft, firstName: 'Ravi' }]) assert.deepEqual([...missing(row, rules.fields, true)], ['EMAIL'])
  assert.equal(missing({ ...draft, email: 'asha@example.test' }, rules.fields, true).length, 0)
})

test('optional and inactive fields do not inherit a previous job requirement', () => {
  const rules = requirements(form(field('EMAIL', { isRequired: false }), field('LAST_NAME', { isActive: false })), false)
  assert.equal(rules.fields.has('email'), false)
  assert.equal(rules.fields.has('lastName'), false)
  assert.equal(missing(draft, rules.fields, true).length, 0)
})

test('required salary and notice distinguish missing from explicit zero', () => {
  const rules = requirements(form(field('EXPECTED_CTC', { fieldTypeCode: 'NUMBER' }), field('NOTICE_PERIOD_DAYS', { fieldTypeCode: 'NUMBER' })), false)
  assert.deepEqual([...missing(draft, rules.fields, true)], ['EXPECTED_CTC', 'NOTICE_PERIOD_DAYS'])
  assert.equal(missing({ ...draft, expectedCtc: 0, noticePeriodDays: 0 }, rules.fields, true).length, 0)
})

test('existing ATS and contact requirements remain conditional and match markers', () => {
  const rules = requirements(null, true)
  assert.equal(rules.fields.has('currentTitle'), true)
  assert.equal(rules.fields.has('skills'), true)
  assert.deepEqual([...missing({ ...draft, phone: '' }, rules.fields, true)], ['Current designation', 'At least one skill', 'Email or phone'])
  assert.equal(requirements(null, false).fields.has('currentTitle'), false)
  assert.equal(missing({ ...draft, phone: '' }, requirements(null, false).fields, false).length, 0)
})

test('all editable profile fields use stable mappings instead of display labels', () => {
  const rules = requirements(form(field('LAST_NAME', { label: 'Family name' }), field('CURRENT_LOCATION'), field('CURRENT_COMPANY'), field('HIGHEST_QUALIFICATION'), field('CERTIFICATIONS')), false)
  assert.equal(rules.fields.get('lastName'), 'Family name')
  assert.deepEqual([...missing(draft, rules.fields, true)], ['Family name', 'CURRENT_LOCATION', 'CURRENT_COMPANY', 'HIGHEST_QUALIFICATION', 'CERTIFICATIONS'])
})

test('custom fields and documents remain explicit full-form requirements', () => {
  const rules = requirements(form(field('RESUME', { fieldTypeCode: 'UPLOAD' }), field('IDENTITY_PROOF', { fieldTypeCode: 'UPLOAD' }), field('FATHERS_NAME'), field('EMAIL', { fieldTypeCode: 'SEARCH_SELECT' })), false)
  assert.deepEqual([...rules.additional], ['IDENTITY_PROOF', 'FATHERS_NAME', 'EMAIL'])
  assert.equal(rules.fields.has('email'), false)
})
