import { useCallback, useEffect, useMemo, useState } from 'react'
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Card, Drawer, Empty, Input, InputNumber, Popconfirm, Select, Space, Tabs, message } from 'antd'
import { useLocation, useNavigate } from 'react-router-dom'
import DataTable from '../components/DataTable'
import RecruitmentAtsWorkspace from '../components/RecruitmentAtsWorkspace'
import RecruitmentJobDescriptionManager from '../components/RecruitmentJobDescriptionManager'
import RecruitmentJobPostingManager from '../components/RecruitmentJobPostingManager'
import RecruitmentPipelineBoard from '../components/RecruitmentPipelineBoard'
import RecruitmentRequisitionManager from '../components/RecruitmentRequisitionManager'
import RecruitmentTalentWorkspace from '../components/RecruitmentTalentWorkspace'
import RecruitmentWorkOrderWorkspace from '../components/RecruitmentWorkOrderWorkspace'
import { useAuthSession } from '../components/AuthGate'
import { getClients } from '../services/payrollService'
import { assignConsultant, assignRecruiter, assignVendor, createReferralCampaign, deleteRecruitmentOpenPosition, getRecruitmentDashboard, getRecruitmentMasterOptions, getRecruitmentOpenPositionDetail, getRecruitmentOpenPositions, getRecruitmentOperationsOptions, publishPosition, saveRecruitmentPositionNote, updateRecruitmentPositionStatus } from '../services/recruitmentService'
import type { Client, RecruitmentDashboard, RecruitmentMetric, RecruitmentOpenPosition, RecruitmentOperationsOptions, RecruitmentPositionDetail } from '../types/payroll'

const fallbackPositionStatuses = ['Open', 'Recruiter Assigned', 'Published', 'Candidate Screening', 'Interview In Progress', 'Offer Released', 'Offer Accepted', 'Joining Pending', 'Filled', 'Partially Filled', 'Cancelled', 'Closed', 'On Hold']
const money = (value: number, currency = 'INR') => `${currency} ${Number(value || 0).toLocaleString('en-IN')}`
const dateText = (value?: string | null) => value ? new Date(value).toLocaleDateString('en-GB') : '-'
const dashboard0: RecruitmentDashboard = { drafts: 0, pendingApproval: 0, approved: 0, rejected: 0, returned: 0, withdrawn: 0, openPositions: 0, filledPositions: 0, cancelledPositions: 0, onHoldPositions: 0, remainingPositions: 0, averageApprovalHours: 0, departmentWiseHiring: [], companyWiseHiring: [], priorityWiseHiring: [], upcomingJoiningTargets: [] }
const recruitmentClientScopeKey = 'recruitment.clientScope'

export const recruitmentViews = ['Dashboard', 'Work Orders & SLA', 'Requisitions', 'Open Positions', 'Job Descriptions', 'Job Postings', 'ATS Screening', 'Hiring Pipeline', 'Talent Pool', 'Applications', 'Interviews', 'Offers & Pre-Onboarding'] as const
export type RecruitmentPageView = (typeof recruitmentViews)[number]

type RecruitmentWorkspace = 'overview' | 'requests' | 'jobs' | 'candidates' | 'pipeline' | 'selection' | 'delivery'

const recruitmentWorkspace = (view: RecruitmentPageView): RecruitmentWorkspace => {
  if (view === 'Dashboard') return 'overview'
  if (['Requisitions', 'Open Positions'].includes(view)) return 'requests'
  if (['Job Descriptions', 'Job Postings'].includes(view)) return 'jobs'
  if (['Talent Pool', 'Applications', 'ATS Screening'].includes(view)) return 'candidates'
  if (view === 'Hiring Pipeline') return 'pipeline'
  if (['Interviews', 'Offers & Pre-Onboarding'].includes(view)) return 'selection'
  return 'delivery'
}

const workspaceCopy: Record<RecruitmentWorkspace, { group: string; title: string; description: string }> = {
  overview: { group: 'Talent acquisition', title: 'Overview', description: 'Hiring demand, approvals, open roles and joining targets in one concise view.' },
  requests: { group: 'Plan hiring', title: 'Hiring Requests', description: 'Raise a request and follow the same demand through approval into an approved vacancy.' },
  jobs: { group: 'Attract talent', title: 'Jobs', description: 'Prepare the governed role profile, then publish its approved job and public application link.' },
  candidates: { group: 'Find talent', title: 'Candidates', description: 'Manage reusable talent profiles, job applications and ATS screening from one workspace.' },
  pipeline: { group: 'Select talent', title: 'Candidate Pipeline', description: 'Use configured transitions, approvals and SLA controls to move candidates safely.' },
  selection: { group: 'Close hiring', title: 'Selection & Onboarding', description: 'Coordinate interviews, offers, documents and joining readiness in one operational queue.' },
  delivery: { group: 'Client hiring', title: 'Client Delivery SLA', description: 'Track contract hiring orders and role fulfilment against agreed client timelines.' },
}

export default function RecruitmentPage({ view = 'Dashboard' }: { view?: RecruitmentPageView }) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const navigate = useNavigate()
  const location = useLocation()
  const routeQuery = useMemo(() => new URLSearchParams(location.search), [location.search])
  const boundClientId = Number(session?.user.clientId || 0)
  const canChooseClient = !boundClientId && Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [selectedClientId, setSelectedClientId] = useState(() => boundClientId || Number(routeQuery.get('clientId') || sessionStorage.getItem(recruitmentClientScopeKey) || 0))
  const [dashboard, setDashboard] = useState<RecruitmentDashboard>(dashboard0)
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [detail, setDetail] = useState<RecruitmentPositionDetail | null>(null)
  const [positionStatus, setPositionStatus] = useState('Open')
  const [positionStatusOptions, setPositionStatusOptions] = useState<string[]>(fallbackPositionStatuses)
  const [statusComment, setStatusComment] = useState('')
  const [noteText, setNoteText] = useState('')
  const [ops, setOps] = useState<RecruitmentOperationsOptions>({ allowMultipleRecruiters: false, enableVendorHiring: false, enableConsultantHiring: false, enableInternalHiring: false, enableReferralHiring: false, enableDocumentVerification: false, recruiters: [], vendors: [], consultants: [], positionStatuses: [], publishingChannels: [], assignmentPriorities: [] })
  const [recruiter, setRecruiter] = useState({ primaryRecruiterUserId: 0, secondaryRecruiterUserId: 0, assignmentReason: '' })
  const [vendor, setVendor] = useState({ partnerId: 0, priority: 'Normal', dueDate: '', expectedProfiles: 0, remarks: '' })
  const [consultant, setConsultant] = useState({ partnerId: 0, priority: 'Normal', dueDate: '', expectedProfiles: 0, remarks: '' })
  const [publication, setPublication] = useState({ channel: '', publishingDate: new Date().toISOString().slice(0, 10), expiryDate: '', status: 'Published', remarks: '' })
  const [campaign, setCampaign] = useState({ campaignName: '', startDate: new Date().toISOString().slice(0, 10), endDate: new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10), referralReward: 0, visibilityDepartment: '', visibilityBusinessUnit: '', visibilityLocation: '', visibilityEmploymentType: '', status: 'Open' })

  const load = useCallback(async () => {
    const [metrics, positionRows] = await Promise.all([
      getRecruitmentDashboard(selectedClientId),
      getRecruitmentOpenPositions(selectedClientId)
    ])
    setDashboard(metrics)
    setPositions(positionRows)
  }, [selectedClientId])

  useEffect(() => {
    void getClients().then(setClients)
    void getRecruitmentMasterOptions('Position Status').then(position => {
      if (position.length) setPositionStatusOptions(position)
    })
    void getRecruitmentOperationsOptions().then(setOps)
  }, [])
  useEffect(() => {
    if (!boundClientId || selectedClientId === boundClientId) return
    setSelectedClientId(boundClientId)
  }, [boundClientId, selectedClientId])
  useEffect(() => { void load() }, [load])
  useEffect(() => { if (detail?.position.status) setPositionStatus(detail.position.status) }, [detail?.position.status])

  const changeClientScope = (value?: number) => {
    if (!canChooseClient) return
    const next = Number(value || 0)
    setSelectedClientId(next)
    setDetail(null)
    if (next) sessionStorage.setItem(recruitmentClientScopeKey, String(next))
    else sessionStorage.removeItem(recruitmentClientScopeKey)
    const params = new URLSearchParams(location.search)
    if (next) params.set('clientId', String(next))
    else params.delete('clientId')
    const query = params.toString()
    navigate(`${location.pathname}${query ? `?${query}` : ''}`, { replace: true })
  }

  const scopedPath = (path: string) => {
    if (!selectedClientId) return path
    const separator = path.includes('?') ? '&' : '?'
    return `${path}${separator}clientId=${selectedClientId}`
  }
  const selectedClientName = selectedClientId ? clients.find(row => row.id === selectedClientId)?.name || 'Selected client' : 'All accessible clients'

  const summary = useMemo<Array<[string, string | number, string]>>(() => [
    ['Pending approvals', dashboard.pendingApproval, '/tasks'],
    ['Open vacancies', dashboard.openPositions, '/recruitment/open-positions'],
    ['Filled', dashboard.filledPositions, '/recruitment/open-positions'],
    ['On hold', dashboard.onHoldPositions, '/recruitment/open-positions'],
    ['Cancelled', dashboard.cancelledPositions, '/recruitment/open-positions'],
    ['Avg approval hrs', Number(dashboard.averageApprovalHours || 0).toFixed(1), '/recruitment/requisitions'],
  ], [dashboard])

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

  const workspace = recruitmentWorkspace(view)
  const copy = workspaceCopy[workspace]
  const openPositionsTable = <DataTable rows={positions} exportFileName="recruitment-open-positions" actions={row => <Space size={6} wrap>
    <Button size="small" onClick={() => void openDetail(row)}>View</Button>
    <Button size="small" type="primary" onClick={() => navigate(`/recruitment/job-descriptions?requisitionId=${row.requisitionId}&clientId=${row.clientId}`)}>Prepare JD</Button>
    {canDelete && <Popconfirm title="Delete this open position?" description="Delete linked applications, postings and hiring cases first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteRecruitmentOpenPosition(row.id); if (response.ok) { if (detail?.position.id === row.id) setDetail(null); await load() } }}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
  </Space>} columns={[
    { key: 'positionCode', label: 'Position', render: row => <><b>{row.positionCode}</b><small>{row.rfrNumber}</small></>, value: row => row.positionCode },
    { key: 'positionTitle', label: 'Title' },
    { key: 'clientName', label: 'Client' },
    { key: 'department', label: 'Department' },
    { key: 'vacancies', label: 'Vacancies', value: row => row.remainingPositions, render: row => <><b>{row.remainingPositions} remaining</b><small>{row.filledPositions} filled / {row.approvedPositions} approved</small></> },
    { key: 'targetJoiningDate', label: 'Target', render: row => dateText(row.targetJoiningDate), value: row => row.targetJoiningDate || '' },
    { key: 'salary', label: 'Salary range', value: row => `${row.salaryMin}-${row.salaryMax}`, render: row => `${money(row.salaryMin, row.currency)} - ${money(row.salaryMax, row.currency)}` },
    { key: 'status', label: 'Status' }
  ]} />

  const workspaceContent = workspace === 'overview'
    ? <Dashboard summary={summary} dashboard={dashboard} scopeLabel={selectedClientName} onNavigate={path => navigate(scopedPath(path))} onNew={() => navigate(scopedPath('/recruitment/requisitions?new=1'))} />
    : workspace === 'delivery'
      ? <RecruitmentWorkOrderWorkspace key={`delivery-${selectedClientId}`} initialClientId={selectedClientId} clientScopeManaged />
      : workspace === 'requests'
        ? <Tabs
            className="recruitment-workspace-tabs recruitment-primary-tabs"
            activeKey={view === 'Open Positions' ? 'positions' : 'requests'}
            onChange={key => navigate(scopedPath(key === 'positions' ? '/recruitment/open-positions' : '/recruitment/requisitions'))}
            items={[
              { key: 'requests', label: 'Requests', children: <RecruitmentRequisitionManager key={`${routeQuery.get('new') === '1' ? 'new-request' : 'request-list'}-${selectedClientId}`} embedded initialClientId={selectedClientId} clientScopeManaged initialOpen={routeQuery.get('new') === '1'} onChanged={() => void load()} onPrepareJobDescription={row => navigate(`/recruitment/job-descriptions?requisitionId=${row.id}&clientId=${row.clientId}`)} /> },
              { key: 'positions', label: `Approved vacancies (${positions.length})`, children: openPositionsTable },
            ]}
          />
        : workspace === 'jobs'
          ? <Tabs
              className="recruitment-workspace-tabs recruitment-primary-tabs"
              activeKey={view === 'Job Postings' ? 'publishing' : 'profiles'}
              onChange={key => navigate(scopedPath(key === 'publishing' ? '/recruitment/job-postings' : '/recruitment/job-descriptions'))}
              items={[
                { key: 'profiles', label: 'Role profiles & ATS', children: <RecruitmentJobDescriptionManager key={`jd-${selectedClientId}`} initialClientId={Number(routeQuery.get('clientId') || selectedClientId)} clientScopeManaged initialRequisitionId={Number(routeQuery.get('requisitionId') || 0)} /> },
                { key: 'publishing', label: 'Publishing & public links', children: <RecruitmentJobPostingManager key={`posting-${selectedClientId}`} initialClientId={Number(routeQuery.get('clientId') || selectedClientId)} clientScopeManaged initialPositionId={Number(routeQuery.get('positionId') || 0)} /> },
              ]}
            />
          : workspace === 'candidates'
            ? <Tabs
                className="recruitment-workspace-tabs recruitment-primary-tabs"
                activeKey={view === 'Applications' ? 'applications' : view === 'ATS Screening' ? 'ats' : 'talent'}
                onChange={key => navigate(scopedPath(key === 'applications' ? '/recruitment/applications' : key === 'ats' ? '/recruitment/ats-screening' : '/recruitment/talent-pool'))}
                items={[
                  { key: 'talent', label: 'Talent profiles', children: <RecruitmentTalentWorkspace key={`talent-${selectedClientId}`} mode="candidates" initialClientId={selectedClientId} /> },
                  { key: 'applications', label: 'Applications', children: <RecruitmentTalentWorkspace key={`applications-${selectedClientId}`} mode="applications" initialClientId={selectedClientId} /> },
                  { key: 'ats', label: 'ATS review & resume intake', children: <RecruitmentAtsWorkspace key={`ats-${selectedClientId}`} initialClientId={selectedClientId} clientScopeManaged initialUploadMode={routeQuery.get('upload') === 'bulk' ? 'bulk' : routeQuery.get('upload') === 'single' ? 'single' : undefined} /> },
                ]}
              />
            : workspace === 'pipeline'
              ? <RecruitmentPipelineBoard key={`pipeline-${selectedClientId}`} initialClientId={selectedClientId} clientScopeManaged positionId={Number(routeQuery.get('positionId') || 0)} />
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
    <header className="recruitment-experience-header">
      <div>
        <span className="recruitment-workspace-group">{copy.group}</span>
        <p>{copy.description}</p>
      </div>
      <div className="recruitment-header-actions">
        {canChooseClient && <Select data-testid="recruitment-client-scope" aria-label="Recruitment client scope" allowClear showSearch optionFilterProp="label" value={selectedClientId || undefined} placeholder="All accessible clients" options={clients.map(row => ({ value: row.id, label: `${row.code} · ${row.name}` }))} onChange={changeClientScope} />}
        {['overview', 'requests'].includes(workspace) && <Button data-testid="recruitment-new-hiring-request" type="primary" icon={<PlusOutlined />} onClick={() => navigate(scopedPath('/recruitment/requisitions?new=1'))}>New hiring request</Button>}
      </div>
    </header>
    <div className="recruitment-workspace-surface">
      <div className="recruitment-workspace-content">
        {workspaceContent}
      </div>
    </div>
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
          { key: 'timeline', label: 'Timeline', children: <div className="position-detail-grid"><Card size="small" title="Timeline"><div className="recruitment-timeline">{detail.timeline.map(item => <article key={item.id}><i /> <div><b>{item.eventTitle}</b><span>{item.eventDetails || item.eventType}</span><small>{dateText(item.createdAt)} / {item.actorName || 'System'}</small></div></article>)}{!detail.timeline.length && <p>No timeline yet.</p>}</div></Card>{detail.enableDocumentVerification && <Card size="small" title="Checklist"><div className="recruitment-checklist">{detail.checklist.map(item => <article key={item.id}><b>{item.checklistName}</b><span>{item.stage || '-'} / {item.mandatory ? 'Mandatory' : 'Optional'}</span><small>{item.isCompleted ? 'Completed' : 'Pending'}</small></article>)}{!detail.checklist.length && <p>No checklist configured.</p>}</div></Card>}</div> },
          { key: 'ownership', label: 'Recruiter Assignment', children: <div className="recruitment-operation-grid"><Card size="small" title="Assign recruiter"><label><span>Primary recruiter</span><Select value={recruiter.primaryRecruiterUserId} onChange={value => setRecruiter({ ...recruiter, primaryRecruiterUserId: value })} options={[{ value: 0, label: 'Select recruiter' }, ...ops.recruiters.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` }))]} /></label>{detail.allowMultipleRecruiters && <label><span>Secondary recruiter</span><Select value={recruiter.secondaryRecruiterUserId} onChange={value => setRecruiter({ ...recruiter, secondaryRecruiterUserId: value })} options={[{ value: 0, label: 'None' }, ...ops.recruiters.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` }))]} /></label>}<label className="wide"><span>Reason</span><Input value={recruiter.assignmentReason} onChange={event => setRecruiter({ ...recruiter, assignmentReason: event.target.value })} /></label><Button type="primary" onClick={() => void runAction(assignRecruiter(detail.position.id, recruiter), 'Recruiter assigned.')}>Assign</Button></Card><Card size="small" title="Assignment history"><MiniRows rows={detail.recruiterAssignments} getTitle={row => row.primaryRecruiterName} getText={row => `${row.assignmentStatus} / ${dateText(row.assignmentDate)} / ${row.assignedByName}`} /></Card></div> },
          (detail.enableVendorHiring || detail.enableConsultantHiring) && { key: 'partners', label: 'Partners', children: <div className="recruitment-operation-grid">{detail.enableVendorHiring && <PartnerForm title="Assign vendor" partners={ops.vendors} priorities={ops.assignmentPriorities} value={vendor} setValue={setVendor} onSave={() => void runAction(assignVendor(detail.position.id, vendor), 'Vendor assigned.')} />}{detail.enableConsultantHiring && <PartnerForm title="Assign consultant" partners={ops.consultants} priorities={ops.assignmentPriorities} value={consultant} setValue={setConsultant} onSave={() => void runAction(assignConsultant(detail.position.id, consultant), 'Consultant assigned.')} />}{detail.enableVendorHiring && <Card size="small" title="Vendor assignments"><MiniRows rows={detail.vendorAssignments} getTitle={row => row.partnerName} getText={row => `${row.priority} / ${row.status} / due ${dateText(row.dueDate)}`} /></Card>}{detail.enableConsultantHiring && <Card size="small" title="Consultant assignments"><MiniRows rows={detail.consultantAssignments} getTitle={row => row.partnerName} getText={row => `${row.priority} / ${row.status} / due ${dateText(row.dueDate)}`} /></Card>}</div> },
          (detail.enableInternalHiring || detail.enableReferralHiring) && { key: 'publishing', label: 'Publishing', children: <div className="recruitment-operation-grid"><Card size="small" title="Publish position"><label><span>Channel</span><Select value={publication.channel} onChange={value => setPublication({ ...publication, channel: value })} options={(ops.publishingChannels.length ? ops.publishingChannels : ['Internal Job Portal', 'Employee Referral']).filter(value => (detail.enableInternalHiring || !value.toLowerCase().includes('internal')) && (detail.enableReferralHiring || !value.toLowerCase().includes('referral'))).map(value => ({ value, label: value }))} /></label><label><span>Publishing date</span><Input type="date" value={publication.publishingDate} onChange={event => setPublication({ ...publication, publishingDate: event.target.value })} /></label><label><span>Expiry date</span><Input type="date" value={publication.expiryDate} onChange={event => setPublication({ ...publication, expiryDate: event.target.value })} /></label><label><span>Status</span><Select value={publication.status} onChange={value => setPublication({ ...publication, status: value })} options={['Draft', 'Published', 'Expired', 'Closed'].map(value => ({ value, label: value }))} /></label><label className="wide"><span>Remarks</span><Input value={publication.remarks} onChange={event => setPublication({ ...publication, remarks: event.target.value })} /></label><Button type="primary" onClick={() => void runAction(publishPosition(detail.position.id, publication), 'Position published.')}>Publish</Button></Card><Card size="small" title="Publishing history"><MiniRows rows={detail.publications} getTitle={row => row.channel} getText={row => `${row.status} / ${dateText(row.publishingDate)} - ${dateText(row.expiryDate)}`} /></Card></div> },
          detail.enableReferralHiring && { key: 'referrals', label: 'Referral Campaign', children: <div className="recruitment-operation-grid"><Card size="small" title="Create referral campaign"><label><span>Campaign name</span><Input value={campaign.campaignName} onChange={event => setCampaign({ ...campaign, campaignName: event.target.value })} /></label><label><span>Start date</span><Input type="date" value={campaign.startDate} onChange={event => setCampaign({ ...campaign, startDate: event.target.value })} /></label><label><span>End date</span><Input type="date" value={campaign.endDate} onChange={event => setCampaign({ ...campaign, endDate: event.target.value })} /></label><label><span>Reward</span><InputNumber value={campaign.referralReward} onChange={value => setCampaign({ ...campaign, referralReward: Number(value || 0) })} /></label><label><span>Department visibility</span><Input value={campaign.visibilityDepartment} onChange={event => setCampaign({ ...campaign, visibilityDepartment: event.target.value })} placeholder="Blank means all" /></label><label><span>Location visibility</span><Input value={campaign.visibilityLocation} onChange={event => setCampaign({ ...campaign, visibilityLocation: event.target.value })} placeholder="Blank means all" /></label><Button type="primary" onClick={() => void runAction(createReferralCampaign(detail.position.id, campaign), 'Referral campaign created.')}>Create campaign</Button></Card><Card size="small" title="Campaigns"><MiniRows rows={detail.referralCampaigns} getTitle={row => row.campaignName} getText={row => `${row.status} / reward ${money(row.referralReward)} / ends ${dateText(row.endDate)}`} /></Card></div> },
          { key: 'notes', label: 'Internal Notes', children: <Card size="small" title="Internal notes" className="position-notes-card"><Space.Compact style={{ width: '100%' }}><Input value={noteText} onChange={event => setNoteText(event.target.value)} placeholder="Add HR/recruiter internal note" /><Button type="primary" onClick={() => void saveNote()}>Add</Button></Space.Compact><div className="recruitment-notes">{detail.notes.map(item => <article key={item.id}><b>{item.noteType}</b><p>{item.noteText}</p><small>{item.createdByName} / {dateText(item.createdAt)}</small></article>)}{!detail.notes.length && <p>No internal notes.</p>}</div></Card> }
        ].filter(Boolean) as any} />
      </section>}
    </Drawer>
  </section>
}

function Dashboard({ summary, dashboard, scopeLabel, onNavigate, onNew }: { summary: Array<[string, string | number, string]>; dashboard: RecruitmentDashboard; scopeLabel: string; onNavigate: (path: string) => void; onNew: () => void }) {
  const requestCount = dashboard.drafts + dashboard.pendingApproval + dashboard.approved + dashboard.rejected + dashboard.returned + dashboard.withdrawn
  return <>
    {requestCount === 0 && dashboard.openPositions === 0 && <Card className="recruitment-empty-state"><Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={<><b>No hiring activity for {scopeLabel}</b><span>Create the first hiring request, or choose another client to review its recruitment lifecycle.</span></>}><Button type="primary" icon={<PlusOutlined />} onClick={onNew}>Create hiring request</Button></Empty></Card>}
    <div className="travel-advance-summary recruitment-summary recruitment-overview-metrics">{summary.map(([label, value, path]) => <button type="button" key={String(label)} onClick={() => onNavigate(path)}><span>{label}</span><b>{value}</b><small>Open workspace</small></button>)}</div>
    <section className="recruitment-lifecycle-funnel" aria-label="Hiring lifecycle">
      {[
        ['Requests', requestCount],
        ['Approved', dashboard.approved],
        ['Open vacancies', dashboard.openPositions],
        ['Filled', dashboard.filledPositions],
      ].map(([label, value], index) => <article key={String(label)}><i>{index + 1}</i><span>{label}</span><b>{value}</b></article>)}
    </section>
    <div className="recruitment-dashboard-grid">
      <MetricList title="Department-wise hiring" rows={dashboard.departmentWiseHiring} />
      <MetricList title="Company-wise hiring" rows={dashboard.companyWiseHiring} />
      <MetricList title="Priority-wise hiring" rows={dashboard.priorityWiseHiring} />
      <MetricList title="Upcoming joining targets" rows={dashboard.upcomingJoiningTargets} />
    </div>
  </>
}

function MetricList({ title, rows }: { title: string; rows: RecruitmentMetric[] }) {
  return <Card size="small" title={title}><div className="recruitment-metric-list">{rows.map(row => <article key={row.label}><span>{row.label}</span><b>{row.value}</b></article>)}{!rows.length && <p className="muted">No data.</p>}</div></Card>
}

function MiniRows<T>({ rows, getTitle, getText }: { rows: T[]; getTitle: (row: T) => string; getText: (row: T) => string }) {
  return <div className="recruitment-mini-rows">{rows.map((row, index) => <article key={index}><b>{getTitle(row)}</b><span>{getText(row)}</span></article>)}{!rows.length && <p className="muted">No records.</p>}</div>
}

function PartnerForm({ title, partners, priorities, value, setValue, onSave }: { title: string; partners: { id: number; name: string }[]; priorities: string[]; value: { partnerId: number; priority: string; dueDate: string; expectedProfiles: number; remarks: string }; setValue: (value: { partnerId: number; priority: string; dueDate: string; expectedProfiles: number; remarks: string }) => void; onSave: () => void }) {
  return <Card size="small" title={title}>
    <label><span>Partner</span><Select value={value.partnerId} onChange={partnerId => setValue({ ...value, partnerId })} options={[{ value: 0, label: 'Select' }, ...partners.map(item => ({ value: item.id, label: item.name }))]} /></label>
    <label><span>Priority</span><Select value={value.priority} onChange={priority => setValue({ ...value, priority })} options={(priorities.length ? priorities : ['Normal']).map(item => ({ value: item, label: item }))} /></label>
    <label><span>Due date</span><Input type="date" value={value.dueDate} onChange={event => setValue({ ...value, dueDate: event.target.value })} /></label>
    <label><span>Expected profiles</span><InputNumber value={value.expectedProfiles} onChange={expectedProfiles => setValue({ ...value, expectedProfiles: Number(expectedProfiles || 0) })} /></label>
    <label className="wide"><span>Remarks</span><Input value={value.remarks} onChange={event => setValue({ ...value, remarks: event.target.value })} /></label>
    <Button type="primary" onClick={onSave}>Assign</Button>
  </Card>
}
