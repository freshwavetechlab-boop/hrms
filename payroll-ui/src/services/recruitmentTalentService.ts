import type { ConvertCandidateToEmployeeRequest, Employee, EntityAttachment, PersonActivityEvent, RecruitmentAiScoringSettings, RecruitmentApplicationScore, RecruitmentAtsScoringCriterion, RecruitmentAtsScoringProfile, RecruitmentCandidate, RecruitmentCandidateApplication, RecruitmentCandidateCertification, RecruitmentCandidateChecklistItem, RecruitmentCandidateDetail, RecruitmentCandidateEducation, RecruitmentCandidateExperience, RecruitmentInterview, RecruitmentInterviewFeedback, RecruitmentInterviewSchedulingContext, RecruitmentOffer, RecruitmentOpenPosition, RecruitmentResumeIntakeResult, RecruitmentSkill, RecruitmentTalentDashboard, SaveRecruitmentCandidate, SaveRecruitmentInterviewFeedbackCompetencyScore } from '../types/payroll'
import { deleteJson, getJson, postFormWithProgress, postJson, putJson } from './apiClient'

export const getTalentDashboard = () => getJson<RecruitmentTalentDashboard>('/api/recruitment/talent/dashboard', { talentProfiles: 0, activeApplications: 0, interviewsScheduled: 0, offersPending: 0, preOnboardingPending: 0, joined: 0 })
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
export const deleteApplication = (id: number) => deleteJson(`/api/recruitment/applications/${id}`, null, { successMessage: 'Application and ATS screening data deleted.' })
export const changeApplicationStage = (id: number, stage: string, reason: string) => postJson(`/api/recruitment/applications/${id}/stage`, { stage, status: stage, reason }, null as RecruitmentCandidateApplication | null, { successMessage: 'Candidate stage updated.' })
export const scoreApplication = (id: number) => postJson(`/api/recruitment/applications/${id}/score`, {}, null, { successMessage: 'ATS score recalculated.' })
export const overrideApplicationScore = (scoreId: number, score: number, reason: string) => postJson(`/api/recruitment/application-scores/${scoreId}/override`, { score, reason }, null as RecruitmentApplicationScore | null, { successMessage: 'ATS score override saved.' })
export const getInterviews = () => getJson<RecruitmentInterview[]>('/api/recruitment/interviews', [])
export const getInterviewSchedulingContext = (applicationId: number) => getJson<RecruitmentInterviewSchedulingContext | null>(`/api/recruitment/interviews/scheduling-context/${applicationId}`, null)
export const saveInterview = (row: Partial<RecruitmentInterview> & { applicationId: number; panelUserIds?: number[] }) => postJson('/api/recruitment/interviews', row, null as RecruitmentInterview | null, { successMessage: 'Interview saved.' })
export const getInterviewFeedback = (interviewId: number) => getJson<RecruitmentInterviewFeedback[]>(`/api/recruitment/interviews/${interviewId}/feedback`, [])
export const saveInterviewFeedback = (interviewId: number, row: { panelUserId: number; overallScore: number; recommendation: string; competencyScoresJson?: string; comments: string; competencyScores: SaveRecruitmentInterviewFeedbackCompetencyScore[] }) => postJson(`/api/recruitment/interviews/${interviewId}/feedback`, row, null as RecruitmentInterviewFeedback | null, { successMessage: 'Interview feedback saved.' })
export const getOffers = () => getJson<RecruitmentOffer[]>('/api/recruitment/offers', [])
export const saveOffer = (row: Partial<RecruitmentOffer> & { applicationId: number }) => postJson('/api/recruitment/offers', row, null as RecruitmentOffer | null, { successMessage: 'Offer saved.' })
export const generateOfferLetter = (id: number) => postJson(`/api/recruitment/offers/${id}/generate-letter`, {}, null as RecruitmentOffer | null, { successMessage: 'Offer letter generated and stored securely.' })
export const updateOfferStatus = (id: number, status: string, remarks = '') => postJson(`/api/recruitment/offers/${id}/status`, { status, remarks }, null as RecruitmentOffer | null, { successMessage: 'Offer status updated.' })
export const completeCandidateChecklistItem = (applicationId: number, itemId: number, attachmentPublicId?: string | null) => postJson(`/api/recruitment/applications/${applicationId}/checklist/${itemId}/complete`, { attachmentPublicId: attachmentPublicId || null }, null as RecruitmentCandidateChecklistItem | null, { successMessage: 'Pre-onboarding item completed.' })
export const convertCandidateToEmployee = (applicationId: number, row: ConvertCandidateToEmployeeRequest) => postJson(`/api/recruitment/applications/${applicationId}/convert-to-employee`, row, null as Employee | null, { successMessage: 'Candidate converted to employee.' })
export const uploadCandidateResume = (candidateId: number, fieldConfigurationId: number, file: File, metadata: { documentNumber?: string; issueDate?: string; expiryDate?: string }, onProgress: (value: number) => void) => {
  const body = new FormData(); body.append('fieldConfigurationId', String(fieldConfigurationId)); body.append('file', file)
  if (metadata.documentNumber) body.append('documentNumber', metadata.documentNumber); if (metadata.issueDate) body.append('issueDate', metadata.issueDate); if (metadata.expiryDate) body.append('expiryDate', metadata.expiryDate)
  return postFormWithProgress<{ attachment: EntityAttachment }>(`/api/recruitment/candidates/${candidateId}/resume`, body, {} as { attachment: EntityAttachment }, onProgress)
}
export const intakeRecruitmentResumes = (request: { clientId: number; positionId: number; jobPostingId?: number | null; sourceType: string; files: File[] }, onProgress: (value: number) => void) => {
  const body = new FormData()
  body.append('clientId', String(request.clientId))
  body.append('positionId', String(request.positionId))
  if (request.jobPostingId) body.append('jobPostingId', String(request.jobPostingId))
  body.append('sourceType', request.sourceType)
  request.files.forEach(file => body.append('files', file, file.name))
  return postFormWithProgress<RecruitmentResumeIntakeResult>('/api/recruitment/resume-intake', body, { totalFiles: 0, imported: 0, needsReview: 0, items: [] }, onProgress)
}
export const getAtsProfiles = (clientId?: number) => getJson<RecruitmentAtsScoringProfile[]>(`/api/recruitment-admin/ats-profiles${clientId ? `?clientId=${clientId}` : ''}`, [])
export const getAtsCriterionCatalog = () => getJson<RecruitmentAtsScoringCriterion[]>('/api/recruitment-admin/ats-criteria', [])
export const saveAtsProfile = (row: RecruitmentAtsScoringProfile) => postJson('/api/recruitment-admin/ats-profiles', row, null as RecruitmentAtsScoringProfile | null, { successMessage: 'ATS profile saved.' })
export const deleteAtsProfile = (id: number) => deleteJson(`/api/recruitment-admin/ats-profiles/${id}`, null, { successMessage: 'ATS profile deleted.' })
export const getRecruitmentSkills = (clientId?: number) => getJson<RecruitmentSkill[]>(`/api/recruitment-admin/skills${clientId ? `?clientId=${clientId}` : ''}`, [])
export const saveRecruitmentSkill = (row: RecruitmentSkill) => postJson('/api/recruitment-admin/skills', row, null as RecruitmentSkill | null, { successMessage: 'Skill saved.' })
export const deleteRecruitmentSkill = (id: number) => deleteJson(`/api/recruitment-admin/skills/${id}`, null, { successMessage: 'Skill deleted.' })
export const getRecruitmentAiScoringSettings = (clientId: number) => getJson<RecruitmentAiScoringSettings>(`/api/recruitment-admin/ai-scoring?clientId=${clientId}`, { id: 0, clientId, clientName: '', enableAiScoring: false, providerCode: 'Gemini', modelName: 'gemini-3.5-flash', aiBlendWeight: 20, minimumConfidence: .65, maximumResumeCharacters: 40000, requestTimeoutSeconds: 45, hasApiKey: false, apiKey: '', healthStatus: 'NotTested', lastHealthMessage: '', lastTestedAt: null, isActive: true })
export const saveRecruitmentAiScoringSettings = (row: RecruitmentAiScoringSettings) => postJson('/api/recruitment-admin/ai-scoring', row, null as RecruitmentAiScoringSettings | null, { successMessage: 'AI scoring setup saved securely.' })
export const testRecruitmentAiScoringSettings = (clientId: number) => postJson(`/api/recruitment-admin/ai-scoring/${clientId}/test`, {}, null as RecruitmentAiScoringSettings | null, { successMessage: 'Gemini scoring connection is healthy.', timeoutMs: 120000 })
export const deleteRecruitmentAiScoringSettings = (clientId: number) => deleteJson(`/api/recruitment-admin/ai-scoring/${clientId}`, null, { successMessage: 'AI scoring setup and encrypted API key deleted.' })
export const getEmployeeActivity360 = (employeeId: number) => getJson<PersonActivityEvent[]>(`/api/employees/${employeeId}/activity-360`, [])
export type { RecruitmentOpenPosition }
