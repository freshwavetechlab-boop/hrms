import { useEffect, useMemo, useState } from 'react'
import dayjs from 'dayjs'
import {
  CheckCircleOutlined, CloseCircleOutlined, CopyOutlined, EditOutlined, GlobalOutlined,
  LinkOutlined, PlusOutlined, RocketOutlined, SaveOutlined, SettingOutlined,
} from '@ant-design/icons'
import {
  Alert, Badge, Button, Card, Col, DatePicker, Descriptions, Empty, Form, Input, InputNumber,
  List, Modal, Row, Segmented, Select, Space, Spin, Switch, Tag, Typography, message,
} from 'antd'
import { getClients } from '../services/payrollService'
import {
  assignRecruitmentPipeline, closeRecruitmentJobPosting, getRecruitmentJobDescriptions,
  getRecruitmentJobPostings, getRecruitmentOrchestrationLookups, getRecruitmentPipelines,
  getRecruitmentPositionPipelineAssignment, publishRecruitmentJobPosting, saveRecruitmentJobPosting,
} from '../services/recruitmentOrchestrationService'
import type { Client } from '../types/payroll'
import type {
  RecruitmentJobDescriptionVersion, RecruitmentJobPosting, RecruitmentOrchestrationLookups,
  RecruitmentPipelineDefinition, RecruitmentPositionPipelineAssignment,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = {
  initialClientId?: number
  initialPositionId?: number
  onPublished?: (posting: RecruitmentJobPosting) => void
}

const emptyLookups: RecruitmentOrchestrationLookups = {
  lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [],
}
const editableStatuses = new Set(['Draft'])

export default function RecruitmentJobPostingManager({ initialClientId = 0, initialPositionId = 0, onPublished }: Props) {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [lookups, setLookups] = useState(emptyLookups)
  const [pipelines, setPipelines] = useState<RecruitmentPipelineDefinition[]>([])
  const [postings, setPostings] = useState<RecruitmentJobPosting[]>([])
  const [editor, setEditor] = useState<RecruitmentJobPosting | null>(null)
  const [jobDescriptions, setJobDescriptions] = useState<RecruitmentJobDescriptionVersion[]>([])
  const [pipelineVersionId, setPipelineVersionId] = useState<number>()
  const [pipelineAssignment, setPipelineAssignment] = useState<RecruitmentPositionPipelineAssignment | null>(null)
  const [listStatus, setListStatus] = useState('All')
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    void getClients().then(rows => {
      setClients(rows)
      if (!clientId && rows.length) setClientId(rows[0].id)
    })
  }, [])

  useEffect(() => {
    if (!clientId) return
    void loadClient(clientId)
  }, [clientId])

  const positions = useMemo(() => lookups.positions.filter(row => row.clientId === clientId), [lookups.positions, clientId])
  const visiblePostings = useMemo(() => postings.filter(row => {
    const statusMatch = listStatus === 'All' || row.status === listStatus
    const needle = search.trim().toLowerCase()
    return statusMatch && (!needle || `${row.publicTitle} ${row.positionCode} ${row.positionTitle}`.toLowerCase().includes(needle))
  }), [postings, listStatus, search])
  const selectedPosition = positions.find(row => row.id === editor?.positionId)
  const readOnly = !!editor?.id && !editableStatuses.has(editor.status)
  const publishedPipelineOptions = useMemo(() => pipelines
    .filter(row => row.isActive && row.currentPublishedVersionId)
    .map(row => ({ value: Number(row.currentPublishedVersionId), label: `${row.pipelineName} · published`, definitionId: row.id })), [pipelines])
  const selectedPipeline = publishedPipelineOptions.find(row => row.value === pipelineVersionId)
  const applicationFormOptions = useMemo(() => {
    const rows = lookups.forms.filter(row => row.status === 'Active' && (row.clientId === clientId || row.clientId === 0))
      .flatMap(row => {
        const versions = (row.versions || []).filter(version => ['Published', 'Retired'].includes(version.status))
        if (versions.length) return versions.map(version => ({ value: version.id, label: `${row.formName} · v${version.versionNumber}${version.status === 'Retired' ? ' (retired)' : ''}` }))
        return row.currentPublishedVersionId ? [{ value: Number(row.currentPublishedVersionId), label: `${row.formName} · current published` }] : []
      })
    if (editor?.applicationFormVersionId && !rows.some(row => row.value === editor.applicationFormVersionId)) rows.push({ value: editor.applicationFormVersionId, label: `Assigned form version #${editor.applicationFormVersionId}` })
    return rows
  }, [lookups.forms, clientId, editor?.applicationFormVersionId])

  async function loadClient(scope: number, preferredPostingId = 0) {
    setLoading(true)
    const [lookupRows, postingRows, pipelineRows] = await Promise.all([
      getRecruitmentOrchestrationLookups(scope), getRecruitmentJobPostings(scope), getRecruitmentPipelines(scope),
    ])
    setLookups(lookupRows); setPostings(postingRows); setPipelines(pipelineRows)
    const preferred = postingRows.find(row => row.id === preferredPostingId)
    if (preferred) await choosePosting(preferred, lookupRows.positions)
    else if (!editor || editor.clientId !== scope) {
      setEditor(null); setJobDescriptions([]); setPipelineVersionId(undefined); setPipelineAssignment(null)
      if (initialPositionId && lookupRows.positions.some(row => row.id === initialPositionId)) await startNew(initialPositionId, lookupRows)
    }
    setLoading(false)
  }

  async function loadPositionContext(positionId: number, allPositions = positions) {
    const position = allPositions.find(row => row.id === positionId)
    const descriptionPromise = position?.requisitionId ? getRecruitmentJobDescriptions(position.requisitionId) : Promise.resolve([])
    const [descriptionRows, assignment] = await Promise.all([
      descriptionPromise, getRecruitmentPositionPipelineAssignment(positionId),
    ])
    setJobDescriptions(descriptionRows.filter(row => row.status === 'Approved'))
    setPipelineAssignment(assignment)
    setPipelineVersionId(assignment?.pipelineVersionId || undefined)
    return descriptionRows
  }

  async function choosePosting(row: RecruitmentJobPosting, allPositions = positions) {
    setLoading(true)
    setEditor({ ...row })
    await loadPositionContext(row.positionId, allPositions)
    setLoading(false)
  }

  async function startNew(positionId = 0, allLookups = lookups) {
    const position = allLookups.positions.find(row => row.id === positionId)
    setEditor(blankPosting(clientId, positionId, position?.positionTitle ?? ''))
    setJobDescriptions([]); setPipelineVersionId(undefined); setPipelineAssignment(null)
    if (positionId) {
      const descriptions = await loadPositionContext(positionId, allLookups.positions)
      const approved = descriptions.find(row => row.status === 'Approved')
      setEditor(current => current ? { ...current, jobDescriptionVersionId: approved?.id ?? 0 } : current)
    }
  }

  async function changePosition(positionId: number) {
    const position = positions.find(row => row.id === positionId)
    setEditor(current => current ? {
      ...current, positionId, positionCode: position?.positionCode ?? '', positionTitle: position?.positionTitle ?? '',
      publicTitle: current.id ? current.publicTitle : position?.positionTitle ?? '', jobDescriptionVersionId: 0,
    } : blankPosting(clientId, positionId, position?.positionTitle ?? ''))
    const descriptions = await loadPositionContext(positionId)
    const approved = descriptions.find(row => row.status === 'Approved')
    if (approved) setEditor(current => current ? { ...current, jobDescriptionVersionId: approved.id } : current)
  }

  function patch(value: Partial<RecruitmentJobPosting>) {
    setEditor(current => current ? { ...current, ...value } : current)
  }

  async function save() {
    if (!editor || readOnly) return
    const error = validatePosting(editor, false)
    if (error) return message.warning(error)
    setSaving(true)
    const response = await saveRecruitmentJobPosting({
      id: editor.id, positionId: editor.positionId, jobDescriptionVersionId: editor.jobDescriptionVersionId,
      applicationFormVersionId: editor.applicationFormVersionId || null, publicTitle: editor.publicTitle.trim(),
      opensAtUtc: editor.opensAtUtc || null, closesAtUtc: editor.closesAtUtc || null,
      maximumApplications: editor.maximumApplications || null, searchEngineVisible: editor.searchEngineVisible,
    })
    setSaving(false)
    if (!response.ok || !response.data) return
    await loadClient(clientId, response.data.id)
  }

  async function assignPipeline() {
    if (!editor?.positionId || !pipelineVersionId) return message.warning('Select a position and published pipeline.')
    setSaving(true)
    const response = await assignRecruitmentPipeline({ positionId: editor.positionId, jobPostingId: null, pipelineVersionId })
    setSaving(false)
    if (response.ok && response.data) setPipelineAssignment(response.data)
  }

  function publish() {
    if (!editor?.id) return message.info('Save the posting draft before publishing.')
    const error = validatePosting(editor, true)
    if (error) return message.warning(error)
    if (!pipelineAssignment?.isActive) return message.warning('Assign a published hiring pipeline before publishing.')
    Modal.confirm({
      title: 'Publish this job?', icon: <RocketOutlined />, okText: 'Publish now',
      content: 'The public careers link will become available immediately (subject to the opening date). The assigned form and pipeline versions remain stable for applicants.',
      onOk: async () => {
        setSaving(true)
        const response = await publishRecruitmentJobPosting(editor.id)
        setSaving(false)
        if (response.ok && response.data) { onPublished?.(response.data); await loadClient(clientId, editor.id) }
      },
    })
  }

  function closePosting() {
    if (!editor?.id) return
    Modal.confirm({
      title: 'Close this public job?', icon: <CloseCircleOutlined />, okText: 'Close posting', okButtonProps: { danger: true },
      content: 'New public applications will stop. Existing candidate and pipeline records remain available.',
      onOk: async () => { const response = await closeRecruitmentJobPosting(editor.id); if (response.ok) await loadClient(clientId) },
    })
  }

  async function copyLink() {
    if (!editor?.publicSlug) return
    const link = publicLink(editor.publicSlug)
    try { await navigator.clipboard.writeText(link); message.success('Public careers link copied.') }
    catch { message.info(link) }
  }

  return <section className="orchestration-shell posting-manager">
    <div className="orchestration-toolbar">
      <div>
        <span className="orchestration-kicker">Approved JD to external careers page</span>
        <h2 className="orchestration-title">Job Posting Manager</h2>
        <p className="orchestration-subtitle">Bind an approved JD, published application form and published pipeline before going live.</p>
      </div>
      <Space wrap>
        <Select value={clientId || undefined} placeholder="Select client" showSearch optionFilterProp="label" style={{ minWidth: 230 }}
          options={clients.map(row => ({ value: row.id, label: row.name }))}
          onChange={value => { setClientId(value); setEditor(null) }} />
        <Button type="primary" icon={<PlusOutlined />} onClick={() => void startNew()}>New posting</Button>
      </Space>
    </div>

    <Spin spinning={loading}>
      <div className="posting-workspace-layout">
        <Card size="small" className="posting-list" title={`Postings (${visiblePostings.length})`}>
          <Space direction="vertical" size={10} style={{ width: '100%' }}>
            <Input.Search allowClear value={search} onChange={event => setSearch(event.target.value)} placeholder="Search title or position" />
            <Segmented block size="small" value={listStatus} onChange={value => setListStatus(String(value))} options={['All', 'Draft', 'Published', 'Closed']} />
            <List dataSource={visiblePostings} locale={{ emptyText: 'No matching job postings.' }} renderItem={row => <List.Item className={editor?.id === row.id ? 'active' : ''} onClick={() => void choosePosting(row)}>
              <List.Item.Meta title={<Space><Typography.Text strong ellipsis>{row.publicTitle}</Typography.Text><PostingStatus status={row.status} /></Space>} description={<Space direction="vertical" size={2}><span>{row.positionCode} · {row.positionTitle}</span><span>{row.applicationCount} application{row.applicationCount === 1 ? '' : 's'}</span></Space>} />
            </List.Item>} />
          </Space>
        </Card>

        {!editor ? <Card><Empty description="Select a posting or create a new one." /></Card> : <div className="posting-editor">
          <Card size="small">
            <div className="orchestration-toolbar">
              <Space wrap><PostingStatus status={editor.status} />{editor.applicationCount > 0 && <Badge count={editor.applicationCount} overflowCount={99999} showZero color="#6b4eff" />}</Space>
              <Space wrap>
                {editor.publicSlug && <Button icon={<CopyOutlined />} onClick={() => void copyLink()}>Copy public link</Button>}
                {editor.status === 'Published' && <Button danger icon={<CloseCircleOutlined />} onClick={closePosting}>Close</Button>}
                {(editor.status === 'Draft' || editor.status === 'Closed') && <Button type="primary" icon={<RocketOutlined />} disabled={!editor.id} onClick={publish}>Publish</Button>}
                <Button icon={<SaveOutlined />} loading={saving} disabled={readOnly} onClick={() => void save()}>Save draft</Button>
              </Space>
            </div>
            {readOnly && <Alert className="jd-readonly-alert" type="info" showIcon message="Published details are locked" description="Close this posting and create a new posting if the approved JD, form or schedule must change." />}
            {editor.publicSlug && <div className="public-link-banner"><GlobalOutlined /><div><Typography.Text type="secondary">Public candidate URL</Typography.Text><Typography.Link href={publicLink(editor.publicSlug)} target="_blank" rel="noreferrer">{publicLink(editor.publicSlug)} <LinkOutlined /></Typography.Link></div></div>}
          </Card>

          <Card size="small" title={<Space><EditOutlined /> Posting details</Space>}>
            <Form layout="vertical" disabled={readOnly}>
              <Row gutter={14}>
                <Col xs={24} lg={12}><Form.Item label="Open position" required><Select value={editor.positionId || undefined} showSearch optionFilterProp="label" placeholder="Select an approved open position" onChange={value => void changePosition(value)} options={positions.map(row => ({ value: row.id, label: `${row.positionCode} · ${row.positionTitle} · ${row.department || 'No department'}`, disabled: row.remainingPositions === 0 }))} /></Form.Item></Col>
                <Col xs={24} lg={12}><Form.Item label="Public job title" required><Input value={editor.publicTitle} maxLength={240} onChange={event => patch({ publicTitle: event.target.value })} /></Form.Item></Col>
              </Row>
              {selectedPosition && <Descriptions className="posting-position-summary" size="small" column={{ xs: 1, sm: 2, lg: 4 }}>
                <Descriptions.Item label="RFR">{selectedPosition.rfrNumber || '—'}</Descriptions.Item><Descriptions.Item label="Department">{selectedPosition.department || '—'}</Descriptions.Item><Descriptions.Item label="Location">{selectedPosition.jobLocation || '—'}</Descriptions.Item><Descriptions.Item label="Open seats">{selectedPosition.remainingPositions ?? '—'}</Descriptions.Item>
              </Descriptions>}
              <Row gutter={14}>
                <Col xs={24} lg={12}><Form.Item label="Approved job description" required extra={!editor.positionId ? 'Select a position first.' : !jobDescriptions.length ? 'No approved JD exists for this requisition.' : 'Only workflow-approved versions can be posted.'}><Select value={editor.jobDescriptionVersionId || undefined} disabled={readOnly || !jobDescriptions.length} placeholder="Select approved JD version" options={jobDescriptions.map(row => ({ value: row.id, label: `v${row.versionNumber} · ${row.title}` }))} onChange={jobDescriptionVersionId => patch({ jobDescriptionVersionId })} /></Form.Item></Col>
                <Col xs={24} lg={12}><Form.Item label="Published candidate application form" required extra="The selected immutable form version is used for every new application."><Select value={editor.applicationFormVersionId || undefined} allowClear showSearch optionFilterProp="label" placeholder="Select published form" options={applicationFormOptions} onChange={applicationFormVersionId => patch({ applicationFormVersionId: applicationFormVersionId ?? null })} /></Form.Item></Col>
              </Row>
              <Row gutter={14}>
                <Col xs={24} md={8}><Form.Item label="Opens at"><DatePicker showTime style={{ width: '100%' }} value={editor.opensAtUtc ? dayjs(editor.opensAtUtc) : null} onChange={value => patch({ opensAtUtc: value?.toISOString() ?? null })} /></Form.Item></Col>
                <Col xs={24} md={8}><Form.Item label="Closes at"><DatePicker showTime style={{ width: '100%' }} value={editor.closesAtUtc ? dayjs(editor.closesAtUtc) : null} onChange={value => patch({ closesAtUtc: value?.toISOString() ?? null })} /></Form.Item></Col>
                <Col xs={24} md={8}><Form.Item label="Maximum applications"><InputNumber min={1} precision={0} style={{ width: '100%' }} value={editor.maximumApplications} placeholder="Unlimited" onChange={value => patch({ maximumApplications: value == null ? null : Number(value) })} /></Form.Item></Col>
              </Row>
              <Form.Item label="Allow search engine indexing" extra="Turn this off for private or link-only hiring campaigns."><Switch checked={editor.searchEngineVisible} onChange={searchEngineVisible => patch({ searchEngineVisible })} checkedChildren="Visible" unCheckedChildren="Private" /></Form.Item>
            </Form>
          </Card>

          <Card size="small" title={<Space><SettingOutlined /> Hiring pipeline</Space>} extra={pipelineAssignment?.isActive && pipelineAssignment.pipelineVersionId === pipelineVersionId ? <Tag icon={<CheckCircleOutlined />} color="success">Assigned</Tag> : <Tag color="warning">Not assigned</Tag>}>
            <Alert showIcon type="info" message="Stable position-level pipeline" description="Applications created from this posting start in the initial stage of the assigned published version. Future pipeline edits do not mutate active applications." />
            <Row gutter={12} align="bottom" className="pipeline-assignment-row">
              <Col xs={24} lg={18}><Form.Item label="Published pipeline"><Select value={pipelineVersionId} disabled={readOnly || !editor.positionId} showSearch optionFilterProp="label" placeholder="Select a published pipeline" options={publishedPipelineOptions} onChange={setPipelineVersionId} /></Form.Item></Col>
              <Col xs={24} lg={6}><Button block type="primary" ghost icon={<SettingOutlined />} loading={saving} disabled={readOnly || !pipelineVersionId || !editor.positionId} onClick={() => void assignPipeline()}>Assign pipeline</Button></Col>
            </Row>
            {selectedPipeline && <Typography.Text type="secondary">Selected: {selectedPipeline.label}</Typography.Text>}
          </Card>
        </div>}
      </div>
    </Spin>
  </section>
}

function PostingStatus({ status }: { status: string }) {
  const color = status === 'Published' ? 'green' : status === 'Closed' ? 'default' : 'gold'
  return <Tag color={color}>{status || 'Draft'}</Tag>
}

function blankPosting(clientId: number, positionId = 0, positionTitle = ''): RecruitmentJobPosting {
  return {
    id: 0, clientId, positionId, jobDescriptionVersionId: 0, applicationFormVersionId: null,
    publicSlug: '', publicTitle: positionTitle, status: 'Draft', opensAtUtc: null, closesAtUtc: null,
    maximumApplications: null, applicationCount: 0, searchEngineVisible: true, publishedAtUtc: null,
    positionCode: '', positionTitle, clientName: '',
  }
}

function validatePosting(row: RecruitmentJobPosting, publishing: boolean) {
  if (!row.positionId) return 'Select an open position.'
  if (!row.jobDescriptionVersionId) return 'Select an approved job-description version.'
  if (!row.publicTitle.trim()) return 'Enter the public job title.'
  if (row.opensAtUtc && row.closesAtUtc && !dayjs(row.closesAtUtc).isAfter(dayjs(row.opensAtUtc))) return 'Closing date must be after the opening date.'
  if (publishing && !row.applicationFormVersionId) return 'Select a published candidate application form before publishing.'
  if (publishing && row.closesAtUtc && !dayjs(row.closesAtUtc).isAfter(dayjs())) return 'Closing date must be in the future.'
  return ''
}

function publicLink(slug: string) { return `${window.location.origin}/careers/${encodeURIComponent(slug)}` }
