export type RecruitmentEligibilityRecord = {
  eligibilityStatus?: string
  eligibilityReasons?: string[]
  scoreStatus?: string
  atsScore?: number | null
  totalScore?: number
}

export function recruitmentEligibility(row: RecruitmentEligibilityRecord) {
  const stored = row.eligibilityStatus && row.eligibilityStatus !== 'NotAssessed'
    ? row.eligibilityStatus : row.scoreStatus
  const status = /^(?:ineligible|noteligible)$/i.test(stored || '') ? 'ineligible'
    : /^needsreview$/i.test(stored || '') ? 'review'
      : row.eligibilityStatus === 'NotAssessed' ? 'pending'
      : row.eligibilityStatus === 'Eligible' || row.atsScore != null || row.totalScore != null ? 'eligible' : 'pending'
  return {
    status,
    label: status === 'ineligible' ? 'Not eligible' : status === 'review' ? 'Needs review' : status === 'eligible' ? 'Eligible' : 'Not assessed',
    color: status === 'ineligible' ? 'red' : status === 'review' ? 'gold' : status === 'eligible' ? 'green' : 'default',
    showScore: status === 'eligible' || status === 'review',
    isEligible: status === 'eligible',
    provisional: status === 'review',
    reason: (row.eligibilityReasons || []).join(' '),
  } as const
}
