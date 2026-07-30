import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button, Card, Checkbox, Drawer, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Statistic, Tag } from 'antd'
import { DeleteOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import EntityAttachmentPanel, { type EntityAttachmentDraft } from './EntityAttachmentPanel'
import RecruitmentAtsScoreDetails from './RecruitmentAtsScoreDetails'
import RecruitmentInterviewEditor from './RecruitmentInterviewEditor'
import RecruitmentResumeIntake, { type RecruitmentResumeIntakeMode } from './RecruitmentResumeIntake'
import SearchSelect, { selectOptions } from './SearchSelect'
import { getEntityAttachments, openAttachmentWithTicket, uploadEntityAttachment } from '../services/attachmentService'
import { getClients } from '../services/payrollService'
import { getRecruitmentMasterOptions, getRecruitmentOpenPositions } from '../services/recruitmentService'
import { getEmployeeManagerUsers, getWorkLocations } from '../services/settingsService'
import { changeApplicationStage, completeCandidateChecklistItem, convertCandidateToEmployee, createApplication, deleteApplication, deleteCandidate, generateOfferLetter, getApplications, getCandidate, getCandidates, getInterviews, getOffers, getTalentDashboard, overrideApplicationScore, saveCandidate, saveCandidateProfileSections, saveOffer, scoreApplication, updateOfferStatus, uploadCandidateResume } from '../services/recruitmentTalentService'
import { useAuthSession } from './AuthGate'
import type { AttachmentFieldConfiguration, Client, ConvertCandidateToEmployeeRequest, EntityAttachment, RecruitmentApplicationScore, RecruitmentCandidate, RecruitmentCandidateApplication, RecruitmentCandidateCertification, RecruitmentCandidateDetail, RecruitmentCandidateEducation, RecruitmentCandidateExperience, RecruitmentCandidateChecklistItem, RecruitmentInterview, RecruitmentOffer, RecruitmentOpenPosition, RecruitmentTalentDashboard, SaveRecruitmentCandidate, WorkLocation, WorkflowApprover } from '../types/payroll'
import { useToast } from './ToastProvider'
import './RecruitmentTalentWorkspace.css'

type Mode = 'candidates' | 'applications' | 'interviews' | 'offers'
const dashboard0: RecruitmentTalentDashboard = { talentProfiles: 0, activeApplications: 0, interviewsScheduled: 0, offersPending: 0, preOnboardingPending: 0, joined: 0 }
const candidate0: SaveRecruitmentCandidate = { id: 0, clientId: 0, firstName: '', lastName: '', email: '', phone: '', currentCompany: '', currentTitle: '', totalExperienceMonths: 0, currentLocation: '', preferredLocationsJson: '[]', noticePeriodDays: 0, currentCtc: 0, expectedCtc: 0, highestQualification: '', sourceType: 'Direct', sourceReferenceId: null, profileStatus: 'Active', consentStatus: 'Pending', consentCapturedAt: null, retentionUntil: null }
const conversion0: ConvertCandidateToEmployeeRequest = { employeeCode: '', dateOfJoining: '', workEmail: '', gender: '', department: '', designation: '', grade: '', workLocationId: 0, reportingManagerId: 0, reportingManagerUserId: null, portalAccess: true, salaryStructureId: '', annualCtc: 0 }
const experience0: RecruitmentCandidateExperience = { id: 0, candidateId: 0, employer: '', jobTitle: '', startDate: null, endDate: null, isCurrent: false, description: '', displayOrder: 100 }
const education0: RecruitmentCandidateEducation = { id: 0, candidateId: 0, qualification: '', institution: '', specialization: '', completionYear: null, score: '', displayOrder: 100 }
const certification0: RecruitmentCandidateCertification = { id: 0, candidateId: 0, certificationName: '', issuer: '', issueDate: null, expiryDate: null, credentialId: '' }
const canApplyCandidate = (row: RecruitmentCandidate) => row.profileStatus === 'Active' && row.consentStatus !== 'Revoked' && (!row.retentionUntil || new Date(row.retentionUntil).getTime() >= Date.now())
const canMoveApplication = (row: RecruitmentCandidateApplication) => !['Rejected', 'Withdrawn', 'Joined'].includes(row.currentStage) && !row.currentStage.startsWith('Offer')

export default function RecruitmentTalentWorkspace({ mode }: { mode: Mode }) {
  const notify = useToast()
  const session = useAuthSession()
  const canDeleteRecruitmentData = Boolean(session?.user.permissions.includes('settings.manage'))
  const [dashboard, setDashboard] = useState(dashboard0)
  const [clients, setClients] = useState<Client[]>([])
  const [workLocations, setWorkLocations] = useState<WorkLocation[]>([])
  const [panelUsers, setPanelUsers] = useState<WorkflowApprover[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [candidates, setCandidates] = useState<RecruitmentCandidate[]>([])
  const [applications, setApplications] = useState<RecruitmentCandidateApplication[]>([])
  const [interviews, setInterviews] = useState<RecruitmentInterview[]>([])
  const [offers, setOffers] = useState<RecruitmentOffer[]>([])
  const [stages, setStages] = useState<string[]>([])
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('')
  const [candidateDraft, setCandidateDraft] = useState<SaveRecruitmentCandidate | null>(null)
  const [detail, setDetail] = useState<RecruitmentCandidateDetail | null>(null)
  const [candidateAttachments, setCandidateAttachments] = useState<EntityAttachment[]>([])
  const [applicationDraft, setApplicationDraft] = useState({ candidateId: 0, positionId: 0, sourceType: 'Direct' })
  const [stageDraft, setStageDraft] = useState<{ row: RecruitmentCandidateApplication; stage: string; reason: string } | null>(null)
  const [interviewDraft, setInterviewDraft] = useState<{ applicationId: number; interview?: RecruitmentInterview } | null>(null)
  const [feedbackInterview, setFeedbackInterview] = useState<RecruitmentInterview | null>(null)
  const [offerDraft, setOfferDraft] = useState<Partial<RecruitmentOffer> & { applicationId: number } | null>(null)
  const [offerStatusDraft, setOfferStatusDraft] = useState<{ row: RecruitmentOffer; status: 'Rejected' | 'Negotiation' | 'Withdrawn'; reason: string } | null>(null)
  const [checklistDraft, setChecklistDraft] = useState<{ item: RecruitmentCandidateChecklistItem; attachmentPublicId: string } | null>(null)
  const [conversionDraft, setConversionDraft] = useState<{ applicationId: number; data: ConvertCandidateToEmployeeRequest } | null>(null)
  const [profileDraft, setProfileDraft] = useState<{ experience: RecruitmentCandidateExperience[]; education: RecruitmentCandidateEducation[]; certifications: RecruitmentCandidateCertification[] } | null>(null)
  const [scoreOverrideDraft, setScoreOverrideDraft] = useState<{ row: RecruitmentApplicationScore; score: number; reason: string } | null>(null)
  const [resumeIntakeMode, setResumeIntakeMode] = useState<RecruitmentResumeIntakeMode | null>(null)

  const load = useCallback(async () => {
    const [metrics, candidateRows, applicationRows, interviewRows, offerRows] = await Promise.all([getTalentDashboard(), getCandidates(query, status), getApplications(), getInterviews(), getOffers()])
    setDashboard(metrics); setCandidates(candidateRows); setApplications(applicationRows); setInterviews(interviewRows); setOffers(offerRows)
  }, [query, status])
  useEffect(() => {
    void Promise.all([getClients(), getWorkLocations(), getEmployeeManagerUsers(), getRecruitmentOpenPositions(), getRecruitmentMasterOptions('Candidate Status')]).then(([clientRows, locationRows, userRows, positionRows, stageRows]) => { setClients(clientRows); setWorkLocations(locationRows); setPanelUsers(userRows); setPositions(positionRows); setStages(stageRows) })
    void load()
  }, [load])

  const openCandidate = async (id: number) => {
    const [candidateDetail, documents] = await Promise.all([getCandidate(id), getEntityAttachments('CANDIDATE', id)])
    setDetail(candidateDetail); setCandidateAttachments(documents)
  }
  const refreshDetail = async () => { if (detail?.candidate.id) await openCandidate(detail.candidate.id) }
  const saveCandidateDraft = async () => {
    if (!candidateDraft) return
    const response = await saveCandidate(candidateDraft)
    if (!response.ok || !response.data) return
    setCandidateDraft(null); await load(); await openCandidate(response.data.id)
  }
  const addApplication = async () => {
    const candidateId = applicationDraft.candidateId || detail?.candidate.id || 0
    const response = await createApplication({ ...applicationDraft, candidateId })
    if (!response.ok) return
    setApplicationDraft({ candidateId: 0, positionId: 0, sourceType: 'Direct' }); await load(); await refreshDetail()
  }
  const saveStage = async () => {
    if (!stageDraft) return
    const response = await changeApplicationStage(stageDraft.row.id, stageDraft.stage, stageDraft.reason)
    if (!response.ok) return
    setStageDraft(null); await load(); await refreshDetail()
  }
  const refreshInterviewData = async () => { await load(); await refreshDetail() }
  const saveOfferDraft = async () => {
    if (!offerDraft) return
    const response = await saveOffer(offerDraft)
    if (!response.ok) return
    setOfferDraft(null); await load(); await refreshDetail()
  }
  const saveOfferStatusDraft = async () => {
    if (!offerStatusDraft?.reason.trim()) return
    const response = await updateOfferStatus(offerStatusDraft.row.id, offerStatusDraft.status, offerStatusDraft.reason)
    if (!response.ok) return
    setOfferStatusDraft(null); await load(); await refreshDetail()
  }
  const loadOfferDocuments = async (applicationId: number) => {
    const application = applications.find(row => row.id === applicationId)
    setCandidateAttachments(application ? await getEntityAttachments('CANDIDATE', application.candidateId) : [])
  }
  const completeChecklist = async () => {
    if (!checklistDraft) return
    const response = await completeCandidateChecklistItem(checklistDraft.item.applicationId, checklistDraft.item.id, checklistDraft.attachmentPublicId || null)
    if (!response.ok) return
    setChecklistDraft(null); await load(); await refreshDetail()
  }
  const startConversion = (application: RecruitmentCandidateApplication) => {
    const position = positions.find(row => row.id === application.positionId)
    const offer = detail?.offers.find(row => row.applicationId === application.id && row.status === 'Accepted')
    setConversionDraft({ applicationId: application.id, data: { ...conversion0, dateOfJoining: offer?.proposedJoiningDate?.slice(0, 10) || '', workEmail: detail?.candidate.email || application.candidateEmail || '', department: position?.department || '', designation: position?.positionTitle || application.positionTitle, annualCtc: offer?.offeredCtc || 0 } })
  }
  const saveConversion = async () => {
    if (!conversionDraft) return
    const response = await convertCandidateToEmployee(conversionDraft.applicationId, conversionDraft.data)
    if (!response.ok || !response.data) return
    notify(`${response.data.employeeCode} employee profile created.`, 'success')
    setConversionDraft(null); await load(); await refreshDetail()
  }
  const patchConversion = (patch: Partial<ConvertCandidateToEmployeeRequest>) => setConversionDraft(current => current ? ({ ...current, data: { ...current.data, ...patch } }) : current)
  const saveProfileSections = async () => {
    if (!profileDraft || !detail?.candidate.id) return
    const response = await saveCandidateProfileSections(detail.candidate.id, profileDraft)
    if (!response.ok || !response.data) return
    setProfileDraft(null); setDetail(response.data); await load()
  }
  const patchExperience = (index: number, patch: Partial<RecruitmentCandidateExperience>) => setProfileDraft(current => current ? ({ ...current, experience: current.experience.map((row, rowIndex) => rowIndex === index ? { ...row, ...patch } : row) }) : current)
  const patchEducation = (index: number, patch: Partial<RecruitmentCandidateEducation>) => setProfileDraft(current => current ? ({ ...current, education: current.education.map((row, rowIndex) => rowIndex === index ? { ...row, ...patch } : row) }) : current)
  const patchCertification = (index: number, patch: Partial<RecruitmentCandidateCertification>) => setProfileDraft(current => current ? ({ ...current, certifications: current.certifications.map((row, rowIndex) => rowIndex === index ? { ...row, ...patch } : row) }) : current)
  const saveScoreOverride = async () => {
    if (!scoreOverrideDraft) return
    const response = await overrideApplicationScore(scoreOverrideDraft.row.id, scoreOverrideDraft.score, scoreOverrideDraft.reason)
    if (!response.ok) return
    setScoreOverrideDraft(null); await load(); await refreshDetail()
  }
  const currentScoreFor = (applicationId: number) => detail?.scores.find(row => row.applicationId === applicationId && row.isCurrent)
  const candidateFromDetail = detail?.candidate
  const candidateOptions = selectOptions(candidates.map(row => ({ value: row.id, label: `${row.candidateCode} - ${row.candidateName}` })), 'Select candidate', 0)
  const applicationOptions = selectOptions(applications.map(row => ({ value: row.id, label: `${row.applicationCode} - ${row.candidateName} / ${row.positionTitle}` })), 'Select application', 0)
  const positionOptions = selectOptions(positions.filter(row => !['Closed', 'Cancelled', 'Filled'].includes(row.status)).map(row => ({ value: row.id, label: `${row.positionCode} - ${row.positionTitle}` })), 'Select position', 0)
  const stageOptions = (stages.length ? stages : ['New', 'Screening', 'Shortlisted', 'Interview Scheduled', 'Interview Completed', 'Selected', 'Rejected', 'Withdrawn', 'On Hold']).filter(value => !['Offer Released', 'Offer Accepted', 'Joined'].includes(value)).map(value => ({ value, label: value }))
  const checklistDocumentOptions = checklistDraft ? candidateAttachments
    .filter(file => !checklistDraft.item.attachmentAttributeId || file.attachmentAttributeId === checklistDraft.item.attachmentAttributeId)
    .filter(file => !checklistDraft.item.requiresVerification || file.verificationStatus === 'Verified')
    .map(file => ({ value: file.publicId, label: `${file.originalFileName} · ${file.verificationStatus}` })) : []
  const offerDocumentOptions = candidateAttachments.filter(file => file.attributeCode === 'OFFER_LETTER').map(file => ({ value: file.publicId, label: `${file.originalFileName} · v${file.versionNumber}` }))

  return <div className="talent-workspace">
    <div className="talent-metrics">{[
      ['Talent profiles', dashboard.talentProfiles], ['Active applications', dashboard.activeApplications], ['Interviews', dashboard.interviewsScheduled], ['Offers pending', dashboard.offersPending], ['Pre-onboarding', dashboard.preOnboardingPending], ['Joined', dashboard.joined]
    ].map(([label, value]) => <Card size="small" key={String(label)}><Statistic title={label} value={value} /></Card>)}</div>

    {mode === 'candidates' && <>
      <div className="talent-toolbar"><Input.Search value={query} onChange={event => setQuery(event.target.value)} onSearch={() => void load()} placeholder="Name, email, phone, code, skill" allowClear /><Select value={status} onChange={setStatus} options={[{ value: '', label: 'All profiles' }, ...['Active', 'Inactive', 'Joined', 'Archived'].map(value => ({ value, label: value }))]} /><div className="talent-toolbar-actions"><Button onClick={() => setResumeIntakeMode('single')}>Upload resume</Button><Button onClick={() => setResumeIntakeMode('bulk')}>Bulk resumes</Button><Button type="primary" onClick={() => setCandidateDraft({ ...candidate0 })}>Add candidate</Button></div></div>
      <DataTable rows={candidates} exportFileName="talent-pool" actions={row => <Space><Button size="small" onClick={() => void openCandidate(row.id)}>360° profile</Button>{canApplyCandidate(row) && <Button size="small" type="primary" onClick={() => setApplicationDraft({ candidateId: row.id, positionId: 0, sourceType: 'Direct' })}>Apply</Button>}{canDeleteRecruitmentData && <Popconfirm title="Delete candidate permanently?" description="Only safe pre-interview/test records can be deleted. Joined, workflow, interview, offer and forwarded records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteCandidate(row.id); if (response.ok) { if (detail?.candidate.id === row.id) setDetail(null); await load() } }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.candidateName}`} /></Popconfirm>}</Space>} columns={[
        { key: 'candidateCode', label: 'Candidate', render: row => <><b>{row.candidateName}</b><small>{row.candidateCode} · {row.email || row.phone}</small></>, value: row => row.candidateName },
        { key: 'currentTitle', label: 'Current role', render: row => <><b>{row.currentTitle || '-'}</b><small>{row.currentCompany || '-'}</small></> },
        { key: 'experience', label: 'Experience', value: row => row.totalExperienceMonths, render: row => `${Math.floor(row.totalExperienceMonths / 12)}y ${row.totalExperienceMonths % 12}m` },
        { key: 'currentLocation', label: 'Location' }, { key: 'sourceType', label: 'Source' }, { key: 'applicationCount', label: 'Applications' },
        { key: 'latestScore', label: 'ATS', render: row => row.latestScore == null ? '-' : <Tag color={row.latestScore >= 60 ? 'green' : 'orange'}>{row.latestScore.toFixed(1)}</Tag> }, { key: 'profileStatus', label: 'Status' }
      ]} />
    </>}
    {mode === 'applications' && <><div className="talent-toolbar right"><Button onClick={() => setResumeIntakeMode('single')}>Upload resume</Button><Button type="primary" onClick={() => setResumeIntakeMode('bulk')}>Bulk resumes</Button></div><DataTable rows={applications} exportFileName="candidate-applications" actions={row => <Space><Button size="small" onClick={() => void openCandidate(row.candidateId)}>Profile</Button>{canMoveApplication(row) && <Button size="small" onClick={() => void scoreApplication(row.id).then(load)}>Score</Button>}{canMoveApplication(row) && <Button size="small" type="primary" onClick={() => setStageDraft({ row, stage: row.currentStage, reason: '' })}>Move stage</Button>}{canDeleteRecruitmentData && <Popconfirm title="Delete application permanently?" description="ATS scores and safe pipeline test data are removed. Interview, offer, workflow and joined records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteApplication(row.id); if (response.ok) await load() }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.applicationCode}`} /></Popconfirm>}</Space>} columns={[
      { key: 'applicationCode', label: 'Application' }, { key: 'candidateName', label: 'Candidate' }, { key: 'positionTitle', label: 'Position', render: row => <><b>{row.positionTitle}</b><small>{row.positionCode}</small></> }, { key: 'clientName', label: 'Client' }, { key: 'currentStage', label: 'Stage' }, { key: 'recruiterName', label: 'Recruiter' }, { key: 'atsScore', label: 'ATS score', render: row => row.atsScore == null ? '-' : <Tag color={row.atsScore >= 60 ? 'green' : 'orange'}>{row.atsScore.toFixed(1)}</Tag> }, { key: 'appliedAt', label: 'Applied', render: row => new Date(row.appliedAt).toLocaleDateString('en-IN') }
    ]} /></>}
    {mode === 'interviews' && <><div className="talent-toolbar right"><Button type="primary" onClick={() => setInterviewDraft({ applicationId: 0 })}>Schedule interview</Button></div><DataTable rows={interviews} exportFileName="recruitment-interviews" actions={row => <Space><Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row })}>Update</Button><Button size="small" type="primary" onClick={() => setFeedbackInterview(row)}>Panel feedback</Button></Space>} columns={[
      { key: 'candidateName', label: 'Candidate' }, { key: 'positionTitle', label: 'Position' }, { key: 'roundCode', label: 'Round' }, { key: 'interviewType', label: 'Type' }, { key: 'scheduledStart', label: 'Schedule', render: row => new Date(row.scheduledStart).toLocaleString('en-IN') }, { key: 'mode', label: 'Mode' }, { key: 'status', label: 'Status' }, { key: 'result', label: 'Result' }, { key: 'overallScore', label: 'Score' }
    ]} /></>}
    {mode === 'offers' && <><div className="talent-toolbar right"><Button type="primary" onClick={() => { setCandidateAttachments([]); setOfferDraft({ applicationId: 0, offeredCtc: 0, currency: 'INR', proposedJoiningDate: new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10), status: 'Draft' }) }}>Create offer</Button></div><DataTable rows={offers} exportFileName="recruitment-offers" actions={row => <Space wrap>{row.status === 'Draft' && <Button size="small" onClick={() => { setOfferDraft({ ...row }); void loadOfferDocuments(row.applicationId) }}>Edit</Button>}{row.status === 'Draft' && <Button size="small" onClick={() => void generateOfferLetter(row.id).then(load)}>{row.offerLetterAttachmentPublicId ? 'Regenerate letter' : 'Generate letter'}</Button>}{row.offerLetterAttachmentPublicId && <Button size="small" onClick={() => void openAttachmentWithTicket(row.offerLetterAttachmentPublicId!, 'Preview')}>View letter</Button>}{['Draft', 'Approved'].includes(row.status) && <Button size="small" type="primary" disabled={!row.offerLetterAttachmentPublicId} title={!row.offerLetterAttachmentPublicId ? 'Generate or link the offer letter first.' : undefined} onClick={() => void updateOfferStatus(row.id, 'Pending Candidate').then(load)}>{row.status === 'Approved' ? 'Release' : 'Submit / release'}</Button>}{['Pending Candidate', 'Released', 'Negotiation'].includes(row.status) && <><Button size="small" type="primary" onClick={() => void updateOfferStatus(row.id, 'Accepted').then(load)}>Accept</Button><Button size="small" danger onClick={() => setOfferStatusDraft({ row, status: 'Rejected', reason: '' })}>Reject</Button></>}{['Pending Candidate', 'Released'].includes(row.status) && <Button size="small" onClick={() => setOfferStatusDraft({ row, status: 'Negotiation', reason: '' })}>Negotiate</Button>}{['Draft', 'Approved', 'Pending Candidate', 'Released', 'Negotiation'].includes(row.status) && <Button size="small" danger onClick={() => setOfferStatusDraft({ row, status: 'Withdrawn', reason: '' })}>Withdraw</Button>}</Space>} columns={[
      { key: 'offerNumber', label: 'Offer' }, { key: 'candidateName', label: 'Candidate' }, { key: 'positionTitle', label: 'Position' }, { key: 'offerTemplateName', label: 'Template', render: row => row.offerTemplateName || '-' }, { key: 'offerLetter', label: 'Letter', render: row => <Tag color={row.offerLetterAttachmentPublicId ? 'green' : 'orange'}>{row.offerLetterAttachmentPublicId ? 'Secured' : 'Missing'}</Tag> }, { key: 'offeredCtc', label: 'CTC', render: row => `${row.currency} ${Number(row.offeredCtc).toLocaleString('en-IN')}` }, { key: 'approvedBudgetAmount', label: 'Approved budget', render: row => row.approvedBudgetAmount > 0 ? `${row.currency} ${Number(row.approvedBudgetAmount).toLocaleString('en-IN')}` : '-' }, { key: 'variancePercent', label: 'Variance', render: row => row.stageOfferConfigurationId ? <Tag color={row.varianceExceeded ? 'red' : 'green'}>{Number(row.variancePercent).toFixed(2)}%</Tag> : '-' }, { key: 'approvalPolicy', label: 'Approval policy', render: row => row.approvalPolicy || (row.stageOfferConfigurationId ? 'Pipeline direct' : 'Global') }, { key: 'proposedJoiningDate', label: 'Joining', render: row => new Date(row.proposedJoiningDate).toLocaleDateString('en-IN') }, { key: 'status', label: 'Status' }
    ]} /></>}

    <Modal open={!!candidateDraft} title={candidateDraft?.id ? 'Edit talent profile' : 'Add talent profile'} onCancel={() => setCandidateDraft(null)} onOk={() => void saveCandidateDraft()} width={850}>{candidateDraft && <Form layout="vertical" className="talent-form-grid">
      <Form.Item label="Client" required><SearchSelect disabled={candidateDraft.id > 0} value={candidateDraft.clientId} onChange={value => setCandidateDraft({ ...candidateDraft, clientId: Number(value) })} options={selectOptions(clients.map(row => ({ value: row.id, label: row.name })), 'Select client', 0)} /></Form.Item>
      <Form.Item label="First name" required><Input value={candidateDraft.firstName} onChange={event => setCandidateDraft({ ...candidateDraft, firstName: event.target.value })} /></Form.Item><Form.Item label="Last name"><Input value={candidateDraft.lastName} onChange={event => setCandidateDraft({ ...candidateDraft, lastName: event.target.value })} /></Form.Item>
      <Form.Item label="Email"><Input value={candidateDraft.email} onChange={event => setCandidateDraft({ ...candidateDraft, email: event.target.value })} /></Form.Item><Form.Item label="Phone"><Input value={candidateDraft.phone} onChange={event => setCandidateDraft({ ...candidateDraft, phone: event.target.value })} /></Form.Item>
      <Form.Item label="Current company"><Input value={candidateDraft.currentCompany} onChange={event => setCandidateDraft({ ...candidateDraft, currentCompany: event.target.value })} /></Form.Item><Form.Item label="Current title"><Input value={candidateDraft.currentTitle} onChange={event => setCandidateDraft({ ...candidateDraft, currentTitle: event.target.value })} /></Form.Item>
      <Form.Item label="Experience (months)"><InputNumber min={0} value={candidateDraft.totalExperienceMonths} onChange={value => setCandidateDraft({ ...candidateDraft, totalExperienceMonths: Number(value || 0) })} /></Form.Item><Form.Item label="Current location"><Input value={candidateDraft.currentLocation} onChange={event => setCandidateDraft({ ...candidateDraft, currentLocation: event.target.value })} /></Form.Item>
      <Form.Item label="Notice period (days)"><InputNumber min={0} value={candidateDraft.noticePeriodDays} onChange={value => setCandidateDraft({ ...candidateDraft, noticePeriodDays: Number(value || 0) })} /></Form.Item><Form.Item label="Qualification"><Input value={candidateDraft.highestQualification} onChange={event => setCandidateDraft({ ...candidateDraft, highestQualification: event.target.value })} /></Form.Item>
      <Form.Item label="Current annual CTC"><InputNumber min={0} value={candidateDraft.currentCtc} onChange={value => setCandidateDraft({ ...candidateDraft, currentCtc: Number(value || 0) })} /></Form.Item><Form.Item label="Expected annual CTC"><InputNumber min={0} value={candidateDraft.expectedCtc} onChange={value => setCandidateDraft({ ...candidateDraft, expectedCtc: Number(value || 0) })} /></Form.Item>
      <Form.Item label="Source"><Select value={candidateDraft.sourceType} onChange={value => setCandidateDraft({ ...candidateDraft, sourceType: value })} options={['Direct', 'Employee Referral', 'Consultant', 'Vendor', 'Job Portal', 'Campus'].map(value => ({ value, label: value }))} /></Form.Item><Form.Item label="Profile status"><Select value={candidateDraft.profileStatus} onChange={value => setCandidateDraft({ ...candidateDraft, profileStatus: value })} options={['Active', 'Inactive', 'Joined', 'Archived'].map(value => ({ value, label: value }))} /></Form.Item>
      <Form.Item label="Consent"><Select value={candidateDraft.consentStatus} onChange={value => setCandidateDraft({ ...candidateDraft, consentStatus: value })} options={['Pending', 'Granted', 'Revoked'].map(value => ({ value, label: value }))} /></Form.Item><Form.Item label="Retention until"><Input type="date" value={candidateDraft.retentionUntil?.slice(0, 10) || ''} onChange={event => setCandidateDraft({ ...candidateDraft, retentionUntil: event.target.value || null })} /></Form.Item>
    </Form>}</Modal>

    <Modal open={applicationDraft.candidateId > 0 || (!!detail && applicationDraft.positionId > 0)} title="Create application" onCancel={() => setApplicationDraft({ candidateId: 0, positionId: 0, sourceType: 'Direct' })} onOk={() => void addApplication()}><Form layout="vertical"><Form.Item label="Candidate"><SearchSelect value={applicationDraft.candidateId || detail?.candidate.id || 0} onChange={value => setApplicationDraft({ ...applicationDraft, candidateId: Number(value) })} options={candidateOptions} /></Form.Item><Form.Item label="Open position"><SearchSelect value={applicationDraft.positionId} onChange={value => setApplicationDraft({ ...applicationDraft, positionId: Number(value) })} options={positionOptions} /></Form.Item><Form.Item label="Source"><Select value={applicationDraft.sourceType} onChange={value => setApplicationDraft({ ...applicationDraft, sourceType: value })} options={['Direct', 'Employee Referral', 'Consultant', 'Vendor', 'Job Portal'].map(value => ({ value, label: value }))} /></Form.Item></Form></Modal>
    <Modal open={!!stageDraft} title="Move candidate stage" onCancel={() => setStageDraft(null)} onOk={() => void saveStage()} okButtonProps={{ disabled: !stageDraft?.reason.trim() || stageDraft.stage === stageDraft.row.currentStage }}>{stageDraft && <Form layout="vertical"><Form.Item label="Stage"><Select value={stageDraft.stage} onChange={stage => setStageDraft({ ...stageDraft, stage })} options={stageOptions} /></Form.Item><Form.Item label="Reason / note" required><Input.TextArea value={stageDraft.reason} onChange={event => setStageDraft({ ...stageDraft, reason: event.target.value })} /></Form.Item></Form>}</Modal>

    <Modal open={!!scoreOverrideDraft} title="Override ATS score" onCancel={() => setScoreOverrideDraft(null)} onOk={() => void saveScoreOverride()} okButtonProps={{ disabled: !scoreOverrideDraft?.reason.trim() }}>{scoreOverrideDraft && <Form layout="vertical"><Form.Item label="Calculated score"><InputNumber value={scoreOverrideDraft.row.totalScore} disabled /></Form.Item><Form.Item label="Override score" required><InputNumber min={0} max={100} precision={2} value={scoreOverrideDraft.score} onChange={score => setScoreOverrideDraft({ ...scoreOverrideDraft, score: Number(score || 0) })} /></Form.Item><Form.Item label="Reason" required><Input.TextArea value={scoreOverrideDraft.reason} onChange={event => setScoreOverrideDraft({ ...scoreOverrideDraft, reason: event.target.value })} placeholder="Document the business reason for this manual override." /></Form.Item></Form>}</Modal>

    <Modal open={!!profileDraft} title="Candidate profile details" onCancel={() => setProfileDraft(null)} onOk={() => void saveProfileSections()} width={1000}>{profileDraft && <div className="candidate-profile-editor">
      <Card size="small" title="Experience" extra={<Button size="small" onClick={() => setProfileDraft({ ...profileDraft, experience: [...profileDraft.experience, { ...experience0, candidateId: detail?.candidate.id || 0 }] })}>Add experience</Button>}>{profileDraft.experience.map((row, index) => <div className="candidate-profile-row" key={`experience-${index}`}><Input placeholder="Employer" value={row.employer} onChange={event => patchExperience(index, { employer: event.target.value })} /><Input placeholder="Job title" value={row.jobTitle} onChange={event => patchExperience(index, { jobTitle: event.target.value })} /><Input type="date" value={row.startDate?.slice(0, 10) || ''} onChange={event => patchExperience(index, { startDate: event.target.value || null })} /><Input type="date" disabled={row.isCurrent} value={row.endDate?.slice(0, 10) || ''} onChange={event => patchExperience(index, { endDate: event.target.value || null })} /><Checkbox checked={row.isCurrent} onChange={event => patchExperience(index, { isCurrent: event.target.checked, endDate: event.target.checked ? null : row.endDate })}>Current</Checkbox><Button danger size="small" onClick={() => setProfileDraft({ ...profileDraft, experience: profileDraft.experience.filter((_, rowIndex) => rowIndex !== index) })}>Remove</Button></div>)}</Card>
      <Card size="small" title="Education" extra={<Button size="small" onClick={() => setProfileDraft({ ...profileDraft, education: [...profileDraft.education, { ...education0, candidateId: detail?.candidate.id || 0 }] })}>Add education</Button>}>{profileDraft.education.map((row, index) => <div className="candidate-profile-row education" key={`education-${index}`}><Input placeholder="Qualification" value={row.qualification} onChange={event => patchEducation(index, { qualification: event.target.value })} /><Input placeholder="Institution" value={row.institution} onChange={event => patchEducation(index, { institution: event.target.value })} /><Input placeholder="Specialization" value={row.specialization} onChange={event => patchEducation(index, { specialization: event.target.value })} /><InputNumber placeholder="Year" min={1950} max={2100} value={row.completionYear} onChange={value => patchEducation(index, { completionYear: value ? Number(value) : null })} /><Input placeholder="Score / grade" value={row.score} onChange={event => patchEducation(index, { score: event.target.value })} /><Button danger size="small" onClick={() => setProfileDraft({ ...profileDraft, education: profileDraft.education.filter((_, rowIndex) => rowIndex !== index) })}>Remove</Button></div>)}</Card>
      <Card size="small" title="Certifications" extra={<Button size="small" onClick={() => setProfileDraft({ ...profileDraft, certifications: [...profileDraft.certifications, { ...certification0, candidateId: detail?.candidate.id || 0 }] })}>Add certification</Button>}>{profileDraft.certifications.map((row, index) => <div className="candidate-profile-row" key={`certification-${index}`}><Input placeholder="Certification" value={row.certificationName} onChange={event => patchCertification(index, { certificationName: event.target.value })} /><Input placeholder="Issuer" value={row.issuer} onChange={event => patchCertification(index, { issuer: event.target.value })} /><Input type="date" value={row.issueDate?.slice(0, 10) || ''} onChange={event => patchCertification(index, { issueDate: event.target.value || null })} /><Input type="date" value={row.expiryDate?.slice(0, 10) || ''} onChange={event => patchCertification(index, { expiryDate: event.target.value || null })} /><Input placeholder="Credential ID" value={row.credentialId} onChange={event => patchCertification(index, { credentialId: event.target.value })} /><Button danger size="small" onClick={() => setProfileDraft({ ...profileDraft, certifications: profileDraft.certifications.filter((_, rowIndex) => rowIndex !== index) })}>Remove</Button></div>)}</Card>
    </div>}</Modal>

    <Modal open={!!checklistDraft} title="Complete pre-onboarding item" onCancel={() => setChecklistDraft(null)} onOk={() => void completeChecklist()} okButtonProps={{ disabled: !!checklistDraft?.item.attachmentAttributeId && !checklistDraft.attachmentPublicId }}>{checklistDraft && <Form layout="vertical">
      <Form.Item label="Checklist item"><Input value={checklistDraft.item.checklistName} disabled /></Form.Item>
      {checklistDraft.item.attachmentAttributeId ? <Form.Item label="Candidate document" required help={checklistDraft.item.requiresVerification ? 'Only a verified document of the configured type can complete this item.' : 'The document remains managed by the global attachment system.'}><Select value={checklistDraft.attachmentPublicId || undefined} onChange={attachmentPublicId => setChecklistDraft({ ...checklistDraft, attachmentPublicId })} placeholder="Select uploaded document" options={checklistDocumentOptions} /></Form.Item> : <p>No document is required for this checklist item.</p>}
      {!!checklistDraft.item.attachmentAttributeId && !checklistDocumentOptions.length && <p>Upload the configured document in Candidate documents first.</p>}
    </Form>}</Modal>

    <Modal open={!!conversionDraft} title="Create employee from selected candidate" onCancel={() => setConversionDraft(null)} onOk={() => void saveConversion()} width={820}>{conversionDraft && <Form layout="vertical" className="talent-form-grid">
      <Form.Item label="Employee code" required><Input value={conversionDraft.data.employeeCode} onChange={event => patchConversion({ employeeCode: event.target.value })} /></Form.Item>
      <Form.Item label="Date of joining" required><Input type="date" value={conversionDraft.data.dateOfJoining} onChange={event => patchConversion({ dateOfJoining: event.target.value })} /></Form.Item>
      <Form.Item label="Work email"><Input value={conversionDraft.data.workEmail} onChange={event => patchConversion({ workEmail: event.target.value })} /></Form.Item>
      <Form.Item label="Gender"><Select value={conversionDraft.data.gender || undefined} onChange={gender => patchConversion({ gender })} options={['Male', 'Female', 'Other'].map(value => ({ value, label: value }))} allowClear /></Form.Item>
      <Form.Item label="Department"><Input value={conversionDraft.data.department} onChange={event => patchConversion({ department: event.target.value })} /></Form.Item>
      <Form.Item label="Designation"><Input value={conversionDraft.data.designation} onChange={event => patchConversion({ designation: event.target.value })} /></Form.Item>
      <Form.Item label="Grade"><Input value={conversionDraft.data.grade} onChange={event => patchConversion({ grade: event.target.value })} /></Form.Item>
      <Form.Item label="Work location"><SearchSelect value={conversionDraft.data.workLocationId} onChange={value => patchConversion({ workLocationId: Number(value) })} options={selectOptions(workLocations.filter(row => row.clientId === candidateFromDetail?.clientId && row.isActive).map(row => ({ value: row.id, label: `${row.name} - ${row.city}` })), 'Select work location', 0)} /></Form.Item>
      <Form.Item label="Annual CTC"><InputNumber min={0} value={conversionDraft.data.annualCtc} onChange={value => patchConversion({ annualCtc: Number(value || 0) })} /></Form.Item>
      <Form.Item label="Portal access"><Checkbox checked={conversionDraft.data.portalAccess} onChange={event => patchConversion({ portalAccess: event.target.checked })}>Enable the employee portal-access flag for onboarding</Checkbox></Form.Item>
    </Form>}</Modal>

    <Drawer open={!!detail} onClose={() => { setDetail(null); setCandidateAttachments([]) }} width="min(1040px, 96vw)" title={candidateFromDetail ? `${candidateFromDetail.candidateCode} - ${candidateFromDetail.candidateName}` : 'Talent profile'}>{candidateFromDetail && <div className="candidate-360">
      <div className="candidate-hero"><div><h2>{candidateFromDetail.candidateName}</h2><p>{candidateFromDetail.currentTitle || 'Candidate'} · {candidateFromDetail.currentCompany || 'Independent'}</p></div><Space wrap><Tag>{candidateFromDetail.profileStatus}</Tag><Tag color={candidateFromDetail.consentStatus === 'Granted' ? 'green' : candidateFromDetail.consentStatus === 'Revoked' ? 'red' : 'orange'}>{candidateFromDetail.consentStatus} consent</Tag>{candidateFromDetail.employeeCode && <Tag color="green">Employee {candidateFromDetail.employeeCode}</Tag>}<Button onClick={() => setCandidateDraft({ ...candidateFromDetail })}>Edit summary</Button><Button onClick={() => setProfileDraft({ experience: detail.experience.map(row => ({ ...row })), education: detail.education.map(row => ({ ...row })), certifications: detail.certifications.map(row => ({ ...row })) })}>Profile details</Button>{canApplyCandidate(candidateFromDetail) && <Button type="primary" onClick={() => setApplicationDraft({ candidateId: candidateFromDetail.id, positionId: 0, sourceType: 'Direct' })}>Add application</Button>}</Space></div>
      <div className="candidate-facts">{[['Email', candidateFromDetail.email || '-'], ['Phone', candidateFromDetail.phone || '-'], ['Location', candidateFromDetail.currentLocation || '-'], ['Experience', `${Math.floor(candidateFromDetail.totalExperienceMonths / 12)}y ${candidateFromDetail.totalExperienceMonths % 12}m`], ['Notice', `${candidateFromDetail.noticePeriodDays} days`], ['Qualification', candidateFromDetail.highestQualification || '-']].map(([label, value]) => <article key={label}><span>{label}</span><b>{value}</b></article>)}</div>
      <div className="candidate-profile-grid"><Card size="small" title="Experience">{detail.experience.map(row => <article key={row.id}><b>{row.jobTitle || '-'}</b><span>{row.employer || '-'} · {row.startDate?.slice(0, 7) || '-'} to {row.isCurrent ? 'Present' : row.endDate?.slice(0, 7) || '-'}</span></article>)}{!detail.experience.length && <p>No experience details.</p>}</Card><Card size="small" title="Education">{detail.education.map(row => <article key={row.id}><b>{row.qualification || '-'}</b><span>{row.institution || '-'} · {row.completionYear || '-'}</span></article>)}{!detail.education.length && <p>No education details.</p>}</Card><Card size="small" title="Certifications">{detail.certifications.map(row => <article key={row.id}><b>{row.certificationName}</b><span>{row.issuer || '-'} · {row.credentialId || 'No credential ID'}</span></article>)}{!detail.certifications.length && <p>No certifications.</p>}</Card></div>
      <Card size="small" title="Applications and ATS"><DataTable rows={detail.applications} actions={row => { const score = currentScoreFor(row.id); return <Space wrap>{canMoveApplication(row) && <Button size="small" onClick={() => setStageDraft({ row, stage: row.currentStage, reason: '' })}>Move stage</Button>}{score && canMoveApplication(row) && <Button size="small" onClick={() => setScoreOverrideDraft({ row: score, score: score.overrideScore ?? score.totalScore, reason: score.overrideReason || '' })}>Override score</Button>}{row.currentStage === 'Offer Accepted' && !row.joinedEmployeeId && <Button size="small" type="primary" onClick={() => startConversion(row)}>Create employee</Button>}</Space> }} columns={[{ key: 'applicationCode', label: 'Application' }, { key: 'positionTitle', label: 'Position' }, { key: 'currentStage', label: 'Stage' }, { key: 'atsScore', label: 'ATS', render: row => row.atsScore == null ? '-' : row.atsScore.toFixed(1) }]} /></Card>
      <RecruitmentAtsScoreDetails scores={detail.scores} applications={detail.applications} onOverride={row => setScoreOverrideDraft({ row, score: row.overrideScore ?? row.totalScore, reason: row.overrideReason || '' })} />
      <Card size="small" title="Interview history"><DataTable rows={detail.interviews} actions={row => <Space><Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row })}>Update</Button><Button size="small" type="primary" onClick={() => setFeedbackInterview(row)}>Panel feedback</Button></Space>} columns={[{ key: 'roundCode', label: 'Round' }, { key: 'interviewType', label: 'Type' }, { key: 'scheduledStart', label: 'Schedule', render: row => new Date(row.scheduledStart).toLocaleString('en-IN') }, { key: 'status', label: 'Status' }, { key: 'result', label: 'Result' }, { key: 'overallScore', label: 'Panel average' }]} /></Card>
      <Card size="small" title="Offer history"><DataTable rows={detail.offers} columns={[{ key: 'offerNumber', label: 'Offer' }, { key: 'positionTitle', label: 'Position' }, { key: 'offeredCtc', label: 'CTC', render: row => `${row.currency} ${Number(row.offeredCtc).toLocaleString('en-IN')}` }, { key: 'proposedJoiningDate', label: 'Joining', render: row => new Date(row.proposedJoiningDate).toLocaleDateString('en-IN') }, { key: 'status', label: 'Status' }]} /></Card>
      <Card size="small" title="Resume parsing"><DataTable rows={detail.resumes} columns={[{ key: 'originalFileName', label: 'Resume' }, { key: 'versionNumber', label: 'Version' }, { key: 'isPrimary', label: 'Primary', render: row => row.isPrimary ? 'Yes' : 'No' }, { key: 'parsingStatus', label: 'Parsing status', render: row => <Tag color={row.parsingStatus === 'Parsed' ? 'green' : row.parsingStatus === 'Failed' ? 'red' : 'orange'}>{row.parsingStatus}</Tag> }, { key: 'parserName', label: 'Parser' }, { key: 'parserVersion', label: 'Parser version' }, { key: 'detectedExperience', label: 'Detected experience', render: row => row.parseFacts?.totalExperienceMonths == null ? '-' : `${Math.floor(row.parseFacts.totalExperienceMonths / 12)}y ${row.parseFacts.totalExperienceMonths % 12}m` }, { key: 'parsingError', label: 'Review note', render: row => row.parsingError || '-' }, { key: 'createdAt', label: 'Uploaded', render: row => new Date(row.createdAt).toLocaleString('en-IN') }]} /></Card>
      <EntityAttachmentPanel entityType="CANDIDATE" entityId={candidateFromDetail.id} clientId={candidateFromDetail.clientId} moduleCode="RECRUITMENT" formCodes={['CANDIDATE_APPLICATION', 'EMPLOYEE_REFERRAL', 'PRE_ONBOARDING']} title="Candidate documents" uploadOverride={(configuration: AttachmentFieldConfiguration, draft: EntityAttachmentDraft, onProgress) => !draft.file ? Promise.resolve({ ok: false, error: 'Select a file.' }) : configuration.attributeCode === 'RESUME' ? uploadCandidateResume(candidateFromDetail.id, configuration.id, draft.file, draft, onProgress) : uploadEntityAttachment(configuration.id, 'CANDIDATE', candidateFromDetail.id, draft.file, draft, onProgress)} onChanged={() => void refreshDetail()} />
      <Card size="small" title="Skills extracted from resume"><div className="candidate-skills">{detail.skills.map(skill => <Tag key={`${skill.id}-${skill.skillName}`}>{skill.skillName} · {Math.round(skill.confidence * 100)}%</Tag>)}{!detail.skills.length && <p>No skills extracted yet. Upload a supported resume.</p>}</div></Card>
      <Card size="small" title="Pre-onboarding checklist"><div className="candidate-checklist">{detail.checklist.map(item => <article key={item.id}><div><b>{item.checklistName}</b><span>{item.stage} · {item.mandatory ? 'Mandatory' : 'Optional'}{item.dueDate ? ` · Due ${new Date(item.dueDate).toLocaleDateString('en-IN')}` : ''}{item.requiresVerification ? ' · Verified document required' : ''}</span></div><Space><Tag color={item.status === 'Completed' ? 'green' : 'orange'}>{item.status}</Tag>{item.status !== 'Completed' && <Button size="small" type="primary" onClick={() => setChecklistDraft({ item, attachmentPublicId: '' })}>Complete</Button>}</Space></article>)}{!detail.checklist.length && <p>No checklist snapshot for this candidate.</p>}</div></Card>
      <Card size="small" title="Person activity timeline"><div className="candidate-activity">{detail.activity.map(item => <article key={`${item.moduleCode}-${item.id}`}><i /><div><b>{item.eventTitle}</b><p>{item.eventSummary}</p><small>{new Date(item.occurredAt).toLocaleString('en-IN')} · {item.actorName || 'System'} · {item.moduleCode}</small></div></article>)}{!detail.activity.length && <p>No activity recorded.</p>}</div></Card>
    </div>}</Drawer>

    <RecruitmentResumeIntake
      open={resumeIntakeMode !== null}
      initialMode={resumeIntakeMode || 'single'}
      onClose={() => setResumeIntakeMode(null)}
      onCompleted={async () => { await load() }}
    />

    {interviewDraft && <RecruitmentInterviewEditor mode="schedule" open applications={applications} panelUsers={panelUsers} interview={interviewDraft.interview} initialApplicationId={interviewDraft.applicationId} onClose={() => setInterviewDraft(null)} onSaved={refreshInterviewData} />}
    {feedbackInterview && <RecruitmentInterviewEditor mode="feedback" open applications={applications} panelUsers={panelUsers} interview={feedbackInterview} onClose={() => setFeedbackInterview(null)} onSaved={refreshInterviewData} />}
    <Modal open={!!offerDraft} title="Offer" onCancel={() => { setOfferDraft(null); if (!detail) setCandidateAttachments([]) }} onOk={() => void saveOfferDraft()}>{offerDraft && <Form layout="vertical"><Form.Item label="Application"><SearchSelect disabled={Boolean(offerDraft.id)} value={offerDraft.applicationId} onChange={value => { const applicationId = Number(value); setOfferDraft({ ...offerDraft, applicationId }); void loadOfferDocuments(applicationId) }} options={applicationOptions} /></Form.Item><Form.Item label="Offered annual CTC"><InputNumber min={0} value={offerDraft.offeredCtc} onChange={value => setOfferDraft({ ...offerDraft, offeredCtc: Number(value || 0) })} /></Form.Item><Form.Item label="Currency"><Input value={offerDraft.currency} onChange={event => setOfferDraft({ ...offerDraft, currency: event.target.value.toUpperCase() })} /></Form.Item><Form.Item label="Proposed joining"><Input type="date" value={String(offerDraft.proposedJoiningDate || '').slice(0, 10)} onChange={event => setOfferDraft({ ...offerDraft, proposedJoiningDate: event.target.value })} /></Form.Item><Form.Item label="Expiry"><Input type="date" value={String(offerDraft.expiryDate || '').slice(0, 10)} onChange={event => setOfferDraft({ ...offerDraft, expiryDate: event.target.value })} /></Form.Item><Form.Item label="Global offer-letter document" extra="After saving this draft, use Generate letter in the Offers table. A manually uploaded current Offer Letter can also be linked here."><Select allowClear value={offerDraft.offerLetterAttachmentPublicId || undefined} onChange={offerLetterAttachmentPublicId => setOfferDraft({ ...offerDraft, offerLetterAttachmentPublicId })} options={offerDocumentOptions} placeholder="Generated or uploaded offer letter" /></Form.Item><Form.Item label="Remarks"><Input.TextArea value={offerDraft.remarks} onChange={event => setOfferDraft({ ...offerDraft, remarks: event.target.value })} /></Form.Item></Form>}</Modal>
    <Modal open={!!offerStatusDraft} title={offerStatusDraft ? `${offerStatusDraft.status} offer ${offerStatusDraft.row.offerNumber}` : 'Offer status'} onCancel={() => setOfferStatusDraft(null)} onOk={() => void saveOfferStatusDraft()} okButtonProps={{ disabled: !offerStatusDraft?.reason.trim() }}>{offerStatusDraft && <Form layout="vertical"><Form.Item label="Reason" required><Input.TextArea rows={4} value={offerStatusDraft.reason} onChange={event => setOfferStatusDraft({ ...offerStatusDraft, reason: event.target.value })} placeholder="This reason is retained in the candidate timeline and audit trail." /></Form.Item></Form>}</Modal>
  </div>
}
