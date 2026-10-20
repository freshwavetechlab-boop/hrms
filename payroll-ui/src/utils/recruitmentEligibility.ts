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
    hasException: (row.eligibilityReasons || []).some(reason => reason.startsWith('Approved eligibility exception:')),
    reason: (row.eligibilityReasons || []).join(' '),
  } as const
}

export function isRecruitmentPipelineCandidate(row: RecruitmentEligibilityRecord & {
  currentStage?: string; currentStatus?: string; atsShortlistThreshold?: number
}) {
  return row.eligibilityStatus === 'Eligible' && row.atsScore != null
    && row.atsScore >= (row.atsShortlistThreshold || 60)
    && !/new|application|intake/i.test(row.currentStage || '')
    && !/reject|withdraw/i.test(`${row.currentStage || ''} ${row.currentStatus || ''}`)
}
