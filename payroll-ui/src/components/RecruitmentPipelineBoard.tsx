import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import {
  CalendarOutlined, ClockCircleOutlined, FileProtectOutlined, PauseCircleOutlined, PlayCircleOutlined,
  ReloadOutlined, SearchOutlined, UserOutlined,
} from '@ant-design/icons'
import { Badge, Button, Card, Drawer, Empty, Form, Input, Modal, Select, Space, Tag, message } from 'antd'
import {
  getRecruitmentApplicationTransitions, getRecruitmentJobPostings, getRecruitmentPipelineBoard,
  pauseRecruitmentApplication, resumeRecruitmentApplication, transitionRecruitmentApplication,
} from '../services/recruitmentOrchestrationService'
import type {
  RecruitmentJobPosting, RecruitmentPipelineBoard as Board, RecruitmentPipelineBoardCard,
  RecruitmentPipelineBoardLane, RecruitmentPipelineTransition,
} from '../types/recruitmentOrchestration'
import DataTable from './DataTable'
import RecruitmentCandidateActionManager from './RecruitmentCandidateActionManager'
import RecruitmentProcessDocumentPanel from './RecruitmentProcessDocumentPanel'
import './RecruitmentOrchestration.css'

type Props = {
  positionId?: number
  onOpenCandidate?: (candidateId: number, applicationId: number) => void
  onScheduleInterview?: (applicationId: number) => void
}
type TransitionDraft = { card: RecruitmentPipelineBoardCard; transition: RecruitmentPipelineTransition; reason: string }
type PauseDraft = { card: RecruitmentPipelineBoardCard; reason: string }
type DocumentDraft = { lane: RecruitmentPipelineBoardLane; card: RecruitmentPipelineBoardCard }
type PipelineViewMode = 'pipeline' | 'table' | 'both'
type PipelineTableRow = {
  id: number
  lane: RecruitmentPipelineBoardLane
  card: RecruitmentPipelineBoardCard
  sla: ReturnType<typeof slaState>
  liveElapsed: number
}

const pipelineViewOptions: Array<{ value: PipelineViewMode; label: string }> = [
  { value: 'pipeline', label: 'Pipeline view' },
  { value: 'table', label: 'Table view' },
  { value: 'both', label: 'Both views' },
]
const pipelineViewStorageKey = 'recruitment.pipeline.view'

export default function RecruitmentPipelineBoard({ positionId: suppliedPositionId = 0, onOpenCandidate, onScheduleInterview }: Props) {
  const [postings, setPostings] = useState<RecruitmentJobPosting[]>([])
  const [postingId, setPostingId] = useState(0)
  const [positionId, setPositionId] = useState(suppliedPositionId)
  const [clientId, setClientId] = useState(0)
  const [board, setBoard] = useState<Board | null>(null)
  const [query, setQuery] = useState('')
  const [slaFilter, setSlaFilter] = useState('All')
  const [viewMode, setViewMode] = useState<PipelineViewMode>(() => {
    if (typeof window === 'undefined') return 'pipeline'
    const saved = window.localStorage.getItem(pipelineViewStorageKey)
    return saved === 'table' || saved === 'both' ? saved : 'pipeline'
  })
  const [loadedAt, setLoadedAt] = useState(Date.now())
  const [tick, setTick] = useState(Date.now())
  const [transitions, setTransitions] = useState<Record<number, RecruitmentPipelineTransition[]>>({})
  const [transitionDraft, setTransitionDraft] = useState<TransitionDraft | null>(null)
  const [pauseDraft, setPauseDraft] = useState<PauseDraft | null>(null)
  const [documentDraft, setDocumentDraft] = useState<DocumentDraft | null>(null)
  const [loading, setLoading] = useState(false)
  const boardRequest = useRef(0)

  useEffect(() => {
    void getRecruitmentJobPostings().then(rows => {
      setPostings(rows)
      if (!suppliedPositionId && rows.length) {
        setPositionId(rows[0].positionId)
        setPostingId(rows[0].id)
      }
    })
  }, [suppliedPositionId])
  useEffect(() => { if (suppliedPositionId) setPositionId(suppliedPositionId) }, [suppliedPositionId])
  useEffect(() => { if (positionId) void loadBoard(positionId, postingId || undefined) }, [positionId, postingId])
  useEffect(() => {
    const id = window.setInterval(() => setTick(Date.now()), 1000)
    return () => window.clearInterval(id)
  }, [])
  useEffect(() => { window.localStorage.setItem(pipelineViewStorageKey, viewMode) }, [viewMode])

  const loadBoard = async (targetPositionId = positionId, targetPostingId?: number) => {
    if (!targetPositionId) return
    const requestId = ++boardRequest.current
    setLoading(true)
    const next = await getRecruitmentPipelineBoard(targetPositionId, targetPostingId)
    if (requestId !== boardRequest.current) return
    setLoading(false)
    if (next) setBoard(next)
    setLoadedAt(Date.now())
    setTick(Date.now())
    setTransitions({})
  }
  const clientOptions = useMemo(() => Array.from(new Map(postings.map(row => [row.clientId, row.clientName])).entries()).map(([value, label]) => ({ value, label })), [postings])
  const postingOptions = postings.filter(row => !clientId || row.clientId === clientId).map(row => ({ value: row.id, label: `${row.positionCode} · ${row.publicTitle || row.positionTitle}` }))
  const elapsedSinceLoad = Math.max(0, Math.floor((tick - loadedAt) / 1000))
  const normalizedQuery = query.trim().toLowerCase()
  const filtered = board?.lanes.map(lane => ({
    ...lane,
    applications: lane.applications.filter(card => {
      const searchMatch = !normalizedQuery || `${card.candidateName} ${card.applicationCode} ${card.candidateEmail}`.toLowerCase().includes(normalizedQuery)
      const sla = slaState(lane, card, elapsedSinceLoad)
      return searchMatch && (slaFilter === 'All' || slaFilter === sla.label)
    }),
  })) ?? []
  const tableRows: PipelineTableRow[] = filtered.flatMap(lane => lane.applications.map(card => ({
    id: card.applicationId,
    lane,
    card,
    sla: slaState(lane, card, elapsedSinceLoad),
    liveElapsed: card.elapsedSeconds + (card.stageStatus === 'Paused' ? 0 : elapsedSinceLoad),
  })))

  const choosePosting = (id: number) => {
    const posting = postings.find(row => row.id === id)
    setPostingId(id)
    if (posting) setPositionId(posting.positionId)
  }
  const loadTransitions = async (applicationId: number) => {
    if (transitions[applicationId]) return
    const rows = await getRecruitmentApplicationTransitions(applicationId)
    setTransitions(current => ({ ...current, [applicationId]: rows }))
  }
  const performTransition = async () => {
    if (!transitionDraft) return
    const response = await transitionRecruitmentApplication(transitionDraft.card.applicationId, transitionDraft.transition.id, transitionDraft.reason)
    if (!response.ok) return
    message.success(response.data?.message || `${transitionDraft.card.candidateName} moved successfully.`)
    setTransitionDraft(null)
    await loadBoard(positionId, postingId || undefined)
  }
  const pauseApplication = async () => {
    if (!pauseDraft?.reason.trim()) return
    const response = await pauseRecruitmentApplication(pauseDraft.card.applicationId, pauseDraft.reason.trim())
    if (!response.ok) return
    setPauseDraft(null)
    setSlaFilter('All')
    await loadBoard(positionId, postingId || undefined)
  }
  const resumeApplication = async (card: RecruitmentPipelineBoardCard) => {
    const response = await resumeRecruitmentApplication(card.applicationId)
    if (response.ok) {
      setSlaFilter('All')
      await loadBoard(positionId, postingId || undefined)
    }
  }

  return <section className="orchestration-shell">
    <div className="orchestration-toolbar">
      <div><span className="orchestration-kicker">Talent operations</span><h2 className="orchestration-title">Hiring Pipeline</h2><p className="orchestration-subtitle">Controlled transitions and live SLA clocks computed from persisted stage timestamps.</p></div>
      <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void loadBoard()}>Refresh</Button>
    </div>
    <Card size="small"><div className="orchestration-toolbar">
      <div><Select allowClear value={clientId || undefined} placeholder="All clients" options={clientOptions} onChange={value => { const next = Number(value || 0); setClientId(next); const first = postings.find(row => !next || row.clientId === next); if (first) choosePosting(first.id) }} /><Select showSearch optionFilterProp="label" value={postingId || undefined} placeholder="Select job posting" options={postingOptions} onChange={choosePosting} /></div>
      <div><Input allowClear prefix={<SearchOutlined />} value={query} onChange={event => setQuery(event.target.value)} placeholder="Candidate, email or application" /><Select value={slaFilter} onChange={setSlaFilter} options={['All', 'On track', 'Due soon', 'Overdue', 'Paused'].map(value => ({ value, label: value === 'All' ? 'All SLA states' : value }))} /><Select className="pipeline-view-select" aria-label="Hiring pipeline view" value={viewMode} onChange={setViewMode} options={pipelineViewOptions} /></div>
    </div></Card>
    {!board ? <Card><Empty description={positionId ? 'No published pipeline is assigned to this posting.' : 'Select a job posting.'} /></Card> : <>
      <Card size="small"><Space wrap><Tag color="purple">{board.positionCode}</Tag><strong>{board.positionTitle}</strong><Tag>Pipeline version #{board.pipelineVersionId}</Tag><span>{board.lanes.reduce((total, lane) => total + lane.applications.length, 0)} application(s)</span></Space></Card>
      {viewMode !== 'table' && <div className="pipeline-board" data-testid="pipeline-board-view"><div className="pipeline-board-columns">{filtered.map(lane => <section key={lane.stageId} data-testid={`pipeline-lane-${lane.stageCode}`} className="pipeline-board-column" style={{ '--stage-color': stageColor(lane.stageType) } as CSSProperties}>
        <header><h4>{lane.stageName}</h4><Badge count={lane.applications.length} showZero color={stageColor(lane.stageType)} /></header>
        {!lane.applications.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No candidates" />}
        {lane.applications.map(card => <CandidateCard key={card.applicationId} lane={lane} card={card} elapsedSinceLoad={elapsedSinceLoad} transitions={transitions[card.applicationId] ?? []} onLoadTransitions={() => void loadTransitions(card.applicationId)} onOpen={onOpenCandidate ? () => onOpenCandidate(card.candidateId, card.applicationId) : undefined} onSchedule={onScheduleInterview && lane.stageType === 'Interview' ? () => onScheduleInterview(card.applicationId) : undefined} onDocuments={lane.processDocumentRequirements?.length ? () => setDocumentDraft({ lane, card }) : undefined} onPause={() => setPauseDraft({ card, reason: '' })} onResume={() => void resumeApplication(card)} onTransition={transition => setTransitionDraft({ card, transition, reason: '' })} />)}
      </section>)}</div></div>}
      {viewMode !== 'pipeline' && <Card size="small" className="pipeline-table-view" data-testid="pipeline-table-view"><DataTable
        rows={tableRows}
        getRowId={row => row.id}
        exportFileName="hiring-pipeline"
        emptyText="No candidates match the selected filters."
        columns={[
          { key: 'candidate', label: 'Candidate', width: '210px', value: row => row.card.candidateName, render: row => <div className="pipeline-table-candidate"><strong>{row.card.candidateName}</strong><small>{row.card.candidateEmail}</small></div> },
          { key: 'application', label: 'Application', width: '150px', value: row => row.card.applicationCode },
          { key: 'stage', label: 'Stage', width: '180px', value: row => row.lane.stageName, render: row => <Tag color={stageColor(row.lane.stageType)}>{row.lane.stageName}</Tag> },
          { key: 'ats', label: 'ATS score', width: '100px', value: row => row.card.atsScore ?? '', render: row => row.card.atsScore == null ? '-' : row.card.atsScore.toFixed(1) },
          { key: 'sla', label: 'SLA', width: '160px', value: row => `${row.sla.label} ${row.sla.clock}`, render: row => <Tag className={`sla-chip ${row.sla.className}`}>{row.sla.label}: {row.sla.clock}</Tag> },
          { key: 'timeInStage', label: 'Time in stage', width: '145px', value: row => row.liveElapsed, render: row => formatClock(row.liveElapsed * 1000) },
          { key: 'status', label: 'Status', width: '110px', value: row => row.card.stageStatus },
          { key: 'entered', label: 'Entered', width: '170px', value: row => row.card.enteredAtUtc, render: row => new Date(row.card.enteredAtUtc).toLocaleString('en-IN') },
          { key: 'actions', label: 'Actions', width: '390px', sortable: false, filterable: false, render: row => <Space className="pipeline-table-actions" size={4} wrap>
            {onOpenCandidate && <Button size="small" onClick={() => onOpenCandidate(row.card.candidateId, row.card.applicationId)}>Profile</Button>}
            {onScheduleInterview && row.lane.stageType === 'Interview' && <Button size="small" icon={<CalendarOutlined />} onClick={() => onScheduleInterview(row.card.applicationId)}>Schedule</Button>}
            {!!row.lane.processDocumentRequirements?.length && <Button size="small" icon={<FileProtectOutlined />} onClick={() => setDocumentDraft({ lane: row.lane, card: row.card })}>Documents</Button>}
            <RecruitmentCandidateActionManager applicationId={row.card.applicationId} candidateName={row.card.candidateName} />
            {row.card.stageStatus === 'Paused' ? <Button size="small" icon={<PlayCircleOutlined />} onClick={() => void resumeApplication(row.card)}>Resume</Button> : <Button size="small" icon={<PauseCircleOutlined />} onClick={() => setPauseDraft({ card: row.card, reason: '' })}>Pause</Button>}
            <Select size="small" placeholder="Next action" onDropdownVisibleChange={open => open && void loadTransitions(row.card.applicationId)} notFoundContent="No allowed action" options={(transitions[row.card.applicationId] ?? []).map(item => ({ value: item.id, label: item.actionLabel }))} onChange={id => { const transition = (transitions[row.card.applicationId] ?? []).find(item => item.id === id); if (transition) setTransitionDraft({ card: row.card, transition, reason: '' }) }} />
          </Space> },
        ]}
      /></Card>}
    </>}
    <Modal open={!!transitionDraft} title={transitionDraft ? `${transitionDraft.transition.actionLabel}: ${transitionDraft.card.candidateName}` : 'Move candidate'} onCancel={() => setTransitionDraft(null)} onOk={() => void performTransition()} okButtonProps={{ disabled: Boolean(transitionDraft?.transition.requiresReason && !transitionDraft.reason.trim()) }}>
      {transitionDraft && <Form layout="vertical"><p>This controlled action is audit logged. A mapped approval workflow starts automatically.</p><Form.Item label="Reason / note" required={transitionDraft.transition.requiresReason}><Input.TextArea rows={4} value={transitionDraft.reason} onChange={event => setTransitionDraft({ ...transitionDraft, reason: event.target.value })} placeholder="Add a clear decision note." /></Form.Item></Form>}
    </Modal>
    <Modal open={!!pauseDraft} title={pauseDraft ? `Pause SLA: ${pauseDraft.card.candidateName}` : 'Pause pipeline SLA'} onCancel={() => setPauseDraft(null)} onOk={() => void pauseApplication()} okText="Pause SLA" okButtonProps={{ disabled: !pauseDraft?.reason.trim() }}>
      {pauseDraft && <Form layout="vertical"><p>The live stage timer stops until this application is resumed. Both actions remain audit logged.</p><Form.Item label="Pause reason" required><Input.TextArea rows={4} value={pauseDraft.reason} onChange={event => setPauseDraft({ ...pauseDraft, reason: event.target.value })} placeholder="Why is this application being put on hold?" /></Form.Item></Form>}
    </Modal>
    <Drawer open={!!documentDraft} width="min(900px, 96vw)" title={documentDraft ? `${documentDraft.card.candidateName} · ${documentDraft.lane.stageName} documents` : 'Stage documents'} onClose={() => setDocumentDraft(null)}>
      {documentDraft && board && <RecruitmentProcessDocumentPanel clientId={board.clientId} applicationId={documentDraft.card.applicationId} pipelineStageId={documentDraft.lane.stageId} requirements={documentDraft.lane.processDocumentRequirements ?? []} title="Candidate MoM, score annexure, proposal and joining documents" />}
    </Drawer>
  </section>
}

function CandidateCard({ lane, card, elapsedSinceLoad, transitions, onLoadTransitions, onOpen, onSchedule, onDocuments, onPause, onResume, onTransition }: {
  lane: RecruitmentPipelineBoardLane
  card: RecruitmentPipelineBoardCard
  elapsedSinceLoad: number
  transitions: RecruitmentPipelineTransition[]
  onLoadTransitions: () => void
  onOpen?: () => void
  onSchedule?: () => void
  onDocuments?: () => void
  onPause: () => void
  onResume: () => void
  onTransition: (transition: RecruitmentPipelineTransition) => void
}) {
  const sla = slaState(lane, card, elapsedSinceLoad)
  const liveElapsed = card.elapsedSeconds + (card.stageStatus === 'Paused' ? 0 : elapsedSinceLoad)
  return <Card size="small" className="pipeline-card" data-testid={`pipeline-candidate-${card.applicationId}`}>
    <div className="pipeline-card-name"><strong title={card.candidateName}>{card.candidateName}</strong>{card.atsScore == null ? <Tag>Not scored</Tag> : <Tag color={card.atsScore >= 60 ? 'green' : 'orange'}>{card.atsScore.toFixed(1)}</Tag>}</div>
    <div className="pipeline-card-meta"><span><UserOutlined /> {card.applicationCode}</span><span className="pipeline-card-email" title={card.candidateEmail}>{card.candidateEmail}</span><span data-testid="stage-elapsed-clock"><ClockCircleOutlined /> In stage {formatClock(liveElapsed * 1000)}</span>{card.pausedDurationSeconds > 0 && <span>Paused total: {formatClock(card.pausedDurationSeconds * 1000)}</span>}<span>Status: {card.stageStatus}</span>{card.pendingBlockingActionCount > 0 && <Tag color="gold">{card.pendingBlockingActionCount} required action pending</Tag>}{card.failedActionCount > 0 && <Tag color="error">{card.failedActionCount} automation failed</Tag>}</div>
    <div className="pipeline-card-footer"><Tag data-testid="sla-clock" className={`sla-chip ${sla.className}`}>{sla.label}: {sla.clock}</Tag><Space size={4} wrap>
      {onOpen && <Button size="small" onClick={onOpen}>Profile</Button>}
      {onSchedule && <Button size="small" icon={<CalendarOutlined />} onClick={onSchedule}>Schedule</Button>}
      {onDocuments && <Button size="small" icon={<FileProtectOutlined />} onClick={onDocuments}>Documents</Button>}
      <RecruitmentCandidateActionManager applicationId={card.applicationId} candidateName={card.candidateName} />
      {card.stageStatus === 'Paused' ? <Button size="small" icon={<PlayCircleOutlined />} onClick={onResume}>Resume</Button> : <Button size="small" icon={<PauseCircleOutlined />} onClick={onPause}>Pause</Button>}
      <Select size="small" placeholder="Next action" style={{ minWidth: 130 }} onDropdownVisibleChange={open => open && onLoadTransitions()} notFoundContent="No allowed action" options={transitions.map(row => ({ value: row.id, label: row.actionLabel }))} onChange={id => { const transition = transitions.find(row => row.id === id); if (transition) onTransition(transition) }} />
    </Space></div>
    <small>{lane.stageType} · entered {new Date(card.enteredAtUtc).toLocaleString('en-IN')}</small>
  </Card>
}

function slaState(lane: RecruitmentPipelineBoardLane, card: RecruitmentPipelineBoardCard, elapsedSinceLoad: number) {
  if (card.stageStatus === 'Paused') return { label: 'Paused', className: 'warning', clock: formatClock(card.elapsedSeconds * 1000) }
  if (!card.dueAtUtc) return { label: 'In stage', className: 'safe', clock: formatClock((card.elapsedSeconds + elapsedSinceLoad) * 1000) }
  const remaining = card.remainingSeconds - elapsedSinceLoad
  if (card.isSlaBreached || remaining < 0) return { label: 'Overdue', className: 'danger', clock: formatClock(Math.abs(remaining) * 1000) }
  if (card.isSlaWarning || remaining <= lane.slaWarningMinutes * 60) return { label: 'Due soon', className: 'warning', clock: formatClock(remaining * 1000) }
  return { label: 'On track', className: 'safe', clock: formatClock(remaining * 1000) }
}
function formatClock(milliseconds: number) { const seconds = Math.max(0, Math.floor(milliseconds / 1000)); const days = Math.floor(seconds / 86400); const hours = Math.floor((seconds % 86400) / 3600); const minutes = Math.floor((seconds % 3600) / 60); const rest = seconds % 60; return `${days ? `${days}d ` : ''}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}` }
function stageColor(type: string) { return ({ Screening: '#6b4eff', ATS: '#2563eb', Interview: '#0e9f6e', Approval: '#d97706', Offer: '#db2777', Terminal: '#475467' } as Record<string, string>)[type] || '#6b7280' }
