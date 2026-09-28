import type { RecruitmentResumePreview } from '../types/payroll'
import type { DynamicFormVersion } from '../types/recruitmentOrchestration'

const draftFields = {
  FIRST_NAME: 'firstName', LAST_NAME: 'lastName', EMAIL: 'email', PHONE: 'phone',
  CURRENT_LOCATION: 'address', CURRENT_COMPANY: 'currentCompany', CURRENT_DESIGNATION: 'currentTitle',
  TOTAL_EXPERIENCE_MONTHS: 'totalExperienceMonths', TOTAL_EXPERIENCE_YEARS: 'totalExperienceMonths',
  HIGHEST_QUALIFICATION: 'highestQualification', CURRENT_CTC: 'currentCtc', EXPECTED_CTC: 'expectedCtc',
  NOTICE_PERIOD_DAYS: 'noticePeriodDays', CERTIFICATIONS: 'certifications', SKILLS: 'skills',
} satisfies Record<string, keyof RecruitmentResumePreview>

export type CandidateDraftField = typeof draftFields[keyof typeof draftFields]

export function candidateDraftRequirements(form: DynamicFormVersion | null, requireAtsDetails: boolean) {
  const fields = new Map<CandidateDraftField, string>([['firstName', 'First name']])
  const additional: string[] = []
  for (const field of form?.sections.flatMap(section => section.fields) ?? []) {
    if (!field.isActive || !field.isRequired) continue
    const codes = (field.semanticCodes.length ? field.semanticCodes : [field.stableFieldCode]).map(code => code.toUpperCase())
    const key = codes.map(code => draftFields[code as keyof typeof draftFields]).find(Boolean)
    if (key && ['TEXT', 'TEXTAREA', 'EMAIL', 'PHONE', 'NUMBER'].includes(field.fieldTypeCode)) fields.set(key, field.label)
    else if (!(field.fieldTypeCode === 'UPLOAD' && codes.includes('RESUME') && (field.attachmentConstraints?.minimumFileCount ?? 1) <= 1)) additional.push(field.label)
  }
  if (requireAtsDetails) {
    if (!fields.has('currentTitle')) fields.set('currentTitle', 'Current designation')
    if (!fields.has('skills')) fields.set('skills', 'At least one skill')
  }
  return { fields, additional }
}

export function missingCandidateDraftFields(draft: RecruitmentResumePreview, fields: Map<CandidateDraftField, string>, requireIdentity: boolean) {
  const missing = [...fields].filter(([key]) => {
    const value = draft[key]
    return value == null || (typeof value === 'string' ? !value.trim() : Array.isArray(value) ? !value.some(item => item.trim()) : !Number.isFinite(value))
  }).map(([, label]) => label)
  if (requireIdentity && !fields.has('email') && !fields.has('phone') && !draft.email.trim() && !draft.phone.trim()) missing.push('Email or phone')
  return missing
}
