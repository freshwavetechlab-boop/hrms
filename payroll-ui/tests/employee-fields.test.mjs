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
const fields = load('../src/utils/employeeFields.ts', { '../config/bulkImportDefinitions': definitions, './xlsx': xlsx, '../services/apiClient': {} })
const mapping = load('../src/utils/smartBulkImport.ts', { './xlsx': xlsx })
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
  assert.equal(cell('Portal Access'), 'FALSE')
  const definition = fields.employeeImportDefinition({ fields: [extra], forms: [], values: {} })
  const result = mapping.autoMapBulkImportColumns({ headers, rows: [row] }, definition)
  assert.equal(Object.keys(result).length, headers.length)
})
