import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const modules = new Map()
function load(path) {
  if (modules.has(path)) return modules.get(path)
  const exports = {}
  modules.set(path, exports)
  const code = ts.transpileModule(readFileSync(new URL(`../src/utils/${path}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, name => load(name.replace(/^\.\//, '')))
  return exports
}
const { salaryTemplateReadiness } = load('salaryTemplateReadiness')
const basic = { id: 1, name: 'Basic salary', code: 'BASIC', active: true, calculationType: 'Fixed Amount', formula: '', value: '' }
const hra = { id: 2, name: 'House rent', code: 'HRA', active: true, calculationType: 'Formula', formula: 'BASIC * 40%', value: '' }
const template = { id: 17, name: 'Location salary', active: true, clientId: '12', annualCtc: '', lines: [{ componentId: '1', value: '' }, { componentId: '2', value: '' }] }
const state = (patch = {}, components = [basic, hra]) => salaryTemplateReadiness({ ...template, ...patch }, components)

test('configured source formulas remain configured without employee wages, CTC or attendance', () => {
  assert.equal(state().status, 'Configured')
  assert.equal(state({ lines: [{ componentId: '2', formula: 'IF(OLD_RATE_DAYS > 0, OLD_RATE * OLD_RATE_DAYS, NEW_RATE * NEW_RATE_DAYS)' }] }).status, 'Configured')
})

test('empty or incomplete configuration is Draft, with a useful reason', () => {
  assert.match(state({ lines: [] }).reason, /Add salary components/)
  assert.equal(state({ lines: [] }).status, 'Draft')
  assert.equal(state({ name: ' ' }).status, 'Draft')
  assert.equal(state({}, [basic, { ...hra, formula: '', value: '' }]).status, 'Draft')
})

test('missing, inactive and duplicate component references require review', () => {
  assert.equal(state({}, [basic]).status, 'Draft')
  assert.equal(state({}, [basic, { ...hra, active: false }]).status, 'Draft')
  assert.equal(state({ lines: [{ componentId: '1' }, { componentId: '1' }] }).status, 'Draft')
})

test('malformed expressions become Draft while valid IF and scientific numeric expressions stay configured', () => {
  assert.equal(state({ lines: [{ componentId: '2', formula: 'IF(BASIC > 0, 50, 0' }] }).status, 'Draft')
  assert.equal(state({ lines: [{ componentId: '2', formula: 'IF(BASIC > 0, 50, 0)' }] }).status, 'Configured')
  assert.equal(state({ lines: [{ componentId: '1', value: '1E3' }] }).status, 'Configured')
})

test('numeric zero and blank employee-owned fixed/manual/rate inputs do not imply a draft', () => {
  assert.equal(state({ lines: [{ componentId: '1', value: '0' }] }).status, 'Configured')
  assert.equal(state({ lines: [{ componentId: '1', value: '' }] }).status, 'Configured')
  assert.equal(state({ lines: [{ componentId: '1', value: '10,000' }] }).status, 'Configured')
  assert.equal(state({ lines: [{ componentId: '1', value: '' }] }, [{ ...basic, calculationType: 'Daily Rate' }]).status, 'Configured')
  assert.equal(state({ lines: [{ componentId: '1', value: '' }] }, [{ ...basic, calculationType: 'Manual / Variable' }]).status, 'Configured')
})

test('inactive status takes precedence and readiness never mutates salary configuration', () => {
  const original = JSON.stringify({ template, basic, hra })
  assert.equal(state({ active: false, lines: [] }).status, 'Inactive')
  state()
  assert.equal(JSON.stringify({ template, basic, hra }), original)
})
