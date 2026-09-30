import type { AttachmentFieldConfiguration, Employee, EntityAttachment } from '../types/payroll'

export type EmployeeInformationIssue = { label: string; fields: string[]; infotype: '0001' | '0002' | '0006' | '0008' | '0009' | 'DOCS' }
export type EmployeeDocumentCheck = { configurations: AttachmentFieldConfiguration[]; attachments: EntityAttachment[] }

export function employeeMissingInformation(employee: Employee, documents?: EmployeeDocumentCheck | null, today = new Date().toISOString().slice(0, 10)): EmployeeInformationIssue[] {
  const personal = employee.personalDetails, payment = employee.paymentDetails
  const issues: EmployeeInformationIssue[] = []
  const group = (label: string, infotype: EmployeeInformationIssue['infotype'], values: [string, unknown][]) => {
    const fields = values.filter(([, value]) => !String(value ?? '').trim()).map(([field]) => field)
    if (fields.length) issues.push({ label, infotype, fields })
  }
  group('Employment', '0001', [['Employee code', employee.employeeCode], ['Employee type', personal?.employmentType], ['Category', personal?.skillCategory], ['Department', employee.department], ['Designation', employee.designation], ['Work location', employee.workLocationId > 0 || ''], ...(employee.portalAccess ? [['Work email', employee.workEmail] as [string, unknown]] : [])])
  group('Personal details', '0002', [['Name', employee.firstName], ['Joining date', employee.dateOfJoining], ['Date of birth', personal?.dateOfBirth], ['Mobile', personal?.mobile]])
  group('Address', '0006', [['Address', personal?.address || personal?.correspondenceAddress || personal?.permanentAddress]])
  group('Salary setup', '0008', [['Salary template', employee.salaryStructureId], ['Annual CTC', employee.annualCtc > 0 || '']])
  const needsBank = !['cash', 'cheque'].includes(payment?.paymentMode?.trim().toLowerCase())
  group('Bank details', '0009', [['Payment mode', payment?.paymentMode], ...(needsBank ? [['Bank name', payment?.bankName], ['Account number', payment?.bankAccountNo], ['IFSC', payment?.ifscCode]] as [string, unknown][] : [])])
  if (documents) {
    const fields: string[] = []
    const required = new Map<number, AttachmentFieldConfiguration>()
    for (const config of documents.configurations.filter(item => item.isActive && item.isRequired)) {
      const previous = required.get(config.attachmentAttributeId)
      if (!previous || config.minimumFileCount > previous.minimumFileCount) required.set(config.attachmentAttributeId, config)
    }
    for (const config of required.values()) {
      const files = documents.attachments.filter(file => file.attachmentAttributeId === config.attachmentAttributeId && file.isDeleted !== true && file.isCurrent !== false)
      const valid = files.filter(file => file.verificationStatus?.toLowerCase() !== 'rejected' && (!file.expiryDate || file.expiryDate.slice(0, 10) >= today))
      const minimum = Math.max(1, config.minimumFileCount || 0)
      if (valid.length < minimum) fields.push(`${config.fieldLabel || config.attributeName}${files.length > valid.length ? ' (replacement needed)' : minimum > 1 ? ` (${valid.length}/${minimum})` : ''}`)
    }
    if (fields.length) issues.push({ label: 'Documents', infotype: 'DOCS', fields })
  }
  return issues
}
