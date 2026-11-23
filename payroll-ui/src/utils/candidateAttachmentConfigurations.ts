import type { AttachmentFieldConfiguration, EntityAttachment } from '../types/payroll'

// Joining uploads reuse core configurations, including fields originally used
// by Employee or a joining form. Show their authorized files without adding a
// second empty checklist or changing the original form's upload controls.
export function candidateAttachmentConfigurations(base: AttachmentFieldConfiguration[], joining: AttachmentFieldConfiguration[], files: EntityAttachment[], clientId: number) {
  const configurations = new Map(base.map(field => [field.id, field]))
  const supplementalIds = new Set<number>()
  if (clientId <= 0) return { configurations: [...configurations.values()], supplementalIds }
  for (const field of joining) {
    if (configurations.has(field.id) || !field.isActive || ![0, clientId].includes(field.clientId)) continue
    if (!files.some(file => file.fieldConfigurationId === field.id && file.clientId === clientId && file.isCurrent !== false && file.isDeleted !== true)) continue
    configurations.set(field.id, field)
    supplementalIds.add(field.id)
  }
  return { configurations: [...configurations.values()], supplementalIds }
}
