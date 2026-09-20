import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const contract = read('../../Payroll.API/Models/RecruitmentRequisitionTextLimits.cs')
const limits = [...contract.matchAll(/new\("(\w+)", "([^"]+)", (\d+)(?:, "([^"]+)")?\)/g)]
  .map(([, field, label, maximum, unit]) => ({ field, label, maximum: Number(maximum), unit: unit || 'characters' }))
const context = vm.createContext({ exports: {}, TextEncoder })
vm.runInContext(ts.transpileModule(read('../src/services/requisitionTextLimits.ts'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const { requisitionTextError: error, requisitionTextErrors: errors } = context.exports

test('all 28 fields share the actual API SQL storage bounds', () => {
  assert.equal(limits.length, 28)
  const repository = read('../../Payroll.API/Repositories/RecruitmentRepository.cs')
  const base = repository.match(/CREATE TABLE IF NOT EXISTS recruitment_requisitions\s*\(([\s\S]*?)\);/i)?.[1]
  assert.ok(base, 'requisition schema found')
  const schema = new Map([...base.matchAll(/(?:^|,)\s*(\w+)\s+(VARCHAR\((\d+)\)|TEXT)/gi)]
    .map(([, field, type, max]) => [field.toLowerCase(), { maximum: max ? Number(max) : 65535, unit: type === 'TEXT' ? 'UTF-8 bytes' : 'characters' }]))
  for (const [, field, type, max] of repository.matchAll(/EnsureColumnAsync\(db, "recruitment_requisitions", "(\w+)", "(VARCHAR\((\d+)\)|TEXT)/gi))
    schema.set(field.toLowerCase(), { maximum: max ? Number(max) : 65535, unit: type === 'TEXT' ? 'UTF-8 bytes' : 'characters' })
  for (const rule of limits) assert.deepEqual(schema.get(rule.field.toLowerCase()), { maximum: rule.maximum, unit: rule.unit }, rule.field)
})

for (const rule of limits) test(`${rule.field}: boundary, overflow, Unicode and no truncation`, () => {
  assert.equal(error('x'.repeat(rule.maximum), rule), '')
  const value = 'x'.repeat(rule.maximum + 1)
  const draft = { [rule.field]: value }
  assert.match(errors(draft, limits)[0].errors[0], /maximum/)
  assert.equal(draft[rule.field], value)
  assert.equal(error(undefined, rule), '')
  const unicode = rule.unit === 'characters' ? '😀'.repeat(rule.maximum) : 'ह'.repeat(rule.maximum / 3)
  assert.equal(error(unicode, rule), '')
  assert.notEqual(error(unicode + 'ह', rule), '')
})

test('every form field validates; manual and automatic save guard before writing', () => {
  const manager = read('../src/components/RecruitmentRequisitionManager.tsx')
  for (const { field } of limits) assert.ok(manager.includes(`textRules('${field}')`), `${field} inline validation`)
  for (const name of ['runAutoSave', 'saveRequest']) {
    const fn = manager.slice(manager.indexOf(`async function ${name}(`))
    assert.ok(fn.indexOf('checkTextLengths(') < fn.indexOf('saveRecruitmentRequisition('), name)
  }
  assert.match(manager, /field-limits/)
  assert.match(manager, /if \(!textLimits.length\)/)
  assert.match(manager, /requisition-text-errors/)
  assert.doesNotMatch(manager, /maxLength=\{190\}/)
})

test('parser and API reuse the contract, including ESS save; JSON is not a bounded text column', () => {
  const parser = read('../../Payroll.API/Services/RecruitmentRequestDocumentParsingService.cs')
  const api = read('../../Payroll.API/Program.cs')
  assert.match(parser, /return RecruitmentRequisitionTextLimits.Review\(new RecruitmentRequestDocumentParseResult/)
  assert.doesNotMatch(parser, /SetAi\([^\n]+, Limit\(/)
  assert.doesNotMatch(parser, /CompactList\([^\n]+, (480|1800)\)/)
  for (const route of ['/api/recruitment/requisitions', '/api/ess/recruitment/requisitions']) {
    const start = api.indexOf(`app.MapPost("${route}"`)
    assert.ok(start > 0)
    assert.match(api.slice(start, api.indexOf('\n});', start)), /repository.SaveDraftAsync\(request, user\)/)
  }
  assert.ok(!limits.some(rule => rule.field === 'sourceParsedJson'))
})
