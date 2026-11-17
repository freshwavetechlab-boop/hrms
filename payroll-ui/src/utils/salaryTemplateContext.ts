import type { Structure } from '../types/payroll'

/** Read the legacy client references without treating labels as client IDs. */
export function salaryTemplateClientIds(value: string | number | null | undefined): number[] {
  return [...new Set(String(value ?? '').split(/[;,|]/).map(part => Number(part.split(':')[0].trim()))
    .filter(id => Number.isSafeInteger(id) && id > 0))]
}

const validClientId = (clientId: number) => Number.isSafeInteger(clientId) && clientId > 0
const sharedTemplate = (template: Structure) => /^0?$/.test(String(template.clientId ?? '').trim())
const belongsToClient = (template: Structure, clientId: number) => salaryTemplateClientIds(template.clientId).includes(clientId)
const ownedByClient = (template: Structure, clientId: number) => {
  const ids = salaryTemplateClientIds(template.clientId)
  return ids.length === 1 && ids[0] === clientId
}

/** Retain authorized input order and objects; selecting a client never changes a template. */
export function scopeSalaryTemplates(templates: Structure[], clientId: number): Structure[] {
  return validClientId(clientId) ? templates.filter(template => sharedTemplate(template) || belongsToClient(template, clientId)) : []
}

/** Restore an explicit scoped choice; otherwise auto-open only an unambiguous owned template. */
export function preferredSalaryTemplate(templates: Structure[], clientId: number, rememberedId?: string | number | null): Structure | undefined {
  const scoped = scopeSalaryTemplates(templates, clientId)
  if (rememberedId !== undefined && rememberedId !== null && String(rememberedId).trim()) {
    const remembered = scoped.find(template => String(template.id) === String(rememberedId))
    if (remembered) return remembered
  }
  const owned = scoped.filter(template => ownedByClient(template, clientId))
  const active = owned.filter(template => template.active)
  return active.length === 1 ? active[0] : active.length > 1 ? undefined : owned.length === 1 ? owned[0] : undefined
}

/** Shared and multi-client records become a local draft, never a mutation of their saved ID. */
export function templateForClient(template: Structure, clientId: number): Structure | undefined {
  if (!validClientId(clientId) || !sharedTemplate(template) && !belongsToClient(template, clientId)) return undefined
  if (ownedByClient(template, clientId)) return template
  return { ...template, id: 0, clientId: String(clientId), lines: template.lines.map(line => ({ ...line })) }
}
