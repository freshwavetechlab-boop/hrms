import type { RecruitmentJobDescriptionVersion } from '../types/recruitmentOrchestration'

export const jdTextLimits = {
  title: { label: 'Public role title', limit: 180, bytes: false },
  summary: { label: 'Role summary', limit: 65535, bytes: true },
  rolePurpose: { label: 'Role purpose', limit: 65535, bytes: true },
  responsibilityText: { label: 'Responsibility', limit: 65535, bytes: true },
  skillName: { label: 'Skill', limit: 180, bytes: false },
  minimumProficiency: { label: 'Reviewer level', limit: 80, bytes: false },
  qualificationName: { label: 'Qualification', limit: 65535, bytes: true },
  specialization: { label: 'Specialization', limit: 180, bytes: false },
  certificationName: { label: 'Certification', limit: 65535, bytes: true },
  languageName: { label: 'Language', limit: 120, bytes: false },
  proficiency: { label: 'Language proficiency', limit: 80, bytes: false },
  benefitName: { label: 'Benefit', limit: 180, bytes: false },
  description: { label: 'Benefit description', limit: 1000, bytes: false },
} as const

export type JdTextField = keyof typeof jdTextLimits
export type JdLengthSection = 'role' | 'responsibilities' | 'skills' | 'qualifications' | 'additional'

export function jdTextLength(field: JdTextField, value: string) {
  const definition = jdTextLimits[field]
  const text = (value || '').trim()
  const count = definition.bytes ? new TextEncoder().encode(text).length : Array.from(text).length
  const unit = definition.bytes ? 'UTF-8 bytes' : 'characters'
  const action = field === 'qualificationName' ? 'Split the full eligibility wording across qualification rows.'
    : field === 'specialization' ? 'Move the full eligibility wording into Qualification.' : 'Review this field before saving.'
  return {
    count, limit: definition.limit,
    counter: `${count.toLocaleString('en-IN')} / ${definition.limit.toLocaleString('en-IN')} ${unit}`,
    error: count > definition.limit ? `${definition.label} exceeds ${definition.limit.toLocaleString('en-IN')} ${unit} (${count.toLocaleString('en-IN')} entered). ${action} All entered text is retained.` : '',
  }
}

export function jdFieldLengthIssues(row: Pick<RecruitmentJobDescriptionVersion, 'title' | 'summary' | 'rolePurpose' | 'responsibilities' | 'skills' | 'qualifications' | 'certifications' | 'languages' | 'benefits'>) {
  const issues: { section: JdLengthSection; field: JdTextField; message: string }[] = []
  const check = (section: JdLengthSection, field: JdTextField, value: string, prefix = '') => {
    const state = jdTextLength(field, value)
    if (state.error) issues.push({ section, field, message: `${prefix}${state.error}` })
  }
  check('role', 'title', row.title); check('role', 'summary', row.summary); check('role', 'rolePurpose', row.rolePurpose)
  row.responsibilities.forEach((item, index) => check('responsibilities', 'responsibilityText', item.responsibilityText, `Responsibility row ${index + 1}: `))
  row.skills.forEach((item, index) => { check('skills', 'skillName', item.skillName, `Skill row ${index + 1}: `); check('skills', 'minimumProficiency', item.minimumProficiency, `Skill row ${index + 1}: `) })
  row.qualifications.forEach((item, index) => { check('qualifications', 'qualificationName', item.qualificationName, `Qualification row ${index + 1}: `); check('qualifications', 'specialization', item.specialization, `Qualification row ${index + 1}: `) })
  row.certifications.forEach((item, index) => check('additional', 'certificationName', item.certificationName, `Certification row ${index + 1}: `))
  row.languages.forEach((item, index) => { check('additional', 'languageName', item.languageName, `Language row ${index + 1}: `); check('additional', 'proficiency', item.proficiency, `Language row ${index + 1}: `) })
  row.benefits.forEach((item, index) => { check('additional', 'benefitName', item.benefitName, `Benefit row ${index + 1}: `); check('additional', 'description', item.description, `Benefit row ${index + 1}: `) })
  return issues
}
