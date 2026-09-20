import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/pages/RecruitmentPage.tsx', import.meta.url), 'utf8')
const file = ts.createSourceFile('RecruitmentPage.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
let tabExpression, itemsExpression
function walk(node) {
  if (ts.isVariableDeclaration(node) && node.name.getText(file) === 'requestTab') tabExpression = node.initializer.getText(file)
  if (ts.isJsxSelfClosingElement(node) && node.tagName.getText(file) === 'Tabs'
    && node.attributes.properties.some(attr => ts.isJsxAttribute(attr) && attr.name.text === 'activeKey'
      && attr.initializer?.expression?.getText(file) === 'requestTab')) {
    itemsExpression = node.attributes.properties.find(attr => ts.isJsxAttribute(attr) && attr.name.text === 'items').initializer.expression.getText(file)
  }
  ts.forEachChild(node, walk)
}
walk(file)
assert.ok(tabExpression && itemsExpression, 'request tab selection and items must exist')

function renderTabs(workflowEnabled, view, query = '') {
  const context = { view, requisitionWorkflowEnabled: workflowEnabled, routeQuery: new URLSearchParams(query),
    dashboard: { pendingApproval: 2 }, positions: [{}], selectedClientId: 20,
    draftRequisitionStatuses: [], pendingRequisitionStatuses: [], openPositionsTable: 'positions-table',
    RecruitmentRequisitionManager: 'requests-manager', React: { createElement: () => 'request-table' } }
  const items = ts.transpileModule(`globalThis.items = (${itemsExpression});`, {
    compilerOptions: { jsx: ts.JsxEmit.React, target: ts.ScriptTarget.ES2022 },
  }).outputText
  vm.runInNewContext(`globalThis.activeKey = (${tabExpression});\n${items}`, context)
  return context
}

for (const workflow of [false, true]) {
  test(`open-position link renders the position table with approval workflow ${workflow ? 'on' : 'off'}`, () => {
    for (const query of ['', 'status=pending', 'clientId=20']) {
      const { activeKey, items } = renderTabs(workflow, 'Open Positions', query)
      assert.equal(activeKey, 'approved')
      assert.equal(items.find(item => item.key === activeKey)?.children, 'positions-table')
      assert.equal(items.find(item => item.key === activeKey)?.label, workflow ? 'Approved (1)' : 'Open positions (1)')
    }
  })
  test(`request and pending routes retain their workflow ${workflow ? 'on' : 'off'} behavior`, () => {
    assert.equal(renderTabs(workflow, 'Requisitions').activeKey, 'requests')
    const { activeKey, items } = renderTabs(workflow, 'Requisitions', 'status=pending')
    assert.equal(activeKey, workflow ? 'pending' : 'requests')
    assert.equal(items.some(item => item.key === 'pending'), workflow)
    assert.equal(items.find(item => item.key === activeKey)?.children, 'request-table')
  })
}
