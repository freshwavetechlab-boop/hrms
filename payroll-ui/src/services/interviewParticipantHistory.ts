import type { InternalInterviewEvent } from '../types/internalInterviews'

export type InterviewPresence = { id: number; identity: string; participantSessionId: string; role: string; eventType: string; occurredAtUtc: string; receivedAtUtc: string }
export function interviewParticipantHistory(events: InternalInterviewEvent[]): InterviewPresence[] {
  const rows: InterviewPresence[] = []
  for (const event of events) {
    if (event.kind !== 'media-presence' || event.source !== 'livekit-webhook') continue
    try {
      const value = JSON.parse(event.text)
      if (!value || !['participant_joined', 'participant_left', 'participant_connection_aborted', 'room_started', 'room_finished'].includes(value.eventType)
        || !['identity', 'participantSessionId', 'role', 'occurredAtUtc'].every(key => typeof value[key] === 'string')
        || !Number.isFinite(Date.parse(value.occurredAtUtc))) continue
      rows.push({ id: event.id, identity: value.identity, participantSessionId: value.participantSessionId, role: value.role,
        eventType: value.eventType, occurredAtUtc: value.occurredAtUtc, receivedAtUtc: event.createdAtUtc })
    } catch { /* Invalid metadata is not inferred as attendance. The original audit remains available. */ }
  }
  return rows.sort((a, b) => Date.parse(a.occurredAtUtc) - Date.parse(b.occurredAtUtc) || a.id - b.id)
}
