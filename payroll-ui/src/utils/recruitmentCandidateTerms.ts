export type CandidateTerms = { currentCtc?: number | null; expectedCtc?: number | null; noticePeriodDays?: number | null }
export type CandidateTerm = keyof CandidateTerms
export const candidateTermLabels: Record<CandidateTerm, string> = {
  currentCtc: 'Current CTC', expectedCtc: 'Expected CTC', noticePeriodDays: 'Notice period',
}

export function candidateTermText(value: number | null | undefined, field: CandidateTerm) {
  if (value == null) return 'Not provided'
  if (field === 'noticePeriodDays') return value === 0 ? 'Immediate (0 days)' : `${value} days`
  return `₹${value.toLocaleString('en-IN', { maximumFractionDigits: 2 })}/year`
}
