import type { Structure } from '../types/payroll'
import { salaryTemplateClientIds, scopeSalaryTemplates } from './salaryTemplateContext'

const referenceId = (value: string | number | null | undefined) => String(value ?? '').split(':')[0].trim()

/** An employee can choose an active client/shared template, never another client's fallback. */
export function employeeSalaryTemplates(templates: Structure[], clientId: string | number): Structure[] {
  return scopeSalaryTemplates(templates, Number(referenceId(clientId))).filter(template => template.active !== false)
}

/** A missing assignment stays missing until an explicit template selection. */
export function assignedEmployeeSalaryTemplate(templates: Structure[], clientId: string | number, assignment: string | number | null | undefined): Structure | undefined {
  const id = referenceId(assignment)
  return id && id !== '0' ? employeeSalaryTemplates(templates, clientId).find(template => String(template.id) === id) : undefined
}

/** Keep globally available and authorized multi-client definitions visible in the template workspace. */
export function salaryTemplatesForVisibleClients(templates: Structure[], clients: Iterable<string | number>): Structure[] {
  const ids = new Set([...clients].map(value => Number(referenceId(value))).filter(value => Number.isSafeInteger(value) && value > 0))
  return templates.filter(template => /^0?$/.test(String(template.clientId ?? '').trim())
    || salaryTemplateClientIds(template.clientId).some(id => ids.has(id)))
}
