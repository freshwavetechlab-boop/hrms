import { apiRequest, apiUrl, deleteJson, getJson, postFormWithProgress, postJson, putJson, readError } from './apiClient'
import type {
  CandidateActionDecision,
  DynamicFormDefinition,
  DynamicFormVersion,
  DynamicLookupOption,
  PublicApplicationSession,
  PublicCandidateActionContext,
  PublicFormValue,
  PublicRecruitmentJob,
  PublicUploadedFile,
  RecruitmentApplicationStageInstance,
  RecruitmentCandidateActionSession,
  RecruitmentJobDescriptionVersion,
  RecruitmentJobPosting,
  RecruitmentOrchestrationLookups,
  RecruitmentPipelineBoard,
  RecruitmentPipelineDefinition,
  RecruitmentPipelineDetail,
  RecruitmentPipelineTransition,
  RecruitmentPipelineTransitionResult,
  RecruitmentPipelineVersion,
  RecruitmentPositionPipelineAssignment,
  AssignRecruitmentPipelineRequest,
  SaveDynamicFormDefinition,
  SaveRecruitmentJobDescriptionVersion,
  SaveRecruitmentJobPosting,
  SaveRecruitmentPipelineDefinition,
  StartPublicApplicationRequest,
} from '../types/recruitmentOrchestration'

const internalBase = '/api/recruitment-orchestration'
const publicBase = '/api/public/recruitment'

const emptyLookups: RecruitmentOrchestrationLookups = {
  lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [],
}

export const getRecruitmentOrchestrationLookups = async (clientId = 0) => {
  const response = await getJson<RecruitmentOrchestrationLookups>(`${internalBase}/lookups?clientId=${clientId}`, emptyLookups)
  return {
    ...response,
    workflows: (response.workflows ?? []).filter(row => row.isActive && (row.clientId == null || row.clientId === clientId)),
    attachmentFieldConfigurations: response.attachmentConfigurations ?? [],
    atsProfiles: (response.atsScoringProfiles ?? []).map(row => ({ id: row.id, profileCode: row.positionCategory || `PROFILE_${row.id}`, profileName: row.profileName, isActive: row.isActive })),
  }
}

export const getRecruitmentForms = (clientId = 0) =>
  getJson<DynamicFormDefinition[]>(`${internalBase}/forms?clientId=${clientId}`, [])

export const getRecruitmentForm = (id: number) =>
  getJson<DynamicFormDefinition | null>(`${internalBase}/forms/${id}`, null)

export const saveRecruitmentFormDefinition = (definition: SaveDynamicFormDefinition) =>
  postJson(`${internalBase}/forms`, definition, null as DynamicFormDefinition | null, { successMessage: 'Form definition saved.' })

export const deleteRecruitmentFormDefinition = (id: number) =>
  deleteJson(`${internalBase}/forms/${id}`, null, { successMessage: 'Form definition deleted.' })

export const saveRecruitmentFormVersion = (definitionId: number, version: DynamicFormVersion) =>
  postJson(`${internalBase}/forms/${definitionId}/versions`, {
    id: version.id,
    formDefinitionId: definitionId,
    sections: version.sections,
  }, null as DynamicFormVersion | null, { successMessage: 'Form draft saved.' })

export const publishRecruitmentFormVersion = (versionId: number) =>
  postJson(`${internalBase}/form-versions/${versionId}/publish`, {}, null as DynamicFormVersion | null, { successMessage: 'Form version published.' })

export const loadInternalSelectOptions = (sourceCode: string, search: string, clientId: number) => {
  const query = new URLSearchParams({ clientId: String(clientId), search })
  return getJson<DynamicLookupOption[]>(`${internalBase}/lookups/${encodeURIComponent(sourceCode)}/options?${query}`, [])
}

export const getRecruitmentPipelines = (clientId = 0) =>
  getJson<RecruitmentPipelineDefinition[]>(`${internalBase}/pipelines?clientId=${clientId}`, [])

export const saveRecruitmentInterviewCompetency = (request: { id: number; clientId: number; competencyCode: string; competencyName: string; description: string; isActive: boolean }) =>
  postJson(`${internalBase}/interview-competencies`, request, null as { id: number; clientId: number; competencyCode: string; competencyName: string; description: string; isActive: boolean } | null, { successMessage: 'Interview score component saved.' })

export const getRecruitmentPipeline = (id: number) =>
  getJson<RecruitmentPipelineDetail | null>(`${internalBase}/pipelines/${id}`, null)

export const getRecruitmentPipelineVersions = (definitionId: number) =>
  getJson<RecruitmentPipelineVersion[]>(`${internalBase}/pipelines/${definitionId}/versions`, [])

export const getRecruitmentPipelineVersion = (versionId: number) =>
  getJson<RecruitmentPipelineVersion | null>(`${internalBase}/pipeline-versions/${versionId}`, null)

export const saveRecruitmentPipelineDefinition = (definition: SaveRecruitmentPipelineDefinition) =>
  postJson(`${internalBase}/pipelines`, definition, null as RecruitmentPipelineDefinition | null, { successMessage: 'Pipeline definition saved.' })

export const deleteRecruitmentPipelineDefinition = (id: number) =>
  deleteJson(`${internalBase}/pipelines/${id}`, null, { successMessage: 'Pipeline definition deleted.' })

export const saveRecruitmentPipelineVersion = (definitionId: number, version: RecruitmentPipelineVersion) =>
  postJson(`${internalBase}/pipelines/${definitionId}/versions`, {
    id: version.id,
    pipelineDefinitionId: definitionId,
    scopeType: version.scopeType ?? 'Application',
    slaMode: version.slaMode ?? 'StageEntry',
    overallSlaMinutes: version.overallSlaMinutes ?? 0,
    stages: version.stages,
    transitions: version.transitions,
  }, null as RecruitmentPipelineVersion | null, { successMessage: 'Pipeline draft saved.' })

export const publishRecruitmentPipelineVersion = (versionId: number) =>
  postJson(`${internalBase}/pipeline-versions/${versionId}/publish`, {}, null as RecruitmentPipelineVersion | null, { successMessage: 'Pipeline version published.' })

export const getRecruitmentPipelineBoard = (positionId: number, jobPostingId?: number) => {
  const query = new URLSearchParams({ positionId: String(positionId) })
  if (jobPostingId) query.set('jobPostingId', String(jobPostingId))
  return getJson<RecruitmentPipelineBoard | null>(`${internalBase}/pipeline-board?${query}`, null)
}

export const getRecruitmentApplicationTransitions = (applicationId: number) =>
  getJson<RecruitmentPipelineTransition[]>(`${internalBase}/applications/${applicationId}/transitions`, [])

export const transitionRecruitmentApplication = (applicationId: number, transitionId: number, reason: string) =>
  postJson(`${internalBase}/applications/${applicationId}/transitions/${transitionId}`, { transitionId, reason }, null as RecruitmentPipelineTransitionResult | null, { successMessage: 'Candidate moved to the next stage.' })

export const pauseRecruitmentApplication = (applicationId: number, reason: string) =>
  postJson(`${internalBase}/applications/${applicationId}/pause`, { reason }, null as RecruitmentApplicationStageInstance | null, { successMessage: 'Pipeline SLA paused.' })

export const resumeRecruitmentApplication = (applicationId: number) =>
  postJson(`${internalBase}/applications/${applicationId}/resume`, {}, null as RecruitmentApplicationStageInstance | null, { successMessage: 'Pipeline SLA resumed.' })

export const getRecruitmentCandidateActions = (applicationId: number) =>
  getJson<RecruitmentCandidateActionSession[]>(`${internalBase}/applications/${applicationId}/candidate-actions`, [])

export const createCurrentStageCandidateAction = (applicationId: number) =>
  postJson(`${internalBase}/applications/${applicationId}/candidate-actions/current-stage`, {}, null as RecruitmentCandidateActionSession | null, { successMessage: 'Secure candidate action link generated.' })

export const revokeRecruitmentCandidateAction = (id: number) =>
  postJson(`${internalBase}/candidate-actions/${id}/revoke`, {}, null as unknown, { toast: 'error-only' })

export const getRecruitmentJobPostings = (clientId = 0) =>
  getJson<RecruitmentJobPosting[]>(`${internalBase}/job-postings${clientId > 0 ? `?clientId=${clientId}` : ''}`, [])

export const getRecruitmentJobPosting = (id: number) =>
  getJson<RecruitmentJobPosting | null>(`${internalBase}/job-postings/${id}`, null)

export const saveRecruitmentJobPosting = (posting: SaveRecruitmentJobPosting) =>
  postJson(`${internalBase}/job-postings`, posting, null as RecruitmentJobPosting | null, { successMessage: 'Job posting saved.' })

export const publishRecruitmentJobPosting = (id: number) =>
  postJson(`${internalBase}/job-postings/${id}/publish`, {}, null as RecruitmentJobPosting | null, { successMessage: 'Job is live on the public careers page.' })

export const closeRecruitmentJobPosting = (id: number) =>
  postJson(`${internalBase}/job-postings/${id}/close`, {}, null as unknown, { successMessage: 'Job posting closed.' })

export const deleteRecruitmentJobPosting = (id: number) =>
  deleteJson(`${internalBase}/job-postings/${id}`, null, { successMessage: 'Job posting deleted.' })

export const getRecruitmentJobDescriptions = (requisitionId: number) =>
  getJson<RecruitmentJobDescriptionVersion[]>(`${internalBase}/job-descriptions?requisitionId=${requisitionId}`, [])

export const getRecruitmentJobDescription = (id: number) =>
  getJson<RecruitmentJobDescriptionVersion | null>(`${internalBase}/job-descriptions/${id}`, null)

export const deleteRecruitmentJobDescription = (id: number) =>
  deleteJson(`${internalBase}/job-descriptions/${id}`, null, { successMessage: 'Job-description version deleted.' })

export const saveRecruitmentJobDescription = (description: SaveRecruitmentJobDescriptionVersion) =>
  postJson(`${internalBase}/job-descriptions`, description, null as RecruitmentJobDescriptionVersion | null, { successMessage: 'Job-description draft saved.' })

export const submitRecruitmentJobDescription = (id: number, workflowId: number) =>
  postJson(`${internalBase}/job-descriptions/${id}/submit?workflowId=${workflowId}`, {}, null as RecruitmentJobDescriptionVersion | null, { successMessage: 'Job description sent for approval.' })

export const assignRecruitmentPipeline = (request: AssignRecruitmentPipelineRequest) =>
  postJson(`${internalBase}/pipeline-assignments`, request, null as RecruitmentPositionPipelineAssignment | null, { successMessage: 'Hiring pipeline assigned.' })

export const getRecruitmentPositionPipelineAssignment = (positionId: number) =>
  getJson<RecruitmentPositionPipelineAssignment | null>(`${internalBase}/pipeline-assignments/${positionId}`, null)

export const getPublicCareerJob = (slug: string) =>
  getJson<PublicRecruitmentJob | null>(`${publicBase}/jobs/${encodeURIComponent(slug)}`, null)

export const createPublicApplicationSession = (slug: string, request: StartPublicApplicationRequest) =>
  postJson(`${publicBase}/jobs/${encodeURIComponent(slug)}/sessions`, request, null as PublicApplicationSession | null, { toast: false, loader: false })

export const savePublicApplicationValues = (token: string, values: PublicFormValue[]) =>
  putJson(`${publicBase}/sessions/${encodeURIComponent(token)}/values`, { values }, null as unknown)

export const uploadPublicApplicationFile = (token: string, fieldId: number, file: File, onProgress: (percent: number) => void) => {
  const body = new FormData()
  body.append('file', file)
  return postFormWithProgress<PublicUploadedFile>(`${publicBase}/sessions/${encodeURIComponent(token)}/files/${fieldId}`, body, null as unknown as PublicUploadedFile, onProgress)
}

export const submitPublicApplication = (token: string) =>
  postJson(`${publicBase}/sessions/${encodeURIComponent(token)}/submit`, {}, null as { applicationCode: string; message: string } | null, { toast: false, loader: false })

export const getPublicCandidateAction = async (token: string) => {
  const row = await getJson<PublicCandidateActionContext | null>(`${publicBase}/actions/${encodeURIComponent(token)}`, null)
  if (!row) return null
  const purpose = row.purposeCode === 'OFFER_RESPONSE' ? 'OfferResponse' : row.purposeCode === 'DOCUMENT_REQUEST' ? 'DocumentRequest' : 'ProfileUpdate'
  const offer = row.offer?.documentUrl ? { ...row.offer, documentUrl: apiUrl(row.offer.documentUrl) } : row.offer
  return { ...row, offer, purpose, message: row.message || row.instructions || '' } as PublicCandidateActionContext
}

export const savePublicCandidateActionValues = (token: string, values: PublicFormValue[]) =>
  putJson(`${publicBase}/actions/${encodeURIComponent(token)}/values`, { values }, null as unknown)

export const uploadPublicCandidateActionFile = (token: string, fieldId: number, file: File, onProgress: (percent: number) => void) => {
  const body = new FormData()
  body.append('file', file)
  return postFormWithProgress<PublicUploadedFile>(`${publicBase}/actions/${encodeURIComponent(token)}/files/${fieldId}`, body, null as unknown as PublicUploadedFile, onProgress)
}

export const completePublicCandidateAction = (token: string, values: PublicFormValue[], decision?: CandidateActionDecision, remarks = '') =>
  postJson(`${publicBase}/actions/${encodeURIComponent(token)}`, { values, decision, remarks }, null as { status: string; message: string } | null, { toast: false, loader: false })

export async function loadPublicCandidateActionOptions(token: string, fieldId: number, search: string) {
  const query = new URLSearchParams({ search })
  try {
    const response = await apiRequest(`${publicBase}/actions/${encodeURIComponent(token)}/fields/${fieldId}/options?${query}`, { loader: false })
    if (!response.ok) return [] as DynamicLookupOption[]
    return await response.json() as DynamicLookupOption[]
  } catch {
    return [] as DynamicLookupOption[]
  }
}

export async function loadPublicSelectOptions(sessionToken: string, fieldId: number, search: string) {
  const query = new URLSearchParams({ search })
  try {
    const response = await apiRequest(`${publicBase}/sessions/${encodeURIComponent(sessionToken)}/fields/${fieldId}/options?${query}`, { loader: false })
    if (!response.ok) return [] as DynamicLookupOption[]
    return await response.json() as DynamicLookupOption[]
  } catch {
    return [] as DynamicLookupOption[]
  }
}

export async function publicRequestError(response: Response) {
  return response.ok ? '' : await readError(response)
}
