import type { RecruitmentPipelineStageAccessPolicy } from '../types/recruitmentOrchestration'

export const pipelineOwnershipLegend = [
  { ownerType: 'GAD', label: 'GAD team', color: '#6b4eff', description: 'Work owned by the GAD team' },
  { ownerType: 'Client', label: 'Client team', color: '#c9364f', description: 'Work owned by the client team' },
  { ownerType: 'Shared', label: 'Shared', color: '#2463b4', description: 'Joint GAD and client responsibility' },
  { ownerType: 'Unassigned', label: 'Owner not set', color: '#667085', description: 'Configure responsibility in Manage pipeline' },
] as const

// Ownership describes responsibility. The API separately enforces explicit
// role/user grants; a color must never be treated as an access permission.
export function pipelineStageOwnership(stage?: { accessPolicy?: RecruitmentPipelineStageAccessPolicy | null; stakeholderCode?: string }) {
  const policy = stage?.accessPolicy
  const owner = pipelineOwnershipLegend.find(row => row.ownerType === policy?.ownerType) ?? pipelineOwnershipLegend[3]
  const department = policy?.departmentLabel?.trim() || stage?.stakeholderCode?.trim()
  return { ...owner, label: department ? `${owner.label} · ${department}` : owner.label }
}
