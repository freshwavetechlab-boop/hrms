import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const cache = new Map()
function compile(name) {
  if (cache.has(name)) return cache.get(name)
  const path = new URL(`../src/utils/${name}.ts`, import.meta.url)
  let code = ts.transpileModule(readFileSync(path, 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText
  code = code.replace(/from ['"]\.\/([^'"]+)['"]/g, (_, dependency) => `from '${compile(dependency)}'`)
  const url = 'data:text/javascript;base64,' + Buffer.from(code).toString('base64')
  cache.set(name, url); return url
}
const { analyzeSalaryTemplateSheet, suggestSalaryExcelHeaderRows, salaryExcelFormulaSignature, buildSalaryTemplateExcelSource, selectedSalaryTemplateExcelBindings } = await import(compile('salaryTemplateExcel'))
const text = value => ({ value: String(value), dataType: 'Text' })
const number = value => ({ value: String(value), dataType: 'Number' })
const formula = (formulaText, value) => ({ value: String(value), dataType: 'Number', formula: true, formulaText })
const sheet = (headers, rows, headerRow = 1) => ({ name: 'Salary source', columnCount: headers.length, rows: [{ sourceRow: headerRow, cells: headers.map(text) }, ...rows] })
const monthly = () => sheet(['Employee Name', 'Basic', 'Total Days', 'Payable Days', 'Basic', 'PF', 'Total Earnings', 'Net Pay'], [
  { sourceRow: 7, cells: [text('Synthetic A'), number('18320'), number('26'), number('17'), formula('B7/C7*D7', '11978.4615384615'), formula('MIN(E7,15000)*12%', '1437.41538461538'), formula('SUM(E7:E7)', '11978.4615384615'), formula('G7-F7', '10541.0461538462')] },
  { sourceRow: 8, cells: [text('Synthetic B'), number('21000'), number('26'), number('21.5'), formula('B8/C8*D8', '17365.3846153846'), formula('MIN(E8,15000)*12%', '1800'), formula('SUM(E8:E8)', '17365.3846153846'), formula('G8-F8', '15565.3846153846')] },
], 6)
const master = (id, code, name, category, active = true) => ({ id, code, name, category, active, componentRole: category === 'Deduction' ? 'Regular Deduction' : 'Regular Earning', calculationType: 'Fixed Amount', value: '', formula: '' })

test('formatting-only used-range columns do not consume the selected binding limit or renumber captured source coordinates', () => {
  const bindings = Array.from({ length: 250 }, (_, columnIndex) => ({ columnIndex, kind: columnIndex === 6 ? 'earning' : columnIndex === 205 ? 'input' : 'ignore', code: columnIndex === 6 || columnIndex === 205 ? 'BASIC' : '' }))
  const selected = selectedSalaryTemplateExcelBindings(bindings)
  assert.deepEqual(selected.map(column => column.columnIndex), [6, 205])
  assert.equal(selected[0], bindings[6]); assert.equal(selected[1], bindings[205])
  assert.equal(bindings.length, 250); assert.equal(bindings[205].columnIndex, 205)
})

test('uses the existing payslip worksheet/header recognition with sparse original coordinates', () => {
  const source = monthly()
  source.rows.unshift({ sourceRow: 1, cells: [text('Synthetic company')] }, { sourceRow: 3, cells: [text('Monthly salary statement')] })
  assert.equal(suggestSalaryExcelHeaderRows(source)[0].row, 6)
  const result = analyzeSalaryTemplateSheet(source, 6)
  assert.equal(result.headerRow, 6); assert.equal(result.sampleRow, 7)
  assert.deepEqual(result.sourceRows, [7, 8])
  assert.equal(result.columns[4].letter, 'E')
})

test('monthly and earned Basic pair by existing classification and dependencies, without a duplicate paid component', () => {
  const source = monthly(), original = structuredClone(source)
  const result = analyzeSalaryTemplateSheet(source, 6, 8, [master(3, 'BASIC', 'Basic Pay', 'Earning'), master(4, 'PF', 'Provident Fund', 'Deduction')])
  assert.equal(result.columns[1].suggestedRole, 'input')
  assert.equal(result.columns[4].suggestedRole, 'earning')
  assert.equal(result.columns[1].suggestedCode, result.columns[4].suggestedCode)
  assert.equal(result.columns[4].suggestedCode, 'BASIC')
  assert.equal(result.columns[4].matchedComponentId, '3')
  assert.deepEqual(result.columns[4].dependencies, [1, 2, 3])
  assert.equal(result.columns[4].sampleFormula, 'B8/C8*D8')
  assert.equal(result.columns[4].sampleValue, '17365.3846153846')
  assert.equal(result.columns[2].suggestedRole, 'payrollDays')
  assert.equal(result.columns[3].suggestedRole, 'payableDays')
  assert.equal(result.columns[6].suggestedRole, 'grossTotal')
  assert.equal(result.columns[7].suggestedRole, 'netPay')
  assert.deepEqual(source, original, 'analysis never changes source caches, formulas, or employee values')
})

test('monthly deduction/employer helpers pair only with the matching dependent output and retain employee/employer identity', () => {
  const source = sheet(['Employee Name', 'Payroll days', 'Payable days', 'Monthly Basic', 'Basic', 'Monthly Employee PF', 'Employee PF', 'Monthly Employer PF', 'Employer PF'], [
    { sourceRow: 2, cells: [text('Synthetic employee'), number(30), number(15), number(10000), formula('D2/B2*C2', 5000), formula('E2*12%', 600), formula('F2', 600), formula('E2*13%', 650), formula('H2', 650)] },
  ])
  const result = analyzeSalaryTemplateSheet(source, 1)
  assert.equal(result.columns[5].suggestedRole, 'input')
  assert.equal(result.columns[7].suggestedRole, 'input')
  assert.equal(result.columns[5].suggestedCode, result.columns[6].suggestedCode)
  assert.equal(result.columns[7].suggestedCode, result.columns[8].suggestedCode)
  assert.notEqual(result.columns[5].suggestedCode, result.columns[7].suggestedCode)
  assert.equal(result.columns[6].suggestedRole, 'deduction')
  assert.equal(result.columns[8].suggestedRole, 'employer')
})

test('late employee formula variants remain visible rather than being sampled away or freezing IF branches', () => {
  const source = monthly()
  for (let sourceRow = 9; sourceRow <= 250; sourceRow++) source.rows.push({ sourceRow, cells: [text(`Synthetic ${sourceRow}`), number('19000'), number('26'), number('17'), formula(`B${sourceRow}/C${sourceRow}*D${sourceRow}`, '12423.0769230769'), formula(sourceRow === 250 ? `IF(E${sourceRow}>15000,15000*12%,E${sourceRow}*12%)` : `MIN(E${sourceRow},15000)*12%`, '1490.76923076923'), formula(`E${sourceRow}`, '12423.0769230769'), formula(`G${sourceRow}-F${sourceRow}`, '10932.3076923077')] })
  const result = analyzeSalaryTemplateSheet(source, 6)
  assert.equal(result.columns[4].totalFormulaVariants, 1)
  assert.equal(result.columns[5].totalFormulaVariants, 2)
  assert.equal(result.columns[5].formulaVariants.reduce((sum, item) => sum + item.count, 0), 244)
  assert.ok(result.columns[5].formulaVariants.some(item => item.formula.startsWith('IF(') && item.sourceRows.includes(250)))
  assert.equal(result.hasFormulaVariants, true)
})

test('formula signatures retain absolute rows and text-literal whitespace while normalizing employee-relative rows', () => {
  assert.equal(salaryExcelFormulaSignature('=ROUND(B7/C7*D7, 2)', 7), salaryExcelFormulaSignature('ROUND(B8/C8*D8,2)', 8))
  assert.notEqual(salaryExcelFormulaSignature('$B$7/C7*D7', 7), salaryExcelFormulaSignature('$B$8/C8*D8', 8))
  assert.notEqual(salaryExcelFormulaSignature('IF(A7="New Delhi",B7,0)', 7), salaryExcelFormulaSignature('IF(A8="NewDelhi",B8,0)', 8))
})

test('identity columns and stated totals stay outside paid components, and CTC controls remain explicit', () => {
  const source = sheet(['Employee Name', 'UAN', 'Mobile Number', 'Bank Account Number', 'Basic', 'Total', 'Net Pay', 'Annual CTC', 'Monthly CTC'], [{ sourceRow: 2, cells: [text('Synthetic A'), number('100000000001'), number('9000000000'), number('100000000000'), number('10000'), formula('E2', '10000'), formula('F2', '10000'), number('120000'), number('10000')] }])
  const result = analyzeSalaryTemplateSheet(source, 1)
  for (const index of [0, 1, 2, 3]) assert.equal(result.columns[index].suggestedRole, 'ignore')
  assert.equal(result.columns[5].suggestedRole, 'grossTotal')
  assert.equal(result.columns[6].suggestedRole, 'netPay')
  assert.equal(result.columns[7].suggestedRole, 'annualCtc')
  assert.equal(result.columns[8].suggestedRole, 'monthlyCtc')
})

test('existing structural evidence retains linked-sheet review instead of substituting cached salary', () => {
  const source = sheet(['Employee Name', 'Basic', 'Net Pay'], [{ sourceRow: 2, cells: [text('Synthetic A'), formula("'Rates'!B2", '10000.125'), formula('B2', '10000.125')] }])
  const rates = { name: 'Rates', columnCount: 2, rows: [{ sourceRow: 2, cells: [text('Synthetic A'), number('10000.125')] }] }
  const result = analyzeSalaryTemplateSheet(source, 1, 2, [], [source, rates])
  assert.equal(result.columns[1].sampleFormula, "'Rates'!B2")
  assert.equal(result.columns[1].sampleValue, '10000.125')
  assert.ok(result.columns[1].issues.some(issue => issue.includes('supplied linked worksheet')))
  assert.equal(result.columns[1].suggestedRole, 'earning')
})

test('mixed literal/formula employee rows require variant review; zero values are not silently treated as blanks', () => {
  const source = monthly()
  source.rows[2].cells[4] = number('0')
  const result = analyzeSalaryTemplateSheet(source, 6, 8)
  assert.equal(result.columns[4].sampleValue, '0')
  assert.equal(result.columns[4].sampleFormula, '')
  assert.equal(result.columns[4].totalFormulaVariants, 2)
  assert.ok(result.columns[4].formulaVariants.some(item => item.signature === 'SOURCE_LITERAL_OR_BLANK' && item.sourceRows.includes(8)))
})

test('ambiguous or inactive master matches are never picked or overwritten', () => {
  const source = monthly()
  const ambiguous = analyzeSalaryTemplateSheet(source, 6, 7, [master(1, 'BASIC', 'Basic', 'Earning'), master(2, 'BASIC_ALT', 'Basic', 'Earning')])
  assert.equal(ambiguous.columns[4].matchedComponentId, undefined)
  const inactive = analyzeSalaryTemplateSheet(source, 6, 7, [master(1, 'BASIC', 'Basic', 'Earning', false), master(2, 'BASIC', 'Basic', 'Deduction')])
  assert.equal(inactive.columns[4].matchedComponentId, undefined)
})

test('an existing zero or calculated master default never freezes a new per-employee input salary', () => {
  const source = monthly()
  for (const unsafe of [{ value: '0' }, { value: '22000' }, { formula: 'CTC*40%', calculationType: 'Formula' }]) {
    const masters = [{ ...master(1, 'BASIC', 'Basic', 'Earning'), ...unsafe }], original = structuredClone(masters)
    const result = analyzeSalaryTemplateSheet(source, 6, 7, masters)
    assert.equal(result.columns[4].matchedComponentId, undefined)
    assert.equal(result.columns[4].suggestedCode, 'BASIC_XL_INPUT')
    assert.equal(result.columns[1].suggestedCode, 'BASIC_XL_INPUT', 'monthly source binding must use the new output code')
    assert.deepEqual(masters, original, 'shared master metadata stays intact')
  }
})

test('a unique blank input-family master is reused on later extracts while generic formula outputs retain their master', () => {
  const masters = [{ ...master(1, 'BASIC', 'Basic', 'Earning'), value: '0' }, master(9, 'BASIC_XL_INPUT', 'Basic (E)', 'Earning'), { ...master(10, 'PF', 'Provident Fund', 'Deduction'), calculationType: 'Formula', formula: 'MIN(BASIC_EARNED,15000)*12%', taxable: false, statutoryType: 'PF Employee' }]
  const original = structuredClone(masters), result = analyzeSalaryTemplateSheet(monthly(), 6, 7, masters)
  assert.equal(result.columns[4].matchedComponentId, '9')
  assert.equal(result.columns[4].suggestedCode, 'BASIC_XL_INPUT')
  assert.equal(result.columns[1].suggestedCode, 'BASIC_XL_INPUT')
  assert.equal(result.columns[5].matchedComponentId, '10')
  assert.equal(result.columns[5].suggestedCode, 'PF')
  assert.deepEqual(masters, original)
})

test('Monthly Basic to Basic Pay aliases retain their verified input relationship and never reuse a zero default', () => {
  const source = sheet(['Employee Name', 'Other cost', 'Monthly Basic', 'Total Days', 'Payable Days', 'Basic Pay', 'Net Pay'], [{ sourceRow: 3, cells: [text('Synthetic A'), number('1200'), number('20000'), number('30'), number('25'), formula('C3/D3*E3', '16666.6666666667'), formula('F3', '16666.6666666667')] }], 2)
  const zeroMaster = { ...master(1, 'BASIC', 'Basic', 'Earning'), value: '0' }
  const result = analyzeSalaryTemplateSheet(source, 2, 3, [zeroMaster])
  assert.equal(result.columns[2].suggestedRole, 'input')
  assert.equal(result.columns[5].suggestedRole, 'earning')
  assert.equal(result.columns[5].suggestedCode, 'BASIC_XL_INPUT')
  assert.equal(result.columns[2].suggestedCode, 'BASIC_XL_INPUT')
  assert.equal(result.columns[5].matchedComponentId, undefined)
  assert.notEqual(result.columns[1].suggestedCode, 'BASIC_XL_INPUT', 'an unrelated cost with a similar amount must not join the salary input pair')
  const reused = analyzeSalaryTemplateSheet(source, 2, 3, [zeroMaster, master(2, 'BASIC_XL_INPUT', 'Basic Pay', 'Earning')])
  assert.equal(reused.columns[5].matchedComponentId, '2')
  assert.equal(reused.columns[2].suggestedCode, 'BASIC_XL_INPUT')
})

test('source capture preserves sparse positions, errors, formula expressions and precision, with explicit optional sample minimization', () => {
  const source = monthly()
  source.rows[1].cells[5].missingCachedValue = true
  source.rows[1].cells[5].error = '#VALUE!'
  const full = buildSalaryTemplateExcelSource(source, 6)
  assert.equal(full.rows[1].sourceRow, 7)
  assert.equal(full.rows[1].cells[4].value, '11978.4615384615')
  assert.equal(full.rows[1].cells[5].formulaText, 'MIN(E7,15000)*12%')
  assert.equal(full.rows[1].cells[5].missingCachedValue, true)
  assert.equal(full.rows[1].cells[5].error, '#VALUE!')
  const sample = buildSalaryTemplateExcelSource(source, 6, 8)
  assert.deepEqual(sample.rows.map(row => row.sourceRow), [6, 8])
  assert.equal(sample.completeSheetCapture, undefined, 'a minimized snapshot never claims complete workbook provenance')
  assert.throws(() => buildSalaryTemplateExcelSource(source, 6, 2), /valid employee sample/)
})

test('closing totals and notes are not mistaken for employee formula variants', () => {
  const source = monthly()
  source.rows.push({ sourceRow: 9, cells: [text('Grand Total'), number('39320'), number('52'), number('38.5'), formula('SUM(E7:E8)', '29343.8461538461'), number('3237.41538461538'), formula('SUM(G7:G8)', '29343.8461538461'), formula('SUM(H7:H8)', '26106.4307692308')] })
  source.rows.push({ sourceRow: 11, cells: [text('Prepared by'), text('Synthetic reviewer'), number('2026')] })
  const result = analyzeSalaryTemplateSheet(source, 6)
  assert.deepEqual(result.sourceRows, [7, 8])
  assert.equal(result.columns[4].totalFormulaVariants, 1)
  assert.throws(() => analyzeSalaryTemplateSheet(source, 5), /source row containing/)
})
