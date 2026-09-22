import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const context = vm.createContext({ exports: {} })
vm.runInContext(ts.transpileModule(readFileSync(new URL('../src/services/recruitmentDeleteFeedback.ts', import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, context)
const actions = (path, error) => JSON.parse(JSON.stringify(context.exports.recruitmentDeleteActions(path, error)))

test('linked JD blocker opens the exact hiring request version history', () => {
  assert.deepEqual(actions('/api/recruitment/requisitions/42', 'Delete the 1 linked job-description version(s) first.'), [
    { label: 'Manage linked JD versions', href: '/recruitment/requisitions?requisitionId=42&jdHistory=1' },
  ])
  assert.equal(actions('/api/recruitment/requisitions/56?clientId=20', 'Delete the 2 linked job-description version(s) first.')[0].href,
    '/recruitment/requisitions?requisitionId=56&jdHistory=1')
})

test('other dependency actions keep their existing destinations', () => {
  assert.equal(actions('/api/recruitment/requisitions/42', 'Delete the 1 linked open position(s) first.')[0].href, '/recruitment/open-positions')
  assert.equal(actions('/api/recruitment/job-descriptions/77', 'Delete the 1 linked job posting(s) first.')[0].href, '/recruitment/job-postings')
  assert.deepEqual(actions('/api/payroll/42', 'Delete the 1 linked job-description version(s) first.'), [])
  assert.deepEqual(actions('/api/recruitment/requisitions/42', 'Requisition was not found in your permitted client scope.'), [])
})
