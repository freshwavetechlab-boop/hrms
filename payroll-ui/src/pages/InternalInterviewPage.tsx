import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Card, Checkbox, Input, Modal, Select, Space, Spin, Tag } from 'antd'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import InternalInterviewSetup from '../components/InternalInterviewSetup'
import InterviewMediaRoom from '../components/InterviewMediaRoom'
import InterviewVoiceControls from '../components/InterviewVoiceControls'
import InterviewEvidenceReview from '../components/InterviewEvidenceReview'
import InterviewParticipantHistory from '../components/InterviewParticipantHistory'
import { getInternalInterviewCapabilities, getInternalInterviewContext, InterviewSessionClient, InterviewSessionError, issueInterviewLink } from '../services/internalInterviewService'
import type { InternalInterviewContext, InternalInterviewEvent, InternalInterviewView, InterviewMediaGrant } from '../types/internalInterviews'
import './InternalInterviewPage.css'
import { sendInterviewInvite } from '../services/recruitmentTalentService'
import { InterviewEventBuffer } from '../services/interviewEventBuffer'
import type { InterviewVoicePublisher } from '../services/interviewVoicePlayback'

const utc = (value: string) => new Date(/Z$|[+-]\d\d:\d\d$/.test(value) ? value : value + 'Z')
function SessionTimer({ started, ended }: { started?: string | null; ended?: string | null }) {
  const [now, setNow] = useState(Date.now())
  useEffect(() => {
    if (!started || ended) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [started, ended])
  const elapsed = started ? Math.max(0, Math.floor(((ended ? utc(ended).getTime() : now) - utc(started).getTime()) / 1000)) : 0
  return <b style={{ fontVariantNumeric: 'tabular-nums' }}>Session timer {Math.floor(elapsed / 60)}:{String(elapsed % 60).padStart(2, '0')}</b>
}
export default function InternalInterviewPage({ candidate = false }: { candidate?: boolean }) {
  const { id } = useParams()
  const { hash } = useLocation()
  return <InterviewSessionScreen key={`${id}:${candidate ? hash : 'panel'}`} candidate={candidate} />
}

function InterviewSessionScreen({ candidate }: { candidate: boolean }) {
  const id = Number(useParams().id)
  const navigate = useNavigate()
  const [token] = useState(() => candidate ? new URLSearchParams(location.hash.slice(1)).get('access') || '' : undefined)
  const client = useMemo(() => new InterviewSessionClient(id, token), [id, token])
  const [view, setView] = useState<InternalInterviewView | null>(null)
  const [context, setContext] = useState<InternalInterviewContext | null>(null)
  const [events, setEvents] = useState<InternalInterviewEvent[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const actionPending = useRef(false)
  const [loaded, setLoaded] = useState(false)
  const [canConfigure, setCanConfigure] = useState(false)
  const [draining, setDraining] = useState(false)
  const [recordConsent, setRecordConsent] = useState(false)
  const [transcriptConsent, setTranscriptConsent] = useState(false)
  const [browserNotice, setBrowserNotice] = useState(false)
  const [grant, setGrant] = useState<InterviewMediaGrant | null>(null)
  const [voicePublisher, setVoicePublisher] = useState<InterviewVoicePublisher | undefined>()
  const [link, setLink] = useState('')
  const [text, setText] = useState('')
  const [confirmation, setConfirmation] = useState<'complete' | 'link' | 'schedule' | null>(null)
  const [sectionSkill, setSectionSkill] = useState<string | undefined>()
  const [setupMode, setSetupMode] = useState<'settings' | 'bank' | null>(null)
  const [evidenceWarning, setEvidenceWarning] = useState('')
  const eventBuffer = useRef<InterviewEventBuffer | null>(null)
  const pageReloadLogged = useRef(false)
  const cursor = useRef(0)
  const polling = useRef(false)
  const container = useRef<HTMLDivElement>(null)
  const activeView = useRef(view); activeView.current = view
  const finished = ['Completed', 'Cancelled', 'No Show'].includes(view?.status || '')
  const refresh = useCallback(async () => {
    if (polling.current) return
    polling.current = true
    try {
      const next = await client.status(); setView(next); setDraining(!!next.draining)
      const additions = await client.events(cursor.current)
      if (additions.length) { cursor.current = additions[additions.length - 1].id; setEvents(current => [...current, ...additions].slice(-10000)) }
      setError('')
    } catch (problem) {
      if (candidate && problem instanceof InterviewSessionError && [401, 403, 404, 410].includes(problem.status)) {
        eventBuffer.current?.close()
        setView(null); setGrant(null); setEvents([]); cursor.current = 0
      }
      setError(problem instanceof Error ? problem.message : 'Interview status unavailable.')
    }
    finally { setLoaded(true); polling.current = false }
  }, [client, candidate])
  useEffect(() => {
    if (!candidate) {
      void getInternalInterviewContext(id).then(result => { if (result.ok) setContext(result.data) })
      void getInternalInterviewCapabilities().then(result => { setCanConfigure(result.canManage); if (result.enabled) setDraining(result.acceptingNewSessions === false) })
    }
    void refresh(); const timer = window.setInterval(() => void refresh(), 3000)
    return () => window.clearInterval(timer)
  }, [id, candidate, refresh])
  useEffect(() => { if (finished) { setGrant(null); eventBuffer.current?.close() } }, [finished])
  useEffect(() => {
    if (!candidate || !token) return
    let disposed = false
    let timer: number | undefined
    // Partition pending evidence by a non-reversible link fingerprint; never persist the bearer link itself.
    void crypto.subtle.digest('SHA-256', new TextEncoder().encode(token)).then(digest => {
      if (disposed) return
      const fingerprint = Array.from(new Uint8Array(digest), x => x.toString(16).padStart(2, '0')).join('')
      let storage: Storage | undefined
      try { storage = sessionStorage } catch { /* memory-only queue with visible warning */ }
      const queue = new InterviewEventBuffer({ key: `hrms-interview-events:${id}:${fingerprint}`, storage,
        send: event => client.event(event.kind, '', undefined, event.offsetMs, event.eventKey),
        onStatus: message => { if (!disposed) setEvidenceWarning(message) } })
      eventBuffer.current = queue
      timer = window.setInterval(() => {
        const current = activeView.current
        if (!current) return
        if (['Completed', 'Cancelled', 'No Show'].includes(current.status)) { queue.close(); return }
        if (!current.consentAtUtc) return
        if (!pageReloadLogged.current) {
          pageReloadLogged.current = true
          if ((performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined)?.type === 'reload') queue.enqueue('page-reload')
        }
        if (navigator.onLine) void queue.flush()
      }, 1000)
    }).catch(() => setEvidenceWarning('Browser evidence buffering is unavailable. Inform the panel; this does not decide your application outcome.'))
    return () => { disposed = true; window.clearInterval(timer); eventBuffer.current?.dispose(); eventBuffer.current = null }
  }, [candidate, client, id, token])
  const act = async (work: () => Promise<unknown>) => {
    if (actionPending.current) return
    actionPending.current = true; setBusy(true); setError('')
    try { await work(); await refresh() }
    catch (problem) { setError(problem instanceof Error ? problem.message : 'Interview action failed.') }
    finally { actionPending.current = false; setBusy(false) }
  }
  const browserEvent = useCallback((kind: string) => {
    if (!candidate || !activeView.current?.consentAtUtc || ['Completed', 'Cancelled', 'No Show'].includes(activeView.current.status)) return
    const started = activeView.current.startedAtUtc
    eventBuffer.current?.enqueue(kind, started ? Math.max(0, Math.min(86400000, Date.now() - utc(started).getTime())) : undefined)
  }, [candidate])
  useEffect(() => {
    if (!candidate) return
    const bindings: Array<[EventTarget, string, EventListener]> = [
      [window, 'blur', () => browserEvent('blur')], [window, 'focus', () => browserEvent('focus')],
      [document, 'visibilitychange', () => browserEvent(document.hidden ? 'visibility-hidden' : 'visibility-visible')],
      [document, 'fullscreenchange', () => { if (!document.fullscreenElement) browserEvent('fullscreen-exit') }],
      [window, 'offline', () => browserEvent('connection-lost')], [window, 'online', () => browserEvent('reconnected')],
      [document, 'copy', () => browserEvent('copy')], [document, 'paste', () => browserEvent('paste')], [window, 'pagehide', () => browserEvent('page-exit')],
    ]
    bindings.forEach(([target, event, callback]) => target.addEventListener(event, callback))
    return () => bindings.forEach(([target, event, callback]) => target.removeEventListener(event, callback))
  }, [candidate, browserEvent])
  const lastQuestion = [...events].reverse().find(event => event.kind === 'question')
  const answered = !!lastQuestion && events.some(event => event.kind === 'answer' && event.questionEventId === lastQuestion.id)
  useEffect(() => { if (candidate) setText('') }, [candidate, lastQuestion?.id])
  const confirmAction = () => act(async () => {
    if (!view || !confirmation) return
    if (confirmation === 'complete') await client.command('complete', view.revision)
    else if (confirmation === 'schedule') { await client.resetSchedule(view.revision); setLink(''); setSetupMode(null) }
    else { const result = await issueInterviewLink(id); if (!result.ok || !result.data) throw new Error(result.error); setLink(result.data.url) }
    setConfirmation(null)
  })
  return <main className="internal-interview-page" ref={container}>
    <header><div><span>TALENT ACQUISITION · INTERNAL INTERVIEW</span><h1>{view?.candidateName || context?.candidateName || 'Interview session'}</h1><p>{view?.positionTitle || context?.positionTitle} · {view?.roundCode || context?.roundCode}</p></div><Space wrap>{view && <Tag color={view.status === 'Live' ? 'green' : 'blue'}>{view.status}</Tag>}{!candidate && <Button onClick={() => navigate('/recruitment/interviews')}>Back to Interview Tracker</Button>}<Button onClick={() => void container.current?.requestFullscreen().catch(() => setError('Fullscreen is unavailable in this browser.'))}>Fullscreen</Button></Space></header>
    {error && <Alert type="warning" showIcon message={error} action={<Button onClick={() => void refresh()}>Refresh</Button>} />}
    {evidenceWarning && <Alert type="info" showIcon message={evidenceWarning} />}
    {draining && <Alert type="info" showIcon message="Interview maintenance: new sessions are paused" description="Live interviews can continue and finish. Waiting candidates should contact HR to reschedule; saved evidence remains available to authorized reviewers." />}
    {!loaded && <Spin tip="Loading secured interview…" />}
    {!view && context && !candidate && canConfigure && !draining && <InternalInterviewSetup context={context} onSaved={() => void refresh()} />}
    {view && <>
      <div className="interview-session-summary"><Tag>{view.configuration.mode} Interview</Tag><span>{utc(view.scheduledStart).toLocaleString(undefined, { timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short' })} · {view.timeZoneId}</span><SessionTimer started={view.startedAtUtc} ended={view.endedAtUtc} /><Tag>Control: {view.control}</Tag></div>
      {candidate && !view.consentAtUtc && !finished && <Card title="Before you join">
        <p>This interview uses your camera and microphone after you choose Join. Focus, visibility, fullscreen, device and connection events may be captured for human review. These signals do not establish cheating and never automatically reject you. Clipboard contents are not collected.</p>
        <Space direction="vertical">
          <Checkbox checked={browserNotice} onChange={e => setBrowserNotice(e.target.checked)}>I have read the interview privacy and browser-event notice.</Checkbox>
          {view.configuration.recordingEnabled && <Checkbox checked={recordConsent} onChange={e => setRecordConsent(e.target.checked)}>I consent to interview audio/video recording and authorized HR replay.</Checkbox>}
          {view.configuration.transcriptionEnabled && <Checkbox checked={transcriptConsent} onChange={e => setTranscriptConsent(e.target.checked)}>I consent to local transcription and an AI-assisted draft for human review.</Checkbox>}
          <p>If you do not consent, contact HR to arrange an alternative. No hiring decision is made by this page.</p>
          <Button type="primary" loading={busy} disabled={draining || !browserNotice || (view.configuration.recordingEnabled && !recordConsent) || (view.configuration.transcriptionEnabled && !transcriptConsent)} onClick={() => void act(() => client.consent(recordConsent, transcriptConsent, view.noticeVersion))}>Agree and enter waiting room</Button>
        </Space>
      </Card>}
      {view.status === 'Waiting' && <Alert showIcon type="info" message={candidate ? 'You are in the waiting room. The panel will start your interview.' : 'Candidate consent received. Ready to start.'} />}
      {view.mediaError && <Alert type="warning" showIcon message="Media needs attention" description={view.mediaError} />}
      {view.mediaState === 'Starting' && <Alert type="info" showIcon message="Preparing the private audio/video room…" />}
      {view.mediaState === 'Closing' && <Alert type="warning" showIcon message="Session ended; the server is still confirming media-room closure. Pending closure will retry." />}
      <Space wrap className="interview-session-actions">
        {!candidate && view.canManage && <Button onClick={() => setSetupMode(setupMode === 'bank' ? null : 'bank')}>{setupMode === 'bank' ? 'Close question bank' : 'Manage job question bank'}</Button>}
        {!candidate && view.canManage && view.status === 'Scheduled' && !view.consentAtUtc && <Button disabled={draining} onClick={() => setSetupMode(setupMode === 'settings' ? null : 'settings')}>Edit internal settings</Button>}
        {!candidate && view.canResetSchedule && <Button type="primary" disabled={busy || draining} onClick={() => setConfirmation('schedule')}>Use updated schedule</Button>}
        {!candidate && !finished && view.canManage && <Button loading={busy} disabled={view.status === 'Live' || draining} onClick={() => void act(async () => { const result = await sendInterviewInvite(id); if (!result.ok) throw new Error(result.error) })}>Send candidate & panel invites</Button>}
        {!candidate && !finished && view.canManage && <Button disabled={view.status === 'Live' || busy || draining} onClick={() => setConfirmation('link')}>Generate candidate link</Button>}
        {!candidate && view.status === 'Waiting' && <Button type="primary" loading={busy} disabled={draining} onClick={() => void act(() => client.command('start', view.revision))}>Start interview</Button>}
        {view.status === 'Live' && !grant && <Button type="primary" loading={busy} onClick={() => void act(async () => setGrant(await client.join()))}>Join audio/video</Button>}
        {!candidate && view.status === 'Live' && <>
          {view.control === 'Human' && view.configuration.mode !== 'Human' && <Select aria-label="AI interview section" placeholder="All remaining approved sections" allowClear style={{ minWidth: 250, maxWidth: '100%' }} value={sectionSkill} onChange={setSectionSkill} options={[...new Set((view.questions || []).map(q => q.skill))].filter(Boolean).map(skill => ({ value: skill, label: skill }))} />}
          <Button onClick={() => void act(() => client.command(view.control === 'AI' ? 'human' : 'ai', view.revision, view.control === 'Human' ? sectionSkill : undefined))} disabled={busy || view.configuration.mode === 'Human'}>{view.control === 'AI' ? 'Resume human control' : 'Hand control to AI'}</Button><Button danger disabled={busy} onClick={() => setConfirmation('complete')}>Complete interview session</Button></>}
      </Space>
      {!candidate && <Modal open={confirmation !== null} getContainer={() => container.current || document.body} title={confirmation === 'complete' ? 'End this session?' : confirmation === 'schedule' ? 'Use the updated schedule?' : 'Generate a new candidate link?'}
        onOk={() => void confirmAction()} onCancel={() => { if (!busy) setConfirmation(null) }} confirmLoading={busy} cancelButtonProps={{ disabled: busy }} closable={!busy} keyboard={!busy} maskClosable={false} destroyOnClose>
        <p>{confirmation === 'complete' ? 'Panel feedback and the final hiring decision remain separate HR actions.' : confirmation === 'schedule' ? 'Previous links and consent will be revoked. The interview has not started; question snapshots and audit history remain available.' : 'The previous link will stop working and candidate consent must be given again.'}</p>
        {error && <Alert type="warning" showIcon message={error} />}
      </Modal>}
      {!candidate && context && view.canManage && setupMode && <InternalInterviewSetup key={setupMode + view.revision} context={context} bankOnly={setupMode === 'bank' || view.status !== 'Scheduled' || !!view.consentAtUtc} initial={{ ...view.configuration, questionIds: (view.questions || []).map(q => q.id) }} onSaved={() => { setSetupMode(null); setLink(''); void refresh() }} />}
      {link && !finished && <Card title="Candidate-only interview link"><Input.TextArea readOnly value={link} autoSize /><Button onClick={() => void navigator.clipboard.writeText(link).catch(() => setError('Clipboard unavailable; copy the link from the field.'))}>Copy candidate link</Button><small>Send this only to the candidate. Panel members use their normal HRMS login.</small></Card>}
      {grant && <InterviewMediaRoom grant={grant} onLeave={() => setGrant(null)} onEvent={browserEvent} onError={setError} onVoicePublisher={publisher => setVoicePublisher(() => publisher)} />}
      {finished && <Alert type="success" message="Interview session ended" description="Session evidence is retained for authorized review. Your application outcome is a separate human HR decision." />}
      {candidate && view.status === 'Live' && view.transcriptionConsent && !answered && <InterviewVoiceControls client={client} question={lastQuestion || events.find(event => event.kind === 'introduction')} answerSeconds={view.configuration.answerSeconds} publishVoice={voicePublisher} onDraft={setText} onError={setError} />}
      {!candidate && view.status === 'Live' && events.some(event => event.kind === 'ai-error') && <Button disabled={busy} onClick={() => void act(() => client.retryAi())}>Retry AI assistance</Button>}
      {!candidate && finished && <InterviewEvidenceReview client={client} events={events} canAnalyze={view.status === 'Completed' && view.transcriptionConsent} onError={setError} />}
      {!candidate && (view.startedAtUtc || finished) && <InterviewParticipantHistory events={events} />}
      {!candidate && finished && <Card title="Human panel outcome"><Space wrap>
        <Button type="primary" onClick={() => navigate(`/recruitment/interviews?feedbackInterviewId=${id}`)}>Panel feedback / rubric</Button>
        {view.canManage && <Button onClick={() => navigate(`/recruitment/interviews?decisionInterviewId=${id}`)}>Final human decision / another round</Button>}
      </Space><p>Uses the existing authorized feedback and interview-result workflow. AI notes do not populate or approve a score, selection or rejection.</p></Card>}
      <div className="internal-interview-columns">
        <Card title="Question / answer timeline">{events.filter(event => ['introduction', 'question', 'answer', 'transcript', 'answer-timeout', 'questions-finished'].includes(event.kind)).map(event => <section key={event.id} id={`interview-event-${event.id}`} className="interview-timeline-item"><small>{utc(event.createdAtUtc).toLocaleTimeString()} · {event.actor} · {event.source}</small><p>{event.text}</p></section>)}
          {!finished && view.status === 'Live' && <>
            {candidate && !view.transcriptionConsent && <Alert type="info" message="Answer directly in the video call. Written/voice-transcribed answers are disabled because transcription consent was not requested." />}
            {candidate && answered && <Tag color="green">Answer saved. Waiting for the next question.</Tag>}
            <Input.TextArea disabled={candidate && (!view.transcriptionConsent || answered)} rows={3} maxLength={8000} value={text} onChange={e => setText(e.target.value)} placeholder={candidate ? 'Type your answer or use the configured voice interview.' : 'Ask an instant question or save a private panel note.'} /><Space wrap><Button aria-label={candidate ? 'Submit answer' : 'Ask question'} aria-busy={busy} disabled={busy || !text.trim() || (candidate && (!lastQuestion || answered || !view.transcriptionConsent)) || (!candidate && view.control !== 'Human')} loading={busy} onClick={() => void act(async () => { await client.event(candidate ? 'answer' : 'question', text, candidate ? lastQuestion?.id : undefined); setText('') })}>{candidate ? 'Submit answer' : 'Ask question'}</Button>{!candidate && <Button disabled={busy || !text.trim()} onClick={() => void act(async () => { await client.event('note', text); setText('') })}>Save panel note</Button>}</Space></>}
        </Card>
        {!candidate && <Card title="Panel notes & review-only session events"><Alert type="info" message="Browser events are self-reported observations, not cheating scores or hiring recommendations." />{events.filter(event => !['question', 'answer', 'transcript', 'ai-review', 'ai-summary'].includes(event.kind) && (event.kind !== 'ai-error' || !finished)).map(event => <section key={event.id} className="interview-timeline-item"><small>{utc(event.createdAtUtc).toLocaleTimeString()} · {event.kind} · {event.source}</small>{event.text && <p>{event.text}</p>}</section>)}</Card>}
      </div>
    </>}
  </main>
}
