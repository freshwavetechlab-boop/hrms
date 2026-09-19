import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import vm from 'node:vm'
import ts from 'typescript'

const source = (path) => {
  const file = fileURLToPath(new URL(`../src/${path}`, import.meta.url))
  return ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true, file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS)
}
const find = (root, predicate) => {
  const matches = []
  const visit = (node) => { if (predicate(node)) matches.push(node); ts.forEachChild(node, visit) }
  visit(root)
  return matches
}

test('progress upload preserves its original default and supports a scoped timeout/message', async () => {
  const file = source('services/apiClient.ts')
  const fn = find(file, (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'postFormWithProgress')[0]
  const context = {
    Promise, apiUrl: (path) => path, legacyTokenKey: 'synthetic-test-key',
    sessionStorage: { getItem: () => null }, localStorage: { getItem: () => null },
    notifyMutation: () => {},
    XMLHttpRequest: class {
      upload = {}
      open() {}
      send() { context.lastTimeout = this.timeout; this.ontimeout() }
    },
  }
  vm.createContext(context)
  vm.runInContext(ts.transpile(fn.getText(file).replace(/^export\s+/, ''), { target: ts.ScriptTarget.ES2022 }), context)
  const ordinary = await context.postFormWithProgress('/ordinary', {}, null, () => {})
  assert.equal(context.lastTimeout, 120000)
  assert.equal(ordinary.error, 'Upload timed out.')
  const scoped = await context.postFormWithProgress('/resume-preview', {}, null, () => {}, 660000, 'Resume parsing took too long.')
  assert.equal(context.lastTimeout, 660000)
  assert.equal(scoped.error, 'Resume parsing took too long.')
  assert.equal(scoped.ok, false)
  assert.match(file.text, /options\.timeoutMs \?\? 30000/)
})

for (const [path, expectedCount] of [['services/recruitmentTalentService.ts', 3], ['services/recruitmentOrchestrationService.ts', 1]]) {
  test(`${path}: AI document calls have bounded 660-second windows and specific messages`, () => {
    const file = source(path)
    const calls = find(file, (node) => ts.isCallExpression(node) && node.expression.getText(file) === 'postFormWithProgress')
    const scoped = calls.filter(call => call.arguments[4]?.getText(file) === '660000')
    assert.equal(scoped.length, expectedCount)
    for (const call of scoped) assert.match(call.arguments[5].getText(file), /parsing took too long/)
    if (path.includes('Orchestration')) {
      assert.equal(calls.find(call => call.arguments[0].getText(file).includes('/actions/')).arguments.length, 4)
    }
  })
}

test('JD preview window is extended without changing regular recruitment requests', () => {
  const file = source('services/recruitmentService.ts')
  const call = find(file, node => ts.isCallExpression(node) && node.expression.getText(file) === 'postForm')[0]
  const settings = call.arguments[3].getText(file)
  assert.match(settings, /timeoutMs: 660_000/)
  assert.match(settings, /No job description was saved by this preview/)
})

test('queued Talent Pool results are not represented as completed', () => {
  const file = source('components/RecruitmentGlobalTalentPool.tsx')
  const alert = find(file, node => ts.isJsxSelfClosingElement(node) && node.tagName.getText(file) === 'Alert'
    && node.attributes.getText(file).includes('runResult.scored'))[0]
  const expression = name => alert.attributes.properties.find(attribute => attribute.name?.getText(file) === name).initializer.expression.getText(file)
  const runResult = { warnings: [], queued: 2, scored: 0, skipped: 0 }
  assert.equal(vm.runInNewContext(expression('type'), { runResult }), 'info')
  assert.match(vm.runInNewContext(expression('description'), { runResult }), /queued in the background/)
  runResult.warnings = ['One candidate needs review.']
  assert.equal(vm.runInNewContext(expression('type'), { runResult }), 'warning')
  assert.match(vm.runInNewContext(expression('description'), { runResult }), /needs review.*queued in the background/)
  runResult.warnings = []
  runResult.queued = 0
  assert.equal(vm.runInNewContext(expression('type'), { runResult }), 'success')
  assert.match(vm.runInNewContext(expression('description'), { runResult }), /ready for recruiter selection/)
  assert.match(source('services/recruitmentTalentService.ts').text, /Talent Pool matching request accepted/)
})
