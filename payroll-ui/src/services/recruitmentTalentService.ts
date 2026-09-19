import type { ConvertCandidateToEmployeeRequest, Employee, EntityAttachment, PersonActivityEvent, RecruitmentAiScoringSettings, RecruitmentApplicationScore, RecruitmentAtsScoringCriterion, RecruitmentAtsScoringProfile, RecruitmentCandidate, RecruitmentCandidateApplication, RecruitmentCandidateCertification, RecruitmentCandidateChecklistItem, RecruitmentCandidateDetail, RecruitmentCandidateEducation, RecruitmentCandidateExperience, RecruitmentInterview, RecruitmentInterviewFeedback, RecruitmentInterviewSchedulingContext, RecruitmentOffer, RecruitmentOpenPosition, RecruitmentResumeIntakeItem, RecruitmentResumeIntakeResult, RecruitmentResumePreview, RecruitmentSkill, RecruitmentTalentDashboard, RecruitmentTalentPoolMatchRunResult, SaveRecruitmentCandidate, SaveRecruitmentInterviewFeedbackCompetencyScore } from '../types/payroll'
import { deleteJson, getJson, getJsonResult, postFormWithProgress, postJson, putJson, type ApiResult } from './apiClient'
import { toast } from '../components/ToastProvider'

export const getTalentDashboard = (clientId = 0) => getJson<RecruitmentTalentDashboard>(`/api/recruitment/talent/dashboard${clientId ? `?clientId=${clientId}` : ''}`, { talentProfiles: 0, activeApplications: 0, interviewsScheduled: 0, offersPending: 0, preOnboardingPending: 0, joined: 0 })
export const getCandidates = (query = '', status = '', clientId?: number) => {
  const search = new URLSearchParams({ query, status }); if (clientId) search.set('clientId', String(clientId))
  return getJson<RecruitmentCandidate[]>(`/api/recruitment/candidates?${search}`, [])
}
export const getCandidate = (id: number) => getJson<RecruitmentCandidateDetail | null>(`/api/recruitment/candidates/${id}`, null)
export const saveCandidate = (row: SaveRecruitmentCandidate) => postJson('/api/recruitment/candidates', row, null as RecruitmentCandidate | null, { successMessage: 'Talent profile saved.' })
export const deleteCandidate = (id: number) => deleteJson(`/api/recruitment/candidates/${id}`, null, { successMessage: 'Candidate and safe test-stage recruitment data deleted.' })
export const saveCandidateProfileSections = (candidateId: number, row: { experience: RecruitmentCandidateExperience[]; education: RecruitmentCandidateEducation[]; certifications: RecruitmentCandidateCertification[] }) => putJson(`/api/recruitment/candidates/${candidateId}/profile-sections`, row, null as RecruitmentCandidateDetail | null)
export const getApplications = (filters: { positionId?: number; candidateId?: number; stage?: string } = {}) => {
  const search = new URLSearchParams(); Object.entries(filters).forEach(([key, value]) => { if (value != null && value !== '') search.set(key, String(value)) })
  return getJson<RecruitmentCandidateApplication[]>(`/api/recruitment/applications?${search}`, [])
}
export const createApplication = (row: { candidateId: number; positionId: number; sourceType: string; resumeId?: number | null; recruiterUserId?: number | null }) => postJson('/api/recruitment/applications', row, null as RecruitmentCandidateApplication | null, { successMessage: 'Application created.' })
export const deleteApplication = (id: number) => deleteJson(`/api/recruitment/applications/${id}`, null, { successMessage: 'Application deleted. An orphaned candidate profile and stored resume were also purged.' })
export const changeApplicationStage = (id: number, stage: string, reason: string) => postJson(`/api/recruitment/applications/${id}/stage`, { stage, status: stage, reason }, null as RecruitmentCandidateApplication | null, { successMessage: 'Candidate stage updated.' })
const scoringRequests = new Map<number, Promise<ApiResult<null>>>()
export function scoreApplication(id: number): Promise<ApiResult<null>> {
  const running = scoringRequests.get(id)
  if (running) return running
  const work = (async (): Promise<ApiResult<null>> => {
    const queued = await postJson(`/api/recruitment/applications/${id}/score?background=true`, {}, null as { jobId: number } | null, { loader: false, toast: 'error-only' })
    if (!queued.ok || !queued.data) return { ...queued, data: null }
    toast.info('ATS request saved. Scoring is running in the background; you can keep working.')
    const path = `/api/recruitment/applications/${id}/score-jobs/${queued.data.jobId}`
    for (let attempt = 0; attempt < 90; attempt++) {
      await new Promise(resolve => window.setTimeout(resolve, 2000))
      const result = await getJsonResult(path, null as { status: string; lastError: string } | null, { loader: false, toast: false, timeoutMs: 8000 })
      if (!result.ok) {
        toast.warning('ATS request is saved, but its status could not be refreshed. Check the application before retrying.')
        return { ...result, data: null }
      }
      if (result.data?.status === 'Completed') {
        toast.success('ATS score recalculated. Pipeline automation continues in the background.')
        return { ok: true, data: null, error: '', status: 200 }
      }
      if (result.data?.status === 'Failed') {
        const error = result.data.lastError || 'ATS scoring failed. Review the resume and scoring configuration.'
        toast.error(error)
        return { ok: false, data: null, error, status: 422 }
      }
      if (result.data?.status === 'Retry') {
        toast.warning('ATS could not finish this attempt. The saved job will retry automatically; check the application status shortly.')
        return { ok: true, data: null, error: '', status: 202 }
      }
    }
    toast.info('ATS is still running in the background. The request is saved; refresh later to see its result.')
    return { ok: true, data: null, error: '', status: 202 }
  })().finally(() => scoringRequests.delete(id))
  scoringRequests.set(id, work)
  return work
}
export const moveApplicationCandidateToGlobalTalentPool = (id: number) => postJson(`/api/recruitment/applications/${id}/global-talent-pool`, {}, null as RecruitmentCandidateApplication | null, { successMessage: 'Candidate moved to the Global Talent Pool.' })
export const moveApplicationsToGlobalTalentPool = (applicationIds: number[]) => postJson('/api/recruitment/applications/global-talent-pool', { applicationIds }, null as { moved: number; failed: number; errors: string[] } | null, { successMessage: 'Selected candidates moved to the Global Talent Pool.' })
export const overrideApplicationScore = (scoreId: number, score: number, reason: string) => postJson(`/api/recruitment/application-scores/${scoreId}/override`, { score, reason }, null as RecruitmentApplicationScore | null, { successMessage: 'ATS score override saved.' })
export const getInterviews = () => getJson<RecruitmentInterview[]>('/api/recruitment/interviews', [])
export const getInterviewSchedulingContext = (applicationId: number) => getJson<RecruitmentInterviewSchedulingContext | null>(`/api/recruitment/interviews/scheduling-context/${applicationId}`, null)
export const saveInterview = (row: Partial<RecruitmentInterview> & { applicationId: number; panelUserIds?: number[] }) => postJson('/api/recruitment/interviews', row, null as RecruitmentInterview | null, { successMessage: 'Interview saved.' })
export const sendInterviewInvite = (id: number) => postJson(`/api/recruitment/interviews/${id}/invite`, {}, { recipientCount: 0 }, { successMessage: 'Interview invite emailed to the candidate and panel.' })
export const saveInterviewBatchItem = (row: Partial<RecruitmentInterview> & { applicationId: number; panelUserIds?: number[] }) => postJson('/api/recruitment/interviews', row, null as RecruitmentInterview | null, { toast: 'error-only' })
export const sendInterviewInviteBatchItem = (id: number) => postJson(`/api/recruitment/interviews/${id}/invite`, {}, { recipientCount: 0 }, { toast: 'error-only' })
export const deleteInterview = (id: number) => deleteJson(`/api/recruitment/interviews/${id}`, null, { successMessage: 'Interview deleted.' })
export const getInterviewFeedback = (interviewId: number) => getJson<RecruitmentInterviewFeedback[]>(`/api/recruitment/interviews/${interviewId}/feedback`, [])
export const saveInterviewFeedback = (interviewId: number, row: { panelUserId: number; overallScore: number; recommendation: string; competencyScoresJson?: string; comments: string; competencyScores: SaveRecruitmentInterviewFeedbackCompetencyScore[] }) => postJson(`/api/recruitment/interviews/${interviewId}/feedback`, row, null as RecruitmentInterviewFeedback | null, { successMessage: 'Interview feedback saved.' })
export const getOffers = () => getJson<RecruitmentOffer[]>('/api/recruitment/offers', [])
export const saveOffer = (row: Partial<RecruitmentOffer> & { applicationId: number }) => postJson('/api/recruitment/offers', row, null as RecruitmentOffer | null, { successMessage: 'Offer saved.' })
export const deleteOffer = (id: number) => deleteJson(`/api/recruitment/offers/${id}`, null, { successMessage: 'Offer deleted.' })
export const generateOfferLetter = (id: number) => postJson(`/api/recruitment/offers/${id}/generate-letter`, {}, null as RecruitmentOffer | null, { successMessage: 'Offer letter generated and stored securely.' })
export const updateOfferStatus = (id: number, status: string, remarks = '') => postJson(`/api/recruitment/offers/${id}/status`, { status, remarks }, null as RecruitmentOffer | null, { successMessage: 'Offer status updated.' })
export const completeCandidateChecklistItem = (applicationId: number, itemId: number, attachmentPublicId?: string | null) => postJson(`/api/recruitment/applications/${applicationId}/checklist/${itemId}/complete`, { attachmentPublicId: attachmentPublicId || null }, null as RecruitmentCandidateChecklistItem | null, { successMessage: 'Pre-onboarding item completed.' })
export const convertCandidateToEmployee = (applicationId: number, row: ConvertCandidateToEmployeeRequest) => postJson(`/api/recruitment/applications/${applicationId}/convert-to-employee`, row, null as Employee | null, { successMessage: 'Candidate converted to employee.' })
export const uploadCandidateResume = (candidateId: number, fieldConfigurationId: number, file: File, metadata: { documentNumber?: string; issueDate?: string; expiryDate?: string }, onProgress: (value: number) => void) => {
  const body = new FormData(); body.append('fieldConfigurationId', String(fieldConfigurationId)); body.append('file', file)
  if (metadata.documentNumber) body.append('documentNumber', metadata.documentNumber); if (metadata.issueDate) body.append('issueDate', metadata.issueDate); if (metadata.expiryDate) body.append('expiryDate', metadata.expiryDate)
  return postFormWithProgress<{ attachment: EntityAttachment }>(`/api/recruitment/candidates/${candidateId}/resume`, body, {} as { attachment: EntityAttachment }, onProgress,
    660000, 'Resume upload or parsing took too long. Refresh the candidate and check the current resume before uploading again; the file may already be saved.')
}
export type RecruitmentResumeUploadProgress = {
  percent: number
  completedFiles: number
  totalFiles: number
  activeFrom: number
  activeTo: number
  phase: 'uploading' | 'processing' | 'complete'
}

export type RecruitmentResumeReviewedDraft = RecruitmentResumePreview & { file: File }

export const previewRecruitmentResume = (request: { clientId?: number; positionId?: number; jobPostingId?: number | null; talentPoolOnly?: boolean; enableParsing?: boolean; file: File }) => {
  const body = new FormData()
  if (request.clientId) body.append('clientId', String(request.clientId))
  if (request.positionId) body.append('positionId', String(request.positionId))
  if (request.jobPostingId) body.append('jobPostingId', String(request.jobPostingId))
  if (request.talentPoolOnly) body.append('talentPoolOnly', 'true')
  body.append('enableParsing', request.enableParsing === false ? 'false' : 'true')
  body.append('file', request.file, request.file.name)
  return postFormWithProgress<RecruitmentResumePreview>('/api/recruitment/resume-intake/preview', body, {} as RecruitmentResumePreview, () => undefined,
    660000, 'Resume preview and parsing took too long. No candidate was created by this preview. Keep the file selected and retry or continue with manual review.')
}

export const intakeRecruitmentResumes = async (request: { clientId?: number; positionId?: number; jobPostingId?: number | null; talentPoolOnly?: boolean; forceUpload?: boolean; deferAtsScoring?: boolean; sourceType: string; files: File[]; drafts?: RecruitmentResumeReviewedDraft[] }, onProgress: (progress: RecruitmentResumeUploadProgress) => void): Promise<ApiResult<RecruitmentResumeIntakeResult>> => {
  const totalFiles = request.files.length
  // One resume per request gives an exact processed-file percentage, isolates a
  // bad document, and avoids holding the entire bulk operation behind one response.
  const batchSize = 1
  const aggregate: RecruitmentResumeIntakeResult = { totalFiles, imported: 0, needsReview: 0, items: [] }
  let completedFiles = 0
  let lastStatus = 200
  let successResponses = 0
  const errors: string[] = []

  for (let start = 0; start < totalFiles; start += batchSize) {
    const files = request.files.slice(start, start + batchSize)
    const activeFrom = start + 1
    const activeTo = start + files.length
    onProgress({ percent: Math.round(completedFiles / Math.max(1, totalFiles) * 100), completedFiles, totalFiles, activeFrom, activeTo, phase: 'uploading' })
    const response = await uploadRecruitmentResumeBatch({ ...request, files, draft: request.drafts?.[start] }, uploadPercent => {
      const uploadShare = files.length * Math.min(100, uploadPercent) / 100 * 0.2
      const percent = Math.min(99, Math.round((completedFiles + uploadShare) / Math.max(1, totalFiles) * 100))
      onProgress({ percent, completedFiles, totalFiles, activeFrom, activeTo, phase: uploadPercent >= 100 ? 'processing' : 'uploading' })
    })
    lastStatus = response.status
    if (response.ok) {
      successResponses++
      aggregate.imported += response.data.imported
      aggregate.needsReview += response.data.needsReview
      aggregate.items.push(...response.data.items)
    } else {
      const error = response.error || 'This batch could not be processed.'
      errors.push(error)
      const failedItems: RecruitmentResumeIntakeItem[] = files.map(file => ({ fileName: file.name, success: false, forceUploaded: false, error, parsingStatus: 'Failed', detectedName: '', detectedEmail: '', detectedPhone: '', detectedAddress: '' }))
      aggregate.needsReview += failedItems.length
      aggregate.items.push(...failedItems)
    }
    completedFiles += files.length
    onProgress({ percent: Math.round(completedFiles / Math.max(1, totalFiles) * 100), completedFiles, totalFiles, activeFrom, activeTo, phase: completedFiles === totalFiles ? 'complete' : 'processing' })
  }
  return { ok: successResponses > 0, data: aggregate, error: errors.join(' '), status: successResponses > 0 ? 200 : lastStatus }
}

const uploadRecruitmentResumeBatch = (request: { clientId?: number; positionId?: number; jobPostingId?: number | null; talentPoolOnly?: boolean; forceUpload?: boolean; deferAtsScoring?: boolean; sourceType: string; files: File[]; draft?: RecruitmentResumeReviewedDraft }, onProgress: (value: number) => void) => {
  const body = new FormData()
  if (request.clientId) body.append('clientId', String(request.clientId))
  if (request.positionId) body.append('positionId', String(request.positionId))
  if (request.jobPostingId) body.append('jobPostingId', String(request.jobPostingId))
  if (request.talentPoolOnly) body.append('talentPoolOnly', 'true')
  if (request.forceUpload) body.append('forceUpload', 'true')
  if (request.deferAtsScoring) body.append('deferAtsScoring', 'true')
  body.append('sourceType', request.sourceType)
  if (request.draft) {
    body.append('draftReviewed', 'true')
    if (request.draft.existingCandidateId) body.append('draftExistingCandidateId', String(request.draft.existingCandidateId))
    body.append('draftFirstName', request.draft.firstName)
    body.append('draftLastName', request.draft.lastName)
    body.append('draftEmail', request.draft.email)
    body.append('draftPhone', request.draft.phone)
    body.append('draftAddress', request.draft.address)
    body.append('draftTotalExperienceMonths', String(request.draft.totalExperienceMonths || 0))
    body.append('draftCurrentCompany', request.draft.currentCompany)
    body.append('draftCurrentTitle', request.draft.currentTitle)
    body.append('draftHighestQualification', request.draft.highestQualification)
    body.append('draftSkills', request.draft.skills.join('\n'))
    body.append('draftCertifications', request.draft.certifications.join('\n'))
  }
  request.files.forEach(file => body.append('files', file, file.name))
  return postFormWithProgress<RecruitmentResumeIntakeResult>('/api/recruitment/resume-intake', body, { totalFiles: 0, imported: 0, needsReview: 0, items: [] }, onProgress,
    660000, 'Candidate upload or parsing took too long. Refresh Applications and check whether the candidate and resume were saved before retrying.')
}
export const getGlobalTalentPoolCandidates = (query = '', status = '') => getJson<RecruitmentCandidate[]>(`/api/recruitment/talent-pool/candidates?${new URLSearchParams({ query, status })}`, [])
export const getTalentPoolMatches = (positionId?: number, status = '') => {
  const search = new URLSearchParams({ status }); if (positionId) search.set('positionId', String(positionId))
  return getJson<RecruitmentCandidateApplication[]>(`/api/recruitment/talent-pool/matches?${search}`, [])
}
export const runTalentPoolMatch = (positionId: number, candidateIds: number[] = []) => postJson('/api/recruitment/talent-pool/match', { positionId, candidateIds }, null as RecruitmentTalentPoolMatchRunResult | null, { successMessage: 'Talent Pool matching request accepted.', timeoutMs: 180000 })
export const directSelectTalentPoolCandidate = (candidateId: number, positionId: number) => postJson('/api/recruitment/talent-pool/select-direct', { candidateId, positionId }, null as RecruitmentCandidateApplication | null, { successMessage: 'Resume selected for the role without ATS scoring.' })
export const setTalentPoolMatchSelection = (id: number, selected: boolean) => postJson(`/api/recruitment/talent-pool/matches/${id}/selection`, { selected }, null as RecruitmentCandidateApplication | null, { successMessage: selected ? 'Candidate added to Selected.' : 'Candidate returned to ATS Matches.' })
export const promoteTalentPoolMatch = (id: number) => postJson(`/api/recruitment/talent-pool/matches/${id}/promote`, {}, null as RecruitmentCandidateApplication | null, { successMessage: 'Selected candidate moved to the hiring pipeline.' })
export const getAtsProfiles = (clientId?: number) => getJson<RecruitmentAtsScoringProfile[]>(`/api/recruitment-admin/ats-profiles${clientId ? `?clientId=${clientId}` : ''}`, [])
export const getAtsCriterionCatalog = () => getJson<RecruitmentAtsScoringCriterion[]>('/api/recruitment-admin/ats-criteria', [])
export const saveAtsProfile = (row: RecruitmentAtsScoringProfile) => postJson('/api/recruitment-admin/ats-profiles', row, null as RecruitmentAtsScoringProfile | null, { successMessage: 'ATS profile saved.' })
export const deleteAtsProfile = (id: number) => deleteJson(`/api/recruitment-admin/ats-profiles/${id}`, null, { successMessage: 'ATS profile deleted.' })
export const getRecruitmentSkills = (clientId?: number) => getJson<RecruitmentSkill[]>(`/api/recruitment-admin/skills${clientId ? `?clientId=${clientId}` : ''}`, [])
export const saveRecruitmentSkill = (row: RecruitmentSkill) => postJson('/api/recruitment-admin/skills', row, null as RecruitmentSkill | null, { successMessage: 'Skill saved.' })
export const deleteRecruitmentSkill = (id: number) => deleteJson(`/api/recruitment-admin/skills/${id}`, null, { successMessage: 'Skill deleted.' })
export const getRecruitmentAiScoringSettings = (clientId: number) => getJson<RecruitmentAiScoringSettings>(`/api/recruitment-admin/ai-scoring?clientId=${clientId}`, { id: 0, clientId, clientName: '', accountEmail: '', enableAiScoring: false, providerCode: 'Gemini', modelName: 'gemini-3.5-flash', endpointUrl: '', aiBlendWeight: 20, minimumConfidence: .65, maximumResumeCharacters: 40000, requestTimeoutSeconds: 45, hasApiKey: false, credentialStatus: 'Missing', apiKey: '', healthStatus: 'NotTested', lastHealthMessage: '', lastTestedAt: null, isActive: true, isPrimary: false, priority: 100, monthlyRequestLimit: 1000, usagePeriod: '', usageRequestCount: 0, usageInputTokens: 0, usageOutputTokens: 0, usagePercent: 0, lastUsedAt: null, consecutiveFailureCount: 0, lastFailureAt: null })
export const saveRecruitmentAiScoringSettings = (row: RecruitmentAiScoringSettings) => postJson('/api/recruitment-admin/ai-scoring', row, null as RecruitmentAiScoringSettings | null, { successMessage: 'AI scoring setup saved securely.' })
export const testRecruitmentAiScoringSettings = (clientId: number) => postJson(`/api/recruitment-admin/ai-scoring/${clientId}/test`, {}, null as RecruitmentAiScoringSettings | null, { successMessage: 'Gemini scoring connection is healthy.', timeoutMs: 120000 })
export const deleteRecruitmentAiScoringSettings = (clientId: number) => deleteJson(`/api/recruitment-admin/ai-scoring/${clientId}`, null, { successMessage: 'AI scoring setup and encrypted API key deleted.' })
export const getEmployeeActivity360 = (employeeId: number) => getJson<PersonActivityEvent[]>(`/api/employees/${employeeId}/activity-360`, [])
export type { RecruitmentOpenPosition }
