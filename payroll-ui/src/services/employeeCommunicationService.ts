import { getJsonResult, postJson } from './apiClient'
import type {
  CommunicationChannel,
  CommunicationTemplate,
  EmployeeCommunicationCampaign,
  EmployeeCommunicationCampaignPage,
  EmployeeCommunicationDraft,
  EmployeeCommunicationPreview,
  EmployeeCommunicationRecipientResult,
  EmployeeCommunicationSelection,
  EmployeeConversationDetail,
  EmployeeConversationPage,
} from '../types/employeeCommunication'

const recipientsFallback: EmployeeCommunicationRecipientResult = { items: [], total: 0, emailReadyCount: 0, mobileReadyCount: 0 }
const campaignPageFallback: EmployeeCommunicationCampaignPage = { items: [], total: 0, page: 1, pageSize: 25 }
const conversationPageFallback: EmployeeConversationPage = { items: [], total: 0 }

const queryString = (values: Record<string, string | number | undefined | null>) => {
  const params = new URLSearchParams()
  Object.entries(values).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value))
  })
  return params.toString()
}

export const getCommunicationRecipients = (filters: {
  clientId: number
  search?: string
  workLocationId?: number
  department?: string
  designation?: string
  limit?: number
}) => getJsonResult<EmployeeCommunicationRecipientResult>(`/api/employee-communications/recipients?${queryString(filters)}`, recipientsFallback, { toast: false })

export const getEmployeeCommunicationTemplates = (clientId: number, channel: CommunicationChannel) =>
  getJsonResult<CommunicationTemplate[]>(`/api/communication-settings/templates?${queryString({ clientId, channel })}`, [], { toast: false })

export const createEmployeeCommunicationDraft = (clientId: number, channel: CommunicationChannel) =>
  postJson<{ clientId: number; channel: CommunicationChannel }, EmployeeCommunicationDraft | null>('/api/employee-communications/drafts', { clientId, channel }, null, { toast: false })

export const previewEmployeeCommunication = (request: EmployeeCommunicationSelection) =>
  postJson<EmployeeCommunicationSelection, EmployeeCommunicationPreview | null>('/api/employee-communications/preview', request, null, { toast: false })

export const sendEmployeeCommunication = (request: EmployeeCommunicationSelection & { idempotencyKey: string }) =>
  postJson<typeof request, EmployeeCommunicationCampaign | null>('/api/employee-communications/send', request, null, { toast: false, timeoutMs: 120000 })

export const getEmployeeCommunicationCampaigns = (filters: {
  clientId?: number
  channel?: CommunicationChannel | ''
  status?: string
  search?: string
  page?: number
  pageSize?: number
}) => getJsonResult<EmployeeCommunicationCampaignPage>(`/api/employee-communications/campaigns?${queryString(filters)}`, campaignPageFallback, { toast: false })

export const getEmployeeCommunicationCampaign = (id: number) =>
  getJsonResult<EmployeeCommunicationCampaign | null>(`/api/employee-communications/campaigns/${id}`, null, { toast: false })

export const retryFailedEmployeeCommunication = (id: number) =>
  postJson<Record<string, never>, EmployeeCommunicationCampaign | null>(`/api/employee-communications/campaigns/${id}/retry-failed`, {}, null, { toast: false, successMessage: 'Failed deliveries queued for retry.' })

export const getEmployeeConversations = (filters: {
  clientId?: number
  channel?: CommunicationChannel | ''
  status?: string
  search?: string
}) => getJsonResult<EmployeeConversationPage>(`/api/employee-communications/conversations?${queryString(filters)}`, conversationPageFallback, { toast: false })

export const getEmployeeConversation = (id: number) =>
  getJsonResult<EmployeeConversationDetail | null>(`/api/employee-communications/conversations/${id}`, null, { toast: false })

export const replyToEmployeeConversation = (id: number, request: { body: string; templateId?: number | null; draftId?: number | null; idempotencyKey: string }) =>
  postJson<typeof request, EmployeeConversationDetail | null>(`/api/employee-communications/conversations/${id}/reply`, request, null, { toast: false, timeoutMs: 120000 })
