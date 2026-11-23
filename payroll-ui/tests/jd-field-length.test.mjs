import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const exports = {}
const compile = code => ts.transpileModule(code, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
new Function('exports', compile(readFileSync(new URL('../src/utils/jdFieldLength.ts', import.meta.url), 'utf8')))(exports)
const { jdTextLength, jdFieldLengthIssues } = exports
const sourceText = readFileSync(new URL('../src/components/RecruitmentJobDescriptionManager.tsx', import.meta.url), 'utf8')
const source = ts.createSourceFile('manager.tsx', sourceText, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
const functions = []
function visit(node) {
  if (ts.isFunctionDeclaration(node) && ['validateDescription', 'saveDraft', 'lengthFeedback', 'reviewLengthIssue'].includes(node.name?.text)) functions.push(node.getText(source))
  ts.forEachChild(node, visit)
}
visit(source)
assert.equal(functions.length, 4)

const draft = () => ({ id: 0, title: 'Synthetic role', summary: 'Synthetic summary', rolePurpose: '', responsibilities: [{ responsibilityText: 'Synthetic duty' }], skills: [{ skillName: 'Research', minimumProficiency: 'Intermediate', isRequired: true, weightPercent: 100 }], qualifications: [{ qualificationName: 'a'.repeat(230), specialization: '' }], certifications: [], languages: [], benefits: [] })
function harness(row) {
  const calls = { save: 0, sections: [], warnings: [], tabs: [] }
  const context = vm.createContext({ draft: row, editingDisabled: false, lastSaveFailure: { current: null }, jdTextLength, jdFieldLengthIssues, skillWeightTotal: () => 100, formatPercent: value => `${value}%`, selectSection: section => calls.sections.push(section), setAdditionalTab: tab => calls.tabs.push(tab), message: { warning: value => calls.warnings.push(value) }, setSaving() {}, saveRecruitmentJobDescription: async () => { calls.save++; throw new Error('Unexpected API mutation') } })
  vm.runInContext(compile(functions.join('\n')), context)
  return { context, calls }
}

test('the full 230-character parsed qualification remains valid instead of restoring the obsolete 180 limit', () => {
  const row = draft(), before = JSON.stringify(row), h = harness(row)
  assert.equal(jdTextLength('qualificationName', row.qualifications[0].qualificationName).error, '')
  assert.equal(h.context.validateDescription(row), '')
  assert.equal(JSON.stringify(row), before)
})

test('TEXT limits use actual UTF-8 bytes and fixed fields count Unicode codepoints after trim', () => {
  const exact = 'अ'.repeat(21845)
  assert.equal(jdTextLength('qualificationName', exact).count, 65535)
  assert.equal(jdTextLength('qualificationName', exact).error, '')
  assert.match(jdTextLength('qualificationName', exact + 'अ').error, /65,535 UTF-8 bytes/)
  assert.equal(jdTextLength('specialization', '  ' + '😀'.repeat(180) + '  ').error, '')
  assert.equal(jdTextLength('specialization', '😀'.repeat(181)).count, 181)
  assert.match(jdTextLength('specialization', '😀'.repeat(181)).error, /180 characters/)
})

test('all persisted fixed field boundaries accept exact capacity and reject one additional codepoint', () => {
  for (const [field, cap] of [['title', 180], ['skillName', 180], ['minimumProficiency', 80], ['languageName', 120], ['proficiency', 80], ['benefitName', 180], ['description', 1000]]) {
    assert.equal(jdTextLength(field, '😀'.repeat(cap)).error, '', field)
    assert.ok(jdTextLength(field, '😀'.repeat(cap + 1)).error, field)
  }
})

test('manual Save rejects an oversized parsed qualification before POST, retains all text and opens the right section', async () => {
  const row = draft(); row.qualifications[0].qualificationName = 'अ'.repeat(21846)
  const before = JSON.stringify(row), h = harness(row)
  assert.equal(await h.context.saveDraft(), false)
  assert.equal(h.calls.save, 0)
  assert.equal(h.context.lastSaveFailure.current.requestSent, false)
  assert.deepEqual(h.calls.sections, ['qualifications'])
  assert.equal(JSON.stringify(row), before)
  assert.match(h.context.lengthFeedback('qualificationName', row.qualifications[0].qualificationName).help, /Split the full eligibility/)
})

test('auto Save rejects a too-long specialization without a POST or repeated warning', async () => {
  const row = draft(); row.qualifications[0].specialization = 'x'.repeat(181)
  const h = harness(row)
  assert.equal(await h.context.saveDraft(true), false)
  assert.equal(h.calls.save, 0)
  assert.equal(h.calls.warnings.length, 0)
  assert.match(h.context.lastSaveFailure.current.error, /Specialization exceeds 180/)
})

test('title mismatch and hidden child fields give immediate row/section feedback without disclosing their text', async () => {
  for (const [configure, section, label] of [
    [row => { row.title = 'x'.repeat(181) }, 'role', 'Public role title'],
    [row => { row.skills[0].skillName = 'x'.repeat(181) }, 'skills', 'Skill row 1'],
    [row => { row.languages = [{ languageName: 'x'.repeat(121), proficiency: 'Professional' }] }, 'additional', 'Language row 1'],
    [row => { row.benefits = [{ benefitName: 'Health', description: 'x'.repeat(1001) }] }, 'additional', 'Benefit row 1'],
  ]) {
    const row = draft(); configure(row); const before = JSON.stringify(row), h = harness(row)
    assert.equal(await h.context.saveDraft(), false)
    assert.equal(h.calls.save, 0)
    assert.equal(h.calls.sections[0], section)
    assert.ok(h.context.lastSaveFailure.current.error.includes(label))
    assert.ok(!h.context.lastSaveFailure.current.error.includes('x'.repeat(20)))
    assert.equal(JSON.stringify(row), before)
  }
})
