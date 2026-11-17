import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const exports = {}
new Function('exports', ts.transpileModule(readFileSync(new URL('../src/utils/salaryTemplateContext.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
const { salaryTemplateClientIds, scopeSalaryTemplates, preferredSalaryTemplate, templateForClient } = exports
const template = (id, clientId, active = true) => ({ id, clientId, name: `Template ${id}`, annualCtc: '600000', active, lines: [{ componentId: '1', value: '0', formula: 'IF(PAYABLE_DAYS>0,BASIC_MONTHLY,0)', proRataOverride: 'false' }] })
const owned = template(11, '10'), otherOwned = template(12, '10'), foreign = template(21, '20'), shared = template(31, ''), multi = template(41, '10:REC;20:Other')

test('legacy client IDs accept separators and labels without duplicate or unsafe IDs', () => {
  assert.deepEqual(salaryTemplateClientIds(' 10:REC;20:Other|10, 30 '), [10, 20, 30])
  assert.deepEqual(salaryTemplateClientIds(10), [10])
  for (const value of ['', undefined, null, '0', '-1', 'Infinity', 'NaN', '1.5', '9007199254740992', 'REC']) assert.deepEqual(salaryTemplateClientIds(value), [])
})

test('client scoping keeps saved object identity/order and never includes another client', () => {
  const rows = [foreign, shared, owned, multi]
  assert.deepEqual(scopeSalaryTemplates(rows, 10), [shared, owned, multi])
  assert.equal(scopeSalaryTemplates(rows, 10)[1], owned)
  assert.deepEqual(rows, [foreign, shared, owned, multi])
  for (const clientId of [0, -1, NaN, 1.5]) assert.deepEqual(scopeSalaryTemplates(rows, clientId), [])
})

test('a malformed client reference is not silently promoted to a global template', () => {
  assert.deepEqual(scopeSalaryTemplates([template(1, 'unexpected'), template(2, '0'), template(3, ' ')], 10).map(row => row.id), [2, 3])
})

test('the only owned template opens automatically, even beside shared or multi-client choices', () => {
  assert.equal(preferredSalaryTemplate([foreign, shared, owned, multi], 10), owned)
  assert.equal(preferredSalaryTemplate([shared, multi], 10), undefined)
  assert.equal(preferredSalaryTemplate([], 10), undefined)
})

test('multiple active owned templates never guess a location or use input order as preference', () => {
  assert.equal(preferredSalaryTemplate([owned, otherOwned], 10), undefined)
  assert.equal(preferredSalaryTemplate([otherOwned, owned], 10), undefined)
  assert.equal(preferredSalaryTemplate([foreign], 10), undefined)
})

test('one active owned template wins over inactive alternatives; inactive ambiguity stays explicit', () => {
  const inactive = template(51, '10', false), inactive2 = template(52, '10', false)
  assert.equal(preferredSalaryTemplate([inactive, owned], 10), owned)
  assert.equal(preferredSalaryTemplate([inactive], 10), inactive)
  assert.equal(preferredSalaryTemplate([inactive, inactive2, shared], 10), undefined)
})

test('remembered choices restore only inside the selected client scope', () => {
  assert.equal(preferredSalaryTemplate([owned, otherOwned, foreign], 10, '12'), otherOwned)
  assert.equal(preferredSalaryTemplate([owned, otherOwned, foreign], 10, foreign.id), undefined)
  assert.equal(preferredSalaryTemplate([owned, foreign], 10, foreign.id), owned)
  assert.equal(preferredSalaryTemplate([shared, multi], 10, String(shared.id)), shared)
  assert.equal(preferredSalaryTemplate([shared, multi], 10, multi.id), multi)
  assert.equal(preferredSalaryTemplate([owned], 0, owned.id), undefined)
})

test('exactly owned templates preserve the saved ID and shared source formulas remain immutable', () => {
  assert.equal(templateForClient(owned, 10), owned)
  for (const source of [shared, multi]) {
    const before = structuredClone(source)
    const draft = templateForClient(source, 10)
    assert.equal(draft.id, 0)
    assert.equal(draft.clientId, '10')
    assert.equal(draft.annualCtc, source.annualCtc)
    assert.deepEqual(draft.lines, source.lines)
    assert.notEqual(draft.lines, source.lines)
    assert.notEqual(draft.lines[0], source.lines[0])
    draft.lines[0].formula = '0'
    assert.deepEqual(source, before)
  }
})

test('foreign and missing-client template conversion is refused instead of reassigning records', () => {
  assert.equal(templateForClient(foreign, 10), undefined)
  assert.equal(templateForClient(template(61, 'invalid'), 10), undefined)
  assert.equal(templateForClient(shared, 0), undefined)
  assert.equal(templateForClient(owned, -1), undefined)
})
