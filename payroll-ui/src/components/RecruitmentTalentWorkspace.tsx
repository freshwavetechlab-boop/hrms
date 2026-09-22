import CandidateEditorDrawer from './RecruitmentCandidateEditorDrawer'
import CandidateProfileFields from './RecruitmentCandidateProfileFields'
import CandidateTrackingPreview from './CandidateTrackingPreview'
import { interviewReady } from '../services/recruitmentInterviewEligibility'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { Alert, Avatar, Badge, Button, Card, Checkbox, Drawer, Dropdown, Empty, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Table, Tag, Tooltip } from 'antd'
import { BranchesOutlined, CalendarOutlined, DeleteOutlined, EditOutlined, FileSearchOutlined, MailOutlined, PhoneOutlined, PlusOutlined, RobotOutlined, SearchOutlined, UploadOutlined, UserAddOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import OfferLetterPreview from './OfferLetterPreview'
import FinalOfferActions from './FinalOfferActions'
import FinalOfferSettings from './FinalOfferSettings'
import EntityAttachmentPanel, { type EntityAttachmentDraft } from './EntityAttachmentPanel'
import RecruitmentAtsScoreDetails from './RecruitmentAtsScoreDetails'
import RecruitmentInterviewEditor from './RecruitmentInterviewEditor'
import { getInternalInterviewCapabilities } from '../services/internalInterviewService'
import InternalInterviewDashboard from './InternalInterviewDashboard'
import RecruitmentBatchInterviewEditor from './RecruitmentBatchInterviewEditor'
import RecruitmentEditorDrawer from './RecruitmentEditorDrawer'
import RecruitmentGlobalTalentPool from './RecruitmentGlobalTalentPool'
import RecruitmentInternalCandidateForm from './RecruitmentInternalCandidateForm'
import RecruitmentResumeIntake, { type RecruitmentResumeIntakeMode } from './RecruitmentResumeIntake'
import SearchSelect, { selectOptions } from './SearchSelect'
import { getEntityAttachments, openAttachmentWithTicket, uploadEntityAttachment } from '../services/attachmentService'
import { getClients } from '../services/payrollService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import { getEmployeeManagerUsers, getWorkLocations } from '../services/settingsService'
import { completeCandidateChecklistItem, convertCandidateToEmployee, createApplication, deleteApplication, deleteCandidate, deleteInterview, deleteOffer, generateOfferLetter, getApplications, getCandidate, getCandidates, getInterviews, getOffers, moveApplicationCandidateToGlobalTalentPool, moveApplicationsToGlobalTalentPool, overrideApplicationScore, saveCandidate, saveCandidateProfileSections, saveOffer, scoreApplication, sendInterviewInvite, updateOfferStatus, uploadCandidateResume } from '../services/recruitmentTalentService'
import { useAuthSession } from './AuthGate'
import type { AttachmentFieldConfiguration, Client, ConvertCandidateToEmployeeRequest, EntityAttachment, RecruitmentApplicationScore, RecruitmentCandidate, RecruitmentCandidateApplication, RecruitmentCandidateCertification, RecruitmentCandidateDetail, RecruitmentCandidateEducation, RecruitmentCandidateExperience, RecruitmentCandidateChecklistItem, RecruitmentInterview, RecruitmentOffer, RecruitmentOpenPosition, SaveRecruitmentCandidate, WorkLocation, WorkflowApprover } from '../types/payroll'
import { useToast } from './ToastProvider'
import { formatApiDateTime } from '../utils/apiDateTime'
import './RecruitmentTalentWorkspace.css'

type Mode = 'candidates' | 'applications' | 'interviewQueue' | 'interviews' | 'offers'
const candidate0: SaveRecruitmentCandidate = { id: 0, clientId: 0, firstName: '', lastName: '', email: '', phone: '', currentCompany: '', currentTitle: '', totalExperienceMonths: 0, currentLocation: '', preferredLocationsJson: '[]', noticePeriodDays: 0, currentCtc: 0, expectedCtc: 0, highestQualification: '', sourceType: 'Direct', sourceReferenceId: null, profileStatus: 'Active', consentStatus: 'Pending', consentCapturedAt: null, retentionUntil: null }
const conversion0: ConvertCandidateToEmployeeRequest = { employeeCode: '', dateOfJoining: '', workEmail: '', gender: '', department: '', designation: '', grade: '', workLocationId: 0, reportingManagerId: 0, reportingManagerUserId: null, portalAccess: true, salaryStructureId: '', annualCtc: 0 }
const canApplyCandidate = (row: RecruitmentCandidate) => row.profileStatus === 'Active' && row.consentStatus !== 'Revoked' && (!row.retentionUntil || new Date(row.retentionUntil).getTime() >= Date.now())
const canMoveApplication = (row: RecruitmentCandidateApplication) => !['Rejected', 'Withdrawn', 'Joined'].includes(row.currentStage) && !row.currentStage.startsWith('Offer')
type ApplicationQuickFilter = 'all' | 'new' | 'needs-score' | 'scored' | 'pipeline' | 'rejected'
type InterviewQuickFilter = 'all' | 'scheduled' | 'completed' | 'attention'
const rejectedApplication = (row: RecruitmentCandidateApplication) => /reject|withdraw/i.test(`${row.currentStage} ${row.currentStatus}`)
const newApplication = (row: RecruitmentCandidateApplication) => /new|application|intake/i.test(row.currentStage || '')
const publicJobApplication = (row: RecruitmentCandidateApplication) => /public\s*job|career/i.test(row.sourceType || '')
const canOverrideAts = (row: RecruitmentCandidateApplication) => row.atsScore != null
  && !row.atsOverridden
  && (row.atsScore < (row.atsShortlistThreshold || 60) || /ineligible|needsreview/i.test(row.scoreStatus || ''))
const initials = (name: string) => name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase()).join('') || 'C'
const experienceLabel = (months: number) => months > 0 ? `${Math.floor(months / 12)}y ${months % 12}m` : 'Not specified'

function NewInterviewMenu({ onSelect }: { onSelect: (startNow: boolean) => void }) {
  return <Dropdown menu={{ items: [
    { key: 'later', label: 'Create a meeting for later', icon: <CalendarOutlined /> },
    { key: 'now', label: 'Start an instant meeting', icon: <CalendarOutlined /> },
  ], onClick: ({ key }) => onSelect(key === 'now') }}>
    <Button type="primary" icon={<CalendarOutlined />}>New interview</Button>
  </Dropdown>
}

function AddCandidateMenu({ onSelect }: { onSelect: (target: 'talentPool' | 'job') => void }) {
  return <Dropdown trigger={['hover']} menu={{ items: [
    { key: 'talentPool', label: 'Add to Talent Pool', icon: <UserAddOutlined /> },
    { key: 'job', label: 'Add candidate to job', icon: <UploadOutlined /> },
  ], onClick: ({ key }) => onSelect(key as 'talentPool' | 'job') }}>
    <Button icon={<UserAddOutlined />}>Add candidate</Button>
  </Dropdown>
}

export default function RecruitmentTalentWorkspace({ mode, initialClientId = 0, negotiationMode = false }: { mode: Mode; initialClientId?: number; negotiationMode?: boolean }) {
  const navigate = useNavigate()
  const location = useLocation()
  const notify = useToast()
  const session = useAuthSession()
  const canDeleteRecruitmentData = Boolean(session?.user.permissions.includes('settings.manage'))
  const canScheduleInterviews = !!session?.user.permissions.some(permission => ['recruitment.interview.schedule', 'recruitment.manage', 'settings.manage'].includes(permission))
  const [clients, setClients] = useState<Client[]>([])
  const [workLocations, setWorkLocations] = useState<WorkLocation[]>([])
  const [panelUsers, setPanelUsers] = useState<WorkflowApprover[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [candidates, setCandidates] = useState<RecruitmentCandidate[]>([])
  const [applications, setApplications] = useState<RecruitmentCandidateApplication[]>([])
  const [interviews, setInterviews] = useState<RecruitmentInterview[]>([])
  const [internalInterviewsEnabled, setInternalInterviewsEnabled] = useState(false)
  useEffect(() => { if (mode === 'interviews') void getInternalInterviewCapabilities().then(value => setInternalInterviewsEnabled(value.enabled)) }, [mode])
  const [offers, setOffers] = useState<RecruitmentOffer[]>([])
  const [candidateDraft, setCandidateDraft] = useState<SaveRecruitmentCandidate | null>(null)
  const [detail, setDetail] = useState<RecruitmentCandidateDetail | null>(null)
  const [candidateAttachments, setCandidateAttachments] = useState<EntityAttachment[]>([])
  const [applicationDraft, setApplicationDraft] = useState({ candidateId: 0, positionId: 0, sourceType: 'Direct' })
  const [interviewDraft, setInterviewDraft] = useState<{ applicationId: number; interview?: RecruitmentInterview; standalone?: boolean; startNow?: boolean; initialDecision?: 'Selected' | 'Rejected' } | null>(null)
  const [feedbackInterview, setFeedbackInterview] = useState<RecruitmentInterview | null>(null)
  useEffect(() => {
    if (mode !== 'interviews') return
    const query = new URLSearchParams(location.search)
    const feedbackId = Number(query.get('feedbackInterviewId') || 0)
    const decisionId = Number(query.get('decisionInterviewId') || 0)
    const interview = interviews.find(row => row.id === (feedbackId || decisionId))
    if (!interview) return // Only the existing server-authorized interview list can open the drawer.
    if (feedbackId) setFeedbackInterview(interview)
    else if (decisionId) setInterviewDraft({ applicationId: interview.applicationId, interview })
    query.delete('feedbackInterviewId'); query.delete('decisionInterviewId')
    navigate(`${location.pathname}${query.size ? `?${query}` : ''}`, { replace: true })
  }, [mode, interviews, location.pathname, location.search, navigate])
  const [offerDraft, setOfferDraft] = useState<Partial<RecruitmentOffer> & { applicationId: number } | null>(null)
  const [offerStatusDraft, setOfferStatusDraft] = useState<{ row: RecruitmentOffer; status: 'Rejected' | 'Negotiation' | 'Withdrawn'; reason: string } | null>(null)
  const [checklistDraft, setChecklistDraft] = useState<{ item: RecruitmentCandidateChecklistItem; attachmentPublicId: string } | null>(null)
  const [conversionDraft, setConversionDraft] = useState<{ applicationId: number; data: ConvertCandidateToEmployeeRequest } | null>(null)
  const [profileDraft, setProfileDraft] = useState<{ experience: RecruitmentCandidateExperience[]; education: RecruitmentCandidateEducation[]; certifications: RecruitmentCandidateCertification[] } | null>(null)
  const [scoreOverrideDraft, setScoreOverrideDraft] = useState<{ row: RecruitmentApplicationScore; score: number; reason: string } | null>(null)
  const [resumeIntakeMode, setResumeIntakeMode] = useState<RecruitmentResumeIntakeMode | null>(null)
  const [resumeIntakeTarget, setResumeIntakeTarget] = useState<RecruitmentCandidateApplication | null>(null)
  const [resumeIntakeTalentPoolOnly, setResumeIntakeTalentPoolOnly] = useState(false)
  const [candidateFormOpen, setCandidateFormOpen] = useState(() => new URLSearchParams(location.search).get('add') === '1')
  const [applicationQuickFilter, setApplicationQuickFilter] = useState<ApplicationQuickFilter>('all')
  const [applicationSearch, setApplicationSearch] = useState('')
  const [applicationPositionId, setApplicationPositionId] = useState(0)
  const [applicationSource, setApplicationSource] = useState('')
  const [applicationStage, setApplicationStage] = useState('')
  const [applicationScoreBand, setApplicationScoreBand] = useState('')
  const [applicationSort, setApplicationSort] = useState('recent')
  const [interviewQuickFilter, setInterviewQuickFilter] = useState<InterviewQuickFilter>('all')
  const [batchInterviewOpen, setBatchInterviewOpen] = useState(false)
  const [batchInterviewApplicationIds, setBatchInterviewApplicationIds] = useState<number[]>([])
  const [talentPoolDraft, setTalentPoolDraft] = useState<RecruitmentCandidateApplication | null>(null)
  const [selectedRejectedApplicationIds, setSelectedRejectedApplicationIds] = useState<number[]>([])
  const routeQuery = new URLSearchParams(location.search)
  const requestedPostingId = Number(routeQuery.get('jobPostingId') || 0)
  const requestedPositionId = Number(routeQuery.get('positionId') || 0)
  const requestedUploadMode = routeQuery.get('upload')
  const closeCandidateForm = () => {
    setCandidateFormOpen(false)
    const params = new URLSearchParams(location.search)
    params.delete('add')
    params.delete('jobPostingId')
    const query = params.toString()
    navigate(`${location.pathname}${query ? `?${query}` : ''}`, { replace: true })
  }
  const closeResumeIntake = () => {
    setResumeIntakeMode(null)
    setResumeIntakeTarget(null)
    setResumeIntakeTalentPoolOnly(false)
    const params = new URLSearchParams(location.search)
    if (!params.has('upload')) return
    params.delete('upload')
    const query = params.toString()
    navigate(`${location.pathname}${query ? `?${query}` : ''}`, { replace: true })
  }

  const load = useCallback(async () => {
    if (mode === 'interviews') {
      const interviewRows = await getInterviews()
      setInterviews(initialClientId ? interviewRows.filter(row => row.clientId === initialClientId) : interviewRows)
      // Panel users must not depend on (or request) HR-only candidates/offers endpoints.
      if (canScheduleInterviews) setApplications(await getApplications())
      return
    }
    const [candidateRows, allApplicationRows, allInterviewRows, allOfferRows] = await Promise.all([getCandidates('', '', initialClientId || undefined), getApplications(), getInterviews(), getOffers()])
    const applicationRows = initialClientId ? allApplicationRows.filter(row => row.clientId === initialClientId) : allApplicationRows
    const applicationIds = new Set(applicationRows.map(row => row.id))
    const interviewRows = initialClientId ? allInterviewRows.filter(row => applicationIds.has(row.applicationId)) : allInterviewRows
    const offerRows = initialClientId ? allOfferRows.filter(row => row.clientId === initialClientId) : allOfferRows
    setCandidates(candidateRows); setApplications(applicationRows); setInterviews(interviewRows); setOffers(offerRows)
  }, [initialClientId, mode, canScheduleInterviews])
  useEffect(() => {
    if (mode === 'interviews' && !canScheduleInterviews) { void load(); return }
    void Promise.all([getClients(), getWorkLocations(), getEmployeeManagerUsers(), getRecruitmentOpenPositions(initialClientId)]).then(([clientRows, locationRows, userRows, positionRows]) => { setClients(clientRows); setWorkLocations(locationRows); setPanelUsers(userRows); setPositions(positionRows) })
    void load()
  }, [initialClientId, load, mode, canScheduleInterviews])
  useEffect(() => {
    if (String(mode) !== 'applications' || !['single', 'bulk'].includes(requestedUploadMode || '')) return
    setResumeIntakeMode(requestedUploadMode as RecruitmentResumeIntakeMode)
  }, [mode, requestedUploadMode])

  const openCandidate = async (id: number) => {
    const [candidateDetail, documents] = await Promise.all([getCandidate(id), getEntityAttachments('CANDIDATE', id)])
    setDetail(candidateDetail); setCandidateAttachments(documents)
  }
  const viewApplicationResume = async (applicationId: number) => {
    const application = applications.find(row => row.id === applicationId) ?? detail?.applications.find(row => row.id === applicationId)
    if (!application) return void notify('Candidate application could not be found.', 'warning')
    if (application.resumeId && application.resumeAvailable === false) return void notify('Resume file was deleted from storage. Use Re-upload to attach its replacement.', 'warning')
    const candidateDetail = detail?.candidate.id === application.candidateId ? detail : await getCandidate(application.candidateId)
    const resumes = candidateDetail?.resumes ?? []
    const resume = resumes.find(row => row.id === application.resumeId)
      ?? resumes.find(row => row.isPrimary)
      ?? [...resumes].sort((left, right) => right.id - left.id)[0]
    if (!resume?.attachmentPublicId) return void notify('No resume is attached to this candidate.', 'warning')
    if (resume.attachmentAvailable === false) return void notify('Resume file was deleted from storage. Use Re-upload to attach its replacement.', 'warning')
    await openAttachmentWithTicket(resume.attachmentPublicId, 'Preview')
  }
  const openResumeReplacement = (application: RecruitmentCandidateApplication) => {
    setResumeIntakeTalentPoolOnly(false)
    setResumeIntakeTarget(application)
    setResumeIntakeMode('single')
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
    if (response.data?.pipelineWarning) notify(response.data.pipelineWarning, 'warning')
    setApplicationDraft({ candidateId: 0, positionId: 0, sourceType: 'Direct' }); await load(); await refreshDetail()
  }
  const createStandaloneInterviewApplication = async ({ fullName, email, phone, positionId }: { fullName: string; email: string; phone: string; positionId: number }) => {
    const position = positions.find(row => row.id === positionId)
    if (!position) return null
    const parts = fullName.trim().split(/\s+/)
    const candidate = await saveCandidate({ ...candidate0, clientId: position.clientId, firstName: parts[0] || '', lastName: parts.slice(1).join(' '), email: email.trim(), phone: phone.trim(), sourceType: 'Direct', consentStatus: 'Pending' })
    if (!candidate.ok || !candidate.data) return null
    const application = await createApplication({ candidateId: candidate.data.id, positionId, sourceType: 'Direct' })
    return application.ok ? application.data : null
  }
  const refreshInterviewData = async () => { await load(); await refreshDetail() }
  const selectApplicationAction = async (row: RecruitmentCandidateApplication, action: 'override' | 'pool') => {
    if (action === 'pool') return setTalentPoolDraft(row)
    const candidateDetail = detail?.candidate.id === row.candidateId ? detail : await getCandidate(row.candidateId)
    const score = candidateDetail?.scores.find(item => item.applicationId === row.id && item.isCurrent)
    if (!score) return void notify('Run ATS before overriding its score.', 'warning')
    setScoreOverrideDraft({ row: score, score: score.overrideScore ?? score.totalScore, reason: score.overrideReason || '' })
  }
  const moveToGlobalTalentPool = async () => {
    if (!talentPoolDraft) return
    const response = await moveApplicationCandidateToGlobalTalentPool(talentPoolDraft.id)
    if (!response.ok) return
    setTalentPoolDraft(null)
    await load()
  }
  const moveSelectedRejectedToGlobalTalentPool = async () => {
    const response = await moveApplicationsToGlobalTalentPool(selectedRejectedApplicationIds)
    if (!response.ok) return
    setSelectedRejectedApplicationIds([])
    await load()
  }
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
  const saveScoreOverride = async () => {
    if (!scoreOverrideDraft) return
    const response = await overrideApplicationScore(scoreOverrideDraft.row.id, scoreOverrideDraft.score, scoreOverrideDraft.reason)
    if (!response.ok) return
    setScoreOverrideDraft(null); await load(); await refreshDetail()
  }
  const currentScoreFor = (applicationId: number) => detail?.scores.find(row => row.applicationId === applicationId && row.isCurrent)
  const candidateFromDetail = detail?.candidate
  const intakeApplications = useMemo(() => applications.filter(row => row.applicationType === 'Application'), [applications])
  const candidateById = useMemo(() => new Map(candidates.map(row => [row.id, row])), [candidates])
  const editCandidate = async (candidateId: number) => {
    const candidate = candidateById.get(candidateId) ?? (await getCandidate(candidateId))?.candidate
    if (!candidate) return void notify('Candidate profile could not be loaded.', 'warning')
    setCandidateDraft({ ...candidate })
  }
  const applicationStages = useMemo(() => [...new Set(intakeApplications.map(row => row.currentStage).filter(Boolean))].sort(), [intakeApplications])
  const visibleApplications = useMemo(() => {
    const term = applicationSearch.trim().toLowerCase()
    const rows = intakeApplications.filter(row => {
      if (term && !`${row.candidateName} ${row.candidateEmail} ${row.candidatePhone} ${row.applicationCode} ${row.positionCode} ${row.positionTitle}`.toLowerCase().includes(term)) return false
      if (applicationPositionId && row.positionId !== applicationPositionId) return false
      if (applicationSource === 'published' && !publicJobApplication(row)) return false
      if (applicationSource === 'manual' && publicJobApplication(row)) return false
      if (applicationStage && row.currentStage !== applicationStage) return false
      if (applicationScoreBand === 'needs-score' && row.atsScore != null) return false
      if (applicationScoreBand === 'below-60' && !(row.atsScore != null && row.atsScore < 60)) return false
      if (applicationScoreBand === '60-79' && !(row.atsScore != null && row.atsScore >= 60 && row.atsScore < 80)) return false
      if (applicationScoreBand === '80-plus' && !(row.atsScore != null && row.atsScore >= 80)) return false
      if (applicationQuickFilter === 'new' && !newApplication(row)) return false
      if (applicationQuickFilter === 'needs-score' && row.atsScore != null) return false
      if (applicationQuickFilter === 'scored' && row.atsScore == null) return false
      if (applicationQuickFilter === 'pipeline' && (newApplication(row) || rejectedApplication(row))) return false
      if (applicationQuickFilter === 'rejected' && !rejectedApplication(row)) return false
      return true
    })
    return rows.sort((left, right) => applicationSort === 'oldest'
      ? new Date(left.appliedAt).getTime() - new Date(right.appliedAt).getTime()
      : applicationSort === 'score'
        ? (right.atsScore ?? -1) - (left.atsScore ?? -1)
        : new Date(right.appliedAt).getTime() - new Date(left.appliedAt).getTime())
  }, [applicationPositionId, applicationQuickFilter, applicationScoreBand, applicationSearch, applicationSort, applicationSource, applicationStage, intakeApplications])
  const applicationMetrics: Array<{ key: ApplicationQuickFilter; label: string; count: number; tone: string }> = [
    { key: 'all', label: 'All', count: intakeApplications.length, tone: 'blue' },
    { key: 'new', label: 'New applications', count: intakeApplications.filter(newApplication).length, tone: 'green' },
    { key: 'needs-score', label: 'Needs ATS', count: intakeApplications.filter(row => row.atsScore == null).length, tone: 'orange' },
    { key: 'scored', label: 'ATS scored', count: intakeApplications.filter(row => row.atsScore != null).length, tone: 'purple' },
    { key: 'pipeline', label: 'In pipeline', count: intakeApplications.filter(row => !newApplication(row) && !rejectedApplication(row)).length, tone: 'cyan' },
    { key: 'rejected', label: 'Rejected', count: intakeApplications.filter(rejectedApplication).length, tone: 'red' },
  ]
  const visibleInterviews = useMemo(() => interviews.filter(row => {
    if (interviewQuickFilter === 'scheduled') return ['Scheduled', 'Rescheduled'].includes(row.status)
    if (interviewQuickFilter === 'completed') return row.status === 'Completed'
    if (interviewQuickFilter === 'attention') return ['Cancelled', 'No Show'].includes(row.status)
    return true
  }), [interviewQuickFilter, interviews])
  const interviewReadyApplications = useMemo(() => applications.filter(row => interviewReady(row, interviews)), [applications, interviews])
  const selectedBatchApplications = useMemo(() => interviewReadyApplications.filter(row => batchInterviewApplicationIds.includes(row.id)), [batchInterviewApplicationIds, interviewReadyApplications])
  const selectedBatchPositionId = selectedBatchApplications[0]?.positionId || 0
  const interviewMetrics: Array<{ key: InterviewQuickFilter; label: string; count: number; tone: string }> = [
    { key: 'all', label: 'All interviews', count: interviews.length, tone: 'blue' },
    { key: 'scheduled', label: 'Scheduled', count: interviews.filter(row => ['Scheduled', 'Rescheduled'].includes(row.status)).length, tone: 'green' },
    { key: 'completed', label: 'Completed', count: interviews.filter(row => row.status === 'Completed').length, tone: 'purple' },
    { key: 'attention', label: 'Cancelled / no show', count: interviews.filter(row => ['Cancelled', 'No Show'].includes(row.status)).length, tone: 'red' },
  ]
  const candidateOptions = selectOptions(candidates.map(row => ({ value: row.id, label: `${row.candidateCode} - ${row.candidateName}` })), 'Select candidate', 0)
  const offerCandidate = candidates.find(candidate => candidate.id === applications.find(application => application.id === offerDraft?.applicationId)?.candidateId)
  const negotiationApplications = applications.filter(application => {
    if (/reject|withdraw|joined/i.test(application.currentStage)) return false
    const interview = interviews.filter(row => row.applicationId === application.id).sort((left, right) => right.id - left.id)[0]
    const position = positions.find(row => row.id === application.positionId)
    return interview?.status === 'Completed' && interview.result === 'Selected'
      && /negotiation|approval|offer/i.test(position?.pipelineStageName || '')
  })
  const offerApplicationOptions = selectOptions((negotiationMode ? negotiationApplications : applications).map(row => ({ value: row.id, label: `${row.applicationCode} - ${row.candidateName} / ${row.positionTitle}` })), 'Select application', 0)
  const applicationOptions = selectOptions(applications.map(row => ({ value: row.id, label: `${row.applicationCode} - ${row.candidateName} / ${row.positionTitle}` })), 'Select application', 0)
  const positionOptions = selectOptions(positions.filter(row => !['Closed', 'Cancelled', 'Filled'].includes(row.status)).map(row => ({ value: row.id, label: `${row.positionCode} - ${row.positionTitle}` })), 'Select position', 0)
  const checklistDocumentOptions = checklistDraft ? candidateAttachments
    .filter(file => !checklistDraft.item.attachmentAttributeId || file.attachmentAttributeId === checklistDraft.item.attachmentAttributeId)
    .filter(file => !checklistDraft.item.requiresVerification || file.verificationStatus === 'Verified')
    .map(file => ({ value: file.publicId, label: `${file.originalFileName} · ${file.verificationStatus}` })) : []
  const offerDocumentOptions = candidateAttachments.filter(file => file.attributeCode === 'OFFER_LETTER').map(file => ({ value: file.publicId, label: `${file.originalFileName} · v${file.versionNumber}` }))

  if (String(mode) === 'applications') return <div className="talent-workspace">
    <section className="candidate-applications-workspace" data-testid="candidate-applications-workspace">
      <header className="candidate-applications-heading"><div><span>CANDIDATE OPERATIONS</span><h2>Candidates</h2><p>Published-link and manual applications with ATS readiness in one queue.</p></div><Space wrap>{applicationQuickFilter === 'rejected' && <Popconfirm title="Move selected candidates to Global Talent Pool?" description="Their rejected applications and complete pipeline history will remain linked to the original jobs." okText="Move selected" disabled={!selectedRejectedApplicationIds.length} onConfirm={() => void moveSelectedRejectedToGlobalTalentPool()}><Button disabled={!selectedRejectedApplicationIds.length}>Move selected to Global Talent Pool ({selectedRejectedApplicationIds.length})</Button></Popconfirm>}<AddCandidateMenu onSelect={target => { setResumeIntakeTarget(null); setResumeIntakeTalentPoolOnly(target === 'talentPool'); setResumeIntakeMode('single') }} /><Button type="primary" icon={<UploadOutlined />} onClick={() => { setResumeIntakeTalentPoolOnly(false); setResumeIntakeMode('bulk') }}>Bulk candidate upload</Button></Space></header>
      <div className="candidate-status-strip" role="tablist" aria-label="Application status filters">{applicationMetrics.map(metric => <button key={metric.key} type="button" role="tab" aria-selected={applicationQuickFilter === metric.key} className={applicationQuickFilter === metric.key ? 'is-active' : ''} onClick={() => setApplicationQuickFilter(metric.key)}><b className={`tone-${metric.tone}`}>{metric.count}</b><span>{metric.label}</span></button>)}</div>
      <div className="candidate-applications-layout">
        <div className="candidate-applications-main">
          <div className="candidate-list-toolbar"><Input allowClear prefix={<SearchOutlined />} value={applicationSearch} onChange={event => setApplicationSearch(event.target.value)} placeholder="Search candidates by name, ID, email, phone or job" /><span>Showing <b>{visibleApplications.length}</b> candidates</span><Select value={applicationSort} onChange={setApplicationSort} options={[{ value: 'recent', label: 'Created date: Recent first' }, { value: 'oldest', label: 'Created date: Oldest first' }, { value: 'score', label: 'ATS score: Highest first' }]} /></div>
          <div className="candidate-application-list">{visibleApplications.map(row => {
            const candidate = candidateById.get(row.candidateId)
            const isPublic = publicJobApplication(row)
            const source = isPublic ? 'Published job link' : `Manual · ${row.sourceType || 'Direct'}`
            return <article key={row.id} className="candidate-application-card" tabIndex={0} role="button" onClick={() => void openCandidate(row.candidateId)} onKeyDown={event => { if (event.key === 'Enter') void openCandidate(row.candidateId) }}>
              <Avatar size={46}>{initials(row.candidateName)}</Avatar>
              <div className="candidate-application-copy"><div className="candidate-card-title"><h3>{row.candidateName}</h3><Tag>{row.applicationCode}</Tag>{!row.candidateEmail && !row.candidatePhone && <Tag color="orange">Profile incomplete</Tag>}{row.atsScore != null && <Tag color={row.atsScore >= (row.atsShortlistThreshold || 60) ? 'green' : 'orange'} icon={<RobotOutlined />}>{row.atsScore.toFixed(1)} / 100</Tag>}{row.isInGlobalTalentPool && <Tag color="cyan">Global Talent Pool</Tag>}</div><p>{row.positionTitle} <span>({row.positionCode})</span></p>{row.atsJobStatus && row.atsJobStatus !== 'Completed' && <div style={{ whiteSpace: 'normal', marginBottom: 6 }}><Tag color={row.atsJobStatus === 'Failed' ? 'error' : 'processing'}>ATS {row.atsJobStatus === 'Retry' ? 'retry scheduled' : row.atsJobStatus.toLowerCase()}</Tag>{row.atsJobError && <small>{row.atsJobError}</small>}</div>}<div className="candidate-card-contact"><span><FileSearchOutlined /> {experienceLabel(candidate?.totalExperienceMonths || 0)}</span><span><MailOutlined /> {row.candidateEmail || 'No email'}</span><span><PhoneOutlined /> {row.candidatePhone || 'No phone'}</span><Tag color={isPublic ? 'blue' : 'default'}>{source}</Tag></div><small>Applied {formatApiDateTime(row.appliedAt)} · {row.recruiterName || 'Recruiter not assigned'}</small></div>
              <div className="candidate-card-actions" onClick={event => event.stopPropagation()}>
                {rejectedApplication(row) && !row.isInGlobalTalentPool && <Checkbox checked={selectedRejectedApplicationIds.includes(row.id)} onChange={event => setSelectedRejectedApplicationIds(current => event.target.checked ? [...new Set([...current, row.id])] : current.filter(id => id !== row.id))}>Select</Checkbox>}
                <Tag color={rejectedApplication(row) ? 'red' : 'green'}>{row.currentStage || row.currentStatus}</Tag>
                {(canOverrideAts(row) || (session?.user.clientId == null && row.canMoveToGlobalTalentPool)) && <Select
                  className="candidate-next-action"
                  size="small"
                  placeholder="Next action"
                  value={undefined}
                  options={[
                    ...(canOverrideAts(row) && !rejectedApplication(row) && !row.currentStage.startsWith('Offer')
                      ? [{ value: 'override', label: 'Override ATS' }]
                      : []),
                    ...(session?.user.clientId == null && row.canMoveToGlobalTalentPool ? [{ value: 'pool', label: row.isInGlobalTalentPool ? 'In Global Talent Pool' : 'Move to Global Talent Pool', disabled: row.isInGlobalTalentPool }] : []),
                  ]}
                  onChange={(action: 'override' | 'pool') => void selectApplicationAction(row, action)}
                />}
                <Space wrap><Button size="small" icon={<EditOutlined />} onClick={() => void editCandidate(row.candidateId)}>Edit</Button>{row.resumeId && row.resumeAvailable !== false ? <Button size="small" icon={<FileSearchOutlined />} onClick={() => void viewApplicationResume(row.id)}>Resume</Button> : <><Tag color="red">{row.resumeId ? 'Resume deleted' : 'No resume'}</Tag><Button size="small" icon={<UploadOutlined />} onClick={() => openResumeReplacement(row)}>Re-upload</Button></>}{canMoveApplication(row) && (!row.autoRunAts || row.atsScore == null) && <Tooltip title={row.resumeId && row.resumeAvailable !== false ? row.autoRunAts ? 'Auto ATS is pending; run it now as a fallback' : 'Calculate or refresh ATS evidence' : 'Re-upload the resume before ATS scoring'}><Button size="small" disabled={!row.resumeId || row.resumeAvailable === false} icon={<RobotOutlined />} onClick={() => void scoreApplication(row.id).then(load)}>{row.autoRunAts ? 'Run ATS now' : 'Run ATS'}</Button></Tooltip>}<Button size="small" type="primary" icon={<BranchesOutlined />} onClick={() => navigate(`/recruitment/hiring-pipeline?clientId=${row.clientId}&positionId=${row.positionId}&flow=candidates`)}>Pipeline</Button>{canDeleteRecruitmentData && <Popconfirm title="Delete application permanently?" description="This removes its ATS and pipeline data. If it is the candidate's only application, the candidate profile and stored resume file are also permanently deleted." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteApplication(row.id); if (response.ok) await load() }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.applicationCode}`} /></Popconfirm>}</Space>
              </div>
            </article>
          })}{!visibleApplications.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No applications match these filters." />}</div>
        </div>
        <aside className="candidate-filter-panel"><header><strong>Filters</strong><Badge count={[applicationPositionId, applicationSource, applicationStage, applicationScoreBand].filter(Boolean).length} showZero color="#5b4ce6" /></header><label><span>Job role</span><Select allowClear showSearch optionFilterProp="label" value={applicationPositionId || undefined} onChange={value => setApplicationPositionId(Number(value || 0))} placeholder="All job roles" options={positions.map(row => ({ value: row.id, label: `${row.positionTitle} (${row.positionCode})` }))} /></label><label><span>Application source</span><Select allowClear value={applicationSource || undefined} onChange={value => setApplicationSource(value || '')} placeholder="All sources" options={[{ value: 'published', label: 'Published job link' }, { value: 'manual', label: 'Manual upload' }]} /></label><label><span>Pipeline stage</span><Select allowClear showSearch optionFilterProp="label" value={applicationStage || undefined} onChange={value => setApplicationStage(value || '')} placeholder="All stages" options={applicationStages.map(value => ({ value, label: value }))} /></label><label><span>ATS score</span><Select allowClear value={applicationScoreBand || undefined} onChange={value => setApplicationScoreBand(value || '')} placeholder="All ATS states" options={[{ value: 'needs-score', label: 'Needs scoring' }, { value: 'below-60', label: 'Below 60' }, { value: '60-79', label: '60–79' }, { value: '80-plus', label: '80 and above' }]} /></label><Button onClick={() => { setApplicationPositionId(0); setApplicationSource(''); setApplicationStage(''); setApplicationScoreBand(''); setApplicationSearch(''); setApplicationQuickFilter('all') }}>Reset filters</Button></aside>
      </div>
    </section>
    <Drawer className="candidate-profile-drawer candidate-application-detail" open={!!detail} onClose={() => { setDetail(null); setCandidateAttachments([]) }} width="min(1040px, 96vw)" title={candidateFromDetail ? `${candidateFromDetail.candidateName} · Candidate profile` : 'Candidate profile'}>{candidateFromDetail && <div className="candidate-360">
      <div className="candidate-detail-hero"><Avatar size={58}>{initials(candidateFromDetail.candidateName)}</Avatar><div><h2>{candidateFromDetail.candidateName}</h2><p>{candidateFromDetail.currentTitle || detail.applications[0]?.positionTitle || 'Candidate'}</p></div><Space wrap><Tag>{candidateFromDetail.candidateCode}</Tag><Tag color={candidateFromDetail.consentStatus === 'Granted' ? 'green' : 'orange'}>{candidateFromDetail.consentStatus} consent</Tag></Space></div>
      <Card size="small" title="Personal details"><div className="candidate-facts">{[['Email', candidateFromDetail.email || '-'], ['Phone', candidateFromDetail.phone || '-'], ['Location', candidateFromDetail.currentLocation || '-'], ['Experience', experienceLabel(candidateFromDetail.totalExperienceMonths)], ['Qualification', candidateFromDetail.highestQualification || '-'], ['Current company', candidateFromDetail.currentCompany || '-']].map(([label, value]) => <article key={label}><span>{label}</span><b>{value}</b></article>)}</div></Card>
      <Card size="small" title="Hiring pipeline"><div className="candidate-hiring-timeline">{detail.applications.map(row => <article key={row.id}><i /><div><strong>{row.positionTitle}</strong><span>{row.applicationCode} · {row.currentStage}</span><small>Applied {formatApiDateTime(row.appliedAt)}</small></div><Space wrap><CandidateTrackingPreview applicationId={row.id} /><Tag color={row.atsScore == null ? 'orange' : row.atsScore >= 60 ? 'green' : 'red'}>{row.atsScore == null ? 'ATS pending' : `ATS ${row.atsScore.toFixed(1)}`}</Tag>{row.atsScore == null && canMoveApplication(row) && <Button size="small" icon={<RobotOutlined />} disabled={!row.resumeId || row.resumeAvailable === false} onClick={() => void scoreApplication(row.id).then(async () => { await load(); await refreshDetail() })}>Run ATS now</Button>}<Button size="small" type="primary" onClick={() => navigate(`/recruitment/hiring-pipeline?clientId=${row.clientId}&positionId=${row.positionId}&flow=candidates`)}>Open pipeline</Button></Space></article>)}</div></Card>
      <RecruitmentAtsScoreDetails scores={detail.scores} applications={detail.applications} />
      <Card size="small" title="Interview feedback & decisions"><DataTable rows={detail.interviews} emptyText="No interviews recorded." actions={row => <Space wrap><Button size="small" icon={<FileSearchOutlined />} onClick={() => void viewApplicationResume(row.applicationId)}>Resume</Button>{canScheduleInterviews && !['Completed', 'Cancelled', 'No Show'].includes(row.status) && <Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row })}>Update</Button>}<Button size="small" type="primary" onClick={() => setFeedbackInterview(row)}>{row.status === 'Completed' || (row.submittedPanelUserIds || []).length > 0 ? 'View feedback' : 'Panel feedback'}</Button></Space>} columns={[{ key: 'roundCode', label: 'Round' }, { key: 'scheduledStart', label: 'Schedule', render: row => new Date(row.scheduledStart).toLocaleString('en-IN') }, { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Completed' ? 'green' : 'blue'}>{row.status}</Tag> }, { key: 'result', label: 'Decision' }, { key: 'overallScore', label: 'Score', render: row => `${Number(row.overallScore || 0).toFixed(1)} / 100` }]} /></Card>
      <Card size="small" title="Offers"><DataTable rows={detail.offers} emptyText="No offer recorded." actions={row => row.offerLetterAttachmentPublicId ? <OfferLetterPreview publicId={row.offerLetterAttachmentPublicId} title={`${row.candidateName} · ${row.offerNumber}`} /> : null} columns={[{ key: 'offerNumber', label: 'Offer' }, { key: 'positionTitle', label: 'Position' }, { key: 'offeredCtc', label: 'CTC', render: row => `${row.currency} ${Number(row.offeredCtc).toLocaleString('en-IN')}` }, { key: 'proposedJoiningDate', label: 'Joining', render: row => new Date(row.proposedJoiningDate).toLocaleDateString('en-IN') }, { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Accepted' ? 'green' : row.status === 'Rejected' ? 'red' : 'blue'}>{row.status}</Tag> }]} /></Card>
      <EntityAttachmentPanel entityType="CANDIDATE" entityId={candidateFromDetail.id} clientId={candidateFromDetail.clientId} moduleCode="RECRUITMENT" formCodes={['CANDIDATE_APPLICATION', 'EMPLOYEE_REFERRAL', 'PRE_ONBOARDING']} title="Candidate documents" uploadOverride={(configuration: AttachmentFieldConfiguration, draft: EntityAttachmentDraft, onProgress) => !draft.file ? Promise.resolve({ ok: false, error: 'Select a file.' }) : configuration.attributeCode === 'RESUME' ? uploadCandidateResume(candidateFromDetail.id, configuration.id, draft.file, draft, onProgress) : uploadEntityAttachment(configuration.id, 'CANDIDATE', candidateFromDetail.id, draft.file, draft, onProgress)} onChanged={() => void refreshDetail()} />
      <Card size="small" title="Activity timeline"><div className="candidate-activity">{detail.activity.map(item => <article key={`${item.moduleCode}-${item.id}`}><i /><div><b>{item.eventTitle}</b><p>{item.eventSummary}</p><small>{new Date(item.occurredAt).toLocaleString('en-IN')} · {item.actorName || 'System'}</small></div></article>)}{!detail.activity.length && <p>No activity recorded.</p>}</div></Card>
    </div>}</Drawer>
    <Modal open={!!scoreOverrideDraft} title="Override ATS score" onCancel={() => setScoreOverrideDraft(null)} onOk={() => void saveScoreOverride()} okButtonProps={{ disabled: !scoreOverrideDraft?.reason.trim() }}>{scoreOverrideDraft && <Form layout="vertical"><Form.Item label="Calculated score"><InputNumber value={scoreOverrideDraft.row.totalScore} disabled /></Form.Item><Form.Item label="Override score" required><InputNumber min={0} max={100} precision={2} value={scoreOverrideDraft.score} onChange={score => setScoreOverrideDraft({ ...scoreOverrideDraft, score: Number(score || 0) })} /></Form.Item><Form.Item label="Reason" required><Input.TextArea value={scoreOverrideDraft.reason} onChange={event => setScoreOverrideDraft({ ...scoreOverrideDraft, reason: event.target.value })} placeholder="Document the business reason for this manual override." /></Form.Item></Form>}</Modal>
    <Modal open={!!talentPoolDraft} title="Move to Global Talent Pool?" onCancel={() => setTalentPoolDraft(null)} onOk={() => void moveToGlobalTalentPool()} okText="Move candidate">
      {talentPoolDraft && <p><b>{talentPoolDraft.candidateName}</b> will be available in the Global Talent Pool. This application and its pipeline history will remain linked to the current job.</p>}
    </Modal>
    <CandidateEditorDrawer draft={candidateDraft} clients={clients} onChange={setCandidateDraft} onClose={() => setCandidateDraft(null)} onSubmit={() => void saveCandidateDraft()} />
    {candidateFormOpen && <RecruitmentInternalCandidateForm open clientId={initialClientId} initialPostingId={requestedPostingId} onClose={closeCandidateForm} onCompleted={load} />}
    <RecruitmentResumeIntake open={resumeIntakeMode !== null} initialMode={resumeIntakeMode || 'single'} initialClientId={resumeIntakeTarget?.clientId ?? initialClientId} initialPositionId={resumeIntakeTarget?.positionId ?? requestedPositionId} initialJobPostingId={resumeIntakeTarget?.jobPostingId ?? (requestedPostingId || null)} talentPoolOnly={resumeIntakeTalentPoolOnly} onClose={closeResumeIntake} onCompleted={async () => { await load() }} onEditCandidate={editCandidate} title={resumeIntakeTarget ? `Re-upload resume · ${resumeIntakeTarget.candidateName}` : resumeIntakeTalentPoolOnly ? 'Global Talent Pool intake' : resumeIntakeMode === 'bulk' ? 'Bulk candidate upload' : 'Add candidate application'} description={resumeIntakeTarget ? 'Preview the replacement, correct parsed details if required, then upload. The deleted resume reference will be replaced.' : resumeIntakeTalentPoolOnly ? 'Preview and edit parsed details first; final upload stores the resume in the Global Talent Pool.' : 'Preview and edit parsed candidate details first. Final upload creates the candidate/application and stores the resume.'} />
    {interviewDraft && <RecruitmentInterviewEditor mode="schedule" open applications={applications} positions={positions} panelUsers={panelUsers} standalone={Boolean(interviewDraft.standalone)} onCreateStandaloneApplication={session?.user.permissions.some(permission => ['recruitment.manage', 'settings.manage'].includes(permission)) ? createStandaloneInterviewApplication : undefined} interview={interviewDraft.interview} initialDecision={interviewDraft.initialDecision} initialApplicationId={interviewDraft.applicationId} initialStartNow={Boolean(interviewDraft.startNow)} onClose={() => setInterviewDraft(null)} onSaved={refreshInterviewData} />}
    {feedbackInterview && <RecruitmentInterviewEditor mode="feedback" open applications={applications} panelUsers={panelUsers} interview={feedbackInterview} readOnly={feedbackInterview.status === 'Completed'} onClose={() => setFeedbackInterview(null)} onSaved={refreshInterviewData} />}
  </div>

  return <div className="talent-workspace">
    {mode === 'candidates' && initialClientId > 0 && candidates.some(row => row.clientId === initialClientId) && <Card title="Client candidate profiles" size="small" data-testid="client-candidate-profiles">
      <DataTable rows={candidates.filter(row => row.clientId === initialClientId)} exportFileName="client-candidate-profiles" actions={row => <Button size="small" onClick={() => void openCandidate(row.id)}>View profile</Button>} columns={[
        { key: 'candidateCode', label: 'Candidate code' }, { key: 'candidateName', label: 'Candidate' }, { key: 'email', label: 'Email' }, { key: 'phone', label: 'Phone' }, { key: 'profileStatus', label: 'Status' },
      ]} />
    </Card>}
    {mode === 'candidates' && <RecruitmentGlobalTalentPool onViewCandidate={openCandidate} onChanged={load} />}
    {mode === 'applications' && <><div className="talent-toolbar right"><Button onClick={() => setResumeIntakeMode('single')}>Upload resume</Button><Button type="primary" onClick={() => setResumeIntakeMode('bulk')}>Bulk resumes</Button></div><DataTable rows={intakeApplications} emptyText="No applications have arrived through a published job link or manual upload for this client." exportFileName="candidate-applications" actions={row => <Space><Button size="small" onClick={() => void openCandidate(row.candidateId)}>Profile</Button><Button size="small" icon={<FileSearchOutlined />} onClick={() => void viewApplicationResume(row.id)}>Resume</Button>{canMoveApplication(row) && <Button size="small" onClick={() => void scoreApplication(row.id).then(load)}>Score</Button>}<Button size="small" type="primary" onClick={() => navigate(`/recruitment/hiring-pipeline?positionId=${row.positionId}`)}>Open pipeline</Button>{canDeleteRecruitmentData && <Popconfirm title="Delete application permanently?" description="This removes its ATS and pipeline data. If it is the candidate's only application, the candidate profile and stored resume file are also permanently deleted." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteApplication(row.id); if (response.ok) await load() }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.applicationCode}`} /></Popconfirm>}</Space>} columns={[
      { key: 'applicationCode', label: 'Application' }, { key: 'candidateName', label: 'Candidate' }, { key: 'positionTitle', label: 'Position', render: row => <><b>{row.positionTitle}</b><small>{row.positionCode}</small></> }, { key: 'clientName', label: 'Client' }, { key: 'sourceType', label: 'Application source', render: row => <Tag color={publicJobApplication(row) ? 'blue' : 'default'}>{publicJobApplication(row) ? 'Published job link' : `Manual · ${row.sourceType || 'Direct'}`}</Tag> }, { key: 'currentStage', label: 'Stage' }, { key: 'recruiterName', label: 'Recruiter' }, { key: 'atsScore', label: 'ATS score', render: row => row.atsScore == null ? '-' : <Tag color={row.atsScore >= 60 ? 'green' : 'orange'}>{row.atsScore.toFixed(1)}</Tag> }, { key: 'appliedAt', label: 'Applied', render: row => new Date(row.appliedAt).toLocaleDateString('en-IN') }
    ]} /></>}
    {mode === 'interviewQueue' && <section className="interview-operations-workspace">
      <header className="candidate-applications-heading"><div><span>INTERVIEW QUEUE</span><h2>Candidates ready for interview</h2><p>ATS-qualified candidates stay in the bulk queue. New interview can be scheduled independently without changing ATS.</p></div><Space wrap><NewInterviewMenu onSelect={(startNow) => setInterviewDraft({ applicationId: 0, standalone: true, startNow })} /><Button type="primary" icon={<CalendarOutlined />} disabled={!selectedBatchApplications.length} onClick={() => setBatchInterviewOpen(true)}>Schedule selected ({selectedBatchApplications.length})</Button></Space></header>
      <Card size="small" title="Candidates ready for interview" extra={<Tag color="cyan">{interviewReadyApplications.length} ready</Tag>}>
        <Table<RecruitmentCandidateApplication>
          rowKey="id"
          size="small"
          pagination={{ pageSize: 8, hideOnSinglePage: true }}
          dataSource={interviewReadyApplications}
          locale={{ emptyText: 'No ATS-qualified candidates are ready for interview yet.' }}
          rowSelection={{
            selectedRowKeys: batchInterviewApplicationIds,
            onChange: keys => setBatchInterviewApplicationIds(keys.map(Number)),
            getCheckboxProps: row => ({ disabled: Boolean(selectedBatchPositionId && row.positionId !== selectedBatchPositionId) }),
          }}
          columns={[
            { title: 'Candidate', render: (_, row) => <div><b>{row.candidateName}</b><br /><small>{row.applicationCode}</small></div> },
            { title: 'Job', render: (_, row) => <div>{row.positionTitle}<br /><small>{row.positionCode}</small></div> },
            { title: 'ATS', render: (_, row) => row.atsScore == null ? <Tag>Pending</Tag> : <Tag color={row.atsScore >= (row.atsShortlistThreshold || 60) ? 'green' : 'orange'}>{row.atsScore.toFixed(1)}</Tag> },
            { title: 'Candidate stage', dataIndex: 'currentStage', render: value => <Tag color={/interview/i.test(String(value)) ? 'blue' : 'purple'}>{value}</Tag> },
          ]}
        />
      </Card>
    </section>}
    {mode === 'interviews' && <section className="interview-operations-workspace">
      {internalInterviewsEnabled && <InternalInterviewDashboard />}
      {canScheduleInterviews && <div className="talent-toolbar right"><NewInterviewMenu onSelect={(startNow) => setInterviewDraft({ applicationId: 0, standalone: true, startNow })} /></div>}
      <div className="candidate-status-strip interview-status-strip" role="tablist" aria-label="Interview status filters">{interviewMetrics.map(metric => <button key={metric.key} type="button" role="tab" aria-selected={interviewQuickFilter === metric.key} className={interviewQuickFilter === metric.key ? 'is-active' : ''} onClick={() => setInterviewQuickFilter(metric.key)}><b className={`tone-${metric.tone}`}>{metric.count}</b><span>{metric.label}</span></button>)}</div>
      <DataTable rows={visibleInterviews} emptyText="No interviews match this status." exportFileName="recruitment-interviews" actions={row => <Space wrap>
        {internalInterviewsEnabled && <Button size="small" onClick={() => navigate(`/recruitment/interview-session/${row.id}`)}>Internal session</Button>}
        <Button size="small" icon={<FileSearchOutlined />} onClick={() => void viewApplicationResume(row.applicationId)}>Resume</Button>
        {canScheduleInterviews && !['Completed', 'Cancelled', 'No Show'].includes(row.status) && <Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row })}>Update</Button>}
        {canScheduleInterviews && ['Scheduled', 'Rescheduled'].includes(row.status) && <Tooltip title={row.locationOrLink ? 'Email the candidate and selected panel' : 'Add a meeting link or location first'}><Button size="small" icon={<MailOutlined />} disabled={!row.locationOrLink} onClick={() => void sendInterviewInvite(row.id)}>Send invite</Button></Tooltip>}
        {canScheduleInterviews && ['Scheduled', 'Rescheduled'].includes(row.status) && <Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: { ...row, status: 'No Show', result: 'No Show' } })}>No show</Button>}
        {canScheduleInterviews && ['Scheduled', 'Rescheduled'].includes(row.status) && <Button size="small" danger onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: { ...row, status: 'Cancelled', result: 'Pending' } })}>Cancel</Button>}
        <Button size="small" type="primary" onClick={() => setFeedbackInterview(row)}>{row.status === 'Completed' || (row.submittedPanelUserIds || []).length > 0 ? 'View feedback' : 'Panel feedback'}</Button>
        {row.canRecordDecision && <Button size="small" type="primary" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row, initialDecision: 'Selected' })}>Select</Button>}
        {row.canRecordDecision && <Button size="small" danger onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row, initialDecision: 'Rejected' })}>Reject</Button>}
        {canDeleteRecruitmentData && <Popconfirm title="Delete this interview?" description="Panel feedback and safe interview documents will also be removed. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteInterview(row.id); if (response.ok) await load() }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete interview ${row.roundCode}`} /></Popconfirm>}
      </Space>} columns={[
        { key: 'candidateName', label: 'Candidate', width: 210, wrap: true },
        { key: 'positionTitle', label: 'Position', width: 220, wrap: true },
        { key: 'roundCode', label: 'Round', width: 220, wrap: true },
        { key: 'panelFeedbackStatus', label: 'Panel feedback', width: 240, wrap: true, render: row => <Space direction="vertical">{(row.panelFeedbackStatus || []).map(member => <span key={member.userId}>{member.displayName} <Tag color={member.submitted ? 'green' : 'orange'}>{member.submitted ? 'Submitted' : 'Pending'}</Tag></span>)}</Space> },
        { key: 'scheduledStart', label: 'Schedule', render: row => <><b>{new Date(row.scheduledStart).toLocaleDateString('en-IN')}</b><small>{new Date(row.scheduledStart).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })} - {new Date(row.scheduledEnd).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}</small></> },
        { key: 'mode', label: 'Mode' },
        { key: 'locationOrLink', label: 'Link / location', width: 210, wrap: true, render: row => /^https?:\/\//i.test(row.locationOrLink || '') ? <Button size="small" type="link" href={row.locationOrLink} target="_blank" rel="noreferrer">Open link</Button> : row.locationOrLink || '-' },
        { key: 'status', label: 'Status', render: row => <Tag color={['Scheduled', 'Rescheduled'].includes(row.status) ? 'blue' : row.status === 'Completed' ? 'green' : 'red'}>{row.status}</Tag> },
        { key: 'result', label: 'Result' },
        { key: 'decisionApproverName', label: 'Decision approver', width: 180, wrap: true, render: row => row.decisionApproverName || 'HR / scheduling team' },
      ]} />
    </section>}
    {mode === 'offers' && <><div className="talent-toolbar right">{session?.user.clientId == null && session?.user.roles.includes('super_admin') && <FinalOfferSettings clients={clients} initialClientId={initialClientId} reload={load} />}<Button type="primary" icon={<PlusOutlined />} onClick={() => { setCandidateAttachments([]); setOfferDraft({ applicationId: 0, offeredCtc: 0, currency: 'INR', proposedJoiningDate: new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10), status: 'Draft' }) }}>{negotiationMode ? 'Record negotiation' : 'Create offer'}</Button></div><DataTable rows={offers} exportFileName="recruitment-offers" actions={row => <Space wrap><Button size="small" icon={<FileSearchOutlined />} onClick={() => void viewApplicationResume(row.applicationId)}>Resume</Button>{row.status === 'Draft' && <Button size="small" onClick={() => { setOfferDraft({ ...row }); void loadOfferDocuments(row.applicationId) }}>Edit</Button>}{row.status === 'Draft' && <Button size="small" onClick={() => void generateOfferLetter(row.id).then(load)}>{row.offerLetterAttachmentPublicId ? 'Regenerate letter' : 'Generate letter'}</Button>}{row.offerLetterAttachmentPublicId && <OfferLetterPreview publicId={row.offerLetterAttachmentPublicId} title={`${row.candidateName} · ${row.offerNumber}`} label="View letter" />}<FinalOfferActions offer={row} reload={load} />{['Draft', 'Approved'].includes(row.status) && <Button size="small" type="primary" disabled={!row.offerLetterAttachmentPublicId} title={!row.offerLetterAttachmentPublicId ? 'Generate or link the offer letter first.' : undefined} onClick={() => void updateOfferStatus(row.id, 'Pending Candidate').then(load)}>{row.status === 'Approved' ? 'Release' : negotiationMode ? 'Submit negotiated terms' : 'Submit / release'}</Button>}{['Pending Candidate', 'Released', 'Negotiation'].includes(row.status) && <><Button size="small" type="primary" onClick={() => void updateOfferStatus(row.id, 'Accepted').then(load)}>Accept</Button><Button size="small" danger onClick={() => setOfferStatusDraft({ row, status: 'Rejected', reason: '' })}>Reject</Button></>}{['Pending Candidate', 'Released'].includes(row.status) && <Button size="small" onClick={() => setOfferStatusDraft({ row, status: 'Negotiation', reason: '' })}>Negotiate</Button>}{['Draft', 'Approved', 'Pending Candidate', 'Released', 'Negotiation'].includes(row.status) && <Button size="small" danger onClick={() => setOfferStatusDraft({ row, status: 'Withdrawn', reason: '' })}>Withdraw</Button>}{canDeleteRecruitmentData && <Popconfirm title="Delete this offer?" description="The generated offer letter will also be removed. Joined applications are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteOffer(row.id); if (response.ok) await load() }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete offer ${row.offerNumber}`} /></Popconfirm>}</Space>} columns={[
      { key: 'offerNumber', label: 'Offer' }, { key: 'candidateName', label: 'Candidate' }, { key: 'positionTitle', label: 'Position' }, { key: 'offerTemplateName', label: 'Template', render: row => row.offerTemplateName || '-' }, { key: 'offerLetter', label: 'Letter', render: row => <Tag color={row.offerLetterAttachmentPublicId ? 'green' : 'orange'}>{row.offerLetterAttachmentPublicId ? 'Secured' : 'Missing'}</Tag> }, { key: 'offeredCtc', label: 'CTC', render: row => `${row.currency} ${Number(row.offeredCtc).toLocaleString('en-IN')}` }, { key: 'approvedBudgetAmount', label: 'Approved budget', render: row => row.approvedBudgetAmount > 0 ? `${row.currency} ${Number(row.approvedBudgetAmount).toLocaleString('en-IN')}` : '-' }, { key: 'variancePercent', label: 'Variance', render: row => row.stageOfferConfigurationId ? <Tag color={row.varianceExceeded ? 'red' : 'green'}>{Number(row.variancePercent).toFixed(2)}%</Tag> : '-' }, { key: 'approvalPolicy', label: 'Approval policy', render: row => row.approvalPolicy || (row.stageOfferConfigurationId ? 'Pipeline direct' : 'Global') }, { key: 'proposedJoiningDate', label: 'Joining', render: row => new Date(row.proposedJoiningDate).toLocaleDateString('en-IN') }, { key: 'status', label: 'Status' }
    ]} /></>}

    <CandidateEditorDrawer draft={candidateDraft} clients={clients} onChange={setCandidateDraft} onClose={() => setCandidateDraft(null)} onSubmit={() => void saveCandidateDraft()} />

    <RecruitmentEditorDrawer open={applicationDraft.candidateId > 0 || (!!detail && applicationDraft.positionId > 0)} eyebrow="Candidate application" title="Add application" description="Link the selected candidate to an open position and record its source." onClose={() => setApplicationDraft({ candidateId: 0, positionId: 0, sourceType: 'Direct' })} onSubmit={() => void addApplication()} submitText="Add application" width="min(640px, 96vw)"><Form layout="vertical"><Form.Item label="Candidate"><SearchSelect value={applicationDraft.candidateId || detail?.candidate.id || 0} onChange={value => setApplicationDraft({ ...applicationDraft, candidateId: Number(value) })} options={candidateOptions} /></Form.Item><Form.Item label="Open position"><SearchSelect value={applicationDraft.positionId} onChange={value => setApplicationDraft({ ...applicationDraft, positionId: Number(value) })} options={positionOptions} /></Form.Item><Form.Item label="Source"><Select value={applicationDraft.sourceType} onChange={value => setApplicationDraft({ ...applicationDraft, sourceType: value })} options={['Direct', 'Employee Referral', 'Consultant', 'Vendor', 'Job Portal'].map(value => ({ value, label: value }))} /></Form.Item></Form></RecruitmentEditorDrawer>
    <Modal open={!!scoreOverrideDraft} title="Override ATS score" onCancel={() => setScoreOverrideDraft(null)} onOk={() => void saveScoreOverride()} okButtonProps={{ disabled: !scoreOverrideDraft?.reason.trim() }}>{scoreOverrideDraft && <Form layout="vertical"><Form.Item label="Calculated score"><InputNumber value={scoreOverrideDraft.row.totalScore} disabled /></Form.Item><Form.Item label="Override score" required><InputNumber min={0} max={100} precision={2} value={scoreOverrideDraft.score} onChange={score => setScoreOverrideDraft({ ...scoreOverrideDraft, score: Number(score || 0) })} /></Form.Item><Form.Item label="Reason" required><Input.TextArea value={scoreOverrideDraft.reason} onChange={event => setScoreOverrideDraft({ ...scoreOverrideDraft, reason: event.target.value })} placeholder="Document the business reason for this manual override." /></Form.Item></Form>}</Modal>

    <RecruitmentEditorDrawer open={!!profileDraft} eyebrow="Candidate profile" title="Experience, education & certifications" description="Keep the detailed profile sections together in one candidate record." onClose={() => setProfileDraft(null)} onSubmit={() => void saveProfileSections()} submitText="Save profile" width="min(1000px, 96vw)">{profileDraft && <CandidateProfileFields profileDraft={profileDraft} setProfileDraft={setProfileDraft} candidateId={detail?.candidate.id || 0} />}</RecruitmentEditorDrawer>

    <Modal open={!!checklistDraft} title="Complete pre-onboarding item" onCancel={() => setChecklistDraft(null)} onOk={() => void completeChecklist()} okButtonProps={{ disabled: !!checklistDraft?.item.attachmentAttributeId && !checklistDraft.attachmentPublicId }}>{checklistDraft && <Form layout="vertical">
      <Form.Item label="Checklist item"><Input value={checklistDraft.item.checklistName} disabled /></Form.Item>
      {checklistDraft.item.attachmentAttributeId ? <Form.Item label="Candidate document" required help={checklistDraft.item.requiresVerification ? 'Only a verified document of the configured type can complete this item.' : 'The document remains managed by the global attachment system.'}><Select value={checklistDraft.attachmentPublicId || undefined} onChange={attachmentPublicId => setChecklistDraft({ ...checklistDraft, attachmentPublicId })} placeholder="Select uploaded document" options={checklistDocumentOptions} /></Form.Item> : <p>No document is required for this checklist item.</p>}
      {!!checklistDraft.item.attachmentAttributeId && !checklistDocumentOptions.length && <p>Upload the configured document in Candidate documents first.</p>}
    </Form>}</Modal>

    <RecruitmentEditorDrawer open={!!conversionDraft} eyebrow="Candidate conversion" title="Create employee" description="Review joining and organization details before creating the employee master record." onClose={() => setConversionDraft(null)} onSubmit={() => void saveConversion()} submitText="Create employee" width="min(820px, 96vw)">{conversionDraft && <Form layout="vertical" className="talent-form-grid">
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
    </Form>}</RecruitmentEditorDrawer>

    <Drawer className="candidate-profile-drawer" open={!!detail} onClose={() => { setDetail(null); setCandidateAttachments([]) }} width="min(1180px, 96vw)" title={candidateFromDetail ? `${candidateFromDetail.candidateCode} - ${candidateFromDetail.candidateName}` : 'Talent profile'}>{candidateFromDetail && <div className="candidate-360">
      <div className="candidate-hero"><div><h2>{candidateFromDetail.candidateName}</h2><p>{candidateFromDetail.currentTitle || 'Candidate'} · {candidateFromDetail.currentCompany || 'Independent'}</p></div><Space wrap><Tag>{candidateFromDetail.profileStatus}</Tag><Tag color={candidateFromDetail.consentStatus === 'Granted' ? 'green' : candidateFromDetail.consentStatus === 'Revoked' ? 'red' : 'orange'}>{candidateFromDetail.consentStatus} consent</Tag>{candidateFromDetail.employeeCode && <Tag color="green">Employee {candidateFromDetail.employeeCode}</Tag>}<Button onClick={() => setCandidateDraft({ ...candidateFromDetail })}>Edit summary</Button><Button onClick={() => setProfileDraft({ experience: detail.experience.map(row => ({ ...row })), education: detail.education.map(row => ({ ...row })), certifications: detail.certifications.map(row => ({ ...row })) })}>Profile details</Button>{canApplyCandidate(candidateFromDetail) && <Button type="primary" onClick={() => setApplicationDraft({ candidateId: candidateFromDetail.id, positionId: 0, sourceType: 'Direct' })}>Add application</Button>}{canDeleteRecruitmentData && <Popconfirm title="Delete this candidate permanently?" description="Joined, workflow-linked and business-history records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteCandidate(candidateFromDetail.id); if (response.ok) { setDetail(null); await load() } }}><Button danger icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</Space></div>
      <div className="candidate-facts">{[['Email', candidateFromDetail.email || '-'], ['Phone', candidateFromDetail.phone || '-'], ['Location', candidateFromDetail.currentLocation || '-'], ['Experience', `${Math.floor(candidateFromDetail.totalExperienceMonths / 12)}y ${candidateFromDetail.totalExperienceMonths % 12}m`], ['Notice', `${candidateFromDetail.noticePeriodDays} days`], ['Qualification', candidateFromDetail.highestQualification || '-']].map(([label, value]) => <article key={label}><span>{label}</span><b>{value}</b></article>)}</div>
      <div className="candidate-profile-grid"><Card size="small" title="Experience">{detail.experience.map(row => <article key={row.id}><b>{row.jobTitle || '-'}</b><span>{row.employer || '-'} · {row.startDate?.slice(0, 7) || '-'} to {row.isCurrent ? 'Present' : row.endDate?.slice(0, 7) || '-'}</span></article>)}{!detail.experience.length && <p>No experience details.</p>}</Card><Card size="small" title="Education">{detail.education.map(row => <article key={row.id}><b>{row.qualification || '-'}</b><span>{row.institution || '-'} · {row.completionYear || '-'}</span></article>)}{!detail.education.length && <p>No education details.</p>}</Card><Card size="small" title="Certifications">{detail.certifications.map(row => <article key={row.id}><b>{row.certificationName}</b><span>{row.issuer || '-'} · {row.credentialId || 'No credential ID'}</span></article>)}{!detail.certifications.length && <p>No certifications.</p>}</Card></div>
      <Card size="small" title="Applications and ATS"><DataTable rows={detail.applications} actions={row => { const score = currentScoreFor(row.id); return <Space wrap><Button size="small" type="primary" onClick={() => navigate(`/recruitment/hiring-pipeline?positionId=${row.positionId}`)}>Open pipeline</Button>{score && canMoveApplication(row) && <Button size="small" onClick={() => setScoreOverrideDraft({ row: score, score: score.overrideScore ?? score.totalScore, reason: score.overrideReason || '' })}>Override score</Button>}{row.currentStage === 'Offer Accepted' && !row.joinedEmployeeId && <Button size="small" type="primary" onClick={() => startConversion(row)}>Create employee</Button>}{canDeleteRecruitmentData && <Popconfirm title="Delete this application permanently?" description="Interview, offer, workflow and joined records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteApplication(row.id); if (response.ok) { await load(); await refreshDetail() } }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete ${row.applicationCode}`} /></Popconfirm>}</Space> }} columns={[{ key: 'applicationCode', label: 'Application' }, { key: 'positionTitle', label: 'Position' }, { key: 'currentStage', label: 'Stage' }, { key: 'atsScore', label: 'ATS', render: row => row.atsScore == null ? '-' : row.atsScore.toFixed(1) }]} /></Card>
      <RecruitmentAtsScoreDetails scores={detail.scores} applications={detail.applications} onOverride={row => setScoreOverrideDraft({ row, score: row.overrideScore ?? row.totalScore, reason: row.overrideReason || '' })} />
      <Card size="small" title="Interview history"><DataTable rows={detail.interviews} actions={row => <Space wrap><Button size="small" icon={<FileSearchOutlined />} onClick={() => void viewApplicationResume(row.applicationId)}>Resume</Button>{canScheduleInterviews && !['Completed', 'Cancelled', 'No Show'].includes(row.status) && <Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: row })}>Update</Button>}{canScheduleInterviews && ['Scheduled', 'Rescheduled'].includes(row.status) && <Button size="small" onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: { ...row, status: 'No Show', result: 'No Show' } })}>No show</Button>}{canScheduleInterviews && ['Scheduled', 'Rescheduled'].includes(row.status) && <Button size="small" danger onClick={() => setInterviewDraft({ applicationId: row.applicationId, interview: { ...row, status: 'Cancelled', result: 'Pending' } })}>Cancel</Button>}<Button size="small" type="primary" onClick={() => setFeedbackInterview(row)}>{row.status === 'Completed' || (row.submittedPanelUserIds || []).length > 0 ? 'View feedback' : 'Panel feedback'}</Button>{canDeleteRecruitmentData && <Popconfirm title="Delete this interview?" description="Panel feedback and safe interview documents will also be removed." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteInterview(row.id); if (response.ok) { await load(); await refreshDetail() } }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete interview ${row.roundCode}`} /></Popconfirm>}</Space>} columns={[{ key: 'roundCode', label: 'Round' }, { key: 'interviewType', label: 'Type' }, { key: 'scheduledStart', label: 'Schedule', render: row => new Date(row.scheduledStart).toLocaleString('en-IN') }, { key: 'status', label: 'Status' }, { key: 'result', label: 'Result' }, { key: 'overallScore', label: 'Panel average' }]} /></Card>
      <Card size="small" title="Offer history"><DataTable rows={detail.offers} actions={row => canDeleteRecruitmentData ? <Popconfirm title="Delete this offer?" description="Its generated offer letter will also be removed. Joined applications are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteOffer(row.id); if (response.ok) { await load(); await refreshDetail() } }}><Button size="small" danger icon={<DeleteOutlined />} aria-label={`Delete offer ${row.offerNumber}`} /></Popconfirm> : null} columns={[{ key: 'offerNumber', label: 'Offer' }, { key: 'positionTitle', label: 'Position' }, { key: 'offeredCtc', label: 'CTC', render: row => `${row.currency} ${Number(row.offeredCtc).toLocaleString('en-IN')}` }, { key: 'proposedJoiningDate', label: 'Joining', render: row => new Date(row.proposedJoiningDate).toLocaleDateString('en-IN') }, { key: 'status', label: 'Status' }]} /></Card>
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
      onEditCandidate={editCandidate}
    />

    {interviewDraft && <RecruitmentInterviewEditor mode="schedule" open applications={applications} panelUsers={panelUsers} interview={interviewDraft.interview} initialDecision={interviewDraft.initialDecision} initialApplicationId={interviewDraft.applicationId} onClose={() => setInterviewDraft(null)} onSaved={refreshInterviewData} />}
    {batchInterviewOpen && <RecruitmentBatchInterviewEditor open applications={selectedBatchApplications} panelUsers={panelUsers} onClose={() => { setBatchInterviewOpen(false); setBatchInterviewApplicationIds([]) }} onSaved={refreshInterviewData} />}
    {feedbackInterview && <RecruitmentInterviewEditor mode="feedback" open applications={applications} panelUsers={panelUsers} interview={feedbackInterview} readOnly={feedbackInterview.status === 'Completed'} onClose={() => setFeedbackInterview(null)} onSaved={refreshInterviewData} />}
    <RecruitmentEditorDrawer open={!!offerDraft} eyebrow="Candidate offer" title={negotiationMode ? 'Record negotiated terms' : offerDraft?.id ? 'Edit offer' : 'Create offer'} description={negotiationMode ? 'After MoM signing, use expected CTC as the starting point and record the agreed annual CTC for HR approval.' : 'Maintain the compensation, joining date and secured offer-letter reference.'} onClose={() => { setOfferDraft(null); if (!detail) setCandidateAttachments([]) }} onSubmit={() => void saveOfferDraft()} submitText={negotiationMode ? 'Save negotiated terms' : offerDraft?.id ? 'Save changes' : 'Create offer'} width="min(720px, 96vw)">{offerDraft && <Form layout="vertical"><Form.Item label="Application"><SearchSelect disabled={Boolean(offerDraft.id)} value={offerDraft.applicationId} onChange={value => { const applicationId = Number(value); const candidate = candidates.find(row => row.id === applications.find(application => application.id === applicationId)?.candidateId); setOfferDraft({ ...offerDraft, applicationId, offeredCtc: candidate?.expectedCtc || 0 }); void loadOfferDocuments(applicationId) }} options={offerApplicationOptions} /></Form.Item>{offerDraft.applicationId > 0 && <Alert showIcon type={offerCandidate?.expectedCtc ? 'info' : 'warning'} message={offerCandidate?.expectedCtc ? `Candidate expected annual CTC: INR ${Number(offerCandidate.expectedCtc).toLocaleString('en-IN')}` : 'Expected CTC is missing. Update the candidate profile before negotiation.'} description="The amount below records the negotiated offer. Saving it does not change the candidate's expected CTC or complete HR approval." />}<Form.Item label={negotiationMode ? 'Negotiated annual CTC (INR)' : 'Offered annual CTC'}><InputNumber min={0} value={offerDraft.offeredCtc} onChange={value => setOfferDraft({ ...offerDraft, offeredCtc: Number(value || 0) })} /></Form.Item><Form.Item label="Currency"><Input value={offerDraft.currency} onChange={event => setOfferDraft({ ...offerDraft, currency: event.target.value.toUpperCase() })} /></Form.Item><Form.Item label="Proposed joining"><Input type="date" value={String(offerDraft.proposedJoiningDate || '').slice(0, 10)} onChange={event => setOfferDraft({ ...offerDraft, proposedJoiningDate: event.target.value })} /></Form.Item><Form.Item label="Expiry"><Input type="date" value={String(offerDraft.expiryDate || '').slice(0, 10)} onChange={event => setOfferDraft({ ...offerDraft, expiryDate: event.target.value })} /></Form.Item><Form.Item label="Global offer-letter document" extra="After saving this draft, use Generate letter in the Offers table. A manually uploaded current Offer Letter can also be linked here."><Select allowClear value={offerDraft.offerLetterAttachmentPublicId || undefined} onChange={offerLetterAttachmentPublicId => setOfferDraft({ ...offerDraft, offerLetterAttachmentPublicId })} options={offerDocumentOptions} placeholder="Generated or uploaded offer letter" /></Form.Item><Form.Item label="Remarks"><Input.TextArea value={offerDraft.remarks} onChange={event => setOfferDraft({ ...offerDraft, remarks: event.target.value })} /></Form.Item></Form>}</RecruitmentEditorDrawer>
    <Modal open={!!offerStatusDraft} title={offerStatusDraft ? `${offerStatusDraft.status} offer ${offerStatusDraft.row.offerNumber}` : 'Offer status'} onCancel={() => setOfferStatusDraft(null)} onOk={() => void saveOfferStatusDraft()} okButtonProps={{ disabled: !offerStatusDraft?.reason.trim() }}>{offerStatusDraft && <Form layout="vertical"><Form.Item label="Reason" required><Input.TextArea rows={4} value={offerStatusDraft.reason} onChange={event => setOfferStatusDraft({ ...offerStatusDraft, reason: event.target.value })} placeholder="This reason is retained in the candidate timeline and audit trail." /></Form.Item></Form>}</Modal>
  </div>
}
