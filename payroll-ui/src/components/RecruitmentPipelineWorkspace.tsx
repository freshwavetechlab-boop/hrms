import { useCallback, useEffect, useMemo, useState, type CSSProperties } from 'react'
import {
  ApartmentOutlined, ArrowRightOutlined, BranchesOutlined, ClockCircleOutlined, DeploymentUnitOutlined, HistoryOutlined,
  FileDoneOutlined, FileTextOutlined, FormOutlined, PauseCircleOutlined, PlayCircleOutlined, ProfileOutlined, RocketOutlined, SettingOutlined, TeamOutlined, UnorderedListOutlined,
} from '@ant-design/icons'
import { Badge, Button, Card, Drawer, Empty, Form, Input, Modal, Segmented, Select, Skeleton, Space, Tabs, Tag, Tooltip, message } from 'antd'
import { useNavigate } from 'react-router-dom'
import { getRecruitmentPipelineWorkspace } from '../services/recruitmentOrchestrationService'
import { advanceRecruitmentHiringCase, getRecruitmentHiringCaseTransitions, pauseRecruitmentHiringCase, resumeRecruitmentHiringCase, startRecruitmentHiringCase } from '../services/recruitmentCaseService'
import { getDropdowns } from '../services/settingsService'
import type { Drop } from '../types/payroll'
import type { RecruitmentPipelineBoardCard, RecruitmentPipelineDemandCard, RecruitmentPipelineWorkspace as PipelineWorkspaceResponse, RecruitmentUnifiedPipelineLane } from '../types/recruitmentOrchestration'
import type { RecruitmentPipelineDisplayMode } from '../types/recruitmentPipelineView'
import { recruitmentPipelineDisplayOptions, recruitmentPipelineDisplayStorageKey } from '../types/recruitmentPipelineView'
import { usePipelineScroller } from '../utils/usePipelineScroller'
import DataTable from './DataTable'
import { useAuthSession } from './AuthGate'
import RecruitmentHiringTemplateManager from './RecruitmentAdminSettings'
import RecruitmentFormBuilder from './RecruitmentFormBuilder'
import RecruitmentPipelineBoard from './RecruitmentPipelineBoard'
import RecruitmentPipelineDesigner from './RecruitmentPipelineDesigner'
import RecruitmentWorkOrderWorkspace from './RecruitmentWorkOrderWorkspace'
import './RecruitmentPipelineWorkspace.css'

type PipelineView = 'hiring' | 'candidates' | 'orders'
type PipelineManagerTool = 'pipeline' | 'forms' | 'templates'
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
  const session = useAuthSession()
  const navigate = useNavigate()
  const hasClientScope = initialClientId > 0
  const canManagePipeline = Boolean(session?.user.permissions.some(permission =>
    permission === 'settings.manage' || permission === 'recruitment.manage'))
  const canOperateHiringJourney = Boolean(session?.user.permissions.some(permission =>
    permission === 'settings.manage' || permission === 'recruitment.manage' || permission === 'recruitment.hiring-case.manage'))
  const [view, setView] = useState<PipelineView>(initialView)
  const [workspace, setWorkspace] = useState<PipelineWorkspaceResponse>(emptyWorkspace)
  const [workspaceSyncedAt, setWorkspaceSyncedAt] = useState(Date.now())
  const [clockNow, setClockNow] = useState(Date.now())
  const [loading, setLoading] = useState(hasClientScope)
  const [designerOpen, setDesignerOpen] = useState(() => typeof window !== 'undefined' && new URLSearchParams(window.location.search).get('manage') === '1')
  const [managerTool, setManagerTool] = useState<PipelineManagerTool>(() => {
    const requested = typeof window === 'undefined' ? '' : new URLSearchParams(window.location.search).get('tool')
    return requested === 'forms' || requested === 'templates' ? requested : 'pipeline'
  })
  const [managerRevision, setManagerRevision] = useState(0)
  const [designerDropdowns, setDesignerDropdowns] = useState<Drop[]>([])
  const [displayMode, setDisplayMode] = useState<RecruitmentPipelineDisplayMode>(() => {
    if (typeof window === 'undefined') return 'pipeline'
    const saved = window.localStorage.getItem(recruitmentPipelineDisplayStorageKey)
    return saved === 'table' || saved === 'both' || saved === 'flow' ? saved : 'pipeline'
  })

  useEffect(() => { setView(initialView) }, [initialView])
  const load = useCallback(async (silent = false) => {
    if (!initialClientId) {
      setWorkspace(emptyWorkspace)
      setLoading(false)
      return
    }
    if (!silent) setLoading(true)
    try {
      let nextWorkspace = await getRecruitmentPipelineWorkspace(initialClientId, positionId)
      const automaticStarts = canOperateHiringJourney
        ? [...nextWorkspace.unassignedDemandCards, ...nextWorkspace.lanes.flatMap(lane => lane.demandCards)]
          .filter(card => !card.hiringCaseId && card.requisitionId && card.pipelineVersionId && !card.needsPipelineSelection && !['Completed', 'Cancelled'].includes(card.status))
        : []
      if (automaticStarts.length) {
        const results = await Promise.all(automaticStarts.map(card => startRecruitmentHiringCase(card.workOrderLineId, card.pipelineVersionId!, true)))
        if (results.some(result => result.ok && result.data)) nextWorkspace = await getRecruitmentPipelineWorkspace(initialClientId, positionId)
      }
      setWorkspace(nextWorkspace)
      setWorkspaceSyncedAt(Date.now())
    }
    finally { if (!silent) setLoading(false) }
  }, [canOperateHiringJourney, initialClientId, positionId])
  useEffect(() => { void load() }, [load])
  useEffect(() => {
    const timer = window.setInterval(() => setClockNow(Date.now()), 1_000)
    return () => window.clearInterval(timer)
  }, [])
  useEffect(() => {
    const refresh = () => void load(true)
    const timer = window.setInterval(refresh, 30_000)
    window.addEventListener('focus', refresh)
    return () => { window.clearInterval(timer); window.removeEventListener('focus', refresh) }
  }, [load])
  useEffect(() => { window.localStorage.setItem(recruitmentPipelineDisplayStorageKey, displayMode) }, [displayMode])
  useEffect(() => { if (designerOpen && !designerDropdowns.length) void getDropdowns().then(setDesignerDropdowns) }, [designerOpen, designerDropdowns.length])

  const journeyLanes = useMemo(() => [...workspace.lanes]
    .sort((left, right) => left.pipelineVersionId - right.pipelineVersionId || left.displayOrder - right.displayOrder), [workspace.lanes])
  const demandCount = workspace.unassignedDemandCards.length + journeyLanes.reduce((total, lane) => total + lane.demandCards.length, 0)
  const candidateCount = workspace.lanes.reduce((total, lane) => total + lane.applications.length, 0)
  const workspaceHeading = view === 'orders' ? 'Work orders & SLA' : view === 'candidates' ? 'Candidate progression' : 'Demand to joining'

  const setWorkspaceView = (next: PipelineView) => {
    setView(next)
    const params = new URLSearchParams(window.location.search)
    params.set('flow', next)
    const query = params.toString()
    navigate(`${window.location.pathname}${query ? `?${query}` : ''}`, { replace: true })
  }
  const setPipelineManagerOpen = (open: boolean) => {
    setDesignerOpen(open)
    const params = new URLSearchParams(window.location.search)
    if (open) params.set('manage', '1')
    else { params.delete('manage'); params.delete('tool') }
    const query = params.toString()
    navigate(`${window.location.pathname}${query ? `?${query}` : ''}`, { replace: true })
  }
  const setPipelineManagerTool = (tool: PipelineManagerTool) => {
    setManagerTool(tool)
    const params = new URLSearchParams(window.location.search)
    params.set('manage', '1')
    if (tool === 'pipeline') params.delete('tool')
    else params.set('tool', tool)
    const query = params.toString()
    navigate(`${window.location.pathname}?${query}`, { replace: true })
  }

  return <section className="unified-pipeline-workspace" data-testid="recruitment-unified-pipeline">
    <Card className="pipeline-command-card" size="small">
      <div className="pipeline-command-row">
        <div>
          <span className="orchestration-kicker">One hiring journey</span>
          <h2>{workspaceHeading}</h2>
          <p>Follow each client demand from work-order intake and approval through publishing, candidates and joining.</p>
        </div>
        <div className="pipeline-command-controls">
          {canManagePipeline && <Button data-testid="manage-hiring-pipeline" icon={<SettingOutlined />} disabled={!hasClientScope} onClick={() => setPipelineManagerOpen(true)}>Manage pipeline</Button>}
          {canChooseClient
            ? <Select data-testid="pipeline-client-scope" aria-label="Pipeline client scope" allowClear showSearch optionFilterProp="label" value={initialClientId || undefined} placeholder="Select a client" options={clientOptions} onChange={value => onClientChange?.(value)} />
            : null}
          <Select className="pipeline-view-select" data-testid="pipeline-display-mode" aria-label="Pipeline display view" disabled={!hasClientScope} value={displayMode} onChange={setDisplayMode} options={recruitmentPipelineDisplayOptions} />
        </div>
      </div>
      <Segmented
        block
        aria-label="Pipeline workspace view"
        value={view}
        onChange={value => setWorkspaceView(value as PipelineView)}
        options={[
          { value: 'hiring', disabled: !hasClientScope, label: <span><BranchesOutlined /> Hiring flow <Badge count={demandCount} showZero /></span> },
          { value: 'candidates', disabled: !hasClientScope, label: <span><TeamOutlined /> Candidates <Badge count={candidateCount} showZero color="#2563eb" /></span> },
          { value: 'orders', disabled: !hasClientScope, label: <span><UnorderedListOutlined /> Work orders</span> },
        ]}
      />
    </Card>

    {!hasClientScope && <Card className="pipeline-empty-card" data-testid="pipeline-client-required"><Empty description={canChooseClient ? 'Select a client to load its configured hiring pipeline and candidates.' : 'A client scope is required to load the hiring pipeline.'} /></Card>}
    {hasClientScope && displayMode === 'flow' && (loading
      ? <Card><Skeleton active paragraph={{ rows: 5 }} /></Card>
      : <PipelineFlowDiagram view={view} lanes={workspace.lanes} unassigned={workspace.unassignedDemandCards} />)}
    {hasClientScope && displayMode !== 'flow' && view === 'hiring' && (loading
      ? <Card><Skeleton active paragraph={{ rows: 6 }} /></Card>
      : <DemandBoard
          clientId={initialClientId}
          lanes={journeyLanes}
          unassigned={workspace.unassignedDemandCards}
          displayMode={displayMode}
          clockNow={clockNow}
          workspaceSyncedAt={workspaceSyncedAt}
          onOpenOrders={() => setWorkspaceView('orders')}
          onOpenCandidates={() => setWorkspaceView('candidates')}
          onChanged={() => void load(true)}
        />)}
    {hasClientScope && displayMode !== 'flow' && view === 'candidates' && <RecruitmentPipelineBoard embedded key={`candidate-pipeline-${initialClientId}-${positionId}`} initialClientId={initialClientId} clientScopeManaged={clientScopeManaged} positionId={positionId} displayMode={displayMode} onDisplayModeChange={setDisplayMode} />}
    {hasClientScope && displayMode !== 'flow' && view === 'orders' && <RecruitmentWorkOrderWorkspace key={`work-orders-${initialClientId}`} initialClientId={initialClientId} clientScopeManaged={clientScopeManaged} displayMode={displayMode} />}
    <Drawer className="recruitment-pipeline-manager-drawer" title="Manage hiring automation" open={designerOpen && canManagePipeline && hasClientScope} width="min(1480px, 98vw)" onClose={() => setPipelineManagerOpen(false)} destroyOnClose>
      <Tabs className="recruitment-automation-tabs" activeKey={managerTool} onChange={key => setPipelineManagerTool(key as PipelineManagerTool)} items={[
        { key: 'pipeline', label: <span><BranchesOutlined aria-hidden /> Pipeline design</span>, children: <RecruitmentPipelineDesigner key={`pipeline-manager-${initialClientId}-${managerRevision}`} initialClientId={initialClientId} dropdowns={designerDropdowns} onDropdownsChange={setDesignerDropdowns} onSaved={() => void load(true)} /> },
        { key: 'forms', label: <span><FormOutlined aria-hidden /> Candidate forms</span>, children: <RecruitmentFormBuilder key={`pipeline-forms-${initialClientId}`} initialClientId={initialClientId} clientScopeManaged onSaved={() => setManagerRevision(value => value + 1)} /> },
        { key: 'templates', label: <span><FileTextOutlined aria-hidden /> Hiring templates</span>, children: <RecruitmentHiringTemplateManager initialClientId={initialClientId} onSaved={() => setManagerRevision(value => value + 1)} /> },
      ]} />
    </Drawer>
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
        <FlowNode title="Unassigned intake" type="Position" scope="Position" count={unassigned.length} demandCount={unassigned.length} candidateCount={0} targetLabel="SLA not started" order={0} />
      </div>}
      {groups.map(([versionId, versionLanes]) => <div className="pipeline-flow-group" key={versionId} data-testid={`pipeline-flow-version-${versionId}`}>
        <div className="pipeline-flow-group-label"><span>{versionLanes[0]?.pipelineName || 'Hiring pipeline'}</span><small>{versionLanes[0]?.clientName ? `${versionLanes[0].clientName} · ` : ''}v{versionLanes[0]?.pipelineVersionNumber || versionId} · {versionLanes.length} stage{versionLanes.length === 1 ? '' : 's'}</small></div>
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
                targetLabel={laneTargetLabel(lane)}
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

function FlowNode({ title, type, scope, count, demandCount, candidateCount, targetLabel, order, stageId }: { title: string; type: string; scope: 'Position' | 'Application'; count: number; demandCount: number; candidateCount: number; targetLabel: string; order: number; stageId?: number }) {
  const color = stageColor(type)
  return <article className={`pipeline-flow-node ${type === 'Approval' ? 'is-decision' : ''}`} data-testid={stageId ? `pipeline-flow-stage-${stageId}` : 'pipeline-flow-unassigned'} style={{ '--flow-color': color } as CSSProperties}>
    <header><span>{order ? String(order).padStart(2, '0') : 'IN'}</span><Badge count={count} showZero color={color} /></header>
    <h4 title={title}>{title}</h4>
    <div className="pipeline-flow-node-meta"><Tag>{scope === 'Position' ? 'Hiring demand' : 'Candidate'}</Tag><span><ClockCircleOutlined /> {targetLabel}</span></div>
    <footer><span>{demandCount} demand</span><span>{candidateCount} candidate{candidateCount === 1 ? '' : 's'}</span></footer>
  </article>
}

function DemandBoard({ clientId, lanes, unassigned, displayMode, clockNow, workspaceSyncedAt, onOpenOrders, onOpenCandidates, onChanged }: { clientId: number; lanes: RecruitmentUnifiedPipelineLane[]; unassigned: RecruitmentPipelineDemandCard[]; displayMode: RecruitmentPipelineDisplayMode; clockNow: number; workspaceSyncedAt: number; onOpenOrders: () => void; onOpenCandidates: () => void; onChanged: () => void }) {
  const scroller = usePipelineScroller<HTMLDivElement>()
  if (!lanes.length && !unassigned.length) return <Card className="pipeline-empty-card"><Empty description="No hiring pipeline activity in this client scope."><Space wrap><Button type="primary" onClick={onOpenOrders}>Open work orders</Button><Button href={`/recruitment/requisitions?new=1&clientId=${clientId}`}>New hiring request</Button></Space></Empty></Card>

  const stageNameCounts = lanes.reduce((counts, lane) => {
    const key = lane.stageName.trim().toLowerCase()
    counts.set(key, (counts.get(key) ?? 0) + 1)
    return counts
  }, new Map<string, number>())

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
          applications={[]}
          helper="Select the approved pipeline so its SLA and next actions can start."
          clockNow={clockNow}
          workspaceSyncedAt={workspaceSyncedAt}
          onOpenCandidates={onOpenCandidates}
          onChanged={onChanged}
        />}
        {lanes.map(lane => <DemandLane key={lane.stageId} clientId={clientId} title={lane.stageName} color={stageColor(lane.stageType)} cards={lane.demandCards} applications={lane.applications} helper={laneTargetLabel(lane)} context={(stageNameCounts.get(lane.stageName.trim().toLowerCase()) ?? 0) > 1 || lanes.length > 1 ? `${lane.pipelineName || 'Hiring pipeline'} · ${lane.clientName || 'Client'} · v${lane.pipelineVersionNumber || lane.pipelineVersionId}` : lane.pipelineName} clockNow={clockNow} workspaceSyncedAt={workspaceSyncedAt} onOpenCandidates={onOpenCandidates} onChanged={onChanged} />)}
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
        { key: 'sla', label: 'Live SLA', width: '260px', value: row => demandSla(row.card, clockNow, workspaceSyncedAt) },
        { key: 'actions', label: 'Actions', width: '420px', sortable: false, filterable: false, render: row => <DemandActions card={row.card} clientId={clientId} compact onChanged={onChanged} /> },
      ]}
    /></Card>}
  </>
}

function DemandLane({ clientId, title, color, cards, applications, helper, context, clockNow, workspaceSyncedAt, onOpenCandidates, onChanged }: { clientId: number; title: string; color: string; cards: RecruitmentPipelineDemandCard[]; applications: RecruitmentPipelineBoardCard[]; helper: string; context?: string; clockNow: number; workspaceSyncedAt: number; onOpenCandidates: () => void; onChanged: () => void }) {
  const totalCards = cards.length + applications.length
  return <section className="demand-pipeline-lane" style={{ '--lane-color': color } as CSSProperties}>
    <header><div>{context && <span className="demand-lane-context" title={context}>{context}</span>}<h3>{title}</h3><small>{helper}</small></div><Badge count={totalCards} showZero color={color} /></header>
    <div className="demand-pipeline-lane-body">
      {!totalCards && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No records in this stage" />}
      {cards.map(card => <DemandCard key={`${card.workOrderLineId}-${card.hiringCaseId ?? 0}`} card={card} clientId={clientId} clockNow={clockNow} workspaceSyncedAt={workspaceSyncedAt} onChanged={onChanged} />)}
      {applications.map(card => <JourneyCandidateCard key={card.applicationId} card={card} clockNow={clockNow} workspaceSyncedAt={workspaceSyncedAt} onOpen={onOpenCandidates} />)}
    </div>
  </section>
}

function JourneyCandidateCard({ card, clockNow, workspaceSyncedAt, onOpen }: { card: RecruitmentPipelineBoardCard; clockNow: number; workspaceSyncedAt: number; onOpen: () => void }) {
  const sla = candidateSla(card, clockNow, workspaceSyncedAt)
  return <Card size="small" className={`journey-candidate-card ${card.isSlaBreached ? 'is-breached' : ''}`} data-testid={`journey-candidate-${card.applicationId}`}>
    <div className="journey-candidate-heading"><div><strong title={card.candidateName}>{card.candidateName}</strong><span>{card.applicationCode}</span></div>{card.atsScore == null ? <Tag>Not scored</Tag> : <Tag color={card.atsScore >= 60 ? 'green' : 'orange'}>{card.atsScore.toFixed(1)}</Tag>}</div>
    <div className="journey-candidate-meta"><span><TeamOutlined /> Candidate</span>{card.candidateEmail && <span title={card.candidateEmail}>{card.candidateEmail}</span>}{card.rejectedFromStageName && <span><BranchesOutlined /> Rejected after {card.rejectedFromStageName}</span>}<span><ClockCircleOutlined /> {sla}</span></div>
    <div className="journey-candidate-footer"><Tag color={statusColor(card.stageStatus)}>{card.stageStatus}</Tag><Button size="small" type="link" onClick={onOpen}>Manage candidate</Button></div>
  </Card>
}

function DemandCard({ card, clientId, clockNow, workspaceSyncedAt, onChanged }: { card: RecruitmentPipelineDemandCard; clientId: number; clockNow: number; workspaceSyncedAt: number; onChanged: () => void }) {
  const sla = demandSla(card, clockNow, workspaceSyncedAt)
  return <Card size="small" className={`demand-card ${card.isSlaBreached ? 'is-breached' : ''}`} data-testid={`pipeline-demand-${card.workOrderLineId}`}>
    <div className="demand-card-heading">
      <div><strong title={card.positionName}>{card.positionName}</strong><span>{card.workOrderNumber}{card.payBandLevelCode ? ` · ${card.payBandLevelCode}` : ''}</span></div>
      <Tag color={card.isSlaBreached ? 'error' : 'processing'}>{card.currentStageName || card.status || 'Intake'}</Tag>
    </div>
    <div className="demand-card-context">
      {card.division && <span><ApartmentOutlined /> {card.division}</span>}
      <span className="demand-live-sla" data-testid={`demand-sla-timer-${card.workOrderLineId}`}><ClockCircleOutlined /> {sla}</span>
    </div>
    <div className="demand-milestones" aria-label="Hiring milestones">
      <Milestone label="Work order" status={card.workOrderStatus} />
      <Milestone label="Request" status={card.requisitionStatus} />
      <Milestone label="Vacancy" status={card.positionStatus} />
      <Milestone label="JD" status={card.jobDescriptionStatus} />
      <Milestone label="Posting" status={card.jobPostingStatus} />
    </div>
    <div className="demand-card-actions"><DemandActions card={card} clientId={clientId} onChanged={onChanged} /></div>
    {card.needsPipelineSelection && <div className="demand-card-callout">Choose a published Position or Hybrid pipeline in the work order to start governed stage tracking.</div>}
  </Card>
}

function DemandActions({ card, clientId, compact = false, onChanged }: { card: RecruitmentPipelineDemandCard; clientId: number; compact?: boolean; onChanged: () => void }) {
  const navigate = useNavigate()
  const [busyAction, setBusyAction] = useState<'pause' | 'resume' | 'move' | ''>('')
  const [pauseOpen, setPauseOpen] = useState(false)
  const [pauseReason, setPauseReason] = useState('')
  const [moveOpen, setMoveOpen] = useState(false)
  const [moveReason, setMoveReason] = useState('')
  const [moveOptions, setMoveOptions] = useState<Awaited<ReturnType<typeof getRecruitmentHiringCaseTransitions>>>([])
  const [moveOutcome, setMoveOutcome] = useState('ADVANCE')
  const [moveCaseId, setMoveCaseId] = useState(card.hiringCaseId || 0)
  const scopedClientId = card.clientId || clientId
  const params = `clientId=${scopedClientId}&workOrderId=${card.workOrderId}&workOrderLineId=${card.workOrderLineId}${card.hiringCaseId ? `&hiringCaseId=${card.hiringCaseId}` : ''}`
  const requestPath = card.requisitionId
    ? `/recruitment/requisitions?clientId=${scopedClientId}&requisitionId=${card.requisitionId}`
    : `/recruitment/requisitions?new=1&${params}`
  const jdPath = card.requisitionId ? `/recruitment/job-descriptions?clientId=${scopedClientId}&requisitionId=${card.requisitionId}` : ''
  const postingPath = card.positionId ? `/recruitment/job-postings?clientId=${scopedClientId}&positionId=${card.positionId}` : ''
  const resumePath = card.positionId ? `/recruitment/ats-screening?upload=single&clientId=${scopedClientId}&positionId=${card.positionId}` : ''
  const selectedMove = moveOptions.find(row => row.outcomeCode === moveOutcome)
  const activeJourney = Boolean(card.hiringCaseId && card.status === 'Active')
  const canAutoStart = Boolean(!card.hiringCaseId && card.requisitionId && card.pipelineVersionId && !card.needsPipelineSelection)

  const finishMove = async (outcomeCode: string, reason = '', hiringCaseId = moveCaseId || card.hiringCaseId || 0) => {
    if (!hiringCaseId) return
    setBusyAction('move')
    const response = await advanceRecruitmentHiringCase(hiringCaseId, reason, outcomeCode)
    setBusyAction('')
    if (!response.ok || !response.data) return void message.error(response.error || 'The position could not move to the next stage.')
    setMoveOpen(false); setMoveReason(''); onChanged()
    if (response.data.advanceStatus === 'Pending Approval') message.info(response.data.advanceMessage || 'Stage movement is awaiting approval.')
    else message.success(response.data.advanceMessage || 'Position moved to the next pipeline stage.')
  }
  const prepareMove = async () => {
    if (card.advanceStatus === 'Pending Approval') return
    setBusyAction('move')
    let hiringCaseId = card.hiringCaseId || 0
    if (!hiringCaseId && canAutoStart && card.pipelineVersionId) {
      const started = await startRecruitmentHiringCase(card.workOrderLineId, card.pipelineVersionId)
      if (!started.ok || !started.data) {
        setBusyAction('')
        return void message.error(started.error || 'The request SLA could not start automatically.')
      }
      hiringCaseId = started.data.id
    }
    if (!hiringCaseId) { setBusyAction(''); return }
    setMoveCaseId(hiringCaseId)
    const transitions = await getRecruitmentHiringCaseTransitions(hiringCaseId)
    setBusyAction('')
    const preferred = transitions.find(row => row.outcomeCode === 'ADVANCE') ?? transitions[0]
    if (!preferred) return void finishMove('ADVANCE', '', hiringCaseId)
    if (transitions.length === 1 && !preferred.requiresReason) return void finishMove(preferred.outcomeCode, '', hiringCaseId)
    setMoveOptions(transitions); setMoveOutcome(preferred.outcomeCode); setMoveReason(''); setMoveOpen(true)
  }
  const pause = async () => {
    if (!card.hiringCaseId || pauseReason.trim().length < 3) return
    setBusyAction('pause')
    const response = await pauseRecruitmentHiringCase(card.hiringCaseId, pauseReason.trim())
    setBusyAction('')
    if (!response.ok || !response.data) return void message.error(response.error || 'SLA could not be paused.')
    setPauseOpen(false); setPauseReason(''); onChanged()
  }
  const resume = async () => {
    if (!card.hiringCaseId) return
    setBusyAction('resume')
    const response = await resumeRecruitmentHiringCase(card.hiringCaseId)
    setBusyAction('')
    if (!response.ok || !response.data) return void message.error(response.error || 'SLA could not be resumed.')
    onChanged()
  }

  return <>
    <Space size={compact ? 4 : 6} wrap>
      <Tooltip title="Open the original work order and secured documents"><Button className="action-work-order" size="small" icon={<FileDoneOutlined />} onClick={() => navigate(`/recruitment/work-orders-and-sla?${params}`)}>Work order</Button></Tooltip>
      {card.hiringCaseId && <Tooltip title="View time spent in every completed and active stage"><Button data-testid={`demand-stage-log-${card.workOrderLineId}`} size="small" icon={<HistoryOutlined />} onClick={() => navigate(`/recruitment/work-orders-and-sla?${params}&stageLog=1`)}>Time log</Button></Tooltip>}
      {(card.requisitionId || !card.hiringCaseId) && <Button className="action-request" title={requestActionLabel(card)} size="small" icon={<ProfileOutlined />} onClick={() => navigate(requestPath)}>{requestActionLabel(card)}</Button>}
      {jdPath && <Button className="action-jd" title={jdActionLabel(card)} size="small" onClick={() => navigate(jdPath)}>{jdActionLabel(card)}</Button>}
      {postingPath && <Button className="action-posting" title={postingActionLabel(card)} size="small" onClick={() => navigate(postingPath)}>{postingActionLabel(card)}</Button>}
      {resumePath && <Button className="action-resume" title="Add resume" size="small" icon={<RocketOutlined />} onClick={() => navigate(resumePath)}>Add resume</Button>}
      {activeJourney && (card.isPaused
        ? <Button data-testid={`demand-resume-sla-${card.workOrderLineId}`} className="action-resume-sla" size="small" loading={busyAction === 'resume'} icon={<PlayCircleOutlined />} onClick={() => void resume()}>Resume SLA</Button>
        : card.allowPause && <Button data-testid={`demand-pause-sla-${card.workOrderLineId}`} className="action-pause-sla" size="small" loading={busyAction === 'pause'} icon={<PauseCircleOutlined />} onClick={() => setPauseOpen(true)}>Pause SLA</Button>)}
      {(activeJourney || canAutoStart) && <Button data-testid={`demand-move-next-${card.workOrderLineId}`} className="action-next-stage" type="primary" size="small" loading={busyAction === 'move'} disabled={card.isPaused || card.advanceStatus === 'Pending Approval'} icon={<ArrowRightOutlined />} onClick={() => void prepareMove()}>{card.advanceStatus === 'Pending Approval' ? 'Approval pending' : card.isTerminal ? 'Complete journey' : 'Move next'}</Button>}
    </Space>
    <Modal open={pauseOpen} title="Pause this SLA" okText="Pause SLA" confirmLoading={busyAction === 'pause'} okButtonProps={{ disabled: pauseReason.trim().length < 3 }} onOk={() => void pause()} onCancel={() => { setPauseOpen(false); setPauseReason('') }} destroyOnClose>
      <Form.Item label="Pause reason" required><Input.TextArea data-testid={`demand-pause-reason-${card.workOrderLineId}`} autoFocus rows={3} value={pauseReason} placeholder="For example: awaiting client documents" onChange={event => setPauseReason(event.target.value)} /></Form.Item>
    </Modal>
    <Modal open={moveOpen} title="Move this position" okText={selectedMove?.actionLabel || 'Move stage'} confirmLoading={busyAction === 'move'} okButtonProps={{ disabled: Boolean(selectedMove?.requiresReason && moveReason.trim().length < 3) }} onOk={() => void finishMove(moveOutcome, moveReason.trim())} onCancel={() => { setMoveOpen(false); setMoveReason('') }} destroyOnClose>
      {moveOptions.length > 1 && <Form.Item label="Pipeline action" required><Select value={moveOutcome} options={moveOptions.map(row => ({ value: row.outcomeCode, label: row.actionLabel || row.outcomeCode }))} onChange={setMoveOutcome} /></Form.Item>}
      {selectedMove?.requiresReason && <Form.Item label="Reason" required><Input.TextArea rows={3} value={moveReason} onChange={event => setMoveReason(event.target.value)} /></Form.Item>}
    </Modal>
  </>
}

function Milestone({ label, status }: { label: string; status?: string | null }) {
  const value = status?.trim() || 'Not started'
  return <span title={`${label}: ${value}`}><small>{label}</small><Tag color={statusColor(value)}>{value}</Tag></span>
}

function requestActionLabel(card: RecruitmentPipelineDemandCard) {
  if (!card.requisitionId) return 'Complete intake'
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
function demandSla(card: RecruitmentPipelineDemandCard, now: number, syncedAt: number) {
  if (!card.hiringCaseId) return 'Starts after request save'
  if (['Completed', 'Cancelled'].includes(card.status)) return card.status
  const due = card.overallDueAtUtc || card.dueAtUtc
  if (!due) {
    const running = !card.isPaused && card.status === 'Active'
    const elapsed = Math.max(0, card.activeDurationSeconds || 0) + (running ? Math.max(0, Math.floor((now - syncedAt) / 1000)) : 0)
    return card.isPaused ? `Paused at ${formatDurationClock(elapsed)}` : formatDurationClock(elapsed)
  }
  const referenceNow = card.isPaused ? syncedAt : now
  const remaining = Math.round((new Date(due).getTime() - referenceNow) / 1000)
  if (remaining < 0) return `${formatDurationClock(Math.abs(remaining))} overdue`
  return card.isPaused ? `Paused · ${formatDurationClock(remaining)} left` : `${formatDurationClock(remaining)} left`
}
function candidateSla(card: RecruitmentPipelineBoardCard, now: number, syncedAt: number) {
  const elapsed = Math.max(0, card.elapsedSeconds || 0) + (card.stageStatus !== 'Paused' ? Math.max(0, Math.floor((now - syncedAt) / 1000)) : 0)
  if (card.stageStatus === 'Paused') return `Paused at ${formatDurationClock(elapsed)}`
  if (!card.dueAtUtc) return `In stage ${formatDurationClock(elapsed)}`
  const seconds = Math.round((new Date(card.dueAtUtc).getTime() - now) / 1000)
  if (card.isSlaBreached || seconds < 0) return `${formatDurationClock(Math.abs(seconds))} overdue`
  return `${formatDurationClock(seconds)} remaining`
}
function formatDurationClock(seconds: number) {
  const total = Math.max(0, Math.floor(seconds))
  const days = Math.floor(total / 86400)
  const hours = Math.floor((total % 86400) / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const remainingSeconds = total % 60
  return `${days ? `${days}d ` : ''}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(remainingSeconds).padStart(2, '0')}`
}
function durationText(minutes: number) {
  if (!minutes) return 'Untimed'
  if (minutes >= 1440) return `${Math.ceil(minutes / 1440)}d`
  if (minutes >= 60) return `${Math.ceil(minutes / 60)}h`
  return `${Math.max(1, minutes)}m`
}
function laneTargetLabel(lane: RecruitmentUnifiedPipelineLane) {
  if (lane.slaMode === 'CumulativeFromAnchor') {
    if (lane.targetOffsetMinutes == null) return 'Cumulative target not configured'
    if (lane.targetOffsetMinutes === 0) return 'SLA starts here'
    return `${durationText(lane.targetOffsetMinutes)} cumulative target`
  }
  return `${durationText(lane.slaDurationMinutes)} ${lane.cardScope === 'Application' ? 'candidate' : 'hiring'} stage target`
}
function stageColor(type: string) {
  return ({ Screening: '#6b4eff', ATS: '#2563eb', Interview: '#0e9f6e', Approval: '#d97706', Offer: '#db2777', Position: '#6b4eff' } as Record<string, string>)[type] || '#667085'
}
