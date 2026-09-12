import { useCallback, useEffect, useMemo, useState } from 'react'
import { BranchesOutlined, DeleteOutlined, FormOutlined, PlusOutlined, RocketOutlined } from '@ant-design/icons'
import { Button, Card, Drawer, Input, InputNumber, Modal, Popconfirm, Select, Space, Tabs, Tag, message } from 'antd'
import { useLocation, useNavigate } from 'react-router-dom'
import DataTable from '../components/DataTable'
import RecruitmentAtsWorkspace from '../components/RecruitmentAtsWorkspace'
import RecruitmentDashboardOverview from '../components/RecruitmentDashboardOverview'
import RecruitmentFormBuilder from '../components/RecruitmentFormBuilder'
import RecruitmentJobPostingManager from '../components/RecruitmentJobPostingManager'
import RecruitmentPipelineWorkspace from '../components/RecruitmentPipelineWorkspace'
import RecruitmentRequisitionManager from '../components/RecruitmentRequisitionManager'
import RecruitmentTalentWorkspace from '../components/RecruitmentTalentWorkspace'
import RecruitmentWorkOrderWorkspace from '../components/RecruitmentWorkOrderWorkspace'
import { useAuthSession } from '../components/AuthGate'
import { getClients } from '../services/payrollService'
import { assignRecruiter, createReferralCampaign, deleteRecruitmentOpenPosition, getRecruitmentDashboard, getRecruitmentMasterOptions, getRecruitmentOpenPositionDetail, getRecruitmentOpenPositions, getRecruitmentOperationsOptions, getRecruitmentRequisitionApprovalMode, publishPosition, saveRecruitmentPositionNote, updateRecruitmentPositionStatus } from '../services/recruitmentService'
import type { Client, RecruitmentDashboard, RecruitmentOpenPosition, RecruitmentOperationsOptions, RecruitmentPositionDetail } from '../types/payroll'
import { recruitmentStageColor } from '../utils/recruitmentStage'

const fallbackPositionStatuses = ['Open', 'Recruiter Assigned', 'Published', 'Candidate Screening', 'Interview In Progress', 'Offer Released', 'Offer Accepted', 'Joining Pending', 'Filled', 'Partially Filled', 'Cancelled', 'Closed', 'On Hold']
const money = (value: number, currency = 'INR') => `${currency} ${Number(value || 0).toLocaleString('en-IN')}`
const dateText = (value?: string | null) => value ? new Date(value).toLocaleDateString('en-GB') : '-'
const dashboard0: RecruitmentDashboard = { drafts: 0, pendingApproval: 0, approved: 0, rejected: 0, returned: 0, withdrawn: 0, openPositions: 0, filledPositions: 0, cancelledPositions: 0, onHoldPositions: 0, remainingPositions: 0, averageApprovalHours: 0, departmentWiseHiring: [], companyWiseHiring: [], priorityWiseHiring: [], upcomingJoiningTargets: [] }
const recruitmentClientScopeKey = 'recruitment.clientScope'
const draftRequisitionStatuses = ['Draft', 'Sent Back']
const pendingRequisitionStatuses = ['Pending Approval']
export const recruitmentViews = ['Dashboard', 'Work Orders & SLA', 'Requisitions', 'Open Positions', 'Job Descriptions', 'Job Postings', 'ATS Screening', 'Hiring Pipeline', 'Talent Pool', 'Applications', 'Interviews', 'Offers & Pre-Onboarding'] as const
export type RecruitmentPageView = (typeof recruitmentViews)[number]

type RecruitmentWorkspace = 'overview' | 'orders' | 'requests' | 'jobs' | 'candidates' | 'pipeline' | 'selection'

const recruitmentWorkspace = (view: RecruitmentPageView): RecruitmentWorkspace => {
  if (view === 'Dashboard') return 'overview'
  if (view === 'Work Orders & SLA') return 'orders'
  if (['Requisitions', 'Open Positions'].includes(view)) return 'requests'
  if (['Job Descriptions', 'Job Postings'].includes(view)) return 'jobs'
  if (['Talent Pool', 'Applications', 'ATS Screening'].includes(view)) return 'candidates'
  if (view === 'Hiring Pipeline') return 'pipeline'
  if (['Interviews', 'Offers & Pre-Onboarding'].includes(view)) return 'selection'
  return 'selection'
}

const workspaceCopy: Record<RecruitmentWorkspace, { group: string; title: string; description: string }> = {
  overview: { group: 'Talent acquisition', title: 'Overview', description: 'Hiring demand, approvals, open roles and joining targets in one concise view.' },
  orders: { group: 'Client hiring intake', title: 'Work Orders', description: 'Register the client order once, then create role-wise Hiring Requests. SLA is applied automatically from the published pipeline.' },
  requests: { group: 'Plan hiring', title: 'Hiring Requests', description: 'Raise a request and follow the same demand through approval into an approved vacancy.' },
  jobs: { group: 'Attract talent', title: 'Jobs', description: 'Prepare the governed role profile, then publish its approved job and public application link.' },
  candidates: { group: 'Find talent', title: 'Candidates', description: 'Manage reusable talent profiles, job applications and ATS screening from one workspace.' },
  pipeline: { group: 'One hiring journey', title: 'Pipeline', description: 'Follow each client demand through hiring and candidate selection.' },
  selection: { group: 'Close hiring', title: 'Selection & Onboarding', description: 'Coordinate interviews, offers, documents and joining readiness in one operational queue.' },
}

export default function RecruitmentPage({ view = 'Dashboard' }: { view?: RecruitmentPageView }) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const navigate = useNavigate()
  const location = useLocation()
  const routeQuery = useMemo(() => new URLSearchParams(location.search), [location.search])
  const workspace = recruitmentWorkspace(view)
  const isRequestWorkspace = workspace === 'requests'
  const boundClientId = Number(session?.user.clientId || 0)
  const canChooseClient = !boundClientId && Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [workspacePending, setWorkspacePending] = useState({ dirty: false, busy: false })
  const [selectedClientId, setSelectedClientId] = useState(() => boundClientId || Number(routeQuery.get('clientId') || sessionStorage.getItem(recruitmentClientScopeKey) || 0))
  const [dashboard, setDashboard] = useState<RecruitmentDashboard>(dashboard0)
  const [requisitionWorkflowEnabled, setRequisitionWorkflowEnabled] = useState(true)
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [manageVacanciesOpen, setManageVacanciesOpen] = useState(false)
  const [detail, setDetail] = useState<RecruitmentPositionDetail | null>(null)
  const [positionStatus, setPositionStatus] = useState('Open')
  const [positionStatusOptions, setPositionStatusOptions] = useState<string[]>(fallbackPositionStatuses)
  const [statusComment, setStatusComment] = useState('')
  const [noteText, setNoteText] = useState('')
  const [ops, setOps] = useState<RecruitmentOperationsOptions>({ allowMultipleRecruiters: true, enableVendorHiring: false, enableConsultantHiring: false, enableInternalHiring: true, enableReferralHiring: true, enableDocumentVerification: true, recruiters: [], vendors: [], consultants: [], positionStatuses: [], publishingChannels: [], assignmentPriorities: [] })
  const [recruiter, setRecruiter] = useState({ primaryRecruiterUserId: 0, secondaryRecruiterUserId: 0, assignmentReason: '' })
  const [publication, setPublication] = useState({ channel: '', publishingDate: new Date().toISOString().slice(0, 10), expiryDate: '', status: 'Published', remarks: '' })
  const [campaign, setCampaign] = useState({ campaignName: '', startDate: new Date().toISOString().slice(0, 10), endDate: new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10), referralReward: 0, visibilityDepartment: '', visibilityBusinessUnit: '', visibilityLocation: '', visibilityEmploymentType: '', status: 'Open' })

  const load = useCallback(async () => {
    // JD and ATS own their scoped queries; request-table data is not needed there.
    if (!isRequestWorkspace) return
    const [metrics, positionRows, approvalMode] = await Promise.all([
      getRecruitmentDashboard(selectedClientId),
      getRecruitmentOpenPositions(selectedClientId),
      getRecruitmentRequisitionApprovalMode(selectedClientId),
    ])
    setDashboard(metrics)
    setPositions(positionRows)
    setRequisitionWorkflowEnabled(approvalMode.workflowEnabled)
  }, [selectedClientId, isRequestWorkspace])

  useEffect(() => {
    void getClients().then(setClients)
  }, [])
  useEffect(() => {
    if (!isRequestWorkspace) return
    void getRecruitmentMasterOptions('Position Status').then(position => {
      if (position.length) setPositionStatusOptions(position)
    })
    void getRecruitmentOperationsOptions().then(setOps)
  }, [isRequestWorkspace])
  useEffect(() => {
    if (!boundClientId || selectedClientId === boundClientId) return
    setSelectedClientId(boundClientId)
  }, [boundClientId, selectedClientId])
  useEffect(() => {
    if (boundClientId || !routeQuery.has('clientId')) return
    const routeClientId = Number(routeQuery.get('clientId'))
    if (Number.isSafeInteger(routeClientId) && routeClientId >= 0) setSelectedClientId(routeClientId)
  }, [boundClientId, routeQuery])
  useEffect(() => { void load() }, [load])
  useEffect(() => { if (detail?.position.status) setPositionStatus(detail.position.status) }, [detail?.position.status])

  const confirmWorkspaceNavigation = (action: () => void) => {
    if (workspacePending.busy) {
      message.info('Please wait for the current save or upload to finish.')
      return
    }
    if (workspacePending.dirty) {
      Modal.confirm({
        title: 'Leave this workspace?',
        content: 'Unsaved edits or selected files will be discarded. Saved records will not change.',
        okText: 'Discard and continue', cancelText: 'Keep editing',
        onOk: () => { setWorkspacePending({ dirty: false, busy: false }); action() },
      })
      return
    }
    action()
  }

  const changeClientScope = (value?: number) => {
    if (!canChooseClient) return
    const next = Number(value || 0)
    if (next === selectedClientId) return
    confirmWorkspaceNavigation(() => {
      setSelectedClientId(next)
      setDetail(null)
      if (next) sessionStorage.setItem(recruitmentClientScopeKey, String(next))
      else sessionStorage.removeItem(recruitmentClientScopeKey)
      const params = new URLSearchParams(location.search)
      for (const key of ['positionId', 'requisitionId', 'jobDescriptionVersionId', 'jobPostingId', 'workOrderId', 'workOrderLineId', 'upload']) params.delete(key)
      if (next) params.set('clientId', String(next))
      else params.delete('clientId')
      const query = params.toString()
      navigate(`${location.pathname}${query ? `?${query}` : ''}`, { replace: true })
    })
  }

  const scopedPath = (path: string) => {
    if (!selectedClientId) return path
    const separator = path.includes('?') ? '&' : '?'
    return `${path}${separator}clientId=${selectedClientId}`
  }
  const selectedClientName = selectedClientId ? clients.find(row => row.id === selectedClientId)?.name || 'Selected client' : 'All accessible clients'

  const openDetail = async (row: RecruitmentOpenPosition) => {
    const next = await getRecruitmentOpenPositionDetail(row.id)
    setDetail(next)
  }

  const saveStatus = async () => {
    if (!detail?.position) return
    const response = await updateRecruitmentPositionStatus(detail.position.id, { status: positionStatus, comment: statusComment })
    if (!response.ok) return message.error('Unable to update position status.')
    const next = await response.json()
    setDetail(next)
    setStatusComment('')
    await load()
    message.success('Position status updated.')
  }

  const saveNote = async () => {
    if (!detail?.position || !noteText.trim()) return
    const response = await saveRecruitmentPositionNote(detail.position.id, { noteType: 'General', noteText })
    if (!response.ok) return message.error('Unable to save internal note.')
    const next = await response.json()
    setDetail(next)
    setNoteText('')
    message.success('Internal note saved.')
  }

  const runAction = async (call: Promise<Response>, ok: string) => {
    const response = await call
    if (!response.ok) return message.error('Action failed.')
    const next = await response.json()
    setDetail(next)
    await load()
    message.success(ok)
  }

  const copy = workspaceCopy[workspace]
  const openPositionsTable = <DataTable rows={positions} exportFileName="recruitment-open-positions" actions={row => <Space size={6} wrap>
    <Button size="small" onClick={() => void openDetail(row)}>View</Button>
    {canDelete && <Button size="small" onClick={() => navigate(`/recruitment/requisitions?clientId=${row.clientId}&requisitionId=${row.requisitionId}`)}>Edit request</Button>}
    <Button size="small" type="primary" onClick={() => navigate(`/recruitment/requisitions?requisitionId=${row.requisitionId}&clientId=${row.clientId}`)}>{jobDescriptionActionLabel(row.jobDescriptionStatus)}</Button>
    {row.jobDescriptionStatus === 'Approved' && row.remainingPositions > 0 && <Button size="small" icon={<RocketOutlined />} onClick={() => navigate(`/recruitment/job-postings?clientId=${row.clientId}&positionId=${row.id}`)}>Publish job</Button>}
    {canDelete && <Popconfirm title="Delete this open position?" description="Delete linked applications, postings and hiring cases first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteRecruitmentOpenPosition(row.id); if (response.ok) { if (detail?.position.id === row.id) setDetail(null); await load() } }}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
  </Space>} columns={[
    { key: 'positionCode', label: 'Position', render: row => <><b>{row.positionCode}</b><small>{row.rfrNumber}</small></>, value: row => row.positionCode },
    { key: 'positionTitle', label: 'Title' },
    { key: 'clientName', label: 'Client' },
    { key: 'department', label: 'Department' },
    { key: 'vacancies', label: 'Vacancies', value: row => row.remainingPositions, render: row => <><b>{row.remainingPositions} remaining</b><small>{row.filledPositions} filled / {row.approvedPositions} approved</small></> },
    { key: 'targetJoiningDate', label: 'Target', render: row => dateText(row.targetJoiningDate), value: row => row.targetJoiningDate || '' },
    { key: 'salary', label: 'Salary range', value: row => `${row.salaryMin}-${row.salaryMax}`, render: row => `${money(row.salaryMin, row.currency)} - ${money(row.salaryMax, row.currency)}` },
    { key: 'jobDescriptionStatus', label: 'JD status', render: row => row.jobDescriptionStatus || 'Not Started' },
    { key: 'pipelineStageName', label: 'Current stage', width: '190px', value: row => row.pipelineStageName || 'Pipeline not started', render: row => {
      const stageName = row.pipelineStageName || 'Pipeline not started'
      return <Tag
        data-testid={`open-position-stage-${row.id}`}
        color={recruitmentStageColor(row.pipelineStageType || '', stageName)}
        icon={<BranchesOutlined />}
        role="link"
        tabIndex={0}
        title={`Open ${stageName} in pipeline`}
        style={{ cursor: 'pointer', marginInlineEnd: 0 }}
        onClick={() => navigate(`/recruitment/hiring-pipeline?clientId=${row.clientId}&positionId=${row.id}&flow=hiring`)}
        onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); navigate(`/recruitment/hiring-pipeline?clientId=${row.clientId}&positionId=${row.id}&flow=hiring`) } }}
      >{stageName}</Tag>
    } },
    { key: 'status', label: 'Status' }
  ]} />

  const pipelineView = routeQuery.get('flow') === 'candidates' ? 'candidates' : 'hiring'
  const requestTab = !requisitionWorkflowEnabled
    ? 'requests'
    : view === 'Open Positions'
    ? 'approved'
    : routeQuery.get('status') === 'pending'
      ? 'pending'
      : 'requests'
  const workspaceContent = workspace === 'overview'
    ? <RecruitmentDashboardOverview clients={clients} selectedClientId={selectedClientId} canChooseClient={canChooseClient} onClientChange={changeClientScope} onNavigate={path => navigate(scopedPath(path))} />
    : workspace === 'orders'
      ? <RecruitmentWorkOrderWorkspace key={`work-orders-${selectedClientId}`} initialClientId={selectedClientId} clientScopeManaged displayMode="pipeline" />
    : workspace === 'requests'
        ? <Tabs
            className="recruitment-workspace-tabs recruitment-primary-tabs"
            activeKey={requestTab}
            destroyInactiveTabPane
            onChange={key => navigate(scopedPath(key === 'approved' ? '/recruitment/open-positions' : key === 'pending' ? '/recruitment/requisitions?status=pending' : '/recruitment/requisitions'))}
            items={[
              { key: 'requests', label: 'Requests', children: <RecruitmentRequisitionManager key={`${routeQuery.get('new') === '1' ? 'new-request' : 'request-list'}-${selectedClientId}-${routeQuery.get('requisitionId') || 0}-${routeQuery.get('workOrderId') || 0}-${routeQuery.get('workOrderLineId') || 0}`} embedded initialClientId={selectedClientId} clientScopeManaged initialOpen={routeQuery.get('new') === '1'} initialRequisitionId={Number(routeQuery.get('requisitionId') || 0)} initialWorkOrderId={Number(routeQuery.get('workOrderId') || 0)} initialWorkOrderLineId={Number(routeQuery.get('workOrderLineId') || 0)} statusScope={requisitionWorkflowEnabled ? draftRequisitionStatuses : []} showStatusFilter={!requisitionWorkflowEnabled} pipelinePositions={positions} onOpenPipeline={position => navigate(`/recruitment/hiring-pipeline?clientId=${position.clientId}&positionId=${position.id}&flow=hiring`)} onOpenRequestPipeline={row => navigate(`/recruitment/hiring-pipeline?clientId=${row.clientId}&flow=hiring${row.openPositionId ? `&positionId=${row.openPositionId}` : ''}`)} onChanged={() => void load()} onPrepareJobDescription={row => navigate(`/recruitment/requisitions?clientId=${row.clientId}&requisitionId=${row.id}`)} /> },
              ...(requisitionWorkflowEnabled ? [
                { key: 'pending', label: `Pending for approval (${dashboard.pendingApproval})`, children: <RecruitmentRequisitionManager key={`pending-requests-${selectedClientId}`} embedded initialClientId={selectedClientId} clientScopeManaged statusScope={pendingRequisitionStatuses} showStatusFilter={false} pipelinePositions={positions} onOpenPipeline={position => navigate(`/recruitment/hiring-pipeline?clientId=${position.clientId}&positionId=${position.id}&flow=hiring`)} onOpenRequestPipeline={row => navigate(`/recruitment/hiring-pipeline?clientId=${row.clientId}&flow=hiring${row.openPositionId ? `&positionId=${row.openPositionId}` : ''}`)} onChanged={() => void load()} /> },
                { key: 'approved', label: `Approved (${positions.length})`, children: openPositionsTable },
              ] : []),
            ]}
          />
        : workspace === 'jobs'
          ? <Tabs
              className="recruitment-workspace-tabs recruitment-primary-tabs"
              activeKey={routeQuery.get('tool') === 'forms' ? 'forms' : 'jobs'}
              destroyInactiveTabPane
              onChange={key => navigate(scopedPath(key === 'forms' ? '/recruitment/job-postings?tool=forms' : '/recruitment/job-postings'))}
              items={[
                { key: 'jobs', label: <span><RocketOutlined /> Jobs & publishing</span>, children: <RecruitmentJobPostingManager key={`posting-${selectedClientId}-${routeQuery.get('positionId') || 0}`} initialClientId={selectedClientId} clientScopeManaged initialPositionId={Number(routeQuery.get('positionId') || 0)} onManageCandidateForms={() => navigate(scopedPath('/recruitment/job-postings?tool=forms'))} /> },
                { key: 'forms', label: <span><FormOutlined /> Candidate forms</span>, children: <RecruitmentFormBuilder key={`candidate-forms-${selectedClientId}`} initialClientId={selectedClientId} clientScopeManaged /> },
              ]}
            />
          : workspace === 'candidates'
            ? view === 'Applications'
              ? <RecruitmentTalentWorkspace key={`applications-${selectedClientId}`} mode="applications" initialClientId={selectedClientId} />
              : view === 'ATS Screening'
                ? <RecruitmentAtsWorkspace key={`ats-${selectedClientId}-${routeQuery.get('positionId') || 0}`} initialClientId={selectedClientId} initialPositionId={Number(routeQuery.get('positionId') || 0)} initialJobPostingId={Number(routeQuery.get('jobPostingId') || 0) || null} clientScopeManaged initialUploadMode={routeQuery.get('upload') === 'bulk' ? 'bulk' : routeQuery.get('upload') === 'single' ? 'single' : undefined} onNavigationStateChange={setWorkspacePending} />
                : <RecruitmentTalentWorkspace key={`talent-${selectedClientId}`} mode="candidates" initialClientId={selectedClientId} />
            : workspace === 'pipeline'
              ? <RecruitmentPipelineWorkspace
                  key={`pipeline-${selectedClientId}-${pipelineView}`}
                  initialClientId={selectedClientId}
                  clientScopeManaged
                  positionId={Number(routeQuery.get('positionId') || 0)}
                  initialView={pipelineView}
                  canChooseClient={canChooseClient}
                  clientOptions={clients.map(row => ({ value: row.id, label: `${row.code} · ${row.name}` }))}
                  onClientChange={changeClientScope}
                />
              : <Tabs
                  className="recruitment-workspace-tabs recruitment-primary-tabs"
                  activeKey={view === 'Offers & Pre-Onboarding' ? 'offers' : 'interviews'}
                  onChange={key => navigate(scopedPath(key === 'offers' ? '/recruitment/offers-and-pre-onboarding' : '/recruitment/interviews'))}
                  items={[
                    { key: 'interviews', label: 'Interviews', children: <RecruitmentTalentWorkspace key={`interviews-${selectedClientId}`} mode="interviews" initialClientId={selectedClientId} /> },
                    { key: 'offers', label: 'Offers & pre-onboarding', children: <RecruitmentTalentWorkspace key={`offers-${selectedClientId}`} mode="offers" initialClientId={selectedClientId} /> },
                  ]}
                />

  return <section className="recruitment-monitor-page recruitment-experience" aria-label={copy.title}>
    {!['pipeline', 'overview'].includes(workspace) && <header className="recruitment-scopebar">
      <div>
        <span>Client scope</span>
        <strong>{selectedClientName}</strong>
        <small>{copy.group}</small>
      </div>
      <div className="recruitment-header-actions">
        {canChooseClient && <Select data-testid="recruitment-client-scope" aria-label="Recruitment client scope" allowClear showSearch optionFilterProp="label" value={selectedClientId || undefined} placeholder="All accessible clients" options={clients.map(row => ({ value: row.id, label: `${row.code} · ${row.name}` }))} onChange={changeClientScope} />}
        {workspace === 'requests' && canDelete && !requisitionWorkflowEnabled && <Button onClick={() => setManageVacanciesOpen(true)}>Manage vacancies</Button>}
        {workspace === 'requests' && <Button data-testid="recruitment-new-hiring-request" type="primary" icon={<PlusOutlined />} onClick={() => navigate(scopedPath('/recruitment/requisitions?new=1'))}>New hiring request</Button>}
      </div>
    </header>}
    <div className="recruitment-workspace-surface">
      <div className="recruitment-workspace-content">
        {workspaceContent}
      </div>
    </div>
    <Drawer title="Manage vacancies" open={canDelete && manageVacanciesOpen} width="min(1400px, 96vw)" onClose={() => setManageVacanciesOpen(false)} destroyOnClose>{openPositionsTable}</Drawer>
    <Drawer className="recruitment-detail-drawer" open={!!detail} width="min(1120px, 96vw)" onClose={() => setDetail(null)} destroyOnClose title={detail?.position?.positionCode ? `${detail.position.positionCode} - ${detail.position.positionTitle}` : 'Open position'}>
      {detail?.position && <section className="recruitment-position-detail">
        <div className="position-summary-panel">
          {[
            ['Position number', detail.position.positionCode],
            ['Department', detail.position.department],
            ['Client', detail.position.clientName],
            ['Project', detail.position.project || '-'],
            ['Hiring type', detail.position.hiringType],
            ['Employment type', detail.position.employmentType],
            ['Open positions', detail.position.approvedPositions],
            ['Filled', detail.position.filledPositions],
            ['Remaining', detail.position.remainingPositions],
            ['Priority', detail.position.hiringPriority || 'Normal'],
            ['Target joining', dateText(detail.position.targetJoiningDate)],
            ['Current status', detail.position.status],
          ].map(([label, value]) => <article key={String(label)}><span>{label}</span><b>{value}</b></article>)}
        </div>
        <div className="position-status-bar">
          <Select value={positionStatus} onChange={setPositionStatus} options={positionStatusOptions.map(value => ({ value, label: value }))} />
          <Input value={statusComment} onChange={event => setStatusComment(event.target.value)} placeholder="Status change comment" />
          <Button type="primary" onClick={() => void saveStatus()}>Update status</Button>
        </div>
        <Tabs className="recruitment-workspace-tabs" items={[
          { key: 'timeline', label: 'Timeline', children: <Card size="small" title="Timeline"><div className="recruitment-timeline">{detail.timeline.map(item => <article key={item.id}><i /> <div><b>{item.eventTitle}</b><span>{item.eventDetails || item.eventType}</span><small>{dateText(item.createdAt)} / {item.actorName || 'System'}</small></div></article>)}{!detail.timeline.length && <p>No timeline yet.</p>}</div></Card> },
          { key: 'documents', label: 'Document Requirements', children: <Card size="small" title="Position document requirements" extra={canDelete ? <Button onClick={() => { setDetail(null); navigate(`/recruitment/hiring-pipeline?clientId=${detail.position.clientId}&positionId=${detail.position.id}&manage=1`) }}>Manage pipeline documents</Button> : null}><div className="recruitment-checklist">{detail.checklist.map(item => <article key={item.id}><b>{item.checklistName}</b><span>{item.stage || '-'} / {item.mandatory ? 'Mandatory' : 'Optional'}</span><small>{item.isCompleted ? 'Completed' : 'Pending'}</small></article>)}{!detail.checklist.length && <p>No position checklist is configured. Candidate-facing document requirements are managed on the assigned pipeline's Documents or Pre-onboarding stage.</p>}</div></Card> },
          { key: 'ownership', label: 'Recruiter Assignment', children: <div className="recruitment-operation-grid"><Card size="small" title="Assign recruiter"><label><span>Primary recruiter</span><Select value={recruiter.primaryRecruiterUserId} onChange={value => setRecruiter({ ...recruiter, primaryRecruiterUserId: value })} options={[{ value: 0, label: 'Select recruiter' }, ...ops.recruiters.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` }))]} /></label><label><span>Secondary recruiter</span><Select value={recruiter.secondaryRecruiterUserId} onChange={value => setRecruiter({ ...recruiter, secondaryRecruiterUserId: value })} options={[{ value: 0, label: 'None' }, ...ops.recruiters.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` }))]} /></label><label className="wide"><span>Reason</span><Input value={recruiter.assignmentReason} onChange={event => setRecruiter({ ...recruiter, assignmentReason: event.target.value })} /></label><Button type="primary" onClick={() => void runAction(assignRecruiter(detail.position.id, recruiter), 'Recruiter assigned.')}>Assign</Button></Card><Card size="small" title="Assignment history"><MiniRows rows={detail.recruiterAssignments} getTitle={row => row.primaryRecruiterName} getText={row => `${row.assignmentStatus} / ${dateText(row.assignmentDate)} / ${row.assignedByName}`} /></Card></div> },
          { key: 'publishing', label: 'Publishing', children: <div className="recruitment-operation-grid"><Card size="small" title="Publish position"><label><span>Channel</span><Select value={publication.channel} onChange={value => setPublication({ ...publication, channel: value })} options={publishingOptions(detail.position.hiringType, ops.publishingChannels).map(value => ({ value, label: value }))} /></label><label><span>Publishing date</span><Input type="date" value={publication.publishingDate} onChange={event => setPublication({ ...publication, publishingDate: event.target.value })} /></label><label><span>Expiry date</span><Input type="date" value={publication.expiryDate} onChange={event => setPublication({ ...publication, expiryDate: event.target.value })} /></label><label><span>Status</span><Select value={publication.status} onChange={value => setPublication({ ...publication, status: value })} options={['Draft', 'Published', 'Expired', 'Closed'].map(value => ({ value, label: value }))} /></label><label className="wide"><span>Remarks</span><Input value={publication.remarks} onChange={event => setPublication({ ...publication, remarks: event.target.value })} /></label><Button type="primary" onClick={() => void runAction(publishPosition(detail.position.id, publication), 'Position published.')}>Publish</Button></Card><Card size="small" title="Publishing history"><MiniRows rows={detail.publications} getTitle={row => row.channel} getText={row => `${row.status} / ${dateText(row.publishingDate)} - ${dateText(row.expiryDate)}`} /></Card></div> },
          String(detail.position.hiringType || '').toLowerCase().includes('referral') && { key: 'referrals', label: 'Referral Campaign', children: <div className="recruitment-operation-grid"><Card size="small" title="Create referral campaign"><label><span>Campaign name</span><Input value={campaign.campaignName} onChange={event => setCampaign({ ...campaign, campaignName: event.target.value })} /></label><label><span>Start date</span><Input type="date" value={campaign.startDate} onChange={event => setCampaign({ ...campaign, startDate: event.target.value })} /></label><label><span>End date</span><Input type="date" value={campaign.endDate} onChange={event => setCampaign({ ...campaign, endDate: event.target.value })} /></label><label><span>Reward</span><InputNumber value={campaign.referralReward} onChange={value => setCampaign({ ...campaign, referralReward: Number(value || 0) })} /></label><label><span>Department visibility</span><Input value={campaign.visibilityDepartment} onChange={event => setCampaign({ ...campaign, visibilityDepartment: event.target.value })} placeholder="Blank means all" /></label><label><span>Location visibility</span><Input value={campaign.visibilityLocation} onChange={event => setCampaign({ ...campaign, visibilityLocation: event.target.value })} placeholder="Blank means all" /></label><Button type="primary" onClick={() => void runAction(createReferralCampaign(detail.position.id, campaign), 'Referral campaign created.')}>Create campaign</Button></Card><Card size="small" title="Campaigns"><MiniRows rows={detail.referralCampaigns} getTitle={row => row.campaignName} getText={row => `${row.status} / reward ${money(row.referralReward)} / ends ${dateText(row.endDate)}`} /></Card></div> },
          { key: 'notes', label: 'Internal Notes', children: <Card size="small" title="Internal notes" className="position-notes-card"><Space.Compact style={{ width: '100%' }}><Input value={noteText} onChange={event => setNoteText(event.target.value)} placeholder="Add HR/recruiter internal note" /><Button type="primary" onClick={() => void saveNote()}>Add</Button></Space.Compact><div className="recruitment-notes">{detail.notes.map(item => <article key={item.id}><b>{item.noteType}</b><p>{item.noteText}</p><small>{item.createdByName} / {dateText(item.createdAt)}</small></article>)}{!detail.notes.length && <p>No internal notes.</p>}</div></Card> }
        ].filter(Boolean) as any} />
      </section>}
    </Drawer>
  </section>
}

function jobDescriptionActionLabel(status?: string) {
  const value = String(status || 'Not Started').toLowerCase()
  if (value === 'draft' || value === 'sent back') return 'Continue JD'
  if (value === 'pending approval') return 'View pending JD'
  if (value === 'approved') return 'View approved JD'
  if (value === 'published' || value === 'retired') return 'View JD'
  return 'Create JD'
}

function MiniRows<T>({ rows, getTitle, getText }: { rows: T[]; getTitle: (row: T) => string; getText: (row: T) => string }) {
  return <div className="recruitment-mini-rows">{rows.map((row, index) => <article key={index}><b>{getTitle(row)}</b><span>{getText(row)}</span></article>)}{!rows.length && <p className="muted">No records.</p>}</div>
}

function publishingOptions(hiringType: string, configured: string[]) {
  const channels = configured.length ? configured : ['Career Site', 'Internal Job Portal', 'Employee Referral', 'Campus', 'Walk-in']
  const type = String(hiringType || '').toLowerCase()
  const keyword = type.includes('referral') ? 'referral'
    : type.includes('internal') ? 'internal'
      : type.includes('campus') ? 'campus'
        : type.includes('walk') ? 'walk'
          : ''
  if (!keyword) return channels
  const matched = channels.filter(channel => channel.toLowerCase().includes(keyword))
  return matched.length ? matched : [keyword === 'referral' ? 'Employee Referral' : keyword === 'internal' ? 'Internal Job Portal' : keyword === 'walk' ? 'Walk-in' : 'Campus']
}
