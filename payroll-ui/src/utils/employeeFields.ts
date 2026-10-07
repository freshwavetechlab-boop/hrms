import type { EmployeeAttributeField, EmployeeAttributeForm } from '../types/employeeAttributes'
import { employeeBulkImportDefinition } from '../config/bulkImportDefinitions'
import type { BulkImportDefinition, BulkImportFieldType } from './smartBulkImport'
import { downloadXlsx } from './xlsx'
import type { Employee, WorkLocation, WorkflowApprover, Structure } from '../types/payroll'
import { getJsonResult } from '../services/apiClient'

export type EmployeeFieldColumn = { code: string; header: string; infotypeCode: string; formDefinitionId: number; field: EmployeeAttributeField }
export type EmployeeFieldExchange = { forms: EmployeeAttributeForm[]; fields: EmployeeFieldColumn[]; values: Record<number, Record<string, string>> }
export const emptyEmployeeFields: EmployeeFieldExchange = { forms: [], fields: [], values: {} }
export const employeeCoreFields: Record<string, string[]> = {
  '0001': ['Department', 'Designation', 'Grade', 'Employee Type', 'Employee Category', 'Work Location', 'Reporting Manager', 'Work Email', 'Portal Access'],
  '0002': ['First Name', 'Last Name', 'Gender', 'Date Of Birth', 'Mobile', 'PAN', 'Aadhaar', 'UAN Number', 'ESIC Number'],
  '0006': ['Address', 'Correspondence Address', 'Permanent Address'],
  '0008': ['Salary Template', 'Annual CTC'],
  '0009': ['Bank Name', 'Bank Account No', 'IFSC', 'Payment Mode'],
}
export async function getEmployeeFields(clientId: number, includeValues = true) {
  const result = await getJsonResult<EmployeeFieldExchange>(`/api/employees/field-configuration/catalog?clientId=${clientId}&includeValues=${includeValues}`, emptyEmployeeFields)
  if (!result.ok || !result.data) throw new Error(result.error || 'Employee field configuration could not be loaded.')
  return result.data
}
export function employeeImportDefinition(exchange: EmployeeFieldExchange): BulkImportDefinition {
  return { ...employeeBulkImportDefinition, fields: [{ code: 'EmployeeId', header: 'Employee ID', label: 'Employee ID', group: 'Identity', type: 'number', description: 'Leave blank for a new employee.' }, ...employeeBulkImportDefinition.fields.map(field => field.code === 'EmployeeCode' ? { ...field, aliases: field.aliases?.filter(alias => alias !== 'Employee ID') } : field),
    ...[{ code: 'WorkLocationId', header: 'Work Location Id', group: 'Employment', type: 'number' }, { code: 'ReportingManagerUserId', header: 'Reporting Manager User Id', group: 'Employment', type: 'number' }, { code: 'SalaryTemplateId', header: 'Salary Template Id', group: 'Payroll', type: 'text' }, { code: 'SalaryJson', header: 'Salary Json', group: 'Payroll', type: 'text' }, { code: 'City', header: 'City', group: 'Address', type: 'text' }, { code: 'District', header: 'District', group: 'Address', type: 'text' }, { code: 'State', header: 'State', group: 'Address', type: 'text' }].map(field => ({ ...field, label: field.header, type: field.type as BulkImportFieldType })), ...exchange.fields.map(column => {
    const type: BulkImportFieldType = column.field.fieldTypeCode === 'NUMBER' ? 'number' : column.field.fieldTypeCode === 'DATE' ? 'date' : column.field.fieldTypeCode === 'EMAIL' ? 'email' : column.field.fieldTypeCode === 'CHECKBOX' ? 'boolean' : 'text'
    // A stable marker survives label changes; ambiguous labels are never auto-mapped.
    const labelUnique = exchange.fields.filter(other => other.field.label.toLowerCase() === column.field.label.toLowerCase()).length === 1
    return { code: column.code, header: column.header, label: column.field.label, group: `Info Type ${column.infotypeCode}`, type,
      required: column.field.isRequired, requiredForNew: true, aliases: [`[${column.code}]`, ...(labelUnique ? [column.field.label, ...(exchange.fields.filter(other => other.field.stableFieldCode === column.field.stableFieldCode).length === 1 ? [column.field.stableFieldCode] : [])] : [])],
      description: column.field.fieldTypeCode === 'MULTI_SELECT' ? 'Separate option codes with a semicolon.' : column.field.options.filter(option => option.isActive).map(option => option.optionCode).join(', ') }
  })] }
}

export function employeeFieldValue(row: Employee, code: string, locations: WorkLocation[], users: WorkflowApprover[], templates: Structure[]) {
  const personal = row.personalDetails, bank = row.paymentDetails
  const values: Record<string, unknown> = {
    EmployeeId: row.id || '', WorkLocationId: row.workLocationId || '', ReportingManagerUserId: row.reportingManagerUserId || '', SalaryTemplateId: row.salaryStructureId, SalaryJson: row.salaryJson, City: personal.city, District: personal.district, State: personal.state,
    EmployeeCode: row.employeeCode, FirstName: row.firstName, LastName: row.lastName, Gender: row.gender,
    DateOfBirth: personal.dateOfBirth, WorkEmail: row.workEmail, Mobile: personal.mobile, DateOfJoining: row.dateOfJoining,
    Department: row.department, Designation: row.designation, Grade: row.grade, EmploymentType: personal.employmentType, EmployeeCategory: personal.skillCategory,
    WorkLocation: locations.find(location => location.id === row.workLocationId)?.name, ReportingManagerEmail: users.find(user => user.id === row.reportingManagerUserId)?.email,
    PortalAccess: row.portalAccess ? 'TRUE' : 'FALSE', Active: row.isActive ? 'TRUE' : 'FALSE',
    SalaryTemplate: templates.find(template => String(template.id) === row.salaryStructureId)?.name, AnnualCtc: row.annualCtc,
    Pan: personal.panNumber, Aadhaar: personal.aadhaarNumber, UanNumber: personal.uanNumber, EsicNumber: personal.esicNumber,
    Address: personal.address, CorrespondenceAddress: personal.correspondenceAddress, PermanentAddress: personal.permanentAddress,
    BankName: bank.bankName, BankAccountNo: bank.bankAccountNo, Ifsc: bank.ifscCode, PaymentMode: bank.paymentMode, ChangeReason: '',
  }
  return String(values[code] ?? '')
}
export function exportCompleteEmployees(rows: Employee[], exchange: EmployeeFieldExchange, locations: WorkLocation[], users: WorkflowApprover[], templates: Structure[]) {
  const definition = employeeImportDefinition(exchange)
  downloadXlsx('employee-master.xlsx', [{ name: 'Employees', rows: [
    definition.fields.map(field => field.header),
    ...rows.map(row => definition.fields.map(field => exchange.values[row.id]?.[field.code] ?? employeeFieldValue(row, field.code, locations, users, templates))),
  ] }, { name: 'Instructions', rows: [['Edit the Employees sheet; choose Add new + update existing when uploading.'], ['For new employees leave Employee ID blank and supply a new Employee Code.'], ['Blank cells keep existing values. Keep CUSTOM markers in column headers.'], ['Documents are uploaded separately from the employee Documents tab.']] }])
}
