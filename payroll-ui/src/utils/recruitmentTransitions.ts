export type HiringTransitionLabelSource = { outcomeCode?: string | null; actionLabel?: string | null }

export function isDivisionRejectionOutcome(outcomeCode?: string | null) {
  return ['REJECT', 'REJECT_BY_DIVISION', 'REJECTED_BY_DIVISION'].includes(String(outcomeCode || '').trim().toUpperCase())
}

export function hiringTransitionLabel(transition?: HiringTransitionLabelSource | null) {
  if (!transition) return 'Move stage'
  return isDivisionRejectionOutcome(transition.outcomeCode)
    ? 'Rejected by Division'
    : transition.actionLabel || transition.outcomeCode || 'Move stage'
}
