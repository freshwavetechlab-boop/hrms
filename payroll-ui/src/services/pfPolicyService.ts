import { getJsonResult, postJson } from './apiClient'

export type PfContributionRule = {
  componentId: string
  ratePercent: number
  monthlyWageCeiling: number | null
  componentCode?: string
  statutoryType?: string
}

export type PfPolicyInput = {
  id?: string
  salaryStructureId: string
  name: string
  effectiveFrom: string
  reason: string
  baseComponentCode: string
  ceilingBasis: 'CalendarDays' | 'PayableDays'
  contributions: PfContributionRule[]
  edliMonthlyWageCeiling: number | null
}

export type PfPolicyVersion = PfPolicyInput & {
  id: string
  versionNumber: number
  clientId: number
  status: 'Draft' | 'Published'
  createdAtUtc: string
  publishedAtUtc: string | null
  createdBy: string
  publishedBy: string
}

const endpoint = (clientId: number) => `/api/clients/${clientId}/pf-policy-versions`

export const getPfPolicyVersions = (clientId: number) =>
  getJsonResult<PfPolicyVersion[]>(endpoint(clientId), [], { toast: false })

export const savePfPolicyVersion = (clientId: number, input: PfPolicyInput) =>
  postJson<PfPolicyInput, PfPolicyVersion | null>(endpoint(clientId), input, null, { toast: false })

export const publishPfPolicyVersion = (clientId: number, id: string, reason: string) =>
  postJson<{ reason: string }, PfPolicyVersion | null>(`${endpoint(clientId)}/${encodeURIComponent(id)}/publish`, { reason }, null, { toast: false })
