import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Badge, Button, Card, Drawer, Dropdown, Empty, Input, Modal, Popconfirm, Progress, Select, Space, Spin, Tabs, Tag, Typography } from 'antd'
import { ArrowLeftOutlined, DeleteOutlined, DownloadOutlined, EyeOutlined, FileAddOutlined, FileSearchOutlined, FolderOpenOutlined, MoreOutlined, ReloadOutlined, RobotOutlined, SettingOutlined, UploadOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { useAuthSession } from './AuthGate'
import DataTable from './DataTable'
import RecruitmentAtsAdmin from './RecruitmentAtsAdmin'
import RecruitmentAtsScoreDetails from './RecruitmentAtsScoreDetails'
import RecruitmentGlobalTalentPool from './RecruitmentGlobalTalentPool'
import RecruitmentResumeIntake, { type RecruitmentResumeIntakeMode } from './RecruitmentResumeIntake'
import RecruitmentSeedPackModal from './RecruitmentSeedPackModal'
import RecruitmentWorkspaceLayout from './RecruitmentWorkspaceLayout'
import { downloadAttachmentBlob, openAttachmentWithTicket } from '../services/attachmentService'
import { getClients } from '../services/payrollService'
import { getDropdowns } from '../services/settingsService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import { deleteApplication, getApplications, getCandidate, scoreApplication } from '../services/recruitmentTalentService'
import { downloadCandidateSeedTemplate } from '../services/recruitmentSeedPackService'
import type { Client, Drop, RecruitmentCandidateApplication, RecruitmentCandidateDetail, RecruitmentOpenPosition } from '../types/payroll'
import './RecruitmentResumeIntake.css'
import './RecruitmentAtsWorkspace.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  initialPositionId?: number
  initialJobPostingId?: number | null
  initialUploadMode?: RecruitmentResumeIntakeMode
  onNavigationStateChange?: (state: { dirty: boolean; busy: boolean }) => void
}
type IntakeLaunch = { mode: RecruitmentResumeIntakeMode; clientId: number; positionId: number; jobPostingId?: number | null }
type CandidateSelection = { candidateId: number; applicationId?: number }

export default function RecruitmentAtsWorkspace({ initialClientId = 0, clientScopeManaged = false, initialPositionId = 0, initialJobPostingId = null, initialUploadMode, onNavigationStateChange }: Props) {
  const session = useAuthSession()
  const navigate = useNavigate()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [applications, setApplications] = useState<RecruitmentCandidateApplication[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [positionId, setPositionId] = useState(initialPositionId)
  const [stage, setStage] = useState('')
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [scoringId, setScoringId] = useState(0)
  const [view, setView] = useState(initialUploadMode ? 'upload' : 'applications')
  const [intake, setIntake] = useState<IntakeLaunch | null>(() => initialUploadMode ? { mode: initialUploadMode, clientId: initialClientId, positionId: initialPositionId, jobPostingId: initialJobPostingId } : null)
  const [selection, setSelection] = useState<CandidateSelection | null>(null)
  const [detail, setDetail] = useState<RecruitmentCandidateDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailTab, setDetailTab] = useState('evidence')
  const [preview, setPreview] = useState<{ url: string; type: string } | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [configurationOpen, setConfigurationOpen] = useState(() => typeof window !== 'undefined' && new URLSearchParams(window.location.search).get('manage') === '1')
  const [seedPackOpen, setSeedPackOpen] = useState(false)
  const [intakeDirty, setIntakeDirty] = useState(false)
  const [intakeBusy, setIntakeBusy] = useState(false)
  const listRequest = useRef(0)
  const detailRequest = useRef(0)
  const scope = useRef({ clientId, positionId })
  scope.current = { clientId, positionId }

  useEffect(() => { onNavigationStateChange?.({ dirty: intakeDirty, busy: intakeBusy }) }, [intakeDirty, intakeBusy, onNavigationStateChange])
  useEffect(() => () => { onNavigationStateChange?.({ dirty: false, busy: false }) }, [onNavigationStateChange])
  useEffect(() => {
    if (!intakeDirty && !intakeBusy) return
    const protectUpload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', protectUpload)
    return () => window.removeEventListener('beforeunload', protectUpload)
  }, [intakeDirty, intakeBusy])

  useEffect(() => {
    let active = true
    void Promise.all([getClients(), getRecruitmentOpenPositions(), getDropdowns()]).then(([clientRows, positionRows, dropdownRows]) => {
      if (!active) return
      setClients(clientRows)
      setPositions(positionRows)
      setDropdowns(dropdownRows)
      if (!initialClientId) {
        const position = positionRows.find(row => row.id === initialPositionId)
        if (position) setClientId(position.clientId)
        else if (clientRows.length === 1) setClientId(clientRows[0].id)
      }
    })
    return () => { active = false }
  }, [initialClientId, initialPositionId])

  useEffect(() => {
    setClientId(initialClientId)
    setPositionId(initialPositionId)
    setSelection(null)
    setDetail(null)
    ++detailRequest.current
    if (initialUploadMode) {
      setIntake({ mode: initialUploadMode, clientId: initialClientId, positionId: initialPositionId, jobPostingId: initialJobPostingId })
      setView('upload')
    }
  }, [initialClientId, initialPositionId, initialJobPostingId, initialUploadMode])

  const load = useCallback(async () => {
    const currentPositionId = scope.current.positionId
    const request = ++listRequest.current
    setLoading(true)
    try {
      const rows = await getApplications(currentPositionId ? { positionId: currentPositionId } : {})
      if (request === listRequest.current && currentPositionId === scope.current.positionId) setApplications(rows)
    } finally {
      if (request === listRequest.current) setLoading(false)
    }
  }, [])
  useEffect(() => {
    setApplications([])
    void load()
    return () => { ++listRequest.current }
  }, [load, positionId])

  const availablePositions = useMemo(() => positions.filter(row => !clientId || row.clientId === clientId), [positions, clientId])
  const selectedPosition = availablePositions.find(row => row.id === positionId)
  const scopedApplications = useMemo(() => applications.filter(row => (!clientId || row.clientId === clientId) && (!positionId || row.positionId === positionId)), [applications, clientId, positionId])
  const stages = useMemo(() => Array.from(new Set(scopedApplications.map(row => row.currentStage).filter(Boolean))).sort(), [scopedApplications])
  const rows = useMemo(() => scopedApplications.filter(row => {
    if (stage && row.currentStage !== stage) return false
    if (!query.trim()) return true
    return `${row.applicationCode} ${row.candidateName} ${row.candidateEmail} ${row.candidatePhone} ${row.positionCode} ${row.positionTitle}`.toLowerCase().includes(query.trim().toLowerCase())
  }), [scopedApplications, stage, query])
  const selectedApplication = detail?.applications.find(row => row.id === selection?.applicationId)
  const detailApplications = useMemo(() => (detail?.applications || []).filter(row => (!clientId || row.clientId === clientId) && (!positionId || row.positionId === positionId)), [detail, clientId, positionId])
  const selectedScores = useMemo(() => (detail?.scores || []).filter(row => row.applicationId === selectedApplication?.id), [detail, selectedApplication?.id])
  // An application's recorded resume must never silently switch to another application or candidate version.
  const selectedResume = selection?.applicationId
    ? detail?.resumes.find(row => row.id === selectedApplication?.resumeId)
    : detail?.resumes.find(row => row.isPrimary) || detail?.resumes[0]

  useEffect(() => {
    let active = true
    setPreview(null)
    setPreviewLoading(false)
    if (detailTab !== 'resume' || !selectedResume) return
    setPreviewLoading(true)
    void downloadAttachmentBlob(selectedResume.attachmentPublicId).then(response => {
      if (!active || !response.ok || !response.blob) return
      const type = response.blob.type || ''
      const pdf = /\.pdf$/i.test(selectedResume.originalFileName)
      const blob = pdf && type === 'application/octet-stream' ? new Blob([response.blob], { type: 'application/pdf' }) : response.blob
      setPreview({ url: URL.createObjectURL(blob), type: blob.type })
    }).finally(() => { if (active) setPreviewLoading(false) })
    return () => { active = false }
  }, [detailTab, selectedResume?.attachmentPublicId])
  useEffect(() => () => { if (preview) URL.revokeObjectURL(preview.url) }, [preview])
  useEffect(() => () => { ++detailRequest.current }, [])

  const closeDetail = () => { ++detailRequest.current; setSelection(null); setDetail(null); setDetailLoading(false); setPreview(null) }
  const openCandidate = async (candidateId: number, applicationId?: number) => {
    const request = ++detailRequest.current
    setView('applications')
    setSelection({ candidateId, applicationId })
    setDetailLoading(true)
    setDetail(null)
    setDetailTab('evidence')
    try {
      const candidate = await getCandidate(candidateId)
      if (request !== detailRequest.current) return
      setDetail(candidate)
      if (!applicationId) {
        const contextual = candidate?.applications.filter(row => (!clientId || row.clientId === clientId) && (!positionId || row.positionId === positionId)) || []
        if (contextual.length === 1) setSelection({ candidateId, applicationId: contextual[0].id })
      }
    } finally {
      if (request === detailRequest.current) setDetailLoading(false)
    }
  }
  const openEvidence = (row: RecruitmentCandidateApplication) => openCandidate(row.candidateId, row.id)
  const launchIntake = (mode: RecruitmentResumeIntakeMode, application?: RecruitmentCandidateApplication) => {
    if (intakeBusy) { closeDetail(); setView('upload'); return }
    const next = { mode, clientId: application?.clientId || clientId, positionId: application?.positionId || positionId, jobPostingId: application?.jobPostingId ?? (positionId === initialPositionId ? initialJobPostingId : null) }
    const changedContext = intake && (intake.clientId !== next.clientId || intake.positionId !== next.positionId || intake.jobPostingId !== next.jobPostingId)
    const show = () => { if (!intakeDirty || changedContext) setIntake(next); closeDetail(); setView('upload') }
    if (intakeDirty && changedContext) Modal.confirm({ title: 'Change the resume job?', content: 'Selected files have not been uploaded. Changing the job will clear those files.', okText: 'Change job', onOk: show })
    else show()
  }
  const recalculate = async (row: RecruitmentCandidateApplication) => {
    if (scoringId) return
    const request = detailRequest.current
    setScoringId(row.id)
    try {
      const response = await scoreApplication(row.id)
      if (response.ok) {
        await load()
        if (request === detailRequest.current && selection?.applicationId === row.id) await openCandidate(row.candidateId, row.id)
      }
    } finally { setScoringId(0) }
  }
  const changeScope = (nextClientId: number, nextPositionId: number) => {
    const change = () => {
      closeDetail()
      setClientId(nextClientId)
      setPositionId(nextPositionId)
      setStage('')
      if (view === 'upload') setIntake({ mode: intake?.mode || 'bulk', clientId: nextClientId, positionId: nextPositionId, jobPostingId: null })
      else setIntake(null)
    }
    if (nextClientId === clientId && nextPositionId === positionId) return
    if (intakeBusy) return
    if (intakeDirty) Modal.confirm({ title: 'Change the resume job?', content: 'Selected files have not been uploaded. Changing the job will clear those files.', okText: 'Change job', onOk: change })
    else change()
  }
  const setScoringConfigurationOpen = (open: boolean) => {
    setConfigurationOpen(open)
    const params = new URLSearchParams(window.location.search)
    if (open) params.set('manage', '1')
    else params.delete('manage')
    const queryString = params.toString()
    navigate(`${window.location.pathname}${queryString ? `?${queryString}` : ''}`, { replace: true })
  }
  const changeView = (key: string) => {
    if (key === 'upload') { if (intake) { closeDetail(); setView('upload') } else launchIntake('bulk') }
    else { setView(key); closeDetail() }
  }
  const removeApplication = async (row: RecruitmentCandidateApplication) => {
    const response = await deleteApplication(row.id)
    if (!response.ok) return
    if (selection?.applicationId === row.id) closeDetail()
    await load()
  }

  return <div className="ats-screening-workspace ats-focused-workspace" data-testid="ats-screening-workspace">
    <section className="ats-context-bar">
      <div className="ats-context-title"><span className="ats-context-icon"><RobotOutlined /></span><div><h3>Screen candidates</h3><p>Choose a job, add resumes and review the matching evidence.</p></div></div>
      <div className="ats-context-selectors">
        {!clientScopeManaged && <Select aria-label="ATS client" disabled={intakeBusy} showSearch optionFilterProp="label" value={clientId || undefined} placeholder="All permitted clients" allowClear options={clients.map(row => ({ value: row.id, label: `${row.code} - ${row.name}` }))} onChange={value => changeScope(value || 0, 0)} />}
        <Select data-testid="ats-job-context" aria-label="ATS job" disabled={intakeBusy} showSearch optionFilterProp="label" value={positionId || undefined} placeholder="Choose a job / JD" allowClear options={availablePositions.map(row => ({ value: row.id, label: `${row.positionCode} - ${row.positionTitle}` }))} onChange={value => { const next = availablePositions.find(row => row.id === value); changeScope(next?.clientId || clientId, value || 0) }} />
        <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void load()} aria-label="Refresh applications" />
        <Dropdown menu={{ items: [
          { key: 'template', label: <span data-testid="candidate-seed-template">Candidate template</span>, icon: <DownloadOutlined />, onClick: () => { void downloadCandidateSeedTemplate() } },
          { key: 'seed', label: <span data-testid="candidate-seed-import">Seed candidates</span>, icon: <UploadOutlined />, onClick: () => setSeedPackOpen(true) },
          ...(canDelete ? [{ key: 'rules', label: 'Manage scoring rules', icon: <SettingOutlined />, onClick: () => setScoringConfigurationOpen(true) }] : []),
        ] }} trigger={['click']}><Button icon={<MoreOutlined />} aria-label="More screening actions">More</Button></Dropdown>
      </div>
      {selectedPosition && <div className="ats-context-summary" data-testid="ats-job-summary"><Tag color="blue">{selectedPosition.clientName}</Tag><strong>{selectedPosition.positionTitle}</strong><span>{selectedPosition.department || 'Department not specified'} · {selectedPosition.jobLocation || 'Location not specified'}</span><Tag>Latest JD: {selectedPosition.jobDescriptionStatus || 'Not prepared'}{selectedPosition.latestJobDescriptionVersionNumber ? ` v${selectedPosition.latestJobDescriptionVersionNumber}` : ''}</Tag><span className="ats-context-scoring-note">ATS uses the approved job requirements or the application's posting. The evidence report shows the assessed JD version.</span></div>}
    </section>

    <RecruitmentWorkspaceLayout className="ats-focus-layout" ariaLabel="Candidate screening workspace" scrollKey={`${selection?.applicationId || 0}:${selection?.candidateId || 0}:${detailTab}`}
      navigation={[
        { key: 'applications', label: 'Applications', icon: <FileSearchOutlined />, description: 'Review scores and resumes', badge: scopedApplications.length },
        { key: 'upload', label: 'Add resumes', icon: <UploadOutlined />, description: 'Upload one or multiple files' },
        { key: 'pool', label: 'Resume bank', icon: <FolderOpenOutlined />, description: 'Search existing talent' },
      ]} activeKey={view} onChange={changeView}
      sidebar={view === 'applications' && selection ? <div className="ats-candidate-list" aria-label="Candidate applications"><div className="ats-candidate-list-heading"><strong>Applications</strong><Badge count={rows.length} showZero color="#5b4ce6" /></div>{rows.map(row => <button type="button" key={row.id} data-testid={`ats-candidate-application-${row.id}`} className={selection.applicationId === row.id ? 'is-active' : ''} onClick={() => void openEvidence(row)} aria-current={selection.applicationId === row.id ? 'true' : undefined}><strong>{row.candidateName}</strong><span>{row.positionTitle}</span><small>{row.currentStage} · {row.atsScore == null ? row.scoreStatus || 'Not scored' : `${row.atsScore.toFixed(1)} / 100`}</small></button>)}</div> : undefined}>
      {intake && <div className="ats-upload-content" hidden={view !== 'upload'}><div className="ats-detail-toolbar"><Button icon={<ArrowLeftOutlined />} onClick={() => setView('applications')}>Applications</Button><Typography.Text strong>Add resumes</Typography.Text></div>{!intake.positionId ? <Alert type="info" showIcon message="Choose a job above to add resumes." description="The approved job context will be filled automatically. To store resumes without a job, open Resume bank." /> : <RecruitmentResumeIntake key={`${intake.clientId}-${intake.positionId}-${intake.jobPostingId || 0}`} open embedded initialMode={intake.mode} initialClientId={intake.clientId} initialPositionId={intake.positionId} initialJobPostingId={intake.jobPostingId} contextClients={clients} contextPositions={positions} onChangeJob={() => document.querySelector<HTMLInputElement>('[data-testid="ats-job-context"] input')?.focus()} onPendingChange={setIntakeDirty} onBusyChange={setIntakeBusy} onClose={() => setView('applications')} onCompleted={async () => { await load() }} />}</div>}

      {view === 'pool' && <RecruitmentGlobalTalentPool key={`${clientId}-${positionId}`} initialPositionId={positionId} clientId={clientId} contextPositions={positions} embeddedPreview onViewCandidate={id => openCandidate(id)} onChanged={load} />}

      {view === 'applications' && !selection && <Card size="small" className="ats-workbench-table-card" title="Candidate applications" extra={<Space><Button icon={<FileAddOutlined />} onClick={() => launchIntake('single')}>Single resume</Button><Button type="primary" icon={<UploadOutlined />} onClick={() => launchIntake('bulk')}>Bulk resumes</Button></Space>}>
        <div className="ats-application-filters">
          <Input.Search value={query} allowClear placeholder="Candidate, email, application or position" onChange={event => setQuery(event.target.value)} />
          <Select aria-label="Application stage" value={stage || undefined} placeholder="All pipeline stages" allowClear options={stages.map(value => ({ value, label: value }))} onChange={value => setStage(value || '')} />
        </div>
        <div data-testid="ats-applications-table">
          {loading && !applications.length ? <div className="ats-workbench-loading"><Spin /><span>Loading applications…</span></div> : <DataTable<RecruitmentCandidateApplication>
            rows={rows} hideSearch exportFileName="ats-screening-applications" emptyText="No applications match this view. Add resumes or search the resume bank."
            actions={row => <Space size={4} wrap>
              <Button size="small" icon={<EyeOutlined />} onClick={() => void openEvidence(row)}>Evidence</Button>
              <Button size="small" loading={scoringId === row.id} disabled={!row.resumeId || Boolean(scoringId && scoringId !== row.id)} onClick={() => void recalculate(row)}>Score</Button>
              <Button size="small" icon={<UploadOutlined />} onClick={() => launchIntake('single', row)}>Resume</Button>
              {canDelete && <Popconfirm title="Delete this application?" description="ATS scores and safe pipeline data will be removed. Interviews, offers and joined records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => removeApplication(row)}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
            </Space>}
            columns={[
              { key: 'candidateName', label: 'Candidate', width: '220px', render: row => <div className="ats-primary-cell"><b>{row.candidateName}</b><span>{row.candidateEmail || row.candidatePhone || row.candidateCode}</span></div>, value: row => row.candidateName },
              { key: 'applicationCode', label: 'Application', width: '150px', render: row => <div className="ats-primary-cell"><b>{row.applicationCode}</b><span>{new Date(row.appliedAt).toLocaleDateString('en-IN')}</span></div> },
              { key: 'positionTitle', label: 'Job / JD context', width: '220px', render: row => <div className="ats-primary-cell"><b>{row.positionTitle}</b><span>{row.positionCode} · {row.clientName}</span></div>, value: row => row.positionTitle },
              { key: 'currentStage', label: 'Pipeline stage', width: '155px', render: row => <Tag color="blue">{row.currentStage}</Tag> },
              { key: 'atsScore', label: 'ATS score', width: '155px', value: row => row.atsScore ?? '', render: row => row.atsScore == null ? <Tag color="orange">{row.scoreStatus || 'Not scored'}</Tag> : <div className="ats-score-cell"><Progress percent={Math.max(0, Math.min(100, row.atsScore))} size="small" showInfo={false} /><b>{row.atsScore.toFixed(1)}</b></div> },
              { key: 'sourceType', label: 'Source', width: '145px' },
              { key: 'recruiterName', label: 'Recruiter', width: '170px', render: row => row.recruiterName || '-' },
            ]} />}
        </div>
      </Card>}

      {view === 'applications' && selection && <section className="ats-candidate-detail" data-testid="ats-candidate-detail">
        <div className="ats-detail-toolbar"><Button icon={<ArrowLeftOutlined />} onClick={closeDetail}>All applications</Button>{selectedApplication && <Space wrap><Button onClick={() => navigate(`/recruitment/hiring-pipeline?clientId=${selectedApplication.clientId}&positionId=${selectedApplication.positionId}&flow=candidates`)}>Hiring pipeline</Button><Button icon={<UploadOutlined />} onClick={() => launchIntake('single', selectedApplication)}>Add resume</Button><Button type="primary" icon={<RobotOutlined />} loading={scoringId === selectedApplication.id} disabled={!selectedApplication.resumeId || Boolean(scoringId && scoringId !== selectedApplication.id)} onClick={() => void recalculate(selectedApplication)}>Run ATS</Button></Space>}</div>
        {detailLoading ? <div className="ats-workbench-loading"><Spin /><span>Loading candidate evidence…</span></div> : !detail ? <Empty description="Candidate details are unavailable." /> : <>
          <section className="ats-candidate-summary" data-testid="ats-candidate-summary"><div className="ats-candidate-avatar">{initials(detail.candidate.candidateName)}</div><div><Typography.Title level={3}>{detail.candidate.candidateName}</Typography.Title><Typography.Paragraph>{detail.candidate.email || detail.candidate.phone || 'Contact pending'}</Typography.Paragraph></div><Space wrap><Tag color="blue">{detail.candidate.profileStatus}</Tag>{selectedApplication && <Tag>{selectedApplication.currentStage}</Tag>}</Space></section>
          <div className="ats-candidate-facts">{[['Current role', detail.candidate.currentTitle || 'Not specified'], ['Location', detail.candidate.currentLocation || 'Not specified'], ['Experience', `${Math.floor(detail.candidate.totalExperienceMonths / 12)}y ${detail.candidate.totalExperienceMonths % 12}m`], ['Phone', detail.candidate.phone || 'Not specified']].map(([label, value]) => <article key={label}><span>{label}</span><b>{value}</b></article>)}</div>
          {detailApplications.length > 1 && <div className="ats-detail-application-select"><Typography.Text strong>Application</Typography.Text><Select aria-label="Candidate application" placeholder="Choose the application to review" value={selectedApplication?.id} onChange={id => { setSelection({ candidateId: detail.candidate.id, applicationId: id }); setDetailTab('evidence') }} options={detailApplications.map(row => ({ value: row.id, label: `${row.applicationCode} · ${row.positionTitle}` }))} /></div>}
          {selectedApplication && <div className="ats-selected-application"><strong>{selectedApplication.positionTitle}</strong><span>{selectedApplication.applicationCode} · {selectedApplication.clientName}</span></div>}
          <Tabs activeKey={detailTab} onChange={setDetailTab} items={[
            { key: 'evidence', label: <span><RobotOutlined /> ATS evidence</span>, children: selectedApplication ? <RecruitmentAtsScoreDetails key={selectedApplication.id} display="inline" scores={selectedScores} applications={[selectedApplication]} /> : <Alert type="info" showIcon message={detailApplications.length > 1 ? 'Choose an application to view the correct job assessment.' : 'This resume is not linked to a job in the current view.'} description="Use Resume bank to choose an approved job and run ATS or select the candidate directly." /> },
            { key: 'resume', label: <span><FileSearchOutlined /> Original resume</span>, children: <div className="ats-resume-panel">{!selectedResume ? <Empty description="No resume is linked to this application." /> : <><div className="ats-resume-heading"><strong>{selectedResume.originalFileName}</strong><Button icon={<DownloadOutlined />} onClick={() => void openAttachmentWithTicket(selectedResume.attachmentPublicId, 'Download')}>Download</Button></div>{previewLoading ? <div className="ats-workbench-loading"><Spin /><span>Opening secure resume…</span></div> : preview && /^(application\/pdf|text\/plain|image\/(png|jpeg|gif|webp))/.test(preview.type) ? <iframe data-testid="ats-resume-preview" src={preview.url} title={selectedResume.originalFileName} /> : <Alert type="info" showIcon message="Download the original file to view this format." />}</>}</div> },
          ]} />
        </>}
      </section>}
    </RecruitmentWorkspaceLayout>

    <Drawer className="recruitment-ats-manager-drawer" open={configurationOpen && canDelete} onClose={() => setScoringConfigurationOpen(false)} width="min(1480px, 98vw)" destroyOnClose title="Manage ATS scoring rules"><RecruitmentAtsAdmin clients={clients} dropdowns={dropdowns} onDropdownsChange={setDropdowns} initialClientId={clientId} /></Drawer>
    <RecruitmentSeedPackModal open={seedPackOpen} mode="candidate" onClose={() => setSeedPackOpen(false)} onImported={load} />
  </div>
}

function initials(name: string) {
  return String(name || 'Candidate').split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase()).join('') || 'C'
}
