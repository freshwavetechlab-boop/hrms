import RecruitmentOfferClientSelect from './RecruitmentOfferClientSelect'
import RecruitmentEligibilityBadge from './RecruitmentEligibilityBadge'
import { recruitmentDuration, recruitmentJobSla } from '../utils/recruitmentSla'
import { PageHeaderPortal } from './layout/AppPageHeader'
import { useRecruitmentView, useRecruitmentPipelineDisplay, useSessionPreference } from '../hooks/useRecruitmentPreferences'
import RecruitmentHiringProgress from './RecruitmentHiringProgress'
import RecruitmentInterviewRounds from './RecruitmentInterviewRounds'
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import {
  ApartmentOutlined, ArrowLeftOutlined, ArrowRightOutlined, BranchesOutlined, ClockCircleOutlined, DeploymentUnitOutlined, HistoryOutlined,
  FileDoneOutlined, FileTextOutlined, PauseCircleOutlined, PlayCircleOutlined, ProfileOutlined, RocketOutlined, SettingOutlined, TeamOutlined,
} from '@ant-design/icons'
import { Alert, Badge, Button, Card, Drawer, Empty, Form, Input, Modal, Segmented, Select, Skeleton, Space, Tabs, Tag, Tooltip, message } from 'antd'
import { useNavigate } from 'react-router-dom'
import { getRecruitmentPipelineWorkspace, getRecruitmentWorkLocations } from '../services/recruitmentOrchestrationService'
import { advanceRecruitmentHiringCase, getRecruitmentHiringCaseTransitions, pauseRecruitmentHiringCase, resumeRecruitmentHiringCase, startRecruitmentHiringCase } from '../services/recruitmentCaseService'
import { getDropdowns } from '../services/settingsService'
import type { Drop, WorkLocation } from '../types/payroll'
import { recruitmentWorkLocationLabel } from '../utils/recruitmentWorkLocation'
import type { RecruitmentPipelineBoardCard, RecruitmentPipelineDemandCard, RecruitmentPipelineWorkspace as PipelineWorkspaceResponse, RecruitmentUnifiedPipelineLane } from '../types/recruitmentOrchestration'
import type { RecruitmentPipelineDisplayMode } from '../types/recruitmentPipelineView'
import { recruitmentPipelineDisplayOptions, recruitmentPipelineDisplayStorageKey } from '../types/recruitmentPipelineView'
import { hiringTransitionLabel, isDivisionRejectionOutcome } from '../utils/recruitmentTransitions'
import { usePipelineScroller } from '../utils/usePipelineScroller'
import { recruitmentJourneyLanes } from '../utils/recruitmentPipelineJourney'
import { pipelineStageOwnership, pipelineOwnershipLegend } from '../utils/recruitmentPipelineOwnership'
import DataTable from './DataTable'
import { useAuthSession } from './AuthGate'
import RecruitmentHiringTemplateManager from './RecruitmentAdminSettings'
import RecruitmentPipelineBoard from './RecruitmentPipelineBoard'
import RecruitmentPipelineDesigner from './RecruitmentPipelineDesigner'
import './RecruitmentPipelineWorkspace.css'

type PipelineView = 'hiring' | 'candidates'
type PipelineManagerTool = 'pipeline' | 'templates'
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
  const canBrowse = hasClientScope || Boolean(session && !(session.originalUser || session.user).clientId)
  const canManagePipeline = Boolean(session?.user.permissions.some(permission =>
    permission === 'settings.manage' || permission === 'recruitment.manage' || permission === 'recruitment.configuration.manage'))
  const canOperateHiringJourney = Boolean(session?.user.permissions.some(permission =>
    permission === 'settings.manage' || permission === 'recruitment.manage' || permission === 'recruitment.hiring-case.manage'))
  const [view, setView] = useState<PipelineView>(initialView)
  const [selectedCandidateCount, setSelectedCandidateCount] = useState<number | null>(null)
  const [workspace, setWorkspace] = useState<PipelineWorkspaceResponse>(emptyWorkspace)
  const workspaceMatchesClient = (workspace.clientId || 0) === initialClientId
  const currentWorkspace = workspaceMatchesClient ? workspace : emptyWorkspace
  const [pipelineVersionId, setPipelineVersionId] = useSessionPreference(`recruitment.pipeline.journey:${session?.user.id ?? 'guest'}:${initialClientId}`, 0)
  const [workLocations, setWorkLocations] = useState<WorkLocation[]>([])
  const [workLocationId, setWorkLocationId] = useSessionPreference(`recruitment.pipeline.location:${session?.user.id ?? 'guest'}:${initialClientId}`, 0)
  const workspaceRequest = useRef(0)
  const [workspaceSyncedAt, setWorkspaceSyncedAt] = useState(Date.now())
  const [clockNow, setClockNow] = useState(Date.now())
  const [loading, setLoading] = useState(canBrowse)
  const [designerOpen, setDesignerOpen] = useState(() => typeof window !== 'undefined' && new URLSearchParams(window.location.search).get('manage') === '1')
  const [managerTool, setManagerTool] = useState<PipelineManagerTool>(() => {
    const requested = typeof window === 'undefined' ? '' : new URLSearchParams(window.location.search).get('tool')
    return requested === 'templates' ? requested : 'pipeline'
  })
  const [managerRevision, setManagerRevision] = useState(0)
  const [designerDropdowns, setDesignerDropdowns] = useState<Drop[]>([])
  const [localDisplayMode, setDisplayMode] = useState<RecruitmentPipelineDisplayMode>(() => {
    if (typeof window === 'undefined') return 'pipeline'
    const saved = window.localStorage.getItem(recruitmentPipelineDisplayStorageKey)
    return saved === 'table' || saved === 'both' || saved === 'flow' ? saved : 'pipeline'
  })

  const sharedView = useRecruitmentView()
  const sharedPipelineDisplay = useRecruitmentPipelineDisplay()
  const displayMode: RecruitmentPipelineDisplayMode = sharedPipelineDisplay ?? (sharedView ? sharedView === 'Table' ? 'table' : 'pipeline' : localDisplayMode)
  useEffect(() => { setView(initialView) }, [initialView])
  useEffect(() => {
    let active = true
    setWorkLocations([])
    if (!initialClientId) { if (workLocationId) setWorkLocationId(0); return }
    void getRecruitmentWorkLocations(initialClientId).then(rows => {
      if (!active) return
      setWorkLocations(rows)
      if (workLocationId && !rows.some(row => row.id === workLocationId)) setWorkLocationId(0)
    })
    return () => { active = false }
  }, [initialClientId, workLocationId])
  const load = useCallback(async (silent = false) => {
    const request = ++workspaceRequest.current
    if (!canBrowse) {
      setWorkspace(emptyWorkspace)
      setLoading(false)
      return
    }
    if (!silent) setLoading(true)
    try {
      let nextWorkspace = await getRecruitmentPipelineWorkspace(initialClientId, positionId, 0, hasClientScope ? workLocationId : 0)
      if (request !== workspaceRequest.current) return
      const automaticStarts = hasClientScope && canOperateHiringJourney
        ? [...nextWorkspace.unassignedDemandCards, ...nextWorkspace.lanes.flatMap(lane => lane.demandCards)]
          .filter(card => card.hasWorkOrder && !card.hiringCaseId && card.requisitionId && card.pipelineVersionId && !card.needsPipelineSelection && !['Completed', 'Cancelled'].includes(card.status))
        : []
      if (automaticStarts.length) {
        const results = await Promise.all(automaticStarts.map(card => startRecruitmentHiringCase(card.workOrderLineId, card.pipelineVersionId!, true)))
        if (results.some(result => result.ok && result.data)) nextWorkspace = await getRecruitmentPipelineWorkspace(initialClientId, positionId, 0, workLocationId)
      }
      if (request !== workspaceRequest.current) return
      setWorkspace(nextWorkspace)
      setWorkspaceSyncedAt(Date.now())
    }
    finally { if (!silent && request === workspaceRequest.current) setLoading(false) }
  }, [canBrowse, hasClientScope, canOperateHiringJourney, initialClientId, positionId, workLocationId])
  useEffect(() => { void load(); return () => { ++workspaceRequest.current } }, [load])
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

  const pipelineChoices = useMemo(() => Array.from(new Set(currentWorkspace.lanes.map(lane => lane.pipelineVersionId))).map(id => {
    const lanes = currentWorkspace.lanes.filter(lane => lane.pipelineVersionId === id)
    return { value: id, label: `${hasClientScope ? '' : lanes[0].clientName + ' · '}${lanes[0].pipelineName} · ${lanes.filter(lane => lane.cardScope === 'Position').length} hiring / ${lanes.filter(lane => lane.cardScope === 'Application').length} candidate` }
  }), [currentWorkspace.lanes, hasClientScope])
  useEffect(() => { if (workspaceMatchesClient && !loading && pipelineVersionId && !pipelineChoices.some(row => row.value === pipelineVersionId)) setPipelineVersionId(0) }, [loading, workspaceMatchesClient, pipelineChoices, pipelineVersionId, setPipelineVersionId])
  const journeyLanes = useMemo(() => recruitmentJourneyLanes(currentWorkspace.lanes.filter(lane => view !== 'hiring' || !pipelineVersionId || lane.pipelineVersionId === pipelineVersionId), 'hiring'), [currentWorkspace.lanes, pipelineVersionId, view])
  const unassigned = view === 'hiring' && pipelineVersionId ? [] : currentWorkspace.unassignedDemandCards
  const managerLanes = view === 'candidates' ? journeyLanes.filter(lane => lane.cardScope === 'Application') : journeyLanes
  const selectedDefinitionId = managerLanes.length && new Set(managerLanes.map(lane => lane.pipelineDefinitionId)).size === 1 ? managerLanes[0].pipelineDefinitionId : undefined
  const selectedVersionId = managerLanes.length && new Set(managerLanes.map(lane => lane.pipelineVersionId)).size === 1 ? managerLanes[0].pipelineVersionId : undefined
  const demandCount = unassigned.length + journeyLanes.reduce((total, lane) => total + lane.demandCards.length, 0)
  const candidateCount = currentWorkspace.lanes.reduce((total, lane) => total + lane.applications.length, 0)

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
    {sharedView && <PageHeaderPortal slot="recruitment-page-controls">{canManagePipeline && <Button data-testid="manage-hiring-pipeline" icon={<SettingOutlined />} disabled={!canBrowse} onClick={() => setPipelineManagerOpen(true)}>Manage pipeline</Button>}</PageHeaderPortal>}
    <div className="pipeline-workspace-toolbar">
      <Segmented
        aria-label="Pipeline workspace view"
        value={view}
        onChange={value => setWorkspaceView(value as PipelineView)}
        options={[
          { value: 'hiring', disabled: !canBrowse, label: <span><BranchesOutlined /> Full hiring journey <Badge count={demandCount} showZero /></span> },
          { value: 'candidates', disabled: !canBrowse, label: <span title={view === 'candidates' && displayMode !== 'flow' ? 'Candidates in the selected position / posting pipeline' : 'Candidates across permitted pipelines'}><TeamOutlined /> Candidates <Badge data-testid="pipeline-candidate-count" count={view === 'candidates' && displayMode !== 'flow' ? selectedCandidateCount ?? '…' : candidateCount} showZero color="#2563eb" /></span> },
        ]}
      />
      {view === 'hiring' && <Select aria-label="Configured hiring journey" className="pipeline-journey-select" disabled={!canBrowse || loading || !workspaceMatchesClient} showSearch optionFilterProp="label" value={pipelineVersionId} options={[{ value: 0, label: 'All configured journeys' }, ...pipelineChoices]} onChange={setPipelineVersionId} />}
      <Select aria-label="Hiring pipeline work location" style={{ minWidth: 220, maxWidth: 340 }} disabled={!hasClientScope} showSearch optionFilterProp="label" value={workLocationId} options={[{ value: 0, label: 'All permitted work locations' }, ...workLocations.map(row => ({ value: row.id, label: recruitmentWorkLocationLabel(row) }))]} onChange={setWorkLocationId} />
      {!sharedView && <div className="pipeline-command-controls">{canManagePipeline && <Button data-testid="manage-hiring-pipeline" icon={<SettingOutlined />} disabled={!canBrowse} onClick={() => setPipelineManagerOpen(true)}>Manage pipeline</Button>}{canChooseClient && <RecruitmentOfferClientSelect includeSetup aria-label="Pipeline client scope" allowClear value={initialClientId || undefined} options={clientOptions} placeholder="All accessible clients" onChange={value => onClientChange?.(value)} />}<Select aria-label="Pipeline display view" value={displayMode} onChange={setDisplayMode} options={recruitmentPipelineDisplayOptions} /></div>}
    </div>

    {canBrowse && !loading && journeyLanes.length > 0 && <div className="pipeline-owner-legend" aria-label="Stage responsibility colors">{pipelineOwnershipLegend.map(owner => <span key={owner.ownerType} title={owner.description}><i style={{ background: owner.color }} />{owner.label}</span>)}<small>Responsibility colors · access follows assigned roles and users</small></div>}
    {canBrowse && !loading && displayMode !== 'flow' && journeyLanes.length > 0 && <section className="pipeline-complete-journey" aria-label="Complete configured hiring journey">
      <div><strong>{journeyLanes.length} visible stages</strong><span>{journeyLanes.filter(lane => lane.cardScope === 'Position').length} hiring demand + {journeyLanes.filter(lane => lane.cardScope === 'Application').length} candidate processing. Each stage belongs to its named pipeline.</span></div>
      <div className="pipeline-complete-journey-stages">{journeyLanes.map(lane => { const owner = pipelineStageOwnership(lane); return <button type="button" key={`${lane.pipelineVersionId}:${lane.stageId}`} title={`${lane.clientName} · ${lane.pipelineName} · ${owner.label}`} onClick={() => setWorkspaceView(lane.cardScope === 'Application' ? 'candidates' : 'hiring')} style={{ borderColor: owner.color }}><b>{lane.displayOrder}</b><span>{lane.stageName}<small style={{ color: owner.color }}>{owner.label} · {lane.cardScope === 'Application' ? 'Candidate' : 'Hiring demand'}</small></span></button> })}</div>
    </section>}
    {!canBrowse && <Card className="pipeline-empty-card" data-testid="pipeline-client-required"><Empty description="No permitted hiring workspace is available." /></Card>}
    {canBrowse && displayMode === 'flow' && (loading
      ? <Card><Skeleton active paragraph={{ rows: 5 }} /></Card>
      : <PipelineFlowDiagram view={view} lanes={journeyLanes} unassigned={unassigned} />)}
    {canBrowse && displayMode !== 'flow' && view === 'hiring' && (loading
      ? <Card><Skeleton active paragraph={{ rows: 6 }} /></Card>
      : <DemandBoard
          clientId={initialClientId}
          lanes={journeyLanes}
          unassigned={unassigned}
          displayMode={displayMode}
          clockNow={clockNow}
          workspaceSyncedAt={workspaceSyncedAt}
          onOpenOrders={() => navigate(`/recruitment/work-orders-and-sla?clientId=${initialClientId}`)}
          onOpenCandidates={() => setWorkspaceView('candidates')}
          onChanged={() => void load(true)}
        />)}
    {canBrowse && displayMode !== 'flow' && view === 'candidates' && <RecruitmentPipelineBoard embedded key={`candidate-pipeline-${initialClientId}-${positionId}-${workLocationId}`} initialClientId={initialClientId} clientScopeManaged={clientScopeManaged} positionId={positionId} workLocationId={hasClientScope ? workLocationId : 0} displayMode={displayMode} onDisplayModeChange={setDisplayMode} clientPipelineCandidateCount={candidateCount} onCandidateCountChange={setSelectedCandidateCount} clientPipelineLanes={journeyLanes.filter(lane => lane.cardScope === 'Application')} />}
    <Drawer className="recruitment-pipeline-manager-drawer" title="Manage pipeline" open={designerOpen && canManagePipeline && canBrowse} width="100%" onClose={() => setPipelineManagerOpen(false)} destroyOnClose>
      <Tabs className="recruitment-automation-tabs" activeKey={managerTool} onChange={key => setPipelineManagerTool(key as PipelineManagerTool)} items={[
        { key: 'pipeline', label: <span><BranchesOutlined aria-hidden /> Pipeline design</span>, children: <RecruitmentPipelineDesigner key={`pipeline-manager-${initialClientId}-${managerRevision}`} initialClientId={initialClientId} initialPipelineDefinitionId={selectedDefinitionId} initialPipelineVersionId={selectedVersionId} dropdowns={designerDropdowns} onDropdownsChange={setDesignerDropdowns} onSaved={() => void load(true)} /> },
        { key: 'templates', label: <span><FileTextOutlined aria-hidden /> Hiring templates</span>, children: <RecruitmentHiringTemplateManager initialClientId={initialClientId} onSaved={() => setManagerRevision(value => value + 1)} /> },
      ]} />
    </Drawer>
  </section>
}

function PipelineFlowDiagram({ view, lanes, unassigned }: { view: PipelineView; lanes: RecruitmentUnifiedPipelineLane[]; unassigned: RecruitmentPipelineDemandCard[] }) {
  const scroller = usePipelineScroller<HTMLDivElement>({ rootScrollsVertically: true })
  const scopedLanes = recruitmentJourneyLanes(lanes, view)
  const groups = Array.from(scopedLanes.reduce((map, lane) => {
    const group = map.get(lane.pipelineVersionId) ?? []
    group.push(lane)
    map.set(lane.pipelineVersionId, group)
    return map
  }, new Map<number, RecruitmentUnifiedPipelineLane[]>()))

  if (!groups.length && !unassigned.length) return <Card className="pipeline-empty-card"><Empty description="No configured stages are available for this client scope." /></Card>

  return <section className="pipeline-flow-view" data-testid="pipeline-flow-view">
    <header className="pipeline-flow-heading">
      <div><span className="orchestration-kicker">Live process map</span><h3>{view === 'candidates' ? 'Candidate journey' : 'Complete hiring journey'}</h3><p>Configured stages, scope hand-offs, live demand and candidate volume in one operational flow.</p></div>
      <Space wrap><Tag color="purple">{scopedLanes.filter(lane => lane.cardScope === 'Position').length} hiring stages</Tag><Tag color="blue">{scopedLanes.filter(lane => lane.cardScope === 'Application').length} candidate stages</Tag></Space>
    </header>
    <div ref={scroller.ref} className="pipeline-flow-scroll" tabIndex={0} onKeyDown={scroller.onKeyDown} aria-label="Scrollable pipeline flow diagram">
      {!!unassigned.length && view !== 'candidates' && <div className="pipeline-flow-group is-unassigned">
        <div className="pipeline-flow-group-label"><span>Awaiting pipeline</span><small>Work-order intake</small></div>
        <FlowNode title="Unassigned intake" type="Position" scope="Position" count={unassigned.length} demandCount={unassigned.length} candidateCount={0} targetLabel="SLA not started" order={0} />
      </div>}
      {groups.map(([versionId, versionLanes]) => <div className="pipeline-flow-group" key={versionId} data-testid={`pipeline-flow-version-${versionId}`}>
        <div className="pipeline-flow-group-label"><span>{versionLanes[0]?.pipelineName || 'Hiring pipeline'}</span><small>{versionLanes[0]?.clientName ? `${versionLanes[0].clientName} · ` : ''}{versionLanes.length} stage{versionLanes.length === 1 ? '' : 's'}</small></div>
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
                owner={pipelineStageOwnership(lane)}
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

function FlowNode({ title, type, scope, count, demandCount, candidateCount, targetLabel, order, stageId, owner = pipelineStageOwnership() }: { title: string; type: string; scope: 'Position' | 'Application'; count: number; demandCount: number; candidateCount: number; targetLabel: string; order: number; stageId?: number; owner?: ReturnType<typeof pipelineStageOwnership> }) {
  const color = owner.color
  return <article className={`pipeline-flow-node ${type === 'Approval' ? 'is-decision' : ''}`} data-testid={stageId ? `pipeline-flow-stage-${stageId}` : 'pipeline-flow-unassigned'} style={{ '--flow-color': color } as CSSProperties}>
    <header><span>{order ? String(order).padStart(2, '0') : 'IN'}</span><Badge count={count} showZero color={color} /></header>
    <h4 title={title}>{title}</h4>
    <Tag color={color}>{owner.label}</Tag>
    <div className="pipeline-flow-node-meta"><Tag>{scope === 'Position' ? 'Hiring demand' : 'Candidate'}</Tag><span><ClockCircleOutlined /> {targetLabel}</span></div>
    <footer><span>{demandCount} demand</span><span>{candidateCount} candidate{candidateCount === 1 ? '' : 's'}</span></footer>
  </article>
}

function DemandBoard({ clientId, lanes, unassigned, displayMode, clockNow, workspaceSyncedAt, onOpenOrders, onOpenCandidates, onChanged }: { clientId: number; lanes: RecruitmentUnifiedPipelineLane[]; unassigned: RecruitmentPipelineDemandCard[]; displayMode: RecruitmentPipelineDisplayMode; clockNow: number; workspaceSyncedAt: number; onOpenOrders: () => void; onOpenCandidates: () => void; onChanged: () => void }) {
  const scroller = usePipelineScroller<HTMLDivElement>()
  if (!lanes.length && !unassigned.length) return <Card className="pipeline-empty-card"><Empty description="No hiring pipeline activity in this client scope."><Space wrap><Button type="primary" onClick={onOpenOrders}>Open work orders</Button><Button href={`/recruitment/requisitions?new=1&clientId=${clientId}`}>New hiring request</Button></Space></Empty></Card>


  const tableRows = [
    ...unassigned.map(card => ({ id: `unassigned-${card.workOrderLineId}`, card, candidate: null as RecruitmentPipelineBoardCard | null, stageName: 'Work-order intake', stageType: 'Position', owner: pipelineStageOwnership() })),
    ...lanes.flatMap(lane => [
      ...lane.demandCards.map(card => ({ id: `${lane.stageId}-demand-${card.workOrderLineId}`, card, candidate: null as RecruitmentPipelineBoardCard | null, stageName: lane.stageName, stageType: lane.stageType, owner: pipelineStageOwnership(lane) })),
      ...lane.applications.map(candidate => ({ id: `${lane.stageId}-candidate-${candidate.applicationId}`, card: null as RecruitmentPipelineDemandCard | null, candidate, stageName: lane.stageName, stageType: lane.stageType, owner: pipelineStageOwnership(lane) })),
    ]),
  ]
  return <div className={`pipeline-results${displayMode === 'both' ? ' is-both' : ''}`}>
    {displayMode !== 'table' && <div ref={scroller.ref} className="demand-pipeline-board" data-testid="pipeline-hiring-flow" tabIndex={0} onKeyDown={scroller.onKeyDown} aria-label="Scrollable hiring pipeline">
      <div className="demand-pipeline-columns">
        {!!unassigned.length && <DemandLane
          key="unassigned"
          clientId={clientId}
          title="Work-order intake"
          color="#667085"
          cards={unassigned}
          applications={[]}
          helper="Select the approved pipeline so its SLA and next actions can start."
          clockNow={clockNow}
          workspaceSyncedAt={workspaceSyncedAt}
          onOpenCandidates={onOpenCandidates}
          onChanged={onChanged}
        />}
        {lanes.map(lane => <DemandLane key={lane.stageId} clientId={clientId} title={lane.stageName} color={pipelineStageOwnership(lane).color} ownerLabel={pipelineStageOwnership(lane).label} cards={lane.demandCards} applications={lane.applications} helper={laneTargetLabel(lane)} context={[lane.clientName,lane.pipelineName].filter(Boolean).join(' · ')} clockNow={clockNow} workspaceSyncedAt={workspaceSyncedAt} onOpenCandidates={onOpenCandidates} onChanged={onChanged} />)}
      </div>
    </div>}
    {displayMode !== 'pipeline' && <Card size="small" className="pipeline-demand-table" data-testid="pipeline-demand-table"><DataTable fillHeight
      rows={tableRows}
      getRowId={row => row.id}
      exportFileName="hiring-demand-pipeline"
      emptyText="No hiring demand matches this client scope."
      columns={[
        { key: 'position', label: 'Hiring demand / Candidate', width: '220px', value: row => row.card?.positionName || row.candidate?.candidateName || '', render: row => <div className="pipeline-table-candidate"><strong>{row.card?.positionName || row.candidate?.candidateName}</strong><small>{row.card ? row.card.division || row.card.payBandLevelCode || 'Role details pending' : row.candidate?.applicationCode}</small></div> },
        { key: 'recordType', label: 'Record type', width: '120px', value: row => row.card ? 'Hiring demand' : 'Candidate' },
        { key: 'workLocation', label: 'Work location', width: '200px', value: row => row.card?.workLocationName || row.candidate?.workLocationName || 'Not linked' },
        { key: 'workOrder', label: 'Work order / Position', width: '190px', value: row => row.card?.workOrderNumber || row.candidate?.positionTitle || '' },
        { key: 'stage', label: 'Current stage', width: '190px', value: row => row.stageName, render: row => <Tag color={row.owner.color}>{row.stageName}</Tag> },
        { key: 'owner', label: 'Responsible team', width: '160px', value: row => row.owner.label },
        { key: 'request', label: 'Request', width: '130px', value: row => row.card ? row.card.requisitionStatus || 'Not started' : '', render: row => row.card ? <Tag color={statusColor(row.card.requisitionStatus || '')}>{row.card.requisitionStatus || 'Not started'}</Tag> : '—' },
        { key: 'jd', label: 'JD', width: '130px', value: row => row.card ? row.card.jobDescriptionStatus || 'Not started' : '', render: row => row.card ? <Tag color={statusColor(row.card.jobDescriptionStatus || '')}>{row.card.jobDescriptionStatus || 'Not started'}</Tag> : '—' },
        { key: 'posting', label: 'Posting', width: '130px', value: row => row.card ? row.card.jobPostingStatus || 'Not started' : '', render: row => row.card ? <Tag color={statusColor(row.card.jobPostingStatus || '')}>{row.card.jobPostingStatus || 'Not started'}</Tag> : '—' },
        { key: 'candidates', label: 'Candidates', width: '110px', value: row => row.card ? row.card.candidateCount || 0 : 1 },
        { key: 'sla', label: 'Live SLA', width: '260px', value: row => row.card ? demandSla(row.card, clockNow, workspaceSyncedAt) : row.candidate ? candidateSla(row.candidate, clockNow, workspaceSyncedAt) : '' },
        { key: 'actions', label: 'Actions', width: '420px', sortable: false, filterable: false, render: row => row.card ? <DemandActions card={row.card} clientId={row.card.clientId || clientId} compact onChanged={onChanged} /> : <Button onClick={onOpenCandidates}>Manage candidate</Button> },
      ]}
    /></Card>}
  </div>
}

function DemandLane({ clientId, title, color, cards, applications, helper, context, ownerLabel = 'Owner not set', clockNow, workspaceSyncedAt, onOpenCandidates, onChanged }: { clientId: number; title: string; color: string; cards: RecruitmentPipelineDemandCard[]; applications: RecruitmentPipelineBoardCard[]; helper: string; context?: string; ownerLabel?: string; clockNow: number; workspaceSyncedAt: number; onOpenCandidates: () => void; onChanged: () => void }) {
  const totalCards = cards.length + applications.length
  return <section className="demand-pipeline-lane" style={{ '--lane-color': color } as CSSProperties}>
    <header><div>{context && <span className="demand-lane-context" title={context}>{context}</span>}<h3>{title}</h3><Tag color={color}>{ownerLabel}</Tag><small>{helper}</small></div><Badge count={totalCards} showZero color={color} /></header>
    <div className="demand-pipeline-lane-body">
      {!totalCards && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No records in this stage" />}
      {cards.map(card => <DemandCard key={`${card.workOrderLineId}-${card.hiringCaseId ?? 0}`} card={card} clientId={card.clientId || clientId} clockNow={clockNow} workspaceSyncedAt={workspaceSyncedAt} onChanged={onChanged} />)}
      {applications.map(card => <JourneyCandidateCard key={card.applicationId} card={card} clockNow={clockNow} workspaceSyncedAt={workspaceSyncedAt} onOpen={onOpenCandidates} />)}
    </div>
  </section>
}

function JourneyCandidateCard({ card, clockNow, workspaceSyncedAt, onOpen }: { card: RecruitmentPipelineBoardCard; clockNow: number; workspaceSyncedAt: number; onOpen: () => void }) {
  const sla = candidateSla(card, clockNow, workspaceSyncedAt)
  return <Card size="small" className={`journey-candidate-card ${card.isSlaBreached ? 'is-breached' : ''}`} data-testid={`journey-candidate-${card.applicationId}`}>
    <RecruitmentInterviewRounds applicationId={card.applicationId} editable={false} />
    <div className="journey-candidate-heading"><div><strong title={card.candidateName}>{card.candidateName}</strong><span>{card.applicationCode}</span></div><Space size={4} wrap><RecruitmentEligibilityBadge row={card} showScore />{card.interviewScore != null && <Tag color={card.interviewScore >= 60 ? 'green' : 'orange'}>Interview {card.interviewScore.toFixed(1)}</Tag>}</Space></div>
    <div data-testid="candidate-job" style={{ whiteSpace: 'normal', overflowWrap: 'anywhere', marginBlock: 8 }}><strong>{card.positionTitle || 'Job details unavailable'}</strong>{card.positionCode && <div>{card.positionCode}</div>}</div>
    <div className="journey-candidate-meta"><span><TeamOutlined /> Candidate</span>{card.candidateEmail && <span title={card.candidateEmail}>{card.candidateEmail}</span>}{card.rejectedFromStageName && <span><BranchesOutlined /> Rejected after {card.rejectedFromStageName}</span>}<span><ClockCircleOutlined /> {sla}</span></div>
    <div className="journey-candidate-footer"><Tag color={statusColor(card.stageStatus)}>{card.stageStatus}</Tag><Button size="small" type="link" onClick={onOpen}>Manage candidate</Button></div>
  </Card>
}

function DemandCard({ card, clientId, clockNow, workspaceSyncedAt, onChanged }: { card: RecruitmentPipelineDemandCard; clientId: number; clockNow: number; workspaceSyncedAt: number; onChanged: () => void }) {
  const sla = demandSla(card, clockNow, workspaceSyncedAt)
  const jobSla = recruitmentJobSla(card, clockNow, workspaceSyncedAt)
  return <Card size="small" className={`demand-card ${jobSla.breached ? 'is-breached' : ''}`} data-testid={`pipeline-demand-${card.workOrderLineId}`}>
    <div className="demand-card-heading">
      <div><strong title={card.positionName}>{card.positionName}</strong><span>{card.workOrderNumber}{card.payBandLevelCode ? ` · ${card.payBandLevelCode}` : ''}</span></div>
      <div className="demand-card-statuses">{card.hiringCaseId && <Tag color={!card.overallDueAtUtc ? 'default' : jobSla.breached ? 'error' : 'success'}>{jobSla.label}</Tag>}</div>
    </div>
    <div className="demand-card-context">
      {card.workLocationName && <Tag>{card.workLocationName}</Tag>}
      {card.division && <span><ApartmentOutlined /> {card.division}</span>}
      <Tag color="cyan"><TeamOutlined /> {card.candidateCount || 0} candidate{card.candidateCount === 1 ? '' : 's'}</Tag>
      <Tag>Required: {card.requiredCandidateCount ?? '—'}</Tag><Tag color="blue">In panel: {card.inPanelCount ?? 0}</Tag><Tag color="green">Selected: {card.selectedCandidateCount ?? 0}</Tag>
      <span className="demand-live-sla" data-testid={`demand-sla-timer-${card.workOrderLineId}`}><ClockCircleOutlined /> {sla}</span>
    </div>
    <RecruitmentHiringProgress progress={card.hiringProgress} />
    {card.interviewRoundsApplicationId && /interview|panel/i.test(card.currentStageName || '') && <RecruitmentInterviewRounds applicationId={card.interviewRoundsApplicationId} editable={false} jobOnly />}
    <details className="demand-supporting-details"><summary>Documents & status</summary><div className="demand-milestones" aria-label="Hiring milestones">
      <Milestone label="Work order" status={card.workOrderStatus} />
      <Milestone label="Request" status={card.requisitionStatus} />
      <Milestone label="Vacancy" status={card.positionStatus} />
      <Milestone label="JD" status={card.jobDescriptionStatus} />
      <Milestone label="Posting" status={card.jobPostingStatus} />
    </div></details>
    <div className="demand-card-actions"><DemandActions card={card} clientId={clientId} onChanged={onChanged} /></div>
    {card.needsPipelineSelection && <div className="demand-card-callout">Choose a saved hiring or combined pipeline in the work order to start stage tracking.</div>}
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
  const jdPath = card.requisitionId ? `/recruitment/requisitions?clientId=${scopedClientId}&requisitionId=${card.requisitionId}` : ''
  const postingPath = card.positionId ? `/recruitment/job-postings?clientId=${scopedClientId}&positionId=${card.positionId}` : ''
  const resumePath = card.positionId ? `/recruitment/applications?upload=single&clientId=${scopedClientId}&positionId=${card.positionId}` : ''
  const selectedMove = moveOptions.find(row => row.outcomeCode === moveOutcome)
  const activeJourney = Boolean(card.hiringCaseId && card.status === 'Active')
  const canAutoStart = Boolean(card.hasWorkOrder && !card.hiringCaseId && card.requisitionId && card.pipelineVersionId && !card.needsPipelineSelection)

  const finishMove = async (outcomeCode: string, reason = '', hiringCaseId = moveCaseId || card.hiringCaseId || 0) => {
    if (!hiringCaseId) return
    setBusyAction('move')
    const response = await advanceRecruitmentHiringCase(hiringCaseId, reason, outcomeCode)
    setBusyAction('')
    if (!response.ok || !response.data) return void message.error(response.error || 'The position could not be moved.')
    setMoveOpen(false); setMoveReason(''); onChanged()
    if (response.data.advanceStatus === 'Pending Approval') message.info(response.data.advanceMessage || 'Stage movement is awaiting approval.')
    else message.success(response.data.advanceMessage || 'Position moved in the pipeline.')
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

  const secondaryActions = <Space size={4} wrap>
      {card.hasWorkOrder && <Tooltip title="Open the original work order and secured documents"><Button className="action-work-order" size="small" icon={<FileDoneOutlined />} onClick={() => navigate(`/recruitment/work-orders-and-sla?${params}`)}>Work order</Button></Tooltip>}
      {card.hiringCaseId && <Tooltip title="View time spent in every completed and active stage"><Button data-testid={`demand-stage-log-${card.workOrderLineId}`} size="small" icon={<HistoryOutlined />} onClick={() => navigate(`/recruitment/work-orders-and-sla?${params}&stageLog=1`)}>Time log</Button></Tooltip>}
      {jdPath && <Button className="action-jd" title={jdActionLabel(card)} size="small" onClick={() => navigate(jdPath)}>{jdActionLabel(card)}</Button>}
      {postingPath && <Button className="action-posting" title={postingActionLabel(card)} size="small" onClick={() => navigate(postingPath)}>{postingActionLabel(card)}</Button>}
      {resumePath && <Button className="action-resume" title="Add resume" size="small" icon={<RocketOutlined />} onClick={() => navigate(resumePath)}>Add resume</Button>}
      {activeJourney && (card.isPaused
        ? <Button data-testid={`demand-resume-sla-${card.workOrderLineId}`} className="action-resume-sla" size="small" loading={busyAction === 'resume'} icon={<PlayCircleOutlined />} onClick={() => void resume()}>Resume SLA</Button>
        : card.allowPause && <Button data-testid={`demand-pause-sla-${card.workOrderLineId}`} className="action-pause-sla" size="small" loading={busyAction === 'pause'} icon={<PauseCircleOutlined />} onClick={() => setPauseOpen(true)}>Pause SLA</Button>)}
  </Space>
  return <>
    <Space size={compact ? 4 : 6} wrap>
      {(card.requisitionId || !card.hiringCaseId) && <Button className="action-request" title={requestActionLabel(card)} size="small" icon={<ProfileOutlined />} onClick={() => navigate(requestPath)}>{requestActionLabel(card)}</Button>}
      {(activeJourney || canAutoStart) && <Button data-testid={`demand-move-next-${card.workOrderLineId}`} className="action-next-stage" type="primary" size="small" loading={busyAction === 'move'} disabled={card.isPaused || card.advanceStatus === 'Pending Approval'} onClick={() => void prepareMove()}>{card.advanceStatus === 'Pending Approval' ? 'Approval pending' : card.isTerminal ? 'Complete journey' : <><ArrowLeftOutlined /> Move <ArrowRightOutlined /></>}</Button>}
      {compact && secondaryActions}
    </Space>
    {!compact && <details className="demand-supporting-details"><summary>More actions</summary>{secondaryActions}</details>}
    <Modal open={pauseOpen} title="Pause this SLA" okText="Pause SLA" confirmLoading={busyAction === 'pause'} okButtonProps={{ disabled: pauseReason.trim().length < 3 }} onOk={() => void pause()} onCancel={() => { setPauseOpen(false); setPauseReason('') }} destroyOnClose>
      <Form.Item label="Pause reason" required><Input.TextArea data-testid={`demand-pause-reason-${card.workOrderLineId}`} autoFocus rows={3} value={pauseReason} placeholder="For example: awaiting client documents" onChange={event => setPauseReason(event.target.value)} /></Form.Item>
    </Modal>
    <Modal open={moveOpen} title={isDivisionRejectionOutcome(moveOutcome) ? 'Reject this hiring demand?' : 'Move this position'} okText={isDivisionRejectionOutcome(moveOutcome) ? 'Reject demand' : 'Move'} confirmLoading={busyAction === 'move'} okButtonProps={{ danger: isDivisionRejectionOutcome(moveOutcome), disabled: Boolean(selectedMove?.requiresReason && moveReason.trim().length < 3) }} onOk={() => void finishMove(moveOutcome, moveReason.trim())} onCancel={() => { setMoveOpen(false); setMoveReason('') }} destroyOnClose>
      {isDivisionRejectionOutcome(moveOutcome) && <Alert showIcon type="warning" message="Rejected by Division" description="This rejection is recorded separately from the deadline-based Business SLA breach status." />}
      {moveOptions.length > 1 && <Form.Item label="Pipeline action" required><Select value={moveOutcome} options={moveOptions.map(row => ({ value: row.outcomeCode, label: hiringTransitionLabel(row) }))} onChange={setMoveOutcome} /></Form.Item>}
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
  if (!card.hiringCaseId) return card.hasWorkOrder ? 'Starts after request save' : 'Awaiting work-order link'
  return `Overall SLA: ${recruitmentJobSla(card, now, syncedAt).text}`
}
function candidateSla(card: RecruitmentPipelineBoardCard, now: number, syncedAt: number) {
  const elapsed = Math.max(0, card.elapsedSeconds || 0) + (card.stageStatus !== 'Paused' ? Math.max(0, Math.floor((now - syncedAt) / 1000)) : 0)
  if (card.stageStatus === 'Paused') return `Paused at ${formatDurationClock(elapsed)}`
  if (!card.dueAtUtc) return `In stage ${formatDurationClock(elapsed)}`
  const seconds = Math.round((new Date(card.dueAtUtc).getTime() - now) / 1000)
  if (card.isSlaBreached || seconds < 0) return `${formatDurationClock(Math.abs(seconds))} overdue`
  return `${formatDurationClock(seconds)} remaining`
}
const formatDurationClock = recruitmentDuration

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
