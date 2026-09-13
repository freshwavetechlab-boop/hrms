import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import dayjs from 'dayjs'
import {
  CloseCircleOutlined, CopyOutlined, DeleteOutlined, DollarOutlined, EditOutlined, EnvironmentOutlined, FieldTimeOutlined, FormOutlined, GlobalOutlined,
  LaptopOutlined, LinkOutlined, RocketOutlined, TeamOutlined, UserAddOutlined,
} from '@ant-design/icons'
import {
  Alert, Badge, Button, Card, Col, DatePicker, Descriptions, Empty, Form, Input,
  List, Modal, Popconfirm, Row, Segmented, Select, Space, Spin, Tag, Tooltip, Typography,
} from 'antd'
import { useAuthSession } from './AuthGate'
import { useToast, type ToastType } from './ToastProvider'
import { getClients } from '../services/payrollService'
import {
  closeRecruitmentJobPosting, deleteRecruitmentJobPosting, getRecruitmentJobDescriptions,
  getRecruitmentJobPosting, getRecruitmentJobPostings, getRecruitmentOrchestrationLookups, getPublicCareerJob,
  getRecruitmentPositionPipelineAssignment, normalizePublicCareerUrl, publishRecruitmentJobPosting, saveRecruitmentJobPosting,
} from '../services/recruitmentOrchestrationService'
import type { Client } from '../types/payroll'
import type {
  DynamicFormDefinition, RecruitmentJobDescriptionVersion, RecruitmentJobPosting, RecruitmentOrchestrationLookups,
  RecruitmentPositionOption, RecruitmentPositionPipelineAssignment,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  initialPositionId?: number
  onPublished?: (posting: RecruitmentJobPosting) => void
  onManageCandidateForms?: () => void
}

const emptyLookups: RecruitmentOrchestrationLookups = {
  lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [],
}
const editableStatuses = new Set(['Draft'])
type ActionFeedback = { type: ToastType; message: string; description?: string }
type ConfirmationAction = 'publish' | 'close'

export default function RecruitmentJobPostingManager({ initialClientId = 0, clientScopeManaged = false, initialPositionId = 0, onPublished, onManageCandidateForms }: Props) {
  const navigate = useNavigate()
  const session = useAuthSession()
  const notify = useToast()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const canViewAllClients = session?.user.clientId == null
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId || session?.user.clientId || 0)
  const [lookups, setLookups] = useState(emptyLookups)
  const [postings, setPostings] = useState<RecruitmentJobPosting[]>([])
  const [editor, setEditor] = useState<RecruitmentJobPosting | null>(null)
  const [jobDescriptions, setJobDescriptions] = useState<RecruitmentJobDescriptionVersion[]>([])
  const [pipelineVersionId, setPipelineVersionId] = useState<number>()
  const [pipelineAssignment, setPipelineAssignment] = useState<RecruitmentPositionPipelineAssignment | null>(null)
  const [listStatus, setListStatus] = useState('All')
  const [search, setSearch] = useState('')
  const [sortOrder, setSortOrder] = useState<'recent' | 'oldest'>('recent')
  const [loading, setLoading] = useState(false)
  const [actionBusy, setActionBusy] = useState(false)
  const [confirmationAction, setConfirmationAction] = useState<ConfirmationAction | null>(null)
  const [confirmationError, setConfirmationError] = useState('')
  const [actionFeedback, setActionFeedback] = useState<ActionFeedback | null>(null)
  const [quickPublishTarget, setQuickPublishTarget] = useState<RecruitmentJobPosting | null>(null)
  const [quickPublishingId, setQuickPublishingId] = useState(0)

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
  useEffect(() => {
    if (!clientScopeManaged) return
    const nextClientId = initialClientId || session?.user.clientId || 0
    if (nextClientId === clientId) return
    setClientId(nextClientId)
    setEditor(null)
    setActionFeedback(null)
  }, [clientScopeManaged, initialClientId, session?.user.clientId, clientId])

  const editorClientId = editor?.id ? editor.clientId : clientId
  const positions = useMemo(() => editorClientId > 0
    ? lookups.positions.filter(row => row.clientId === editorClientId)
    : lookups.positions, [lookups.positions, editorClientId])
  const visiblePostings = useMemo(() => postings.filter(row => {
    const statusMatch = listStatus === 'All' || row.status === listStatus
    const needle = search.trim().toLowerCase()
    return statusMatch && (!needle || `${row.publicTitle} ${row.positionCode} ${row.positionTitle}`.toLowerCase().includes(needle))
  }).sort((left, right) => {
    const a = dayjs(left.updatedAtUtc || left.createdAtUtc || 0).valueOf()
    const b = dayjs(right.updatedAtUtc || right.createdAtUtc || 0).valueOf()
    return sortOrder === 'recent' ? b - a : a - b
  }), [postings, listStatus, search, sortOrder])
  const statusCounts = useMemo(() => ({
    All: postings.length,
    Draft: postings.filter(row => row.status === 'Draft').length,
    Published: postings.filter(row => row.status === 'Published').length,
    Closed: postings.filter(row => row.status === 'Closed').length,
  }), [postings])
  const selectedPosition = positions.find(row => row.id === editor?.positionId)
  const readOnly = !!editor?.id && !editableStatuses.has(editor.status)
  const publicUrl = normalizePublicCareerUrl(editor?.publicUrl, editor?.publicSlug)
  const applicationFormOptions = useMemo(() => {
    const rows = lookups.forms.filter(row => row.status?.toLowerCase() === 'active' && formDefinitionBelongsToClient(row, editorClientId))
      .flatMap(row => {
        const versions = (row.versions || []).filter(version => ['published', 'retired'].includes(version.status?.toLowerCase()))
        if (versions.length) return versions.map(version => ({ value: version.id, label: `${row.formName} · v${version.versionNumber}${version.status === 'Retired' ? ' (retired)' : ''}` }))
        return row.currentPublishedVersionId ? [{ value: Number(row.currentPublishedVersionId), label: `${row.formName} · current published` }] : []
      })
    if (editor?.applicationFormVersionId && !rows.some(row => row.value === editor.applicationFormVersionId)) rows.push({ value: editor.applicationFormVersionId, label: `Assigned form version #${editor.applicationFormVersionId}` })
    return rows
  }, [lookups.forms, editorClientId, editor?.applicationFormVersionId])
  const automaticApplicationFormVersionId = uniqueCurrentPublishedApplicationFormVersionId(lookups.forms, editorClientId)
  const effectiveApplicationFormVersionId = editor?.applicationFormVersionId || automaticApplicationFormVersionId
  useEffect(() => {
    if (!editor || readOnly || editor.applicationFormVersionId || !automaticApplicationFormVersionId) return
    setEditor(current => current && !current.applicationFormVersionId ? { ...current, applicationFormVersionId: automaticApplicationFormVersionId } : current)
  }, [editor?.id, editor?.applicationFormVersionId, automaticApplicationFormVersionId, readOnly])

  async function loadClient(scope: number, preferredPostingId = 0) {
    setLoading(true)
    const [lookupRows, postingRows] = await Promise.all([
      getRecruitmentOrchestrationLookups(scope), getRecruitmentJobPostings(scope),
    ])
    setLookups(lookupRows); setPostings(postingRows)
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
    const [detailed] = await Promise.all([
      getRecruitmentJobPosting(row.id),
      loadPositionContext(row.positionId, allPositions),
    ])
    setEditor(detailed ? { ...detailed } : {
      ...row,
      candidateProofReady: row.candidateProofReady ?? true,
      candidateProofValidationMessage: row.candidateProofValidationMessage ?? '',
    })
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

  function report(type: ToastType, message: string, description = '') {
    setActionFeedback({ type, message, description })
    notify(description ? `${message} ${description}` : message, type)
  }

  function publish() {
    if (!editor) return
    const error = validatePosting(editor, false)
    if (error) { report('warning', 'Select an approved role', error); return }
    setQuickPublishTarget({ ...editor, applicationFormVersionId: effectiveApplicationFormVersionId })
    setConfirmationError('')
    setConfirmationAction('publish')
  }

  async function publishFromCard(row: RecruitmentJobPosting) {
    setQuickPublishingId(row.id)
    const [detailed, assignment] = await Promise.all([
      getRecruitmentJobPosting(row.id),
      getRecruitmentPositionPipelineAssignment(row.positionId),
    ])
    setQuickPublishingId(0)
    if (!detailed) return void report('error', 'Job could not be loaded', 'Refresh the Jobs page and try again.')
    const scopedForms = lookups.forms.some(row => formDefinitionBelongsToClient(row, detailed.clientId))
      ? lookups.forms
      : (await getRecruitmentOrchestrationLookups(detailed.clientId)).forms
    const defaultFormVersionId = uniqueCurrentPublishedApplicationFormVersionId(scopedForms, detailed.clientId)
    const publishable = detailed.applicationFormVersionId || !defaultFormVersionId
      ? detailed
      : { ...detailed, applicationFormVersionId: defaultFormVersionId }
    setPipelineAssignment(assignment)
    setPipelineVersionId(assignment?.pipelineVersionId || undefined)
    setQuickPublishTarget(publishable)
    setConfirmationError('')
    setConfirmationAction('publish')
  }

  async function copyPostingLink(row: RecruitmentJobPosting) {
    const link = normalizePublicCareerUrl(row.publicUrl, row.publicSlug)
    if (!link) return void report('warning', 'Public link unavailable', 'Open the job and check the candidate portal configuration.')
    try {
      await copyText(link)
      notify('Public careers link copied.', 'success')
    } catch {
      notify('Browser copy was blocked. Open the job to copy its public URL.', 'warning')
    }
  }

  function unpublishPosting() {
    if (!editor?.id) { report('warning', 'Posting cannot be unpublished', 'Select a saved posting first.'); return }
    setConfirmationError('')
    setConfirmationAction('close')
  }

  async function confirmPublish() {
    const target = quickPublishTarget ?? editor
    if (!target) return
    const inputError = validatePosting(target, true)
    if (inputError) { setConfirmationError(inputError); return }
    setActionBusy(true)
    setConfirmationError('')
    setActionFeedback({ type: 'info', message: 'Preparing job posting...', description: 'Saving the approved role and publishing choices.' })
    const saved = await saveRecruitmentJobPosting({
      id: target.id, positionId: target.positionId, jobDescriptionVersionId: target.jobDescriptionVersionId,
      applicationFormVersionId: target.applicationFormVersionId || null, publicTitle: target.publicTitle.trim(),
      opensAtUtc: target.opensAtUtc || null, closesAtUtc: target.closesAtUtc || null,
      maximumApplications: null, searchEngineVisible: true,
    })
    if (!saved.ok || !saved.data) {
      const current = target.id ? await getRecruitmentJobPosting(target.id) : null
      if (current?.status === 'Published') {
        setConfirmationAction(null)
        setQuickPublishTarget(null)
        setActionBusy(false)
        setActionFeedback({ type: 'success', message: 'Job is already live.', description: normalizePublicCareerUrl(current.publicUrl, current.publicSlug) })
        notify('Job is already published.', 'success')
        await loadClient(clientId, editor ? current.id : 0)
        return
      }
      const error = saved.error || 'The server could not prepare this job posting.'
      setActionBusy(false)
      setConfirmationError(error)
      return
    }
    const targetUrl = normalizePublicCareerUrl(saved.data.publicUrl, saved.data.publicSlug)
    const issues = validatePublishing(saved.data, pipelineAssignment, pipelineVersionId, targetUrl)
    if (issues.length) {
      setActionBusy(false)
      setEditor(saved.data)
      setQuickPublishTarget(saved.data)
      setConfirmationError(issues.join(' '))
      return
    }
    setActionFeedback({ type: 'info', message: 'Publishing job posting...', description: 'Waiting for the server to activate the public careers page.' })
    const response = await publishRecruitmentJobPosting(saved.data.id)
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
    setQuickPublishTarget(null)
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
    await loadClient(clientId, editor ? response.data.id : 0)
  }

  async function confirmClose() {
    if (!editor?.id) return
    const postingId = editor.id
    setActionBusy(true)
    setConfirmationError('')
    setActionFeedback({ type: 'info', message: 'Unpublishing job posting...', description: 'Waiting for the server to stop new public applications.' })
    const response = await closeRecruitmentJobPosting(postingId)
    if (!response.ok) {
      const error = response.error || 'The server could not unpublish this posting.'
      setActionBusy(false)
      setConfirmationError(error)
      setActionFeedback({ type: 'error', message: 'Unpublish failed', description: error })
      notify(error, 'error')
      return
    }
    setConfirmationAction(null)
    setActionBusy(false)
    setActionFeedback({ type: 'success', message: 'Job posting unpublished.', description: 'New public applications are no longer accepted. You can publish it again from the archived tab.' })
    notify('Job posting unpublished.', 'success')
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
        <h2 className="orchestration-title">Jobs</h2>
        <p className="orchestration-subtitle">Publish approved roles and manage their public candidate intake.</p>
      </div>
      <Space wrap>
        {!clientScopeManaged && <Select value={clientId} placeholder="Select client" showSearch optionFilterProp="label" style={{ minWidth: 230 }}
          options={[...(canViewAllClients ? [{ value: 0, label: 'All clients' }] : []), ...clients.map(row => ({ value: row.id, label: row.name }))]}
          onChange={value => { setClientId(value); setEditor(null); setActionFeedback(null) }} />}
      </Space>
    </div>

    <Spin spinning={loading}>
      <div className={`posting-workspace-layout${editor ? ' has-editor' : ''}`}>
        {!editor && <Card size="small" className="posting-list" title={`Jobs (${visiblePostings.length})`}>
          <Space direction="vertical" size={10} style={{ width: '100%' }}>
            <Segmented className="job-status-strip" block value={listStatus} onChange={value => setListStatus(String(value))} options={[
              { value: 'All', label: <span><b>{statusCounts.All}</b>All</span> },
              { value: 'Draft', label: <span><b>{statusCounts.Draft}</b>Draft</span> },
              { value: 'Published', label: <span><b>{statusCounts.Published}</b>Active</span> },
              { value: 'Closed', label: <span><b>{statusCounts.Closed}</b>Archived</span> },
            ]} />
            <div className="job-filter-bar">
              <Input.Search allowClear value={search} onChange={event => setSearch(event.target.value)} placeholder="Search jobs by name or ID" />
              <Select value={sortOrder} onChange={setSortOrder} options={[{ value: 'recent', label: 'Updated: recent first' }, { value: 'oldest', label: 'Updated: oldest first' }]} />
            </div>
            <List className="job-card-grid" grid={{ gutter: 16, xs: 1, xl: 2 }} dataSource={visiblePostings} locale={{ emptyText: 'No matching jobs.' }} renderItem={row => {
              const position = positions.find(item => item.id === row.positionId)
              return <List.Item><Card hoverable className="job-summary-card" role="button" tabIndex={0} onClick={() => void choosePosting(row)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') void choosePosting(row) }}>
                <div className="job-card-heading"><div className="job-avatar">{(row.publicTitle || row.positionTitle || 'J').trim().charAt(0).toUpperCase()}</div><div><Typography.Title level={4} ellipsis={{ rows: 1 }}>{row.publicTitle || row.positionTitle}</Typography.Title><Tag color="blue">ID: {row.positionCode || row.id}</Tag></div><Space className="job-card-top-actions" size={6}>
                  {row.status === 'Published' && <Tooltip title="Copy public link"><Button aria-label="Copy public link" size="small" shape="circle" icon={<CopyOutlined />} onClick={event => { event.stopPropagation(); void copyPostingLink(row) }} /></Tooltip>}
                  <PostingStatus status={row.status} />
                </Space></div>
                <div className="job-card-facts"><Tag>{position?.employmentType || 'Employment type not set'}</Tag><Tag color="cyan"><UserAddOutlined /> {row.applicationCount} candidate{row.applicationCount === 1 ? '' : 's'}</Tag></div>
                <div className="job-card-metrics">
                  <JobFact icon={<FieldTimeOutlined />} label="Experience" value={position?.requisitionExperienceRange || position?.experienceRange || 'Not specified'} />
                  <JobFact icon={<DollarOutlined />} label="CTC" value={formatCtc(position)} />
                  <JobFact icon={<LaptopOutlined />} label="Work mode" value={formatWorkMode(position?.requisitionWorkMode || position?.workMode)} />
                  <JobFact icon={<EnvironmentOutlined />} label="Location" value={position?.requisitionJobLocation || position?.jobLocation || 'Not specified'} />
                  <JobFact icon={<TeamOutlined />} label="Vacancies" value={position ? `${position.remainingPositions ?? 0} open / ${position.numberOfPositions || position.requisitionNumberOfOpenings || 0} total` : 'Not specified'} />
                </div>
                {clientId === 0 && <Typography.Text type="secondary">{row.clientName || `Client #${row.clientId}`}</Typography.Text>}
                <div className="job-card-footer"><Typography.Text type="secondary">Updated {row.updatedAtUtc ? dayjs(row.updatedAtUtc).format('DD MMM YYYY') : '—'}</Typography.Text><Space wrap>
                  <Button icon={<EditOutlined />} onClick={event => { event.stopPropagation(); void choosePosting(row) }}>{row.status === 'Draft' ? 'Edit' : 'View'}</Button>
                  {row.status === 'Draft' && <Button type="primary" icon={<RocketOutlined />} loading={quickPublishingId === row.id} onClick={event => { event.stopPropagation(); void publishFromCard(row) }}>Publish</Button>}
                  {row.status === 'Published' && <Button type="primary" icon={<UserAddOutlined />} onClick={event => { event.stopPropagation(); navigate(`/recruitment/applications?clientId=${row.clientId}&add=1&jobPostingId=${row.id}`) }}>Add candidate</Button>}
                </Space></div>
              </Card></List.Item>
            }} />
          </Space>
        </Card>}

        {editor && <div className="posting-editor">
          <Button className="job-back-button" onClick={() => { setEditor(null); setActionFeedback(null) }}>Back to jobs</Button>
          <Card size="small">
            <div className="orchestration-toolbar">
              <Space wrap><PostingStatus status={editor.status} />{editor.applicationCount > 0 && <Badge count={editor.applicationCount} overflowCount={99999} showZero color="#6b4eff" />}</Space>
              <Space wrap>
                {publicUrl && <Button icon={<CopyOutlined />} disabled={actionBusy} onClick={() => void copyLink()}>Copy public link</Button>}
                {editor.status === 'Published' && publicUrl && <Button icon={<GlobalOutlined />} href={publicUrl} target="_blank" rel="noreferrer">Open public page</Button>}
                {editor.status === 'Published' && <Button danger icon={<CloseCircleOutlined />} disabled={actionBusy} onClick={unpublishPosting}>Unpublish</Button>}
                {(editor.status === 'Draft' || editor.status === 'Closed') && <Button data-testid="job-posting-publish-button" type="primary" icon={<RocketOutlined />} loading={actionBusy && confirmationAction === 'publish'} disabled={actionBusy} onClick={publish}>Publish</Button>}
                {canDelete && editor.id > 0 && <Popconfirm title="Delete this job posting?" description="Delete linked applications first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteRecruitmentJobPosting(editor.id); if (response.ok) { setEditor(null); await loadClient(clientId) } }}><Button danger icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}
              </Space>
            </div>
            {actionFeedback && <Alert data-testid="job-posting-action-feedback" style={{ marginBottom: 12 }} closable showIcon type={actionFeedback.type} message={actionFeedback.message} description={actionFeedback.description} onClose={() => setActionFeedback(null)} />}
            {readOnly && <Alert className="jd-readonly-alert" type="info" showIcon message="Published details are locked" description="Close this posting and create a new posting if the approved JD, form or schedule must change." />}
            {publicUrl && <div className="public-link-banner"><GlobalOutlined /><div><Typography.Text type="secondary">{editor.status === 'Published' ? 'Live public candidate URL' : 'Public candidate URL preview'}</Typography.Text><Typography.Link href={publicUrl} target="_blank" rel="noreferrer">{publicUrl} <LinkOutlined /></Typography.Link>{editor.status !== 'Published' && <Typography.Text type="secondary">This URL starts accepting applications only after the posting is published and open.</Typography.Text>}</div></div>}
          </Card>

          <Card size="small" title={<Space><EditOutlined /> Approved role</Space>}>
            <Form layout="vertical" disabled={readOnly}>
              <Row gutter={14}>
                <Col span={24}><Form.Item label="Approved hiring request / position" required><Select value={editor.positionId || undefined} showSearch optionFilterProp="label" placeholder="Select an approved position" onChange={value => void changePosition(value)} options={positions.map(row => ({ value: row.id, label: `${row.positionCode} · ${row.positionTitle} · ${row.department || 'No department'}`, disabled: row.remainingPositions === 0 }))} /></Form.Item></Col>
              </Row>
              {selectedPosition && <Descriptions className="posting-position-summary" size="small" column={{ xs: 1, sm: 2, lg: 4 }}>
                <Descriptions.Item label="Role">{editor.publicTitle || selectedPosition.positionTitle}</Descriptions.Item><Descriptions.Item label="Approved JD">{jobDescriptions.find(row => row.id === editor.jobDescriptionVersionId)?.title || 'Auto-selected'}</Descriptions.Item><Descriptions.Item label="Location">{selectedPosition.jobLocation || '—'}</Descriptions.Item><Descriptions.Item label="Open seats">{selectedPosition.remainingPositions ?? '—'}</Descriptions.Item>
              </Descriptions>}
            </Form>
          </Card>

        </div>}
      </div>
    </Spin>
    <Modal
      width={720}
      open={confirmationAction !== null}
      title={confirmationAction === 'publish' ? 'Publish this job?' : 'Unpublish this job?'}
      okText={confirmationAction === 'publish' ? 'Publish now' : 'Unpublish'}
      okButtonProps={{ danger: confirmationAction === 'close' }}
      confirmLoading={actionBusy}
      cancelButtonProps={{ disabled: actionBusy }}
      closable={!actionBusy}
      maskClosable={!actionBusy}
      onCancel={() => { if (!actionBusy) { setConfirmationAction(null); setQuickPublishTarget(null); setConfirmationError('') } }}
      onOk={() => void (confirmationAction === 'publish' ? confirmPublish() : confirmClose())}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        {confirmationAction === 'publish' && quickPublishTarget ? <Form layout="vertical">
          <Row gutter={12}>
            <Col xs={24} md={12}><Form.Item label="Opens at"><DatePicker showTime style={{ width: '100%' }} value={quickPublishTarget.opensAtUtc ? dayjs(quickPublishTarget.opensAtUtc) : null} onChange={value => setQuickPublishTarget(current => current ? { ...current, opensAtUtc: value?.toISOString() ?? null } : current)} /></Form.Item></Col>
            <Col xs={24} md={12}><Form.Item label="Closes at"><DatePicker showTime style={{ width: '100%' }} value={quickPublishTarget.closesAtUtc ? dayjs(quickPublishTarget.closesAtUtc) : null} onChange={value => setQuickPublishTarget(current => current ? { ...current, closesAtUtc: value?.toISOString() ?? null } : current)} /></Form.Item></Col>
          </Row>
          <Form.Item label="Candidate application form" required extra="This published form version stays fixed for applicants of this job.">
            <div className="posting-form-picker">
              <Select value={quickPublishTarget.applicationFormVersionId || undefined} allowClear showSearch optionFilterProp="label" placeholder="Select published form" options={applicationFormOptions} onChange={applicationFormVersionId => setQuickPublishTarget(current => current ? { ...current, applicationFormVersionId: applicationFormVersionId ?? null } : current)} />
              {onManageCandidateForms && <Button icon={<FormOutlined />} onClick={onManageCandidateForms}>Create / manage</Button>}
            </div>
          </Form.Item>
        </Form> : <Typography.Text>The public careers link will stop accepting new applications. Existing candidate and pipeline records remain available.</Typography.Text>}
        {actionBusy && <Alert showIcon type="info" message={confirmationAction === 'publish' ? 'Publishing job posting...' : 'Unpublishing job posting...'} description="Please wait for the server response." />}
        {confirmationError && <Alert data-testid="job-posting-confirmation-error" showIcon type="error" message="The action could not be completed" description={confirmationError} />}
      </Space>
    </Modal>
  </section>
}

function PostingStatus({ status }: { status: string }) {
  const color = status === 'Published' ? 'green' : status === 'Closed' ? 'default' : 'gold'
  const label = status === 'Published' ? 'Active' : status === 'Closed' ? 'Archived' : status || 'Draft'
  return <Tag color={color}>{label}</Tag>
}

function JobFact({ icon, label, value }: { icon: ReactNode; label: string; value: string }) {
  return <span className="job-card-metric">{icon}<small>{label}</small><strong title={value}>{value}</strong></span>
}

function formatCtc(position?: RecruitmentPositionOption) {
  if (!position) return 'Not specified'
  const minimum = Number(position.requisitionSalaryMin || position.salaryMin || 0)
  const maximum = Number(position.requisitionSalaryMax || position.salaryMax || 0)
  const currency = position.requisitionCurrency || position.currency || 'INR'
  const amount = (value: number) => new Intl.NumberFormat('en-IN', { maximumFractionDigits: 0 }).format(value)
  if (minimum > 0 && maximum > 0) return minimum === maximum ? `${currency} ${amount(minimum)}` : `${currency} ${amount(minimum)} – ${amount(maximum)}`
  if (minimum > 0) return `From ${currency} ${amount(minimum)}`
  if (maximum > 0) return `Up to ${currency} ${amount(maximum)}`
  return 'Not specified'
}

function formatWorkMode(value?: string) {
  const normalized = (value || '').trim().toLowerCase().replace(/[\s_-]+/g, '')
  if (!normalized) return 'Not specified'
  if (normalized === 'office' || normalized === 'onsite' || normalized === 'workfromoffice') return 'On-site'
  if (normalized === 'remote' || normalized === 'workfromhome') return 'Remote'
  if (normalized === 'hybrid') return 'Hybrid'
  return value!.trim()
}

function blankPosting(clientId: number, positionId = 0, positionTitle = ''): RecruitmentJobPosting {
  return {
    id: 0, clientId, positionId, jobDescriptionVersionId: 0, applicationFormVersionId: null,
    publicSlug: '', publicTitle: positionTitle, status: 'Draft', opensAtUtc: null, closesAtUtc: null,
    maximumApplications: null, applicationCount: 0, searchEngineVisible: true, publishedAtUtc: null,
    positionCode: '', positionTitle, clientName: '', candidatePortalReady: false, candidateProofReady: true, candidateProofValidationMessage: '', publicUrl: '',
  }
}

function formDefinitionBelongsToClient(row: DynamicFormDefinition, clientId: number) {
  const rowClientId = Number(row.clientId || 0)
  return clientId <= 0 || rowClientId === 0 || rowClientId === clientId
}

function uniqueCurrentPublishedApplicationFormVersionId(forms: DynamicFormDefinition[], clientId: number) {
  const ids = [...new Set(forms
    .filter(row => row.status?.toLowerCase() === 'active' && formDefinitionBelongsToClient(row, clientId) && row.currentPublishedVersionId)
    .map(row => Number(row.currentPublishedVersionId))
    .filter(id => Number.isFinite(id) && id > 0))]
  return ids.length === 1 ? ids[0] : null
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
  if (row.candidateProofReady === false) issues.push(row.candidateProofValidationMessage || 'Add each configured certification proof upload to exactly one published candidate form.')
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
