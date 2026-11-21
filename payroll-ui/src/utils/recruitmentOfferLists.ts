import type { RecruitmentOffer } from '../types/payroll'
import type { RecruitmentJoiningCandidate } from '../services/recruitmentJoiningService'

export type OfferListRecord = Omit<RecruitmentOffer, 'id'> & {
  id: string; sourceType: 'Offer' | 'Roster'; sourceId: number
  nativeOffer?: RecruitmentOffer
}

// Keep the two ID namespaces separate. A roster release is not a pipeline
// offer and must never be submitted to a native offer mutation endpoint.
export function allRecruitmentOffers(offers: RecruitmentOffer[], joining: RecruitmentJoiningCandidate[], clientId: number): OfferListRecord[] {
  const rows = new Map<string, OfferListRecord>()
  for (const offer of offers) {
    if (!Number.isSafeInteger(offer.id) || offer.id <= 0 || clientId && offer.clientId !== clientId) continue
    const id = `offer:${offer.id}`
    rows.set(id, { ...offer, id, sourceType: 'Offer', sourceId: offer.id, nativeOffer: offer })
  }
  for (const row of joining) {
    const sourceId = row.rosterRowId || row.sourceId
    if (row.sourceType !== 'Roster' || !Number.isSafeInteger(sourceId) || sourceId <= 0 || clientId && row.clientId !== clientId) continue
    const id = `roster:${sourceId}`
    rows.set(id, {
      id, sourceType: 'Roster', sourceId, offerNumber: row.offerNumber || `Roster #${sourceId}`,
      applicationId: row.applicationId, candidateName: row.candidateName, positionTitle: row.positionTitle,
      clientId: row.clientId, offeredCtc: row.offeredCtc, currency: row.currency,
      proposedJoiningDate: row.proposedJoiningDate, status: row.offerStatus, offerTemplateName: '',
      budgetBasis: '', approvedBudgetAmount: 0, budgetExposureAmount: 0, maximumVariancePercent: 0,
      variancePercent: 0, varianceExceeded: false, approvalPolicy: '', candidateResponseValidityDays: 0,
      remarks: row.issue,
    })
  }
  return [...rows.values()]
}
