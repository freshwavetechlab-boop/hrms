import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const manager = read('../src/components/RecruitmentRequisitionManager.tsx')
const repository = read('../../Payroll.API/Repositories/RecruitmentRepository.cs')

test('budget field is a standalone two-decimal numeric input, not a master selector', () => {
  const file = ts.createSourceFile('manager.tsx', manager, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  let budget
  function walk(node) {
    if (ts.isJsxElement(node) && node.openingElement.tagName.getText(file) === 'Form.Item'
      && node.openingElement.attributes.properties.some(attr => ts.isJsxAttribute(attr) && attr.name.getText(file) === 'name' && attr.initializer?.text === 'budgetAmount')) budget = node.getText(file)
    ts.forEachChild(node, walk)
  }
  walk(file)
  assert.ok(budget)
  assert.match(budget, /<InputNumber/)
  assert.match(budget, /precision=\{2\}/)
  assert.match(budget, /min=\{0\.01\}/)
  assert.doesNotMatch(budget, /<Select|RecruitmentMasterSelect/)
  assert.doesNotMatch(manager, /budgetAmounts|getRecruitmentMasterOptions\('Budget Amount'\)/)
})

test('API no longer queries or requires a budget master; existing numeric columns remain', () => {
  assert.doesNotMatch(repository, /ExistsBudgetMasterAsync|Selected budget amount is not active|No active Budget Amount/)
  assert.doesNotMatch(repository, /MasterValuesAsync\(db, [^\n]*"Budget Amount"\)/)
  assert.equal([...repository.matchAll(/BudgetAmount DECIMAL\(18,2\) NOT NULL DEFAULT 0/g)].length, 2)
  assert.match(repository, /var budgetError = ValidateBudgetAmount\(request\)/)
})

test('approver, reapproval after changed amount and offer ceiling checks are retained', () => {
  assert.match(manager, /name="budgetApproverUserId"/)
  assert.match(repository, /budgetChanged && request.BudgetApproverUserId is not > 0/)
  assert.match(repository, /existing.BudgetAmount != request.BudgetAmount/)
  assert.match(repository, /PrepareBudgetApprovalAsync/)
  const gate = read('../../Payroll.API/Services/RecruitmentOfferReleaseGate.cs')
  assert.match(gate, /BudgetApprovalStatus/)
})
