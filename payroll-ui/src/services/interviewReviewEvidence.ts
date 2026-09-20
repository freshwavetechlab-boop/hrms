import type { InternalInterviewEvent } from '../types/internalInterviews'

export type InterviewSummaryEvidence = { questionEventId: number; answerEventId: number; answeredAtUtc: string; evidenceQuote: string }
export type InterviewSummaryObservation = { kind: string; note: string; evidence: InterviewSummaryEvidence[] }
export type InterviewDraftSummary = { questionCount: number; answerCount: number; unansweredQuestionEventIds: number[]; observations: InterviewSummaryObservation[] }

export function readInterviewSummary(events: InternalInterviewEvent[]): InterviewDraftSummary | undefined {
  const event = events.find(e => e.kind === 'ai-summary')
  if (!event) return undefined
  try {
    const result = JSON.parse(event.text) as InterviewDraftSummary
    if (!Number.isInteger(result.questionCount) || !Number.isInteger(result.answerCount)
      || !Array.isArray(result.unansweredQuestionEventIds) || !Array.isArray(result.observations)) return undefined
    if (!result.observations.every(item => typeof item?.note === 'string' && typeof item.kind === 'string'
      && Array.isArray(item.evidence) && item.evidence.every(e => Number.isInteger(e?.answerEventId)
        && Number.isInteger(e.questionEventId) && typeof e.answeredAtUtc === 'string' && typeof e.evidenceQuote === 'string'))) return undefined
    return result
  } catch { return undefined }
}

// Keep failed attempts in the audit, but a later durable success resolves the current warning.
// A retry request alone is NOT success; one answer's recovery cannot hide another failure.
export function interviewAiErrors(events: InternalInterviewEvent[]) {
  const errors = events.filter(e => e.kind === 'ai-error')
  const resolved = (error: InternalInterviewEvent) => {
    const separator = error.eventKey.indexOf(':failed:')
    return separator > 0 && events.some(e => e.id > error.id && e.eventKey === error.eventKey.slice(0, separator)
      && ['ai-review', 'ai-summary', 'question', 'questions-finished'].includes(e.kind))
  }
  return { active: errors.filter(e => !resolved(e)), recovered: errors.filter(resolved) }
}
