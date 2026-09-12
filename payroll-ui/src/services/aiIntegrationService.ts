import type { RecruitmentAiProviderPool, RecruitmentAiScoringSettings } from '../types/payroll'
import { deleteJson, getJson, postJson, putJson } from './apiClient'

export const emptyAiIntegration = (): RecruitmentAiScoringSettings => ({
  id: 0,
  clientId: 0,
  clientName: 'All Frevo',
  enableAiScoring: false,
  providerCode: 'Gemini',
  modelName: '',
  endpointUrl: '',
  aiBlendWeight: 30,
  minimumConfidence: .65,
  maximumResumeCharacters: 40000,
  requestTimeoutSeconds: 120,
  hasApiKey: false,
  apiKey: '',
  healthStatus: 'NotTested',
  lastHealthMessage: '',
  lastTestedAt: null,
  isActive: true,
  isPrimary: false,
  priority: 100,
  monthlyRequestLimit: 1000,
  usagePeriod: '',
  usageRequestCount: 0,
  usageInputTokens: 0,
  usageOutputTokens: 0,
  usagePercent: 0,
  lastUsedAt: null,
  consecutiveFailureCount: 0,
  lastFailureAt: null,
  providerRequestLimit: null,
  providerRequestsRemaining: null,
  providerTokenLimit: null,
  providerTokensRemaining: null,
  providerRequestReset: '',
  providerTokenReset: '',
  providerQuotaObservedAt: null,
})

export const emptyAiProviderPool = (): RecruitmentAiProviderPool => ({ autoSwitchEnabled: false, models: [] })

export const getAiIntegration = () => getJson<RecruitmentAiProviderPool>('/api/integrations/ai', emptyAiProviderPool())
export const saveAiIntegration = (row: RecruitmentAiScoringSettings) => postJson('/api/integrations/ai', row, null as RecruitmentAiScoringSettings | null, { successMessage: 'AI integration saved securely.' })
export const testAiIntegration = (id: number) => postJson(`/api/integrations/ai/${id}/test`, {}, null as RecruitmentAiScoringSettings | null, { successMessage: 'AI provider connection is healthy.', timeoutMs: 120000 })
export const activateAiIntegration = (id: number) => postJson(`/api/integrations/ai/${id}/activate`, {}, emptyAiProviderPool(), { successMessage: 'Active AI model updated.' })
export const updateAiAutoSwitch = (autoSwitchEnabled: boolean) => putJson('/api/integrations/ai/auto-switch', { autoSwitchEnabled }, emptyAiProviderPool())
export const deleteAiIntegration = (id: number) => deleteJson(`/api/integrations/ai/${id}`, null, { successMessage: 'AI model and encrypted API key deleted.' })
