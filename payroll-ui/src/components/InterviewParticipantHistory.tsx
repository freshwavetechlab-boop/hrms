import { Card, Tag } from 'antd'
import type { InternalInterviewEvent } from '../types/internalInterviews'
import { interviewParticipantHistory } from '../services/interviewParticipantHistory'

const utc = (value: string) => new Date(/Z$|[+-]\d\d:\d\d$/.test(value) ? value : value + 'Z').toLocaleString()
const actions: Record<string, string> = { participant_joined: 'Joined', participant_left: 'Left', participant_connection_aborted: 'Connection aborted', room_started: 'Room started', room_finished: 'Room ended' }

export default function InterviewParticipantHistory({ events }: { events: InternalInterviewEvent[] }) {
  const rows = interviewParticipantHistory(events)
  return <Card title="Participant connection history" style={{ marginTop: 16 }}>
    <p>Verified media-server events, ordered by event time. Delayed delivery and missing callbacks are possible; panel assignment alone is not proof of attendance. This history never scores or rejects a candidate.</p>
    {!rows.length ? <p>No verified connection history received. Configure the private media-server callback before relying on this history.</p>
      : <div className="interview-presence-table"><table><thead><tr><th>Participant</th><th>Connection event</th><th>Media event time</th><th>Received by HRMS</th></tr></thead>
        <tbody>{rows.map(row => <tr key={row.id}><td>{row.role}{row.identity && <small>{row.identity} · {row.participantSessionId}</small>}</td>
          <td><Tag color={row.eventType === 'participant_joined' ? 'green' : undefined}>{actions[row.eventType]}</Tag></td>
          <td>{utc(row.occurredAtUtc)}</td><td>{utc(row.receivedAtUtc)}</td></tr>)}</tbody></table></div>}
  </Card>
}
