import { useEffect, useMemo, useRef, useState } from 'react'
import {
  BranchesOutlined, CheckCircleOutlined, DeleteOutlined, DownloadOutlined, EditOutlined, EyeOutlined, FilePdfOutlined, PlusOutlined, ReloadOutlined, SaveOutlined, SearchOutlined, SendOutlined, UploadOutlined,
} from '@ant-design/icons'
import {
  Alert, AutoComplete, Button, Collapse, Form, Input, InputNumber, Modal, Popconfirm, Progress, Select, Space, Spin, Switch,
  Table, Tag, Tooltip, message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useAuthSession } from './AuthGate'
import { getClients, getEmployees } from '../services/payrollService'
import { getJson } from '../services/apiClient'
import { requisitionTextError, requisitionTextErrors, type RequisitionTextLimit } from '../services/requisitionTextLimits'
import {
  deleteRecruitmentRequisition, getRecruitmentMasterOptions, getRecruitmentRequisitions, saveRecruitmentRequisition,
  parseRecruitmentRequestDocument, submitRecruitmentRequisition,
} from '../services/recruitmentService'
import { getEffectiveAttachmentConfigurations, uploadEntityAttachment } from '../services/attachmentService'
import { getDropdowns, getWorkLocations } from '../services/settingsService'
import { getRecruitmentWorkOrder } from '../services/recruitmentCaseService'
import { getRecruitmentPipelineVersions, getRecruitmentPipelines } from '../services/recruitmentOrchestrationService'
import type {
  Client, Drop, Employee, RecruitmentOpenPosition, RecruitmentRequisition, RecruitmentRequestDocumentParseResult, SaveRecruitmentRequisition, WorkLocation,
} from '../types/payroll'
import { recruitmentStageColor } from '../utils/recruitmentStage'
import RecruitmentEditorDrawer from './RecruitmentEditorDrawer'
import RecruitmentMasterSelect from './RecruitmentMasterSelect'
import RecruitmentSeedPackModal from './RecruitmentSeedPackModal'
import RecruitmentJobDescriptionManager, { type RecruitmentJobDescriptionManagerHandle } from './RecruitmentJobDescriptionManager'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { downloadHiringSeedTemplate } from '../services/recruitmentSeedPackService'
import './RecruitmentRequisitionManager.css'

type Props = {
  initialClientId?: number
  clientScopeManaged?: boolean
  initialOpen?: boolean
  initialRequisitionId?: number
  initialWorkOrderId?: number
  initialWorkOrderLineId?: number
  statusScope?: string[]
  showStatusFilter?: boolean
  embedded?: boolean
  pipelinePositions?: RecruitmentOpenPosition[]
  onOpenPipeline?: (position: RecruitmentOpenPosition) => void
  onOpenRequestPipeline?: (request: RecruitmentRequisition) => void
  onChanged?: (row: RecruitmentRequisition) => void
  onPrepareJobDescription?: (row: RecruitmentRequisition) => void
}

type MasterOptions = {
  hiringTypes: string[]
  positionCategories: string[]
  experienceRanges: string[]
  priorities: string[]
}

type BrowserDraftSnapshot = {
  version: 1
  savedAt: string
  values: SaveRecruitmentRequisition
  sourceParse: RecruitmentRequestDocumentParseResult | null
  sourceFilePending: boolean
}

type AutoSaveState = 'idle' | 'local' | 'saving' | 'saved' | 'error'

const editableStatuses = new Set(['Draft', 'Sent Back'])
const emptyMasters: MasterOptions = {
  hiringTypes: [], positionCategories: [], experienceRanges: [], priorities: [],
}

export default function RecruitmentRequisitionManager({ initialClientId = 0, clientScopeManaged = false, initialOpen = false, initialRequisitionId = 0, initialWorkOrderId = 0, initialWorkOrderLineId = 0, statusScope = [], showStatusFilter = true, embedded = false, pipelinePositions = [], onOpenPipeline, onOpenRequestPipeline, onChanged, onPrepareJobDescription }: Props) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const canManageMasters = Boolean(session?.user.permissions.includes('settings.manage'))
  const [form] = Form.useForm<SaveRecruitmentRequisition>()
  const [clients, setClients] = useState<Client[]>([])
  const [dropdowns, setDropdowns] = useState<Drop[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [masters, setMasters] = useState<MasterOptions>(emptyMasters)
  const [textLimits, setTextLimits] = useState<RequisitionTextLimit[]>([])
  const [textErrors, setTextErrors] = useState<string[]>([])
  const [advancedOpen, setAdvancedOpen] = useState<string[]>([])
  const [rows, setRows] = useState<RecruitmentRequisition[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [submitDraft, setSubmitDraft] = useState<RecruitmentRequisition | null>(null)
  const [submissionNotice, setSubmissionNotice] = useState<{ type: 'success' | 'error'; text: string } | null>(null)
  const [dialogOpen, setDialogOpen] = useState(initialOpen)
  const [readOnly, setReadOnly] = useState(false)
  const [activeRequest, setActiveRequest] = useState<RecruitmentRequisition | null>(null)
  const [sourcePrefillLoading, setSourcePrefillLoading] = useState(initialOpen)
  const [sourcePrefillWarning, setSourcePrefillWarning] = useState('')
  const [query, setQuery] = useState('')
  const [clientFilter, setClientFilter] = useState(initialClientId)
  const [statusFilter, setStatusFilter] = useState('')
  const [seedPackOpen, setSeedPackOpen] = useState(false)
  const [sourceFile, setSourceFile] = useState<File | null>(null)
  const [sourceParse, setSourceParse] = useState<RecruitmentRequestDocumentParseResult | null>(null)
  const [sourceParsing, setSourceParsing] = useState(false)
  const [sourceUploadProgress, setSourceUploadProgress] = useState(0)
  const [attachmentRefresh, setAttachmentRefresh] = useState(0)
  const [autoSaveState, setAutoSaveState] = useState<AutoSaveState>('idle')
  const [autoSavedAt, setAutoSavedAt] = useState<Date | null>(null)
  const [watchedForm, setWatchedForm] = useState({ id: 0, clientId: 0, isReplacement: false, budgetAvailable: false })
  const [budgetApprovers, setBudgetApprovers] = useState<{ id: number; displayName: string; email: string }[]>([])
  useEffect(() => {
    let active = true
    setBudgetApprovers([])
    if (watchedForm.clientId > 0 && watchedForm.budgetAvailable)
      void getJson<{ id: number; displayName: string; email: string }[]>(`/api/recruitment/budget-approvers?clientId=${watchedForm.clientId}`, []).then(rows => { if (active) setBudgetApprovers(rows) })
    return () => { active = false }
  }, [watchedForm.clientId, watchedForm.budgetAvailable])
  const [embeddedJdRefreshKey, setEmbeddedJdRefreshKey] = useState(0)
  const [atsSkillWeight, setAtsSkillWeight] = useState(0)
  const [targetSlaDays, setTargetSlaDays] = useState<number | null>(null)
  const openedInitialRequisitionId = useRef(0)
  const targetJoiningManual = useRef(false)
  const targetSlaLoad = useRef(0)
  const embeddedJdRef = useRef<RecruitmentJobDescriptionManagerHandle>(null)
  const autoSaveTimer = useRef<number | null>(null)
  const autoSaveRunning = useRef<Promise<void> | null>(null)
  const autoSavePending = useRef(false)
  const activeBrowserDraftKey = useRef('')
  const sourceFileRef = useRef<File | null>(null)
  const selectedClientId = watchedForm.clientId
  const sourceDocumentName = Form.useWatch('sourceDocumentName', form) || ''
  const canSeedHiring = statusScope.length === 0 || statusScope.some(status => editableStatuses.has(status))
  const replacementHiring = watchedForm.isReplacement
  const budgetAvailable = watchedForm.budgetAvailable
  const approvedEdit = activeRequest?.status === 'Approved' && watchedForm.id > 0 && canDelete && !readOnly
  const clientOptions = useMemo(() => clients.map(row => ({ value: Number(row.id), label: row.name })), [clients])

  function newBrowserDraftKey() {
    return `frevo:hiring-request:draft:${session?.user.id || 'anonymous'}:${initialWorkOrderId || 0}:${initialWorkOrderLineId || 0}`
  }

  function persistBrowserDraft(values = form.getFieldsValue(true) as SaveRecruitmentRequisition) {
    if (!activeBrowserDraftKey.current || readOnly || approvedEdit) return
    try {
      const snapshot: BrowserDraftSnapshot = {
        version: 1,
        savedAt: new Date().toISOString(),
        values,
        sourceParse,
        sourceFilePending: Boolean(sourceFileRef.current),
      }
      window.localStorage.setItem(activeBrowserDraftKey.current, JSON.stringify(snapshot))
      setAutoSaveState(current => current === 'saving' ? current : 'local')
    } catch { /* Server autosave remains available if browser storage is full or disabled. */ }
  }

  function restoreBrowserDraft(base: SaveRecruitmentRequisition, key: string) {
    activeBrowserDraftKey.current = key
    try {
      const raw = window.localStorage.getItem(key)
      if (!raw) return base
      const snapshot = JSON.parse(raw) as BrowserDraftSnapshot
      if (snapshot.version !== 1 || !snapshot.values) return base
      setSourceParse(snapshot.sourceParse || null)
      setAutoSavedAt(snapshot.savedAt ? new Date(snapshot.savedAt) : null)
      setAutoSaveState(snapshot.values.id ? 'saved' : 'local')
      if (snapshot.sourceFilePending) setSourcePrefillWarning('Your field values were restored. Re-select the source document if it had not finished attaching before refresh.')
      return { ...base, ...snapshot.values }
    } catch {
      window.localStorage.removeItem(key)
      return base
    }
  }

  function clearBrowserDraft(requestId = 0) {
    if (activeBrowserDraftKey.current) window.localStorage.removeItem(activeBrowserDraftKey.current)
    if (requestId) window.localStorage.removeItem(`frevo:hiring-request:request:${session?.user.id || 'anonymous'}:${requestId}`)
    activeBrowserDraftKey.current = ''
    setAutoSaveState('idle')
    setAutoSavedAt(null)
  }

  function canAutoSave(values: SaveRecruitmentRequisition) {
    return Boolean(values.clientId && values.requestedByEmployeeId && values.requestDate && values.positionTitle?.trim()
      && values.department?.trim() && Number(values.numberOfOpenings) > 0)
  }

  function textRules(field: string) {
    return [{ validator: async (_: unknown, value: unknown) => {
      const rule = textLimits.find(row => row.field === field)
      const error = rule ? requisitionTextError(value, rule) : ''
      if (error) throw new Error(error)
    } }]
  }

  function checkTextLengths(values: SaveRecruitmentRequisition, reveal = false) {
    // No second hardcoded limit table: the API's schema contract drives the form.
    if (!textLimits.length) {
      setTextErrors(['Field limits could not be loaded. Refresh before saving; your draft is retained in this browser.'])
      return false
    }
    const errors = requisitionTextErrors(values, textLimits)
    setTextErrors(errors.flatMap(row => row.errors))
    if (errors.length) {
      form.setFields(errors)
      if (reveal) setAdvancedOpen(['advanced'])
    }
    return errors.length === 0
  }

  function scheduleAutoSave(values?: SaveRecruitmentRequisition, delay = 1200) {
    const current = values || form.getFieldsValue(true) as SaveRecruitmentRequisition
    persistBrowserDraft(current)
    if (autoSaveTimer.current) window.clearTimeout(autoSaveTimer.current)
    if (readOnly || approvedEdit || !dialogOpen) return
    autoSaveTimer.current = window.setTimeout(() => { autoSaveTimer.current = null; void runAutoSave() }, delay)
  }

  async function runAutoSave() {
    if (readOnly || approvedEdit || !dialogOpen || sourceParsing || saving) return
    const values = form.getFieldsValue(true) as SaveRecruitmentRequisition
    persistBrowserDraft(values)
    if (!checkTextLengths(values)) return
    if (!canAutoSave(values)) return
    if (autoSaveRunning.current) { autoSavePending.current = true; return autoSaveRunning.current }
    const task = (async () => {
      setAutoSaveState('saving')
      const originalId = Number(values.id || 0)
      const response = await saveRecruitmentRequisition(normalize(values))
      if (!response.ok || !response.data) { setAutoSaveState('error'); return }
      const saved = response.data
      if (!originalId && !Number(form.getFieldValue('id') || 0)) form.setFieldValue('id', saved.id)
      setActiveRequest(saved)
      setWatchedForm(current => ({ ...current, id: saved.id, clientId: saved.clientId }))
      setRows(current => [saved, ...current.filter(row => row.id !== saved.id)])
      await storeSourceDocument(saved)
      const latest = form.getFieldsValue(true) as SaveRecruitmentRequisition
      persistBrowserDraft({ ...latest, id: saved.id })
      setAutoSavedAt(new Date())
      setAutoSaveState('saved')
    })().catch(() => setAutoSaveState('error')).finally(() => {
      autoSaveRunning.current = null
      if (autoSavePending.current) { autoSavePending.current = false; scheduleAutoSave(undefined, 250) }
    })
    autoSaveRunning.current = task
    return task
  }

  const applyDraft = (draft: SaveRecruitmentRequisition) => {
    const normalized = {
      ...draft,
      id: Number(draft.id || 0),
      clientId: Number(draft.clientId || 0),
      requestedByEmployeeId: Number(draft.requestedByEmployeeId || 0) || null,
    }
    form.setFieldsValue(normalized)
    setTextErrors(requisitionTextErrors(normalized, textLimits).flatMap(row => row.errors))
    setWatchedForm({ id: normalized.id, clientId: normalized.clientId, isReplacement: Boolean(normalized.isReplacement), budgetAvailable: Boolean(normalized.budgetAvailable) })
  }

  useEffect(() => {
    let active = true
    if (initialOpen) {
      applyDraft(blankRequest(initialClientId))
      setDialogOpen(true)
    }
    void getRecruitmentRequisitions({}).then(requestRows => {
      if (!active) return
      setRows(requestRows)
      setLoading(false)
    })
    void fetchWorkspace().then(async data => {
      if (!active) return
      setClients(data.clientRows)
      setDropdowns(data.dropRows)
      setLocations(data.locationRows)
      setEmployees(data.employeeRows)
      setTextLimits(data.fieldLimits)
      setMasters({
        hiringTypes: data.hiringTypes, positionCategories: data.positionCategories,
        experienceRanges: data.experienceRanges, priorities: data.priorities,
      })
      if (initialOpen) {
        const nextClientId = initialClientId || data.clientRows[0]?.id || 0
        let draft = blankRequest(nextClientId)
        if (initialWorkOrderId) {
          const workOrder = await getRecruitmentWorkOrder(initialWorkOrderId)
          if (!active) return
          const line = initialWorkOrderLineId ? workOrder?.lines?.find(row => row.id === initialWorkOrderLineId) : undefined
          if (workOrder && (!initialWorkOrderLineId || line)) {
            draft.clientId = workOrder.clientId
            draft.workOrderId = workOrder.id
            draft.workOrderLineNumber = line?.lineNumber ?? null
            if (line) {
              draft.positionTitle = line.positionName
              draft.numberOfOpenings = line.numberOfPositions
              draft.jobLocation = line.location
              draft.businessUnit = line.division
            }
            draft.sourceType = 'Client Work Order'
            draft.sourceReference = workOrder.workOrderNumber
            draft.sourceAuthority = workOrder.receivedFrom
            draft.sourceNotes = workOrder.remarks
          } else {
            setSourcePrefillWarning(workOrder
              ? 'The selected work-order role is no longer available. A new blank hiring request is open; return to Work Orders and choose an active request.'
              : 'The linked work order could not be loaded. A new blank hiring request is open; return to Work Orders and retry from the active order.')
          }
        }
        draft.requestedByEmployeeId = session?.user.employeeId
          ?? data.employeeRows.find(row => row.isActive && (!draft.clientId || row.clientId === draft.clientId))?.id
          ?? null
        draft = restoreBrowserDraft(draft, newBrowserDraftKey())
        targetJoiningManual.current = Boolean(draft.targetJoiningDate)
        applyDraft(draft)
        void applyPipelineTarget(Number(draft.clientId || 0), draft.requestDate, true)
        setDialogOpen(true)
      }
      setSourcePrefillLoading(false)
      setLoading(false)
    }).catch(() => {
      if (!active) return
      setSourcePrefillLoading(false)
      setSourcePrefillWarning('Hiring-request setup data could not be loaded. The page is still available, but refresh before saving this request.')
      setLoading(false)
    })
    return () => { active = false }
  }, [])

  useEffect(() => () => {
    if (autoSaveTimer.current) window.clearTimeout(autoSaveTimer.current)
  }, [])

  useEffect(() => {
    if (!initialRequisitionId || openedInitialRequisitionId.current === initialRequisitionId || dialogOpen || loading) return
    const requested = rows.find(row => row.id === initialRequisitionId)
    if (requested) {
      openedInitialRequisitionId.current = initialRequisitionId
      openRequest(requested)
    }
  }, [dialogOpen, initialRequisitionId, loading, rows])

  async function refreshRows() {
    setLoading(true)
    setRows(await getRecruitmentRequisitions({}))
    setLoading(false)
  }

  const filteredRows = useMemo(() => {
    const needle = query.trim().toLowerCase()
    return rows.filter(row => {
      if (clientFilter && row.clientId !== clientFilter) return false
      if (statusScope.length && !statusScope.includes(row.status)) return false
      if (statusFilter && row.status !== statusFilter) return false
      if (!needle) return true
      return [row.rfrNumber, row.positionTitle, row.department, row.clientName, row.requestedByName]
        .some(value => String(value || '').toLowerCase().includes(needle))
    })
  }, [rows, clientFilter, statusFilter, query, statusScope])

  const statuses = useMemo(() => unique(rows.map(row => row.status)), [rows])
  const departmentOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Department', selectedClientId),
    ...employees.filter(row => !selectedClientId || row.clientId === selectedClientId).map(row => row.department),
  ])), [dropdowns, employees, selectedClientId])
  const businessUnitOptions = useMemo(() => asOptions(dropValues(dropdowns, 'Business Unit', selectedClientId)), [dropdowns, selectedClientId])
  const costCenterOptions = useMemo(() => asOptions(dropValues(dropdowns, 'Cost Center', selectedClientId)), [dropdowns, selectedClientId])
  const employmentOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Employment Type', selectedClientId), 'Contractual', 'Permanent', 'Contract', 'Intern',
  ])), [dropdowns, selectedClientId])
  const hiringOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Hiring Type', selectedClientId), ...masters.hiringTypes, 'Contractual',
  ])), [dropdowns, masters.hiringTypes, selectedClientId])
  const categoryOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Position Category', selectedClientId), ...masters.positionCategories,
  ])), [dropdowns, masters.positionCategories, selectedClientId])
  const experienceOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Experience Range', selectedClientId), ...masters.experienceRanges,
  ])), [dropdowns, masters.experienceRanges, selectedClientId])
  const priorityOptions = useMemo(() => asOptions(unique([
    ...dropValues(dropdowns, 'Assignment Priority', selectedClientId), ...masters.priorities,
    'Low', 'Normal', 'High', 'Critical',
  ])), [dropdowns, masters.priorities, selectedClientId])
  const workModeOptions = useMemo(() => unique([...dropValues(dropdowns, 'Work Mode', selectedClientId), 'Office', 'Hybrid', 'Remote']), [dropdowns, selectedClientId])
  const currencyOptions = useMemo(() => unique([...dropValues(dropdowns, 'Currency', selectedClientId), 'INR', 'USD', 'EUR', 'GBP']), [dropdowns, selectedClientId])
  const sourceTypeOptions = useMemo(() => unique([...dropValues(dropdowns, 'Recruitment Request Source Type', selectedClientId), 'Formal Approval Letter', 'Hiring Initiation Email', 'Job Description', 'Modified Job Description', 'Client Work Order', 'Other']), [dropdowns, selectedClientId])
  const clientApprovalOptions = useMemo(() => unique([...dropValues(dropdowns, 'Client Approval Status', selectedClientId), 'Approved for Hiring', 'Hiring Initiated', 'JD Received', 'JD Awaited', 'Pending Client Confirmation', 'Not Available']), [dropdowns, selectedClientId])
  const locationOptions = useMemo(() => asOptions(unique(locations
    .filter(row => row.isActive && (!selectedClientId || row.clientId === selectedClientId))
    .flatMap(row => [row.name, [row.city, row.state].filter(Boolean).join(', ')]))), [locations, selectedClientId])
  const replacementOptions = useMemo(() => employees
    .filter(row => row.isActive && (!selectedClientId || row.clientId === selectedClientId))
    .map(row => ({ value: row.id, label: `${row.employeeCode} · ${row.firstName} ${row.lastName}` })), [employees, selectedClientId])
  const positionByRequisition = useMemo(() => new Map(pipelinePositions.map(position => [position.requisitionId, position])), [pipelinePositions])

  const requesterOptions = useMemo(() => employees
    .filter(row => row.isActive && (!selectedClientId || row.clientId === selectedClientId))
    .map(row => ({ value: row.id, label: `${row.employeeCode} - ${row.firstName} ${row.lastName}` })), [employees, selectedClientId])

  const columns: ColumnsType<RecruitmentRequisition> = [
    {
      title: 'Hiring request', key: 'request', fixed: 'left', width: 205,
      render: (_, row) => <div className="rfr-primary-cell"><b>{row.positionTitle || 'Untitled role'}</b><span>{row.rfrNumber || 'Draft request'}</span></div>,
    },
    {
      title: 'Client & team', key: 'client', width: 185,
      render: (_, row) => <div className="rfr-stacked-cell"><b>{row.clientName || '—'}</b><span>{row.department || 'No department'}</span></div>,
    },
    {
      title: 'Hiring plan', key: 'plan', width: 140,
      render: (_, row) => <div className="rfr-stacked-cell"><b>{row.numberOfOpenings} opening{row.numberOfOpenings === 1 ? '' : 's'}</b><span>{[row.hiringType, row.employmentType].filter(Boolean).join(' · ') || 'Not classified'}</span></div>,
    },
    {
      title: 'Current stage', key: 'pipelineStage', width: 260,
      render: (_, row) => {
        const position = positionByRequisition.get(row.id)
        const stageName = position?.pipelineStageName || row.pipelineStageName || (position ? 'Pipeline not started' : requestStageLabel(row.status))
        const stageType = position?.pipelineStageType || row.pipelineStageType || ''
        const canOpen = Boolean((position && onOpenPipeline) || (row.pipelineStageName && onOpenRequestPipeline))
        const openPipeline = () => {
          if (position && onOpenPipeline) onOpenPipeline(position)
          else if (row.pipelineStageName && onOpenRequestPipeline) onOpenRequestPipeline(row)
        }
        return <Tag
          data-testid={`hiring-request-stage-${row.id}`}
          color={position || row.pipelineStageName ? recruitmentStageColor(stageType, stageName) : statusColor(row.status)}
          icon={<BranchesOutlined />}
          role={canOpen ? 'link' : undefined}
          tabIndex={canOpen ? 0 : undefined}
          title={canOpen ? `Open ${stageName} in pipeline` : stageName}
          style={{ cursor: canOpen ? 'pointer' : 'default', marginInlineEnd: 0 }}
          onClick={openPipeline}
          onKeyDown={event => { if (canOpen && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); openPipeline() } }}
        >{stageName}</Tag>
      },
    },
    {
      title: 'Target', key: 'target', width: 145,
      render: (_, row) => <div className="rfr-stacked-cell"><b>{row.targetJoiningDate ? displayDate(row.targetJoiningDate) : 'Not planned'}</b><span>{row.jobLocation || row.workMode || 'Location pending'}</span></div>,
    },
    {
      title: 'Budget', dataIndex: 'budgetAmount', width: 90, align: 'right',
      render: (value: number, row) => row.budgetAvailable ? <b>{money(value, row.currency)}</b> : <span className="rfr-muted">Not tagged</span>,
    },
    {
      title: 'Status', dataIndex: 'status', width: 145,
      render: (value: string, row) => <div className="rfr-stacked-cell"><Tag color={statusColor(value)}>{value || 'Draft'}</Tag>{value === 'Pending Approval' && <span className="rfr-approval-owner">{row.pendingApproverName ? `With ${row.pendingApproverName}` : 'Approver assignment pending'}{row.approvalStageName ? ` · ${row.approvalStageName}` : ''}</span>}</div>,
    },
    {
      title: 'Updated', dataIndex: 'updatedAt', width: 95,
      render: (value: string) => <span className="rfr-muted">{displayDate(value)}</span>,
    },
    {
      title: 'Actions', key: 'actions', fixed: 'right', width: 180,
      render: (_, row) => <Space className="rfr-row-actions" size={4} wrap={false}>
        {editableStatuses.has(row.status) || (canDelete && row.status === 'Approved')
          ? <Tooltip title={row.status === 'Approved' ? 'Edit approved request' : 'Edit draft'}><Button aria-label={row.status === 'Approved' ? 'Edit approved request' : 'Edit draft'} size="small" icon={<EditOutlined />} onClick={() => openRequest(row)} /></Tooltip>
          : <Tooltip title="View request"><Button aria-label="View request" size="small" icon={<EyeOutlined />} onClick={() => openRequest(row, true)} /></Tooltip>}
        {editableStatuses.has(row.status) && <Tooltip title="Submit hiring request"><Button aria-label="Submit" size="small" icon={<SendOutlined />} loading={submitting && submitDraft?.id === row.id} onClick={() => { setSubmissionNotice(null); setSubmitDraft(row) }} data-mail-event="RFR.SUBMIT" data-mail-resource-type="RecruitmentRequisition" data-mail-resource-id={row.id} data-mail-client-id={row.clientId} data-mail-action-label="Submit hiring request" /></Tooltip>}
        {(editableStatuses.has(row.status) || row.status === 'Approved') && onPrepareJobDescription && <Tooltip title={jobDescriptionActionLabel(row.jobDescriptionStatus)}><Button aria-label={jobDescriptionActionLabel(row.jobDescriptionStatus)} data-testid={`prepare-jd-${row.id}`} size="small" icon={<FilePdfOutlined />} onClick={() => onPrepareJobDescription(row)} /></Tooltip>}
        {canDelete && <Popconfirm placement="left" title="Delete this requisition?" description="Delete its open position and job-description versions first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={async () => { const response = await deleteRecruitmentRequisition(row.id); if (response.ok) await refreshRows() }}><Button title="Delete requisition" danger aria-label="Delete requisition" size="small" icon={<DeleteOutlined />} /></Popconfirm>}
      </Space>,
    },
  ]

  async function applyPipelineTarget(clientId: number, requestDate?: string | null, replaceExisting = false) {
    const sequence = ++targetSlaLoad.current
    if (!clientId || !requestDate) { setTargetSlaDays(null); return }
    const days = await publishedCumulativeSlaDays(clientId)
    if (sequence !== targetSlaLoad.current) return
    setTargetSlaDays(days)
    if (!days || targetJoiningManual.current) return
    const current = form.getFieldsValue(true) as SaveRecruitmentRequisition
    if (current.clientId !== clientId || current.requestDate !== requestDate || (!replaceExisting && current.targetJoiningDate)) return
    const targetJoiningDate = addDays(requestDate, days)
    form.setFieldValue('targetJoiningDate', targetJoiningDate)
    scheduleAutoSave({ ...current, targetJoiningDate })
  }

  function openNew() {
    setReadOnly(false)
    setActiveRequest(null)
    setAtsSkillWeight(0)
    setSourcePrefillWarning('')
    setSourceParse(null)
    const nextClientId = initialClientId || clientFilter || clients[0]?.id || 0
    let draft = blankRequest(nextClientId)
    draft.requestedByEmployeeId = session?.user.employeeId
      ?? employees.find(row => row.isActive && (!nextClientId || row.clientId === nextClientId))?.id
      ?? null
    draft = restoreBrowserDraft(draft, newBrowserDraftKey())
    targetJoiningManual.current = Boolean(draft.targetJoiningDate)
    applyDraft(draft)
    void applyPipelineTarget(nextClientId, draft.requestDate, true)
    setSourceFile(null)
    sourceFileRef.current = null
    setSourceUploadProgress(0)
    setDialogOpen(true)
  }

  function openRequest(row: RecruitmentRequisition, forceReadOnly = false) {
    activeBrowserDraftKey.current = `frevo:hiring-request:request:${session?.user.id || 'anonymous'}:${row.id}`
    setAutoSaveState(editableStatuses.has(row.status) ? 'saved' : 'idle')
    setAutoSavedAt(row.updatedAt ? new Date(row.updatedAt) : null)
    setAtsSkillWeight(0)
    setReadOnly(forceReadOnly || (!editableStatuses.has(row.status) && !(canDelete && row.status === 'Approved')))
    setActiveRequest(row)
    targetJoiningManual.current = true
    setTargetSlaDays(null)
    applyDraft(fromRow(row))
    setSourceFile(null)
    sourceFileRef.current = null
    setSourceParse(null)
    setSourceUploadProgress(0)
    setDialogOpen(true)
  }

  async function parseSourceFile(file: File) {
    if (!/\.(pdf|docx|txt)$/i.test(file.name)) return void message.error('Select a PDF, DOCX or text hiring document.')
    if (file.size > 10 * 1024 * 1024) return void message.error('The source document must be 10 MB or smaller.')
    const current = form.getFieldsValue(true) as SaveRecruitmentRequisition
    const sourceDefaults: Partial<SaveRecruitmentRequisition> = {
      sourceDocumentName: file.name,
      sourceType: current.sourceType || 'Job Description',
      externalApprovalStatus: current.externalApprovalStatus || 'JD Received',
      sourceParsedJson: '',
    }
    form.setFieldsValue(sourceDefaults)
    setSourceFile(file)
    sourceFileRef.current = file
    setSourceParse(null)
    setSourceParsing(true)
    setSourceUploadProgress(0)
    try {
      const response = await parseRecruitmentRequestDocument(file, Number(current.clientId || selectedClientId || 0))
      if (!response.ok || !response.data) {
        setSourceParse(manualSourceReview(file, { ...current, ...sourceDefaults } as SaveRecruitmentRequisition, response.error))
        return void message.warning('Automatic prefill was unavailable. The document is retained; complete the visible fields and save normally.')
      }
      const result = response.data
      const currentWithSource = form.getFieldsValue(true) as SaveRecruitmentRequisition
      const suggested = result.draft
      const detected = new Set(result.detectedFields)
      const patch: Partial<SaveRecruitmentRequisition> = {}
      for (const field of result.detectedFields) {
        const key = field as keyof SaveRecruitmentRequisition
        if (['id', 'clientId', 'requestedByEmployeeId', 'requestDate', 'branchId'].includes(field)) continue
        const value = suggested[key]
        if (value !== undefined && value !== null && value !== '') Object.assign(patch, { [key]: value })
      }
      if (detected.has('experienceRange')) patch.experienceRange = matchExperienceMaster(suggested.experienceRange, experienceOptions.map(row => String(row.value))) || undefined
      if (detected.has('positionCategory')) patch.positionCategory = matchMaster(suggested.positionCategory, categoryOptions.map(row => String(row.value))) || undefined
      if (detected.has('hiringType')) patch.hiringType = matchMaster(suggested.hiringType, hiringOptions.map(row => String(row.value))) || currentWithSource.hiringType
      if (detected.has('employmentType')) patch.employmentType = matchMaster(suggested.employmentType, employmentOptions.map(row => String(row.value))) || currentWithSource.employmentType
      if (detected.has('hiringPriority')) patch.hiringPriority = matchMaster(suggested.hiringPriority, priorityOptions.map(row => String(row.value))) || currentWithSource.hiringPriority
      // An oversized master suggestion must remain editable, not disappear during matching.
      for (const error of requisitionTextErrors(suggested, textLimits)) {
        const key = error.name as keyof SaveRecruitmentRequisition
        if (detected.has(error.name)) Object.assign(patch, { [key]: suggested[key] })
      }
      if (detected.has('targetJoiningDate')) targetJoiningManual.current = true
      const next = { ...currentWithSource, ...patch, sourceDocumentName: file.name, sourceParsedJson: suggested.sourceParsedJson }
      form.setFieldsValue(next)
      setWatchedForm({ id: next.id || 0, clientId: next.clientId || 0, isReplacement: Boolean(next.isReplacement), budgetAvailable: Boolean(next.budgetAvailable) })
      setSourceParse({
        ...result,
        reviewFields: result.reviewFields.filter(field => !(field === 'Client' && next.clientId) && !(field === 'Requested by' && next.requestedByEmployeeId)),
      })
      scheduleAutoSave(next, 250)
      const detectedCount = result.detectedFields.filter(field => field !== 'sourceParsedJson').length
      if (result.status === 'Parsed') message.success(`${detectedCount} fields prefilled. Review and save the draft.`)
      else message.warning(`${detectedCount} fields were suggested. Review the document and complete the remaining fields manually.`)
    } catch (cause) {
      const error = cause instanceof Error ? cause.message : 'The document could not be read automatically.'
      setSourceParse(manualSourceReview(file, { ...current, ...sourceDefaults } as SaveRecruitmentRequisition, error))
      message.warning('Automatic prefill was unavailable. The document is retained; complete the visible fields and save normally.')
    } finally {
      setSourceParsing(false)
      scheduleAutoSave(form.getFieldsValue(true) as SaveRecruitmentRequisition, 250)
    }
  }

  async function storeSourceDocument(row: RecruitmentRequisition) {
    const pendingFile = sourceFileRef.current
    if (!pendingFile) return true
    const configurations = await getEffectiveAttachmentConfigurations(row.clientId, 'RECRUITMENT', 'HIRING_REQUEST')
    const configuration = configurations.find(item => item.attributeCode === 'RECRUITMENT_REQUEST_SOURCE')
    if (!configuration) {
      message.error('Hiring-request source attachment configuration is unavailable. The draft was saved, but the source file was not attached.')
      return false
    }
    const response = await uploadEntityAttachment(configuration.id, 'RECRUITMENT_REQUISITION', row.id, pendingFile, {}, setSourceUploadProgress)
    if (!response.ok) {
      message.error(response.error || 'The hiring request was saved, but its source document could not be attached.')
      return false
    }
    if (sourceFileRef.current === pendingFile) {
      sourceFileRef.current = null
      setSourceFile(null)
    }
    setSourceUploadProgress(0)
    setAttachmentRefresh(value => value + 1)
    return true
  }

  async function saveRequest(submitAfterSave: boolean) {
    if (autoSaveTimer.current) { window.clearTimeout(autoSaveTimer.current); autoSaveTimer.current = null }
    if (autoSaveRunning.current) await autoSaveRunning.current
    if (!checkTextLengths(form.getFieldsValue(true) as SaveRecruitmentRequisition, true)) {
      message.error('Review the field-length messages before saving. Your text has not been shortened.')
      return
    }
    let values: SaveRecruitmentRequisition
    try {
      values = await form.validateFields()
    } catch (validationError) {
      const firstField = (validationError as { errorFields?: Array<{ name?: Array<string | number> }> }).errorFields?.[0]?.name
      message.error('Correct the highlighted fields before saving the hiring request.')
      if (firstField?.length) form.scrollToField(firstField, { block: 'center' })
      return
    }
    if (watchedForm.id > 0 && embeddedJdRef.current && !await embeddedJdRef.current.saveDraftIfNeeded()) return
    setSaving(true)
    try {
      const saved = await saveRecruitmentRequisition(normalize(values))
      if (!saved.ok || !saved.data) return void message.error(saved.error || 'Hiring request could not be saved.')
      let completed = saved.data
      if (!await storeSourceDocument(saved.data)) {
        setActiveRequest(saved.data)
        applyDraft(fromRow(saved.data))
        setEmbeddedJdRefreshKey(value => value + 1)
        await refreshRows()
        return
      }
      if (submitAfterSave) {
        const submitted = await submitRecruitmentRequisition(saved.data.id)
        if (!submitted.ok || !submitted.data) return void message.error(submitted.error || 'Hiring request could not be submitted.')
        completed = submitted.data
        clearBrowserDraft(saved.data.id)
        message.success(submissionSuccess(completed))
      } else message.success(activeRequest?.status === 'Approved' ? 'Approved hiring request, linked vacancy and JD updated.' : 'Hiring request and JD draft saved.')
      if (submitAfterSave) setDialogOpen(false)
      else {
        setActiveRequest(completed)
        applyDraft(fromRow(completed))
        setEmbeddedJdRefreshKey(value => value + 1)
      }
      onChanged?.(completed)
      await refreshRows()
    } finally {
      setSaving(false)
    }
  }

  async function submitSelectedRequest() {
    if (!submitDraft) return
    setSubmitting(true)
    try {
      const response = await submitRecruitmentRequisition(submitDraft.id)
      if (!response.ok || !response.data) {
        const text = response.error || 'Hiring request could not be submitted.'
        setSubmissionNotice({ type: 'error', text })
        message.error(text)
        return
      }
      const successText = submissionSuccess(response.data)
      setSubmissionNotice({ type: 'success', text: successText })
      message.success(successText)
      setSubmitDraft(null)
      onChanged?.(response.data)
      await refreshRows()
    } finally {
      setSubmitting(false)
    }
  }

  const advancedFields = <div className="rfr-advanced-grid">
    <Form.Item name="businessUnit" rules={textRules('businessUnit')} label="Business unit"><AutoComplete options={businessUnitOptions} placeholder="Select or enter business unit" /></Form.Item>
    <Form.Item name="costCenter" rules={textRules('costCenter')} label="Cost center"><AutoComplete options={costCenterOptions} placeholder="Select or enter cost center" /></Form.Item>
    <Form.Item name="workMode" rules={textRules('workMode')} label="Work mode"><RecruitmentMasterSelect masterType="Work Mode" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={workModeOptions} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} testId="rfr-work-mode" /></Form.Item>
    <Form.Item name="project" rules={textRules('project')} label="Project"><Input placeholder="Project or contract" /></Form.Item>
    <Form.Item name="experienceRange" rules={textRules('experienceRange')} label="Experience"><RecruitmentMasterSelect masterType="Experience Range" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={experienceOptions.map(row => String(row.value))} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} allowClear testId="rfr-experience-range" /></Form.Item>
    <Form.Item name="qualification" rules={textRules('qualification')} label="Qualification"><Input placeholder="Minimum qualification" /></Form.Item>
    <Form.Item name="requiredSkills" rules={textRules('requiredSkills')} label="Required skills" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Comma-separated must-have skills" /></Form.Item>
    <Form.Item name="preferredSkills" rules={textRules('preferredSkills')} label="Preferred skills" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Good-to-have skills" /></Form.Item>
    <Form.Item name="certifications" rules={textRules('certifications')} label="Certifications"><Input placeholder="Add certifications" /></Form.Item>
    <Form.Item name="languages" rules={textRules('languages')} label="Languages"><Input placeholder="Add languages" /></Form.Item>
    <Form.Item name="salaryMin" label="Salary range"><InputNumber min={0} controls={false} addonBefore="Min" style={{ width: '100%' }} /></Form.Item>
    <Form.Item name="salaryMax" label=" "><InputNumber min={0} controls={false} addonBefore="Max" style={{ width: '100%' }} /></Form.Item>
    <Form.Item name="currency" rules={textRules('currency')} label="Currency"><RecruitmentMasterSelect masterType="Currency" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={currencyOptions} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} testId="rfr-currency" /></Form.Item>
    <Form.Item name="benefits" rules={textRules('benefits')} label="Benefits"><Input placeholder="Benefits summary" /></Form.Item>
    <Form.Item name="businessJustification" rules={textRules('businessJustification')} label="Business justification" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Why this position is needed" /></Form.Item>
    <Form.Item name="reasonForHiring" rules={textRules('reasonForHiring')} label="Hiring notes" className="rfr-span-2"><Input.TextArea rows={2} placeholder="Context for approvers" /></Form.Item>
    <Form.Item name="externalPositionCode" rules={textRules('externalPositionCode')} label="Client position code" extra="Use the exact code stated by the client. If approved, this becomes the open-position code."><Input placeholder="For example, UIDAI_BTC_0_25" /></Form.Item>
    <Form.Item name="sourceType" rules={textRules('sourceType')} label="Source type"><RecruitmentMasterSelect masterType="Recruitment Request Source Type" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={sourceTypeOptions} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} allowClear testId="rfr-source-type" /></Form.Item>
    <Form.Item name="sourceReference" rules={textRules('sourceReference')} label="Source reference"><Input placeholder="File number, computer number or email subject" /></Form.Item>
    <Form.Item name="sourceDocumentDate" label="Source document date"><Input type="date" /></Form.Item>
    <Form.Item name="sourceDocumentName" rules={textRules('sourceDocumentName')} label="Source document" className="rfr-span-2"><Input placeholder="Original PDF file name(s)" /></Form.Item>
    <Form.Item name="sourceAuthority" rules={textRules('sourceAuthority')} label="Source authority"><Input placeholder="Requesting / approving authority" /></Form.Item>
    <Form.Item name="externalApprovalStatus" rules={textRules('externalApprovalStatus')} label="Client approval state"><RecruitmentMasterSelect masterType="Client Approval Status" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={clientApprovalOptions} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} allowClear testId="rfr-client-approval-status" /></Form.Item>
    <Form.Item name="ctcFlexibilityPercent" label="Salary negotiation (%)" extra="Optional: 20–30% only. An approved 30% case receives one +5 day SLA extension."><InputNumber min={20} max={30} precision={2} style={{ width: '100%' }} /></Form.Item>
    <Form.Item name="sourceNotes" rules={textRules('sourceNotes')} label="Source notes" className="rfr-span-2"><Input.TextArea rows={3} placeholder="Preserve ambiguities and missing facts without inventing values" /></Form.Item>
  </div>

  return <section className="rfr-manager" data-testid="requisition-manager">
    {!embedded && <header className="rfr-header">
      <div><span>Recruitment</span><h2>Hiring Requests</h2><p>Raise, review and submit workforce demand without leaving the request register.</p></div>
      <Space wrap>
        <Button icon={<DownloadOutlined />} onClick={downloadHiringSeedTemplate}>Seed template</Button>
        <Button icon={<UploadOutlined />} onClick={() => setSeedPackOpen(true)}>Import seed pack</Button>
        <Button type="primary" icon={<PlusOutlined />} onClick={openNew} data-testid="new-hiring-request">New hiring request</Button>
      </Space>
    </header>}

    <div className={`rfr-toolbar${clientScopeManaged ? ' is-client-scoped' : ''}${!showStatusFilter && clientScopeManaged ? ' is-status-scoped' : ''}`}>
      <Input allowClear prefix={<SearchOutlined />} value={query} onChange={event => setQuery(event.target.value)} placeholder="Search role, RFR or requester" />
      {!clientScopeManaged && <Select allowClear value={clientFilter || undefined} onChange={value => setClientFilter(value || 0)} placeholder="All clients" showSearch optionFilterProp="label"
        options={clients.map(row => ({ value: row.id, label: row.name }))} />}
      {showStatusFilter && <Select allowClear value={statusFilter || undefined} onChange={value => setStatusFilter(value || '')} placeholder="All statuses" options={asOptions(statuses)} />}
      <Space className="rfr-toolbar-actions" wrap={false}>
        {canSeedHiring && <Button data-testid="hiring-seed-template" icon={<DownloadOutlined />} onClick={downloadHiringSeedTemplate}>Template</Button>}
        {canSeedHiring && <Button data-testid="hiring-seed-import" icon={<UploadOutlined />} onClick={() => setSeedPackOpen(true)}>Import</Button>}
        <Tooltip title="Refresh register"><Button aria-label="Refresh register" icon={<ReloadOutlined />} onClick={() => void refreshRows()} /></Tooltip>
      </Space>
    </div>

    {submissionNotice && <Alert data-testid="requisition-submission-notice" type={submissionNotice.type} showIcon closable message={submissionNotice.text} onClose={() => setSubmissionNotice(null)} />}

    <div className="ant-smart-table rfr-register-table-shell">
      <Table rowKey="id" className="zoho-ant-table rfr-register-table" loading={loading} columns={columns} dataSource={filteredRows} size="middle" tableLayout="fixed"
        pagination={{ pageSize: 10, showSizeChanger: true, showTotal: total => `${total} requests` }}
        scroll={{ x: 1445 }} locale={{ emptyText: 'No hiring requests match the selected filters.' }} />
    </div>

    <RecruitmentEditorDrawer className="rfr-dialog" open={dialogOpen} width="min(1120px, 96vw)" destroyOnClose={false}
      onClose={() => { if (!saving) { persistBrowserDraft(); setDialogOpen(false) } }} kicker="Hiring request"
      title={readOnly ? 'Request details' : approvedEdit ? 'Edit approved request' : watchedForm.id ? 'Edit draft' : 'New hiring request'}
      description={readOnly ? activeRequest?.status === 'Pending Approval' ? 'Review the submitted demand and its current approval ownership.' : 'Review the approved demand and its hiring context.' : 'Capture the essential demand first; advanced role and approval context is available below.'}
      extra={!readOnly && watchedForm.id > 0 ? <Tooltip title="Weights are automatically redistributed so the cumulative ATS skill weight never exceeds 100%."><div className="rfr-ats-weight-indicator"><Progress type="circle" size={52} percent={Math.min(100, Math.max(0, atsSkillWeight))} status={Math.abs(atsSkillWeight - 100) < 0.01 ? 'success' : 'normal'} format={() => `${Math.round(atsSkillWeight)}%`} /><span>Cumulative<br />skill weight</span></div></Tooltip> : undefined}
      footer={<div className="rfr-dialog-actions">
        {!readOnly && !approvedEdit && <span className={`rfr-autosave-status is-${autoSaveState}`} data-testid="hiring-request-autosave-status">{autoSaveLabel(autoSaveState, autoSavedAt)}</span>}
        <div className="rfr-dialog-action-buttons">
        <Button onClick={() => { persistBrowserDraft(); setDialogOpen(false) }}>Close</Button>
        {!readOnly && (approvedEdit
          ? <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={() => void saveRequest(false)} data-testid="update-approved-requisition">Update approved request</Button>
          : <Button type="primary" icon={<SendOutlined />} loading={saving || autoSaveState === 'saving'} onClick={() => void saveRequest(true)} data-testid="save-submit-requisition" data-mail-event="RFR.SUBMIT" data-mail-resource-type="RecruitmentRequisition" data-mail-client-id={watchedForm.clientId} data-mail-action-label="Save and submit hiring request">Save & submit</Button>)}
        </div>
      </div>}>
      {readOnly && activeRequest?.status === 'Pending Approval' && <Alert
        className="rfr-approval-banner"
        type="info"
        showIcon
        message={`Pending at ${activeRequest.pendingApproverName || 'approver assignment'}`}
        description={activeRequest.approvalStageName ? `Current approval stage: ${activeRequest.approvalStageName}` : 'The configured workflow is resolving the current approval stage.'}
      />}
      {sourcePrefillWarning && <Alert className="rfr-source-warning" type="warning" showIcon message="Source record needs attention" description={sourcePrefillWarning} />}
      <Spin spinning={sourcePrefillLoading} tip="Loading the linked work order...">
      {!readOnly && <section className="rfr-source-parser" data-testid="hiring-request-source-parser">
        <div className="rfr-source-parser-copy"><FilePdfOutlined /><div><b>Prefill from hiring request / JD</b><span>{sourceDocumentName ? `${sourceDocumentName} is linked to this draft. Choose another file only to replace it.` : 'Choose one PDF, DOCX or TXT. Suggestions stay editable and nothing is submitted automatically.'}</span></div></div>
        <label className={`rfr-source-picker${sourceParsing ? ' is-busy' : ''}`}>
          <input data-testid="hiring-request-source-file" type="file" accept=".pdf,.docx,.txt" disabled={sourceParsing || saving} onChange={event => { const file = event.currentTarget.files?.[0]; event.currentTarget.value = ''; if (file) void parseSourceFile(file) }} />
          {sourceParsing ? <Spin size="small" /> : <UploadOutlined />}
          <span>{sourceParsing ? 'Reading document...' : sourceFile?.name || (sourceDocumentName ? 'Replace source document' : 'Choose source document')}</span>
        </label>
        {sourceParse && <div className="rfr-source-result" data-testid="hiring-request-source-result">
          <Tag color={sourceParse.status === 'Parsed' ? 'green' : 'orange'} icon={sourceParse.status === 'Parsed' ? <CheckCircleOutlined /> : undefined}>{sourceParse.status}</Tag>
          <span>{sourceParse.detectedFields.filter(field => field !== 'sourceParsedJson').length} fields suggested</span>
          {sourceParse.reviewFields.length > 0 && <span>Complete manually: {sourceParse.reviewFields.join(', ')}</span>}
          {sourceParse.warnings.map(warning => <small key={warning}>{warning}</small>)}
        </div>}
        {sourceUploadProgress > 0 && <small>Securing source document: {sourceUploadProgress}%</small>}
      </section>}
      {textErrors.length > 0 && <Alert type="warning" showIcon data-testid="requisition-text-errors" message="Review field lengths before saving" description={<div>{textErrors.map(error => <div key={error}>{error}</div>)}<Button type="link" onClick={() => setAdvancedOpen(['advanced'])}>Show fields to correct</Button></div>} />}
      <Form form={form} layout="vertical" disabled={readOnly || sourcePrefillLoading} requiredMark className="rfr-form" onValuesChange={(changed: Partial<SaveRecruitmentRequisition>, values: SaveRecruitmentRequisition) => {
        checkTextLengths(values)
        setWatchedForm({ id: values.id || 0, clientId: values.clientId || 0, isReplacement: Boolean(values.isReplacement), budgetAvailable: Boolean(values.budgetAvailable) })
        if (Object.prototype.hasOwnProperty.call(changed, 'targetJoiningDate')) targetJoiningManual.current = true
        if (Object.prototype.hasOwnProperty.call(changed, 'clientId') || Object.prototype.hasOwnProperty.call(changed, 'requestDate')) {
          targetJoiningManual.current = false
          form.setFieldValue('targetJoiningDate', null)
          void applyPipelineTarget(Number(values.clientId || 0), values.requestDate, true)
        }
        scheduleAutoSave(values)
      }}>
        <section className="rfr-form-section">
          <header className="rfr-form-section-head"><div><b>Request essentials</b><span>Hiring ownership, demand and target details</span></div><Tag color={watchedForm.id ? 'blue' : 'purple'}>{watchedForm.id ? 'Existing request' : 'New request'}</Tag></header>
          <div className="rfr-essential-grid">
          <Form.Item name="id" hidden><InputNumber /></Form.Item>
          <Form.Item name="branchId" hidden><InputNumber /></Form.Item>
          <Form.Item className="rfr-span-2" name="clientId" label="Client" rules={[{ required: true, message: 'Select the hiring client.' }]}>
            <Select showSearch optionFilterProp="label" optionLabelProp="label" placeholder={clients.length ? 'Select client' : 'Loading client...'} loading={!clients.length} disabled={clientScopeManaged && initialClientId > 0} options={clientOptions}
              onChange={value => {
                const linkedRequester = session?.user.employeeId
                const belongs = employees.some(row => row.id === linkedRequester && row.clientId === value && row.isActive)
                form.setFieldValue('requestedByEmployeeId', belongs ? linkedRequester : undefined)
              }} />
          </Form.Item>
          <Form.Item className="rfr-span-2" name="requestedByEmployeeId" label="Requested by" rules={[{ required: true, message: 'Select the requester employee.' }]}>
            <Select showSearch optionFilterProp="label" placeholder="Select active employee" disabled={approvedEdit} options={requesterOptions} />
          </Form.Item>
          <Form.Item name="requestDate" label="Request date" rules={[{ required: true, message: 'Enter the hiring request date.' }]}><Input type="date" /></Form.Item>
          <Form.Item name="positionTitle" label="Role / position" rules={[...textRules('positionTitle'), { required: true, whitespace: true, message: 'Enter the position title.' }]}>
            <Input placeholder="For example, Senior .NET Engineer" />
          </Form.Item>
          <Form.Item name="department" label="Department" rules={[...textRules('department'), { required: true, whitespace: true, message: 'Enter the department.' }]}>
            <AutoComplete options={departmentOptions} placeholder="Select or enter department" />
          </Form.Item>
          <Form.Item name="numberOfOpenings" label="Openings" rules={[{ required: true, type: 'number', min: 1, message: 'At least one opening is required.' }]}>
            <InputNumber min={1} max={999} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="hiringType" rules={textRules('hiringType')} label="Hiring type"><RecruitmentMasterSelect masterType="Hiring Type" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={hiringOptions.map(row => String(row.value))} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} allowClear testId="rfr-hiring-type" /></Form.Item>
          <Form.Item name="employmentType" rules={textRules('employmentType')} label="Employment type"><RecruitmentMasterSelect masterType="Employment Type" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={employmentOptions.map(row => String(row.value))} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} allowClear testId="rfr-employment-type" /></Form.Item>
          <Form.Item name="positionCategory" rules={textRules('positionCategory')} label="Position category"><RecruitmentMasterSelect masterType="Position Category" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={categoryOptions.map(row => String(row.value))} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} allowClear testId="rfr-position-category" /></Form.Item>
          <Form.Item name="hiringPriority" rules={textRules('hiringPriority')} label="Priority"><RecruitmentMasterSelect masterType="Assignment Priority" clientId={selectedClientId} clientName={clients.find(row => row.id === selectedClientId)?.name} values={priorityOptions.map(row => String(row.value))} dropdowns={dropdowns} onDropdownsChange={setDropdowns} canAdd={canManageMasters} testId="rfr-priority" /></Form.Item>
          <Form.Item name="jobLocation" rules={textRules('jobLocation')} label="Work location"><AutoComplete options={locationOptions} placeholder="Office, city or remote" /></Form.Item>
          <Form.Item name="targetJoiningDate" label="Target joining" extra={targetSlaDays ? `Auto-calculated from request date using the published ${targetSlaDays}-day cumulative pipeline SLA.` : undefined}><Input type="date" min={today()} /></Form.Item>
        </div>

        <div className="rfr-switch-row">
          <Form.Item name="isReplacement" label="Replacement hiring" valuePropName="checked"><Switch /></Form.Item>
          <Form.Item name="budgetAvailable" label="Budget approval required" valuePropName="checked"><Switch /></Form.Item>
        </div>
        {(replacementHiring || budgetAvailable) && <div className="rfr-context-grid">
          {replacementHiring && <Form.Item name="replacementEmployeeId" label="Employee being replaced" rules={[{ required: true, message: 'Select the employee being replaced.' }]}>
            <Select showSearch optionFilterProp="label" options={replacementOptions} placeholder="Search employee" />
          </Form.Item>}
          {budgetAvailable && <Form.Item name="budgetAmount" label="Annual hiring budget" extra="Enter the total annual budget for all openings; no master setup is needed. Salary min/max is the per-person CTC range." rules={[{ required: true, type: 'number', min: 0.01, message: 'Enter a budget greater than zero.' }]}>
            <InputNumber min={0.01} precision={2} controls={false} style={{ width: '100%' }} placeholder="Enter budget amount" />
          </Form.Item>}
          {budgetAvailable && <Form.Item name="budgetApproverUserId" label="Budget approver" extra="Saving a new or changed budget sends an approval task to this user. Offers remain blocked until approved." rules={[{ required: true, message: 'Select the budget approver.' }]}>
            <Select showSearch optionFilterProp="label" placeholder="Select approver" options={budgetApprovers.map(user => ({ value: user.id, label: `${user.displayName} · ${user.email}` }))} />
          </Form.Item>}
        </div>}
        </section>

        <Form.Item name="sourceParsedJson" hidden><Input /></Form.Item>
        <Collapse ghost className="rfr-advanced" activeKey={advancedOpen} onChange={keys => setAdvancedOpen(Array.isArray(keys) ? keys : [keys])}>
          <Collapse.Panel key="advanced" forceRender header="Advanced role, skills and approval context">{advancedFields}</Collapse.Panel>
        </Collapse>

      </Form>
      {!readOnly && <Collapse ghost className="rfr-advanced rfr-jd-workspace">
        <Collapse.Panel key="job-description" forceRender header="Job description & ATS screening">
          {watchedForm.id > 0
            ? <RecruitmentJobDescriptionManager
                ref={embeddedJdRef}
                key={`embedded-jd-${watchedForm.id}-${embeddedJdRefreshKey}`}
                embedded
                initialClientId={watchedForm.clientId}
                clientScopeManaged
                initialRequisitionId={watchedForm.id}
                onWeightChange={setAtsSkillWeight}
              />
            : <Alert type="info" showIcon message="Save the hiring request once to prepare its JD" description="The parsed role, skills and ATS suggestions will carry forward automatically. Saving does not submit the request." />}
        </Collapse.Panel>
      </Collapse>}
      {watchedForm.id > 0 && <EntityAttachmentPanel key={`${watchedForm.id}-${attachmentRefresh}`} entityType="RECRUITMENT_REQUISITION" entityId={watchedForm.id} clientId={watchedForm.clientId} moduleCode="RECRUITMENT" formCodes={['HIRING_REQUEST']} title="Original hiring request / JD" description="Saved source document. Preview or download it here; replacement is handled from the prefill control above." readOnly />}
      </Spin>
    </RecruitmentEditorDrawer>

    <Modal
      open={!!submitDraft}
      title="Submit this hiring request?"
      okText="Submit request"
      cancelText="Keep as draft"
      confirmLoading={submitting}
      closable={!submitting}
      maskClosable={!submitting}
      onCancel={() => !submitting && setSubmitDraft(null)}
      onOk={() => void submitSelectedRequest()}
    >
      <p><strong>{submitDraft?.rfrNumber}</strong> will move to the configured approval workflow.</p>
      <p>The pending approver and approval stage will be shown in the request status after submission.</p>
      {submissionNotice?.type === 'error' && <Alert type="error" showIcon message={submissionNotice.text} />}
    </Modal>

    <RecruitmentSeedPackModal open={seedPackOpen} mode="hiring" onClose={() => setSeedPackOpen(false)} onImported={refreshRows} />
  </section>
}

function jobDescriptionActionLabel(status?: string) {
  const value = String(status || 'Not Started').toLowerCase()
  if (value === 'draft' || value === 'sent back') return 'Open draft JD'
  if (value === 'pending approval') return 'View pending JD'
  if (value === 'approved') return 'View approved JD'
  if (value === 'published' || value === 'retired') return 'View JD'
  return 'Prepare JD'
}

function submissionSuccess(row: RecruitmentRequisition) {
  if (row.status === 'Approved') return 'Hiring request approved automatically; vacancy created.'
  const owner = row.pendingApproverName ? ` Pending with ${row.pendingApproverName}.` : ''
  const stage = row.approvalStageName ? ` Stage: ${row.approvalStageName}.` : ''
  return `Hiring request submitted for approval.${owner}${stage}`
}

function autoSaveLabel(state: AutoSaveState, savedAt: Date | null) {
  if (state === 'saving') return 'Saving draft…'
  if (state === 'error') return 'Saved in this browser · server retry pending'
  if (state === 'saved') return `Draft saved automatically${savedAt ? ` · ${savedAt.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}` : ''}`
  if (state === 'local') return 'Draft saved in this browser'
  return 'Changes save automatically'
}

function blankRequest(clientId: number): SaveRecruitmentRequisition {
  return {
    id: 0, requestDate: today(), requestedByEmployeeId: null, clientId: clientId || undefined, workOrderId: null, workOrderLineNumber: null, branchId: 0, businessUnit: '', department: '', costCenter: '', positionTitle: '',
    positionCategory: '', employmentType: 'Contractual', hiringType: 'Contractual', numberOfOpenings: 1, isReplacement: false,
    replacementEmployeeId: null, targetJoiningDate: null, jobLocation: '', workMode: 'Office', project: '', budgetAvailable: false,
    budgetAmount: null, hiringPriority: 'High', businessJustification: '', reasonForHiring: '', experienceRange: '', qualification: '',
    requiredSkills: '', preferredSkills: '', certifications: '', languages: 'English', salaryMin: 0, salaryMax: 0, currency: 'INR', benefits: 'As per company norms',
    externalPositionCode: '', sourceType: '', sourceReference: '', sourceDocumentName: '', sourceDocumentDate: null,
    sourceAuthority: '', externalApprovalStatus: '', ctcFlexibilityPercent: null, sourceNotes: '', sourceParsedJson: '',
  }
}

async function publishedCumulativeSlaDays(clientId: number) {
  try {
    const definitions = (await getRecruitmentPipelines(clientId)).filter(row => row.isActive)
    const groups = await Promise.all(definitions.map(async definition => ({ definition, versions: await getRecruitmentPipelineVersions(definition.id) })))
    const minutes = groups.flatMap(({ definition, versions }) => versions
      .filter(version => version.status === 'Published'
        && version.slaMode === 'CumulativeFromAnchor'
        && ['Position', 'Hybrid'].includes(version.scopeType ?? 'Application')
        && (!definition.currentPublishedVersionId || definition.currentPublishedVersionId === version.id))
      .map(version => Number(version.overallSlaMinutes || 0)))
      .filter(value => value > 0)
    return minutes.length ? Math.ceil(Math.max(...minutes) / 1440) : null
  } catch { return null }
}

function addDays(value: string, days: number) {
  const [year, month, day] = value.slice(0, 10).split('-').map(Number)
  if (!year || !month || !day) return null
  const date = new Date(Date.UTC(year, month - 1, day))
  date.setUTCDate(date.getUTCDate() + days)
  return date.toISOString().slice(0, 10)
}

async function fetchWorkspace() {
  const [clientRows, dropRows, locationRows, employeeRows, hiringTypes, positionCategories, experienceRanges, priorities, fieldLimits] = await Promise.all([
    getClients(), getDropdowns(), getWorkLocations(), getEmployees(),
    getRecruitmentMasterOptions('Hiring Type'), getRecruitmentMasterOptions('Position Category'),
    getRecruitmentMasterOptions('Experience Range'), getRecruitmentMasterOptions('Assignment Priority'),
    getJson<RequisitionTextLimit[]>('/api/recruitment/requisitions/field-limits', []),
  ])
  return { clientRows, dropRows, locationRows, employeeRows, hiringTypes, positionCategories, experienceRanges, priorities, fieldLimits }
}

function fromRow(row: RecruitmentRequisition): SaveRecruitmentRequisition {
  return {
    id: row.id, requestDate: row.requestDate?.slice(0, 10) || today(), requestedByEmployeeId: row.requestedByEmployeeId, clientId: row.clientId, workOrderId: row.workOrderId ?? null, workOrderLineNumber: row.workOrderLineNumber ?? null, branchId: row.branchId || 0, businessUnit: row.businessUnit || '', department: row.department || '',
    costCenter: row.costCenter || '', positionTitle: row.positionTitle || '', positionCategory: row.positionCategory || '',
    employmentType: row.employmentType || '', hiringType: row.hiringType || '', numberOfOpenings: row.numberOfOpenings || 1,
    isReplacement: row.isReplacement, replacementEmployeeId: row.replacementEmployeeId ?? null,
    targetJoiningDate: row.targetJoiningDate?.slice(0, 10) || null, jobLocation: row.jobLocation || '', workMode: row.workMode || 'Office',
    project: row.project || '', budgetAvailable: row.budgetAvailable, budgetAmount: Number(row.budgetAmount || 0),
    budgetApproverUserId: row.budgetApproverUserId, budgetApprovalStatus: row.budgetApprovalStatus,
    hiringPriority: row.hiringPriority || 'Normal', businessJustification: row.businessJustification || '', reasonForHiring: row.reasonForHiring || '',
    experienceRange: row.experienceRange || '', qualification: row.qualification || '', requiredSkills: row.requiredSkills || '',
    preferredSkills: row.preferredSkills || '', certifications: row.certifications || '', languages: row.languages || '',
    salaryMin: Number(row.salaryMin || 0), salaryMax: Number(row.salaryMax || 0), currency: row.currency || 'INR', benefits: row.benefits || '',
    externalPositionCode: row.externalPositionCode || '', sourceType: row.sourceType || '', sourceReference: row.sourceReference || '',
    sourceDocumentName: row.sourceDocumentName || '', sourceDocumentDate: row.sourceDocumentDate?.slice(0, 10) || null,
    sourceAuthority: row.sourceAuthority || '', externalApprovalStatus: row.externalApprovalStatus || '',
    ctcFlexibilityPercent: row.ctcFlexibilityPercent == null ? null : Number(row.ctcFlexibilityPercent), sourceNotes: row.sourceNotes || '', sourceParsedJson: row.sourceParsedJson || '',
  }
}

function normalize(row: SaveRecruitmentRequisition): SaveRecruitmentRequisition {
  const clean = (value?: string | null) => String(value || '').trim()
  return {
    ...row, id: Number(row.id || 0), requestedByEmployeeId: Number(row.requestedByEmployeeId || 0) || null, clientId: Number(row.clientId || 0) || undefined, workOrderId: Number(row.workOrderId || 0) || null, workOrderLineNumber: Number(row.workOrderLineNumber || 0) || null, branchId: Number(row.branchId || 0),
    positionTitle: clean(row.positionTitle), department: clean(row.department), businessUnit: clean(row.businessUnit), costCenter: clean(row.costCenter),
    positionCategory: clean(row.positionCategory), employmentType: clean(row.employmentType), hiringType: clean(row.hiringType),
    numberOfOpenings: Number(row.numberOfOpenings || 1), replacementEmployeeId: row.isReplacement ? Number(row.replacementEmployeeId || 0) || null : null,
    targetJoiningDate: row.targetJoiningDate || null, jobLocation: clean(row.jobLocation), workMode: clean(row.workMode) || 'Office', project: clean(row.project),
    budgetAmount: row.budgetAvailable ? Number(row.budgetAmount || 0) : 0, hiringPriority: clean(row.hiringPriority) || 'High',
    businessJustification: clean(row.businessJustification), reasonForHiring: clean(row.reasonForHiring), experienceRange: clean(row.experienceRange),
    qualification: clean(row.qualification), requiredSkills: clean(row.requiredSkills), preferredSkills: clean(row.preferredSkills),
    certifications: clean(row.certifications), languages: clean(row.languages), salaryMin: Number(row.salaryMin || 0), salaryMax: Number(row.salaryMax || 0),
    currency: clean(row.currency) || 'INR', benefits: clean(row.benefits),
    sourceParsedJson: clean(row.sourceParsedJson),
  }
}

function matchMaster(value: string, options: string[]) {
  const needle = String(value || '').trim().toLowerCase()
  return options.find(option => option.trim().toLowerCase() === needle)
    || options.find(option => option.toLowerCase().includes(needle) || needle.includes(option.toLowerCase()))
    || ''
}

function matchExperienceMaster(value: string, options: string[]) {
  const exact = matchMaster(value, options)
  if (exact) return exact
  const raw = String(value || '').trim()
  const years = Number(raw.match(/\d+/)?.[0] || 0)
  if (!years) return raw
  return options.find(option => {
    const numbers = (option.match(/\d+/g) || []).map(Number)
    return numbers.length >= 2 ? years >= numbers[0] && years <= numbers[1] : numbers.length === 1 && years >= numbers[0]
  }) || raw
}

function dropValues(rows: Drop[], type: string, clientId: number) {
  return rows.filter(row => row.isActive && row.type.toLowerCase() === type.toLowerCase() && (!row.clientId || !clientId || row.clientId === clientId)).map(row => row.value)
}
function unique(values: (string | undefined | null)[]) { return [...new Set(values.map(value => String(value || '').trim()).filter(Boolean))] }
function asOptions(values: string[]) { return values.map(value => ({ value, label: value })) }
function displayDate(value: string) { const date = new Date(value); return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) }
function money(value: number, currency = 'INR') { try { return new Intl.NumberFormat('en-IN', { style: 'currency', currency, maximumFractionDigits: 0 }).format(Number(value || 0)) } catch { return `${currency} ${Number(value || 0).toLocaleString('en-IN')}` } }
function today() { const date = new Date(); date.setMinutes(date.getMinutes() - date.getTimezoneOffset()); return date.toISOString().slice(0, 10) }
function statusColor(status: string) {
  if (status === 'Approved') return 'green'
  if (status === 'Pending Approval') return 'blue'
  if (status === 'Rejected') return 'red'
  if (status === 'Sent Back') return 'orange'
  if (status === 'Withdrawn') return 'default'
  return 'gold'
}

function requestStageLabel(status: string) {
  if (status === 'Pending Approval') return 'Approval pending'
  if (status === 'Sent Back') return 'Request sent back'
  if (status === 'Approved') return 'Vacancy pending'
  if (status === 'Rejected') return 'Request rejected'
  if (status === 'Withdrawn') return 'Request withdrawn'
  return 'Request draft'
}

function manualSourceReview(file: File, draft: SaveRecruitmentRequisition, error: string): RecruitmentRequestDocumentParseResult {
  return {
    status: 'NeedsReview',
    parserName: 'Manual fallback',
    parserVersion: '1.0',
    originalFileName: file.name,
    draft,
    detectedFields: ['sourceType', 'sourceDocumentName', 'externalApprovalStatus'],
    reviewFields: ['Role / position', 'Department', 'Openings'],
    warnings: [error || 'Readable text was not found.', 'The original document will be secured with the hiring request when you save. Continue manually; nothing was submitted automatically.'],
  }
}
