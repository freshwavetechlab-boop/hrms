import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const modules = new Map()
function load(name) {
  if (modules.has(name)) return modules.get(name)
  const exports = {}, code = ts.transpileModule(readFileSync(new URL(`../src/utils/${name}.ts`, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
  modules.set(name, exports)
  new Function('exports', 'require', code)(exports, path => load(path.replace('./', '')))
  return exports
}
const { proposeExcelPayrollSetup } = load('excelPayrollSetup')
const { defaultPayslipMapping, payslipHeaders, buildExcelPayslipRows, buildPayslipCalculationSource } = load('excelPayslipImport')
const { validateSalaryComponent } = load('salaryComponentConfig')

function fixture() {
  const { sheet, headerRow } = JSON.parse(readFileSync(new URL('./fixtures/nhdc-salary-import.json', import.meta.url), 'utf8'))
  const contextCells = Array.from({ length: 40 }, () => ({ value: '' }))
  contextCells[2] = { value: 'Formula context' }; contextCells[9] = { value: '5000' }
  contextCells[18] = { value: '5000', formula: true, formulaText: 'J7' }
  const columns = defaultPayslipMapping(payslipHeaders(sheet, headerRow), { ...sheet, rows: [...sheet.rows, { sourceRow: 7, cells: contextCells }] }, headerRow)
  for (const column of columns) if (['earning', 'deduction', 'employer'].includes(column.kind)) column.blankAsZero = true
  const imported = buildExcelPayslipRows(sheet, headerRow, columns)
  assert.deepEqual(imported.issues, [])
  const batch = { id: '0123456789abcdef0123456789abcdef', clientId: 12, clientName: 'Test client', month: '2026-09', sourceFileName: 'Source.xlsx', sheetName: sheet.name, headerRow, rows: imported.rows }
  const source = buildPayslipCalculationSource(sheet, headerRow, columns)
  const context = { source, components: [], workWeeks: [{ value: 'Sunday off', label: 'Sunday off' }], workLocations: [{ id: 10, name: 'Test location' }], employees: imported.rows.map((row, i) => ({ id: i + 1, employeeCode: row.employeeCode, employeeName: row.employeeName, workLocationId: 10 })) }
  return { batch, context, rowIds: batch.rows.map(row => row.id) }
}

test('five actual financial cases propose monthly bases, earned tokens, per-row exemptions and a dynamic EDLI cap', () => {
  const { batch, context, rowIds } = fixture(), before = JSON.stringify({ batch, context })
  const { request, notes } = proposeExcelPayrollSetup(batch, context, rowIds)
  const byCode = new Map(request.components.map(component => [component.code, component]))
  assert.equal(request.payrollDaysColumnIndex, 11); assert.equal(request.payableDaysColumnIndex, 12)
  assert.equal(request.sourceWorkLocationColumnIndex, 4)
  assert.equal(request.createMissingWorkLocations, false)
  assert.equal(request.components.length, 15)
  assert.equal(byCode.get('BASIC').sourceColumnIndex, 5)
  assert.equal(byCode.get('BASIC').calculationType, 'Fixed Amount'); assert.equal(byCode.get('BASIC').proRata, true)
  assert.equal(byCode.get('SPECIAL_ALLOWANCE').sourceColumnIndex, 7)
  assert.equal(byCode.get('EXP_ALLOWANCE').sourceColumnIndex, 8)
  assert.equal(byCode.get('PF').rowFormulas['row-3'], 'BASIC_EARNED*12%')
  assert.equal(byCode.get('PF').rowFormulas['row-26'], '0')
  assert.equal(byCode.get('EPF_ER').rowFormulas['row-26'], '0')
  assert.equal(byCode.get('EPF_ADMIN').rowFormulas['row-42'], '0')
  assert.equal(byCode.get('EPF_EDLI').rowFormulas['row-19'], 'MIN(BASIC_EARNED,25000)*0.5%')
  assert.equal(byCode.get('EPF_EDLI').rowFormulas['row-42'], '0')
  assert.equal(byCode.get('EPF_ADMIN').componentRole, 'Employer Benefit')
  assert.equal(byCode.get('EPF_ADMIN').statutoryType, 'None')
  assert.equal(byCode.get('EPF_EDLI').componentRole, 'Employer Benefit')
  assert.equal(byCode.get('EPF_EDLI').statutoryType, 'None')
  assert.equal(byCode.get('EPF_ER').componentRole, 'Employer Contribution')
  assert.equal(byCode.get('EPF_ER').statutoryType, 'PF Employer')
  assert.equal(byCode.get('ESIC_ER').componentRole, 'Employer Contribution')
  const ordinaryComponents = request.components.map(component => ({ ...component, id: component.key, active: true }))
  for (const code of ['EPF_ADMIN', 'EPF_EDLI', 'EPF_ER', 'ESIC_ER'])
    assert.deepEqual(validateSalaryComponent(ordinaryComponents.find(component => component.code === code), ordinaryComponents), [], `${code} must remain valid in the existing salary component editor`)
  assert.equal(byCode.get('ESIC').rowFormulas['row-86'], 'BASIC_MONTHLY*0.75%')
  assert.equal(byCode.get('STAT_BONUS').rowFormulas['row-86'], 'BASIC_EARNED*8.33%')
  assert.equal(byCode.get('TA_DA').category, 'Reimbursement'); assert.equal(byCode.get('TA_DA').isPeriodAdjustment, true)
  assert.equal(byCode.get('RECOVERY').isPeriodAdjustment, true)
  assert.ok(request.components.every(component => component.blankInputAsZero === false), 'blank monthly-input approval is never inferred from approved outputs')
  assert.ok(notes.some(note => /blank monthly input/.test(note)))
  assert.deepEqual(request.employeeDecisions.map(item => item.matchedEmployeeId), [1, 2, 3, 4, 5])
  assert.equal(context.source.rows.find(row => row.sourceRow === 42).cells[11].value, '12')
  assert.equal(JSON.stringify({ batch, context }), before, 'proposals must not modify the saved source or employee context')
})

test('rounding conversion removes only ROUND precision and preserves a zero MIN argument', () => {
  const { batch, context, rowIds } = fixture(), sourceRow = context.source.rows.find(row => row.sourceRow === 3)
  sourceRow.cells[22].formulaText = 'ROUND(MIN(O3,0),0)+ROUNDUP(MAX(O3,0),0)+ROUNDDOWN(O3,0.0)'
  const component = proposeExcelPayrollSetup(batch, context, rowIds).request.components.find(item => item.code === 'PF')
  assert.equal(component.rowFormulas['row-3'], 'ROUND(MIN(BASIC_EARNED,0))+ROUNDUP(MAX(BASIC_EARNED,0))+ROUNDDOWN(BASIC_EARNED)')
  sourceRow.cells[22].formulaText = 'ROUND(O3,2)'
  const unsupported = proposeExcelPayrollSetup(batch, context, rowIds)
  assert.equal(unsupported.request.components.find(item => item.code === 'PF').rowFormulas['row-3'], 'REVIEW_REQUIRED')
  assert.ok(unsupported.notes.some(note => /rounding precision/.test(note)))
})

test('monthly calculation inputs remain available when ignored in payslip presentation', () => {
  const { batch, context, rowIds } = fixture()
  for (const column of context.source.columns) if (column.columnIndex >= 5 && column.columnIndex <= 13) column.kind = 'ignore'
  const { request } = proposeExcelPayrollSetup(batch, context, rowIds)
  const byCode = new Map(request.components.map(component => [component.code, component]))
  assert.equal(byCode.get('BASIC').sourceColumnIndex, 5)
  assert.equal(byCode.get('HRA').sourceColumnIndex, 6)
  assert.equal(byCode.get('SPECIAL_ALLOWANCE').sourceColumnIndex, 7)
  assert.equal(byCode.get('EXP_ALLOWANCE').sourceColumnIndex, 8)
  assert.equal(byCode.get('OTHER_ALLOW').sourceColumnIndex, 9)
  assert.equal(request.payrollDaysColumnIndex, 11)
  assert.equal(request.payableDaysColumnIndex, 12)
  assert.equal(byCode.get('PF').rowFormulas['row-3'], 'BASIC_EARNED*12%')
  const falseInput = { columnIndex: 40, sourceHeader: 'Basic Bank Account', label: 'Basic Bank Account', kind: 'ignore' }
  context.source.columns.push(falseInput)
  assert.equal(proposeExcelPayrollSetup(batch, context, rowIds).request.components.find(component => component.code === 'BASIC').sourceColumnIndex, 5)
})

test('a calculated condition is never frozen from its cached result', () => {
  const { batch, context, rowIds } = fixture()
  context.source.rows.find(row => row.sourceRow === 3).cells[22].formulaText = 'IF(O3>20000,O3*12%,0)'
  const proposal = proposeExcelPayrollSetup(batch, context, rowIds)
  assert.equal(proposal.request.components.find(item => item.code === 'PF').rowFormulas['row-3'], 'REVIEW_REQUIRED')
  assert.ok(proposal.notes.some(note => /calculated condition/.test(note)))
})

test('employee matching requires a unique normalized name and source work location', () => {
  const { batch, context, rowIds } = fixture()
  context.employees[0].employeeName = '  Mr. Employee 3  '
  context.employees.push({ id: 99, employeeCode: 'OTHER', employeeName: 'Employee 3', workLocation: 'Other location' })
  assert.equal(proposeExcelPayrollSetup(batch, context, rowIds).request.employeeDecisions[0].matchedEmployeeId, 1)
  context.employees.push({ id: 100, employeeCode: 'DUPLICATE', employeeName: 'EMPLOYEE 3', workLocation: ' test-location ' })
  const ambiguous = proposeExcelPayrollSetup(batch, context, rowIds)
  assert.equal(ambiguous.request.employeeDecisions[0].matchedEmployeeId, undefined)
  assert.equal(ambiguous.request.employeeDecisions[0].allowCreate, false)
  assert.ok(ambiguous.notes.some(note => /match several employees/.test(note)))
  context.source.columns[4].sourceHeader = 'Unspecified department'
  const missing = proposeExcelPayrollSetup(batch, context, rowIds)
  assert.ok(missing.request.employeeDecisions.every(item => item.matchedEmployeeId === undefined))
  assert.ok(missing.notes.some(note => /Select one source work-location column/.test(note)))
})

test('approved output blanks cannot hide a source error or approve a monthly-input blank', () => {
  const { batch, context, rowIds } = fixture(), sourceRow = context.source.rows.find(row => row.sourceRow === 42)
  sourceRow.cells[30] = { value: '', error: '#REF!' }
  const proposal = proposeExcelPayrollSetup(batch, context, rowIds)
  assert.equal(proposal.request.components.find(item => item.code === 'EPF_ADMIN').rowFormulas['row-42'], 'REVIEW_REQUIRED')
  assert.equal(proposal.request.components.find(item => item.code === 'EXP_ALLOWANCE').blankInputAsZero, false)
  assert.ok(proposal.notes.some(note => /source error/.test(note)))
})
