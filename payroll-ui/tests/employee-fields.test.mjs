import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
let workbook
const load = (path, imports) => {
  const exports = {}
  new Function('exports', 'require', ts.transpileModule(read(path), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports, name => {
    assert.ok(name in imports, name)
    return imports[name]
  })
  return exports
}
const xlsx = { downloadXlsx: (_, sheets) => { workbook = sheets }, buildXlsxBlob: sheets => { workbook = sheets; return new Blob(['xlsx']) } }
const definitions = load('../src/config/bulkImportDefinitions.ts', {})
const ctc = load('../src/utils/employeeCtc.ts', {})
const fields = load('../src/utils/employeeFields.ts', { '../config/bulkImportDefinitions': definitions, './xlsx': xlsx, '../services/apiClient': {}, './employeeCtc': ctc })
const mapping = load('../src/utils/smartBulkImport.ts', { './xlsx': xlsx, './employeeCtc': ctc })
const column = (code = 'CASTE', label = 'Caste', form = 'PERSONAL') => ({ code: `CUSTOM:0002:${form}:${code}`, header: `${label} [CUSTOM:0002:${form}:${code}]`, infotypeCode: '0002', formDefinitionId: 1, field: { stableFieldCode: code, label, fieldTypeCode: 'TEXT', isRequired: true, options: [] } })

test('renamed custom column still maps by its stable marker and Employee ID is distinct from Employee Code', () => {
  const definition = fields.employeeImportDefinition({ fields: [column('CASTE', 'Community')], values: {}, forms: [] })
  const sheet = { headers: ['Employee ID', 'Employee Code', 'Old caste label [CUSTOM:0002:PERSONAL:CASTE]'], rows: [['15', 'E015', 'General']] }
  const result = mapping.autoMapBulkImportColumns(sheet, definition)
  assert.equal(result.EmployeeId, 0)
  assert.equal(result.EmployeeCode, 1)
  assert.equal(result['CUSTOM:0002:PERSONAL:CASTE'], 2)
  mapping.prepareMappedBulkImport(new File(['source'], 'source.xlsx'), sheet, definition, result, { operation: 'upsert' })
  const rows = workbook[0].rows
  assert.equal(rows[1][rows[0].indexOf('Employee ID')], '15')
  assert.equal(rows[1][rows[0].indexOf('Employee Code')], 'E015')
  assert.equal(rows[1][rows[0].findIndex(header => header.includes('[CUSTOM:'))], 'General')
  assert.ok(!rows[0].includes('Active'), 'unmapped defaults must not overwrite an existing employee')
})

test('ambiguous custom labels require explicit mapping', () => {
  const definition = fields.employeeImportDefinition({ fields: [column('CASTE', 'Caste', 'A'), column('CASTE', 'Caste', 'B')], values: {}, forms: [] })
  const result = mapping.autoMapBulkImportColumns({ headers: ['Caste'], rows: [['value']] }, definition)
  assert.deepEqual(result, {})
})

test('monthly and annual CTC mapping fills the missing amount per row and leaves a bare CTC unassigned', () => {
  const definition = definitions.employeeBulkImportDefinition
  const file = new File(['source'], 'monthly.xlsx')
  const headers = ['Employee Code', 'CTC', 'CTC per month', 'Annual CTC']
  const rows = [
    ['E1', 'unused', '833333333333333.33', ''], ['E2', '', '', '1.02'],
    ['E3', '', '', ''], ['E4', '', '0', ''], ['E5', '', '25', '101'],
  ]
  const sheet = { name: 'Source', headers, rows }
  const original = structuredClone(sheet)
  const mappings = mapping.autoMapBulkImportColumns(sheet, definition)
  assert.deepEqual(mappings, { EmployeeCode: 0, AnnualCtc: 3, MonthlyCtc: 2 })
  const context = { templateId: '81', effectiveFrom: '2026-10-01' }
  const result = mapping.prepareMappedBulkImport(file, sheet, definition, mappings, { operation: 'update', employeeSalaryContext: context })
  assert.deepEqual(result.outputHeaders, ['Employee Code', 'Annual CTC', 'Monthly CTC', 'Salary Template Id', 'Salary Effective From'])
  assert.deepEqual(workbook[0].rows.slice(1), [
    ['E1', '9999999999999999.96', '833333333333333.33', '81', '2026-10-01'],
    ['E2', '1.02', '0.09', '81', '2026-10-01'], ['E3', '', '', '', ''],
    ['E4', '0', '0', '81', '2026-10-01'], ['E5', '101', '25', '81', '2026-10-01'],
  ])
  const monthlyOnly = { EmployeeCode: 0, MonthlyCtc: 2 }
  assert.throws(() => mapping.prepareMappedBulkImport(file, sheet, definition, monthlyOnly, {
    operation: 'update', employeeSalaryContext: { ...context, effectiveFrom: '' },
  }), /effective date/)
  const monthlyResult = mapping.prepareMappedBulkImport(file, sheet, definition, monthlyOnly, { operation: 'update' })
  assert.equal(monthlyResult.columns.find(column => column.targetCode === 'AnnualCtc').sourceHeader, 'Monthly CTC × 12')
  assert.deepEqual(workbook[0].rows[1], ['E1', '9999999999999999.96', '833333333333333.33'])
  assert.deepEqual(sheet, original)
})

test('CTC validation retains conflicting inputs and invalid source cells, including outside dated updates', () => {
  const rule = load('../src/utils/salaryTemplateInput.ts', {})
  const { employeeSalaryImportIssues } = load('../src/utils/employeeSalaryImport.ts', { './salaryTemplateInput': rule, './employeeCtc': ctc })
  assert.deepEqual(ctc.resolveEmployeeCtc('1.02', '0.09'), { annual: '1.02', monthly: '0.09', issues: [] })
  assert.equal(ctc.resolveEmployeeCtc('1,00,000.00', '').monthly, '8333.33')
  assert.deepEqual(ctc.resolveEmployeeCtc('  ', ' '), { annual: '', monthly: '', issues: [] })
  assert.deepEqual(ctc.resolveEmployeeCtc('', '-0.00'), { annual: '0', monthly: '-0.00', issues: [] })
  const row = { 'Monthly CTC': '24', 'Annual CTC': '101' }
  assert.deepEqual(employeeSalaryImportIssues(Object.keys(row), row, 6), [
    { column: 'Monthly CTC', rowNumber: 6, message: 'Annual CTC divided by 12 gives Monthly CTC 8.42. Check both amounts.' },
  ])
  assert.equal(ctc.resolveEmployeeCtc('', '833333333333333.34').issues[0].column, 'Monthly CTC')
  for (const invalid of ['-1', '0.001', 'Rs 1', '1e5']) {
    const result = ctc.resolveEmployeeCtc('', invalid)
    assert.equal(result.monthly, invalid)
    assert.equal(result.annual, '')
    assert.equal(result.issues[0].column, 'Monthly CTC')
  }
})

test('bulk salary targets use the saved template input method and stable component identities', () => {
  const rule = load('../src/utils/salaryTemplateInput.ts', {})
  const { employeeSalaryInputFields } = load('../src/utils/employeeSalaryImport.ts', { './salaryTemplateInput': rule, './employeeCtc': ctc })
  const component = (id, code, calculationType, active = true) => ({ id, code, name: code, calculationType, active })
  const components = [component(1, 'BASIC', 'Formula'), component(2, 'HRA', 'Formula'), component(3, 'PF', 'Formula'),
    component(4, 'RATE', 'Daily Rate'), component(5, 'OTHER', 'Manual Entry', false), component(6, 'BALANCE', 'Balancing Amount'),
    component(7, 'WHOLE', 'Percentage of CTC'), component(8, 'UNRELATED', 'Fixed Amount'),
    component(9, 'gross_earned', 'Manual Entry'), component(10, 'NET_PAY', 'Fixed Amount'), component(11, 'EMPLOYER_COST', 'Formula')]
  components[0].formula = 'CTC * 0.5'
  const template = { active: true, id: 81, name: 'Rates', lines: [
    { componentId: '1', value: '0' }, { componentId: '2', formula: 'BASIC * 0.4' }, { componentId: '3', formula: 'ROUND(BASIC * 12%, 0)' },
    { componentId: '4', value: '100' }, { componentId: '5', value: '0' }, { componentId: '6', value: '0' }, { componentId: '7', value: '30' },
    { componentId: '9', value: '0' }, { componentId: '10', value: '0' }, { componentId: '11', value: '0' },
  ] }
  const salaryFields = employeeSalaryInputFields(template, components)
  assert.deepEqual(salaryFields.map(f => f.code), ['SALARY_INPUT:1', 'SALARY_INPUT:4'])
  assert.match(salaryFields[0].label, /Monthly amount/)
  assert.match(salaryFields[1].label, /Daily rate/)
  assert.deepEqual(employeeSalaryInputFields({ ...template, active: false }, components), [])
  const definition = { ...definitions.employeeBulkImportDefinition, fields: [...definitions.employeeBulkImportDefinition.fields, ...salaryFields] }
  const sheet = { headers: ['Employee Code', 'BASIC', 'BASIC', 'Old label [SALARY_INPUT:4]'], rows: [
    ['E01', '1500', '500', '0'], ['E02', '1800', '600', ''], ['E03', '2000', '800', '700.25'],
  ] }
  const mapped = mapping.autoMapBulkImportColumns(sheet, definition)
  assert.deepEqual(mapped, { EmployeeCode: 0, 'SALARY_INPUT:4': 3 })
  const file = new File([''], 'reference.csv'), employeeSalaryContext = { templateId: '81', effectiveFrom: '2026-10-01' }
  assert.throws(() => mapping.prepareMappedBulkImport(file, sheet, definition, mapped), /effective date/)
  assert.throws(() => mapping.prepareMappedBulkImport(file, sheet, definition, { ...mapped, SalaryJson: 1 }, { employeeSalaryContext }), /Salary Json/)
  mapping.prepareMappedBulkImport(file, sheet, definition, mapped, { operation: 'update', employeeSalaryContext })
  assert.deepEqual(workbook[0].rows, [
    ['Employee Code', 'RATE · Daily rate [SALARY_INPUT:4]', 'Salary Template Id', 'Salary Effective From'],
    ['E01', '0', '81', '2026-10-01'], ['E02', '', '', ''], ['E03', '700.25', '81', '2026-10-01'],
  ])
  const ctcSheet = { headers: ['Employee Code', 'Annual CTC'], rows: [['E01', '250000'], ['E02', ''], ['E03', '0']] }
  const ctcMapping = mapping.autoMapBulkImportColumns(ctcSheet, definition)
  mapping.prepareMappedBulkImport(file, ctcSheet, definition, ctcMapping, { operation: 'update', employeeSalaryContext })
  assert.deepEqual(workbook[0].rows, [
    ['Employee Code', 'Annual CTC', 'Monthly CTC', 'Salary Template Id', 'Salary Effective From'],
    ['E01', '250000', '20833.33', '81', '2026-10-01'], ['E02', '', '', '', ''], ['E03', '0', '0', '81', '2026-10-01'],
  ])
  mapping.prepareMappedBulkImport(file, ctcSheet, definition, ctcMapping, { operation: 'update' })
  assert.deepEqual(workbook[0].rows, [
    ['Employee Code', 'Annual CTC', 'Monthly CTC'], ['E01', '250000', '20833.33'], ['E02', '', ''], ['E03', '0', '0'],
  ])
  for (const operation of ['insert', 'upsert']) {
    mapping.prepareMappedBulkImport(file, ctcSheet, definition, ctcMapping, { operation, employeeSalaryContext })
    assert.deepEqual(workbook[0].rows[2].slice(-2), ['81', '2026-10-01'])
    mapping.prepareMappedBulkImport(file, ctcSheet, definition, { EmployeeCode: 0 }, { operation, employeeSalaryContext })
    assert.deepEqual(workbook[0].rows[1].slice(-2), ['81', '2026-10-01'])
  }
})

test('complete employee export includes core values, salary breakdown and custom values in an importable sheet', () => {
  const employee = { id: 15, employeeCode: 'E015', firstName: 'Existing', lastName: '', salaryStructureId: '7', salaryJson: '{"basic":12000}', annualCtc: 300000, isActive: true, portalAccess: false, personalDetails: { city: 'Delhi', mobile: '9000000000' }, paymentDetails: { bankAccountNo: '001234567' } }
  const extra = column()
  fields.exportCompleteEmployees([employee], { forms: [], fields: [extra], values: { 15: { [extra.code]: 'General' } } }, [], [], [{ id: '7', name: 'Standard' }])
  const [headers, row] = workbook[0].rows
  const cell = header => row[headers.indexOf(header)]
  assert.equal(workbook[0].name, 'Employees')
  assert.equal(headers.filter(header => header === 'Employee ID').length, 1)
  assert.equal(cell('Employee ID'), '15'); assert.equal(cell('Employee Code'), 'E015')
  assert.equal(cell(extra.header), 'General'); assert.equal(cell('City'), 'Delhi')
  assert.equal(cell('Salary Json'), employee.salaryJson); assert.equal(cell('Bank Account No'), '001234567')
  assert.equal(cell('Monthly CTC'), '25000')
  assert.equal(cell('Portal Access'), 'FALSE')
  const definition = fields.employeeImportDefinition({ fields: [extra], forms: [], values: {} })
  const result = mapping.autoMapBulkImportColumns({ headers, rows: [row] }, definition)
  assert.equal(Object.keys(result).length, headers.length)
})

test('edited salary cells are validated using dated input and decimal storage rules', () => {
  const rule = load('../src/utils/salaryTemplateInput.ts', {})
  const { employeeSalaryImportIssues } = load('../src/utils/employeeSalaryImport.ts', { './salaryTemplateInput': rule, './employeeCtc': ctc })
  const salary = 'Basic [SALARY_INPUT:1]'
  const row = { [salary]: '0', 'Annual CTC': '9999999999999999.99', 'Salary Template Id': '81', 'Salary Effective From': '2028-02-29' }
  const errors = patch => employeeSalaryImportIssues(Object.keys(row), { ...row, ...patch }, 5).map(issue => issue.column)
  assert.deepEqual(errors({}), [])
  for (const value of ['-1', '0.00001', '1e5', '2,000', '1000000000.0001']) assert.deepEqual(errors({ [salary]: value }), [salary])
  assert.deepEqual(errors({ [salary]: '1000000000.00000' }), [])
  const rate = 'Rate · Daily rate [SALARY_INPUT:4]'
  const rateErrors = value => employeeSalaryImportIssues([rate, ...Object.keys(row)], { ...row, [rate]: value }, 5).map(issue => issue.column)
  for (const value of ['-1', '0.00001']) assert.deepEqual(rateErrors(value), [rate])
  for (const value of ['', '0', '-0', '+0.0000', '0.0001', '1000000000']) assert.deepEqual(rateErrors(value), [])
  assert.deepEqual(employeeSalaryImportIssues(Object.keys(row), { ...row, [salary]: '', 'Annual CTC': '' }, 5, true), [])
  assert.deepEqual(errors({ 'Annual CTC': '10000000000000000.00' }), ['Annual CTC'])
  assert.deepEqual(errors({ 'Annual CTC': '-1.00' }), ['Annual CTC'])
  assert.deepEqual(errors({ 'Annual CTC': '+0.0100' }), [])
  assert.deepEqual(errors({ 'Annual CTC': '1.001' }), ['Annual CTC'])
  assert.deepEqual(errors({ 'Salary Effective From': '2026-02-29' }), ['Salary Effective From'])
  assert.deepEqual(errors({ 'Salary Effective From': '0999-12-31' }), ['Salary Effective From'])
  assert.deepEqual(errors({ 'Salary Effective From': '1000-01-01' }), [])
  assert.deepEqual(errors({ [salary]: '', 'Annual CTC': '', 'Salary Template Id': '', 'Salary Effective From': '' }), [])
  assert.deepEqual(errors({ 'Salary Json': '{}' }), ['Salary Json'])
  assert.deepEqual(employeeSalaryImportIssues(['Annual CTC'], { 'Annual CTC': '2345' }, 5), [])
})
