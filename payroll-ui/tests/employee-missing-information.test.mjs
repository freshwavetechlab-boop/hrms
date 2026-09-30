import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const source = readFileSync(new URL('../src/utils/employeeMissingInformation.ts', import.meta.url), 'utf8')
const exports = {}
new Function('exports', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText)(exports)
const { employeeMissingInformation: missing } = exports
const employee = {
  employeeCode: 'EMP1', firstName: 'Example', dateOfJoining: '2026-01-01', department: 'Engineering', designation: 'Analyst', workLocationId: 1,
  salaryStructureId: '1', annualCtc: 100000, portalAccess: false,
  personalDetails: { employmentType: 'Contract', skillCategory: 'Skilled', dateOfBirth: '1990-01-01', mobile: '9999999999', correspondenceAddress: 'Example address' },
  paymentDetails: { paymentMode: 'Bank Transfer', bankName: 'Example bank', bankAccountNo: '12345', ifscCode: 'EXAMPLE123' },
}
const config = { id: 1, attachmentAttributeId: 10, fieldLabel: 'ID proof', isActive: true, isRequired: true, minimumFileCount: 1 }
const file = { attachmentAttributeId: 10, verificationStatus: 'Pending', isCurrent: true, isDeleted: false }

test('bank transfer identifies exact missing fields; cash and cheque do not require bank details', () => {
  assert.deepEqual(missing(employee), [])
  const issues = missing({ ...employee, paymentDetails: { paymentMode: 'Bank Transfer', bankName: 'Example bank' } })
  assert.deepEqual(issues, [{ label: 'Bank details', infotype: '0009', fields: ['Account number', 'IFSC'] }])
  for (const paymentMode of ['Cash', 'Cheque']) assert.deepEqual(missing({ ...employee, paymentDetails: { paymentMode } }), [])
})

test('portal contact and other missing values route to the corresponding infotype', () => {
  const issues = missing({ ...employee, portalAccess: true, department: ' ', annualCtc: 0, personalDetails: { ...employee.personalDetails, mobile: '', correspondenceAddress: '' } })
  assert.deepEqual(issues.map(issue => [issue.infotype, issue.fields]), [['0001', ['Department', 'Work email']], ['0002', ['Mobile']], ['0006', ['Address']], ['0008', ['Annual CTC']]])
  assert.ok(!issues.some(issue => issue.fields.includes('PAN') || issue.fields.includes('ESIC')))
})

test('document checks count current files, required minimums and replacements without duplicating form configurations', () => {
  const configurations = [config, { ...config, id: 2 }, { ...config, id: 3, attachmentAttributeId: 20, fieldLabel: 'Optional document', isRequired: false }]
  const check = attachments => missing(employee, { configurations, attachments }, '2026-09-30')
  assert.deepEqual(check([]), [{ label: 'Documents', infotype: 'DOCS', fields: ['ID proof'] }])
  assert.deepEqual(check([file]), []) // Pending verification is uploaded, not missing.
  for (const change of [{ isDeleted: true }, { isCurrent: false }]) assert.equal(check([{ ...file, ...change }])[0].fields[0], 'ID proof')
  for (const change of [{ verificationStatus: 'Rejected' }, { expiryDate: '2026-09-29' }]) assert.match(check([{ ...file, ...change }])[0].fields[0], /replacement needed/)
  assert.deepEqual(check([{ ...file, expiryDate: '2026-09-30' }]), [])
  assert.deepEqual(missing(employee, { configurations: [{ ...config, minimumFileCount: 2 }], attachments: [file] })[0].fields, ['ID proof (1/2)'])
})

test('unavailable metadata is not reported as missing documents', () => {
  assert.deepEqual(missing(employee, null), [])
  assert.deepEqual(missing(employee, { configurations: [], attachments: [] }), [])
})
