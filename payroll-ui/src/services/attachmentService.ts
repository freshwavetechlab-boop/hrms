import type { AttachmentAccessTicket, AttachmentAttribute, AttachmentFieldConfiguration, AttachmentStorageHealthResult, AttachmentStorageServer, AttachmentTargetOption, EntityAttachment, GoogleDriveConnectStart, GoogleDriveSetup } from '../types/payroll'
import { apiRequest, apiUrl, deleteJson, getJson, postEmpty, postForm, postFormWithProgress, postJson } from './apiClient'

export const getAttachmentTargets = () => getJson<AttachmentTargetOption[]>('/api/attachment-targets', [])
export const getAttachmentAttributes = (clientId?: number) => getJson<AttachmentAttribute[]>(`/api/attachment-attributes${clientId == null ? '' : `?clientId=${clientId}`}`, [])
export const saveAttachmentAttribute = (row: AttachmentAttribute) => postJson('/api/attachment-attributes', row, row, { successMessage: 'Attachment attribute saved.' })
export const getAttachmentConfigurations = (clientId?: number) => getJson<AttachmentFieldConfiguration[]>(`/api/attachment-configurations${clientId == null ? '' : `?clientId=${clientId}`}`, [])
export const getEffectiveAttachmentConfigurations = (clientId: number, moduleCode: string, formCode: string) => getJson<AttachmentFieldConfiguration[]>(`/api/attachment-configurations/effective?${new URLSearchParams({ clientId: String(clientId), moduleCode, formCode })}`, [])
export const saveAttachmentConfiguration = (row: AttachmentFieldConfiguration) => postJson('/api/attachment-configurations', row, row, { successMessage: 'Attachment field configuration saved.' })
export const getAttachmentStorageServers = () => getJson<AttachmentStorageServer[]>('/api/attachment-storage-servers', [])
export const saveAttachmentStorageServer = (row: AttachmentStorageServer) => postJson('/api/attachment-storage-servers', row, row, { successMessage: 'Attachment storage server saved.' })
export const testAttachmentStorageServer = (id: number) => postEmpty<AttachmentStorageHealthResult>(`/api/attachment-storage-servers/${id}/test`, { healthy: false, status: '', message: '' }, { toast: 'error-only' })
const googleDriveSetup0: GoogleDriveSetup = { storageServerId: null, googleOAuthConfigured: false, connectionStatus: 'Not configured', callbackUrl: '', googleCloudCredentialsUrl: '' }
export const getGoogleDriveSetup = () => getJson<GoogleDriveSetup>('/api/attachment-storage-servers/google/setup', googleDriveSetup0)
export const configureGoogleDrive = (credentialFile: File, storageServerId?: number | null) => {
  const body = new FormData()
  body.append('credentialFile', credentialFile)
  const query = storageServerId ? `?storageServerId=${encodeURIComponent(storageServerId)}` : ''
  return postForm<GoogleDriveSetup>(`/api/attachment-storage-servers/google/configure${query}`, body, googleDriveSetup0, { toast: 'error-only' })
}
export const connectGoogleDrive = () => postEmpty<GoogleDriveConnectStart>('/api/attachment-storage-servers/google/connect', { authorizationUrl: '' }, { toast: 'error-only' })

export const getEntityAttachments = (entityType: string, entityId: number) => getJson<EntityAttachment[]>(`/api/attachments?${new URLSearchParams({ entityType, entityId: String(entityId) })}`, [])
export const uploadEntityAttachment = (fieldConfigurationId: number, entityType: string, entityId: number, file: File, metadata: { documentNumber?: string; issueDate?: string; expiryDate?: string }, onProgress: (percent: number) => void) => {
  const body = new FormData()
  body.append('fieldConfigurationId', String(fieldConfigurationId))
  body.append('entityType', entityType)
  body.append('entityId', String(entityId))
  if (metadata.documentNumber) body.append('documentNumber', metadata.documentNumber)
  if (metadata.issueDate) body.append('issueDate', metadata.issueDate)
  if (metadata.expiryDate) body.append('expiryDate', metadata.expiryDate)
  body.append('file', file)
  return postFormWithProgress<EntityAttachment>('/api/attachments', body, {} as EntityAttachment, onProgress)
}
export const deleteEntityAttachment = (publicId: string) => deleteJson(`/api/attachments/${publicId}`, null, { toast: false })
export const verifyEntityAttachment = (publicId: string) => postEmpty<EntityAttachment | null>(`/api/attachments/${publicId}/verify`, null, { successMessage: 'Attachment verified.' })
export const rejectEntityAttachment = (publicId: string, reason: string) => postJson(`/api/attachments/${publicId}/reject`, { reason }, null as EntityAttachment | null, { successMessage: 'Attachment rejected.' })
export const issueAttachmentTicket = (publicId: string, purpose: 'Preview' | 'Download') => postJson(`/api/attachments/${publicId}/access-ticket`, { purpose }, null as AttachmentAccessTicket | null, { toast: false })
export const openAttachmentWithTicket = async (publicId: string, purpose: 'Preview' | 'Download') => {
  const previewWindow = purpose === 'Preview' ? window.open('', '_blank') : null
  if (previewWindow) previewWindow.opener = null
  const response = await issueAttachmentTicket(publicId, purpose)
  if (!response.ok || !response.data) {
    previewWindow?.close()
    return response
  }
  const target = apiUrl(response.data.url)
  if (purpose === 'Preview') {
    if (previewWindow) previewWindow.location.replace(target)
    else window.open(target, '_blank', 'noopener,noreferrer')
  }
  else {
    const anchor = document.createElement('a')
    anchor.href = target
    anchor.rel = 'noopener noreferrer'
    anchor.click()
  }
  return response
}
export const downloadAttachmentBlob = async (publicId: string) => {
  const response = await apiRequest(`/api/attachments/${publicId}/content?download=true`)
  return { ok: response.ok, blob: response.ok ? await response.blob() : null }
}
