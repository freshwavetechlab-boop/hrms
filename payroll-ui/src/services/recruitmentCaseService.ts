import { deleteJson, getJson, postJson } from './apiClient'
import type { RecruitmentHiringCase, RecruitmentProcessDocument, RecruitmentProfileSubmissionBatch, RecruitmentWorkOrder, SaveRecruitmentProcessDocument, SaveRecruitmentWorkOrder } from '../types/recruitmentCases'

export const getRecruitmentWorkOrders = (clientId = 0, query = '') => {
  const params = new URLSearchParams()
  if (clientId) params.set('clientId', String(clientId))
  if (query.trim()) params.set('query', query.trim())
  return getJson<RecruitmentWorkOrder[]>(`/api/recruitment/work-orders${params.size ? `?${params}` : ''}`, [])
}

export const getRecruitmentWorkOrder = (id: number) =>
  getJson<RecruitmentWorkOrder | null>(`/api/recruitment/work-orders/${id}`, null)

export const saveRecruitmentWorkOrder = (request: SaveRecruitmentWorkOrder) =>
  postJson('/api/recruitment/work-orders', request, null as RecruitmentWorkOrder | null, { successMessage: request.id ? 'Work order updated.' : 'Work order created.' })

export const deleteRecruitmentWorkOrder = (id: number) =>
  deleteJson(`/api/recruitment/work-orders/${id}`, null, { successMessage: 'Work order deleted.' })

export const getRecruitmentHiringCases = (clientId = 0) =>
  getJson<RecruitmentHiringCase[]>(`/api/recruitment/hiring-cases${clientId ? `?clientId=${clientId}` : ''}`, [])

export const getRecruitmentHiringCase = (id: number) =>
  getJson<RecruitmentHiringCase | null>(`/api/recruitment/hiring-cases/${id}`, null)

export const deleteRecruitmentHiringCase = (id: number) =>
  deleteJson(`/api/recruitment/hiring-cases/${id}`, null, { successMessage: 'Live cumulative pipeline case deleted.' })

export const startRecruitmentHiringCase = (workOrderLineId: number, pipelineVersionId: number) =>
  postJson('/api/recruitment/hiring-cases/start', { workOrderLineId, pipelineVersionId }, null as RecruitmentHiringCase | null, { successMessage: 'Hiring case and cumulative SLA started.' })

export const advanceRecruitmentHiringCase = (id: number, reason: string) =>
  postJson(`/api/recruitment/hiring-cases/${id}/advance`, { outcomeCode: 'ADVANCE', reason }, null as RecruitmentHiringCase | null)

export const pauseRecruitmentHiringCase = (id: number, reason: string) =>
  postJson(`/api/recruitment/hiring-cases/${id}/pause`, { reason }, null as RecruitmentHiringCase | null, { successMessage: 'Hiring SLA paused with an audit reason.' })

export const resumeRecruitmentHiringCase = (id: number) =>
  postJson(`/api/recruitment/hiring-cases/${id}/resume`, {}, null as RecruitmentHiringCase | null, { successMessage: 'Hiring SLA resumed.' })

export const getRecruitmentProcessDocuments = (hiringCaseId?: number | null, applicationId?: number | null) => {
  const params = new URLSearchParams()
  if (hiringCaseId) params.set('hiringCaseId', String(hiringCaseId))
  if (applicationId) params.set('applicationId', String(applicationId))
  return getJson<RecruitmentProcessDocument[]>(`/api/recruitment/process-documents${params.size ? `?${params}` : ''}`, [])
}

export const saveRecruitmentProcessDocument = (request: SaveRecruitmentProcessDocument) =>
  postJson('/api/recruitment/process-documents', request, null as RecruitmentProcessDocument | null, { successMessage: request.id ? 'Process document updated.' : 'Process document prepared.' })

export const generateRecruitmentProcessDocument = (id: number) =>
  postJson(`/api/recruitment/process-documents/${id}/generate`, {}, null as RecruitmentProcessDocument | null, { successMessage: 'Process document generated and stored securely.' })

export const getRecruitmentProfileBatches = (hiringCaseId: number) =>
  getJson<RecruitmentProfileSubmissionBatch[]>(`/api/recruitment/profile-submission-batches?hiringCaseId=${hiringCaseId}`, [])

export const createRecruitmentProfileBatch = (hiringCaseId: number, applicationIds: number[]) =>
  postJson('/api/recruitment/profile-submission-batches', { hiringCaseId, applicationIds }, null as RecruitmentProfileSubmissionBatch | null, { successMessage: 'Draft shortlist batch created.' })

export const approveRecruitmentProfileBatch = (id: number) =>
  postJson(`/api/recruitment/profile-submission-batches/${id}/approve`, {}, null as RecruitmentProfileSubmissionBatch | null, { successMessage: 'Shortlist batch approved.' })

export const forwardRecruitmentProfileBatch = (id: number) =>
  postJson(`/api/recruitment/profile-submission-batches/${id}/forward`, {}, null as RecruitmentProfileSubmissionBatch | null, { successMessage: 'Approved profiles queued for the configured recipients.' })
