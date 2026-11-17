import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'
const modules = new Map()
function load(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}; modules.set(name, exports)
  const code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', '')))
  return exports
}
const { evaluatePayrollFormula: evaluate, inspectPayrollFormula, payrollFormulaErrorKind } = load('payrollFormula')
const { validateSalaryTemplateFormula: validate } = load('salaryTemplateFormulaValidation')
const { calculateSalaryDetails } = load('salary')
const cases = JSON.parse(readFileSync(new URL('./fixtures/salary-formula-parity.json', import.meta.url), 'utf8'))
for (const fixture of cases) test(`native payroll parity: ${fixture.name}`, () => {
  assert.ok(Math.abs(evaluate(fixture.formula, fixture.values) - fixture.expected) < 1e-8)
  assert.deepEqual(validate(fixture.formula, new Set(Object.keys(fixture.values))), [])
})
test('IF does not resolve its unused input or calculate its unused division', () => {
  assert.equal(evaluate('IF(TRUE,8,MISSING_VALUE)', {}), 8)
  assert.equal(evaluate('IF(1=1,8,1/0)', {}), 8)
  assert.throws(() => evaluate('IF(FALSE,8,MISSING_VALUE)', {}), /Unknown or unresolved/)
})
test('IFERROR catches only numeric calculation errors and not missing inputs', () => {
  assert.equal(evaluate('IFERROR(1/0,7)', {}), 7)
  assert.equal(evaluate('IFERROR(ROUND(1,9),7)', {}), 7)
  assert.throws(() => evaluate('IFERROR(MISSING,7)', {}), /Unknown or unresolved/)
  assert.throws(() => evaluate('IFERROR(UNSUPPORTED(1),7)', {}), /Unsupported native/)
})
test('syntax analysis is non-evaluating and retains all branch dependencies', () => {
  assert.deepEqual(inspectPayrollFormula('IF(BASIC_MONTHLY>21000,0,BASIC_EARNED*0.75%)').variables, ['BASIC_MONTHLY', 'BASIC_EARNED'])
  assert.deepEqual(inspectPayrollFormula('IF(1=0,1/0,HRA)').variables, ['HRA'])
  assert.deepEqual(validate('IF(1=1,0,MISSING)', new Set()), ['Unknown value: MISSING.'])
})
test('real invalid conditional syntax and wrong function arity remain blocking', () => {
  for (const text of ['IF(BASIC>1,2,3', 'IF(1,2,3,4)', 'IF(1,,3)', 'MIN()', 'ROUND(1,2,3)', 'IF(1==1,2,3)', 'BASIC!A1', 'Math.random()', '1;2', 'IF(1,2,UNSUPPORTED(3))']) assert.ok(validate(text, new Set(['BASIC'])).length > 0, text)
})
test('top-level booleans are not silently returned as monetary output', () => {
  for (const text of ['TRUE', 'FALSE', '1=1', 'AND(TRUE,FALSE)', 'IF(FALSE,1)']) assert.throws(() => evaluate(text, {}), /must be numeric/)
  assert.equal(evaluate('IF(AND(TRUE,FALSE),1,0)', {}), 0)
})
test('native formula size/depth/argument limits are retained in preview', () => {
  for (const text of ['1'.repeat(4097), '('.repeat(65) + '1' + ')'.repeat(65), 'SUM(' + Array(257).fill('1').join(',') + ')', Array(513).fill('1').join('+')]) assert.throws(() => inspectPayrollFormula(text))
})
test('errors distinguish incomplete inputs from invalid syntax and arithmetic', () => {
  for (const [text, kind] of [['MISSING', 'unresolved'], ['1/0', 'arithmetic'], ['IF(1,,0)', 'syntax'], ['TRUE', 'result-type']]) {
    try { evaluate(text, {}); assert.fail('Expected error') } catch (error) { assert.equal(payrollFormulaErrorKind(error), kind) }
  }
})
test('a valid source conditional does not create false ESIC and dependent-value errors', () => {
  const components = [
    { id: '1', code: 'BASIC', active: true, calculationType: 'Fixed Amount', category: 'Earning', value: '18000', proRata: true, priority: 1 },
    { id: '2', code: 'ESIC', active: true, calculationType: 'Formula', category: 'Deduction', formula: 'IF(BASIC_MONTHLY>21000,0,BASIC_EARNED*0.75%)', proRata: false, priority: 2 },
    { id: '3', code: 'ESIC_DEPENDENCY', active: true, calculationType: 'Formula', category: 'Information', formula: 'ESIC+1', proRata: false, priority: 3 },
  ]
  const structure = { lines: components.map(c => ({ componentId: c.id, value: c.formula || c.value })) }
  const rows = calculateSalaryDetails(0, components, structure, {}, { payrollDays: 30, payableDays: 15 })
  assert.equal(rows[0].earned, 9000)
  assert.equal(rows[1].monthly, 67.5)
  assert.equal(rows[2].monthly, 68.5)
  assert.ok(rows.every(row => !row.error))
})
