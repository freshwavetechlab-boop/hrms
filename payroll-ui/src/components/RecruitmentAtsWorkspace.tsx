import { useCallback, useEffect, useMemo, useState } from 'react'
import { Badge, Button, Card, Col, Descriptions, Drawer, Empty, Input, Popconfirm, Progress, Row, Select, Space, Spin, Statistic, Tag, Typography } from 'antd'
import { DeleteOutlined, EyeOutlined, FileAddOutlined, ReloadOutlined, RobotOutlined, UploadOutlined } from '@ant-design/icons'
import { useAuthSession } from './AuthGate'
import DataTable from './DataTable'
import RecruitmentAtsScoreDetails from './RecruitmentAtsScoreDetails'
import RecruitmentResumeIntake, { type RecruitmentResumeIntakeMode } from './RecruitmentResumeIntake'
import { getClients } from '../services/payrollService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import { deleteApplication, getApplications, getCandidate, scoreApplication } from '../services/recruitmentTalentService'
import type { Client, RecruitmentCandidateApplication, RecruitmentCandidateDetail, RecruitmentOpenPosition } from '../types/payroll'
import './RecruitmentResumeIntake.css'

type Props = {
  initialClientId?: number
  initialPositionId?: number
  initialUploadMode?: RecruitmentResumeIntakeMode
}

type IntakeLaunch = {
  mode: RecruitmentResumeIntakeMode
  clientId: number
  positionId: number
  jobPostingId?: number | null
}

export default function RecruitmentAtsWorkspace({ initialClientId = 0, initialPositionId = 0, initialUploadMode }: Props) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [positions, setPositions] = useState<RecruitmentOpenPosition[]>([])
  const [applications, setApplications] = useState<RecruitmentCandidateApplication[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [positionId, setPositionId] = useState(initialPositionId)
  const [stage, setStage] = useState('')
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [scoringId, setScoringId] = useState(0)
  const [intake, setIntake] = useState<IntakeLaunch | null>(() => initialUploadMode ? { mode: initialUploadMode, clientId: initialClientId, positionId: initialPositionId, jobPostingId: null } : null)
  const [detail, setDetail] = useState<RecruitmentCandidateDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    const [clientRows, positionRows, applicationRows] = await Promise.all([getClients(), getRecruitmentOpenPositions(), getApplications()])
    setClients(clientRows)
    setPositions(positionRows)
    setApplications(applicationRows)
    if (!clientId && clientRows.length === 1) setClientId(clientRows[0].id)
    setLoading(false)
  }, [clientId])

  useEffect(() => {
    let active = true
    void Promise.all([getClients(), getRecruitmentOpenPositions(), getApplications()]).then(([clientRows, positionRows, applicationRows]) => {
      if (!active) return
      setClients(clientRows)
      setPositions(positionRows)
      setApplications(applicationRows)
      if (!initialClientId && clientRows.length === 1) setClientId(clientRows[0].id)
      setLoading(false)
    })
    return () => { active = false }
  }, [initialClientId])

  const availablePositions = useMemo(() => positions.filter(row => !clientId || row.clientId === clientId), [positions, clientId])
  const stages = useMemo(() => Array.from(new Set(applications.map(row => row.currentStage).filter(Boolean))).sort(), [applications])
  const rows = useMemo(() => applications.filter(row => {
    if (clientId && row.clientId !== clientId) return false
    if (positionId && row.positionId !== positionId) return false
    if (stage && row.currentStage !== stage) return false
    if (!query.trim()) return true
    const text = `${row.applicationCode} ${row.candidateName} ${row.candidateEmail} ${row.candidatePhone} ${row.positionCode} ${row.positionTitle}`.toLowerCase()
    return text.includes(query.trim().toLowerCase())
  }), [applications, clientId, positionId, stage, query])
  const scored = rows.filter(row => row.atsScore != null).length
  const atOrAboveSixty = rows.filter(row => (row.atsScore ?? -1) >= 60).length
  const needsScoring = rows.filter(row => row.atsScore == null).length

  const launchIntake = (mode: RecruitmentResumeIntakeMode, application?: RecruitmentCandidateApplication) => setIntake({
    mode,
    clientId: application?.clientId || clientId,
    positionId: application?.positionId || positionId,
    jobPostingId: application?.jobPostingId ?? null,
  })
  const recalculate = async (row: RecruitmentCandidateApplication) => {
    setScoringId(row.id)
    const response = await scoreApplication(row.id)
    setScoringId(0)
    if (response.ok) await load()
  }
  const openEvidence = async (row: RecruitmentCandidateApplication) => {
    setDetailLoading(true)
    setDetail(null)
    const candidate = await getCandidate(row.candidateId)
    setDetail(candidate)
    setDetailLoading(false)
  }

  return <div className="ats-screening-workspace" data-testid="ats-screening-workspace">
    <Card className="ats-workbench-hero" bordered={false}>
      <div className="ats-workbench-heading">
        <div className="ats-workbench-icon"><RobotOutlined /></div>
        <div><Typography.Title level={3}>ATS screening workbench</Typography.Title><Typography.Paragraph>Upload resumes against a live job or review every scored application from one table. Scores remain explainable and require human review.</Typography.Paragraph></div>
        <Space wrap className="ats-workbench-primary-actions">
          <Button icon={<FileAddOutlined />} onClick={() => launchIntake('single')}>Single resume</Button>
          <Button type="primary" icon={<UploadOutlined />} onClick={() => launchIntake('bulk')}>Bulk resumes</Button>
        </Space>
      </div>
    </Card>

    <div className="ats-workbench-metrics">
      <Card size="small"><Statistic title="Visible applications" value={rows.length} /></Card>
      <Card size="small"><Statistic title="Scored" value={scored} valueStyle={{ color: '#2563eb' }} /></Card>
      <Card size="small"><Statistic title="Score 60+" value={atOrAboveSixty} valueStyle={{ color: '#15803d' }} /></Card>
      <Card size="small"><Statistic title="Needs scoring" value={needsScoring} valueStyle={{ color: needsScoring ? '#b45309' : '#64748b' }} /></Card>
    </div>

    <Card size="small" className="ats-workbench-table-card" title="Candidate applications" extra={<Badge count={rows.length} showZero color="#5b4ce6" />}>
      <div className="ats-workbench-filters">
        <Select showSearch optionFilterProp="label" value={clientId || undefined} placeholder="All permitted clients" allowClear options={clients.map(row => ({ value: row.id, label: `${row.code} - ${row.name}` }))} onChange={value => { setClientId(value || 0); setPositionId(0) }} />
        <Select showSearch optionFilterProp="label" value={positionId || undefined} placeholder="All positions" allowClear options={availablePositions.map(row => ({ value: row.id, label: `${row.positionCode} - ${row.positionTitle}` }))} onChange={value => setPositionId(value || 0)} />
        <Select value={stage || undefined} placeholder="All pipeline stages" allowClear options={stages.map(value => ({ value, label: value }))} onChange={value => setStage(value || '')} />
        <Input.Search value={query} allowClear placeholder="Candidate, email, application or position" onChange={event => setQuery(event.target.value)} />
        <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void load()}>Refresh</Button>
      </div>

      <div data-testid="ats-applications-table">
        {loading && !applications.length ? <div className="ats-workbench-loading"><Spin size="large" /><span>Loading applications...</span></div> : <DataTable<RecruitmentCandidateApplication>
          rows={rows}
          exportFileName="ats-screening-applications"
          emptyText="No applications match these filters. Upload a resume to start screening."
          actions={row => <Space size={4} wrap>
            <Button size="small" icon={<EyeOutlined />} onClick={() => void openEvidence(row)}>Evidence</Button>
            <Button size="small" loading={scoringId === row.id} disabled={!row.resumeId} onClick={() => void recalculate(row)}>Score</Button>
            <Button size="small" type="primary" icon={<UploadOutlined />} onClick={() => launchIntake('single', row)}>Resume</Button>
            {canDelete && <Popconfirm title="Delete this application?" description="ATS scores and safe pipeline data will be removed. Interviews, offers and joined records are protected." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteApplication(row.id); if (response.ok) { if (detail?.applications.some(item => item.id === row.id)) setDetail(null); await load() } }}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
          </Space>}
          columns={[
            { key: 'applicationCode', label: 'Application', width: '150px', render: row => <div className="ats-primary-cell"><b>{row.applicationCode}</b><span>{new Date(row.appliedAt).toLocaleDateString('en-IN')}</span></div> },
            { key: 'candidateName', label: 'Candidate', width: '230px', render: row => <div className="ats-primary-cell"><b>{row.candidateName}</b><span>{row.candidateEmail || row.candidatePhone || row.candidateCode}</span></div>, value: row => row.candidateName },
            { key: 'positionTitle', label: 'Job / JD context', width: '240px', render: row => <div className="ats-primary-cell"><b>{row.positionTitle}</b><span>{row.positionCode} · {row.clientName}</span></div>, value: row => row.positionTitle },
            { key: 'currentStage', label: 'Pipeline stage', width: '155px', render: row => <Tag color="blue">{row.currentStage}</Tag> },
            { key: 'atsScore', label: 'ATS score', width: '155px', value: row => row.atsScore ?? '', render: row => row.atsScore == null ? <Tag color="orange">{row.scoreStatus || 'Not scored'}</Tag> : <div className="ats-score-cell"><Progress percent={Math.max(0, Math.min(100, row.atsScore))} size="small" strokeColor={row.atsScore >= 60 ? '#16a34a' : '#d97706'} showInfo={false} /><b>{row.atsScore.toFixed(1)}</b></div> },
            { key: 'sourceType', label: 'Source', width: '145px' },
            { key: 'recruiterName', label: 'Recruiter', width: '170px', render: row => row.recruiterName || '-' },
          ]}
        />}
      </div>
    </Card>

    {intake && <RecruitmentResumeIntake
      open
      initialMode={intake.mode}
      initialClientId={intake.clientId}
      initialPositionId={intake.positionId}
      initialJobPostingId={intake.jobPostingId}
      onClose={() => setIntake(null)}
      onCompleted={load}
    />}

    <Drawer open={detailLoading || !!detail} onClose={() => { setDetail(null); setDetailLoading(false) }} width="min(920px, 96vw)" destroyOnClose title="ATS score evidence">
      {detailLoading && <div className="ats-workbench-loading"><Spin size="large" /><span>Loading score evidence...</span></div>}
      {!detailLoading && detail && <Space direction="vertical" size="large" style={{ width: '100%' }}>
        <Card size="small"><Row gutter={[16, 16]} align="middle"><Col flex="auto"><Typography.Title level={4} style={{ margin: 0 }}>{detail.candidate.candidateName}</Typography.Title><Typography.Text type="secondary">{detail.candidate.candidateCode} · {detail.candidate.email || detail.candidate.phone}</Typography.Text></Col><Col><Tag color="blue">{detail.candidate.profileStatus}</Tag></Col></Row></Card>
        <Descriptions size="small" bordered column={{ xs: 1, sm: 2 }}><Descriptions.Item label="Current role">{detail.candidate.currentTitle || '-'}</Descriptions.Item><Descriptions.Item label="Location">{detail.candidate.currentLocation || '-'}</Descriptions.Item><Descriptions.Item label="Experience">{Math.floor(detail.candidate.totalExperienceMonths / 12)}y {detail.candidate.totalExperienceMonths % 12}m</Descriptions.Item><Descriptions.Item label="Applications">{detail.applications.length}</Descriptions.Item></Descriptions>
        <RecruitmentAtsScoreDetails scores={detail.scores} applications={detail.applications} />
      </Space>}
      {!detailLoading && !detail && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="ATS evidence is unavailable." />}
    </Drawer>
  </div>
}
