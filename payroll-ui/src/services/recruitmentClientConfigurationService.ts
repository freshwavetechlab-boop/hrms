import { getJsonResult } from './apiClient'

export type RecruitmentClientConfigurationSummary = { clientId: number; hasConfiguration: boolean }

export const getRecruitmentClientConfigurationSummary = (signal?: AbortSignal) =>
  getJsonResult<RecruitmentClientConfigurationSummary[]>('/api/recruitment/client-configuration-summary', [], { signal, toast: false, loader: false })
