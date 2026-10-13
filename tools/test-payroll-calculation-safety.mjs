import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from '../payroll-ui/node_modules/typescript/lib/typescript.js'

const moduleUrl = file => 'data:text/javascript;base64,' + Buffer.from(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText).toString('base64')
const formulaUrl = moduleUrl('payroll-ui/src/utils/payrollFormula.ts')
const { evaluatePayrollFormula, roundPayroll } = await import(formulaUrl)
const salarySource = ts.transpileModule(fs.readFileSync('payroll-ui/src/utils/salary.ts', 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText.replace("'./payrollFormula'", JSON.stringify(formulaUrl))
const { calculateSalaryDetails, calculateSalaryJson } = await import('data:text/javascript;base64,' + Buffer.from(salarySource).toString('base64'))
for (const formula of ['UNKNOWN+1', '10/0', 'MIN(1,2', '1+', '10 20', 'ROUND(1,2)', 'BOGUS(1)', 'MIN()']) assert.throws(() => evaluatePayrollFormula(formula, {}), formula)
assert.equal(roundPayroll(115.605), 115.60)
assert.equal(evaluatePayrollFormula('ROUND(-1.5)', {}), -2)
assert.equal(evaluatePayrollFormula('MIN(WAGE_EARNED,15000)*12%', { WAGE_EARNED: 7193.20 }), 863.184)
const component = (id, code, formula, category = 'Earning', priority = 10, proRata = false, calculationType = 'Formula') => ({ id, code, name: code, formula, value: formula, category, priority, proRata, active: true, calculationType, payType: 'Fixed Pay' })
const calculate = (components, payableDays = 30, overrides = {}) => calculateSalaryDetails(360000, components, { lines: components.map(row => ({ componentId: row.id })) }, overrides, { payrollDays: 30, payableDays })
let rows = calculate([component('1', 'WAGE', '15414', 'Earning', 100, true), component('2', 'PF', 'MIN(WAGE_EARNED,15000)*12%', 'Deduction', 1)], 14)
assert.equal(rows.find(row => row.component.code === 'PF').earned, 863.18)
rows = calculate([component('1', 'BASIC', '10000'), component('2', 'RECOVERY', '1000', 'Deduction', 20), component('3', 'FEE', 'SUM(EARNINGS)*10%', 'Deduction', 30)])
assert.equal(rows[2].monthly, 1000)
rows = calculate([component('1', 'BASIC', '10000'), component('2', 'ALLOWANCE', '', 'Earning', 20, false, 'Residual / Balancing')])
assert.equal(rows[1].monthly, 20000)
rows = calculate([component('1', 'WAGE', '10000', 'Earning', 10, true), component('2', 'TAX', 'WAGE_EARNED*10%', 'Deduction', 20)], 15, { '1': 20000 })
assert.equal(rows[1].monthly, 1000)
assert.ok(calculate([component('1', 'A', 'B'), component('2', 'B', 'A')]).some(row => row.error))
assert.ok(calculate([component('1', 'A', 'UNKNOWN')])[0].error)
assert.equal(calculate([component('1', 'A', 'TOTAL_DAYS + WORKING_DAYS')])[0].monthly, 60)
assert.equal(calculate([component('1', 'A', '10', 'Earning', 10, false, 'Percentage of CTC')])[0].monthly, 3000)
assert.equal(calculate([component('1', 'BASIC', '10000', 'Earning', 100), component('2', 'HRA', '40%', 'Earning', 1, false, 'Percentage of Component')])[1].monthly, 4000)
assert.ok(calculate([component('1', 'A', '')])[0].error)
assert.equal(calculate([component('1', 'A', '10000', 'Earning', 10, true)], 0)[0].earned, 0)
const daily = component('1', 'DAILY_WAGES', '700', 'Earning', 10, false, 'Daily Rate')
const dailyTemplate = { lines: [{ componentId: '1' }] }
for (const [days, payrollDays, expected] of [[0,30,0],[22,30,15400],[22.5,30,15750],[28,28,19600],[31,31,21700]]) {
  const result = calculateSalaryDetails(0, [daily], dailyTemplate, {}, { payableDays:days, payrollDays })[0]
  assert.equal(result.earned, expected)
  assert.equal(result.dailyRate, 700)
}
assert.equal(JSON.parse(calculateSalaryJson(0, [daily], dailyTemplate, { '1':850 }))['1'], '850', 'Store the daily rate, not the monthly equivalent')
assert.equal(calculateSalaryDetails(0, [daily], dailyTemplate, { '1':850 }, { payableDays:22 })[0].earned, 18700)
const configSource = ts.transpileModule(fs.readFileSync('payroll-ui/src/utils/salaryComponentConfig.ts', 'utf8'), { compilerOptions:{ module:ts.ModuleKind.ESNext, target:ts.ScriptTarget.ES2022 } }).outputText.replace("'./payrollFormula'", JSON.stringify(formulaUrl))
const { validateSalaryComponent, displayFormula } = await import('data:text/javascript;base64,' + Buffer.from(configSource).toString('base64'))
const legacy = { ...component('1', 'WAGE', '', 'Earning', 10, true, 'Percentage of CTC'), value:'40', baseComponent:'', name:'Wages', code:'WAGE', fbp:true, restrictFbp:true, recurring:false, scheduled:true, epf:'Always', esi:true, ctc:false }
const before = JSON.stringify(legacy)
assert.equal(displayFormula(legacy), 'CTC * 40%')
assert.deepEqual(validateSalaryComponent(legacy, [legacy], legacy), [])
assert.equal(JSON.stringify(legacy), before, 'Reading and validating must not normalize saved fields')
assert.ok(validateSalaryComponent({ ...legacy, calculationType:'Daily Rate' }, [legacy], legacy).length)
const formula = { ...legacy, id:'2', code:'ALLOWANCE', calculationType:'Formula', formula:'WAGE_EARNED*5%', value:'' }
assert.deepEqual(validateSalaryComponent(formula, [legacy]), [])
console.log('PASS: payroll formula errors, dependency ordering, aliases, decimal parity, overrides, circular formulas, and rounding')
