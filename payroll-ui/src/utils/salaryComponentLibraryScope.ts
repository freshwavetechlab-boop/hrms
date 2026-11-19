import type { Client, Component, Structure } from '../types/payroll'
import { salaryTemplateClientIds } from './salaryTemplateContext'

export type SalaryComponentLibraryScope = {
  primaryIds: Set<string>
  otherClientIds: Set<string>
  usageLabels: Map<string, string>
}

/**
 * Browse defaults only. Component masters remain global: template references
 * describe usage, not ownership, and never remove an already selected line.
 */
export function salaryComponentLibraryScope(
  components: readonly Pick<Component, 'id'>[],
  templates: readonly Pick<Structure, 'clientId' | 'lines'>[],
  clientId: number,
  clients: readonly Pick<Client, 'id' | 'name'>[],
  selectedComponentIds: readonly string[] = [],
): SalaryComponentLibraryScope {
  const usage = new Map<string, { clients: Set<number>; shared: boolean; unknown: boolean }>()
  for (const template of templates) {
    const clientIds = salaryTemplateClientIds(template.clientId)
    const shared = /^0?$/.test(String(template.clientId ?? '').trim())
    for (const line of template.lines) {
      const id = String(line.componentId)
      const entry = usage.get(id) ?? { clients: new Set<number>(), shared: false, unknown: false }
      clientIds.forEach(id => entry.clients.add(id))
      entry.shared ||= shared
      entry.unknown ||= !shared && clientIds.length === 0
      usage.set(id, entry)
    }
  }

  const names = new Map(clients.map(client => [client.id, client.name.trim() || `Client #${client.id}`]))
  const selected = new Set(selectedComponentIds.map(String))
  const result: SalaryComponentLibraryScope = { primaryIds: new Set(), otherClientIds: new Set(), usageLabels: new Map() }
  for (const component of components) {
    const id = String(component.id), entry = usage.get(id)
    const referencedClients = [...(entry?.clients ?? [])].sort((a, b) => a - b)
    const usedBy = referencedClients.length ? `Used by ${referencedClients.map(id => names.get(id) || `Client #${id}`).join(', ')}` : ''
    const exclusivelyOtherClients = !!entry && referencedClients.length > 0 && !entry.clients.has(clientId) && !entry.shared && !entry.unknown
    if (exclusivelyOtherClients) result.otherClientIds.add(id)
    if (!exclusivelyOtherClients || selected.has(id)) result.primaryIds.add(id)
    result.usageLabels.set(id, entry?.shared ? 'Shared template' : usedBy || (entry?.unknown ? 'Template usage needs review' : 'Shared master'))
  }
  return result
}
