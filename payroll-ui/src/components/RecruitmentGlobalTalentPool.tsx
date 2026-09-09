import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Card, Descriptions, Drawer, Empty, Input, Modal, Select, Space, Spin, Tabs, Tag, Typography } from 'antd'
import { CheckCircleOutlined, EyeOutlined, FileSearchOutlined, FolderOpenOutlined, RocketOutlined, ThunderboltOutlined, UserSwitchOutlined } from '@ant-design/icons'
import DataTable, { type Column } from './DataTable'
import RecruitmentResumeIntake, { type RecruitmentResumeIntakeMode } from './RecruitmentResumeIntake'
import { downloadAttachmentBlob } from '../services/attachmentService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import {
  directSelectTalentPoolCandidate, getCandidate, getGlobalTalentPoolCandidates, getTalentPoolMatches,
  promoteTalentPoolMatch, runTalentPoolMatch, setTalentPoolMatchSelection,
} from '../services/recruitmentTalentService'
import type {
  RecruitmentCandidate, RecruitmentCandidateApplication, RecruitmentCandidateDetail,
  RecruitmentOpenPosition, RecruitmentTalentPoolMatchRunResult,
} from '../types/payroll'

type Props = {
  onViewCandidate: (candidateId: number) => void | Promise<void>
  onChanged?: () => void | Promise<void>
  initialPositionId?: number
  clientId?: number
  contextPositions?: RecruitmentOpenPosition[]
  embeddedPreview?: boolean
}

type PoolTab = 'resumes' | 'matches' | 'selected'

export default function RecruitmentGlobalTalentPool({ onViewCandidate, onChanged, initialPositionId = 0, clientId = 0, contextPositions, embeddedPreview = false }: Props) {
  const [tab, setTab] = useState<PoolTab>('resumes')
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('')
  const [candidates, setCandidates] = useState<RecruitmentCandidate[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>(contextPositions || [])
  const [positionId, setPositionId] = useState(initialPositionId)
  const [matches, setMatches] = useState<RecruitmentCandidateApplication[]>([])
  const [intakeMode, setIntakeMode] = useState<RecruitmentResumeIntakeMode | null>(null)
  const [matching, setMatching] = useState(false)
  const [runResult, setRunResult] = useState<RecruitmentTalentPoolMatchRunResult | null>(null)
  const [actionCandidate, setActionCandidate] = useState<RecruitmentCandidate | null>(null)
  const [actionDetail, setActionDetail] = useState<RecruitmentCandidateDetail | null>(null)
  const [actionPositionId, setActionPositionId] = useState(0)
  const [actionMatch, setActionMatch] = useState<RecruitmentCandidateApplication | null>(null)
  const [actionSkippedAts, setActionSkippedAts] = useState(false)
  const [actionLoading, setActionLoading] = useState(false)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [previewUrl, setPreviewUrl] = useState('')
  const [previewName, setPreviewName] = useState('Resume preview')
  const [previewType, setPreviewType] = useState('')
  const resumeRequest = useRef(0)
  const matchRequest = useRef(0)
  const actionRequest = useRef(0)
  const previewRequest = useRef(0)
  const poolScope = useRef({ tab, positionId })
  poolScope.current = { tab, positionId }

  const approvedPositions = useMemo(() => positions
    .filter(row => !clientId || row.clientId === clientId)
    .filter(row => !['Closed', 'Cancelled', 'Filled'].includes(row.status))
    .filter(row => row.jobDescriptionStatus === 'Approved' || Boolean(row.latestJobDescriptionVersionId)), [positions, clientId])

  const loadResumes = useCallback(async () => {
    const request = ++resumeRequest.current
    const rows = await getGlobalTalentPoolCandidates(query, status)
    if (request === resumeRequest.current) setCandidates(rows)
  }, [query, status])

  const loadMatches = useCallback(async (nextTab: PoolTab = tab, nextPositionId = positionId) => {
    const request = ++matchRequest.current
    if (nextTab === 'resumes') return
    const rows = await getTalentPoolMatches(nextPositionId || undefined, nextTab === 'selected' ? 'Selected' : '')
    if (request === matchRequest.current) setMatches(rows)
  }, [positionId, tab])

  useEffect(() => {
    const timer = window.setTimeout(() => { void loadResumes() }, 250)
    return () => { window.clearTimeout(timer); ++resumeRequest.current }
  }, [loadResumes])

  useEffect(() => {
    let active = true
    if (contextPositions) { setPositions(contextPositions); return }
    void getRecruitmentOpenPositions().then(rows => { if (active) setPositions(rows) })
    return () => { active = false }
  }, [contextPositions])

  useEffect(() => { setPositionId(initialPositionId); setRunResult(null) }, [initialPositionId])
  useEffect(() => () => { if (previewUrl) URL.revokeObjectURL(previewUrl) }, [previewUrl])
  useEffect(() => () => { ++resumeRequest.current; ++matchRequest.current; ++actionRequest.current; ++previewRequest.current }, [])

  useEffect(() => { void loadMatches(tab, positionId) }, [tab, positionId, loadMatches])

  const changeTab = (key: string) => {
    setTab(key as PoolTab)
    setRunResult(null)
  }

  const runMatch = async () => {
    if (!approvedPositions.some(row => row.id === positionId) || matching) return
    setMatching(true)
    setRunResult(null)
    try {
      const response = await runTalentPoolMatch(positionId)
      if (!response.ok || !response.data) return
      if (poolScope.current.positionId === positionId && poolScope.current.tab === tab) {
        setRunResult(response.data)
        setMatches(response.data.matches)
      }
      await onChanged?.()
    } finally { setMatching(false) }
  }

  const toggleSelection = async (row: RecruitmentCandidateApplication, selected: boolean) => {
    const response = await setTalentPoolMatchSelection(row.id, selected)
    if (!response.ok) return
    await loadMatches(tab, positionId)
  }

  const promote = async (row: RecruitmentCandidateApplication) => {
    const response = await promoteTalentPoolMatch(row.id)
    if (!response.ok) return
    await Promise.all([loadMatches(tab, positionId), onChanged?.()])
  }

  const openResumeAction = async (candidate: RecruitmentCandidate) => {
    const request = ++actionRequest.current
    closePreview()
    setActionCandidate(candidate)
    setActionDetail(null)
    setActionMatch(null)
    setActionSkippedAts(false)
    setActionPositionId(approvedPositions.some(row => row.id === positionId) ? positionId : 0)
    setActionLoading(true)
    try {
      const detail = await getCandidate(candidate.id)
      if (request === actionRequest.current) setActionDetail(detail)
    } finally { if (request === actionRequest.current) setActionLoading(false) }
  }

  const executeCandidateAction = async <T,>(operation: () => Promise<T>): Promise<T | undefined> => {
    const request = actionRequest.current
    setActionLoading(true)
    try {
      const response = await operation()
      return request === actionRequest.current ? response : undefined
    } finally { if (request === actionRequest.current) setActionLoading(false) }
  }

  const runCandidateAts = async () => {
    if (!actionCandidate || !approvedPositions.some(row => row.id === actionPositionId) || actionLoading) return
    const response = await executeCandidateAction(() => runTalentPoolMatch(actionPositionId, [actionCandidate.id]))
    if (!response?.ok || !response.data) return
    setActionSkippedAts(false)
    setActionMatch(response.data.matches.find(row => row.candidateId === actionCandidate.id) || null)
    setRunResult(response.data)
    setPositionId(actionPositionId)
    await loadMatches('matches', actionPositionId)
  }

  const skipAtsAndSelect = async () => {
    if (!actionCandidate || !approvedPositions.some(row => row.id === actionPositionId) || actionLoading) return
    const response = await executeCandidateAction(() => directSelectTalentPoolCandidate(actionCandidate.id, actionPositionId))
    if (!response?.ok || !response.data) return
    setActionSkippedAts(true)
    setActionMatch(response.data)
    setPositionId(actionPositionId)
    await loadMatches('selected', actionPositionId)
  }

  const selectActionMatch = async () => {
    if (!actionMatch || actionLoading) return
    const response = await executeCandidateAction(() => setTalentPoolMatchSelection(actionMatch.id, true))
    if (!response?.ok || !response.data) return
    setActionMatch(response.data)
    await loadMatches('selected', actionPositionId)
  }

  const promoteActionMatch = async () => {
    if (!actionMatch || actionLoading) return
    const response = await executeCandidateAction(() => promoteTalentPoolMatch(actionMatch.id))
    if (!response?.ok) return
    setActionCandidate(null)
    setActionDetail(null)
    setActionMatch(null)
    await Promise.all([loadResumes(), loadMatches('selected', actionPositionId), onChanged?.()])
  }

  const closePreview = () => {
    ++previewRequest.current
    setPreviewUrl('')
    setPreviewLoading(false)
  }

  const previewResume = async () => {
    if (!primaryResume) return
    const request = ++previewRequest.current
    setPreviewLoading(true)
    try {
      const response = await downloadAttachmentBlob(primaryResume.attachmentPublicId)
      if (request !== previewRequest.current || !response.ok || !response.blob) return
      setPreviewName(primaryResume.originalFileName || 'Resume preview')
      const blob = /\.pdf$/i.test(primaryResume.originalFileName) && response.blob.type === 'application/octet-stream' ? new Blob([response.blob], { type: 'application/pdf' }) : response.blob
      setPreviewType(blob.type)
      setPreviewUrl(URL.createObjectURL(blob))
    } finally { if (request === previewRequest.current) setPreviewLoading(false) }
  }

  const matchColumns: Column<RecruitmentCandidateApplication>[] = [
    { key: 'candidate', label: 'Candidate', width: '250px', render: row => <div className="resume-result-person"><b>{row.candidateName}</b><span>{row.candidateCode} · {row.candidateEmail || row.candidatePhone}</span></div>, value: row => row.candidateName },
    { key: 'positionTitle', label: 'Matched role', width: '230px', render: row => <div className="resume-result-person"><b>{row.positionTitle}</b><span>{row.positionCode} · {row.clientName}</span></div> },
    { key: 'atsScore', label: 'ATS score', width: '130px', render: row => row.atsScore == null ? <Tag color="orange">{row.scoreStatus || 'Queued'}</Tag> : <Tag color={row.atsScore >= 60 ? 'green' : 'orange'}>{row.atsScore.toFixed(1)} / 100</Tag> },
    { key: 'currentStatus', label: 'Bucket', width: '120px', render: row => <Tag color={row.currentStatus === 'Selected' ? 'purple' : 'blue'}>{row.currentStatus}</Tag> },
  ]

  const matchTable = <DataTable<RecruitmentCandidateApplication>
    rows={matches.filter(row => (!clientId || row.clientId === clientId) && (!positionId || row.positionId === positionId))}
    getRowId={row => row.id}
    exportFileName={tab === 'selected' ? 'talent-pool-selected' : 'talent-pool-ats-matches'}
    emptyText={positionId || tab === 'selected' ? 'No candidates in this bucket.' : 'Select an approved job role to view ATS matches.'}
    actions={row => <Space wrap>
      <Button size="small" onClick={() => void onViewCandidate(row.candidateId)}>View profile</Button>
      {row.currentStatus === 'Selected'
        ? <Button size="small" onClick={() => void toggleSelection(row, false)}>Remove</Button>
        : <Button size="small" type="primary" onClick={() => void toggleSelection(row, true)}>Select</Button>}
      {row.currentStatus === 'Selected' && <Button size="small" type="primary" icon={<UserSwitchOutlined />} onClick={() => void promote(row)}>Move to pipeline</Button>}
    </Space>}
    columns={matchColumns}
  />

  const positionControl = <Select
    data-testid="talent-pool-position"
    value={positionId || undefined}
    disabled={matching}
    onChange={value => { setPositionId(Number(value || 0)); setRunResult(null) }}
    showSearch
    allowClear
    optionFilterProp="label"
    placeholder="Select approved job role / JD"
    options={approvedPositions.map(row => ({ value: row.id, label: `${row.clientName} · ${row.positionCode} - ${row.positionTitle} · JD v${row.latestJobDescriptionVersionNumber || 1}` }))}
  />

  const primaryResume = actionDetail?.resumes.find(row => row.isPrimary) || actionDetail?.resumes[0]
  const parsedResume = primaryResume?.parsingStatus === 'Parsed'
  const selectedActionPosition = approvedPositions.find(row => row.id === actionPositionId)

  return <div className="global-talent-pool" data-testid="global-talent-pool">
    <Tabs activeKey={tab} onChange={changeTab} items={[
      { key: 'resumes', label: <span><FolderOpenOutlined /> Resume Bank <Tag>{candidates.length}</Tag></span> },
      { key: 'matches', label: <span><FileSearchOutlined /> ATS Matches</span> },
      { key: 'selected', label: <span><UserSwitchOutlined /> Selected</span> },
    ]} />

    {tab === 'resumes' && <>
      <div className="talent-toolbar">
        <Input.Search data-testid="talent-pool-search" value={query} onChange={event => setQuery(event.target.value)} onSearch={() => void loadResumes()} placeholder="Search resume keywords, skills, name, email or phone" allowClear enterButton="Search resumes" />
        <Select value={status} onChange={setStatus} options={[{ value: '', label: 'All active profiles' }, ...['Active', 'Inactive', 'Joined', 'Archived'].map(value => ({ value, label: value }))]} />
        <div className="talent-toolbar-actions">
          <Button onClick={() => setIntakeMode('single')}>Upload resume</Button>
          <Button data-testid="talent-pool-bulk-upload" type="primary" onClick={() => setIntakeMode('bulk')}>Bulk resumes</Button>
        </div>
      </div>
      <Alert type="info" showIcon message="Global resume bank" description="Resumes are stored without a client or job. Search inside parsed resume content, preview the original, then run ATS or select directly for an approved role." />
      <DataTable<RecruitmentCandidate>
        rows={candidates}
        exportFileName="global-talent-pool-resumes"
        hideSearch
        emptyText="No resumes have been added to the global Talent Pool."
        actionsWidth={250}
        actions={row => <Space wrap>
          <Button data-testid={`talent-pool-review-${row.id}`} size="small" type="primary" icon={<EyeOutlined />} onClick={() => void openResumeAction(row)}>Preview & action</Button>
          <Button size="small" onClick={() => void onViewCandidate(row.id)}>360° profile</Button>
        </Space>}
        columns={[
          { key: 'candidateCode', label: 'Candidate', render: row => <div className="resume-result-person"><b>{row.candidateName}</b><span>{row.candidateCode} · {row.email || row.phone}</span></div>, value: row => row.candidateName },
          { key: 'currentTitle', label: 'Current role', render: row => <div className="resume-result-person"><b>{row.currentTitle || '-'}</b><span>{row.currentCompany || '-'}</span></div> },
          { key: 'experience', label: 'Experience', value: row => row.totalExperienceMonths, render: row => `${Math.floor(row.totalExperienceMonths / 12)}y ${row.totalExperienceMonths % 12}m` },
          { key: 'currentLocation', label: 'Location' },
          { key: 'sourceType', label: 'Source' },
          { key: 'profileStatus', label: 'Status', render: row => <Tag color={row.profileStatus === 'Active' ? 'green' : 'default'}>{row.profileStatus}</Tag> },
        ]}
      />
    </>}

    {tab !== 'resumes' && <>
      <div className="talent-pool-match-toolbar">
        <div><Typography.Text strong>{tab === 'matches' ? 'Match Resume Bank against an approved JD' : 'Selected candidates ready for promotion'}</Typography.Text><Typography.Text type="secondary">The role determines its client, approved JD version and configured ATS profile.</Typography.Text></div>
        {positionControl}
        {tab === 'matches' && <Button data-testid="talent-pool-run-ats" type="primary" icon={<FileSearchOutlined />} disabled={!approvedPositions.some(row => row.id === positionId)} loading={matching} onClick={() => void runMatch()}>Run ATS on Resume Bank</Button>}
      </div>
      {runResult && <Alert type={runResult.warnings.length ? 'warning' : 'success'} showIcon message={`${runResult.scored} scored · ${runResult.queued} queued · ${runResult.skipped} skipped`} description={runResult.warnings.join(' ') || 'Role-specific ATS results are ready for recruiter selection.'} />}
      {matchTable}
    </>}

    <RecruitmentResumeIntake
      open={intakeMode !== null}
      initialMode={intakeMode || 'bulk'}
      talentPoolOnly
      onClose={() => setIntakeMode(null)}
      onCompleted={async () => { await loadResumes(); await onChanged?.() }}
    />

    <Drawer
      open={Boolean(actionCandidate)}
      onClose={() => { ++actionRequest.current; closePreview(); setActionLoading(false); setActionCandidate(null); setActionDetail(null); setActionMatch(null); setActionSkippedAts(false) }}
      width={embeddedPreview ? "min(1120px, 96vw)" : "min(780px, 96vw)"}
      className="talent-pool-action-drawer"
      title={<div className="talent-pool-action-title"><span><FileSearchOutlined /></span><div><strong>Resume preview & hiring action</strong><small>{actionCandidate?.candidateName} · {actionCandidate?.email || actionCandidate?.phone}</small></div></div>}
    >
      {actionLoading && !actionDetail ? <div className="talent-pool-action-loading"><Spin /><span>Opening secure resume profile…</span></div> : actionDetail ? <div className="talent-pool-action-body" data-testid="talent-pool-action-drawer">
        <Card size="small" title="Candidate resume" extra={primaryResume && <Tag color={parsedResume ? 'green' : 'orange'}>{primaryResume.parsingStatus}</Tag>}>
          {primaryResume ? <>
            <Descriptions size="small" column={2}>
              <Descriptions.Item label="File">{primaryResume.originalFileName}</Descriptions.Item>
              <Descriptions.Item label="Uploaded">{new Date(primaryResume.createdAt).toLocaleString('en-IN')}</Descriptions.Item>
              <Descriptions.Item label="Email">{actionDetail.candidate.email || primaryResume.parseFacts?.extractedEmail || '-'}</Descriptions.Item>
              <Descriptions.Item label="Phone">{actionDetail.candidate.phone || primaryResume.parseFacts?.extractedPhone || '-'}</Descriptions.Item>
            </Descriptions>
            <Button data-testid="talent-pool-preview-resume" icon={<EyeOutlined />} loading={previewLoading} onClick={() => void previewResume()}>Preview original resume</Button>
          </> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No current resume is available." />}
        </Card>

        {embeddedPreview && previewUrl && <Card size="small" title={previewName} extra={<Button size="small" onClick={closePreview}>Close preview</Button>}>
          {/^(application\/pdf|text\/plain|image\/(png|jpeg|gif|webp))/.test(previewType) ? <iframe className="ats-pool-inline-preview" data-testid="talent-pool-resume-frame" src={previewUrl} title={previewName} /> : <Alert type="info" showIcon message="This file format is available as an original download." description={<a href={previewUrl} download={previewName}>Download resume</a>} />}
        </Card>}
        <Card size="small" title="Choose approved job / JD">
          <Select
            data-testid="talent-pool-action-position"
            value={actionPositionId || undefined}
            disabled={actionLoading}
            onChange={value => { setActionPositionId(Number(value || 0)); setActionMatch(null) }}
            showSearch
            optionFilterProp="label"
            placeholder="Select approved job role / JD"
            options={approvedPositions.map(row => ({ value: row.id, label: `${row.clientName} · ${row.positionCode} - ${row.positionTitle} · JD v${row.latestJobDescriptionVersionNumber || 1}` }))}
          />
          {selectedActionPosition && <Typography.Text type="secondary">{selectedActionPosition.clientName} · {selectedActionPosition.department || 'No department'} · {selectedActionPosition.jobLocation || 'Location not specified'}</Typography.Text>}
          <Alert type="info" showIcon message="Choose how to proceed" description="Run ATS compares this resume with the approved JD. Skip ATS selects it directly and records the recruiter decision in the audit trail." />
          <div className="talent-pool-decision-actions">
            <Button data-testid="talent-pool-run-candidate-ats" type="primary" icon={<ThunderboltOutlined />} disabled={!primaryResume || !parsedResume || !selectedActionPosition} loading={actionLoading} onClick={() => void runCandidateAts()}>Run ATS for this JD</Button>
            <Button data-testid="talent-pool-skip-ats" icon={<CheckCircleOutlined />} disabled={!primaryResume || !selectedActionPosition} loading={actionLoading} onClick={() => void skipAtsAndSelect()}>Skip ATS & select</Button>
          </div>
          {!parsedResume && primaryResume && <Typography.Text type="warning">Automatic ATS needs parsed text. You can still preview and select this resume directly.</Typography.Text>}
        </Card>

        {actionMatch && <Card data-testid="talent-pool-decision-result" size="small" className="talent-pool-decision-result" title="Decision result">
          <div><Tag color={actionMatch.currentStatus === 'Selected' ? 'purple' : 'blue'}>{actionMatch.currentStatus}</Tag>{actionSkippedAts && <Tag color="cyan">ATS skipped for this decision</Tag>}{actionMatch.atsScore == null ? !actionSkippedAts && <Tag>Not scored</Tag> : <Tag color={actionMatch.atsScore >= 60 ? 'green' : 'orange'}>{actionSkippedAts ? 'Previous ATS: ' : ''}{actionMatch.atsScore.toFixed(1)} / 100</Tag>}</div>
          <Typography.Text strong>{actionMatch.positionTitle}</Typography.Text>
          <Typography.Text type="secondary">{actionMatch.clientName} · {actionMatch.positionCode}</Typography.Text>
          <Space wrap>
            {actionMatch.currentStatus !== 'Selected' && <Button type="primary" icon={<CheckCircleOutlined />} loading={actionLoading} onClick={() => void selectActionMatch()}>Select after ATS</Button>}
            {actionMatch.currentStatus === 'Selected' && <Button type="primary" icon={<RocketOutlined />} loading={actionLoading} onClick={() => void promoteActionMatch()}>Move to hiring pipeline</Button>}
          </Space>
        </Card>}
      </div> : <Empty description="Candidate resume could not be loaded." />}
    </Drawer>

    <Modal open={!embeddedPreview && Boolean(previewUrl)} onCancel={closePreview} footer={null} width="min(1120px, 96vw)" title={previewName} destroyOnClose zIndex={1400} className="talent-pool-preview-modal">
      {previewUrl && <iframe data-testid="talent-pool-resume-frame" src={previewUrl} title={previewName} />}
    </Modal>
  </div>
}
