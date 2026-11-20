import { getJsonResult, postJson } from './apiClient'

export type RecruitmentJoiningSelection = { clientId: number; offerIds: number[]; rosterRowIds?: number[]; importReleasedForEss?: boolean }
export type RecruitmentJoiningCandidate = {
  sourceType: 'Offer' | 'Roster'; sourceId: number; rowKey: string; rosterRowId?: number | null; releaseId?: number | null; acceptanceRecorded: boolean
  offerId: number; applicationId: number; candidateId: number; candidateCode: string; candidateName: string
  clientId: number; clientName: string; positionTitle: string; jobLocation: string; proposedJoiningDate: string
  offeredCtc: number; currency: string; offerStatus: string; employeeId?: number | null; employeeCode?: string | null
  importStatus: 'Pending acceptance' | 'Ready' | 'Imported' | 'Review'; issue: string
}
export type RecruitmentJoiningResultItem = {
  offerId: number; applicationId: number; candidateName: string; employeeId?: number | null; employeeCode?: string | null; message: string
  sourceType?: 'Offer' | 'Roster'; sourceId?: number; rowKey?: string; rosterRowId?: number | null
}
export type RecruitmentJoiningAcceptResult = {
  items: (RecruitmentJoiningResultItem & { status: 'Accepted' | 'Already accepted' | 'Review' })[]
  accepted: number; alreadyAccepted: number; review: number
}
export type RecruitmentJoiningImportResult = {
  items: (RecruitmentJoiningResultItem & { status: 'Imported' | 'Already imported' | 'Review' })[]
  imported: number; alreadyImported: number; review: number
}

export const getRecruitmentJoiningCandidates = (clientId: number, importReleasedForEss = false) => getJsonResult<RecruitmentJoiningCandidate[]>(
  `/api/recruitment/joining-candidates?clientId=${clientId}${importReleasedForEss ? '&importReleasedForEss=true' : ''}`, [], { toast: false, loader: false })
export const acceptRecruitmentJoiningOffers = (selection: RecruitmentJoiningSelection) => postJson(
  '/api/recruitment/offers/accept-selected', selection, null as RecruitmentJoiningAcceptResult | null, { toast: false, loader: false, timeoutMs: 180000 })
export const importRecruitmentJoiningCandidates = (selection: RecruitmentJoiningSelection) => postJson(
  '/api/recruitment/joining-candidates/import', selection, null as RecruitmentJoiningImportResult | null, { toast: false, loader: false, timeoutMs: 180000 })
