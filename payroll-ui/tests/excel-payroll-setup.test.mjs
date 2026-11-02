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

test('later client imports reuse configuration while new employees and changed formulas come from the new sheet',()=>{
  const columns=['Employee name','Total Days','Payble Days','Basic','Basic','HRA','Professional Tax'].map((sourceHeader,columnIndex)=>({sourceHeader,label:sourceHeader,columnIndex,kind:['employeeName','info','info','info','earning','earning','deduction'][columnIndex]}))
  const oldSource={headerRow:2,columnCount:7,columns,rows:[{sourceRow:3,cells:['One','26','17','10000','6538.4615384615','326.9230769231','200'].map((value,i)=>({value,...(i===4?{formulaText:'D3/B3*C3'}:i===5?{formulaText:'E3*0.05'}:{})}))}]}
  const oldBatch={id:'september',clientId:9,clientName:'Synthetic',month:'2026-09',rows:[{id:'old',sourceRow:3,employeeCode:'SYN-1',employeeName:'One'}]}
  const oldContext={source:oldSource,components:[],employees:[],workWeeks:[],workLocations:[]}
  const previous=proposeExcelPayrollSetup(oldBatch,oldContext,['old']).request
  Object.assign(previous,{monthlyCtcColumnIndex:3,reviewedFixedInputs:true,reviewedPolicyScope:true,reviewedExtraDutyUnits:true,importAttendance:true,roundingReviews:{old:'old-proof'},templateDecisions:[{createVariant:true}]})
  previous.employeeDecisions[0].allowCreate=true
  previous.components.find(component=>component.code==='BASIC').formula='999999'
  const source={...oldSource,rows:[{sourceRow:4,cells:['Two','26','20','11000','8461.5384615385','846.1538461538','0'].map((value,i)=>({value,...(i===4?{formulaText:'D4/B4*C4'}:i===5?{formulaText:'E4*0.1'}:{})}))}]}
  const batch={...oldBatch,id:'october',month:'2026-10',rows:[{id:'new',sourceRow:4,employeeCode:'SYN-2',employeeName:'Two'}]}
  const context={...oldContext,source,clientConfiguration:{clientId:9,month:oldBatch.month,sourceColumns:columns,savedRequest:previous}}
  const before=JSON.stringify({batch,context})
  const {extendClientPayrollConfiguration}=load('excelPayrollReuseBasis')
  const next=extendClientPayrollConfiguration(batch,context,['new'])
  assert.deepEqual(next.rowIds,['new']);assert.equal(next.batchId,'october');assert.equal(next.effectiveFrom,'2026-10-01')
  assert.equal(next.templateName,previous.templateName);assert.equal(next.monthlyCtcColumnIndex,3)
  assert.ok(JSON.stringify(next.components.find(component=>component.code==='HRA')).includes('0.1'))
  assert.ok(!JSON.stringify(next.components).includes('999999'))
  assert.equal(next.employeeDecisions[0].rowId,'new');assert.equal(next.employeeDecisions[0].allowCreate,false)
  assert.equal(next.importAttendance,false);assert.equal(next.reviewedExtraDutyUnits,false);assert.deepEqual(next.templateDecisions,[]);assert.deepEqual(next.roundingReviews,{})
  assert.equal(JSON.stringify({batch,context}),before)
  assert.equal(extendClientPayrollConfiguration({...batch,clientId:10},context,['new']),null)
  const changed=structuredClone(context);changed.source.columns=changed.source.columns.map(column=>({...column}));changed.source.columns[3].sourceHeader='Daily Rate'
  assert.equal(extendClientPayrollConfiguration(batch,changed,['new']),null)
})

test('different literal PT amounts use one reviewed input rule and one reusable template name', () => {
  const columns=['Employee name','Total Days','Payble Days','Basic','Professional Tax','Net paid'].map((sourceHeader,columnIndex)=>({sourceHeader,label:sourceHeader,columnIndex,kind:['employeeName','info','info','earning','deduction','netPay'][columnIndex]}))
  const source={headerRow:1,columnCount:6,columns,rows:[{sourceRow:2,cells:['One','26','26','1000','200','800'].map(value=>({value}))},{sourceRow:3,cells:['Two','26','26','1000','0','1000'].map(value=>({value}))}]}
  const batch={id:'saved',clientId:9,clientName:'Synthetic',month:'2026-09',rows:[{id:'one',sourceRow:2,employeeName:'One',employeeCode:'SYN-1'},{id:'two',sourceRow:3,employeeName:'Two',employeeCode:'SYN-2'}]}
  const context={source,components:[],employees:[],workWeeks:[],workLocations:[]}
  const request=proposeExcelPayrollSetup(batch,context,['one','two']).request
  const pt=request.components.find(component=>component.code==='PT')
  assert.equal(pt.calculationType,'Fixed Amount');assert.equal(pt.sourceColumnIndex,4);assert.equal(pt.expectedColumnIndex,4)
  assert.equal(pt.proRata,false);assert.equal(pt.rowFormulas,undefined)
  assert.equal(request.updateExistingConfiguration,true)
  assert.equal(request.templateName,proposeExcelPayrollSetup({...batch,id:'reimport',month:'2026-10'},context,['one','two']).request.templateName)
  assert.equal(source.rows[0].cells[4].value,'200');assert.equal(source.rows[1].cells[4].value,'0')
})

test('transport day aliases stay dynamic and an ignored side recovery after net stays excluded', () => {
  const headers = ['Total Days', 'Payble Days', 'Basic', 'DA', 'Bonus', 'Monthly Total', 'Basic', 'DA', 'recovery', 'net paid', 'Recovery']
  const kinds = ['info', 'info', 'info', 'info', 'info', 'info', 'earning', 'earning', 'deduction', 'netPay', 'ignore']
  const values = ['26', '29', '10000', '500', '874.65', '11374.65', '11153.8461538462', '557.6923076923', '0', '11711.5384615385', '900']
  const source = { headerRow: 2, columnCount: headers.length,
    columns: headers.map((sourceHeader, columnIndex) => ({ sourceHeader, columnIndex, label: sourceHeader, kind: kinds[columnIndex] })),
    rows: [{ sourceRow: 3, cells: values.map((value, i) => ({ value, ...(i === 6 ? { formulaText: 'C3/A3*B3' } : i === 7 ? { formulaText: 'D3/A3*B3' } : {}) })) }] }
  const batch = { id: 'synthetic', clientId: 42, clientName: 'Synthetic', month: '2026-09', rows: [{ id: 'row', sourceRow: 3, employeeName: 'Synthetic employee', employeeCode: 'SYN-1' }] }
  const context = { source, components: [], workWeeks: [], workLocations: [], employees: [] }
  const before = JSON.stringify({ batch, context })
  const { request } = proposeExcelPayrollSetup(batch, context, ['row'])
  assert.equal(request.payrollDaysColumnIndex, 0); assert.equal(request.payableDaysColumnIndex, 1)
  assert.equal(request.sourceColumnRoles[10], 'ignore')
  assert.equal(request.components.find(c => c.code === 'BASIC').sourceColumnIndex, 2)
  assert.ok(!request.components.some(c => c.expectedColumnIndex === 10))
  assert.equal(request.reviewedExtraDutyUnits, undefined, 'an operator must review extra-duty meaning')
  assert.equal(request.monthlyCtcColumnIndex, undefined, 'a monthly total is not automatically approved as CTC')
  assert.equal(JSON.stringify({ batch, context }), before)
})

test('reusing a saved-batch basis regenerates new-row literals and clears every cohort approval', () => {
  const source = { headerRow: 2, columnCount: 5,
    columns: ['Total Days', 'Payble Days', 'Basic', 'Basic', 'Professional Tax'].map((sourceHeader, columnIndex) => ({ sourceHeader, label: sourceHeader, columnIndex, kind: columnIndex === 3 ? 'earning' : columnIndex === 4 ? 'deduction' : 'info' })),
    rows: [3, 4].map((sourceRow, i) => ({ sourceRow, cells: ['26', i ? '44' : '17', i ? '12000' : '10000', i ? '20307.6923076923' : '6538.4615384615', i ? '0' : '200'].map((value, column) => ({ value, ...(column === 3 ? { formulaText: `C${sourceRow}/A${sourceRow}*B${sourceRow}` } : {}) })) })) }
  const batch = { id: 'same-batch', month: '2026-09', rows: [{ id: 'one', sourceRow: 3, employeeCode: 'SYN-1', employeeName: 'One' }, { id: 'two', sourceRow: 4, employeeCode: 'SYN-2', employeeName: 'Two' }] }
  const context = { source, components: [], employees: [], workWeeks: [], workLocations: [] }
  const previous = proposeExcelPayrollSetup(batch, context, ['one']).request
  Object.assign(previous, { monthlyCtcColumnIndex: 2, reviewedExtraDutyUnits: true, reviewedFixedInputs: true, reviewedPolicyScope: true, roundingReviews: { one: 'a'.repeat(64) }, templateDecisions: [{ variantKey: '1', createVariant: true }], importAttendance: false,
    templateScope: { reviewed: true, workLocationIds: [10], newLocationNames: [], categories: [] },
    attendanceReview: { mode: 'SourceSeed', reviewed: true, boundSourceInputs: 'old-binding' } })
  previous.employeeDecisions[0].allowCreate = true
  const before = JSON.stringify({ batch, context, previous })
  const { reuseExcelPayrollBasis } = load('excelPayrollReuseBasis')
  const next = reuseExcelPayrollBasis(batch, context, ['two'], previous)
  assert.deepEqual(next.rowIds, ['two']); assert.equal(next.monthlyCtcColumnIndex, 2)
  assert.equal(next.components.find(c => c.code === 'PT').sourceColumnIndex, 4)
  assert.equal(next.components.find(c => c.code === 'PT').calculationType, 'Fixed Amount')
  assert.deepEqual(next.components.find(c => c.code === 'PT').rowFormulas, {})
  assert.equal(next.employeeDecisions[0].allowCreate, false)
  assert.equal(next.templateScope.reviewed, false); assert.equal(next.attendanceReview.reviewed, false)
  assert.equal(next.attendanceReview.boundSourceInputs, undefined)
  assert.equal(next.reviewedFixedInputs, false); assert.equal(next.reviewedPolicyScope, false)
  assert.deepEqual(next.roundingReviews, {}); assert.deepEqual(next.templateDecisions, [])
  assert.notEqual(next.templateScope.workLocationIds, previous.templateScope.workLocationIds)
  assert.equal(JSON.stringify({ batch, context, previous }), before)
  assert.throws(() => reuseExcelPayrollBasis(batch, context, ['one'], previous))
  assert.throws(() => reuseExcelPayrollBasis({ ...batch, id: 'other-batch' }, context, ['two'], previous))
})

test('source evidence keeps raw numeric cache text and the explicit CTC basis while withholding identifiers', () => {
  const batch = { id: 'source', rows: [{ id: 'one', sourceRow: 3 }] }
  const source = { headerRow: 2, columns: [
    { columnIndex: 0, sourceHeader: 'Basic', label: 'Basic', kind: 'earning' },
    { columnIndex: 1, sourceHeader: 'Monthly total', label: 'Monthly total', kind: 'info' },
    { columnIndex: 2, sourceHeader: 'UAN', label: 'UAN', kind: 'info' },
  ], rows: [{ sourceRow: 3, cells: [{ value: '615.38461538461547' }, { value: '1000' }, { value: 'private-identifier' }] }] }
  const request = { batchId: 'source', rowIds: ['one'], payrollDaysColumnIndex: -1, payableDaysColumnIndex: -1, monthlyCtcColumnIndex: 1,
    components: [{ sourceColumnIndex: 2, expectedColumnIndex: 0 }] }
  const before = JSON.stringify(source)
  const rows = load('payrollSourceEvidence').payrollSourceEvidence(batch, source, request)
  assert.equal(rows.find(row => row.cell === 'A3').cached, '615.38461538461547')
  assert.equal(rows.find(row => row.cell === 'B3').cached, '1000')
  assert.ok(!JSON.stringify(rows).includes('private-identifier'))
  assert.equal(JSON.stringify(source), before)
})

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
  assert.equal(request.createMissingWorkLocations, true)
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


test('client defaults prefill organization and attendance without changing source formulas or inventing leave entitlement', () => {
  const { batch, context, rowIds } = fixture()
  context.defaults = { department: 'Operations', designation: 'Unassigned', grade: 'G2', workWeek: 'Friday off', attendanceCycleStartDay: 5, attendanceCycleEndDay: 4, payrollReportGenerationDay: 8, attendanceSource: 'Existing client policy: Operations', notes: ['Reviewed policy'] }
  const { request, notes } = proposeExcelPayrollSetup(batch, context, rowIds)
  assert.equal(request.defaultDepartment, 'Operations'); assert.equal(request.defaultDesignation, 'Unassigned'); assert.equal(request.defaultGrade, 'G2')
  assert.equal(request.workWeek, 'Friday off'); assert.equal(request.attendanceCycleStartDay, 5); assert.equal(request.attendanceCycleEndDay, 4); assert.equal(request.payrollReportGenerationDay, 8)
  assert.equal(request.preserveExistingAttendancePolicies, true); assert.equal(request.createMissingDropdownValues, true)
  assert.ok(notes.some(note => note.includes('Existing client policy')))
  assert.equal(request.editableDefaults.leave.active, false)
  assert.equal(request.components.find(component => component.code === 'PF').rowFormulas['row-3'], 'BASIC_EARNED*12%')
})

test('MNGL-style hidden base and earned columns become payroll components with a fixed divisor and dynamic PF/LWF formulas', () => {
  const headers = ['Name', 'Work Location (City name e.g. Pune, Nasik etc)', 'Besic', 'DA', 'Attendance', 'Besic', 'DA', 'Total', 'Total Earning', 'TOTAL EPF', 'LWF Employee Share', 'NetPay/In Hand']
  const formulas = { 5: 'C2/26*E2', 6: 'D2/26*E2', 7: 'F2+G2', 8: 'H2', 9: 'IF((F2+G2)>15000,(F2+G2)*12%,15000*12%)/26*14+IF((F2+G2)>25000,(F2+G2)*12%,25000*12%)/26*12', 10: 'IF(E2>0,(25/6),0)', 11: 'I2-J2-K2' }
  const values = ['Example Person', 'Pune', '26000', '3900', '13', '13000', '1950', '14950', '14950', '2353.84615384615', '4.16666666666667', '12591.9871794872']
  const source = { headerRow: 1, columnCount: headers.length, columns: headers.map((sourceHeader, columnIndex) => ({ sourceHeader, columnIndex, label: sourceHeader, kind: columnIndex === 0 ? 'employeeName' : columnIndex === 8 ? 'earning' : [9, 10].includes(columnIndex) ? 'deduction' : columnIndex === 11 ? 'netPay' : 'ignore' })), rows: [{ sourceRow: 1, cells: headers.map(value => ({ value })) }, { sourceRow: 2, cells: values.map((value, i) => ({ value, ...(formulas[i] ? { formulaText: formulas[i] } : {}) })) }], excludedSourceRows: [] }
  const batch = { id: 'a'.repeat(32), clientId: 16, clientName: 'Test client', month: '2026-09', rows: [{ id: 'row-2', sourceRow: 2, employeeName: 'Example Person', employeeCode: 'MNGL050', information: [] }] }
  const context = { source, components: [], employees: [], workWeeks: [], workLocations: [], roster: { rows: [{ rowId: 'row-2', status: 'Existing', employeeId: 1 }], missingEmployees: [] } }
  source.rows.push({ sourceRow: 50, cells: [{ value: 'TOTAL' }, { value: '' }, { value: '26000', formulaText: 'SUM(C2:C49)' }, { value: '3900', formulaText: 'SUM(D2:D49)' }] })
  const before = JSON.stringify(source)
  const { request } = proposeExcelPayrollSetup(batch, context, ['row-2'])
  assert.equal(request.fixedPayrollDays, 26); assert.equal(request.payrollDaysColumnIndex, -1)
  assert.equal(request.sourceWorkLocationColumnIndex, 1)
  assert.equal(request.components.length, 4, 'total earning must not duplicate granular earnings')
  const byCode = new Map(request.components.map(c => [c.code, c]))
  assert.equal(byCode.get('BASIC').sourceColumnIndex, 2)
  assert.equal(byCode.get('DA').sourceColumnIndex, 3)
  assert.match(byCode.get('PF').rowFormulas['row-2'], /MAX\(BASIC_EARNED\+DA_EARNED,15000\)/)
  assert.match(byCode.get('PF').rowFormulas['row-2'], /MAX\(BASIC_EARNED\+DA_EARNED,25000\)/)
  assert.match(byCode.get('LWF_EMPLOYEE_SHARE').rowFormulas['row-2'], /ROUNDUP\(PAYABLE_DAYS\)/)
  assert.ok(request.components.every(c => !Object.values(c.rowFormulas || {}).includes('REVIEW_REQUIRED')))
  assert.equal(request.employeeDecisions[0].matchedEmployeeId, 1)
  assert.equal(JSON.stringify(source), before)
  const remapped = proposeExcelPayrollSetup(batch, context, ['row-2'], { 10: 'ignore' })
  assert.equal(remapped.request.components.length, 3)
})

test('component mapping, cohort template names and manual additions remain independent of the saved payslip', () => {
  const { batch, context, rowIds } = fixture(), before = JSON.stringify(batch)
  const first = proposeExcelPayrollSetup(batch, context, rowIds.slice(0, 2)).request
  const next = proposeExcelPayrollSetup(batch, context, rowIds.slice(2)).request
  assert.equal(first.templateName, next.templateName, 'source cohorts share the same reusable configuration name')
  assert.deepEqual(first.employeeDecisions.map(row => row.allowCreate), [false, false])
  assert.equal(JSON.stringify(batch), before)
})
