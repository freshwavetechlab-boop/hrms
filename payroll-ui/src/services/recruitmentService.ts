import { apiRequest, getJson, postEmpty, postJson } from './apiClient'
import type { RecruitmentDashboard, RecruitmentOpenPosition, RecruitmentOperationsOptions, RecruitmentPositionDetail, RecruitmentRequisition, SaveRecruitmentRequisition } from '../types/payroll'

const emptyDashboard: RecruitmentDashboard = { drafts: 0, pendingApproval: 0, approved: 0, rejected: 0, returned: 0, withdrawn: 0, openPositions: 0, filledPositions: 0, cancelledPositions: 0, onHoldPositions: 0, remainingPositions: 0, averageApprovalHours: 0, departmentWiseHiring: [], companyWiseHiring: [], priorityWiseHiring: [], upcomingJoiningTargets: [] }

export const getRecruitmentDashboard = () => getJson<RecruitmentDashboard>('/api/recruitment/dashboard', emptyDashboard)

export const getRecruitmentRequisitions = (filters: { clientId?: number; status?: string; query?: string; department?: string; hiringType?: string; employmentType?: string; priority?: string; businessUnit?: string; positionCategory?: string; experience?: string; location?: string; project?: string; replacementHiring?: boolean; budgetMin?: number; budgetMax?: number; dateFrom?: string; dateTo?: string; recruiterUserId?: number }) => {
  const params = new URLSearchParams()
  Object.entries(filters).forEach(([key, value]) => { if (value !== undefined && value !== null && String(value)) params.set(key, String(value)) })
  return getJson<RecruitmentRequisition[]>(`/api/recruitment/requisitions?${params.toString()}`, [])
}

export const saveRecruitmentRequisition = (request: SaveRecruitmentRequisition) =>
  postJson<SaveRecruitmentRequisition, RecruitmentRequisition | null>('/api/recruitment/requisitions', request, null)

export const submitRecruitmentRequisition = (id: number) =>
  postEmpty<RecruitmentRequisition | null>(`/api/recruitment/requisitions/${id}/submit`, null)

export const getRecruitmentOpenPositions = () => getJson<RecruitmentOpenPosition[]>('/api/recruitment/open-positions', [])
export const getRecruitmentOperationsOptions = () => getJson<RecruitmentOperationsOptions>('/api/recruitment/operations/options', { allowMultipleRecruiters: false, enableVendorHiring: false, enableConsultantHiring: false, enableInternalHiring: false, enableReferralHiring: false, enableDocumentVerification: false, recruiters: [], vendors: [], consultants: [], positionStatuses: [], publishingChannels: [], assignmentPriorities: [] })
export const getRecruitmentMasterOptions = (masterType: string) => getJson<string[]>(`/api/recruitment/masters/${encodeURIComponent(masterType)}`, [])
export const getRecruitmentOpenPositionDetail = (id: number) => getJson<RecruitmentPositionDetail | null>(`/api/recruitment/open-positions/${id}`, null)
export const saveRecruitmentPositionNote = (id: number, request: { noteType: string; noteText: string }) => apiRequest(`/api/recruitment/open-positions/${id}/notes`, { method: 'POST', body: JSON.stringify(request) })
export const updateRecruitmentPositionStatus = (id: number, request: { status: string; comment: string }) => apiRequest(`/api/recruitment/open-positions/${id}/status`, { method: 'POST', body: JSON.stringify(request) })
const postPositionAction = (id: number, path: string, request: unknown) => apiRequest(`/api/recruitment/open-positions/${id}/${path}`, { method: 'POST', body: JSON.stringify(request) })
export const assignRecruiter = (id: number, request: { primaryRecruiterUserId: number; secondaryRecruiterUserId: number; assignmentReason: string }) => postPositionAction(id, 'assign-recruiter', request)
export const assignVendor = (id: number, request: { partnerId: number; priority: string; dueDate?: string; expectedProfiles: number; remarks: string }) => postPositionAction(id, 'vendors', request)
export const assignConsultant = (id: number, request: { partnerId: number; priority: string; dueDate?: string; expectedProfiles: number; remarks: string }) => postPositionAction(id, 'consultants', request)
export const publishPosition = (id: number, request: { channel: string; publishingDate?: string; expiryDate?: string; status: string; remarks: string }) => postPositionAction(id, 'publish', request)
export const createReferralCampaign = (id: number, request: { campaignName: string; startDate: string; endDate: string; referralReward: number; visibilityDepartment: string; visibilityBusinessUnit: string; visibilityLocation: string; visibilityEmploymentType: string; status: string }) => postPositionAction(id, 'referral-campaigns', request)
