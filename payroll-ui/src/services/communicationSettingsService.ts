import { api, getJson, postEmpty, postJson } from './apiClient'

export type CommunicationChannel = 'Email' | 'Sms' | 'WhatsApp'

export type CommunicationProviderAccount = {
  id: number
  clientId?: number | null
  clientName: string
  channel: Exclude<CommunicationChannel, 'Email'>
  providerCode: string
  accountName: string
  baseUrl: string
  apiVersion: string
  senderId: string
  phoneNumberId: string
  businessAccountId: string
  defaultCountryCode: string
  defaultLanguageCode: string
  requestTimeoutSeconds: number
  maximumMessagesPerMinute: number
  isEnabled: boolean
  deliveryPaused: boolean
  healthStatus: string
  lastHealthMessage: string
  lastTestedAtUtc?: string | null
  hasApiKey: boolean
  hasAccessToken: boolean
  hasWebhookSecret: boolean
  webhookPath?: string
}

export type SaveCommunicationProviderAccount = CommunicationProviderAccount & {
  apiKey: string
  accessToken: string
  webhookSecret: string
}

export type CommunicationTemplateVariable = {
  id: number
  templateId: number
  position: number
  variableKey: string
  label: string
  sourceCode: string
  isRequired: boolean
  fallbackValue: string
}

export type CommunicationTemplate = {
  id: number
  clientId?: number | null
  clientName: string
  channel: CommunicationChannel
  code: string
  name: string
  subjectTemplate: string
  bodyTemplate: string
  providerTemplateCode: string
  languageCode: string
  isHtml: boolean
  isActive: boolean
  createdAtUtc?: string | null
  updatedAtUtc?: string | null
  variables: CommunicationTemplateVariable[]
}

function query(values: Record<string, string | number | null | undefined>) {
  const params = new URLSearchParams()
  Object.entries(values).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value))
  })
  const value = params.toString()
  return value ? `?${value}` : ''
}

export const getCommunicationProviders = (clientId?: number | null) =>
  getJson<CommunicationProviderAccount[]>(`/api/communication-settings/providers${query({ clientId })}`, [])

export const saveCommunicationProvider = (provider: SaveCommunicationProviderAccount) =>
  postJson('/api/communication-settings/providers', provider, provider, { successMessage: `${provider.channel} connection saved securely.` })

export const testCommunicationProvider = (id: number) =>
  postEmpty(`/api/communication-settings/providers/${id}/test`, null, { successMessage: 'Connection test completed.' })

export const getCommunicationTemplates = (clientId?: number | null, channel?: CommunicationChannel) =>
  getJson<CommunicationTemplate[]>(`/api/communication-settings/templates${query({ clientId, channel })}`, [])

export const saveCommunicationTemplate = (template: CommunicationTemplate) =>
  postJson('/api/communication-settings/templates', template, template, { successMessage: 'Employee communication template saved.' })

export function communicationWebhookUrl(provider: CommunicationProviderAccount) {
  return provider.webhookPath ? `${api.replace(/\/$/, '')}/${provider.webhookPath.replace(/^\//, '')}` : ''
}
