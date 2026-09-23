import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/pages/RecruitmentPage.tsx', import.meta.url), 'utf8')
const file = ts.createSourceFile('RecruitmentPage.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
let tabExpression, contentExpression
function walk(node) {
  if (ts.isVariableDeclaration(node) && node.name.getText(file) === 'requestTab') tabExpression = node.initializer.getText(file)
  if (ts.isVariableDeclaration(node) && node.name.getText(file) === 'requestContent') contentExpression = node.initializer.getText(file)
  ts.forEachChild(node, walk)
}
walk(file)
assert.ok(tabExpression && contentExpression, 'request route selection and content must exist')

function renderRoute(workflowEnabled, view, query = '') {
  const context = { view, requisitionWorkflowEnabled: workflowEnabled, routeQuery: new URLSearchParams(query),
    dashboard: { pendingApproval: 2 }, positions: [{}], selectedClientId: 20,
    draftRequisitionStatuses: ['Draft', 'Sent Back'], pendingRequisitionStatuses: ['Pending Approval'], openPositionsTable: 'positions-table',
    RecruitmentRequisitionManager: 'requests-manager', React: { createElement: (type, props) => ({ type, props }) } }
  const content = ts.transpileModule(`globalThis.content = (${contentExpression});`, {
    compilerOptions: { jsx: ts.JsxEmit.React, target: ts.ScriptTarget.ES2022 },
  }).outputText
  vm.runInNewContext(`globalThis.requestTab = (${tabExpression});\n${content}`, context)
  return context
}

for (const workflow of [false, true]) {
  test(`open-position link renders the position table with approval workflow ${workflow ? 'on' : 'off'}`, () => {
    for (const query of ['', 'status=pending', 'clientId=20']) {
      const { requestTab, content } = renderRoute(workflow, 'Open Positions', query)
      assert.equal(requestTab, 'approved')
      assert.equal(content, 'positions-table')
    }
  })
  test(`request and pending routes retain their workflow ${workflow ? 'on' : 'off'} behavior`, () => {
    assert.equal(renderRoute(workflow, 'Requisitions').requestTab, 'requests')
    const { requestTab, content } = renderRoute(workflow, 'Requisitions', 'status=pending')
    assert.equal(requestTab, workflow ? 'pending' : 'requests')
    assert.equal(content.type, 'requests-manager')
    assert.deepEqual(Array.from(content.props.statusScope), workflow ? ['Pending Approval'] : [])
    assert.equal(content.props.showStatusFilter, !workflow)
  })
}
