import type { RecruitmentAiScoringSettings } from '../types/payroll'

export const canMakeAiModelActive = (row: RecruitmentAiScoringSettings) =>
  row.providerCode === 'LocalOpenAICompatible'
    ? row.id > 0 && row.hasApiKey && row.credentialStatus === 'Ready'
    : row.enableAiScoring && row.isActive

// Reuse the audited, transactional save endpoint: enabling and selecting the local
// model must not leave it available for fallback after a separate activation fails.
// Empty apiKey keeps the encrypted credential already held by the backend.
export const localPrimaryModel = (row: RecruitmentAiScoringSettings): RecruitmentAiScoringSettings | null =>
  row.providerCode === 'LocalOpenAICompatible' && canMakeAiModelActive(row)
    ? { ...row, apiKey: '', enableAiScoring: true, isActive: true, isPrimary: true }
    : null

// Presentation only. Keep the exact connection URL in the restricted edit form.
export const aiModelEndpointLabel = (row: RecruitmentAiScoringSettings) =>
  row.providerCode === 'LocalOpenAICompatible' ? '' : row.endpointUrl
