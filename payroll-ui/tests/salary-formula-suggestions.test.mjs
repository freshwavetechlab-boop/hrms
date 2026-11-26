import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

function loadUtility(name) {
  const exports = {}
  const source = readFileSync(new URL('../src/utils/' + name + '.ts', import.meta.url), 'utf8')
  new Function('exports', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(exports)
  return exports
}
const { salaryFormulaSuggestions: suggest, getSalaryFormulaTokenContext: context, replaceSalaryFormulaToken: replace,
  salaryFormulaFunctionSuggestions: functions, replaceSalaryFormulaFunction: insertFunction } = loadUtility('salaryFormulaSuggestions')
const { inspectPayrollFormula } = loadUtility('payrollFormula')
const item = (token, componentId, meaning = token, availability = componentId ? 'Included in this template' : 'Required payroll input') => ({ token, componentId, meaning, availability, source: componentId ? 'Calculated component / employee salary assignment' : 'Attendance result', unit: 'Amount', example: '', legacy: false })
const values = [
  item('PAYROLL_DAYS'),
  item('BASIC', 'basic', 'Basic: full-period value'),
  item('BASIC_MONTHLY', 'basic', 'Basic: full-period value'),
  item('BASIC_EARNED', 'basic', 'Basic: after attendance'),
  item('PF', 'pf', 'Provident fund: full-period value'),
  item('PLRS_BASIC', 'plrs', 'PLRS monthly salary'),
  item('MISSING_WAGES', 'missing', 'Missing wages', 'Missing from this template; add it before using'),
]
const scope = { includedComponentIds: ['basic', 'pf', 'missing'], excludeComponentId: 'pf' }

test('only system and included active sources are suggested, excluding the edited component and missing values', () => {
  assert.deepEqual(suggest(values, '', scope).map(row => row.token), ['PAYROLL_DAYS', 'BASIC', 'BASIC_MONTHLY', 'BASIC_EARNED'])
  assert.deepEqual(suggest(values, 'plrs', scope), [])
  assert.deepEqual(suggest(values, 'pf', scope), [])
  assert.deepEqual(suggest([...values, item('INACTIVE', 'basic', 'Inactive value', 'Inactive component')], 'inactive', scope), [])
  assert.equal(suggest(values, '', { includedComponentIds: [], limit: 0 }).length, 0)
})

test('code and readable component names can be searched without rewriting the exact code or hiding its basis', () => {
  assert.deepEqual(suggest(values, 'basic', scope).map(row => row.token), ['BASIC', 'BASIC_MONTHLY', 'BASIC_EARNED'])
  const matches = suggest(values, 'after attendance', scope)
  assert.equal(matches.length, 1)
  const earned = matches[0]
  assert.equal(earned.token, 'BASIC_EARNED')
  assert.equal(earned.meaning, 'Basic: after attendance')
  assert.equal(earned.label, 'BASIC_EARNED — Basic: after attendance')
  assert.deepEqual(values[1], item('BASIC', 'basic', 'Basic: full-period value'), 'source catalogue is not mutated')
})

test('duplicate catalogue rows are deduplicated; ambiguous component codes are omitted and system tokens retain their meaning', () => {
  assert.equal(suggest([values[1], { ...values[1] }], 'BASIC', scope).length, 1)
  assert.deepEqual(suggest([values[1], item('BASIC', 'different')], 'BASIC', { includedComponentIds: ['basic', 'different'] }), [])
  const system = suggest([item('PAYROLL_DAYS', 'basic', 'A component named like attendance'), values[0]], '', scope)
  assert.equal(system.length, 1)
  assert.equal(system[0].componentId, undefined)
  assert.equal(system[0].source, 'Attendance result')
})

test('the actual PF expression offers and replaces only BASIC, preserving ROUND/MIN, ceiling, rate and zero precision', () => {
  const formula = 'ROUND(MIN(bas,15000)*12%,0)'
  const caret = formula.indexOf('bas') + 3
  assert.deepEqual(context(formula, caret), { start: 10, end: 13, query: 'bas' })
  assert.equal(suggest(values, context(formula, caret).query, scope)[0].token, 'BASIC')
  assert.deepEqual(replace(formula, 'BASIC', caret), { value: 'ROUND(MIN(BASIC,15000)*12%,0)', caret: 15 })
})

test('caret and selected fragments replace the full current identifier without damaging surrounding operands', () => {
  const formula = 'BASIC_MONTHLY+IF(PAYABLE_DAYS>0,BASIC_EARNED,0)'
  assert.deepEqual(context(formula, 3), { start: 0, end: 13, query: 'BAS' })
  assert.deepEqual(replace(formula, 'BASIC_EARNED', 3), { value: 'BASIC_EARNED+IF(PAYABLE_DAYS>0,BASIC_EARNED,0)', caret: 12 })
  const start = formula.indexOf('PAYABLE_DAYS')
  assert.deepEqual(replace(formula, 'present_days', start + 2, start + 7), { value: 'BASIC_MONTHLY+IF(PRESENT_DAYS>0,BASIC_EARNED,0)', caret: start + 12 })
})

test('insertion at a blank caret or whole selected expression preserves other text and never treats a numeric exponent as a token', () => {
  assert.equal(context('MIN(,15000)', 4), null)
  assert.deepEqual(replace('MIN(,15000)', 'BASIC_EARNED', 4), { value: 'MIN(BASIC_EARNED,15000)', caret: 16 })
  assert.equal(context('1E3 + 2', 3), null)
  assert.deepEqual(replace('BASIC+HRA', 'BASIC_MONTHLY', 0, 9), { value: 'BASIC_MONTHLY', caret: 13 })
  assert.deepEqual(replace('', 'BASIC'), { value: 'BASIC', caret: 5 })
  assert.throws(() => replace('BAS', 'Basic salary'), /valid payroll token code/)
})

test('partial function names offer supported call signatures without needing a component catalogue', () => {
  assert.deepEqual(functions(' if ').map(fn => fn.name), ['IF', 'IFERROR'])
  assert.deepEqual(functions('round').map(fn => fn.name), ['ROUND', 'ROUNDUP', 'ROUNDDOWN'])
  assert.deepEqual(functions('M').map(fn => fn.name).sort(), ['MAX', 'MIN'])
  assert.deepEqual(functions(''), [])
  assert.deepEqual(functions('VLOOKUP'), [])
  const args = { IF: '1,2,3', IFERROR: '1,2', MIN: '1,2', MAX: '1,2', SUM: '1,2', ROUND: '1,0', ROUNDUP: '1,0',
    ROUNDDOWN: '1,0', ABS: '-1', AND: '1,1', OR: '1,0' }
  for (const [name, parameters] of Object.entries(args)) {
    const fn = functions(name)[0]
    assert.equal(fn.name, name)
    assert.ok(fn.signature.startsWith(name + '(') && fn.description.length > 0)
    assert.doesNotThrow(() => inspectPayrollFormula(name + '(' + parameters + ')'))
  }
})

test('inserting a function preserves surrounding expressions and places the caret inside its brackets', () => {
  assert.deepEqual(insertFunction('mi', 'min', 2), { value: 'MIN()', caret: 4 })
  assert.deepEqual(insertFunction('=basic + ma*0.5%', 'MAX', 11), { value: '=basic + MAX()*0.5%', caret: 13 })
  assert.deepEqual(insertFunction('IF(BASIC>0,mi,0)', 'MIN', 13), { value: 'IF(BASIC>0,MIN(),0)', caret: 15 })
  assert.deepEqual(insertFunction('mAx (basic, VdA) + 1', 'MIN', 1, 3), { value: 'MIN (basic, VdA) + 1', caret: 5 })
  assert.deepEqual(insertFunction('IF(BASIC, 2, 0)', 'IF', 2), { value: 'IF(BASIC, 2, 0)', caret: 3 })
  assert.throws(() => insertFunction('mat', 'Math.max', 3), /supported payroll function/)
})
