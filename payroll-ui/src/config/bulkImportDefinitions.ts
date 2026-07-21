import type { BulkImportDefinition, BulkImportFieldDefinition } from '../utils/smartBulkImport'

const field = (code: string, header: string, group: string, type: BulkImportFieldDefinition['type'], aliases: string[] = [], extra: Partial<BulkImportFieldDefinition> = {}): BulkImportFieldDefinition => ({
  code,
  header,
  label: header,
  group,
  type,
  aliases,
  ...extra
})

export const employeeBulkImportDefinition: BulkImportDefinition = {
  moduleCode: 'EMPLOYEE',
  moduleLabel: 'Employee master',
  targetSheetName: 'Employees',
  fields: [
    field('EmployeeCode', 'Employee Code', 'Identity', 'text', ['Employee ID', 'Employee No', 'Employee Number', 'Emp Code', 'Emp ID', 'Staff ID', 'Staff No', 'Staff Number', 'Staff Ref', 'Personnel Number'], { required: true, description: 'Unique employee identifier. Existing codes update the employee.' }),
    field('FirstName', 'First Name', 'Identity', 'text', ['Given Name', 'Forename']),
    field('LastName', 'Last Name', 'Identity', 'text', ['Surname', 'Family Name']),
    field('Gender', 'Gender', 'Identity', 'lookup', ['Sex']),
    field('DateOfBirth', 'Date Of Birth', 'Identity', 'date', ['DOB', 'Birth Date']),
    field('WorkEmail', 'Work Email', 'Identity', 'email', ['Email', 'Email Address', 'Office Email', 'Office Mail', 'Official Email', 'Company Email']),
    field('Mobile', 'Mobile', 'Identity', 'text', ['Phone', 'Phone Number', 'Mobile Number', 'Contact Number']),

    field('DateOfJoining', 'Date Of Joining', 'Employment', 'date', ['DOJ', 'Joining Date', 'Hire Date', 'Start Date']),
    field('Department', 'Department', 'Employment', 'lookup', ['Dept', 'Business Department']),
    field('Designation', 'Designation', 'Employment', 'lookup', ['Job Title', 'Position', 'Role Title']),
    field('Grade', 'Grade', 'Employment', 'lookup', ['Employee Grade', 'Band', 'Level']),
    field('WorkLocation', 'Work Location', 'Employment', 'lookup', ['Location', 'Office Location', 'Workplace', 'Branch']),
    field('ReportingManagerEmail', 'Reporting Manager Email', 'Employment', 'email', ['Manager Email', 'Supervisor Email', 'Reporting To Email']),
    field('PortalAccess', 'Portal Access', 'Employment', 'boolean', ['ESS Access', 'Login Allowed', 'Portal Login'], { defaultValue: 'TRUE' }),
    field('Active', 'Active', 'Employment', 'boolean', ['Is Active', 'Employment Status', 'Employee Status', 'Status'], { defaultValue: 'TRUE' }),

    field('SalaryTemplate', 'Salary Template', 'Payroll', 'lookup', ['Pay Template', 'Salary Structure', 'Pay Structure']),
    field('AnnualCtc', 'Annual CTC', 'Payroll', 'number', ['CTC', 'Annual Salary', 'Yearly CTC', 'Annual Compensation']),

    field('Pan', 'PAN', 'Statutory', 'text', ['PAN Number', 'Permanent Account Number']),
    field('Aadhaar', 'Aadhaar', 'Statutory', 'text', ['Aadhaar Number', 'Aadhar', 'Aadhar Number', 'UID']),
    field('UanNumber', 'UAN Number', 'Statutory', 'text', ['UAN', 'PF UAN']),
    field('EsicNumber', 'ESIC Number', 'Statutory', 'text', ['ESIC', 'ESI Number']),

    field('Address', 'Address', 'Address', 'text', ['Current Address', 'Residential Address']),
    field('CorrespondenceAddress', 'Correspondence Address', 'Address', 'text', ['Mailing Address', 'Communication Address']),
    field('PermanentAddress', 'Permanent Address', 'Address', 'text', ['Home Address']),

    field('BankName', 'Bank Name', 'Bank', 'text', ['Bank']),
    field('BankAccountNo', 'Bank Account No', 'Bank', 'text', ['Bank Account', 'Account Number', 'Account No']),
    field('Ifsc', 'IFSC', 'Bank', 'text', ['IFSC Code', 'Bank IFSC']),
    field('PaymentMode', 'Payment Mode', 'Bank', 'lookup', ['Salary Payment Mode', 'Pay Mode']),

    field('ChangeReason', 'Change Reason', 'Audit', 'text', ['Reason', 'Import Reason'], { defaultValue: 'Smart spreadsheet import' })
  ]
}
