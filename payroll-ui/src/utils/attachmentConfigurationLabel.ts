import type { AttachmentFieldConfiguration } from '../types/payroll'

const words = (value: string) => value.replace(/[_-]+/g, ' ').trim().toLowerCase().replace(/\b\w/g, letter => letter.toUpperCase())

// Labels are presentation only. Stored module/form codes and configuration IDs
// remain the exact references used by published forms and pipeline stages.
export function attachmentFormLabel(moduleCode: string, formCode: string) {
  const form = formCode.trim().toUpperCase()
  const known: Record<string, string> = {
    EMPLOYEE_CREATE_EDIT: 'Employee Master', EMPLOYEE_PROFILE: 'Employee Profile', EMPLOYEE_COMMUNICATION: 'Employee Communication',
    EMPLOYEE_REFERRAL: 'Employee Referral', CANDIDATE_APPLICATION: 'Candidate Application', PUBLIC_CANDIDATE_APPLICATION: 'Public Candidate Application',
    PRE_ONBOARDING: 'Pre-onboarding documents', DOCUMENT_REQUEST: 'Requested documents', WORK_ORDER: 'Work order and JD',
    HIRING_REQUEST: 'Hiring request', PROCESS_DOCUMENT: 'Process documents', PROCESS_SUPPORT: 'Supporting documents',
  }
  if (known[form]) return known[form]
  if (/^(?:[A-Z0-9]+_)*JOIN(?:ING)?(?:_DOCUMENTS?)?$/.test(form)) return 'Joining documents'
  return words(formCode) || words(moduleCode) || 'Documents'
}

export function attachmentTargetLabel(moduleCode: string, formCode: string) {
  const module = ({ EMPLOYEE: 'Employees', RECRUITMENT: 'Recruitment', PAYROLL: 'Payroll' } as Record<string, string>)[moduleCode.toUpperCase()] || words(moduleCode)
  return [module, attachmentFormLabel(moduleCode, formCode)].filter(Boolean).join(' / ')
}

export function attachmentConfigurationLabel(row: Pick<AttachmentFieldConfiguration, 'attributeName' | 'fieldLabel' | 'attributeCode' | 'moduleCode' | 'formCode' | 'clientId' | 'maximumFileSizeBytes'> & { clientName?: string }) {
  const name = (row.attributeName || row.fieldLabel || row.attributeCode || 'Document').trim()
  const limit = row.maximumFileSizeBytes > 0 ? row.maximumFileSizeBytes >= 1048576
    ? `${Number((row.maximumFileSizeBytes / 1048576).toFixed(2))} MB` : `${Math.ceil(row.maximumFileSizeBytes / 1024)} KB` : ''
  const scope = row.clientId > 0 ? row.clientName || 'Client override' : ''
  return `${name} — ${[attachmentFormLabel(row.moduleCode, row.formCode), scope, limit].filter(Boolean).join(' · ')}`
}
