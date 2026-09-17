import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Button, Card, Col, Descriptions, Drawer, Empty, Form, Input, InputNumber, Modal, Progress, Radio, Row,
  Select, Statistic, Switch, Tag, Typography, Upload,
} from 'antd'
import type { UploadFile, UploadProps } from 'antd'
import { EditOutlined, FileSearchOutlined, InboxOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import { getClients } from '../services/payrollService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import { getRecruitmentJobDescriptions, getRecruitmentJobPostings } from '../services/recruitmentOrchestrationService'
import { intakeRecruitmentResumes, previewRecruitmentResume, type RecruitmentResumeReviewedDraft, type RecruitmentResumeUploadProgress } from '../services/recruitmentTalentService'
import type { Client, RecruitmentOpenPosition, RecruitmentResumeIntakeItem, RecruitmentResumeIntakeResult } from '../types/payroll'
import type { RecruitmentJobDescriptionVersion, RecruitmentJobPosting } from '../types/recruitmentOrchestration'
import { useToast } from './ToastProvider'
import './RecruitmentResumeIntake.css'

export type RecruitmentResumeIntakeMode = 'single' | 'bulk'

type Props = {
  open: boolean
  onClose: () => void
  initialMode?: RecruitmentResumeIntakeMode
  initialClientId?: number
  initialPositionId?: number
  initialJobPostingId?: number | null
  talentPoolOnly?: boolean
  onCompleted?: (result: RecruitmentResumeIntakeResult) => void | Promise<void>
  embedded?: boolean
  contextClients?: Client[]
  contextPositions?: RecruitmentOpenPosition[]
  onChangeJob?: () => void
  onPendingChange?: (pending: boolean) => void
  onBusyChange?: (busy: boolean) => void
  title?: string
  description?: string
  allowBulk?: boolean
  submitLabel?: string
  onEditCandidate?: (candidateId: number) => void | Promise<void>
}

const acceptedResumeTypes = '.pdf,.docx,.rtf,.txt'
const sourceOptions = ['Direct Sourcing', 'Job Portal', 'Agency', 'Employee Referral', 'Internal Database'].map(value => ({ value, label: value }))

export default function RecruitmentResumeIntake({
  open,
  onClose,
  initialMode = 'single',
  initialClientId = 0,
  initialPositionId = 0,
  initialJobPostingId = null,
  talentPoolOnly = false,
  onCompleted,
  embedded = false,
  contextClients,
  contextPositions,
  onChangeJob,
  onPendingChange,
  onBusyChange,
  title,
  description,
  allowBulk = true,
  submitLabel,
  onEditCandidate,
}: Props) {
  const notify = useToast()
  const [mode, setMode] = useState<RecruitmentResumeIntakeMode>(initialMode)
  const [clients, setClients] = useState<Client[]>(contextClients || [])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>(contextPositions || [])
  const [postings, setPostings] = useState<RecruitmentJobPosting[]>([])
  const [descriptions, setDescriptions] = useState<RecruitmentJobDescriptionVersion[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [positionId, setPositionId] = useState(initialPositionId)
  const [jobPostingId, setJobPostingId] = useState<number | null>(initialJobPostingId)
  const [jobDescriptionId, setJobDescriptionId] = useState<number | null>(null)
  const [sourceType, setSourceType] = useState('Direct Sourcing')
  const [fileList, setFileList] = useState<UploadFile[]>([])
  const [loadingContext, setLoadingContext] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [previewing, setPreviewing] = useState(false)
  const [drafts, setDrafts] = useState<RecruitmentResumeReviewedDraft[]>([])
  const [editingDraftIndex, setEditingDraftIndex] = useState<number | null>(null)
  const [editingDraft, setEditingDraft] = useState<RecruitmentResumeReviewedDraft | null>(null)
  const [progress, setProgress] = useState(0)
  const [progressDetail, setProgressDetail] = useState<RecruitmentResumeUploadProgress | null>(null)
  const [result, setResult] = useState<RecruitmentResumeIntakeResult | null>(null)
  const [forceUpload, setForceUpload] = useState(false)
  const [contextExpanded, setContextExpanded] = useState(!initialPositionId)
  const [resolvingPostings, setResolvingPostings] = useState(false)
  const [resolvingDescription, setResolvingDescription] = useState(false)

  useEffect(() => {
    if (!open) return
    setMode(initialMode)
    setClientId(initialClientId)
    setPositionId(initialPositionId)
    setJobPostingId(initialJobPostingId)
    setJobDescriptionId(null)
    setContextExpanded(!initialPositionId)
    setFileList([])
    setForceUpload(false)
    setProgress(0)
    setProgressDetail(null)
    setResult(null)
    setDrafts([])
    setEditingDraftIndex(null)
    setEditingDraft(null)
  }, [open, initialClientId, initialPositionId, initialJobPostingId])

  useEffect(() => {
    setMode(initialMode)
    if (initialMode === 'single') setFileList(current => current.slice(0, 1))
    setDrafts([])
  }, [initialMode])
  useEffect(() => { onPendingChange?.(Boolean(fileList.length && !result) || uploading || previewing) }, [fileList.length, result, uploading, previewing, onPendingChange])
  useEffect(() => { onBusyChange?.(uploading || previewing) }, [uploading, previewing, onBusyChange])
  useEffect(() => () => { onPendingChange?.(false); onBusyChange?.(false) }, [onPendingChange, onBusyChange])

  useEffect(() => {
    if (!open) return
    if (talentPoolOnly) {
      setClientId(0); setPositionId(0); setJobPostingId(null); setJobDescriptionId(null); setDescriptions([]); setResult(null); setDrafts([])
      return
    }
    let active = true
    setLoadingContext(true)
    void Promise.all([contextClients ? Promise.resolve(contextClients) : getClients(), contextPositions ? Promise.resolve(contextPositions) : getRecruitmentOpenPositions()])
      .then(([clientRows, positionRows]) => {
        if (!active) return
        setClients(clientRows)
        setPositions(positionRows)
        if (!initialClientId) {
          const initialPosition = positionRows.find(row => row.id === initialPositionId)
          if (initialPosition) setClientId(initialPosition.clientId)
          else if (clientRows.length === 1) setClientId(clientRows[0].id)
        }
      })
      .finally(() => { if (active) setLoadingContext(false) })
    return () => { active = false }
  }, [open, initialClientId, initialPositionId, talentPoolOnly, contextClients, contextPositions])

  useEffect(() => {
    setPostings([])
    setResolvingPostings(false)
    if (!open || talentPoolOnly || !clientId) return
    let active = true
    setResolvingPostings(true)
    void getRecruitmentJobPostings(clientId).then(rows => {
      if (!active) return
      setPostings(rows)
      if (initialJobPostingId && rows.some(row => row.id === initialJobPostingId && row.clientId === clientId && (!initialPositionId || row.positionId === initialPositionId))) {
        const posting = rows.find(row => row.id === initialJobPostingId)!
        setJobPostingId(posting.id)
        setPositionId(posting.positionId)
        setJobDescriptionId(posting.jobDescriptionVersionId || null)
      }
    }).finally(() => { if (active) setResolvingPostings(false) })
    return () => { active = false }
  }, [open, clientId, initialPositionId, initialJobPostingId, talentPoolOnly])

  useEffect(() => {
    setDescriptions([])
    setJobDescriptionId(null)
    setResolvingDescription(false)
    if (!open || talentPoolOnly || !positionId) return
    const position = positions.find(row => row.id === positionId)
    if (!position?.requisitionId || position.clientId !== clientId) return
    let active = true
    setResolvingDescription(true)
    void getRecruitmentJobDescriptions(position.requisitionId).then(rows => {
      if (!active) return
      const approved = rows.filter(row => row.status === 'Approved').sort((a, b) => b.versionNumber - a.versionNumber)
      setDescriptions(approved)
      const posting = postings.find(row => row.id === jobPostingId)
      // The open-position list exposes its newest JD, not the approved JD pointer
      // used by scoring. Only a posting gives us an exact version here.
      setJobDescriptionId(posting?.jobDescriptionVersionId || null)
    }).finally(() => { if (active) setResolvingDescription(false) })
    return () => { active = false }
  }, [open, clientId, positionId, positions, postings, jobPostingId, talentPoolOnly])

  const availablePositions = useMemo(() => positions
    .filter(row => !clientId || row.clientId === clientId)
    .filter(row => !['Closed', 'Cancelled', 'Filled'].includes(row.status)), [positions, clientId])
  const availablePostings = useMemo(() => postings
    .filter(row => !positionId || row.positionId === positionId)
    .filter(row => !['Closed', 'Cancelled'].includes(row.status)), [postings, positionId])
  const selectedPosition = positions.find(row => row.id === positionId)
  const selectedPosting = postings.find(row => row.id === jobPostingId)
  const parsingDisabled = selectedPosting?.enableResumeParsing === false
  const selectedDescription = descriptions.find(row => row.id === jobDescriptionId)
  const selectedFiles = useMemo<File[]>(() => fileList.flatMap(row => row.originFileObj ? [row.originFileObj as File] : []), [fileList])
  const busy = uploading || previewing
  const draftsReady = drafts.length > 0 && drafts.length === selectedFiles.length && drafts.every((draft, index) => draft.file === selectedFiles[index])

  const clientOptions = clients.map(row => ({ value: row.id, label: `${row.code || 'CLIENT'} - ${row.name}` }))
  const positionOptions = availablePositions.map(row => ({
    value: row.id,
    label: `${row.positionCode} - ${row.positionTitle} · ${row.department || 'No department'}`,
  }))
  const postingOptions = [
    { value: 0, label: 'Use approved position / JD (no public posting)' },
    ...availablePostings.map(row => ({ value: row.id, label: `${row.publicTitle} · ${row.status}` })),
  ]
  const descriptionOptions = descriptions.map(row => ({ value: row.id, label: `v${row.versionNumber} · ${row.title}` }))

  const changeMode = (nextMode: RecruitmentResumeIntakeMode) => {
    setMode(nextMode)
    setFileList(current => nextMode === 'single' ? current.slice(0, 1) : current)
    setResult(null)
    setDrafts([])
  }
  const changeClient = (value: number) => {
    setClientId(value)
    setPositionId(0)
    setJobPostingId(null)
    setJobDescriptionId(null)
    setDescriptions([])
    setResult(null)
    setDrafts([])
  }
  const changePosition = (value: number) => {
    setPositionId(value)
    setDescriptions([])
    setContextExpanded(false)
    setJobPostingId(null)
    setJobDescriptionId(null)
    setResult(null)
    setDrafts([])
  }
  const changePosting = (value: number) => {
    const nextId = value || null
    const posting = postings.find(row => row.id === nextId)
    setJobPostingId(nextId)
    if (posting) {
      setPositionId(posting.positionId)
      setJobDescriptionId(posting.jobDescriptionVersionId || null)
    }
    setResult(null)
    setDrafts([])
  }

  const uploadProps: UploadProps = {
    accept: acceptedResumeTypes,
    multiple: mode === 'bulk',
    maxCount: mode === 'single' ? 1 : 50,
    fileList,
    beforeUpload: () => false,
    onChange: info => {
      const next = info.fileList
        .filter(row => row.originFileObj && row.status !== 'error')
        .slice(0, mode === 'single' ? 1 : 50)
      setFileList(next)
      setResult(null)
      setDrafts([])
    },
    onRemove: file => { setFileList(current => current.filter(row => row.uid !== file.uid)); setDrafts([]); return true },
    disabled: busy,
  }

  const validateSelection = () => {
    if (!talentPoolOnly && !clientId) { notify('Select a client.', 'error'); return false }
    if (!talentPoolOnly && (!selectedPosition || selectedPosition.clientId !== clientId)) { notify('Select a job position for this client.', 'error'); return false }
    if (loadingContext || resolvingPostings || resolvingDescription || busy) return false
    if (!selectedFiles.length) { notify(`Select ${mode === 'single' ? 'a resume' : 'one or more resumes'}.`, 'error'); return false }
    return true
  }

  const preview = async () => {
    if (!validateSelection()) return
    setPreviewing(true)
    setProgress(0)
    setProgressDetail(null)
    setDrafts([])
    setResult(null)
    const nextDrafts: RecruitmentResumeReviewedDraft[] = []
    try {
      for (let index = 0; index < selectedFiles.length; index++) {
        const file = selectedFiles[index]
        setProgress(Math.round(index / selectedFiles.length * 100))
        const response = await previewRecruitmentResume({ clientId, positionId, jobPostingId, talentPoolOnly, enableParsing: !parsingDisabled, file })
        if (!response.ok) {
          notify(`${file.name}: ${response.error || 'Preview could not be prepared.'}`, 'error')
          setDrafts([])
          return
        }
        nextDrafts.push({ ...response.data, file })
        setDrafts([...nextDrafts])
        setProgress(Math.round((index + 1) / selectedFiles.length * 100))
      }
      notify(`${nextDrafts.length} resume draft(s) ready. Review them, then use Final upload.`, 'success')
    } finally {
      setPreviewing(false)
    }
  }

  const submit = async () => {
    if (!validateSelection()) return
    if (!draftsReady) return notify('Run Preview & parse before final upload.', 'error')
    if (drafts.some(draft => !draft.firstName.trim())) return notify('First name is required for every candidate draft.', 'error')
    setUploading(true)
    setProgress(0)
    setProgressDetail({ percent: 0, completedFiles: 0, totalFiles: selectedFiles.length, activeFrom: 1, activeTo: Math.min(1, selectedFiles.length), phase: 'uploading' })
    setResult(null)
    try {
      const response = await intakeRecruitmentResumes({ clientId, positionId, jobPostingId, talentPoolOnly, forceUpload: forceUpload || parsingDisabled, deferAtsScoring: mode === 'bulk', sourceType, files: selectedFiles, drafts }, detail => { setProgress(detail.percent); setProgressDetail(detail) })
      if (!response.ok) return
      setProgress(100)
      setResult(response.data)
      if (response.data.needsReview) notify(`${response.data.imported} resume(s) imported; ${response.data.needsReview} need review.`, 'warning')
      else notify(talentPoolOnly ? `${response.data.imported} resume(s) added to the global Talent Pool.` : `${response.data.imported} resume(s) imported. ATS will run automatically when enabled.`, 'success')
      await onCompleted?.(response.data)
    } finally { setUploading(false) }
  }

  const openDraftEditor = (index: number) => {
    setEditingDraftIndex(index)
    setEditingDraft({ ...drafts[index] })
  }

  const saveDraftEditor = () => {
    if (editingDraftIndex == null || !editingDraft) return
    if (!editingDraft.firstName.trim()) return notify('Candidate first name is required.', 'error')
    setDrafts(current => current.map((draft, index) => index === editingDraftIndex ? {
      ...editingDraft,
      firstName: editingDraft.firstName.trim(),
      lastName: editingDraft.lastName.trim(),
      email: editingDraft.email.trim(),
      phone: editingDraft.phone.trim(),
      address: editingDraft.address.trim(),
    } : draft))
    setEditingDraftIndex(null)
    setEditingDraft(null)
  }

  const content = <div className="resume-intake-shell">
      {allowBulk && <Radio.Group className="resume-intake-mode" value={mode} onChange={event => changeMode(event.target.value)} buttonStyle="solid" disabled={busy}>
        <Radio.Button data-testid="resume-intake-single" value="single">Single resume</Radio.Button>
        <Radio.Button data-testid="resume-intake-bulk" value="bulk">Bulk resumes</Radio.Button>
      </Radio.Group>}

      <Card size="small" className="resume-intake-context" title={talentPoolOnly ? 'Resume source' : 'Job context'} extra={!talentPoolOnly && selectedPosition && !contextExpanded ? <Button size="small" onClick={() => onChangeJob ? onChangeJob() : setContextExpanded(true)} disabled={busy}>Change job</Button> : null}>
        <Form layout="vertical">
          <Row gutter={[16, 0]}>
            {!talentPoolOnly && (contextExpanded || !selectedPosition) && <><Col xs={24} md={12}><Form.Item label="Client" required><Select data-testid="resume-intake-client" loading={loadingContext} disabled={busy} showSearch optionFilterProp="label" value={clientId || undefined} placeholder="Select client" options={clientOptions} onChange={changeClient} /></Form.Item></Col>
              <Col xs={24} md={12}><Form.Item label="Open position" required><Select data-testid="resume-intake-position" disabled={!clientId || busy} showSearch optionFilterProp="label" value={positionId || undefined} placeholder="Select approved position" options={positionOptions} onChange={changePosition} /></Form.Item></Col>
              <Col xs={24} md={12}><Form.Item label="Job posting"><Select data-testid="resume-intake-posting" disabled={!positionId || busy} showSearch optionFilterProp="label" value={jobPostingId || 0} options={postingOptions} onChange={changePosting} /></Form.Item></Col>
              <Col xs={24} md={12}><Form.Item label="Job description used for ATS"><Select data-testid="resume-intake-jd" disabled value={jobDescriptionId || undefined} placeholder={positionId ? 'Approved job requirements' : 'Select a position first'} options={descriptionOptions} /></Form.Item></Col></>}
            {!talentPoolOnly && !contextExpanded && selectedPosition && (availablePostings.length > 0 || selectedPosting) && <Col xs={24} md={12}><Form.Item label="Job posting" extra="Use the approved job requirements or choose a posting-specific JD."><Select data-testid="resume-intake-posting" disabled={busy || resolvingPostings} showSearch optionFilterProp="label" value={jobPostingId || 0} options={postingOptions} onChange={changePosting} /></Form.Item></Col>}
            <Col xs={24} md={talentPoolOnly ? 24 : 12}><Form.Item label="Candidate source" required><Select data-testid="resume-intake-source" disabled={busy} value={sourceType} options={sourceOptions} onChange={value => { setSourceType(value); setDrafts([]) }} /></Form.Item></Col>
          </Row>
        </Form>
        {!talentPoolOnly && selectedPosition && <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 4 }} className="resume-job-summary">
          <Descriptions.Item label="Position">{selectedPosition.positionCode} - {selectedPosition.positionTitle}</Descriptions.Item>
          <Descriptions.Item label="Department">{selectedPosition.department || '-'}</Descriptions.Item>
          <Descriptions.Item label="Location">{selectedPosition.jobLocation || '-'}</Descriptions.Item>
          <Descriptions.Item label="ATS basis">{resolvingDescription ? 'Checking job requirements…' : selectedPosting ? selectedDescription ? `Posting JD v${selectedDescription.versionNumber}` : 'Posting-linked JD' : 'Approved job requirements'}</Descriptions.Item>
        </Descriptions>}
      </Card>

      <Card size="small" className="resume-intake-upload" title={`Add ${mode === 'single' ? 'a resume' : 'resumes'}`}>
        {parsingDisabled && <Alert className="resume-parsing-disabled-alert" type="info" showIcon message="Resume parsing is off for this job" description="All files will still be imported. Candidate cards will be marked for manual review so you can edit missing name, email or phone details." />}
        <div data-testid="resume-intake-files">
          <Upload.Dragger {...uploadProps}>
            <p className="ant-upload-drag-icon"><InboxOutlined /></p>
            <p className="ant-upload-text">Drop {mode === 'single' ? 'a resume' : 'up to 50 resumes'} here, or click to browse</p>
            <p className="ant-upload-hint">PDF, DOCX, RTF or TXT. A readable resume should be 10 MB or smaller for automatic parsing.</p>
          </Upload.Dragger>
        </div>
        <div className={`resume-force-upload${forceUpload || parsingDisabled ? ' is-active' : ''}`}>
          <Switch data-testid="resume-force-upload" checked={forceUpload || parsingDisabled} disabled={busy || parsingDisabled} onChange={setForceUpload} />
          <div><strong>{parsingDisabled ? 'Manual-review upload enabled' : 'Force upload if details cannot be read'}</strong><span>{parsingDisabled ? 'Parsing is off, so every selected resume is retained and can be completed from the candidate card.' : 'The resume will be retained for manual review and ATS will wait until the candidate details are completed.'}</span></div>
        </div>
        <div className="resume-intake-submit-row">
          <Typography.Text type="secondary">{draftsReady ? `${drafts.length} draft(s) ready for review` : selectedFiles.length ? `${selectedFiles.length} file(s) selected` : 'No files selected'}</Typography.Text>
          <div className="resume-intake-submit-actions">
            <Button data-testid="resume-intake-preview" size="large" icon={<FileSearchOutlined />} loading={previewing} disabled={uploading || loadingContext || resolvingPostings || resolvingDescription || (!talentPoolOnly && (!selectedPosition || selectedPosition.clientId !== clientId)) || !selectedFiles.length} onClick={() => void preview()}>{draftsReady ? 'Parse again' : 'Preview & parse'}</Button>
            {draftsReady && <Button data-testid="resume-intake-submit" type="primary" size="large" loading={uploading} disabled={previewing} onClick={() => void submit()}>{talentPoolOnly ? 'Final upload to Talent Pool' : (submitLabel || 'Final upload')}</Button>}
          </div>
        </div>
        {previewing && <div className="resume-intake-progress" aria-live="polite">
          <div className="resume-progress-heading"><strong>Preparing editable drafts — nothing is being saved</strong><b>{progress}%</b></div>
          <Progress percent={progress} status="active" showInfo={false} />
          <div className="resume-progress-meta"><span>Parsing {selectedFiles.length} selected file(s)</span><span>No database or file-storage write</span></div>
        </div>}
        {uploading && <div className="resume-intake-progress" aria-live="polite">
          <div className="resume-progress-heading"><strong>{progressMessage(progressDetail)}</strong><b>{progress}%</b></div>
          <Progress percent={progress} status="active" showInfo={false} />
          <div className="resume-progress-meta"><span>{progressDetail?.completedFiles || 0} of {progressDetail?.totalFiles || selectedFiles.length} files processed</span><span>{progressDetail?.phase === 'processing' ? 'Resume storage and candidate records are being saved.' : 'Secure upload is in progress.'}</span></div>
        </div>}
      </Card>

      {drafts.length > 0 && <Card size="small" className="resume-intake-drafts" title="Review parsed candidate drafts">
        <Alert type="info" showIcon message="Draft only — nothing has been saved yet" description="Review or edit the parsed information. Candidate, application and resume storage records are created only when you click Final upload." />
        <div className="resume-draft-list" data-testid="resume-intake-drafts">
          {drafts.map((draft, index) => <div className="resume-draft-row" key={`${draft.file.name}-${draft.file.lastModified}-${index}`}>
            <div className="resume-draft-file"><FileSearchOutlined /><div><strong>{draft.fileName}</strong><span>{draft.parsingStatus}{draft.parsingError ? ` · ${draft.parsingError}` : ''}</span></div></div>
            <div className="resume-draft-person"><strong>{`${draft.firstName} ${draft.lastName}`.trim() || 'Candidate name required'}</strong><span>{draft.email || 'No email'} · {draft.phone || 'No phone'}</span></div>
            <div className="resume-draft-outcome">
              {draft.existingApplicationId ? <Tag color="blue">Already registered · resume update</Tag> : draft.existingCandidateId ? <Tag color="cyan">Existing profile · new application</Tag> : <Tag color="green">New candidate</Tag>}
              {draft.existingCandidateCode && <span>{draft.existingCandidateCode}</span>}
            </div>
            <Button data-testid={`resume-draft-edit-${index}`} icon={<EditOutlined />} onClick={() => openDraftEditor(index)}>Edit</Button>
          </div>)}
        </div>
      </Card>}

      <div data-testid="resume-intake-results">
        {result && <ResumeIntakeResults result={result} talentPoolOnly={talentPoolOnly} onEditCandidate={onEditCandidate} />}
      </div>
      <Modal
        open={editingDraftIndex != null && Boolean(editingDraft)}
        title="Edit candidate draft"
        okText="Save draft"
        okButtonProps={{ id: 'resume-draft-save' }}
        onOk={saveDraftEditor}
        onCancel={() => { setEditingDraftIndex(null); setEditingDraft(null) }}
        destroyOnClose
      >
        {editingDraft && <Form layout="vertical" className="resume-draft-form">
          <Row gutter={12}>
            <Col span={12}><Form.Item label="First name" required><Input data-testid="resume-draft-first-name" value={editingDraft.firstName} onChange={event => setEditingDraft({ ...editingDraft, firstName: event.target.value })} /></Form.Item></Col>
            <Col span={12}><Form.Item label="Last name"><Input data-testid="resume-draft-last-name" value={editingDraft.lastName} onChange={event => setEditingDraft({ ...editingDraft, lastName: event.target.value })} /></Form.Item></Col>
            <Col span={12}><Form.Item label="Email"><Input data-testid="resume-draft-email" value={editingDraft.email} onChange={event => setEditingDraft({ ...editingDraft, email: event.target.value })} /></Form.Item></Col>
            <Col span={12}><Form.Item label="Phone"><Input data-testid="resume-draft-phone" value={editingDraft.phone} onChange={event => setEditingDraft({ ...editingDraft, phone: event.target.value })} /></Form.Item></Col>
            <Col span={24}><Form.Item label="Current location / address"><Input data-testid="resume-draft-address" value={editingDraft.address} onChange={event => setEditingDraft({ ...editingDraft, address: event.target.value })} /></Form.Item></Col>
            <Col span={24}><Form.Item label="Total experience (months)"><InputNumber data-testid="resume-draft-experience" min={0} max={1200} value={editingDraft.totalExperienceMonths} onChange={value => setEditingDraft({ ...editingDraft, totalExperienceMonths: Number(value || 0) })} /></Form.Item></Col>
          </Row>
        </Form>}
      </Modal>
    </div>

  return embedded ? <section className="resume-intake-embedded">{content}</section> : <Drawer
    open={open}
    onClose={onClose}
    maskClosable={!busy}
    keyboard={!busy}
    closable={!busy}
    width="min(1040px, 96vw)"
    destroyOnClose
    className="resume-intake-drawer"
    title={<div className="resume-intake-title"><FileSearchOutlined /><div><strong>{title || (talentPoolOnly ? 'Global Talent Pool intake' : 'Resume intake & ATS screening')}</strong><span>{description || (talentPoolOnly ? 'Preview and edit parsed details first; Final upload stores the resume in the Talent Pool.' : 'Preview and edit parsed candidate details first. Final upload creates the candidate/application and stores the resume.')}</span></div></div>}
    extra={<Button onClick={onClose} disabled={busy}>Close</Button>}
  >
    {content}
  </Drawer>
}

function progressMessage(progress: RecruitmentResumeUploadProgress | null) {
  if (!progress) return 'Preparing resume upload…'
  if (progress.phase === 'complete') return 'Bulk upload completed'
  const range = progress.activeFrom === progress.activeTo ? `file ${progress.activeFrom}` : `files ${progress.activeFrom}–${progress.activeTo}`
  return progress.phase === 'processing' ? `Processing ${range} of ${progress.totalFiles}` : `Uploading ${range} of ${progress.totalFiles}`
}

function ResumeIntakeResults({ result, talentPoolOnly = false, onEditCandidate }: { result: RecruitmentResumeIntakeResult; talentPoolOnly?: boolean; onEditCandidate?: (candidateId: number) => void | Promise<void> }) {
  const reviewRows = result.items.filter(row => !row.success || Boolean(row.error?.trim()))
  const reviewNotes = [...new Set(reviewRows.map(row => row.error?.trim()).filter((note): note is string => Boolean(note)))]
  const reviewedWithContact = reviewRows.filter(row => Boolean(row.detectedEmail?.trim() || row.detectedPhone?.trim() || row.candidate?.email?.trim() || row.candidate?.phone?.trim())).length
  const needsReview = (row: RecruitmentResumeIntakeItem) => !row.success || Boolean(row.error?.trim())

  return <Card size="small" className="resume-intake-results" title="Import results">
    <div className="resume-result-metrics">
      <Statistic title="Files" value={result.totalFiles} />
      <Statistic title={talentPoolOnly ? 'Added to Talent Pool' : 'Imported'} value={result.imported} valueStyle={{ color: '#15803d' }} />
      <Statistic title="Needs review" value={result.needsReview} valueStyle={{ color: result.needsReview ? '#b45309' : '#64748b' }} />
    </div>
    {result.items.length ? <DataTable<RecruitmentResumeIntakeItem>
      rows={result.items}
      getRowId={(row, index) => `${row.fileName}-${row.application?.id || index}`}
      exportFileName="resume-screening-results"
      emptyText="No resume results were returned."
      rowClassName={row => needsReview(row) ? 'resume-result-review' : 'resume-result-success'}
      actions={row => row.candidate && onEditCandidate ? <Button size="small" icon={<EditOutlined />} onClick={() => void onEditCandidate(row.candidate!.id)}>Edit candidate</Button> : null}
      columns={[
        { key: 'outcome', label: 'Outcome', width: '210px', render: row => needsReview(row) ? <Tag color="orange">{row.forceUploaded ? 'Forced · review' : row.success ? 'Imported · review' : 'Needs review'}</Tag> : row.applicationReused ? <Tag color="blue" title={row.message}>Already registered · resume updated</Tag> : row.candidateReused ? <Tag color="cyan" title={row.message}>Existing profile reused</Tag> : row.resumeReplaced ? <Tag color="blue" title={row.message}>Resume updated</Tag> : <Tag color="green">{talentPoolOnly ? 'Stored' : row.application?.autoRunAts ? 'ATS queued' : 'Imported'}</Tag>, exportValue: row => row.message || (needsReview(row) ? (row.forceUploaded ? 'Forced - review' : row.success ? 'Imported - review' : 'Needs review') : (talentPoolOnly ? 'Stored' : row.application?.autoRunAts ? 'ATS queued' : 'Imported')) },
        { key: 'fileName', label: 'Resume', width: '210px' },
        { key: 'candidate', label: 'Candidate', width: '220px', render: row => <div className="resume-result-person"><b>{row.candidate?.candidateName || row.detectedName || 'Not detected'}</b><span>{row.candidate?.candidateCode || row.parsingStatus || '-'}</span></div>, exportValue: row => row.candidate?.candidateName || row.detectedName },
        { key: 'contact', label: 'Contact extracted', width: '240px', render: row => <div className="resume-result-person"><b>{row.detectedEmail || row.candidate?.email || '-'}</b><span>{row.detectedPhone || row.candidate?.phone || '-'}</span></div>, exportValue: row => `${row.detectedEmail || row.candidate?.email || ''} ${row.detectedPhone || row.candidate?.phone || ''}` },
        { key: 'detectedAddress', label: 'Residential address', width: '230px', render: row => row.detectedAddress || row.candidate?.currentLocation || '-' },
        { key: 'ats', label: 'ATS score', width: '130px', render: row => talentPoolOnly ? <Tag>Run later</Tag> : row.application?.atsScore == null ? <Tag>{row.application?.scoreStatus || 'Not scored'}</Tag> : <Tag color={row.application.atsScore >= 60 ? 'green' : 'orange'}>{row.application.atsScore.toFixed(1)} / 100</Tag>, exportValue: row => talentPoolOnly ? 'Run later' : row.application?.atsScore ?? row.application?.scoreStatus ?? '' },
        { key: 'stage', label: talentPoolOnly ? 'Bucket' : 'Pipeline stage', width: '150px', render: row => talentPoolOnly ? 'Resume Bank' : row.application?.currentStage || '-' },
        { key: 'error', label: 'Review note', width: '260px', render: row => row.error || 'Ready for recruiter review' },
      ]}
    /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No result rows were returned." />}
    {result.needsReview > 0 && <Alert
      className="resume-result-alert"
      type="warning"
      showIcon
      message={`${result.needsReview} resume(s) need human review`}
      description={<div className="resume-review-summary">
        {reviewedWithContact > 0 && reviewedWithContact === reviewRows.length && <span>Contact details were extracted successfully. Review is required for the processing reason below.</span>}
        {reviewedWithContact > 0 && reviewedWithContact < reviewRows.length && <span>Contact details were extracted for {reviewedWithContact} of {reviewRows.length} reviewed resumes. Check each row's Review note for its exact reason.</span>}
        {reviewNotes.length === 1
          ? <strong>{reviewNotes[0]}</strong>
          : reviewNotes.length > 1
            ? <ul>{reviewNotes.map(note => <li key={note}>{note}</li>)}</ul>
            : <span>Open the Review note column for the item-specific reason.</span>}
      </div>}
    />}
  </Card>
}
