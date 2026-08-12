import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react'
import {
  ApartmentOutlined, ArrowRightOutlined, BranchesOutlined, ClockCircleOutlined, DeploymentUnitOutlined,
  FileDoneOutlined, ProfileOutlined, RocketOutlined, TeamOutlined, UnorderedListOutlined,
} from '@ant-design/icons'
import { Badge, Button, Card, Empty, Segmented, Select, Skeleton, Space, Tag, Tooltip } from 'antd'
import { useNavigate } from 'react-router-dom'
import { getRecruitmentPipelineWorkspace } from '../services/recruitmentOrchestrationService'
import type { RecruitmentPipelineDemandCard, RecruitmentPipelineWorkspace as PipelineWorkspaceResponse, RecruitmentUnifiedPipelineLane } from '../types/recruitmentOrchestration'
import type { RecruitmentPipelineDisplayMode } from '../types/recruitmentPipelineView'
import { recruitmentPipelineDisplayOptions, recruitmentPipelineDisplayStorageKey } from '../types/recruitmentPipelineView'
import { usePipelineScroller } from '../utils/usePipelineScroller'
import DataTable from './DataTable'
import RecruitmentPipelineBoard from './RecruitmentPipelineBoard'
import RecruitmentWorkOrderWorkspace from './RecruitmentWorkOrderWorkspace'
import './RecruitmentPipelineWorkspace.css'

type PipelineView = 'hiring' | 'candidates' | 'orders'
type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  positionId?: number
  initialView?: PipelineView
  canChooseClient?: boolean
  clientOptions?: Array<{ value: number; label: string }>
  onClientChange?: (value?: number) => void
}

const emptyWorkspace: PipelineWorkspaceResponse = { clientId: 0, lanes: [], unassignedDemandCards: [] }

export default function RecruitmentPipelineWorkspace({ initialClientId = 0, clientScopeManaged = false, positionId = 0, initialView = 'hiring', canChooseClient = false, clientOptions = [], onClientChange }: Props) {
  const navigate = useNavigate()
  const [view, setView] = useState<PipelineView>(initialView)
  const [workspace, setWorkspace] = useState<PipelineWorkspaceResponse>(emptyWorkspace)
  const [loading, setLoading] = useState(true)
  const [displayMode, setDisplayMode] = useState<RecruitmentPipelineDisplayMode>(() => {
    if (typeof window === 'undefined') return 'pipeline'
    const saved = window.localStorage.getItem(recruitmentPipelineDisplayStorageKey)
    return saved === 'table' || saved === 'both' || saved === 'flow' ? saved : 'pipeline'
  })

  useEffect(() => { setView(initialView) }, [initialView])
  const load = useCallback(async (silent = false) => {
    if (!silent) setLoading(true)
    try { setWorkspace(await getRecruitmentPipelineWorkspace(initialClientId, positionId)) }
    finally { if (!silent) setLoading(false) }
  }, [initialClientId, positionId])
  useEffect(() => { void load() }, [load])
  useEffect(() => {
    const refresh = () => void load(true)
    const timer = window.setInterval(refresh, 30_000)
    window.addEventListener('focus', refresh)
    return () => { window.clearInterval(timer); window.removeEventListener('focus', refresh) }
  }, [load])
  useEffect(() => { window.localStorage.setItem(recruitmentPipelineDisplayStorageKey, displayMode) }, [displayMode])

  const demandLanes = useMemo(() => workspace.lanes
    .filter(lane => lane.cardScope === 'Position' || lane.demandCards.length)
    .sort((left, right) => left.displayOrder - right.displayOrder), [workspace.lanes])
  const demandCount = workspace.unassignedDemandCards.length + demandLanes.reduce((total, lane) => total + lane.demandCards.length, 0)
  const candidateCount = workspace.lanes.reduce((total, lane) => total + lane.applications.length, 0)

  const setWorkspaceView = (next: PipelineView) => {
    setView(next)
    const params = new URLSearchParams(window.location.search)
    params.set('flow', next)
    const query = params.toString()
    navigate(`${window.location.pathname}${query ? `?${query}` : ''}`, { replace: true })
  }

  return <section className="unified-pipeline-workspace" data-testid="recruitment-unified-pipeline">
    <Card className="pipeline-command-card" size="small">
      <div className="pipeline-command-row">
        <div>
          <span className="orchestration-kicker">One hiring journey</span>
          <h2>Pipeline</h2>
          <p>Follow each client demand from work-order intake and approval through publishing, candidates and joining.</p>
        </div>
        <div className="pipeline-command-controls">
          {canChooseClient
            ? <Select data-testid="pipeline-client-scope" aria-label="Pipeline client scope" allowClear showSearch optionFilterProp="label" value={initialClientId || undefined} placeholder="All accessible clients" options={clientOptions} onChange={value => onClientChange?.(value)} />
            : null}
          <Select className="pipeline-view-select" data-testid="pipeline-display-mode" aria-label="Pipeline display view" value={displayMode} onChange={setDisplayMode} options={recruitmentPipelineDisplayOptions} />
        </div>
      </div>
      <Segmented
        block
        aria-label="Pipeline workspace view"
        value={view}
        onChange={value => setWorkspaceView(value as PipelineView)}
        options={[
          { value: 'hiring', label: <span><BranchesOutlined /> Hiring flow <Badge count={demandCount} showZero /></span> },
          { value: 'candidates', label: <span><TeamOutlined /> Candidates <Badge count={candidateCount} showZero color="#2563eb" /></span> },
          { value: 'orders', label: <span><UnorderedListOutlined /> Work orders</span> },
        ]}
      />
    </Card>

    {displayMode === 'flow' && (loading
      ? <Card><Skeleton active paragraph={{ rows: 5 }} /></Card>
      : <PipelineFlowDiagram view={view} lanes={workspace.lanes} unassigned={workspace.unassignedDemandCards} />)}
    {displayMode !== 'flow' && view === 'hiring' && (loading
      ? <Card><Skeleton active paragraph={{ rows: 6 }} /></Card>
      : <DemandBoard
          clientId={initialClientId}
          lanes={demandLanes}
          unassigned={workspace.unassignedDemandCards}
          displayMode={displayMode}
          onOpenOrders={() => setWorkspaceView('orders')}
        />)}
    {displayMode !== 'flow' && view === 'candidates' && <RecruitmentPipelineBoard embedded key={`candidate-pipeline-${initialClientId}-${positionId}`} initialClientId={initialClientId} clientScopeManaged={clientScopeManaged} positionId={positionId} displayMode={displayMode} onDisplayModeChange={setDisplayMode} />}
    {displayMode !== 'flow' && view === 'orders' && <RecruitmentWorkOrderWorkspace key={`work-orders-${initialClientId}`} initialClientId={initialClientId} clientScopeManaged={clientScopeManaged} displayMode={displayMode} />}
  </section>
}

function PipelineFlowDiagram({ view, lanes, unassigned }: { view: PipelineView; lanes: RecruitmentUnifiedPipelineLane[]; unassigned: RecruitmentPipelineDemandCard[] }) {
  const scroller = usePipelineScroller<HTMLDivElement>({ rootScrollsVertically: true })
  const scopedLanes = lanes
    .filter(lane => view === 'candidates' ? lane.cardScope === 'Application' : view === 'orders' ? lane.cardScope === 'Position' : true)
    .sort((left, right) => left.pipelineVersionId - right.pipelineVersionId || left.displayOrder - right.displayOrder)
  const groups = Array.from(scopedLanes.reduce((map, lane) => {
    const group = map.get(lane.pipelineVersionId) ?? []
    group.push(lane)
    map.set(lane.pipelineVersionId, group)
    return map
  }, new Map<number, RecruitmentUnifiedPipelineLane[]>()))

  if (!groups.length && !unassigned.length) return <Card className="pipeline-empty-card"><Empty description="No configured stages are available for this client scope." /></Card>

  return <section className="pipeline-flow-view" data-testid="pipeline-flow-view">
    <header className="pipeline-flow-heading">
      <div><span className="orchestration-kicker">Live process map</span><h3>{view === 'candidates' ? 'Candidate journey' : view === 'orders' ? 'Work-order journey' : 'Complete hiring journey'}</h3><p>Configured stages, scope hand-offs, live demand and candidate volume in one operational flow.</p></div>
      <Space wrap><Tag color="purple">{scopedLanes.filter(lane => lane.cardScope === 'Position').length} hiring stages</Tag><Tag color="blue">{scopedLanes.filter(lane => lane.cardScope === 'Application').length} candidate stages</Tag></Space>
    </header>
    <div ref={scroller.ref} className="pipeline-flow-scroll" tabIndex={0} onKeyDown={scroller.onKeyDown} aria-label="Scrollable pipeline flow diagram">
      {!!unassigned.length && view !== 'candidates' && <div className="pipeline-flow-group is-unassigned">
        <div className="pipeline-flow-group-label"><span>Awaiting pipeline</span><small>Work-order intake</small></div>
        <FlowNode title="Unassigned intake" type="Position" scope="Position" count={unassigned.length} demandCount={unassigned.length} candidateCount={0} duration={0} order={0} />
      </div>}
      {groups.map(([versionId, versionLanes]) => <div className="pipeline-flow-group" key={versionId} data-testid={`pipeline-flow-version-${versionId}`}>
        <div className="pipeline-flow-group-label"><span>Pipeline v{versionId}</span><small>{versionLanes.length} configured stage{versionLanes.length === 1 ? '' : 's'}</small></div>
        <div className="pipeline-flow-track">
          {versionLanes.map((lane, index) => {
            const previous = versionLanes[index - 1]
            const scopeChanges = Boolean(previous && previous.cardScope !== lane.cardScope)
            return <div className="pipeline-flow-step" key={lane.stageId}>
              {index > 0 && <FlowConnector handoff={scopeChanges} />}
              <FlowNode
                title={lane.stageName}
                type={lane.stageType}
                scope={lane.cardScope}
                count={lane.demandCards.length + lane.applications.length}
                demandCount={lane.demandCards.length}
                candidateCount={lane.applications.length}
                duration={lane.slaDurationMinutes}
                order={lane.displayOrder}
                stageId={lane.stageId}
              />
            </div>
          })}
        </div>
      </div>)}
    </div>
  </section>
}

function FlowConnector({ handoff }: { handoff: boolean }) {
  return <div className={`pipeline-flow-connector ${handoff ? 'is-handoff' : ''}`} data-testid={handoff ? 'pipeline-flow-handoff' : undefined} aria-label={handoff ? 'Position to candidate handoff' : 'Next stage'}>
    {handoff && <span title="Position demand hands off to candidate processing"><DeploymentUnitOutlined /></span>}
    <ArrowRightOutlined />
  </div>
}

function FlowNode({ title, type, scope, count, demandCount, candidateCount, duration, order, stageId }: { title: string; type: string; scope: 'Position' | 'Application'; count: number; demandCount: number; candidateCount: number; duration: number; order: number; stageId?: number }) {
  const color = stageColor(type)
  return <article className={`pipeline-flow-node ${type === 'Approval' ? 'is-decision' : ''}`} data-testid={stageId ? `pipeline-flow-stage-${stageId}` : 'pipeline-flow-unassigned'} style={{ '--flow-color': color } as CSSProperties}>
    <header><span>{order ? String(order).padStart(2, '0') : 'IN'}</span><Badge count={count} showZero color={color} /></header>
    <h4 title={title}>{title}</h4>
    <div className="pipeline-flow-node-meta"><Tag>{scope === 'Position' ? 'Hiring demand' : 'Candidate'}</Tag><span><ClockCircleOutlined /> {durationText(duration)}</span></div>
    <footer><span>{demandCount} demand</span><span>{candidateCount} candidate{candidateCount === 1 ? '' : 's'}</span></footer>
  </article>
}

function DemandBoard({ clientId, lanes, unassigned, displayMode, onOpenOrders }: { clientId: number; lanes: RecruitmentUnifiedPipelineLane[]; unassigned: RecruitmentPipelineDemandCard[]; displayMode: RecruitmentPipelineDisplayMode; onOpenOrders: () => void }) {
  const scroller = usePipelineScroller<HTMLDivElement>()
  if (!lanes.length && !unassigned.length) return <Card className="pipeline-empty-card"><Empty description="No active hiring demand in this client scope."><Space wrap><Button type="primary" onClick={onOpenOrders}>Open work orders</Button><Button href={`/recruitment/requisitions?new=1${clientId ? `&clientId=${clientId}` : ''}`}>New hiring request</Button></Space></Empty></Card>

  const tableRows = [
    ...unassigned.map(card => ({ id: `unassigned-${card.workOrderLineId}`, card, stageName: 'Work-order intake', stageType: 'Position' })),
    ...lanes.flatMap(lane => lane.demandCards.map(card => ({ id: `${lane.stageId}-${card.workOrderLineId}`, card, stageName: lane.stageName, stageType: lane.stageType }))),
  ]
  return <>
    {displayMode !== 'table' && <div ref={scroller.ref} className="demand-pipeline-board" data-testid="pipeline-hiring-flow" tabIndex={0} onKeyDown={scroller.onKeyDown} aria-label="Scrollable hiring pipeline">
      <div className="demand-pipeline-columns">
        {!!unassigned.length && <DemandLane
          key="unassigned"
          clientId={clientId}
          title="Work-order intake"
          color="#6b4eff"
          cards={unassigned}
          helper="Select the approved pipeline so its SLA and next actions can start."
        />}
        {lanes.map(lane => <DemandLane key={lane.stageId} clientId={clientId} title={lane.stageName} color={stageColor(lane.stageType)} cards={lane.demandCards} helper={`${durationText(lane.slaDurationMinutes)} stage target`} />)}
      </div>
    </div>}
    {displayMode !== 'pipeline' && <Card size="small" className="pipeline-demand-table" data-testid="pipeline-demand-table"><DataTable
      rows={tableRows}
      getRowId={row => row.id}
      exportFileName="hiring-demand-pipeline"
      emptyText="No hiring demand matches this client scope."
      columns={[
        { key: 'position', label: 'Hiring demand', width: '220px', value: row => row.card.positionName, render: row => <div className="pipeline-table-candidate"><strong>{row.card.positionName}</strong><small>{row.card.division || row.card.payBandLevelCode || 'Role details pending'}</small></div> },
        { key: 'workOrder', label: 'Work order', width: '190px', value: row => row.card.workOrderNumber },
        { key: 'stage', label: 'Current stage', width: '190px', value: row => row.stageName, render: row => <Tag color={stageColor(row.stageType)}>{row.stageName}</Tag> },
        { key: 'request', label: 'Request', width: '130px', value: row => row.card.requisitionStatus || 'Not started', render: row => <Tag color={statusColor(row.card.requisitionStatus || '')}>{row.card.requisitionStatus || 'Not started'}</Tag> },
        { key: 'jd', label: 'JD', width: '130px', value: row => row.card.jobDescriptionStatus || 'Not started', render: row => <Tag color={statusColor(row.card.jobDescriptionStatus || '')}>{row.card.jobDescriptionStatus || 'Not started'}</Tag> },
        { key: 'posting', label: 'Posting', width: '130px', value: row => row.card.jobPostingStatus || 'Not started', render: row => <Tag color={statusColor(row.card.jobPostingStatus || '')}>{row.card.jobPostingStatus || 'Not started'}</Tag> },
        { key: 'sla', label: 'SLA', width: '170px', value: row => demandSla(row.card) },
        { key: 'actions', label: 'Actions', width: '420px', sortable: false, filterable: false, render: row => <DemandActions card={row.card} clientId={clientId} compact /> },
      ]}
    /></Card>}
  </>
}

function DemandLane({ clientId, title, color, cards, helper }: { clientId: number; title: string; color: string; cards: RecruitmentPipelineDemandCard[]; helper: string }) {
  return <section className="demand-pipeline-lane" style={{ '--lane-color': color } as CSSProperties}>
    <header><div><h3>{title}</h3><small>{helper}</small></div><Badge count={cards.length} showZero color={color} /></header>
    <div className="demand-pipeline-lane-body">
      {!cards.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No hiring demands" />}
      {cards.map(card => <DemandCard key={`${card.workOrderLineId}-${card.hiringCaseId ?? 0}`} card={card} clientId={clientId} />)}
    </div>
  </section>
}

function DemandCard({ card, clientId }: { card: RecruitmentPipelineDemandCard; clientId: number }) {
  const sla = demandSla(card)
  return <Card size="small" className={`demand-card ${card.isSlaBreached ? 'is-breached' : ''}`} data-testid={`pipeline-demand-${card.workOrderLineId}`}>
    <div className="demand-card-heading">
      <div><strong title={card.positionName}>{card.positionName}</strong><span>{card.workOrderNumber}{card.payBandLevelCode ? ` · ${card.payBandLevelCode}` : ''}</span></div>
      <Tag color={card.isSlaBreached ? 'error' : 'processing'}>{card.currentStageName || card.status || 'Intake'}</Tag>
    </div>
    <div className="demand-card-context">
      {card.division && <span><ApartmentOutlined /> {card.division}</span>}
      <span><ClockCircleOutlined /> {sla}</span>
    </div>
    <div className="demand-milestones" aria-label="Hiring milestones">
      <Milestone label="Work order" status={card.workOrderStatus} />
      <Milestone label="Request" status={card.requisitionStatus} />
      <Milestone label="Vacancy" status={card.positionStatus} />
      <Milestone label="JD" status={card.jobDescriptionStatus} />
      <Milestone label="Posting" status={card.jobPostingStatus} />
    </div>
    <div className="demand-card-actions"><DemandActions card={card} clientId={clientId} /></div>
    {card.needsPipelineSelection && <div className="demand-card-callout">Choose a published Position or Hybrid pipeline in the work order to start governed stage tracking.</div>}
  </Card>
}

function DemandActions({ card, clientId, compact = false }: { card: RecruitmentPipelineDemandCard; clientId: number; compact?: boolean }) {
  const navigate = useNavigate()
  const scopedClientId = card.clientId || clientId
  const params = `clientId=${scopedClientId}&workOrderId=${card.workOrderId}&workOrderLineId=${card.workOrderLineId}`
  const requestPath = card.requisitionId
    ? `/recruitment/requisitions?clientId=${scopedClientId}&requisitionId=${card.requisitionId}`
    : `/recruitment/requisitions?new=1&${params}`
  const jdPath = card.requisitionId ? `/recruitment/job-descriptions?clientId=${scopedClientId}&requisitionId=${card.requisitionId}` : ''
  const postingPath = card.positionId ? `/recruitment/job-postings?clientId=${scopedClientId}&positionId=${card.positionId}` : ''
  const resumePath = card.positionId ? `/recruitment/ats-screening?upload=single&clientId=${scopedClientId}&positionId=${card.positionId}` : ''
  return <Space size={compact ? 4 : 6} wrap>
    <Tooltip title="Open the authoritative order, documents and SLA record"><Button className="action-work-order" size="small" icon={<FileDoneOutlined />} onClick={() => navigate(`/recruitment/work-orders-and-sla?${params}`)}>Work order</Button></Tooltip>
    <Button className="action-request" title={requestActionLabel(card)} size="small" icon={<ProfileOutlined />} onClick={() => navigate(requestPath)}>{requestActionLabel(card)}</Button>
    <Button className="action-jd" title={jdActionLabel(card)} size="small" disabled={!jdPath} onClick={() => jdPath && navigate(jdPath)}>{jdActionLabel(card)}</Button>
    <Button className="action-posting" title={postingActionLabel(card)} size="small" disabled={!postingPath} onClick={() => postingPath && navigate(postingPath)}>{postingActionLabel(card)}</Button>
    <Button className="action-resume" title="Add resume" size="small" disabled={!resumePath} icon={<RocketOutlined />} onClick={() => resumePath && navigate(resumePath)}>Add resume</Button>
  </Space>
}

function Milestone({ label, status }: { label: string; status?: string | null }) {
  const value = status?.trim() || 'Not started'
  return <span title={`${label}: ${value}`}><small>{label}</small><Tag color={statusColor(value)}>{value}</Tag></span>
}

function requestActionLabel(card: RecruitmentPipelineDemandCard) {
  if (!card.requisitionId) return 'Create request'
  const status = String(card.requisitionStatus || '').toLowerCase()
  return ['draft', 'sent back'].includes(status) ? 'Continue request' : 'View request'
}
function jdActionLabel(card: RecruitmentPipelineDemandCard) {
  if (!card.requisitionId) return 'JD after approval'
  if (!card.jobDescriptionId) return 'Create JD'
  return ['draft', 'sent back'].includes(String(card.jobDescriptionStatus || '').toLowerCase()) ? 'Continue JD' : 'View JD'
}
function postingActionLabel(card: RecruitmentPipelineDemandCard) {
  if (!card.positionId) return 'Posting after approval'
  if (!card.jobPostingId) return 'Prepare posting'
  return String(card.jobPostingStatus || '').toLowerCase() === 'draft' ? 'Continue posting' : 'View posting'
}
function statusColor(value: string) {
  const status = value.toLowerCase()
  if (['approved', 'published', 'open', 'active', 'completed', 'filled'].some(item => status.includes(item))) return 'success'
  if (['pending', 'submitted', 'in progress', 'screening', 'interview'].some(item => status.includes(item))) return 'processing'
  if (['rejected', 'cancelled', 'failed', 'breached'].some(item => status.includes(item))) return 'error'
  if (['draft', 'sent back', 'on hold', 'paused'].some(item => status.includes(item))) return 'warning'
  return 'default'
}
function demandSla(card: RecruitmentPipelineDemandCard) {
  const due = card.dueAtUtc || card.overallDueAtUtc
  if (!due) return card.needsPipelineSelection ? 'SLA starts after pipeline selection' : 'No due date'
  const minutes = Math.round((new Date(due).getTime() - Date.now()) / 60000)
  if (minutes < 0) return `${durationText(Math.abs(minutes))} overdue`
  return `${durationText(minutes)} remaining`
}
function durationText(minutes: number) {
  if (!minutes) return 'Untimed'
  if (minutes >= 1440) return `${Math.ceil(minutes / 1440)}d`
  if (minutes >= 60) return `${Math.ceil(minutes / 60)}h`
  return `${Math.max(1, minutes)}m`
}
function stageColor(type: string) {
  return ({ Screening: '#6b4eff', ATS: '#2563eb', Interview: '#0e9f6e', Approval: '#d97706', Offer: '#db2777', Position: '#6b4eff' } as Record<string, string>)[type] || '#667085'
}
