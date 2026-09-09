import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import {
  ArrowDownOutlined, ArrowRightOutlined, ArrowUpOutlined, AuditOutlined, DeleteOutlined, FileAddOutlined,
  FileTextOutlined, GiftOutlined, GlobalOutlined, OrderedListOutlined, PlusOutlined, ReloadOutlined, RobotOutlined, SafetyCertificateOutlined, SaveOutlined, SendOutlined, UploadOutlined,
} from '@ant-design/icons'
import {
  Alert, Button, Card, Col, Collapse, Descriptions, Empty, Form, Input, InputNumber, List,
  Modal, Popconfirm, Radio, Row, Select, Space, Spin, Switch, Tabs, Tag, Tooltip, Typography, message,
} from 'antd'
import { useAuthSession } from './AuthGate'
import RecruitmentWorkspaceLayout from './RecruitmentWorkspaceLayout'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { useNavigate } from 'react-router-dom'
import { getClients } from '../services/payrollService'
import { getRecruitmentRequisitions, parseRecruitmentRequestDocument } from '../services/recruitmentService'
import { getEffectiveAttachmentConfigurations, uploadEntityAttachment } from '../services/attachmentService'
import { getMyWorkflowRequestsResult } from '../services/workflowService'
import {
  approveRecruitmentJobDescriptionDirectly, deleteRecruitmentJobDescription, getRecruitmentJobDescription, getRecruitmentJobDescriptions, getRecruitmentOrchestrationLookups,
  saveRecruitmentJobDescription, submitRecruitmentJobDescription,
} from '../services/recruitmentOrchestrationService'
import type { Client, RecruitmentRequisition, RecruitmentRequestDocumentParseResult, WorkflowRequestProgress } from '../types/payroll'
import type {
  RecruitmentJdBenefit, RecruitmentJdCertificationRequirement, RecruitmentJdLanguageRequirement,
  RecruitmentJdQualificationRequirement, RecruitmentJdResponsibility, RecruitmentJdSkillRequirement,
  RecruitmentJobDescriptionVersion, RecruitmentOrchestrationLookups,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'
import './RecruitmentJobDescriptionManager.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  initialRequisitionId?: number
  onSaved?: (description: RecruitmentJobDescriptionVersion) => void
  onNavigationStateChange?: (state: { dirty: boolean; busy: boolean }) => void
}

const emptyLookups: RecruitmentOrchestrationLookups = {
  lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [],
}
const localId = () => -Math.floor(Date.now() + Math.random() * 100000)
const editableStatuses = new Set(['Draft', 'Sent Back'])
type EditorStep = 'role' | 'screening'
type EditorSection = 'role' | 'responsibilities' | 'skills' | 'qualifications' | 'additional'

export default function RecruitmentJobDescriptionManager({ initialClientId = 0, clientScopeManaged = false, initialRequisitionId = 0, onSaved, onNavigationStateChange }: Props) {
  const session = useAuthSession()
  const navigate = useNavigate()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const canDirectApprove = Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [requisitions, setRequisitions] = useState<RecruitmentRequisition[]>([])
  const [requisitionId, setRequisitionId] = useState(initialRequisitionId)
  const [versions, setVersions] = useState<RecruitmentJobDescriptionVersion[]>([])
  const [draft, setDraft] = useState<RecruitmentJobDescriptionVersion | null>(null)
  const [lookups, setLookups] = useState(emptyLookups)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [approvalOpen, setApprovalOpen] = useState(false)
  const [approvalMode, setApprovalMode] = useState<'workflow' | 'direct'>('workflow')
  const [workflowId, setWorkflowId] = useState<number>()
  const [approvalProgress, setApprovalProgress] = useState<WorkflowRequestProgress | null>(null)
  const [approvalProgressLoading, setApprovalProgressLoading] = useState(false)
  const [approvalProgressError, setApprovalProgressError] = useState('')
  const [approvalProgressRefreshKey, setApprovalProgressRefreshKey] = useState(0)
  const [editorStep, setEditorStep] = useState<EditorStep>('role')
  const [editorSection, setEditorSection] = useState<EditorSection>('role')
  const [historyOpen, setHistoryOpen] = useState(false)
  const [sourceFile, setSourceFile] = useState<File | null>(null)
  const [sourceParsing, setSourceParsing] = useState(false)
  const [sourceReview, setSourceReview] = useState<{ fields: string[]; warnings: string[] } | null>(null)
  const [sourceUploadError, setSourceUploadError] = useState('')
  const [sourceUploadProgress, setSourceUploadProgress] = useState(0)
  const [sourceDocumentsOpen, setSourceDocumentsOpen] = useState(false)
  const [attachmentRefresh, setAttachmentRefresh] = useState(0)
  const draftBaseline = useRef('')
  const requisitionsRef = useRef<RecruitmentRequisition[]>([])
  const scopeLoad = useRef(0)
  const versionLoad = useRef(0)
  const sourceLoad = useRef(0)

  useEffect(() => {
    if (clientScopeManaged) return
    let active = true
    void getClients().then(rows => {
      if (!active) return
      setClients(rows)
      if (!clientId && rows.length === 1) setClientId(rows[0].id)
    })
    return () => { active = false }
  }, [clientScopeManaged, initialClientId])

  useEffect(() => {
    const sequence = ++scopeLoad.current
    ++versionLoad.current
    if (!clientId) return
    setLoading(true)
    Promise.all([
      getRecruitmentRequisitions({ clientId }),
      getRecruitmentOrchestrationLookups(clientId),
    ]).then(async ([requestRows, lookupRows]) => {
      if (sequence !== scopeLoad.current) return
      const eligibleRequests = requestRows.filter(row => ['Draft', 'Sent Back', 'Approved'].includes(row.status))
      setRequisitions(eligibleRequests)
      requisitionsRef.current = eligibleRequests
      setLookups(lookupRows)
      const requestedId = requisitionId || initialRequisitionId
      const nextId = eligibleRequests.some(row => row.id === requestedId) ? requestedId : eligibleRequests.length === 1 ? eligibleRequests[0].id : 0
      setRequisitionId(nextId)
      if (!nextId) { setVersions([]); setDraftSnapshot(null) }
      else await loadVersions(nextId, 0, eligibleRequests)
    }).catch(() => { if (sequence === scopeLoad.current) message.error('Unable to load this client’s hiring requests.') })
      .finally(() => { if (sequence === scopeLoad.current) setLoading(false) })
    return () => { ++scopeLoad.current; ++versionLoad.current }
  }, [clientId])

  useEffect(() => {
    return () => { ++sourceLoad.current }
  }, [])

  useEffect(() => {
    let cancelled = false
    setApprovalProgress(null)
    setApprovalProgressError('')
    const instanceId = draft?.workflowInstanceId
    if (!instanceId || draft?.status !== 'Pending Approval') { setApprovalProgressLoading(false); return }
    setApprovalProgressLoading(true)
    void getMyWorkflowRequestsResult(instanceId).then(async result => {
      if (cancelled) return
      if (!result.ok) {
        setApprovalProgressError(result.error || 'Unable to load approval progress.')
        return
      }
      const progress = result.data.find(row => row.instanceId === instanceId)
      if (!progress) {
        setApprovalProgressError('Approval progress is not available for this request.')
        return
      }
      setApprovalProgress(progress)
      if (progress.status !== 'Pending' && draft?.id && draft.requisitionId) await loadVersions(draft.requisitionId, draft.id)
    }).finally(() => {
      if (!cancelled) setApprovalProgressLoading(false)
    })
    return () => { cancelled = true }
  }, [draft?.workflowInstanceId, draft?.status, approvalProgressRefreshKey])

  const selectedRequisition = requisitions.find(row => row.id === requisitionId)
  const readOnly = !!draft?.id && !editableStatuses.has(draft.status)
  const editingDisabled = readOnly || saving || sourceParsing
  const hasUnsavedChanges = Boolean(sourceFile || (draft && !readOnly && descriptionSnapshot(draft) !== draftBaseline.current))
  useEffect(() => {
    onNavigationStateChange?.({ dirty: hasUnsavedChanges, busy: saving || sourceParsing })
  }, [hasUnsavedChanges, saving, sourceParsing, onNavigationStateChange])
  useEffect(() => () => onNavigationStateChange?.({ dirty: false, busy: false }), [onNavigationStateChange])
  useEffect(() => {
    if (!hasUnsavedChanges && !saving && !sourceParsing) return
    const confirmLeave = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', confirmLeave)
    return () => window.removeEventListener('beforeunload', confirmLeave)
  }, [hasUnsavedChanges, saving, sourceParsing])
  const approvalWorkflows = useMemo(() => {
    const active = lookups.workflows.filter(row => row.isActive && (!row.clientId || row.clientId === clientId))
    return active.filter(row => row.resourceType === 'RecruitmentJobDescription')
  }, [lookups.workflows, clientId])
  const candidateProofOptions = useMemo(() => {
    const scope = draft?.clientId || clientId
    const now = Date.now()
    const effective = lookups.attachmentConfigurations
      .filter(row => row.isActive && (row.clientId === 0 || row.clientId === scope)
        && row.moduleCode?.toUpperCase() === 'RECRUITMENT'
        && ['CANDIDATE_APPLICATION', 'PUBLIC_CANDIDATE_APPLICATION', 'PRE_ONBOARDING'].includes(row.formCode?.toUpperCase())
        && (!row.effectiveFromUtc || new Date(row.effectiveFromUtc).getTime() <= now)
        && (!row.effectiveUntilUtc || new Date(row.effectiveUntilUtc).getTime() >= now))
      .sort((left, right) => Number(right.clientId === scope) - Number(left.clientId === scope) || left.fieldLabel.localeCompare(right.fieldLabel))
    const resolved = new Map<string, typeof effective[number]>()
    for (const row of effective) {
      const key = `${row.moduleCode}|${row.formCode}|${row.fieldKey}`.toUpperCase()
      if (!resolved.has(key)) resolved.set(key, row)
    }
    return [...resolved.values()].map(row => ({
      value: row.id,
      label: `${row.fieldLabel || row.attributeName} (${row.attributeCode})${row.requiresVerification ? ' - HR verification' : ''}`,
      metadata: [row.requiresDocumentNumber && 'number', row.requiresIssueDate && 'issue date', row.requiresExpiryDate && 'expiry date'].filter(Boolean).join(', '),
    }))
  }, [lookups.attachmentConfigurations, draft?.clientId, clientId])

  function setDraftSnapshot(value: RecruitmentJobDescriptionVersion | null) {
    draftBaseline.current = value ? descriptionSnapshot(value) : ''
    setDraft(value)
  }

  function clearSourceDraft() {
    ++sourceLoad.current
    setSourceFile(null)
    setSourceReview(null)
    setSourceUploadError('')
    setSourceUploadProgress(0)
    setSourceParsing(false)
  }

  function withDraftGuard(action: () => void) {
    if (saving || sourceParsing) return
    const proceed = () => { clearSourceDraft(); action() }
    if (hasUnsavedChanges) {
      Modal.confirm({ title: 'Leave these JD edits?', content: 'Unsaved edits and the selected source document will be discarded. Your saved versions are unchanged.', okText: 'Discard edits', cancelText: 'Keep editing', onOk: proceed })
    } else proceed()
  }

  function selectRequisition(value: number) {
    withDraftGuard(() => {
      setRequisitionId(value)
      setDraftSnapshot(null)
      setVersions([])
      selectStep('role')
      void loadVersions(value)
    })
  }

  function selectClient(value: number) {
    withDraftGuard(() => {
      ++scopeLoad.current
      ++versionLoad.current
      setClientId(value)
      setRequisitionId(0)
      setRequisitions([])
      requisitionsRef.current = []
      setDraftSnapshot(null)
      setVersions([])
      selectStep('role')
    })
  }

  async function loadVersions(requestId: number, preferredId = 0, requestRows = requisitionsRef.current) {
    const sequence = ++versionLoad.current
    setLoading(true)
    try {
      const rows = await getRecruitmentJobDescriptions(requestId)
      if (sequence !== versionLoad.current) return
      const preferred = rows.find(row => row.id === preferredId)
        ?? rows.find(row => editableStatuses.has(row.status))
        ?? rows[0]
      const full = preferred ? await getRecruitmentJobDescription(preferred.id) : null
      if (sequence !== versionLoad.current) return
      setVersions(rows)
      const request = requestRows.find(row => row.id === requestId)
      setDraftSnapshot(full ?? preferred ?? (request ? blankDescription(request) : null))
    } catch {
      if (sequence === versionLoad.current) message.error('Unable to load the JD. Please try again.')
    } finally {
      if (sequence === versionLoad.current) setLoading(false)
    }
  }

  function chooseVersion(id: number) {
    if (!id || id === draft?.id) return
    withDraftGuard(() => { setHistoryOpen(false); void loadVersions(requisitionId, id) })
  }

  function startRevision() {
    if (!selectedRequisition) return message.warning('Select a requisition first.')
    withDraftGuard(() => {
      setDraftSnapshot(draft?.id ? cloneDescription(draft) : blankDescription(selectedRequisition))
      selectStep('role')
    })
  }

  function patch(value: Partial<RecruitmentJobDescriptionVersion>) {
    setDraft(current => current ? { ...current, ...value } : current)
  }

  function selectSection(key: string) {
    const section = key as EditorSection
    setEditorSection(section)
    setEditorStep(section === 'role' || section === 'responsibilities' ? 'role' : 'screening')
  }

  function selectStep(key: string) {
    const step = key as EditorStep
    setEditorStep(step)
    setEditorSection(step === 'role' ? 'role' : 'skills')
  }

  function screenResumes() {
    if (!draft || draft.status !== 'Approved' || !selectedRequisition?.openPositionId) return
    const query = new URLSearchParams({
      clientId: String(clientId), positionId: String(selectedRequisition.openPositionId),
      requisitionId: String(requisitionId),
    })
    navigate(`/recruitment/ats-screening?${query}`)
  }

  function openSourceDocument() {
    if (selectedRequisition) setSourceDocumentsOpen(true)
    else withDraftGuard(() => navigate(`/recruitment/requisitions?new=1${clientId ? `&clientId=${clientId}` : ''}`))
  }

  async function parseSourceFile(file: File) {
    if (!draft || !selectedRequisition || editingDisabled) return
    if (!/\.(pdf|docx|txt)$/i.test(file.name)) return void message.error('Choose a PDF, DOCX or text document.')
    if (file.size > 10 * 1024 * 1024) return void message.error('Choose a document of 10 MB or smaller.')
    const sequence = ++sourceLoad.current
    setSourceFile(file)
    setSourceReview(null)
    setSourceUploadError('')
    setSourceUploadProgress(0)
    setSourceParsing(true)
    try {
      const response = await parseRecruitmentRequestDocument(file)
      if (sequence !== sourceLoad.current) return
      if (!response.ok || !response.data) {
        setSourceReview({ fields: [], warnings: [response.error || 'Automatic reading is unavailable. Complete the JD manually; the selected document will attach when you save.'] })
        return
      }
      const result = response.data
      const suggestions = descriptionSuggestions(selectedRequisition, result)
      setDraft(current => current ? mergeDescriptionSuggestions(current, suggestions.values) : current)
      setSourceReview({ fields: suggestions.fields, warnings: result.warnings })
      if (suggestions.fields.length) message.success(`${suggestions.fields.length} JD sections prefilled. Review them before saving.`)
    } catch (cause) {
      if (sequence === sourceLoad.current) setSourceReview({ fields: [], warnings: [cause instanceof Error ? cause.message : 'The document could not be read automatically. You can complete the JD manually.'] })
    } finally {
      if (sequence === sourceLoad.current) setSourceParsing(false)
    }
  }

  async function storeSourceDocument() {
    if (!sourceFile || !selectedRequisition) return true
    setSourceUploadError('')
    try {
      const configurations = await getEffectiveAttachmentConfigurations(selectedRequisition.clientId, 'RECRUITMENT', 'HIRING_REQUEST')
      const configuration = configurations.find(row => row.attributeCode === 'RECRUITMENT_REQUEST_SOURCE')
      if (!configuration) throw new Error('The hiring-source attachment field is not configured for this client.')
      const result = await uploadEntityAttachment(configuration.id, 'RECRUITMENT_REQUISITION', selectedRequisition.id, sourceFile, {}, setSourceUploadProgress)
      if (!result.ok) throw new Error(result.error || 'The source document could not be attached.')
      setSourceFile(null)
      setSourceUploadProgress(0)
      setAttachmentRefresh(value => value + 1)
      return true
    } catch (cause) {
      const reason = cause instanceof Error ? cause.message : 'Attachment upload failed.'
      setSourceUploadError(`JD saved. Source document is still pending: ${reason}`)
      return false
    }
  }

  async function retrySourceUpload() {
    if (!draft?.id || !sourceUploadError || saving) return
    setSaving(true)
    try { await storeSourceDocument() } finally { setSaving(false) }
  }

  async function saveDraft() {
    if (!draft || editingDisabled) return
    const error = validateDescription(draft)
    if (error) {
      selectSection(!draft.title.trim() || !draft.summary.trim() ? 'role'
        : !draft.responsibilities.some(row => row.responsibilityText.trim()) ? 'responsibilities' : 'skills')
      return message.warning(error)
    }
    setSaving(true)
    try {
      const payload = normalizeDescription(draft)
      const response = await saveRecruitmentJobDescription(payload)
      if (!response.ok || !response.data) return
      setDraftSnapshot(response.data)
      onSaved?.(response.data)
      await storeSourceDocument()
      await loadVersions(response.data.requisitionId, response.data.id)
    } finally { setSaving(false) }
  }

  function openApprovalDialog() {
    setApprovalMode(approvalWorkflows.length ? 'workflow' : canDirectApprove ? 'direct' : 'workflow')
    setApprovalOpen(true)
  }

  async function completeApprovalRoute() {
    if (!draft?.id || readOnly || (approvalMode === 'workflow' && !workflowId)) return
    setSaving(true)
    const response = approvalMode === 'direct'
      ? await approveRecruitmentJobDescriptionDirectly(draft.id)
      : await submitRecruitmentJobDescription(draft.id, workflowId!)
    setSaving(false)
    if (!response.ok || !response.data) return
    setApprovalOpen(false)
    setWorkflowId(undefined)
    await loadVersions(draft.requisitionId, draft.id)
  }

  return <section className="orchestration-shell jd-manager">
    <div className="orchestration-toolbar">
      <div>
        <span className="orchestration-kicker">Demand to approved role profile</span>
        <h2 className="orchestration-title">Job Description Workspace</h2>
        <p className="orchestration-subtitle">Review the role, set the skills, then screen resumes against the approved JD.</p>
      </div>
      <Space wrap className="jd-scope-controls">
        {!clientScopeManaged && <Select aria-label="Client" value={clientId || undefined} placeholder="Select client" showSearch optionFilterProp="label" className="jd-client-select"
          options={clients.map(row => ({ value: row.id, label: row.name }))}
          disabled={saving || sourceParsing} onChange={selectClient} />}
        <Select aria-label="Requisition" value={requisitionId || undefined} placeholder="Select requisition" showSearch optionFilterProp="label" className="jd-request-select"
          options={requisitions.map(row => ({ value: row.id, label: `${row.rfrNumber} · ${row.positionTitle}` }))}
          notFoundContent="No draft or approved hiring requests for this client"
          disabled={saving || sourceParsing || loading} onChange={selectRequisition} />
        <Button data-testid="jd-new-description" icon={<FileAddOutlined />} disabled={!selectedRequisition || saving || sourceParsing} onClick={startRevision}>{draft?.id ? 'Create revision' : 'New JD'}</Button>
        <Button data-testid="jd-new-vacancy" icon={<PlusOutlined />} disabled={saving || sourceParsing} onClick={() => withDraftGuard(() => navigate(`/recruitment/requisitions?new=1${clientId ? `&clientId=${clientId}` : ''}`))}>New vacancy</Button>
      </Space>
    </div>

    {selectedRequisition && <Collapse className="jd-context-summary" size="small"><Collapse.Panel key="request" header={<Space wrap><Typography.Text strong>{selectedRequisition.positionTitle}</Typography.Text><Typography.Text type="secondary">{selectedRequisition.rfrNumber}</Typography.Text><StatusTag status={selectedRequisition.status} /></Space>}>
      <Descriptions size="small" column={{ xs: 1, sm: 2, xl: 3, xxl: 5 }}>
        <Descriptions.Item label="Request">{selectedRequisition.rfrNumber}</Descriptions.Item>
        <Descriptions.Item label="Department">{selectedRequisition.department || '—'}</Descriptions.Item>
        <Descriptions.Item label="Hiring type">{selectedRequisition.hiringType || '—'}</Descriptions.Item>
        <Descriptions.Item label="Openings">{selectedRequisition.numberOfOpenings}</Descriptions.Item>
        <Descriptions.Item label="Budget">{selectedRequisition.budgetAvailable ? `${selectedRequisition.currency} ${selectedRequisition.budgetAmount.toLocaleString()}` : 'Not specified'}</Descriptions.Item>
      </Descriptions>
      {selectedRequisition.status !== 'Approved' && <Alert style={{ marginTop: 12 }} type="info" showIcon message="JD preparation is open" description="You can complete and save this JD now. Submit or approve it after the hiring request is approved." />}
    </Collapse.Panel></Collapse>}

    <Spin spinning={loading}>
      {!selectedRequisition ? <Card><Empty description={requisitions.length ? 'Select a hiring request to prepare its job description.' : 'No draft or approved hiring request exists for this client.'} /><Button icon={<FileAddOutlined />} onClick={openSourceDocument}>Start from a hiring document</Button></Card> : <div className="jd-simple-workspace">
        <Modal className="jd-history-modal" title={<Space><AuditOutlined /> JD version history</Space>} open={historyOpen} onCancel={() => setHistoryOpen(false)} footer={<Button icon={<PlusOutlined />} disabled={saving || sourceParsing} onClick={() => { startRevision(); setHistoryOpen(false) }}>New version</Button>}>
          <List dataSource={versions} locale={{ emptyText: 'No saved versions yet.' }} renderItem={row => <List.Item className={draft?.id === row.id ? 'active' : ''} onClick={() => void chooseVersion(row.id)} actions={canDelete ? [<Popconfirm key="delete" title="Delete this JD version?" description="Delete linked postings first. ATS-scored versions are retained for audit." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async event => { event?.stopPropagation(); const response = await deleteRecruitmentJobDescription(row.id); if (response.ok) { if (draft?.id === row.id) setDraft(null); setVersions(await getRecruitmentJobDescriptions(requisitionId)) } }}><Button aria-label="Delete job description" danger size="small" icon={<DeleteOutlined />} onClick={event => event.stopPropagation()} /></Popconfirm>] : []}>
            <List.Item.Meta title={<Space><span>Version {row.versionNumber}</span><StatusTag status={row.status} /></Space>} description={row.title || 'Untitled job description'} />
          </List.Item>} />
          {!versions.length && <Alert showIcon type="info" message="Your first draft is ready" description="Complete the role profile, save it, then submit it to the configured approval workflow." />}
        </Modal>

        {!draft ? <Card><Empty description="Create or select a version." /></Card> : <div className="jd-editor">
          <div className="jd-version-toolbar">
            <Space wrap>
              <Select aria-label="JD version" className="jd-version-select" disabled={saving || sourceParsing} value={draft.id || 0} onChange={chooseVersion} options={[
                ...(!draft.id ? [{ value: 0, label: `New draft · v${draft.versionNumber || versions.length + 1}` }] : []),
                ...versions.map(row => ({ value: row.id, label: `v${row.versionNumber} · ${row.status}` })),
              ]} />
              <StatusTag status={draft.status} />
              <Button type="text" icon={<AuditOutlined />} disabled={saving || sourceParsing} onClick={() => setHistoryOpen(true)}>Version history</Button>
            </Space>
            <Button icon={<FileTextOutlined />} onClick={openSourceDocument}>Source document</Button>
          </div>
          <Tabs className="jd-editor-steps" activeKey={editorStep} onChange={selectStep} items={[
            { key: 'role', label: <span data-testid="jd-step-role"><FileTextOutlined /> 1. Role details</span> },
            { key: 'screening', label: <span data-testid="jd-step-screening"><RobotOutlined /> 2. Skills & screening</span> },
          ]} />
          <RecruitmentWorkspaceLayout className="jd-section-workspace" ariaLabel="Job description editor" activeKey={editorSection} onChange={selectSection}
            navigation={editorStep === 'role' ? [
              { key: 'role', label: 'Role overview', description: 'Title, summary and purpose', icon: <FileTextOutlined /> },
              { key: 'responsibilities', label: 'Responsibilities', description: 'Outcomes and ownership', icon: <OrderedListOutlined />, badge: draft.responsibilities.filter(row => row.responsibilityText.trim()).length },
            ] : [
              { key: 'skills', label: 'Skills & ATS scoring', description: 'Must-have and preferred', icon: <RobotOutlined />, badge: draft.skills.length },
              { key: 'qualifications', label: 'Qualifications', description: 'Education and specialization', icon: <SafetyCertificateOutlined />, badge: draft.qualifications.length },
              { key: 'additional', label: 'Additional requirements', description: 'Certifications, languages, benefits', icon: <GiftOutlined />, badge: draft.certifications.length + draft.languages.length + draft.benefits.length },
            ]}
            footer={<div className="jd-editor-footer">
              <Typography.Text type="secondary">{readOnly ? `${draft.status} · v${draft.versionNumber}` : draft.id ? 'Editing saved draft' : 'New draft · review prefilled details'}</Typography.Text>
              <Space wrap>
                {editorStep === 'role' && <Button icon={<ArrowRightOutlined />} onClick={() => selectStep('screening')}>Skills & screening</Button>}
                {!readOnly && <Button type="primary" icon={<SaveOutlined />} loading={saving} disabled={sourceParsing} onClick={() => void saveDraft()}>{draft.id ? 'Update draft' : 'Save draft'}</Button>}
                {!readOnly && <Tooltip title={hasUnsavedChanges ? 'Save your current JD edits before approval' : !draft.id ? 'Save this JD first' : selectedRequisition.status !== 'Approved' ? 'Approve the hiring request before JD approval' : undefined}><Button icon={<SendOutlined />} disabled={!draft.id || hasUnsavedChanges || editingDisabled || selectedRequisition.status !== 'Approved'} onClick={openApprovalDialog}>{canDirectApprove ? 'Review approval route' : 'Submit for approval'}</Button></Tooltip>}
                {draft.status === 'Approved' && <Tooltip title={!selectedRequisition.openPositionId ? 'A linked vacancy is required for resume screening' : "Screening uses the vacancy's current approved JD and configured ATS rules"}><Button data-testid="jd-screen-resumes" type="primary" icon={<RobotOutlined />} disabled={!selectedRequisition.openPositionId || selectedRequisition.status !== 'Approved'} onClick={screenResumes}>Screen resumes</Button></Tooltip>}
              </Space>
            </div>}>
          <div className="jd-status-notices">
            {sourceUploadError && <Alert className="jd-source-review" data-testid="jd-source-upload-error" type="warning" showIcon message={sourceUploadError} action={<Button size="small" loading={saving} onClick={() => void retrySourceUpload()}>Retry attachment</Button>} />}
            {approvalProgress && <Alert className="jd-approval-progress" data-testid="jd-approval-progress" type="info" showIcon
              message={`Pending with ${approvalProgress.pendingApproverName || 'assigned approver'}`}
              description={[approvalProgress.currentStageName, approvalProgress.currentApproverType, approvalProgress.pendingSince ? `Since ${formatProgressDate(approvalProgress.pendingSince)}` : ''].filter(Boolean).join(' · ')}
              action={<Button size="small" icon={<ReloadOutlined />} loading={approvalProgressLoading} onClick={() => setApprovalProgressRefreshKey(value => value + 1)}>Refresh</Button>} />}
            {!approvalProgress && approvalProgressLoading && <Alert className="jd-approval-progress" type="info" showIcon message="Loading approval progress..." />}
            {approvalProgressError && <Alert className="jd-approval-progress" data-testid="jd-approval-progress-error" type="error" showIcon
              message="Approval progress could not be refreshed" description={approvalProgressError}
              action={<Button size="small" icon={<ReloadOutlined />} onClick={() => setApprovalProgressRefreshKey(value => value + 1)}>Try again</Button>} />}
            {readOnly && <Alert className="jd-readonly-alert" type={draft.status === 'Approved' ? 'success' : 'info'} showIcon message={`${draft.status} versions are immutable`} description="Create a new version to make changes without altering the historical approved JD." />}
          </div>
          {editorSection === 'role' && <Card size="small" title="Role overview">
            {!readOnly && <div className="jd-source-import" data-testid="jd-source-import">
              <div><Typography.Text strong>Start with a hiring request or JD document</Typography.Text><Typography.Text type="secondary">Upload a PDF, DOCX or TXT. Review the suggestions; the original attaches when you save this JD.</Typography.Text></div>
              <label className={`jd-source-picker${editingDisabled ? ' is-disabled' : ''}`}><input data-testid="jd-source-file" type="file" accept=".pdf,.docx,.txt" disabled={editingDisabled} onChange={event => { const file = event.currentTarget.files?.[0]; event.currentTarget.value = ''; if (file) void parseSourceFile(file) }} /><UploadOutlined /><span>{sourceParsing ? 'Reading document...' : sourceFile ? 'Choose another document' : 'Upload document'}</span></label>
            </div>}
            {sourceFile && <div className="jd-source-selection"><FileTextOutlined /><span>{sourceFile.name}</span><Tag color="orange">{saving && sourceUploadProgress > 0 ? `Attaching ${sourceUploadProgress}%` : 'Pending attachment'}</Tag><Button type="text" size="small" disabled={saving || sourceParsing} onClick={clearSourceDraft}>Remove file</Button>{sourceReview && !sourceReview.fields.length && <Button size="small" icon={<ReloadOutlined />} disabled={editingDisabled} onClick={() => void parseSourceFile(sourceFile)}>Retry reading</Button>}</div>}
            {sourceReview && <Alert className="jd-source-review" data-testid="jd-source-review" type={sourceReview.fields.length ? 'info' : 'warning'} showIcon message={sourceReview.fields.length ? `Review prefilled sections: ${sourceReview.fields.join(', ')}` : 'Complete the JD manually'} description={<><span>Review the title, summary, responsibilities and required skills before saving. Client and hiring-request identity are unchanged.</span>{sourceReview.warnings.map((warning, index) => <div key={index}>{warning}</div>)}</>} />}
            {selectedRequisition.sourceDocumentName && <Alert className="jd-prefill-notice" type="info" showIcon message={`Source: ${selectedRequisition.sourceDocumentName}`} description="Details from the hiring request are prefilled in a new JD. Review and edit them before saving." />}
            <Form layout="vertical" disabled={editingDisabled}>
              <Row gutter={14}>
                <Col xs={24} lg={14}><Form.Item label="Public role title" required><Input value={draft.title} maxLength={240} onChange={event => patch({ title: event.target.value })} /></Form.Item></Col>
                <Col xs={24} lg={10}><Form.Item label="Internal position"><Input value={`${selectedRequisition.positionTitle} (${selectedRequisition.rfrNumber})`} disabled /></Form.Item></Col>
              </Row>
              <Form.Item label="Role summary" required extra="A concise overview shown to candidates and approvers."><Input.TextArea rows={3} value={draft.summary} maxLength={4000} showCount onChange={event => patch({ summary: event.target.value })} /></Form.Item>
              <Form.Item label="Role purpose"><Input.TextArea rows={3} value={draft.rolePurpose} maxLength={4000} showCount onChange={event => patch({ rolePurpose: event.target.value })} /></Form.Item>
            </Form>
          </Card>}

          {editorSection === 'responsibilities' && <RepeaterSection title="Responsibilities" description="Define measurable outcomes and ownership for this role." rows={draft.responsibilities} readOnly={editingDisabled}
            addLabel="Add responsibility" onAdd={() => patch({ responsibilities: [...draft.responsibilities, responsibility()] })}
            onChange={responsibilities => patch({ responsibilities })}
            render={(row, index, update) => <Input.TextArea rows={2} value={row.responsibilityText} placeholder={`Responsibility ${index + 1}`} maxLength={1000} onChange={event => update({ responsibilityText: event.target.value })} />} />}

          {editorSection === 'skills' && <RepeaterSection className="jd-skills-repeater" title="Skills & ATS scoring" description="Must-have skills determine eligibility. Preferred skills improve ranking. Keep equal weighting, or adjust each skill under Advanced." rows={draft.skills} readOnly={editingDisabled}
            addLabel="Add skill" onAdd={() => patch({ skills: [...draft.skills, skill()] })} onChange={skills => patch({ skills })}
            render={(row, _index, update) => <Row gutter={10}>
              <Col xs={24} md={16}><Form.Item label="Skill" required validateStatus={!row.skillName.trim() ? 'error' : undefined} help={!row.skillName.trim() ? 'Enter a skill or remove this row.' : undefined}><Input value={row.skillName} placeholder="e.g. SAP MM" onChange={event => update({ skillName: event.target.value })} /></Form.Item></Col>
              <Col xs={24} md={8}><Form.Item label="Category" required><Select value={row.isRequired ? 'MustHave' : 'Preferred'} options={[{ value: 'MustHave', label: 'Must-have' }, { value: 'Preferred', label: 'Preferred' }]} onChange={value => update({ isRequired: value === 'MustHave' })} /></Form.Item></Col>
              <Col span={24}><Collapse className="jd-skill-advanced" ghost size="small"><Collapse.Panel key="advanced" header={<span>Advanced <Typography.Text type="secondary">{[
                  row.minimumYears > 0 ? `${row.minimumYears} years` : '', row.minimumProficiency,
                  row.weightPercent > 0 ? `Weight ${row.weightPercent}` : 'Equal weighting',
                ].filter(Boolean).join(' · ')}</Typography.Text></span>}>
                <Row gutter={12}>
                  <Col xs={24} md={8}><Form.Item label={<Tooltip title="Set this only when the resume must prove experience specifically in this skill. It is not the candidate's total career experience.">Skill experience</Tooltip>}><InputNumber min={0} max={50} step={0.5} value={row.minimumYears || undefined} placeholder="Years" onChange={value => update({ minimumYears: Number(value ?? 0) })} /></Form.Item></Col>
                  <Col xs={24} md={8}><Form.Item label={<Tooltip title="Reviewer note shown in ATS evidence. It does not automatically pass, fail, or change the score.">Reviewer level</Tooltip>}><Select value={row.minimumProficiency || undefined} placeholder="Not specified" allowClear options={['Beginner', 'Intermediate', 'Advanced', 'Expert'].map(value => ({ value, label: value }))} onChange={value => update({ minimumProficiency: value ?? '' })} /></Form.Item></Col>
                  <Col xs={24} md={8}><Form.Item label={<Tooltip title="Relative importance inside the Must-have or Preferred group. Leave every skill in the group at zero for equal weighting.">Relative weight</Tooltip>}><InputNumber min={0} max={100} value={row.weightPercent || undefined} placeholder="Equal" onChange={value => update({ weightPercent: Number(value ?? 0) })} /></Form.Item></Col>
                </Row>
              </Collapse.Panel></Collapse></Col>
            </Row>} />}

          {editorSection === 'qualifications' && <RepeaterSection title="Qualifications" description="Keep mandatory and preferred education requirements explicit." rows={draft.qualifications} readOnly={editingDisabled}
            addLabel="Add qualification" onAdd={() => patch({ qualifications: [...draft.qualifications, qualification()] })} onChange={qualifications => patch({ qualifications })}
            render={(row, _index, update) => <Row gutter={10}>
              <Col xs={24} md={10}><Form.Item label="Qualification" required><Input value={row.qualificationName} placeholder="e.g. B.Tech / MCA" onChange={event => update({ qualificationName: event.target.value })} /></Form.Item></Col>
              <Col xs={24} md={10}><Form.Item label="Specialization"><Input value={row.specialization} placeholder="Computer Science" onChange={event => update({ specialization: event.target.value })} /></Form.Item></Col>
              <Col xs={24} md={4}><Form.Item label="Mandatory"><Switch checked={row.isMandatory} onChange={isMandatory => update({ isMandatory })} /></Form.Item></Col>
            </Row>} />}

          {editorSection === 'additional' && <Card size="small" className="jd-additional-requirements" title={<div><Typography.Text strong>Additional role requirements</Typography.Text><Typography.Text type="secondary">Keep certifications, language expectations and candidate benefits organised in focused tabs.</Typography.Text></div>}>
            <Tabs
              className="jd-additional-tabs"
              defaultActiveKey="certifications"
              items={[
                {
                  key: 'certifications',
                  label: <span data-testid="jd-additional-tab-certifications"><SafetyCertificateOutlined /> Certifications <Tag>{draft.certifications.length}</Tag></span>,
                  children: <Space direction="vertical" size={12} style={{ width: '100%' }}>
                    <Alert data-testid="jd-certification-proof-guidance" type="info" showIcon message="Candidate proof uses the secure Dynamic Form upload flow" description="Choose a global attachment field only when candidates must upload evidence. Before publishing the job, add that exact Secure upload field to either the published application form or one candidate-facing External Form, Documents or Pre-Onboarding stage. Keep each proof in one candidate step." />
                    {!candidateProofOptions.length && <Alert data-testid="jd-certification-proof-setup-required" type="warning" showIcon message="No candidate proof upload is configured" description="In Settings > Attachments, add an active RECRUITMENT candidate-facing field configuration. Then add it as a Secure upload field in Form Builder and publish the form." />}
                    <RepeaterSection compact title="Certification requirements" description="List role-specific credentials, eligibility and whether evidence must be collected from the candidate." rows={draft.certifications} readOnly={editingDisabled} addLabel="Add certification" onAdd={() => patch({ certifications: [...draft.certifications, certification()] })} onChange={certifications => patch({ certifications })}
                      render={(row, index, update) => <Row gutter={10}><Col xs={24} md={10}><Form.Item label="Certification"><Input value={row.certificationName} onChange={event => update({ certificationName: event.target.value })} /></Form.Item></Col><Col xs={24} md={9}><Form.Item label="Candidate proof upload" extra={candidateProofOptions.find(option => option.value === row.candidateProofAttachmentFieldConfigurationId)?.metadata ? `Candidate must also enter: ${candidateProofOptions.find(option => option.value === row.candidateProofAttachmentFieldConfigurationId)?.metadata}.` : 'Optional; leave blank when no certificate file is required.'}><Select data-testid={`jd-certification-proof-${index}`} disabled={!candidateProofOptions.length} allowClear showSearch optionFilterProp="label" placeholder={candidateProofOptions.length ? 'Do not collect proof' : 'Configure a candidate upload field first'} value={row.candidateProofAttachmentFieldConfigurationId || undefined} onChange={candidateProofAttachmentFieldConfigurationId => update({ candidateProofAttachmentFieldConfigurationId: candidateProofAttachmentFieldConfigurationId ?? null })} options={candidateProofOptions.map(option => ({ ...option, disabled: draft.certifications.some((item, itemIndex) => itemIndex !== index && item.candidateProofAttachmentFieldConfigurationId === option.value) }))} /></Form.Item></Col><Col xs={24} md={5}><Form.Item label="Mandatory"><Switch checked={row.isMandatory} onChange={isMandatory => update({ isMandatory })} /></Form.Item></Col></Row>} />
                  </Space>,
                },
                {
                  key: 'languages',
                  label: <span data-testid="jd-additional-tab-languages"><GlobalOutlined /> Languages <Tag>{draft.languages.length}</Tag></span>,
                  children: <RepeaterSection compact title="Language requirements" description="Capture the expected proficiency and whether each language is mandatory." rows={draft.languages} readOnly={editingDisabled} addLabel="Add language" onAdd={() => patch({ languages: [...draft.languages, language()] })} onChange={languages => patch({ languages })}
                    render={(row, _index, update) => <Row gutter={10}><Col xs={24} md={9}><Form.Item label="Language"><Input value={row.languageName} onChange={event => update({ languageName: event.target.value })} /></Form.Item></Col><Col xs={24} md={10}><Form.Item label="Proficiency"><Select value={row.proficiency || undefined} allowClear options={['Basic', 'Conversational', 'Professional', 'Native'].map(value => ({ value, label: value }))} onChange={value => update({ proficiency: value ?? '' })} /></Form.Item></Col><Col xs={24} md={5}><Form.Item label="Mandatory"><Switch checked={row.isMandatory} onChange={isMandatory => update({ isMandatory })} /></Form.Item></Col></Row>} />,
                },
                {
                  key: 'benefits',
                  label: <span data-testid="jd-additional-tab-benefits"><GiftOutlined /> Benefits <Tag>{draft.benefits.length}</Tag></span>,
                  children: <RepeaterSection compact title="Candidate benefits" description="Describe the benefits candidates should see with this role." rows={draft.benefits} readOnly={editingDisabled} addLabel="Add benefit" onAdd={() => patch({ benefits: [...draft.benefits, benefit()] })} onChange={benefits => patch({ benefits })}
                    render={(row, _index, update) => <Row gutter={10}><Col xs={24} md={8}><Form.Item label="Benefit"><Input value={row.benefitName} onChange={event => update({ benefitName: event.target.value })} /></Form.Item></Col><Col xs={24} md={16}><Form.Item label="Description"><Input value={row.description} onChange={event => update({ description: event.target.value })} /></Form.Item></Col></Row>} />,
                },
              ]}
            />
          </Card>}
          </RecruitmentWorkspaceLayout>
        </div>}
      </div>}
    </Spin>

    <Modal width={860} title="Source documents" open={sourceDocumentsOpen} onCancel={() => setSourceDocumentsOpen(false)} footer={null} destroyOnClose>
      {selectedRequisition && <EntityAttachmentPanel key={`${selectedRequisition.id}-${attachmentRefresh}`} entityType="RECRUITMENT_REQUISITION" entityId={selectedRequisition.id} clientId={selectedRequisition.clientId} moduleCode="RECRUITMENT" formCodes={['HIRING_REQUEST']} title="Hiring documents" description="Original documents linked to this hiring request. Preview or download a saved version." readOnly />}
    </Modal>

    <Modal width={760} title="Review job-description approval route" open={approvalOpen} okText={approvalMode === 'direct' ? 'Approve directly' : 'Submit to workflow'} confirmLoading={saving}
      okButtonProps={{ disabled: approvalMode === 'workflow' && !workflowId }} onOk={() => void completeApprovalRoute()} onCancel={() => { setApprovalOpen(false); setWorkflowId(undefined) }}>
      {draft && <Descriptions bordered size="small" column={2} style={{ marginBottom: 16 }}>
        <Descriptions.Item label="Role">{draft.title}</Descriptions.Item>
        <Descriptions.Item label="Version">v{draft.versionNumber}</Descriptions.Item>
        <Descriptions.Item label="Hiring request">{selectedRequisition?.rfrNumber || '—'}</Descriptions.Item>
        <Descriptions.Item label="Openings">{selectedRequisition?.numberOfOpenings ?? '—'}</Descriptions.Item>
        <Descriptions.Item label="Summary" span={2}>{draft.summary}</Descriptions.Item>
        <Descriptions.Item label="Must-have skills" span={2}>{draft.skills.filter(row => row.isRequired).map(row => row.skillName).filter(Boolean).join(', ') || 'None'}</Descriptions.Item>
        <Descriptions.Item label="Preferred skills" span={2}>{draft.skills.filter(row => !row.isRequired).map(row => row.skillName).filter(Boolean).join(', ') || 'None'}</Descriptions.Item>
      </Descriptions>}
      {canDirectApprove && <Form layout="vertical"><Form.Item label="Approval route" required>
        <Radio.Group optionType="button" buttonStyle="solid" value={approvalMode} onChange={event => { setApprovalMode(event.target.value); setWorkflowId(undefined) }} options={[{ value: 'workflow', label: 'Send to approver' }, { value: 'direct', label: 'Approve directly' }]} />
      </Form.Item></Form>}
      {approvalMode === 'workflow' && <Alert showIcon type="info" message="The complete JD snapshot will be sent to My Tasks. This version becomes read-only while approval is pending." />}
      {approvalMode === 'workflow' ? <><Form layout="vertical" style={{ marginTop: 16 }}><Form.Item label="Job-description approval workflow" required>
        <Select value={workflowId} showSearch optionFilterProp="label" placeholder="Select the configured approval chain" onChange={setWorkflowId}
          options={approvalWorkflows.map(row => ({ value: row.id, label: `${row.name}${row.code ? ` · ${row.code}` : ''}` }))} />
      </Form.Item></Form>
      {!approvalWorkflows.length && <Alert type="warning" showIcon message="No JD approval workflow is configured" description="Only a workflow whose resource type is RecruitmentJobDescription is accepted. Configure one in Workflow Setup, or use direct approval if your role permits it." />}</> :
      <Alert type="warning" showIcon message="Direct approval is restricted to system administrators" description="This immediately approves the saved version, links it to the vacancy and records the administrator, timestamp and complete JD snapshot in the recruitment audit." />}
    </Modal>
  </section>
}

type OrderedRow = { id: number; displayOrder: number }
type RepeaterProps<T extends OrderedRow> = {
  className?: string
  title: string
  description?: string
  rows: T[]
  readOnly: boolean
  addLabel: string
  compact?: boolean
  onAdd: () => void
  onChange: (rows: T[]) => void
  render: (row: T, index: number, update: (value: Partial<T>) => void) => ReactNode
}

export function RepeaterSection<T extends OrderedRow>({ className, title, description, rows, readOnly, addLabel, compact, onAdd, onChange, render }: RepeaterProps<T>) {
  const update = (index: number, value: Partial<T>) => onChange(rows.map((row, rowIndex) => rowIndex === index ? { ...row, ...value } : row))
  const remove = (index: number) => onChange(rows.filter((_row, rowIndex) => rowIndex !== index).map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })))
  const move = (index: number, delta: number) => {
    const target = index + delta
    if (target < 0 || target >= rows.length) return
    const next = [...rows]; [next[index], next[target]] = [next[target], next[index]]
    onChange(next.map((row, rowIndex) => ({ ...row, displayOrder: rowIndex + 1 })))
  }
  return <Card size="small" className={['jd-repeater', compact ? 'compact' : '', className || ''].filter(Boolean).join(' ')} title={<div><Typography.Text strong>{title}</Typography.Text>{description && <Typography.Text type="secondary">{description}</Typography.Text>}</div>}
    extra={<Button size="small" icon={<PlusOutlined />} disabled={readOnly} onClick={onAdd}>{addLabel}</Button>}>
    {!rows.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={`No ${title.toLowerCase()} added.`} /> : rows.map((row, index) => <Card key={`${row.id}-${index}`} size="small" className="jd-repeater-row">
      <div className="jd-repeater-actions">
        <Tag>{index + 1}</Tag>
        <Tooltip title="Move up"><Button size="small" icon={<ArrowUpOutlined />} disabled={readOnly || index === 0} onClick={() => move(index, -1)} /></Tooltip>
        <Tooltip title="Move down"><Button size="small" icon={<ArrowDownOutlined />} disabled={readOnly || index === rows.length - 1} onClick={() => move(index, 1)} /></Tooltip>
        <Tooltip title="Remove"><Button size="small" danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => remove(index)} /></Tooltip>
      </div>
      <div className="jd-repeater-content"><Form layout="vertical" disabled={readOnly}>{render(row, index, value => update(index, value))}</Form></div>
    </Card>)}
  </Card>
}

function StatusTag({ status }: { status: string }) {
  const color = status === 'Approved' ? 'green' : status === 'Pending Approval' ? 'blue' : status === 'Rejected' ? 'red' : status === 'Sent Back' ? 'orange' : 'gold'
  return <Tag color={color}>{status || 'Draft'}</Tag>
}

function formatProgressDate(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString('en-IN')
}

function blankDescription(request: RecruitmentRequisition): RecruitmentJobDescriptionVersion {
  const parsed = parsedSource(request.sourceParsedJson)
  const required = parsed.requiredSkills.length ? parsed.requiredSkills : splitTerms(request.requiredSkills)
  const preferred = parsed.preferredSkills.length ? parsed.preferredSkills : splitTerms(request.preferredSkills)
  const qualifications = parsed.qualifications.length ? parsed.qualifications : splitTerms(request.qualification)
  const responsibilities = parsed.responsibilities.length ? parsed.responsibilities : splitTerms(request.businessJustification || request.reasonForHiring)
  return {
    id: 0, requisitionId: request.id, clientId: request.clientId, versionNumber: 1,
    title: request.positionTitle, summary: parsed.roleSummary || request.businessJustification || request.reasonForHiring || '',
    rolePurpose: parsed.rolePurpose || request.reasonForHiring || '', status: 'Draft', workflowInstanceId: null,
    responsibilities: (responsibilities.length ? responsibilities : ['']).map(responsibility),
    skills: [...required.map(name => skill(name, true)), ...preferred.map(name => skill(name, false))],
    qualifications: qualifications.map(name => qualification(name)),
    certifications: parsed.certifications.length ? parsed.certifications.map(name => ({ ...certification(), certificationName: name })) : splitTerms(request.certifications).map(name => ({ ...certification(), certificationName: name })),
    languages: parsed.languages.length ? parsed.languages.map(name => ({ ...language(), languageName: name })) : splitTerms(request.languages).map(name => ({ ...language(), languageName: name })),
    benefits: parsed.benefits.length ? parsed.benefits.map(name => ({ ...benefit(), benefitName: name })) : splitTerms(request.benefits).map(name => ({ ...benefit(), benefitName: name })),
  }
}

type ParsedSource = { roleSummary: string; rolePurpose: string; responsibilities: string[]; requiredSkills: string[]; preferredSkills: string[]; qualifications: string[]; certifications: string[]; languages: string[]; benefits: string[] }
const emptyParsedSource = (): ParsedSource => ({ roleSummary: '', rolePurpose: '', responsibilities: [], requiredSkills: [], preferredSkills: [], qualifications: [], certifications: [], languages: [], benefits: [] })
function parsedSource(value: string): ParsedSource {
  try {
    const row = JSON.parse(value || '{}') as Partial<ParsedSource>
    const list = (items: unknown) => Array.isArray(items) ? items.map(item => String(item || '').trim()).filter(Boolean) : []
    return { roleSummary: String(row.roleSummary || ''), rolePurpose: String(row.rolePurpose || ''), responsibilities: list(row.responsibilities), requiredSkills: list(row.requiredSkills), preferredSkills: list(row.preferredSkills), qualifications: list(row.qualifications), certifications: list(row.certifications), languages: list(row.languages), benefits: list(row.benefits) }
  } catch { return emptyParsedSource() }
}

function descriptionSnapshot(value: RecruitmentJobDescriptionVersion) {
  // Save normalization removes incomplete rows; those rows still contain edits
  // that must not be silently discarded when changing job/client/version.
  const { id, requisitionId, title, summary, rolePurpose, responsibilities, skills,
    qualifications, certifications, languages, benefits } = value
  return JSON.stringify({ id, requisitionId, title, summary, rolePurpose, responsibilities,
    skills, qualifications, certifications, languages, benefits })
}

function descriptionSuggestions(request: RecruitmentRequisition, result: RecruitmentRequestDocumentParseResult) {
  const detected = new Set(result.detectedFields)
  const text = (key: keyof RecruitmentRequestDocumentParseResult['draft']) => detected.has(key) ? String(result.draft[key] || '') : ''
  const mapped = blankDescription({
    ...request,
    positionTitle: text('positionTitle'), businessJustification: text('businessJustification'), reasonForHiring: text('reasonForHiring'),
    requiredSkills: text('requiredSkills'), preferredSkills: text('preferredSkills'), qualification: text('qualification'),
    certifications: text('certifications'), languages: text('languages'), benefits: text('benefits'),
    sourceParsedJson: result.draft.sourceParsedJson || '',
  })
  const values: Partial<RecruitmentJobDescriptionVersion> = {}
  const fields: string[] = []
  const add = <K extends keyof RecruitmentJobDescriptionVersion>(key: K, label: string, value: RecruitmentJobDescriptionVersion[K], populated: boolean) => {
    if (!populated) return
    Object.assign(values, { [key]: value })
    fields.push(label)
  }
  add('title', 'Title', mapped.title, Boolean(mapped.title.trim()))
  add('summary', 'Summary', mapped.summary, Boolean(mapped.summary.trim()))
  add('rolePurpose', 'Purpose', mapped.rolePurpose, Boolean(mapped.rolePurpose.trim()))
  add('responsibilities', 'Responsibilities', mapped.responsibilities, mapped.responsibilities.some(row => Boolean(row.responsibilityText.trim())))
  add('skills', 'Skills', mapped.skills, mapped.skills.length > 0)
  add('qualifications', 'Qualifications', mapped.qualifications, mapped.qualifications.length > 0)
  add('certifications', 'Certifications', mapped.certifications, mapped.certifications.length > 0)
  add('languages', 'Languages', mapped.languages, mapped.languages.length > 0)
  add('benefits', 'Benefits', mapped.benefits, mapped.benefits.length > 0)
  return { values, fields }
}

/** Retain existing rows and their scoring/proof settings; append newly detected requirements. */
function mergeDescriptionSuggestions(current: RecruitmentJobDescriptionVersion, values: Partial<RecruitmentJobDescriptionVersion>): RecruitmentJobDescriptionVersion {
  const merge = <T extends OrderedRow>(existing: T[], suggested: T[] | undefined, name: (row: T) => string): T[] => {
    if (!suggested?.length) return existing
    const rows = existing.filter(row => name(row).trim())
    const names = new Set(rows.map(row => name(row).trim().toLocaleLowerCase()))
    for (const row of suggested) {
      const key = name(row).trim().toLocaleLowerCase()
      if (!key || names.has(key)) continue
      rows.push(row)
      names.add(key)
    }
    return rows.map((row, index) => ({ ...row, displayOrder: index + 1 }))
  }
  return {
    ...current, ...values,
    responsibilities: merge(current.responsibilities, values.responsibilities, row => row.responsibilityText),
    skills: merge(current.skills, values.skills, row => row.skillName),
    qualifications: merge(current.qualifications, values.qualifications, row => `${row.qualificationName} ${row.specialization}`),
    certifications: merge(current.certifications, values.certifications, row => row.certificationName),
    languages: merge(current.languages, values.languages, row => row.languageName),
    benefits: merge(current.benefits, values.benefits, row => row.benefitName),
  }
}

function cloneDescription(source: RecruitmentJobDescriptionVersion): RecruitmentJobDescriptionVersion {
  const reset = <T extends { id: number; jobDescriptionVersionId: number; displayOrder: number }>(rows: T[]) => rows.map((row, index) => ({ ...row, id: localId(), jobDescriptionVersionId: 0, displayOrder: index + 1 }))
  return {
    ...source, id: 0, versionNumber: source.versionNumber + 1, status: 'Draft', workflowInstanceId: null,
    approvedByUserId: null, approvedAtUtc: null,
    responsibilities: reset(source.responsibilities), skills: reset(source.skills), qualifications: reset(source.qualifications),
    certifications: reset(source.certifications), languages: reset(source.languages), benefits: reset(source.benefits),
  }
}

function normalizeDescription(source: RecruitmentJobDescriptionVersion) {
  const order = <T extends { displayOrder: number }>(rows: T[]) => rows.map((row, index) => ({ ...row, displayOrder: index + 1 }))
  return {
    id: source.id, requisitionId: source.requisitionId, title: source.title.trim(), summary: source.summary.trim(), rolePurpose: source.rolePurpose.trim(),
    responsibilities: order(source.responsibilities.filter(row => row.responsibilityText.trim()).map(row => ({ ...row, responsibilityText: row.responsibilityText.trim() }))),
    skills: order(source.skills.filter(row => row.skillName.trim()).map(row => ({ ...row, skillName: row.skillName.trim() }))),
    qualifications: order(source.qualifications.filter(row => row.qualificationName.trim()).map(row => ({ ...row, qualificationName: row.qualificationName.trim(), specialization: row.specialization.trim() }))),
    certifications: order(source.certifications.filter(row => row.certificationName.trim()).map(row => ({ ...row, certificationName: row.certificationName.trim() }))),
    languages: order(source.languages.filter(row => row.languageName.trim()).map(row => ({ ...row, languageName: row.languageName.trim() }))),
    benefits: order(source.benefits.filter(row => row.benefitName.trim()).map(row => ({ ...row, benefitName: row.benefitName.trim(), description: row.description.trim() }))),
  }
}

function validateDescription(row: RecruitmentJobDescriptionVersion) {
  if (!row.title.trim()) return 'Enter the job title.'
  if (!row.summary.trim()) return 'Enter a candidate-facing role summary.'
  if (!row.responsibilities.some(item => item.responsibilityText.trim())) return 'Add at least one responsibility.'
  if (!row.skills.length) return 'Add at least one must-have or preferred skill.'
  if (row.skills.some(item => !item.skillName.trim())) return 'Enter a name for every skill row, or remove the blank row.'
  if (!row.skills.some(item => item.isRequired)) return 'Add at least one must-have skill.'
  if (row.skills.some(item => item.weightPercent < 0 || item.weightPercent > 100)) return 'Every ATS skill weight must be between 0 and 100.'
  for (const [label, skills] of [['Must-have', row.skills.filter(item => item.isRequired)], ['Preferred', row.skills.filter(item => !item.isRequired)]] as const) {
    const hasWeighted = skills.some(item => Number(item.weightPercent || 0) > 0)
    if (hasWeighted && skills.some(item => Number(item.weightPercent || 0) <= 0)) return `${label} skills must either all have a relative weight or all be left blank for equal weighting.`
  }
  return ''
}

function splitTerms(value: string) { return (value || '').split(/[,;\n]/).map(item => item.trim()).filter(Boolean) }
function responsibility(text = ''): RecruitmentJdResponsibility { return { id: localId(), jobDescriptionVersionId: 0, responsibilityText: text, displayOrder: 100 } }
function skill(name = '', isRequired = true): RecruitmentJdSkillRequirement { return { id: localId(), jobDescriptionVersionId: 0, skillId: null, skillName: name, isRequired, minimumYears: 0, minimumProficiency: '', weightPercent: 0, displayOrder: 100 } }
function qualification(name = ''): RecruitmentJdQualificationRequirement { return { id: localId(), jobDescriptionVersionId: 0, qualificationName: name, specialization: '', isMandatory: true, displayOrder: 100 } }
function certification(): RecruitmentJdCertificationRequirement { return { id: localId(), jobDescriptionVersionId: 0, certificationName: '', isMandatory: false, displayOrder: 100 } }
function language(): RecruitmentJdLanguageRequirement { return { id: localId(), jobDescriptionVersionId: 0, languageName: '', proficiency: '', isMandatory: false, displayOrder: 100 } }
function benefit(): RecruitmentJdBenefit { return { id: localId(), jobDescriptionVersionId: 0, benefitName: '', description: '', displayOrder: 100 } }
