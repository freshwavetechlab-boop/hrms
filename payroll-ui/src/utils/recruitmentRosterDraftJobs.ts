import type { RecruitmentRequisition } from '../types/payroll'
import type { RecruitmentJobPosting } from '../types/recruitmentOrchestration'

export type RosterDraftJob = RecruitmentJobPosting & { rosterDraftRequest?: RecruitmentRequisition }

// A preparation row is a real hiring request/JD, not a public posting. Its
// negative UI key cannot collide with persisted job-posting IDs or reach a
// posting mutation; opening it always routes to its hiring request.
export function withRosterDraftJobs(postings: RecruitmentJobPosting[], requests: RecruitmentRequisition[], clientId: number): RosterDraftJob[] {
  const rows: RosterDraftJob[] = [...postings]
  for (const request of requests) {
    if (!Number.isSafeInteger(request.id) || request.id <= 0 || clientId && request.clientId !== clientId || request.sourceType !== 'Employee List JD'
      || !request.latestJobDescriptionVersionId || ['Rejected', 'Withdrawn', 'Cancelled'].includes(request.status)
      || postings.some(posting => posting.clientId === request.clientId && posting.positionId === request.openPositionId)) continue
    rows.push({ id: -request.id, clientId: request.clientId, positionId: 0, jobDescriptionVersionId: request.latestJobDescriptionVersionId,
      publicSlug: '', publicUrl: '', publicTitle: request.positionTitle, status: 'Draft', applicationCount: 0,
      autoRunAts: false, enableResumeParsing: false, enableAiParsing: false, requireEmailOtp: false, searchEngineVisible: false,
      positionCode: request.rfrNumber, positionTitle: request.positionTitle, clientName: request.clientName,
      candidatePortalReady: false, createdAtUtc: request.createdAt, updatedAtUtc: request.updatedAt, rosterDraftRequest: request })
  }
  return rows
}

export const rosterDraftRequest = (row: RecruitmentJobPosting) => (row as RosterDraftJob).rosterDraftRequest
