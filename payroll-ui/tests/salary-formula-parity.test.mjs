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
const { validateSalaryTemplateFormula: validate, suggestSalaryTemplateFormula: suggest } = load('salaryTemplateFormulaValidation')
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
test('a premature IF bracket can be reviewed without changing the threshold, rates or operands', () => {
  const known = new Set(['BASIC', 'VDA'])
  for (const operator of ['*', '\\*']) {
    const entered = `If(BASIC + VDA)>=25000,125,(BASIC+VDA)${operator}0.5%`
    assert.ok(validate(entered, known).length, 'the typed formula must still be blocked')
    const hint = suggest(entered, known)
    assert.ok(hint?.message.trim())
    const corrected = hint.formula
    assert.ok(corrected, 'offer a validated correction for the typed formula')
    assert.deepEqual(validate(corrected, known), [])
    assert.deepEqual(inspectPayrollFormula(corrected).variables, ['BASIC', 'VDA'])
    for (const [basic, vda, expected] of [[10000, 10000, 100], [24000, 1000, 125], [27000, 3000, 125]]) {
      assert.equal(evaluate(corrected, { BASIC: basic, VDA: vda }), expected)
    }
  }
})
test('a valid nested IF is kept as typed, and ambiguous IF input never gets a guessed branch', () => {
  const known = new Set(['BASIC', 'VDA'])
  const valid = 'IF(BASIC>=25000,125,IF(VDA>0,(BASIC+VDA)*0.5%,0))'
  assert.deepEqual(validate(valid, known), [])
  assert.equal(suggest(valid, known), undefined)
  for (const entered of ['IF(BASIC + VDA)>=25000,125,', 'IF(BASIC + VDA)>=25000,125,0,7',
    'IF(BASIC + UNKNOWN)>=25000,125,0', 'IF(BASIC + VDA)>=25000,125,NOT_SUPPORTED(0)']) {
    assert.ok(validate(entered, known).length, entered)
    assert.ok(!suggest(entered, known)?.formula, entered)
  }
})
test('common pasted operators and IF separators offer parser-checked equivalents', () => {
  const known = new Set(['BASIC'])
  for (const [entered, expected] of [
    [String.raw`BASIC\*0.5%`, [100, 125]],
    ['IF(BASIC==25000,125,0)', [0, 125]],
    ['IF(BASIC>=25000;125;0)', [0, 125]],
  ]) {
    assert.ok(validate(entered, known).length)
    const corrected = suggest(entered, known)?.formula
    assert.ok(corrected, entered)
    assert.deepEqual(validate(corrected, known), [])
    assert.deepEqual([20000, 25000].map(BASIC => evaluate(corrected, { BASIC })), expected)
  }
})
test('top-level booleans are not silently returned as monetary output', () => {
  for (const text of ['TRUE', 'FALSE', '1=1', 'AND(TRUE,FALSE)', 'IF(FALSE,1)']) assert.throws(() => evaluate(text, {}), /must be numeric/)
  assert.equal(evaluate('IF(AND(TRUE,FALSE),1,0)', {}), 0)
})

test('missing or extra brackets give one-based positions and explicit, validated fixes for simple typos', () => {
  const known = new Set(['BASIC', 'VDA'])
  for (const [typed, corrected, position] of [
    ['MIN(BASIC,25000', 'MIN(BASIC,25000)', 4],
    ['=MAX(BASIC,VDA))', '=MAX(BASIC,VDA)', 16],
    ['IF(BASIC>0,125,0', 'IF(BASIC>0,125,0)', 3],
  ]) {
    const hint = suggest(typed, known)
    assert.match(hint.message, new RegExp('position ' + position + '\\b'))
    assert.equal(hint.formula, corrected)
    assert.deepEqual(validate(corrected, known), [])
    assert.equal(suggest(corrected, known), undefined)
  }
})
test('ambiguous bracket placement or incompletely typed arguments offer a diagnostic without rewriting amounts', () => {
  const known = new Set(['BASIC', 'VDA'])
  for (const typed of [
    'MIN(BASIC,25000*0.5%', 'BASIC+VDA)*5', 'BASIC-VDA+5)', 'MAX(BASIC,SUM(1,2,3)',
    'IF(BASIC>0,125', 'MAX(', 'IF(BASIC>0,125,', 'ROUND(MAX(BASIC,125',
    'MAX(OUTSIDE_THIS_TEMPLATE,125',
  ]) {
    const hint = suggest(typed, known)
    assert.match(hint.message, /bracket.*position|position.*bracket/i, typed)
    assert.equal(hint.formula, undefined, typed)
  }
})
test('bracket and parser diagnostics still work before the token catalogue arrives', () => {
  assert.equal(suggest('MAX(BASIC,VDA)'), undefined)
  assert.match(suggest('MAX(BASIC,VDA').message, /position 4/)
  assert.equal(suggest('MAX(BASIC,VDA').formula, undefined)
  assert.match(suggest('BASIC VDA').message, /syntax/i)
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
