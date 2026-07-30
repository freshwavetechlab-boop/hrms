import { useEffect, useMemo, useState } from 'react'
import { Button, Card, Input, InputNumber, Modal, Select, Space, Tabs, message } from 'antd'
import { useLocation, useNavigate } from 'react-router-dom'
import DataTable from '../components/DataTable'
import RecruitmentAtsWorkspace from '../components/RecruitmentAtsWorkspace'
import RecruitmentJobDescriptionManager from '../components/RecruitmentJobDescriptionManager'
import RecruitmentJobPostingManager from '../components/RecruitmentJobPostingManager'
import RecruitmentPipelineBoard from '../components/RecruitmentPipelineBoard'
import RecruitmentRequisitionManager from '../components/RecruitmentRequisitionManager'
import RecruitmentTalentWorkspace from '../components/RecruitmentTalentWorkspace'
import RecruitmentWorkOrderWorkspace from '../components/RecruitmentWorkOrderWorkspace'
import { assignConsultant, assignRecruiter, assignVendor, createReferralCampaign, getRecruitmentDashboard, getRecruitmentMasterOptions, getRecruitmentOpenPositionDetail, getRecruitmentOpenPositions, getRecruitmentOperationsOptions, publishPosition, saveRecruitmentPositionNote, updateRecruitmentPositionStatus } from '../services/recruitmentService'
import type { RecruitmentDashboard, RecruitmentMetric, RecruitmentOpenPosition, RecruitmentOperationsOptions, RecruitmentPositionDetail } from '../types/payroll'

const fallbackPositionStatuses = ['Open', 'Recruiter Assigned', 'Published', 'Candidate Screening', 'Interview In Progress', 'Offer Released', 'Offer Accepted', 'Joining Pending', 'Filled', 'Partially Filled', 'Cancelled', 'Closed', 'On Hold']
const money = (value: number, currency = 'INR') => `${currency} ${Number(value || 0).toLocaleString('en-IN')}`
const dateText = (value?: string | null) => value ? new Date(value).toLocaleDateString('en-GB') : '-'
const dashboard0: RecruitmentDashboard = { drafts: 0, pendingApproval: 0, approved: 0, rejected: 0, returned: 0, withdrawn: 0, openPositions: 0, filledPositions: 0, cancelledPositions: 0, onHoldPositions: 0, remainingPositions: 0, averageApprovalHours: 0, departmentWiseHiring: [], companyWiseHiring: [], priorityWiseHiring: [], upcomingJoiningTargets: [] }

export const recruitmentViews = ['Dashboard', 'Work Orders & SLA', 'Requisitions', 'Open Positions', 'Job Descriptions', 'Job Postings', 'ATS Screening', 'Hiring Pipeline', 'Talent Pool', 'Applications', 'Interviews', 'Offers & Pre-Onboarding'] as const
export type RecruitmentPageView = (typeof recruitmentViews)[number]

const recruitmentViewGroup = (view: RecruitmentPageView) => view === 'Dashboard'
  ? 'Overview'
  : ['Work Orders & SLA', 'Requisitions', 'Open Positions'].includes(view)
    ? 'Demand planning'
    : ['Job Descriptions', 'Job Postings'].includes(view)
      ? 'Content & publishing'
      : ['ATS Screening', 'Hiring Pipeline', 'Talent Pool', 'Applications'].includes(view)
        ? 'Candidate lifecycle'
        : 'Selection & onboarding'

const recruitmentViewDescription: Record<RecruitmentPageView, string> = {
  Dashboard: 'A concise view of hiring demand, approvals, open roles and joining targets.',
  'Work Orders & SLA': 'Register manual client work orders and run the configurable position-level SLA before candidate pipelines begin.',
  Requisitions: 'Raise a hiring request in a few fields, then follow its approval and JD readiness.',
  'Open Positions': 'Track approved vacancies and continue directly to content, publishing and sourcing.',
  'Job Descriptions': 'Prepare governed role content and ATS requirements from an approved request.',
  'Job Postings': 'Publish approved jobs, public links and application forms from one workspace.',
  'ATS Screening': 'Upload one resume or a complete batch against a JD and see the score immediately.',
  'Hiring Pipeline': 'Move candidates through the configured flow with table and pipeline views.',
  'Talent Pool': 'Search reusable candidate profiles and securely manage their resumes.',
  Applications: 'Review every application, ATS result, position and current stage in one table.',
  Interviews: 'Schedule rounds, capture panel feedback and keep selection evidence together.',
  'Offers & Pre-Onboarding': 'Release offers, collect configured documents and convert selected talent.',
}

const recruitmentJourney = [
  ['Work Orders & SLA', 'Order'], ['Requisitions', 'Request'], ['Job Descriptions', 'Describe'], ['Job Postings', 'Publish'],
  ['ATS Screening', 'Screen'], ['Hiring Pipeline', 'Select'], ['Offers & Pre-Onboarding', 'Onboard'],
] as const

export default function RecruitmentPage({ view = 'Dashboard' }: { view?: RecruitmentPageView }) {
  const navigate = useNavigate()
  const location = useLocation()
  const routeQuery = useMemo(() => new URLSearchParams(location.search), [location.search])
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

  const load = async () => {
    const [metrics, positionRows] = await Promise.all([
      getRecruitmentDashboard(),
      getRecruitmentOpenPositions()
    ])
    setDashboard(metrics)
    setPositions(positionRows)
  }

  useEffect(() => {
    void getRecruitmentMasterOptions('Position Status').then(position => {
      if (position.length) setPositionStatusOptions(position)
    })
    void getRecruitmentOperationsOptions().then(setOps)
  }, [])
  useEffect(() => { void load() }, [])
  useEffect(() => { if (detail?.position.status) setPositionStatus(detail.position.status) }, [detail?.position.status])

  const summary = useMemo<Array<[string, string | number]>>(() => [
    ['Pending approvals', dashboard.pendingApproval],
    ['Open vacancies', dashboard.openPositions],
    ['Filled', dashboard.filledPositions],
    ['On hold', dashboard.onHoldPositions],
    ['Cancelled', dashboard.cancelledPositions],
    ['Avg approval hrs', Number(dashboard.averageApprovalHours || 0).toFixed(1)],
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

  return <section className="recruitment-monitor-page recruitment-experience">
    <header className="recruitment-experience-header">
      <div>
        <span className="recruitment-workspace-group">{recruitmentViewGroup(view)}</span>
        <h1>{view}</h1>
        <p>{recruitmentViewDescription[view]}</p>
      </div>
      {['Dashboard', 'Requisitions', 'Open Positions'].includes(view) && <Button type="primary" size="large" onClick={() => navigate('/recruitment/requisitions?new=1')}>New hiring request</Button>}
      {['ATS Screening', 'Talent Pool', 'Applications'].includes(view) && <Button type="primary" size="large" onClick={() => navigate('/recruitment/ats-screening?upload=single')}>Screen resumes</Button>}
    </header>
    <nav className="recruitment-journey-rail" aria-label="Recruitment journey">
      {recruitmentJourney.map(([target, label], index) => <button type="button" key={target} className={view === target ? 'active' : ''} onClick={() => navigate(`/recruitment/${target.toLowerCase().replace(/&/g, 'and').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}`)}><i>{index + 1}</i><span>{label}</span></button>)}
    </nav>
    <div className="recruitment-workspace-surface">
      <div className="recruitment-workspace-content">
        {view === 'Dashboard' && <Dashboard summary={summary} dashboard={dashboard} />}
        {view === 'Work Orders & SLA' && <RecruitmentWorkOrderWorkspace />}
        {view === 'Requisitions' && <RecruitmentRequisitionManager initialOpen={routeQuery.get('new') === '1'} onChanged={() => void load()} onPrepareJobDescription={row => navigate(`/recruitment/job-descriptions?requisitionId=${row.id}&clientId=${row.clientId}`)} />}
        {view === 'Open Positions' && <DataTable rows={positions} exportFileName="recruitment-open-positions" actions={row => <Space size={6}><Button size="small" onClick={() => void openDetail(row)}>View</Button><Button size="small" type="primary" onClick={() => navigate(`/recruitment/job-descriptions?requisitionId=${row.requisitionId}&clientId=${row.clientId}`)}>Prepare JD</Button></Space>} columns={[
          { key: 'positionCode', label: 'Position', render: row => <><b>{row.positionCode}</b><small>{row.rfrNumber}</small></>, value: row => row.positionCode },
          { key: 'positionTitle', label: 'Title' },
          { key: 'clientName', label: 'Client' },
          { key: 'department', label: 'Department' },
          { key: 'vacancies', label: 'Vacancies', value: row => row.remainingPositions, render: row => <><b>{row.remainingPositions} remaining</b><small>{row.filledPositions} filled / {row.approvedPositions} approved</small></> },
          { key: 'targetJoiningDate', label: 'Target', render: row => dateText(row.targetJoiningDate), value: row => row.targetJoiningDate || '' },
          { key: 'salary', label: 'Salary range', value: row => `${row.salaryMin}-${row.salaryMax}`, render: row => `${money(row.salaryMin, row.currency)} - ${money(row.salaryMax, row.currency)}` },
          { key: 'status', label: 'Status' }
        ]} />}
        {view === 'Job Descriptions' && <RecruitmentJobDescriptionManager initialClientId={Number(routeQuery.get('clientId') || 0)} initialRequisitionId={Number(routeQuery.get('requisitionId') || 0)} />}
        {view === 'Job Postings' && <RecruitmentJobPostingManager initialClientId={Number(routeQuery.get('clientId') || 0)} initialPositionId={Number(routeQuery.get('positionId') || 0)} />}
        {view === 'ATS Screening' && <RecruitmentAtsWorkspace initialUploadMode={routeQuery.get('upload') === 'bulk' ? 'bulk' : routeQuery.get('upload') === 'single' ? 'single' : undefined} />}
        {view === 'Hiring Pipeline' && <RecruitmentPipelineBoard />}
        {view === 'Talent Pool' && <RecruitmentTalentWorkspace mode="candidates" />}
        {view === 'Applications' && <RecruitmentTalentWorkspace mode="applications" />}
        {view === 'Interviews' && <RecruitmentTalentWorkspace mode="interviews" />}
        {view === 'Offers & Pre-Onboarding' && <RecruitmentTalentWorkspace mode="offers" />}
      </div>
    </div>
    <Modal open={!!detail} footer={null} width={1120} onCancel={() => setDetail(null)} title={detail?.position?.positionCode ? `${detail.position.positionCode} - ${detail.position.positionTitle}` : 'Open position'}>
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
    </Modal>
  </section>
}

function Dashboard({ summary, dashboard }: { summary: Array<[string, string | number]>; dashboard: RecruitmentDashboard }) {
  return <>
    <div className="travel-advance-summary recruitment-summary">{summary.map(([label, value]) => <article key={String(label)}><span>{label}</span><b>{value}</b></article>)}</div>
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
