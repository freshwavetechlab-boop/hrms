import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Button, Card, Col, Descriptions, Drawer, Empty, Form, Progress, Radio, Row,
  Select, Statistic, Tag, Typography, Upload,
} from 'antd'
import type { UploadFile, UploadProps } from 'antd'
import { FileSearchOutlined, InboxOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import { getClients } from '../services/payrollService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import { getRecruitmentJobDescriptions, getRecruitmentJobPostings } from '../services/recruitmentOrchestrationService'
import { intakeRecruitmentResumes } from '../services/recruitmentTalentService'
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
  onCompleted?: (result: RecruitmentResumeIntakeResult) => void | Promise<void>
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
  onCompleted,
}: Props) {
  const notify = useToast()
  const [mode, setMode] = useState<RecruitmentResumeIntakeMode>(initialMode)
  const [clients, setClients] = useState<Client[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
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
  const [progress, setProgress] = useState(0)
  const [result, setResult] = useState<RecruitmentResumeIntakeResult | null>(null)

  useEffect(() => {
    if (!open) return
    let active = true
    void Promise.all([getClients(), getRecruitmentOpenPositions()])
      .then(([clientRows, positionRows]) => {
        if (!active) return
        setClients(clientRows)
        setPositions(positionRows)
        if (!initialClientId && clientRows.length === 1) setClientId(clientRows[0].id)
      })
      .finally(() => { if (active) setLoadingContext(false) })
    return () => { active = false }
  }, [open, initialMode, initialClientId, initialPositionId, initialJobPostingId])

  useEffect(() => {
    if (!open || !clientId) return
    let active = true
    void getRecruitmentJobPostings(clientId).then(rows => {
      if (!active) return
      setPostings(rows)
      if (initialJobPostingId && rows.some(row => row.id === initialJobPostingId)) {
        const posting = rows.find(row => row.id === initialJobPostingId)!
        setJobPostingId(posting.id)
        setPositionId(posting.positionId)
        setJobDescriptionId(posting.jobDescriptionVersionId || null)
      }
    })
    return () => { active = false }
  }, [open, clientId, initialJobPostingId])

  useEffect(() => {
    if (!open || !positionId) return
    const position = positions.find(row => row.id === positionId)
    if (!position?.requisitionId) return
    let active = true
    void getRecruitmentJobDescriptions(position.requisitionId).then(rows => {
      if (!active) return
      const approved = rows.filter(row => row.status === 'Approved')
      setDescriptions(approved)
      const posting = postings.find(row => row.id === jobPostingId)
      setJobDescriptionId(posting?.jobDescriptionVersionId || approved[0]?.id || null)
    })
    return () => { active = false }
  }, [open, positionId, positions, postings, jobPostingId])

  const availablePositions = useMemo(() => positions
    .filter(row => !clientId || row.clientId === clientId)
    .filter(row => !['Closed', 'Cancelled', 'Filled'].includes(row.status)), [positions, clientId])
  const availablePostings = useMemo(() => postings
    .filter(row => !positionId || row.positionId === positionId)
    .filter(row => !['Closed', 'Cancelled'].includes(row.status)), [postings, positionId])
  const selectedPosition = positions.find(row => row.id === positionId)
  const selectedPosting = postings.find(row => row.id === jobPostingId)
  const selectedDescription = descriptions.find(row => row.id === jobDescriptionId)
  const selectedFiles = useMemo<File[]>(() => fileList.flatMap(row => row.originFileObj ? [row.originFileObj as File] : []), [fileList])

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
  }
  const changeClient = (value: number) => {
    setClientId(value)
    setPositionId(0)
    setJobPostingId(null)
    setJobDescriptionId(null)
    setDescriptions([])
    setResult(null)
  }
  const changePosition = (value: number) => {
    setPositionId(value)
    setJobPostingId(null)
    setJobDescriptionId(null)
    setResult(null)
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
    },
    onRemove: file => { setFileList(current => current.filter(row => row.uid !== file.uid)); return true },
    disabled: uploading,
  }

  const submit = async () => {
    if (!clientId) return notify('Select a client.', 'error')
    if (!positionId) return notify('Select a job position.', 'error')
    if (!selectedFiles.length) return notify(`Select ${mode === 'single' ? 'a resume' : 'one or more resumes'}.`, 'error')
    setUploading(true)
    setProgress(0)
    setResult(null)
    const response = await intakeRecruitmentResumes({ clientId, positionId, jobPostingId, sourceType, files: selectedFiles }, setProgress)
    setUploading(false)
    if (!response.ok) return
    setProgress(100)
    setResult(response.data)
    if (response.data.needsReview) notify(`${response.data.imported} resume(s) screened; ${response.data.needsReview} need review.`, 'warning')
    else notify(`${response.data.imported} resume(s) uploaded and screened.`, 'success')
    await onCompleted?.(response.data)
  }

  return <Drawer
    open={open}
    onClose={onClose}
    width="min(1040px, 96vw)"
    destroyOnClose
    className="resume-intake-drawer"
    title={<div className="resume-intake-title"><FileSearchOutlined /><div><strong>Resume intake & ATS screening</strong><span>Upload once to create or match the candidate, attach the secure resume, create the application and calculate its ATS score.</span></div></div>}
    extra={<Button onClick={onClose}>Close</Button>}
  >
    <div className="resume-intake-shell">
      <Radio.Group className="resume-intake-mode" value={mode} onChange={event => changeMode(event.target.value)} buttonStyle="solid" disabled={uploading}>
        <Radio.Button data-testid="resume-intake-single" value="single">Single resume</Radio.Button>
        <Radio.Button data-testid="resume-intake-bulk" value="bulk">Bulk resumes</Radio.Button>
      </Radio.Group>

      <Card size="small" className="resume-intake-context" title="1. Select the job context">
        <Form layout="vertical">
          <Row gutter={[16, 0]}>
            <Col xs={24} md={12}><Form.Item label="Client" required><Select data-testid="resume-intake-client" loading={loadingContext} showSearch optionFilterProp="label" value={clientId || undefined} placeholder="Select client" options={clientOptions} onChange={changeClient} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Open position" required><Select data-testid="resume-intake-position" disabled={!clientId} showSearch optionFilterProp="label" value={positionId || undefined} placeholder="Select approved position" options={positionOptions} onChange={changePosition} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Job posting"><Select data-testid="resume-intake-posting" disabled={!positionId} showSearch optionFilterProp="label" value={jobPostingId || 0} options={postingOptions} onChange={changePosting} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Job description used for ATS"><Select data-testid="resume-intake-jd" disabled value={jobDescriptionId || undefined} placeholder={positionId ? 'No approved JD linked' : 'Select a position first'} options={descriptionOptions} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Candidate source"><Select value={sourceType} options={sourceOptions} onChange={setSourceType} /></Form.Item></Col>
          </Row>
        </Form>
        {selectedPosition && <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 4 }} className="resume-job-summary">
          <Descriptions.Item label="Position">{selectedPosition.positionCode} - {selectedPosition.positionTitle}</Descriptions.Item>
          <Descriptions.Item label="Department">{selectedPosition.department || '-'}</Descriptions.Item>
          <Descriptions.Item label="Location">{selectedPosition.jobLocation || '-'}</Descriptions.Item>
          <Descriptions.Item label="ATS basis">{selectedDescription ? `JD v${selectedDescription.versionNumber}` : selectedPosting ? 'Posting-linked JD' : 'Position requirements'}</Descriptions.Item>
        </Descriptions>}
      </Card>

      <Card size="small" className="resume-intake-upload" title={`2. Add ${mode === 'single' ? 'a resume' : 'resumes'}`}>
        <div data-testid="resume-intake-files">
          <Upload.Dragger {...uploadProps}>
            <p className="ant-upload-drag-icon"><InboxOutlined /></p>
            <p className="ant-upload-text">Drop {mode === 'single' ? 'a resume' : 'up to 50 resumes'} here, or click to browse</p>
            <p className="ant-upload-hint">PDF, DOCX, RTF or TXT. A readable resume should be 10 MB or smaller for automatic parsing.</p>
          </Upload.Dragger>
        </div>
        <div className="resume-intake-submit-row">
          <Typography.Text type="secondary">{selectedFiles.length ? `${selectedFiles.length} file(s) ready` : 'No files selected'}</Typography.Text>
          <Button data-testid="resume-intake-submit" type="primary" size="large" icon={<FileSearchOutlined />} loading={uploading} disabled={!clientId || !positionId || !selectedFiles.length} onClick={() => void submit()}>Upload & screen</Button>
        </div>
        {uploading && <div className="resume-intake-progress"><Progress percent={progress} status="active" /><Typography.Text type="secondary">Uploading securely and screening each resume. Keep this window open.</Typography.Text></div>}
      </Card>

      <div data-testid="resume-intake-results">
        {result && <ResumeIntakeResults result={result} />}
      </div>
    </div>
  </Drawer>
}

function ResumeIntakeResults({ result }: { result: RecruitmentResumeIntakeResult }) {
  return <Card size="small" className="resume-intake-results" title="3. Screening results">
    <div className="resume-result-metrics">
      <Statistic title="Files" value={result.totalFiles} />
      <Statistic title="Imported & screened" value={result.imported} valueStyle={{ color: '#15803d' }} />
      <Statistic title="Needs review" value={result.needsReview} valueStyle={{ color: result.needsReview ? '#b45309' : '#64748b' }} />
    </div>
    {result.items.length ? <DataTable<RecruitmentResumeIntakeItem>
      rows={result.items}
      getRowId={(row, index) => `${row.fileName}-${row.application?.id || index}`}
      exportFileName="resume-screening-results"
      emptyText="No resume results were returned."
      rowClassName={row => row.success ? 'resume-result-success' : 'resume-result-review'}
      columns={[
        { key: 'outcome', label: 'Outcome', width: '125px', render: row => row.success ? <Tag color="green">Screened</Tag> : <Tag color="orange">Needs review</Tag>, exportValue: row => row.success ? 'Screened' : 'Needs review' },
        { key: 'fileName', label: 'Resume', width: '210px' },
        { key: 'candidate', label: 'Candidate', width: '220px', render: row => <div className="resume-result-person"><b>{row.candidate?.candidateName || row.detectedName || 'Not detected'}</b><span>{row.candidate?.candidateCode || row.parsingStatus || '-'}</span></div>, exportValue: row => row.candidate?.candidateName || row.detectedName },
        { key: 'contact', label: 'Contact extracted', width: '240px', render: row => <div className="resume-result-person"><b>{row.detectedEmail || row.candidate?.email || '-'}</b><span>{row.detectedPhone || row.candidate?.phone || '-'}</span></div>, exportValue: row => `${row.detectedEmail || row.candidate?.email || ''} ${row.detectedPhone || row.candidate?.phone || ''}` },
        { key: 'detectedAddress', label: 'Residential address', width: '230px', render: row => row.detectedAddress || row.candidate?.currentLocation || '-' },
        { key: 'ats', label: 'ATS score', width: '130px', render: row => row.application?.atsScore == null ? <Tag>{row.application?.scoreStatus || 'Not scored'}</Tag> : <Tag color={row.application.atsScore >= 60 ? 'green' : 'orange'}>{row.application.atsScore.toFixed(1)} / 100</Tag>, exportValue: row => row.application?.atsScore ?? row.application?.scoreStatus ?? '' },
        { key: 'stage', label: 'Pipeline stage', width: '150px', render: row => row.application?.currentStage || '-' },
        { key: 'error', label: 'Review note', width: '260px', render: row => row.error || 'Ready for recruiter review' },
      ]}
    /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No result rows were returned." />}
    {result.needsReview > 0 && <Alert className="resume-result-alert" type="warning" showIcon message="Some resumes need human review" description="No candidate is silently created when both email and mobile are missing. Review the file and correct its contact details before continuing." />}
  </Card>
}
