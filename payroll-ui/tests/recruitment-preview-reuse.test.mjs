import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const file = ts.createSourceFile('recruitmentTalentService.ts', readFileSync(new URL('../src/services/recruitmentTalentService.ts', import.meta.url), 'utf8'), ts.ScriptTarget.Latest, true)
const names = ['intakeRecruitmentResumes', 'uploadRecruitmentResumeBatch']
const code = file.statements.filter(node => ts.isVariableStatement(node)
  && node.declarationList.declarations.some(declaration => names.includes(declaration.name.getText(file))))
  .map(node => node.getText(file).replace(/^export\s+/, '')).join('\n')
const draft = token => ({ previewToken: token, firstName: 'Synthetic', lastName: 'Example', email: 'reviewed@example.test', phone: '', address: '', totalExperienceMonths: 24,
  currentCompany: 'Reviewed Company', currentTitle: 'Reviewed Title', highestQualification: 'Master of Technology', skills: ['Cloud'], certifications: [], noticePeriodDays: 0, currentCtc: 0, expectedCtc: 0 })

test('bulk final upload preserves each preview receipt, manual corrections and explicit zero values', async () => {
  const requests = []
  const context = { FormData, Math, postFormWithProgress: async (path, body) => {
    requests.push({ path, body })
    return { ok: true, status: 200, data: { imported: 1, needsReview: 0, items: [{ success: true }] } }
  } }
  vm.createContext(context)
  vm.runInContext(ts.transpile(code + '\nglobalThis.intake = intakeRecruitmentResumes', { target: ts.ScriptTarget.ES2022 }), context)
  const files = [new File(['source one'], 'one.txt'), new File(['source two'], 'two.txt')]
  const result = await context.intake({ clientId: 20, positionId: 91, jobPostingId: 13, sourceType: 'Direct Sourcing', files, drafts: [draft('A'.repeat(64)), draft('B'.repeat(64))] }, () => {})
  assert.equal(result.data.imported, 2)
  assert.equal(requests.length, 2)
  requests.forEach(({ path, body }, index) => {
    assert.equal(path, '/api/recruitment/resume-intake')
    assert.equal(body.get('previewToken'), (index ? 'B' : 'A').repeat(64))
    assert.equal(body.get('draftCurrentCompany'), 'Reviewed Company')
    assert.equal(body.get('draftCurrentTitle'), 'Reviewed Title')
    assert.equal(body.get('draftCurrentCtc'), '0')
    assert.equal(body.get('draftNoticePeriodDays'), '0')
    assert.equal(body.get('files').name, files[index].name)
    assert.equal(body.get('jobPostingId'), '13')
  })
})

test('legacy drafts without a receipt retain the normal upload path', async () => {
  let body
  const context = { FormData, Math, postFormWithProgress: async (_path, form) => { body = form; return { ok: true, status: 200, data: { imported: 1, needsReview: 0, items: [] } } } }
  vm.createContext(context)
  vm.runInContext(ts.transpile(code + '\nglobalThis.intake = intakeRecruitmentResumes', { target: ts.ScriptTarget.ES2022 }), context)
  await context.intake({ files: [new File(['source'], 'resume.txt')], sourceType: 'Direct Sourcing', drafts: [draft(undefined)] }, () => {})
  assert.equal(body.get('previewToken'), null)
  assert.equal(body.get('draftReviewed'), 'true')
})
