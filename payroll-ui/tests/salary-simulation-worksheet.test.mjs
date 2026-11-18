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
const { salarySimulationWorksheet, salaryColumnLetter, salarySimulationOperands, alignSalaryWorksheetRows, resolveSalaryWorksheetSelection } = load('salarySimulationWorksheet')
const context = { employeeCode: 'OLD-CODE', employeeName: 'Old name', month: '2026-11', clientName: 'Client one' }
const line = (patch = {}) => ({ code: 'BASIC', name: 'Basic salary', category: 'Earning', formula: '10000', substituted: '10000', raw: 10000, monthly: 10000, earned: 8833.33, proRata: true, source: 'Component / template default', rounding: 'Native', inputs: {}, componentId: '1', calculationType: 'Fixed Amount', payType: 'Fixed Pay', ...patch })
const result = { clientId: 7, employeeId: 11, employeeCode: 'EMP-11', employeeName: 'Employee Eleven', workLocationId: 19, workLocationName: 'Effective Mumbai', payPeriod: '2026-10', mode: 'Employee', templateId: 't1', employee: 'EMP-11 - Employee Eleven', complete: true, coverage: 'Native', issues: [], inputs: [{ name: 'ANNUAL_CTC', value: 300000 }, { name: 'PAYROLL_DAYS', value: 30 }, { name: 'PRESENT_DAYS', value: null }, { name: 'PAYABLE_DAYS', value: 26.5 }], lines: [line(), line({ code: 'HRA', name: 'HRA', componentId: '2', formula: 'BASIC * 40 / 100', calculationType: 'Formula', raw: 4000, monthly: 4000, earned: 3533.33 })], gross: 12366.66, deductions: 0, benefits: 0, net: 12366.66 }

const configuredContext = { ...context, template: { lines: [{ componentId: '1', formula: '10000' }, { componentId: '2', formula: 'BASIC * 40 / 100' }, { componentId: '3', formula: 'BASIC * 12 / 100' }, { componentId: '404', formula: '17' }] }, components: [{ id: 1, code: 'BASIC', name: 'Basic salary', category: 'Earning', active: true }, { id: 2, code: 'HRA', name: 'House rent', category: 'Earning', active: true }, { id: 3, code: 'PF', name: 'Provident fund', category: 'Deduction', active: false }] }

test('empty failed native result still exposes every configured component and formula without invented amounts', () => {
  const r = { ...result, complete: false, lines: [], inputs: [], gross: null, deductions: null, benefits: null, net: null, issues: [{ message: 'Different salary assignment' }] }
  const before = JSON.stringify(r)
  const model = salarySimulationWorksheet(r, configuredContext)
  for (const code of ['BASIC', 'HRA', 'PF', 'COMPONENT_404']) for (const suffix of ['input / raw', 'monthly', 'earned']) {
    const cell = model.cells.find(item => item.label === `${code} ${suffix}`)
    assert.equal(cell.value, null); assert.equal(cell.pending, true); assert.equal(cell.inputKey, undefined)
  }
  assert.match(model.cells.find(cell => cell.label === 'PF earned').source, /inactive/)
  assert.match(model.cells.find(cell => cell.label === 'COMPONENT_404 earned').source, /master not available/)
  assert.equal(model.cells.find(cell => cell.label === 'HRA input / raw').configuredFormula, 'BASIC * 40 / 100')
  assert.equal(JSON.stringify(r), before)
})

test('adding template schema preserves successful native values, formulas, zero and additional adjustments', () => {
  const r = { ...result, lines: [...result.lines, line({ code: 'ADJUSTMENT', componentId: '99', raw: 0, monthly: 0, earned: 0 })] }
  const native = salarySimulationWorksheet(r, context), expanded = salarySimulationWorksheet(r, configuredContext)
  for (const original of native.cells) assert.deepEqual(expanded.cells.find(cell => cell.address === original.address), original)
  assert.equal(expanded.cells.find(cell => cell.label === 'ADJUSTMENT earned').value, 0)
  assert.equal(expanded.cells.filter(cell => cell.label === 'BASIC earned').length, 1)
  assert.equal(expanded.cells.find(cell => cell.label === 'PF earned').value, null)
})

test('pending and complete employee rows align template columns and retain Excel references', () => {
  const pending = salarySimulationWorksheet({ ...result, complete: false, lines: [], gross: null, deductions: null, benefits: null, net: null }, configuredContext)
  const complete = salarySimulationWorksheet(result, configuredContext)
  const aligned = alignSalaryWorksheetRows([pending, complete])
  const earnedIndex = aligned.labels.indexOf('BASIC earned'), payableIndex = aligned.labels.indexOf('Payable days')
  assert.equal(aligned.rows[0][earnedIndex].value, null)
  assert.equal(aligned.rows[1][earnedIndex].value, 8833.33)
  assert.ok(aligned.rows[1][earnedIndex].formula.includes(`${salaryColumnLetter(payableIndex)}3`))
  assert.equal(aligned.labels.filter(label => label === 'PF earned').length, 1)
})
test('worksheet identities, month and work location come from the native result', () => {
  const model = salarySimulationWorksheet(result, context)
  assert.deepEqual(model.cells.slice(0, 4).map(cell => cell.value), ['EMP-11', 'Employee Eleven', '2026-10', 'Effective Mumbai'])
  assert.equal(model.cells.some(cell => cell.label === 'Annual CTC input'), false)
})
test('clickable Excel formulas are rebased after Month and Work location columns', () => {
  const model = salarySimulationWorksheet(result, context)
  const monthlyBasic = model.cells.find(cell => cell.label === 'BASIC monthly')
  const hra = model.cells.find(cell => cell.label === 'HRA input / raw')
  assert.equal(model.notice, '')
  assert.match(hra.formula, new RegExp(monthlyBasic.address))
  assert.equal(hra.configuredFormula, 'BASIC * 40 / 100')
  assert.equal(hra.ruleComponentId, '2')
  const payable = model.cells.find(cell => cell.inputKey === 'PAYABLE_DAYS')
  assert.equal(payable.value, 26.5)
  assert.ok(model.cells.find(cell => cell.label === 'BASIC earned').formula.includes(payable.address))
})
test('unknown actual presence remains blank rather than copying payable days or zero', () => {
  const present = salarySimulationWorksheet(result, context).cells.find(cell => cell.inputKey === 'PRESENT_DAYS')
  assert.ok(present.value === null || present.value === '')
  assert.notEqual(present.value, 0)
  assert.notEqual(present.value, 26.5)
})
test('dated policy amount is native read-only, never falsely presented as its overridden configured rule', () => {
  const policy = line({ code: 'PF', name: 'PF policy', componentId: '3', category: 'Deduction', source: 'Effective-dated PF policy (overrides formula)', formula: 'BASIC * 12 / 100', proRata: false, monthly: 1060, earned: 1060, raw: 1060 })
  const model = salarySimulationWorksheet({ ...result, lines: [...result.lines, policy], deductions: 1060, net: 11306.66 }, context)
  const raw = model.cells.find(cell => cell.label === 'PF input / raw')
  assert.match(model.notice, /read-only/)
  assert.equal(raw.value, 1060)
  assert.equal(raw.inputKey, undefined)
  assert.equal(raw.ruleComponentId, undefined)
  assert.equal(raw.configuredFormula, undefined)
})
test('unrepresentable source rules preserve all native amounts without fake local Excel arithmetic', () => {
  const r = { ...result, lines: [line({ formula: 'UNAVAILABLE_BOUND_SOURCE_RATE', calculationType: 'Formula' })] }
  const before = JSON.stringify(r)
  const model = salarySimulationWorksheet(r, context)
  assert.match(model.notice, /without a safe offline Excel equivalent/)
  assert.equal(model.cells.find(cell => cell.label === 'Basic salary · earned').value, 8833.33)
  assert.equal(model.cells.find(cell => cell.label === 'Basic salary · earned').formula, '')
  assert.equal(JSON.stringify(r), before)
})
test('multiple effective locations remain separate names and template scenario never invents an employee location', () => {
  const multi = salarySimulationWorksheet({ ...result, locations: [{ workLocationId: 19, workLocationName: 'Mumbai', templateId: 't1' }, { workLocationId: 27, workLocationName: 'Delhi', templateId: 't2' }] }, context)
  assert.equal(multi.cells[3].value, 'Mumbai / Delhi')
  const scenario = salarySimulationWorksheet({ ...result, mode: 'Template', workLocationName: '', workLocationId: 0, locations: [] }, context)
  assert.equal(scenario.cells[3].value, 'Template scenario · no employee location')
})
test('missing native amounts stay missing and Excel column names extend past Z', () => {
  const model = salarySimulationWorksheet({ ...result, complete: false, inputs: [], lines: [], gross: null, deductions: null, benefits: null, net: null }, context)
  assert.equal(model.cells.find(cell => cell.label === 'Net pay').value, null)
  assert.equal(salaryColumnLetter(26), 'AA')
})
test('manual scenario identities are editable labels, never a replacement for the selected native employee', () => {
  const native = salarySimulationWorksheet(result, context)
  assert.equal(native.cells[0].inputKey, undefined)
  assert.equal(native.cells[1].inputKey, undefined)
  const scenario = salarySimulationWorksheet({ ...result, mode: 'Template' }, context)
  assert.equal(scenario.cells[0].inputKey, 'SCENARIO_EMPLOYEE_CODE')
  assert.equal(scenario.cells[1].inputKey, 'SCENARIO_EMPLOYEE_NAME')
})
test('reported high-precision source rate and independent period-day inputs remain visible read-only', () => {
  const context = { declarationHash: 'a'.repeat(64), manifestHash: 'b'.repeat(64), definitions: [
    { key: 'SOURCE_MONTHLY_BASIC', label: 'Source monthly basic', unit: 'Amount', precision: 18, minimum: 0, maximum: 1000000, required: true, purpose: 'FormulaOperandOnly' },
    { key: 'SOURCE_OLD_UNITS', label: 'Old-rate duty units', unit: 'Days', precision: 4, minimum: 0, maximum: 31, required: true, purpose: 'FormulaOperandOnly' },
    { key: 'SOURCE_NEW_UNITS', label: 'New-rate duty units', unit: 'Days', precision: 4, minimum: 0, maximum: 31, required: true, purpose: 'FormulaOperandOnly' },
  ], values: { SOURCE_MONTHLY_BASIC: 21000.123456789, SOURCE_OLD_UNITS: 5, SOURCE_NEW_UNITS: 25 } }
  const native = { ...result, lines: [line({ formula: 'SOURCE_MONTHLY_BASIC/PAYROLL_DAYS*SOURCE_NEW_UNITS', calculationType: 'Formula', namedOperands: context })] }
  const before = JSON.stringify(native)
  const model = salarySimulationWorksheet(native, { month: '2026-10', clientName: 'Example' })
  for (const [key, value] of Object.entries(context.values)) {
    const cell = model.cells.find(cell => cell.label.includes(`[${key}]`))
    assert.equal(cell.value, value)
    assert.equal(cell.inputKey, undefined)
    assert.match(cell.source, /read-only/)
  }
  assert.match(model.notice, /changing aggregate payable days does not redistribute/i)
  assert.equal(JSON.stringify(native), before)
})
test('resolved extra native drivers are shown without duplicating system or component dependencies', () => {
  const native = { ...result, inputs: [...result.inputs, { name: 'OVERTIME_HOURS', value: 6.5, source: 'Saved native operand', unit: 'Hours' }], lines: [line({ inputs: { PAYROLL_DAYS: 30, BASIC: 10000, BASIC_EARNED: 8833.33, OVERTIME_HOURS: 6.5, SPECIAL_RATE: 125 } })] }
  const operands = salarySimulationOperands(native)
  assert.equal(operands.length, 2)
  assert.equal(operands.find(cell => cell.label === 'OVERTIME_HOURS').value, 6.5)
  assert.equal(operands.find(cell => cell.label === 'SPECIAL_RATE').value, 125)
})
test('two location parts retain separate values for the same named driver', () => {
  const definition = { key: 'LOCATION_RATE', label: 'Location rate', unit: 'Amount', precision: 2, minimum: 0, maximum: 1000000, required: true, purpose: 'FormulaOperandOnly' }
  const native = { ...result, lines: [line({ namedOperandParts: {
    'part-mumbai': { definitions: [definition], values: { LOCATION_RATE: 11000 } },
    'part-delhi': { definitions: [definition], values: { LOCATION_RATE: 15000 } },
  } })] }
  const operands = salarySimulationOperands(native)
  assert.equal(operands.length, 2)
  assert.equal(operands.find(cell => cell.label.includes('part-mumbai')).value, 11000)
  assert.equal(operands.find(cell => cell.label.includes('part-delhi')).value, 15000)
})

test('multiple employees align heterogeneous columns and every Excel reference follows its employee row', () => {
  const a = { notice: '', cells: [
    { address: 'A2', label: 'Employee code', value: 'A', formula: '' },
    { address: 'B2', label: 'Basic', value: 10000, formula: '' },
    { address: 'C2', label: 'Net', value: 10000, formula: '=B2' },
  ] }
  const b = { notice: '', cells: [
    { address: 'A2', label: 'Employee code', value: 'B', formula: '' },
    { address: 'B2', label: 'Special earning', value: 5000, formula: '' },
    { address: 'C2', label: 'Basic', value: 20000, formula: '' },
    { address: 'D2', label: 'Net', value: 25000, formula: '=SUM(B2,$C$2)' },
  ] }
  const before = JSON.stringify([a, b]), aligned = alignSalaryWorksheetRows([a, b])
  assert.deepEqual(aligned.labels, ['Employee code', 'Basic', 'Net', 'Special earning'])
  assert.equal(aligned.rows[0][2].formula, '=B2')
  assert.equal(aligned.rows[1][2].formula, '=SUM(D3,$B$3)')
  assert.equal(aligned.rows[1][2].address, 'C3')
  assert.equal(aligned.rows[0][3].value, null)
  assert.equal(JSON.stringify([a, b]), before)
})

test('alignment preserves repeated labels by occurrence without merging independent native cells', () => {
  const aligned = alignSalaryWorksheetRows([{ notice: '', cells: [{ address: 'A2', label: 'Source rate', value: 100, formula: '' }, { address: 'B2', label: 'Source rate', value: 200, formula: '' }] }])
  assert.deepEqual(aligned.rows[0].map(cell => cell.value), [100, 200])
  assert.deepEqual(aligned.rows[0].map(cell => cell.address), ['A2', 'B2'])
})

test('a missing-attendance worksheet keeps its present-day column and selection when complete CTC results arrive', () => {
  const incomplete = salarySimulationWorksheet({ ...result, complete: false, lines: [], inputs: result.inputs.map(input => ({ ...input, value: null })), gross: null, deductions: null, net: null }, context)
  const complete = salarySimulationWorksheet({ ...result, lines: [line({ formula: 'CTC * 40 / 100', calculationType: 'Formula' })] }, context)
  const before = alignSalaryWorksheetRows([incomplete]), after = alignSalaryWorksheetRows([complete])
  const column = before.labels.indexOf('Present days')
  assert.equal(column, 5)
  assert.equal(after.labels.indexOf('Present days'), column)
  assert.equal(after.rows[0][column].address, 'F2')
  assert.equal(after.rows[0][column].inputKey, 'PRESENT_DAYS')
  assert.ok(after.labels.includes('Annual CTC'))
  assert.ok(after.labels.indexOf('Annual CTC') > after.labels.indexOf('Payable days'))
  const selected = resolveSalaryWorksheetSelection({ rowKey: 'employee-11', columnKey: before.keys[column], row: 0, column }, ['employee-11'], after.keys)
  assert.equal(after.rows[selected.row][selected.column].inputKey, 'PRESENT_DAYS')
  // Identity is authoritative even if an unrelated column was inserted ahead of an inspected amount.
  assert.equal(resolveSalaryWorksheetSelection({ rowKey: 'employee-11', columnKey: 'net', row: 0, column: 1 }, ['employee-22', 'employee-11'], ['extra', 'present', 'net']).column, 2)
})

test('incomplete and completed employees share exactly one canonical editable present-days column', () => {
  const incomplete = salarySimulationWorksheet({ ...result, complete: false, lines: [], inputs: [], gross: null, net: null }, context)
  const completed = salarySimulationWorksheet({ ...result, inputs: result.inputs.map(input => input.name === 'PRESENT_DAYS' ? { ...input, value: 28 } : input), lines: [line({ formula: 'CTC * 40 / 100', calculationType: 'Formula' })] }, context)
  const combined = alignSalaryWorksheetRows([incomplete, completed])
  assert.equal(combined.labels.filter(label => label === 'Present days').length, 1)
  assert.equal(combined.labels.includes('Present days input'), false)
  assert.deepEqual(combined.rows.map(row => row[5].inputKey), ['PRESENT_DAYS', 'PRESENT_DAYS'])
  assert.deepEqual(combined.rows.map(row => row[5].address), ['F2', 'F3'])
  assert.equal(combined.rows[1][5].value, 28)
})
