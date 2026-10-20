export type EligibilityReviewDecision = {
  key: string
  decision: 'Verified' | 'NotMet' | 'Pending' | 'Waived'
  requirement?: string
  note: string
  verifiedExperienceMonths?: number | null
  reviewerUserId?: number
  reviewerName?: string
  reviewedAtUtc?: string
}
export type EligibilityReviewItem = {
  key: string; requirement: string; status: string; evidence: string; canReview: boolean; canWaive?: boolean; review?: EligibilityReviewDecision | null
}
export type EligibilityReviewContext = {
  applicationId: number; applicationCode: string; candidateName: string; resumeId: number; attachmentPublicId?: string | null
  scoreId: number; requirementsHash: string; eligibilityStatus: string; reasons: string[]; atsScore?: number | null
  parsedExperienceMonths?: number | null; verifiedExperienceMonths?: number | null; experienceEvidence: string
  requiredExperienceRange: string; scoreIsStale: boolean; items: EligibilityReviewItem[]
}
export type SaveEligibilityReview = {
  expectedResumeId: number; expectedScoreId: number; requirementsHash: string; decisions: EligibilityReviewDecision[]
  experienceChanged: boolean; verifiedExperienceMonths?: number | null; experienceEvidence: string; runAtsOnly: boolean
}
