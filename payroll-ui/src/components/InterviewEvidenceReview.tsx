import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Card, Space, Tag } from 'antd'
import type { InterviewRecording, InterviewSessionClient } from '../services/internalInterviewService'
import type { InternalInterviewEvent } from '../types/internalInterviews'
import { interviewAiErrors, readInterviewSummary } from '../services/interviewReviewEvidence'

const utc = (value: string) => new Date(/Z$|[+-]\d\d:\d\d$/.test(value) ? value : value + 'Z')
type Observation = { questionEventId: number; answerEventId: number; kind: string; note: string; evidenceQuote: string }
const labels: Record<string, string> = { interviewSummary: 'Interview summary', answerSummary: 'Answer summary', skillEvidence: 'Skill evidence', needsHumanReview: 'Human review needed', inconsistency: 'Clarification needed', crossAnswerInconsistency: 'Clarify across answers' }
function observations(event: InternalInterviewEvent): Observation[] { try { const data: unknown = JSON.parse(event.text); return Array.isArray(data) ? data.filter(x => typeof x?.note === 'string' && typeof x?.evidenceQuote === 'string') : [] } catch { return [] } }

export default function InterviewEvidenceReview({ client, events, canAnalyze, onError }: { client: InterviewSessionClient; events: InternalInterviewEvent[]; canAnalyze: boolean; onError: (message: string) => void }) {
  const [recordings, setRecordings] = useState<InterviewRecording[]>([])
  const [selected, setSelected] = useState<InterviewRecording>()
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)
  const [currentSeconds, setCurrentSeconds] = useState(0)
  const video = useRef<HTMLVideoElement>(null)
  const callbacks = useRef(onError); callbacks.current = onError
  useEffect(() => {
    let active = true
    const load = () => void client.recordings().then(value => { if (active) setRecordings(value) }).catch(error => { if (active) callbacks.current(error instanceof Error ? error.message : 'Recording metadata unavailable.') })
    load(); const timer = window.setInterval(load, 15000)
    return () => { active = false; window.clearInterval(timer) }
  }, [client])
  const mounted = useRef(true)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const open = async (record: InterviewRecording) => {
    setBusy(true)
    try { const source = await client.recordingSource(record.id); if (mounted.current) { setUrl(source); setSelected(record); setCurrentSeconds(0) } }
    catch (error) { if (mounted.current) onError(error instanceof Error ? error.message : 'Recording could not be opened.') }
    finally { if (mounted.current) setBusy(false) }
  }
  const timeline = events.filter(e => ['question', 'answer', 'transcript'].includes(e.kind))
  const seconds = (event: InternalInterviewEvent) => selected ? Math.max(0, (utc(event.createdAtUtc).getTime() - utc(selected.startedAtUtc).getTime()) / 1000) : 0
  const activeEvent = selected ? [...timeline].reverse().find(e => seconds(e) <= currentSeconds) : undefined
  const summary = readInterviewSummary(events)
  const errors = interviewAiErrors(events)
  const [retrying, setRetrying] = useState(false)
  const retry = async () => {
    setRetrying(true)
    try { await client.retryAi() }
    catch (error) { onError(error instanceof Error ? error.message : 'AI retry failed.') }
    finally { if (mounted.current) setRetrying(false) }
  }
  return <div className={`internal-interview-columns ${!recordings.length ? 'interview-no-recordings' : ''}`}>
    <Card title="Authorized recording & synchronized timeline">
      {!recordings.length && <p>No recording metadata. Recording may have been disabled for this consented session.</p>}
      {recordings.map(record => <Space wrap key={record.id}><Tag color={record.status === 'Ready' ? 'green' : record.status === 'Failed' ? 'red' : 'orange'}>{record.status}</Tag><span>{utc(record.startedAtUtc).toLocaleString()}</span><Button loading={busy} disabled={record.status !== 'Ready'} onClick={() => void open(record)}>Load protected replay</Button></Space>)}
      {url && <><video ref={video} crossOrigin="use-credentials" src={url} preload="metadata" controls playsInline style={{ width: '100%', maxHeight: 420 }} onError={() => { setUrl(''); onError('Protected replay stopped. Check your login, recording access and API connectivity, then load it again.') }} onTimeUpdate={() => setCurrentSeconds(video.current?.currentTime ?? 0)} />
        <small>Transcript markers use server event timestamps. Voice answer submission is not word-level speech alignment.</small>
        {timeline.map(event => <section key={event.id} className={`interview-timeline-item ${activeEvent?.id === event.id ? 'interview-evidence-active' : ''}`}><Button size="small" onClick={() => { if (video.current) video.current.currentTime = seconds(event) }}>Seek {Math.floor(seconds(event))}s</Button><p>{event.text}</p></section>)}</>}
    </Card>
    <Card title="AI-assisted draft review">
      <Alert type="info" showIcon message="Draft evidence only. Confirm it against the answers; use existing Panel feedback for scores and the final human decision." />
      <h3>Combined interview review</h3>
      {summary ? <>
        <p>{summary.answerCount} submitted answers · {summary.questionCount} questions · {summary.unansweredQuestionEventIds.length} unanswered</p>
        <small>The complete submitted question/answer set was supplied. These are up to two draft highlights, not an exhaustive assessment. An omitted topic or inconsistency does not establish its absence.</small>
        {!summary.observations.length && <p>No grounded combined observation was returned. Review the saved answers manually.</p>}
        {summary.observations.map((item, index) => <section key={index} className="interview-timeline-item">
          <Tag color={item.kind === 'crossAnswerInconsistency' ? 'orange' : undefined}>{labels[item.kind] || item.kind}</Tag><p>{item.note}</p>
          {item.evidence.map((reference, i) => <div key={i}><blockquote>{reference.evidenceQuote}</blockquote><a href={`#interview-event-${reference.answerEventId}`}>Question #{reference.questionEventId} · Answer #{reference.answerEventId} · {utc(reference.answeredAtUtc).toLocaleTimeString()}</a></div>)}
        </section>)}
      </> : <p>{!canAnalyze ? 'AI review is available only for completed interviews with transcription consent.' : errors.active.some(e => e.eventKey.startsWith('review:summary:failed:')) ? 'Combined review needs attention. The full timeline remains available.' : 'Combined review is pending; per-answer drafts may arrive first.'}</p>}
      <h3>Per-answer evidence</h3>
      {!events.some(e => e.kind === 'ai-review') && <p>No completed AI draft yet. Saved answers remain available for manual review.</p>}
      {events.filter(e => e.kind === 'ai-review').flatMap(event => observations(event).map((item, index) => <section key={`${event.id}:${index}`} className="interview-timeline-item"><Tag>{item.kind}</Tag><p>{item.note}</p><blockquote>{item.evidenceQuote}</blockquote><small>Question #{item.questionEventId} · Answer #{item.answerEventId} · {utc(events.find(e => e.id === item.answerEventId)?.createdAtUtc || event.createdAtUtc).toLocaleTimeString()}</small></section>))}
      {events.filter(e => e.kind === 'question' && !events.some(a => a.kind === 'answer' && a.questionEventId === e.id)).map(event => <p key={event.id}><Tag color="orange">Unanswered</Tag>{event.text}</p>)}
      {errors.active.map(event => <Alert key={event.id} type="warning" message={event.text} />)}
      {!!errors.recovered.length && <details><summary>Recovered AI errors ({errors.recovered.length})</summary>{errors.recovered.map(event => <p key={event.id}>{utc(event.createdAtUtc).toLocaleTimeString()} · {event.text} · Resolved by a later saved draft.</p>)}</details>}
      <Button loading={retrying} disabled={!canAnalyze || !errors.active.length} onClick={() => void retry()}>Retry failed AI review</Button>
    </Card>
  </div>
}
