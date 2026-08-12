import { useEffect, useMemo, useState } from 'react'
import dayjs from 'dayjs'
import {
  CheckCircleOutlined, CloseCircleOutlined, CopyOutlined, DeleteOutlined, EditOutlined, GlobalOutlined,
  LinkOutlined, PlusOutlined, RocketOutlined, SaveOutlined, SettingOutlined,
} from '@ant-design/icons'
import {
  Alert, Badge, Button, Card, Col, DatePicker, Descriptions, Empty, Form, Input, InputNumber,
  List, Modal, Popconfirm, Row, Segmented, Select, Space, Spin, Switch, Tag, Typography,
} from 'antd'
import { useAuthSession } from './AuthGate'
import { useToast, type ToastType } from './ToastProvider'
import { getClients } from '../services/payrollService'
import {
  assignRecruitmentPipeline, closeRecruitmentJobPosting, deleteRecruitmentJobPosting, getRecruitmentJobDescriptions,
  getRecruitmentJobPostings, getRecruitmentOrchestrationLookups, getRecruitmentPipelines, getPublicCareerJob,
  getRecruitmentPositionPipelineAssignment, normalizePublicCareerUrl, publishRecruitmentJobPosting, saveRecruitmentJobPosting,
} from '../services/recruitmentOrchestrationService'
import type { Client } from '../types/payroll'
import type {
  RecruitmentJobDescriptionVersion, RecruitmentJobPosting, RecruitmentOrchestrationLookups,
  RecruitmentPipelineDefinition, RecruitmentPositionPipelineAssignment,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  initialPositionId?: number
  onPublished?: (posting: RecruitmentJobPosting) => void
}

const emptyLookups: RecruitmentOrchestrationLookups = {
  lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [],
}
const editableStatuses = new Set(['Draft'])
type ActionFeedback = { type: ToastType; message: string; description?: string }
type ConfirmationAction = 'publish' | 'close'

export default function RecruitmentJobPostingManager({ initialClientId = 0, clientScopeManaged = false, initialPositionId = 0, onPublished }: Props) {
  const session = useAuthSession()
  const notify = useToast()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const canViewAllClients = session?.user.clientId == null
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId || session?.user.clientId || 0)
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
  const [actionBusy, setActionBusy] = useState(false)
  const [confirmationAction, setConfirmationAction] = useState<ConfirmationAction | null>(null)
  const [confirmationError, setConfirmationError] = useState('')
  const [actionFeedback, setActionFeedback] = useState<ActionFeedback | null>(null)

  useEffect(() => {
    void getClients().then(rows => {
      setClients(rows)
      if (canViewAllClients) {
        setClientId(current => current > 0 && rows.some(row => row.id === current) ? current : 0)
      } else if (!clientId && rows.length) setClientId(session?.user.clientId || rows[0].id)
    })
  }, [])

  useEffect(() => {
    if (!clientId && !canViewAllClients) return
    void loadClient(clientId)
  }, [clientId])

  const editorClientId = editor?.id ? editor.clientId : clientId
  const positions = useMemo(() => editorClientId > 0
    ? lookups.positions.filter(row => row.clientId === editorClientId)
    : lookups.positions, [lookups.positions, editorClientId])
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
  const publicUrl = normalizePublicCareerUrl(editor?.publicUrl, editor?.publicSlug)
  const publishingIssues = editor
    ? validatePublishing(editor, pipelineAssignment, pipelineVersionId, publicUrl)
    : []
  const applicationFormOptions = useMemo(() => {
    const rows = lookups.forms.filter(row => row.status === 'Active' && (editorClientId <= 0 || row.clientId === editorClientId || row.clientId === 0))
      .flatMap(row => {
        const versions = (row.versions || []).filter(version => ['Published', 'Retired'].includes(version.status))
        if (versions.length) return versions.map(version => ({ value: version.id, label: `${row.formName} · v${version.versionNumber}${version.status === 'Retired' ? ' (retired)' : ''}` }))
        return row.currentPublishedVersionId ? [{ value: Number(row.currentPublishedVersionId), label: `${row.formName} · current published` }] : []
      })
    if (editor?.applicationFormVersionId && !rows.some(row => row.value === editor.applicationFormVersionId)) rows.push({ value: editor.applicationFormVersionId, label: `Assigned form version #${editor.applicationFormVersionId}` })
    return rows
  }, [lookups.forms, editorClientId, editor?.applicationFormVersionId])

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
    setActionFeedback(null)
  }

  function report(type: ToastType, message: string, description = '') {
    setActionFeedback({ type, message, description })
    notify(description ? `${message} ${description}` : message, type)
  }

  async function save() {
    if (!editor || readOnly) return
    const error = validatePosting(editor, false)
    if (error) { report('warning', 'Draft not saved', error); return }
    setSaving(true)
    const response = await saveRecruitmentJobPosting({
      id: editor.id, positionId: editor.positionId, jobDescriptionVersionId: editor.jobDescriptionVersionId,
      applicationFormVersionId: editor.applicationFormVersionId || null, publicTitle: editor.publicTitle.trim(),
      opensAtUtc: editor.opensAtUtc || null, closesAtUtc: editor.closesAtUtc || null,
      maximumApplications: editor.maximumApplications || null, searchEngineVisible: editor.searchEngineVisible,
    })
    setSaving(false)
    if (!response.ok || !response.data) {
      setActionFeedback({ type: 'error', message: 'Unable to save posting', description: response.error || 'The server did not return the saved posting.' })
      return
    }
    setActionFeedback({ type: 'success', message: 'Job-posting draft saved.' })
    await loadClient(clientId, response.data.id)
  }

  async function assignPipeline() {
    if (!editor?.positionId || !pipelineVersionId) { report('warning', 'Pipeline not assigned', 'Select a position and published pipeline.'); return }
    setSaving(true)
    const response = await assignRecruitmentPipeline({ positionId: editor.positionId, jobPostingId: null, pipelineVersionId })
    setSaving(false)
    if (response.ok && response.data) {
      setPipelineAssignment(response.data)
      setActionFeedback({ type: 'success', message: 'Published hiring pipeline assigned.' })
    } else {
      setActionFeedback({ type: 'error', message: 'Unable to assign pipeline', description: response.error || 'The server rejected the pipeline assignment.' })
    }
  }

  function publish() {
    if (!editor) return
    if (publishingIssues.length) {
      report('warning', 'Posting is not ready to publish', publishingIssues.join(' '))
      return
    }
    setConfirmationError('')
    setConfirmationAction('publish')
  }

  function closePosting() {
    if (!editor?.id) { report('warning', 'Posting cannot be closed', 'Select a saved posting first.'); return }
    setConfirmationError('')
    setConfirmationAction('close')
  }

  async function confirmPublish() {
    if (!editor?.id) return
    setActionBusy(true)
    setConfirmationError('')
    setActionFeedback({ type: 'info', message: 'Publishing job posting...', description: 'Waiting for the server to activate the public careers page.' })
    const response = await publishRecruitmentJobPosting(editor.id)
    if (!response.ok || !response.data) {
      const error = response.error || 'The server did not return the published posting.'
      setActionBusy(false)
      setConfirmationError(error)
      setActionFeedback({ type: 'error', message: 'Publishing failed', description: error })
      notify(error, 'error')
      return
    }

    const publishedUrl = normalizePublicCareerUrl(response.data.publicUrl, response.data.publicSlug)
    const publicJob = publishedUrl ? await getPublicCareerJob(response.data.publicSlug) : null
    setConfirmationAction(null)
    setActionBusy(false)
    if (!publishedUrl) {
      const error = 'The posting was published, but the server did not return a valid public careers URL. Check the configured candidate-portal base URL.'
      setActionFeedback({ type: 'error', message: 'Published link unavailable', description: error })
      notify(error, 'error')
    } else if (!publicJob) {
      const error = 'The posting is published, but the anonymous public-job API could not load it. Check the candidate-portal deployment route and API configuration before sharing this URL.'
      setActionFeedback({ type: 'warning', message: 'Published link needs attention', description: `${error} ${publishedUrl}` })
      notify(error, 'warning', { actions: [{ label: 'Check public URL', href: publishedUrl }] })
    } else {
      const scheduled = publicJob.availabilityStatus === 'Scheduled'
      const message = scheduled ? 'Job is published and scheduled.' : 'Job is live on the public careers page.'
      const description = scheduled && publicJob.opensAtUtc
        ? `${publishedUrl} Applications open ${dayjs(publicJob.opensAtUtc).format('DD MMM YYYY, hh:mm A')}.`
        : publishedUrl
      setActionFeedback({ type: 'success', message, description })
      notify(message, 'success', { actions: [{ label: 'Open public job', href: publishedUrl }] })
    }
    onPublished?.(response.data)
    await loadClient(clientId, response.data.id)
  }

  async function confirmClose() {
    if (!editor?.id) return
    const postingId = editor.id
    setActionBusy(true)
    setConfirmationError('')
    setActionFeedback({ type: 'info', message: 'Closing job posting...', description: 'Waiting for the server to stop new public applications.' })
    const response = await closeRecruitmentJobPosting(postingId)
    if (!response.ok) {
      const error = response.error || 'The server could not close this posting.'
      setActionBusy(false)
      setConfirmationError(error)
      setActionFeedback({ type: 'error', message: 'Closing failed', description: error })
      notify(error, 'error')
      return
    }
    setConfirmationAction(null)
    setActionBusy(false)
    setActionFeedback({ type: 'success', message: 'Job posting closed.', description: 'New public applications are no longer accepted.' })
    notify('Job posting closed.', 'success')
    await loadClient(clientId, postingId)
  }

  async function copyLink() {
    if (!publicUrl) { report('warning', 'Public link unavailable', 'Configure and enable the candidate portal, then save or publish this posting.'); return }
    try {
      await copyText(publicUrl)
      setActionFeedback({ type: 'success', message: 'Public careers link copied.', description: publicUrl })
      notify('Public careers link copied.', 'success')
    } catch {
      setActionFeedback({ type: 'warning', message: 'Browser copy was blocked', description: 'Select and copy the public URL shown on this page.' })
      notify('Browser copy was blocked. Select and copy the public URL shown on this page.', 'warning')
    }
  }

  return <section className="orchestration-shell posting-manager">
    <div className="orchestration-toolbar">
      <div>
        <span className="orchestration-kicker">Approved JD to external careers page</span>
        <h2 className="orchestration-title">Job Posting Manager</h2>
        <p className="orchestration-subtitle">Bind an approved JD, published application form and published pipeline before going live.</p>
      </div>
      <Space wrap>
        {!clientScopeManaged && <Select value={clientId} placeholder="Select client" showSearch optionFilterProp="label" style={{ minWidth: 230 }}
          options={[...(canViewAllClients ? [{ value: 0, label: 'All clients' }] : []), ...clients.map(row => ({ value: row.id, label: row.name }))]}
          onChange={value => { setClientId(value); setEditor(null); setActionFeedback(null) }} />}
        <Button type="primary" icon={<PlusOutlined />} disabled={clientId <= 0} title={clientId <= 0 ? 'Select a client before creating a posting.' : undefined} onClick={() => void startNew()}>New posting</Button>
      </Space>
    </div>

    <Spin spinning={loading}>
      <div className="posting-workspace-layout">
        <Card size="small" className="posting-list" title={`Postings (${visiblePostings.length})`}>
          <Space direction="vertical" size={10} style={{ width: '100%' }}>
            <Input.Search allowClear value={search} onChange={event => setSearch(event.target.value)} placeholder="Search title or position" />
            <Segmented block size="small" value={listStatus} onChange={value => setListStatus(String(value))} options={['All', 'Draft', 'Published', 'Closed']} />
            <List dataSource={visiblePostings} locale={{ emptyText: 'No matching job postings.' }} renderItem={row => <List.Item className={editor?.id === row.id ? 'active' : ''} onClick={() => void choosePosting(row)}>
              {clientId === 0 && <Tag color={row.clientName ? 'blue' : 'orange'}>{row.clientName || `Deleted client #${row.clientId}`}</Tag>}
              <List.Item.Meta title={<Space><Typography.Text strong ellipsis>{row.publicTitle}</Typography.Text><PostingStatus status={row.status} /></Space>} description={<Space direction="vertical" size={2}><span>{row.positionCode} · {row.positionTitle}</span><span>{row.applicationCount} application{row.applicationCount === 1 ? '' : 's'}</span></Space>} />
            </List.Item>} />
          </Space>
        </Card>

        {!editor ? <Card><Empty description="Select a posting or create a new one." /></Card> : <div className="posting-editor">
          <Card size="small">
            <div className="orchestration-toolbar">
              <Space wrap><PostingStatus status={editor.status} />{editor.applicationCount > 0 && <Badge count={editor.applicationCount} overflowCount={99999} showZero color="#6b4eff" />}</Space>
              <Space wrap>
                {publicUrl && <Button icon={<CopyOutlined />} disabled={actionBusy} onClick={() => void copyLink()}>Copy public link</Button>}
                {editor.status === 'Published' && publicUrl && <Button icon={<GlobalOutlined />} href={publicUrl} target="_blank" rel="noreferrer">Open public page</Button>}
                {editor.status === 'Published' && <Button danger icon={<CloseCircleOutlined />} disabled={actionBusy} onClick={closePosting}>Close</Button>}
                {(editor.status === 'Draft' || editor.status === 'Closed') && <Button data-testid="job-posting-publish-button" type="primary" icon={<RocketOutlined />} loading={actionBusy && confirmationAction === 'publish'} disabled={actionBusy} onClick={publish}>Publish</Button>}
                <Button icon={<SaveOutlined />} loading={saving} disabled={readOnly || actionBusy} onClick={() => void save()}>Save draft</Button>
                {canDelete && editor.id > 0 && <Popconfirm title="Delete this job posting?" description="Delete linked applications first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteRecruitmentJobPosting(editor.id); if (response.ok) { setEditor(null); await loadClient(clientId) } }}><Button danger icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
              </Space>
            </div>
            {actionFeedback && <Alert data-testid="job-posting-action-feedback" style={{ marginBottom: 12 }} closable showIcon type={actionFeedback.type} message={actionFeedback.message} description={actionFeedback.description} onClose={() => setActionFeedback(null)} />}
            {readOnly && <Alert className="jd-readonly-alert" type="info" showIcon message="Published details are locked" description="Close this posting and create a new posting if the approved JD, form or schedule must change." />}
            {(editor.status === 'Draft' || editor.status === 'Closed') && <Alert
              data-testid="job-posting-publish-readiness"
              style={{ marginBottom: 12 }}
              showIcon
              type={publishingIssues.length ? 'warning' : 'success'}
              message={publishingIssues.length ? 'Not ready to publish' : 'Ready to publish'}
              description={publishingIssues.length
                ? <ul style={{ margin: 0, paddingInlineStart: 20 }}>{publishingIssues.map(issue => <li key={issue}>{issue}</li>)}</ul>
                : 'The approved JD, published form, hiring pipeline and public candidate portal are ready.'}
            />}
            {publicUrl && <div className="public-link-banner"><GlobalOutlined /><div><Typography.Text type="secondary">{editor.status === 'Published' ? 'Live public candidate URL' : 'Public candidate URL preview'}</Typography.Text><Typography.Link href={publicUrl} target="_blank" rel="noreferrer">{publicUrl} <LinkOutlined /></Typography.Link>{editor.status !== 'Published' && <Typography.Text type="secondary">This URL starts accepting applications only after the posting is published and open.</Typography.Text>}</div></div>}
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
              <Col xs={24} lg={6}><Button block type="primary" ghost icon={<SettingOutlined />} loading={saving} disabled={readOnly || actionBusy || !pipelineVersionId || !editor.positionId} onClick={() => void assignPipeline()}>Assign pipeline</Button></Col>
            </Row>
            {selectedPipeline && <Typography.Text type="secondary">Selected: {selectedPipeline.label}</Typography.Text>}
          </Card>
        </div>}
      </div>
    </Spin>
    <Modal
      open={confirmationAction !== null}
      title={confirmationAction === 'publish' ? 'Publish this job?' : 'Close this public job?'}
      okText={confirmationAction === 'publish' ? 'Publish now' : 'Close posting'}
      okButtonProps={{ danger: confirmationAction === 'close' }}
      confirmLoading={actionBusy}
      cancelButtonProps={{ disabled: actionBusy }}
      closable={!actionBusy}
      maskClosable={!actionBusy}
      onCancel={() => { if (!actionBusy) { setConfirmationAction(null); setConfirmationError('') } }}
      onOk={() => void (confirmationAction === 'publish' ? confirmPublish() : confirmClose())}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Typography.Text>{confirmationAction === 'publish'
          ? 'The configured public careers link will become available immediately, subject to the opening date. The assigned form and pipeline versions remain stable for applicants.'
          : 'New public applications will stop. Existing candidate and pipeline records remain available.'}</Typography.Text>
        {actionBusy && <Alert showIcon type="info" message={confirmationAction === 'publish' ? 'Publishing job posting...' : 'Closing job posting...'} description="Please wait for the server response." />}
        {confirmationError && <Alert data-testid="job-posting-confirmation-error" showIcon type="error" message="The action could not be completed" description={confirmationError} />}
      </Space>
    </Modal>
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
    positionCode: '', positionTitle, clientName: '', candidatePortalReady: false, publicUrl: '',
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

function validatePublishing(
  row: RecruitmentJobPosting,
  assignment: RecruitmentPositionPipelineAssignment | null,
  selectedPipelineVersionId: number | undefined,
  publicUrl: string,
) {
  const issues: string[] = []
  if (!row.id) issues.push('Save the posting draft before publishing.')
  if (!['Draft', 'Closed'].includes(row.status)) issues.push('Only a draft or closed posting can be published.')
  if (!row.positionId) issues.push('Select an open position.')
  if (!row.jobDescriptionVersionId) issues.push('Select an approved job-description version.')
  if (!row.publicTitle.trim()) issues.push('Enter the public job title.')
  if (!row.applicationFormVersionId) issues.push('Select a published candidate application form.')
  if (row.opensAtUtc && row.closesAtUtc && !dayjs(row.closesAtUtc).isAfter(dayjs(row.opensAtUtc))) issues.push('Closing date must be after the opening date.')
  if (row.closesAtUtc && !dayjs(row.closesAtUtc).isAfter(dayjs())) issues.push('Closing date must be in the future.')
  if (!assignment?.isActive || !selectedPipelineVersionId || assignment.pipelineVersionId !== selectedPipelineVersionId) issues.push('Assign the selected published hiring pipeline.')
  if (!row.candidatePortalReady || !publicUrl) issues.push('Enable the candidate portal and configure a valid public HTTP or HTTPS base URL.')
  return issues
}

async function copyText(value: string) {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(value)
    return
  }
  const input = document.createElement('textarea')
  input.value = value
  input.setAttribute('readonly', '')
  input.style.position = 'fixed'
  input.style.opacity = '0'
  document.body.appendChild(input)
  input.select()
  const copied = document.execCommand('copy')
  input.remove()
  if (!copied) throw new Error('Copy command was rejected.')
}
