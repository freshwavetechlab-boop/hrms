import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import {
  CalendarOutlined, ClockCircleOutlined, DeleteOutlined, FileProtectOutlined, PauseCircleOutlined, PlayCircleOutlined,
  HistoryOutlined, RollbackOutlined, SearchOutlined, UserOutlined,
} from '@ant-design/icons'
import { Badge, Button, Card, Checkbox, Drawer, Empty, Form, Input, Modal, Popconfirm, Select, Space, Tag, Timeline, message } from 'antd'
import { useAuthSession } from './AuthGate'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import { deleteApplication } from '../services/recruitmentTalentService'
import {
  getRecruitmentApplicationStageHistory, getRecruitmentApplicationTransitions, getRecruitmentJobPostings, getRecruitmentPipelineBoard,
  pauseRecruitmentApplication, resumeRecruitmentApplication, transitionRecruitmentApplication,
} from '../services/recruitmentOrchestrationService'
import type {
  RecruitmentJobPosting, RecruitmentPipelineBoard as Board, RecruitmentPipelineBoardCard,
  RecruitmentApplicationStageTimelineItem, RecruitmentPipelineBoardLane, RecruitmentPipelineTransition,
} from '../types/recruitmentOrchestration'
import type { RecruitmentOpenPosition } from '../types/payroll'
import type { RecruitmentPipelineDisplayMode } from '../types/recruitmentPipelineView'
import { recruitmentPipelineDisplayOptions, recruitmentPipelineDisplayStorageKey } from '../types/recruitmentPipelineView'
import { usePipelineScroller } from '../utils/usePipelineScroller'
import DataTable from './DataTable'
import RecruitmentCandidateActionManager from './RecruitmentCandidateActionManager'
import RecruitmentProcessDocumentPanel from './RecruitmentProcessDocumentPanel'
import './RecruitmentOrchestration.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  positionId?: number
  onOpenCandidate?: (candidateId: number, applicationId: number) => void
  onScheduleInterview?: (applicationId: number) => void
  embedded?: boolean
  displayMode?: RecruitmentPipelineDisplayMode
  onDisplayModeChange?: (mode: RecruitmentPipelineDisplayMode) => void
}
type TransitionDraft = { card: RecruitmentPipelineBoardCard; transition: RecruitmentPipelineTransition; reason: string }
type PauseDraft = { card: RecruitmentPipelineBoardCard; reason: string }
type RecoveryDraft = { lane: RecruitmentPipelineBoardLane; cards: RecruitmentPipelineBoardCard[]; transitions: RecruitmentPipelineTransition[]; transitionId: number; reason: string }
type DocumentDraft = { lane: RecruitmentPipelineBoardLane; card: RecruitmentPipelineBoardCard }
type PipelineTarget = {
  key: string
  clientId: number
  clientName: string
  positionId: number
  postingId: number
  label: string
}
type PipelineTableRow = {
  id: number
  lane: RecruitmentPipelineBoardLane
  card: RecruitmentPipelineBoardCard
  sla: ReturnType<typeof slaState>
  liveElapsed: number
}

export default function RecruitmentPipelineBoard({ initialClientId = 0, clientScopeManaged = false, positionId: suppliedPositionId = 0, onOpenCandidate, onScheduleInterview, embedded = false, displayMode, onDisplayModeChange }: Props) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const [postings, setPostings] = useState<RecruitmentJobPosting[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [postingId, setPostingId] = useState(0)
  const [positionId, setPositionId] = useState(suppliedPositionId)
  const [clientId, setClientId] = useState(initialClientId)
  const [board, setBoard] = useState<Board | null>(null)
  const [query, setQuery] = useState('')
  const [slaFilter, setSlaFilter] = useState('All')
  const [internalViewMode, setInternalViewMode] = useState<RecruitmentPipelineDisplayMode>(() => {
    if (typeof window === 'undefined') return 'pipeline'
    const saved = window.localStorage.getItem(recruitmentPipelineDisplayStorageKey)
    return saved === 'table' || saved === 'both' || saved === 'flow' ? saved : 'pipeline'
  })
  const viewMode = displayMode ?? internalViewMode
  const setViewMode = (mode: RecruitmentPipelineDisplayMode) => {
    if (displayMode === undefined) setInternalViewMode(mode)
    onDisplayModeChange?.(mode)
  }
  const [loadedAt, setLoadedAt] = useState(Date.now())
  const [tick, setTick] = useState(Date.now())
  const [transitions, setTransitions] = useState<Record<number, RecruitmentPipelineTransition[]>>({})
  const [transitionDraft, setTransitionDraft] = useState<TransitionDraft | null>(null)
  const [pauseDraft, setPauseDraft] = useState<PauseDraft | null>(null)
  const [recoveryDraft, setRecoveryDraft] = useState<RecoveryDraft | null>(null)
  const [selectedRecoveryIds, setSelectedRecoveryIds] = useState<Record<number, number[]>>({})
  const [recoveryLoading, setRecoveryLoading] = useState(false)
  const [documentDraft, setDocumentDraft] = useState<DocumentDraft | null>(null)
  const [historyCard, setHistoryCard] = useState<RecruitmentPipelineBoardCard | null>(null)
  const [historyRows, setHistoryRows] = useState<RecruitmentApplicationStageTimelineItem[]>([])
  const [historyLoading, setHistoryLoading] = useState(false)
  const [loading, setLoading] = useState(false)
  const boardRequest = useRef(0)
  const pipelineScroller = usePipelineScroller<HTMLDivElement>()

  const loadTargets = useCallback(async (preferredPositionId = 0, preferredPostingId = 0) => {
    const [allPostings, nextPositions] = await Promise.all([
      getRecruitmentJobPostings(initialClientId),
      getRecruitmentOpenPositions(initialClientId),
    ])
    // Only a published public posting is a candidate-pipeline target. A draft or
    // closed posting must not hide its underlying open position.
    const nextPostings = allPostings.filter(row => row.status === 'Published')
    setPostings(nextPostings)
    setPositions(nextPositions)

    const requestedPositionId = suppliedPositionId || preferredPositionId
    const requestedPosting = nextPostings.find(row => row.id === preferredPostingId && (!requestedPositionId || row.positionId === requestedPositionId))
    const matchingPosition = nextPositions.find(row => row.id === requestedPositionId)
    // An explicit position route is the all-source view. Published application
    // links remain separate targets, while referrals, agencies and Talent Pool
    // candidates without a public PostingId stay visible on the position view.
    const matchingPosting = requestedPosting ?? (!matchingPosition ? nextPostings.find(row => row.positionId === requestedPositionId) : undefined)
    const firstPosting = nextPostings[0]
    const firstPosition = nextPositions[0]
    const nextPositionId = matchingPosting?.positionId ?? matchingPosition?.id ?? firstPosting?.positionId ?? firstPosition?.id ?? 0
    const nextPostingId = matchingPosting?.id ?? (!matchingPosition && firstPosting && nextPositionId === firstPosting.positionId ? firstPosting.id : 0)
    setPositionId(nextPositionId)
    setPostingId(nextPostingId)
    if (!nextPositionId) setBoard(null)
    return { positionId: nextPositionId, postingId: nextPostingId }
  }, [initialClientId, suppliedPositionId])

  useEffect(() => { void loadTargets(suppliedPositionId, 0) }, [loadTargets, suppliedPositionId])
  useEffect(() => { setClientId(initialClientId) }, [initialClientId])
  useEffect(() => { if (suppliedPositionId) setPositionId(suppliedPositionId) }, [suppliedPositionId])
  useEffect(() => { if (positionId) void loadBoard(positionId, postingId || undefined) }, [positionId, postingId])
  useEffect(() => {
    const id = window.setInterval(() => setTick(Date.now()), 1000)
    return () => window.clearInterval(id)
  }, [])
  useEffect(() => {
    if (displayMode === undefined) window.localStorage.setItem(recruitmentPipelineDisplayStorageKey, internalViewMode)
  }, [displayMode, internalViewMode])

  const loadBoard = async (targetPositionId = positionId, targetPostingId?: number) => {
    if (!targetPositionId) return
    const requestId = ++boardRequest.current
    setLoading(true)
    try {
      const next = await getRecruitmentPipelineBoard(targetPositionId, targetPostingId)
      if (requestId !== boardRequest.current) return
      setBoard(next ?? null)
      setLoadedAt(Date.now())
      setTick(Date.now())
      setTransitions({})
      setSelectedRecoveryIds({})
    } finally {
      if (requestId === boardRequest.current) setLoading(false)
    }
  }
  const pipelineTargets = useMemo<PipelineTarget[]>(() => {
    const postingPositionIds = new Set(postings.map(row => row.positionId))
    const postingTargets = postings.map(row => ({
      key: `posting:${row.id}`,
      clientId: row.clientId,
      clientName: row.clientName,
      positionId: row.positionId,
      postingId: row.id,
      label: `${row.positionCode} · ${row.publicTitle || row.positionTitle} (Job posting)`,
    }))
    const positionTargets = positions
      .map(row => ({
        key: `position:${row.id}`,
        clientId: row.clientId,
        clientName: row.clientName,
        positionId: row.id,
        postingId: 0,
        label: `${row.positionCode} · ${row.positionTitle} (${postingPositionIds.has(row.id) ? 'All sources' : 'Open position'})`,
      }))
    return [...postingTargets, ...positionTargets]
  }, [postings, positions])
  const clientOptions = useMemo(() => Array.from(new Map(pipelineTargets.map(row => [row.clientId, row.clientName])).entries()).map(([value, label]) => ({ value, label })), [pipelineTargets])
  const targetOptions = pipelineTargets.filter(row => !clientId || row.clientId === clientId).map(row => ({ value: row.key, label: row.label }))
  const selectedTargetKey = postingId ? `posting:${postingId}` : positionId ? `position:${positionId}` : undefined
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

  const chooseTarget = (key: string) => {
    const target = pipelineTargets.find(row => row.key === key)
    if (!target) return
    setPostingId(target.postingId)
    setPositionId(target.positionId)
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
  const toggleRecoverySelection = (lane: RecruitmentPipelineBoardLane, applicationId: number, selected: boolean) => {
    setSelectedRecoveryIds(current => {
      const ids = current[lane.stageId] ?? []
      return { ...current, [lane.stageId]: selected ? Array.from(new Set([...ids, applicationId])) : ids.filter(id => id !== applicationId) }
    })
  }
  const openRecovery = async (lane: RecruitmentPipelineBoardLane, explicitCards?: RecruitmentPipelineBoardCard[]) => {
    const selectedIds = new Set(selectedRecoveryIds[lane.stageId] ?? [])
    const cards = explicitCards ?? lane.applications.filter(card => selectedIds.has(card.applicationId))
    if (!cards.length) return message.warning('Select at least one rejected candidate.')
    setRecoveryLoading(true)
    try {
      const first = cards[0]
      const available = transitions[first.applicationId] ?? await getRecruitmentApplicationTransitions(first.applicationId)
      setTransitions(current => ({ ...current, [first.applicationId]: available }))
      if (!available.length) return message.warning('No return route is configured for this lane. Add an outgoing transition from this terminal stage in the next pipeline version.')
      setRecoveryDraft({ lane, cards, transitions: available, transitionId: available[0].id, reason: '' })
    } finally { setRecoveryLoading(false) }
  }
  const performRecovery = async () => {
    if (!recoveryDraft) return
    const selectedTransition = recoveryDraft.transitions.find(row => row.id === recoveryDraft.transitionId)
    if (!selectedTransition || (selectedTransition.requiresReason && !recoveryDraft.reason.trim())) return
    setRecoveryLoading(true)
    try {
      const results = await Promise.all(recoveryDraft.cards.map(card => transitionRecruitmentApplication(card.applicationId, selectedTransition.id, recoveryDraft.reason.trim())))
      const completed = results.filter(result => result.ok).length
      if (completed === recoveryDraft.cards.length) message.success(`${completed} candidate(s) returned through ${selectedTransition.actionLabel}.`)
      else message.warning(`${completed} of ${recoveryDraft.cards.length} candidates were returned. Review the remaining candidates individually.`)
      setRecoveryDraft(null)
      await loadBoard(positionId, postingId || undefined)
    } finally { setRecoveryLoading(false) }
  }
  const removeApplication = async (card: RecruitmentPipelineBoardCard) => {
    const response = await deleteApplication(card.applicationId)
    if (response.ok) await loadBoard()
  }
  const openHistory = async (card: RecruitmentPipelineBoardCard) => {
    setHistoryCard(card); setHistoryRows([]); setHistoryLoading(true)
    try { setHistoryRows(await getRecruitmentApplicationStageHistory(card.applicationId)) }
    finally { setHistoryLoading(false) }
  }
  const hasAssignedPipeline = Boolean(board?.pipelineVersionId && board.lanes.length)

  return <section className="orchestration-shell" data-testid="recruitment-hiring-pipeline">
    {!embedded && <div className="orchestration-toolbar">
      <div><span className="orchestration-kicker">Candidate progress</span><h2 className="orchestration-title">Candidate stages</h2><p className="orchestration-subtitle">See where every candidate is, what is due next and how long each stage has taken.</p></div>
      <Select className="pipeline-view-select" aria-label="Pipeline display view" value={viewMode} onChange={setViewMode} options={recruitmentPipelineDisplayOptions} />
    </div>}
    <Card size="small"><div className="orchestration-toolbar">
      <div>{!clientScopeManaged && <Select allowClear value={clientId || undefined} placeholder="All clients" options={clientOptions} onChange={value => { const next = Number(value || 0); setClientId(next); const first = pipelineTargets.find(row => !next || row.clientId === next); if (first) chooseTarget(first.key); else { setPositionId(0); setPostingId(0); setBoard(null) } }} />}<Select aria-label="Pipeline position or job posting" showSearch optionFilterProp="label" value={selectedTargetKey} placeholder="Select position or job posting" options={targetOptions} onChange={chooseTarget} /></div>
      <div><Input allowClear prefix={<SearchOutlined />} value={query} onChange={event => setQuery(event.target.value)} placeholder="Candidate, email or application" /><Select value={slaFilter} onChange={setSlaFilter} options={['All', 'On track', 'Due soon', 'Overdue', 'Paused'].map(value => ({ value, label: value === 'All' ? 'All SLA states' : value }))} />{displayMode === undefined && <Select className="pipeline-view-select" aria-label="Pipeline display view" value={viewMode} onChange={setViewMode} options={recruitmentPipelineDisplayOptions} />}</div>
    </div></Card>
    {!board || !hasAssignedPipeline ? <Card><Empty description={positionId ? 'No published candidate stage flow is assigned to this position.' : targetOptions.length ? 'Select a position or published job posting.' : 'No open positions or published job postings found for this client.'}><Space wrap><Button href={`/recruitment/hiring-pipeline?manage=1${clientId ? `&clientId=${clientId}` : ''}`}>Manage pipeline</Button><Button type="primary" href={`/recruitment/ats-screening?upload=single${clientId ? `&clientId=${clientId}` : ''}`}>Add candidate resume</Button></Space></Empty></Card> : <>
      <Card size="small"><Space wrap><Tag color="purple">{board.positionCode}</Tag><strong>{board.positionTitle}</strong><Tag>Pipeline version #{board.pipelineVersionId}</Tag><span>{board.lanes.reduce((total, lane) => total + lane.applications.length, 0)} application(s)</span></Space></Card>
      {viewMode !== 'table' && <div ref={pipelineScroller.ref} className="pipeline-board" data-testid="pipeline-board-view" tabIndex={0} onKeyDown={pipelineScroller.onKeyDown} aria-label="Scrollable candidate pipeline"><div className="pipeline-board-columns">{filtered.map(lane => <section key={lane.stageId} data-testid={`pipeline-lane-${lane.stageCode}`} className="pipeline-board-column" style={{ '--stage-color': stageColor(lane.stageType) } as CSSProperties}>
        <header><h4>{lane.stageName}</h4><Badge count={lane.applications.length} showZero color={stageColor(lane.stageType)} /></header>
        <div className="pipeline-board-column-body">
          {isRecoveryLane(lane) && !!lane.applications.length && <div className="pipeline-recovery-toolbar"><Checkbox indeterminate={(selectedRecoveryIds[lane.stageId]?.length ?? 0) > 0 && (selectedRecoveryIds[lane.stageId]?.length ?? 0) < lane.applications.length} checked={(selectedRecoveryIds[lane.stageId]?.length ?? 0) === lane.applications.length} onChange={event => setSelectedRecoveryIds(current => ({ ...current, [lane.stageId]: event.target.checked ? lane.applications.map(card => card.applicationId) : [] }))}>Select all</Checkbox><Button data-testid={`pipeline-return-selected-${lane.stageCode}`} size="small" icon={<RollbackOutlined />} loading={recoveryLoading} disabled={!(selectedRecoveryIds[lane.stageId]?.length)} onClick={() => void openRecovery(lane)}>Return selected</Button></div>}
          {!lane.applications.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No candidates" />}
          {lane.applications.map(card => <CandidateCard key={card.applicationId} lane={lane} card={card} elapsedSinceLoad={elapsedSinceLoad} transitions={transitions[card.applicationId] ?? []} recoverySelected={(selectedRecoveryIds[lane.stageId] ?? []).includes(card.applicationId)} onRecoverySelected={isRecoveryLane(lane) ? selected => toggleRecoverySelection(lane, card.applicationId, selected) : undefined} onLoadTransitions={() => void loadTransitions(card.applicationId)} onOpen={onOpenCandidate ? () => onOpenCandidate(card.candidateId, card.applicationId) : undefined} onHistory={() => void openHistory(card)} onSchedule={onScheduleInterview && lane.stageType === 'Interview' ? () => onScheduleInterview(card.applicationId) : undefined} onDocuments={lane.processDocumentRequirements?.length ? () => setDocumentDraft({ lane, card }) : undefined} onDelete={canDelete ? () => void removeApplication(card) : undefined} onPause={() => setPauseDraft({ card, reason: '' })} onResume={() => void resumeApplication(card)} onTransition={transition => setTransitionDraft({ card, transition, reason: '' })} />)}
        </div>
      </section>)}</div></div>}
      {viewMode !== 'pipeline' && <Card size="small" className="pipeline-table-view" data-testid="pipeline-table-view"><DataTable
        rows={tableRows}
        getRowId={row => row.id}
        exportFileName="hiring-pipeline"
        emptyText="No candidates match the selected filters."
        columns={[
          { key: 'candidate', label: 'Candidate', width: '210px', value: row => row.card.candidateName, render: row => <div className="pipeline-table-candidate"><strong>{row.card.candidateName}</strong><small>{row.card.candidateEmail}</small></div> },
          { key: 'application', label: 'Application', width: '150px', value: row => row.card.applicationCode },
          { key: 'stage', label: 'Stage', width: '210px', value: row => row.lane.stageName, render: row => <div className="pipeline-table-candidate"><Tag color={stageColor(row.lane.stageType)}>{row.lane.stageName}</Tag>{row.lane.stageType === 'Rejected' && row.card.rejectedFromStageName && <small>Rejected after {row.card.rejectedFromStageName}</small>}</div> },
          { key: 'ats', label: 'ATS score', width: '100px', value: row => row.card.atsScore ?? '', render: row => row.card.atsScore == null ? '-' : row.card.atsScore.toFixed(1) },
          { key: 'sla', label: 'SLA', width: '160px', value: row => `${row.sla.label} ${row.sla.clock}`, render: row => <Tag className={`sla-chip ${row.sla.className}`}>{row.sla.label}: {row.sla.clock}</Tag> },
          { key: 'timeInStage', label: 'Time in stage', width: '145px', value: row => row.liveElapsed, render: row => formatClock(row.liveElapsed * 1000) },
          { key: 'status', label: 'Status', width: '110px', value: row => row.card.stageStatus },
          { key: 'entered', label: 'Entered', width: '170px', value: row => row.card.enteredAtUtc, render: row => new Date(row.card.enteredAtUtc).toLocaleString('en-IN') },
          { key: 'actions', label: 'Actions', width: '390px', sortable: false, filterable: false, render: row => <Space className="pipeline-table-actions" size={4} wrap>
            {onOpenCandidate && <Button size="small" onClick={() => onOpenCandidate(row.card.candidateId, row.card.applicationId)}>Profile</Button>}
            <Button size="small" icon={<HistoryOutlined />} onClick={() => void openHistory(row.card)}>History</Button>
            {onScheduleInterview && row.lane.stageType === 'Interview' && <Button size="small" icon={<CalendarOutlined />} onClick={() => onScheduleInterview(row.card.applicationId)}>Schedule</Button>}
            {!!row.lane.processDocumentRequirements?.length && <Button size="small" icon={<FileProtectOutlined />} onClick={() => setDocumentDraft({ lane: row.lane, card: row.card })}>Documents</Button>}
            <RecruitmentCandidateActionManager applicationId={row.card.applicationId} candidateName={row.card.candidateName} stageName={row.lane.stageName} stageType={row.lane.stageType} />
            {isRecoveryLane(row.lane) && <Button size="small" icon={<RollbackOutlined />} loading={recoveryLoading} onClick={() => void openRecovery(row.lane, [row.card])}>Return</Button>}
            {row.card.stageStatus === 'Paused' ? <Button size="small" icon={<PlayCircleOutlined />} onClick={() => void resumeApplication(row.card)}>Resume</Button> : row.lane.allowPause && <Button size="small" icon={<PauseCircleOutlined />} onClick={() => setPauseDraft({ card: row.card, reason: '' })}>Pause SLA</Button>}
            <Select size="small" placeholder="Next action" onDropdownVisibleChange={open => open && void loadTransitions(row.card.applicationId)} notFoundContent="No allowed action" options={(transitions[row.card.applicationId] ?? []).map(item => ({ value: item.id, label: item.actionLabel }))} onChange={id => { const transition = (transitions[row.card.applicationId] ?? []).find(item => item.id === id); if (transition) setTransitionDraft({ card: row.card, transition, reason: '' }) }} />
            {canDelete && <Popconfirm title="Delete this application?" description="Interviews, offers and joined records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeApplication(row.card)}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
          </Space> },
        ]}
      /></Card>}
    </>}
    <Modal open={!!transitionDraft} title={transitionDraft ? `${transitionDraft.transition.actionLabel}: ${transitionDraft.card.candidateName}` : 'Move candidate'} onCancel={() => setTransitionDraft(null)} onOk={() => void performTransition()} okButtonProps={{ disabled: Boolean(transitionDraft?.transition.requiresReason && !transitionDraft.reason.trim()) }}>
      {transitionDraft && <Form layout="vertical"><p>This controlled action is audit logged. A mapped approval workflow starts automatically.</p><Form.Item label="Reason / note" required={transitionDraft.transition.requiresReason}><Input.TextArea rows={4} value={transitionDraft.reason} onChange={event => setTransitionDraft({ ...transitionDraft, reason: event.target.value })} placeholder="Add a clear decision note." /></Form.Item></Form>}
    </Modal>
    <Modal open={!!pauseDraft} title={pauseDraft ? `Pause SLA: ${pauseDraft.card.candidateName}` : 'Pause pipeline SLA'} onCancel={() => setPauseDraft(null)} onOk={() => void pauseApplication()} okText="Pause SLA" okButtonProps={{ disabled: !pauseDraft?.reason.trim() }}>
      {pauseDraft && <Form layout="vertical"><p>The stage timer stops until this application is resumed. Paused time is excluded from active SLA time, and the configured due-date policy is applied automatically. Both actions remain audit logged.</p><Form.Item label="Pause reason" required><Input.TextArea rows={4} value={pauseDraft.reason} onChange={event => setPauseDraft({ ...pauseDraft, reason: event.target.value })} placeholder="Why is this application being put on hold?" /></Form.Item></Form>}
    </Modal>
    <Modal data-testid="pipeline-recovery-dialog" open={!!recoveryDraft} title={recoveryDraft ? `Return ${recoveryDraft.cards.length} candidate(s)` : 'Return candidates'} confirmLoading={recoveryLoading} okText="Return selected" onOk={() => void performRecovery()} onCancel={() => setRecoveryDraft(null)} okButtonProps={{ disabled: !recoveryDraft || Boolean(recoveryDraft.transitions.find(row => row.id === recoveryDraft.transitionId)?.requiresReason && !recoveryDraft.reason.trim()) }}>
      {recoveryDraft && <Form layout="vertical"><p>The configured recovery route reopens each selected application while retaining its rejection and stage-time history.</p><Form.Item label="Return route" required><Select value={recoveryDraft.transitionId} options={recoveryDraft.transitions.map(row => ({ value: row.id, label: row.actionLabel }))} onChange={transitionId => setRecoveryDraft({ ...recoveryDraft, transitionId })} /></Form.Item><Form.Item label="Reason / note" required={recoveryDraft.transitions.find(row => row.id === recoveryDraft.transitionId)?.requiresReason}><Input.TextArea rows={3} value={recoveryDraft.reason} onChange={event => setRecoveryDraft({ ...recoveryDraft, reason: event.target.value })} placeholder="Why is this candidate being returned?" /></Form.Item></Form>}
    </Modal>
    <Drawer open={!!documentDraft} width="min(900px, 96vw)" title={documentDraft ? `${documentDraft.card.candidateName} · ${documentDraft.lane.stageName} documents` : 'Stage documents'} onClose={() => setDocumentDraft(null)}>
      {documentDraft && board && <RecruitmentProcessDocumentPanel clientId={board.clientId} applicationId={documentDraft.card.applicationId} pipelineStageId={documentDraft.lane.stageId} requirements={documentDraft.lane.processDocumentRequirements ?? []} title="Candidate MoM, score annexure, proposal and joining documents" />}
    </Drawer>
    <Drawer data-testid="candidate-stage-history" open={!!historyCard} width="min(620px, 96vw)" title={historyCard ? `${historyCard.candidateName} · stage history` : 'Stage history'} onClose={() => setHistoryCard(null)}>
      {historyLoading ? <Card loading /> : !historyRows.length ? <Empty description="No stage history recorded." /> : <Timeline items={historyRows.map(row => ({
        color: row.stageType === 'Rejected' ? 'red' : row.isSlaBreached ? 'orange' : row.status === 'Active' ? 'blue' : 'green',
        children: <div className="pipeline-history-item"><Space wrap><strong>{row.stageName}</strong><Tag>{row.status}</Tag>{row.outcomeCode && <Tag color={row.stageType === 'Rejected' ? 'red' : 'blue'}>{row.outcomeCode}</Tag>}</Space><p>{new Date(row.enteredAtUtc).toLocaleString('en-IN')} · {formatClock(row.activeDurationSeconds * 1000)} active{row.pausedDurationSeconds > 0 ? ` · ${formatClock(row.pausedDurationSeconds * 1000)} paused` : ''}</p>{row.reason && <p>{row.reason}</p>}<small>Recorded by {row.changedByName || 'System'}</small></div>,
      }))} />}
    </Drawer>
  </section>
}

function CandidateCard({ lane, card, elapsedSinceLoad, transitions, recoverySelected, onRecoverySelected, onLoadTransitions, onOpen, onHistory, onSchedule, onDocuments, onDelete, onPause, onResume, onTransition }: {
  lane: RecruitmentPipelineBoardLane
  card: RecruitmentPipelineBoardCard
  elapsedSinceLoad: number
  transitions: RecruitmentPipelineTransition[]
  recoverySelected?: boolean
  onRecoverySelected?: (selected: boolean) => void
  onLoadTransitions: () => void
  onOpen?: () => void
  onHistory: () => void
  onSchedule?: () => void
  onDocuments?: () => void
  onDelete?: () => void
  onPause: () => void
  onResume: () => void
  onTransition: (transition: RecruitmentPipelineTransition) => void
}) {
  const sla = slaState(lane, card, elapsedSinceLoad)
  const liveElapsed = card.elapsedSeconds + (card.stageStatus === 'Paused' ? 0 : elapsedSinceLoad)
  return <Card size="small" className="pipeline-card" data-testid={`pipeline-candidate-${card.applicationId}`}>
    <div className={`pipeline-card-name ${onRecoverySelected ? 'has-selection' : ''}`}>{onRecoverySelected && <Checkbox aria-label={`Select ${card.candidateName} for return`} checked={recoverySelected} onChange={event => onRecoverySelected(event.target.checked)} />}<strong title={card.candidateName}>{card.candidateName}</strong>{card.atsScore == null ? <Tag>Not scored</Tag> : <Tag color={card.atsScore >= 60 ? 'green' : 'orange'}>{card.atsScore.toFixed(1)}</Tag>}</div>
    <div className="pipeline-card-meta"><span><UserOutlined /> {card.applicationCode}</span><span className="pipeline-card-email" title={card.candidateEmail}>{card.candidateEmail}</span>{lane.stageType === 'Rejected' && card.rejectedFromStageName && <span><RollbackOutlined /> Rejected after {card.rejectedFromStageName}{card.rejectedAtUtc ? ` · ${new Date(card.rejectedAtUtc).toLocaleString('en-IN')}` : ''}</span>}<span data-testid="stage-elapsed-clock"><ClockCircleOutlined /> In stage {formatClock(liveElapsed * 1000)}</span>{card.pausedDurationSeconds > 0 && <span>Paused total: {formatClock(card.pausedDurationSeconds * 1000)}</span>}<span>Status: {card.stageStatus}</span>{card.pendingBlockingActionCount > 0 && <Tag color="gold">{card.pendingBlockingActionCount} required action pending</Tag>}{card.failedActionCount > 0 && <Tag color="error">{card.failedActionCount} automation failed</Tag>}</div>
    <div className="pipeline-card-footer"><Tag data-testid="sla-clock" className={`sla-chip ${sla.className}`}>{sla.label}: {sla.clock}</Tag><Space size={4} wrap>
      {onOpen && <Button size="small" onClick={onOpen}>Profile</Button>}
      <Button size="small" icon={<HistoryOutlined />} onClick={onHistory}>History</Button>
      {onSchedule && <Button size="small" icon={<CalendarOutlined />} onClick={onSchedule}>Schedule</Button>}
      {onDocuments && <Button size="small" icon={<FileProtectOutlined />} onClick={onDocuments}>Documents</Button>}
      <RecruitmentCandidateActionManager applicationId={card.applicationId} candidateName={card.candidateName} stageName={lane.stageName} stageType={lane.stageType} />
      {card.stageStatus === 'Paused' ? <Button size="small" icon={<PlayCircleOutlined />} onClick={onResume}>Resume</Button> : lane.allowPause && <Button size="small" icon={<PauseCircleOutlined />} onClick={onPause}>Pause SLA</Button>}
      <Select size="small" placeholder="Next action" style={{ minWidth: 130 }} onDropdownVisibleChange={open => open && onLoadTransitions()} notFoundContent="No allowed action" options={transitions.map(row => ({ value: row.id, label: row.actionLabel }))} onChange={id => { const transition = transitions.find(row => row.id === id); if (transition) onTransition(transition) }} />
      {onDelete && <Popconfirm title="Delete this application?" description="Interviews, offers and joined records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={onDelete}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
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
function isRecoveryLane(lane: RecruitmentPipelineBoardLane) { return lane.isTerminal && ['Rejected', 'Withdrawn'].includes(lane.stageType) }
function formatClock(milliseconds: number) { const seconds = Math.max(0, Math.floor(milliseconds / 1000)); const days = Math.floor(seconds / 86400); const hours = Math.floor((seconds % 86400) / 3600); const minutes = Math.floor((seconds % 3600) / 60); const rest = seconds % 60; return `${days ? `${days}d ` : ''}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}` }
function stageColor(type: string) { return ({ Screening: '#6b4eff', ATS: '#2563eb', Interview: '#0e9f6e', Approval: '#d97706', Offer: '#db2777', Terminal: '#475467' } as Record<string, string>)[type] || '#6b7280' }
