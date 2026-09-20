import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { currentRecruitmentHiringCases } from '../services/recruitmentJobVersions'
import { Alert, Button, Card, Divider, Drawer, Empty, Form, Input, Modal, Popconfirm, Select, Space, Statistic, Tag, Timeline, Tooltip, message } from 'antd'
import { ArrowLeftOutlined, ArrowRightOutlined, ClockCircleOutlined, DeleteOutlined, FileProtectOutlined, HistoryOutlined, PauseCircleOutlined, PlayCircleOutlined, PlusOutlined, TeamOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { useAuthSession } from './AuthGate'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { getClients } from '../services/payrollService'
import { getRecruitmentPipelineVersions, getRecruitmentPipelines } from '../services/recruitmentOrchestrationService'
import { advanceRecruitmentHiringCase, approveRecruitmentProfileBatch, createRecruitmentProfileBatch, deleteRecruitmentHiringCase, deleteRecruitmentWorkOrder, forwardRecruitmentProfileBatch, generateRecruitmentProcessDocument, getRecruitmentHiringCase, getRecruitmentHiringCases, getRecruitmentHiringCaseTransitions, getRecruitmentProcessDocumentSignatures, getRecruitmentProcessDocuments, getRecruitmentProfileBatches, getRecruitmentWorkOrder, getRecruitmentWorkOrders, pauseRecruitmentHiringCase, resumeRecruitmentHiringCase, saveRecruitmentProcessDocument, saveRecruitmentProcessDocumentSignature, saveRecruitmentWorkOrder, startRecruitmentHiringCase } from '../services/recruitmentCaseService'
import { getApplications } from '../services/recruitmentTalentService'
import type { Client, RecruitmentCandidateApplication } from '../types/payroll'
import type { RecruitmentPipelineTransition } from '../types/recruitmentOrchestration'
import type { RecruitmentHiringCase, RecruitmentProcessDocument, RecruitmentProcessDocumentSignature, RecruitmentProfileSubmissionBatch, RecruitmentWorkOrder, SaveRecruitmentWorkOrder } from '../types/recruitmentCases'
import type { RecruitmentPipelineDisplayMode } from '../types/recruitmentPipelineView'
import { hiringTransitionLabel, isDivisionRejectionOutcome } from '../utils/recruitmentTransitions'
import DataTable from './DataTable'
import './RecruitmentWorkOrderWorkspace.css'

type WorkOrderDraft = SaveRecruitmentWorkOrder
type PublishedPipelineSla = { id: number; pipelineName: string; versionNumber: number; slaMode: string; overallSlaMinutes: number }

const blankWorkOrder = (clientId = 0): WorkOrderDraft => ({ id: 0, clientId, workOrderNumber: '', receivedAtUtc: new Date().toISOString().slice(0, 16), receivedFrom: '', subject: '', remarks: '', status: 'Active', overallSlaMinutes: 0, lines: [] })
const dateTimeText = (value?: string | null) => value ? new Date(value).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' }) : 'Not set'
const durationText = (minutes?: number | null) => minutes == null ? 'No target' : minutes === 0 ? 'Day 0' : `${(minutes / 1440).toFixed(minutes % 1440 ? 1 : 0)} days`
const formatStageDuration = (seconds = 0) => { const total = Math.max(0, Math.floor(seconds)); const days = Math.floor(total / 86400); const hours = Math.floor((total % 86400) / 3600); const minutes = Math.floor((total % 3600) / 60); const remainingSeconds = total % 60; return `${days ? `${days}d ` : ''}${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(remainingSeconds).padStart(2, '0')}` }
const remainingDuration = (dueAt: string | null | undefined, now: number) => {
  if (!dueAt) return 'No overall due date'
  const seconds = Math.round((new Date(dueAt).getTime() - now) / 1000)
  return seconds < 0 ? `${formatStageDuration(Math.abs(seconds))} overdue` : `${formatStageDuration(seconds)} left`
}
const statusColor = (status: string) => status === 'Completed' ? 'green' : status === 'Active' ? 'blue' : status === 'On Hold' ? 'orange' : ['Cancelled', 'Rejected'].includes(status) ? 'red' : 'default'

export default function RecruitmentWorkOrderWorkspace({ initialClientId = 0, clientScopeManaged = false, displayMode = 'pipeline', postInterview = false }: { initialClientId?: number; clientScopeManaged?: boolean; displayMode?: RecruitmentPipelineDisplayMode; postInterview?: boolean }) {
  const session = useAuthSession()
  const navigate = useNavigate()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [query, setQuery] = useState('')
  const [workOrders, setWorkOrders] = useState<RecruitmentWorkOrder[]>([])
  const [cases, setCases] = useState<RecruitmentHiringCase[]>([])
  const [draft, setDraft] = useState<WorkOrderDraft>(() => blankWorkOrder(initialClientId))
  const [editorOpen, setEditorOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [pipelineSlaLoading, setPipelineSlaLoading] = useState(false)
  const [publishedPipelineSlas, setPublishedPipelineSlas] = useState<PublishedPipelineSla[]>([])
  const [selectedWorkOrder, setSelectedWorkOrder] = useState<RecruitmentWorkOrder | null>(null)
  const [selectedCase, setSelectedCase] = useState<RecruitmentHiringCase | null>(null)
  const [caseSyncedAt, setCaseSyncedAt] = useState(Date.now())
  const [clockNow, setClockNow] = useState(Date.now())
  const [stageLogOpen, setStageLogOpen] = useState(false)
  const [processDocuments, setProcessDocuments] = useState<RecruitmentProcessDocument[]>([])
  const [documentSaving, setDocumentSaving] = useState(false)
  const [signatureDocument, setSignatureDocument] = useState<RecruitmentProcessDocument | null>(null)
  const [documentSignatures, setDocumentSignatures] = useState<RecruitmentProcessDocumentSignature[]>([])
  const [signatureMethod, setSignatureMethod] = useState<'Typed' | 'Drawn' | 'Image'>('Typed')
  const [signerName, setSignerName] = useState(session?.user.displayName || '')
  const [signatureDataUrl, setSignatureDataUrl] = useState('')
  const [signatureSaving, setSignatureSaving] = useState(false)
  const [candidateApplications, setCandidateApplications] = useState<RecruitmentCandidateApplication[]>([])
  const [profileBatches, setProfileBatches] = useState<RecruitmentProfileSubmissionBatch[]>([])
  const [selectedApplicationIds, setSelectedApplicationIds] = useState<number[]>([])
  const [batchSaving, setBatchSaving] = useState(false)
  const [caseActionBusy, setCaseActionBusy] = useState(false)
  const [caseActionError, setCaseActionError] = useState('')
  const [caseActionDialog, setCaseActionDialog] = useState<'pause' | 'advance' | null>(null)
  const [caseActionReason, setCaseActionReason] = useState('')
  const [caseDialogError, setCaseDialogError] = useState('')
  const [caseTransitions, setCaseTransitions] = useState<RecruitmentPipelineTransition[]>([])
  const [selectedCaseOutcome, setSelectedCaseOutcome] = useState('ADVANCE')
  const deepLinkOpened = useRef('')
  const queryParams = new URLSearchParams(typeof window === 'undefined' ? '' : window.location.search)
  const requestedWorkOrderId = Number(queryParams.get('workOrderId') || 0)
  const requestedHiringCaseId = Number(queryParams.get('hiringCaseId') || 0)
  const requestedStageLog = queryParams.get('stageLog') === '1'

  const load = async () => {
    const [orders, hiringCases] = await Promise.all([getRecruitmentWorkOrders(clientId, query), getRecruitmentHiringCases(clientId, true)])
    setWorkOrders(orders); setCases(hiringCases)
  }
  useEffect(() => { void getClients().then(setClients) }, [])
  useEffect(() => { setClientId(initialClientId) }, [initialClientId])
  useEffect(() => { void load() }, [clientId])
  useEffect(() => {
    if (!editorOpen || !draft.clientId) {
      setPipelineSlaLoading(false)
      setPublishedPipelineSlas([])
      return
    }
    let cancelled = false
    setPipelineSlaLoading(true)
    void (async () => {
      try {
        const definitions = (await getRecruitmentPipelines(draft.clientId)).filter(definition => definition.isActive)
        const groups = await Promise.all(definitions.map(async definition => ({ definition, versions: await getRecruitmentPipelineVersions(definition.id) })))
        const rows = groups.flatMap(({ definition, versions }) => versions
          .filter(version => version.status === 'Published'
            && ['Position', 'Hybrid'].includes(version.scopeType ?? 'Application')
            && (!definition.currentPublishedVersionId || definition.currentPublishedVersionId === version.id))
          .map(version => ({ id: version.id, pipelineName: definition.pipelineName, versionNumber: version.versionNumber, slaMode: version.slaMode ?? 'StageEntry', overallSlaMinutes: version.overallSlaMinutes ?? 0 })))
        if (!cancelled) setPublishedPipelineSlas(rows)
      } catch {
        if (!cancelled) setPublishedPipelineSlas([])
      } finally {
        if (!cancelled) setPipelineSlaLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [draft.clientId, editorOpen])
  useEffect(() => {
    const timer = window.setInterval(() => setClockNow(Date.now()), 1_000)
    return () => window.clearInterval(timer)
  }, [])

  const currentCases = useMemo(() => currentRecruitmentHiringCases(cases), [cases])
  const historicalCases = useMemo(() => cases.filter(row => !currentCases.some(current => current.id === row.id)), [cases, currentCases])
  const historicalSelection = Boolean(selectedCase && historicalCases.some(row => row.id === selectedCase.id))
  const stats = useMemo(() => ({
    activeOrders: workOrders.filter(row => row.status === 'Active').length,
    positions: workOrders.reduce((total, row) => total + row.lineCount, 0),
    activeCases: currentCases.filter(row => row.status === 'Active').length,
    breached: currentCases.filter(row => row.status === 'Active' && row.overallDueAtUtc && new Date(row.overallDueAtUtc).getTime() < Date.now()).length,
  }), [currentCases, workOrders])

  const openNew = () => { setDraft(blankWorkOrder(clientId)); setEditorOpen(true) }
  const openEdit = async (row: RecruitmentWorkOrder) => {
    const detail = await getRecruitmentWorkOrder(row.id)
    if (!detail) return message.error('Unable to load this work order.')
    setDraft({ ...detail, receivedAtUtc: new Date(detail.receivedAtUtc).toISOString().slice(0, 16), overallSlaMinutes: 0, lines: detail.lines.map(line => ({ ...line })) })
    setEditorOpen(true)
  }
  const patchDraft = (patch: Partial<WorkOrderDraft>) => setDraft(current => ({ ...current, ...patch }))

  const save = async () => {
    if (!draft.clientId) return message.warning('Select the client that issued this work order.')
    if (!draft.workOrderNumber.trim() || !draft.receivedAtUtc) return message.warning('Work order number and received time are required.')
    setSaving(true)
    const clientName = clients.find(row => row.id === draft.clientId)?.name || draft.receivedFrom
    const response = await saveRecruitmentWorkOrder({ ...draft, receivedFrom: clientName, subject: '', receivedAtUtc: new Date(draft.receivedAtUtc).toISOString(), overallSlaMinutes: 0 })
    setSaving(false)
    if (!response.ok || !response.data) return
    if (response.data.slaLaunchMessage) {
      if (/started/i.test(response.data.slaLaunchMessage)) message.success(response.data.slaLaunchMessage)
      else message.warning(response.data.slaLaunchMessage)
    }
    setEditorOpen(false); await load(); setSelectedWorkOrder(response.data)
  }

  const viewWorkOrder = async (row: RecruitmentWorkOrder) => setSelectedWorkOrder(await getRecruitmentWorkOrder(row.id))
  const addHiringRequest = (workOrder: RecruitmentWorkOrder) => {
    setSelectedWorkOrder(null)
    navigate(`/recruitment/requisitions?new=1&clientId=${workOrder.clientId}&workOrderId=${workOrder.id}`)
  }
  const removeWorkOrder = async (row: RecruitmentWorkOrder) => {
    const response = await deleteRecruitmentWorkOrder(row.id)
    if (!response.ok) return
    if (selectedWorkOrder?.id === row.id) setSelectedWorkOrder(null)
    await load()
  }
  const viewCase = async (row: RecruitmentHiringCase) => {
    setCaseActionError('')
    const [detail, documents, batches, transitions] = await Promise.all([getRecruitmentHiringCase(row.id), getRecruitmentProcessDocuments(row.id), getRecruitmentProfileBatches(row.id), getRecruitmentHiringCaseTransitions(row.id)])
    const applications = detail?.positionId ? await getApplications({ positionId: detail.positionId }) : []
    if (!detail) setCaseActionError('This hiring journey could not be loaded. Refresh the page and try again.')
    setSelectedCase(detail); setCaseSyncedAt(Date.now()); setProcessDocuments(documents); setProfileBatches(batches); setCandidateApplications(applications); setSelectedApplicationIds([]); setCaseTransitions(transitions); setSelectedCaseOutcome(transitions[0]?.outcomeCode ?? 'ADVANCE')
  }
  useEffect(() => {
    const key = requestedHiringCaseId ? `case-${requestedHiringCaseId}-${requestedStageLog}` : requestedWorkOrderId ? `order-${requestedWorkOrderId}` : ''
    if (!key || deepLinkOpened.current === key) return
    if (requestedHiringCaseId) {
      const row = cases.find(item => item.id === requestedHiringCaseId)
      if (!row) return
      deepLinkOpened.current = key
      setStageLogOpen(requestedStageLog)
      void viewCase(row)
      return
    }
    const row = workOrders.find(item => item.id === requestedWorkOrderId)
    if (!row) return
    deepLinkOpened.current = key
    void viewWorkOrder(row)
  }, [cases, requestedHiringCaseId, requestedStageLog, requestedWorkOrderId, workOrders])
  const removeHiringCase = async (row: RecruitmentHiringCase) => {
    const response = await deleteRecruitmentHiringCase(row.id)
    if (!response.ok) return
    if (selectedCase?.id === row.id) setSelectedCase(null)
    await load()
  }

  const prepareStart = async (workOrder: RecruitmentWorkOrder, line: RecruitmentWorkOrder['lines'][number]) => {
    if (!line.requisitionId) {
      navigate(`/recruitment/requisitions?new=1&clientId=${workOrder.clientId}&workOrderId=${workOrder.id}&workOrderLineId=${line.id}`)
      return
    }
    const definitions = await getRecruitmentPipelines(workOrder.clientId)
    const versionGroups = await Promise.all(definitions.map(async definition => ({ definition, versions: await getRecruitmentPipelineVersions(definition.id) })))
    const options = versionGroups.flatMap(group => group.versions
      .filter(version => version.status === 'Published' && ['Position', 'Hybrid'].includes(version.scopeType ?? 'Application') && (!group.definition.currentPublishedVersionId || group.definition.currentPublishedVersionId === version.id))
      .map(version => ({ ...version, label: `${group.definition.pipelineName} · v${version.versionNumber} · ${version.slaMode === 'CumulativeFromAnchor' ? 'Cumulative SLA' : 'Stage SLA'}` })))
    if (!options.length) return message.warning('Publish a Position or Hybrid pipeline for this client first.')
    if (options.length > 1) return message.warning('Keep one current Position or Hybrid pipeline for automatic SLA start.')
    const response = await startRecruitmentHiringCase(line.id, options[0].id)
    if (!response.ok || !response.data) return
    setSelectedCase(response.data); setCaseSyncedAt(Date.now()); setProcessDocuments([]); setSelectedWorkOrder(null); await load()
  }

  const prepareProcessDocument = async (documentType: string, templateId?: number | null) => {
    if (!selectedCase || !activeStage) return
    setDocumentSaving(true)
    const response = await saveRecruitmentProcessDocument({ id: 0, clientId: selectedCase.clientId, hiringCaseId: selectedCase.id, applicationId: null, interviewId: null, pipelineStageId: activeStage.pipelineStageId, documentType, templateId: templateId || null, attachmentPublicId: null, status: 'Draft', workflowInstanceId: null })
    setDocumentSaving(false)
    if (response.ok && response.data) setProcessDocuments(await getRecruitmentProcessDocuments(selectedCase.id))
  }

  const markProcessDocumentSigned = async (document: RecruitmentProcessDocument) => {
    const response = await saveRecruitmentProcessDocument({ ...document, status: 'Signed' })
    if (response.ok && selectedCase) setProcessDocuments(await getRecruitmentProcessDocuments(selectedCase.id))
  }

  const openSignature = async (document: RecruitmentProcessDocument) => {
    setSignatureDocument(document)
    setSignatureMethod('Typed')
    setSignerName(session?.user.displayName || '')
    setSignatureDataUrl('')
    setDocumentSignatures(await getRecruitmentProcessDocumentSignatures(document.id))
  }

  const captureSignature = async () => {
    if (!signatureDocument) return
    if (signerName.trim().length < 2) return void message.error("Enter the signer's full name.")
    if (signatureMethod !== 'Typed' && !signatureDataUrl) return void message.error('Draw or upload the signature first.')
    setSignatureSaving(true)
    const response = await saveRecruitmentProcessDocumentSignature(signatureDocument.id, {
      signatureMethod,
      signerName: signerName.trim(),
      signatureDataUrl: signatureMethod === 'Typed' ? signerName.trim() : signatureDataUrl,
    })
    setSignatureSaving(false)
    if (!response.ok || !response.data) return
    setDocumentSignatures(await getRecruitmentProcessDocumentSignatures(signatureDocument.id))
    if (selectedCase) {
      const refreshed = await getRecruitmentProcessDocuments(selectedCase.id)
      setProcessDocuments(refreshed)
      setSignatureDocument(refreshed.find(row => row.id === signatureDocument.id) || signatureDocument)
    }
  }

  const uploadSignatureImage = (file?: File | null) => {
    if (!file) return
    if (!/^image\/(png|jpeg)$/i.test(file.type) || file.size > 750 * 1024) return void message.error('Use a PNG/JPG signature image up to 750 KB.')
    const reader = new FileReader()
    reader.onload = () => setSignatureDataUrl(String(reader.result || ''))
    reader.readAsDataURL(file)
  }

  const generateProcessDocument = async (document: RecruitmentProcessDocument) => {
    setDocumentSaving(true)
    const response = await generateRecruitmentProcessDocument(document.id)
    setDocumentSaving(false)
    if (response.ok && selectedCase) setProcessDocuments(await getRecruitmentProcessDocuments(selectedCase.id))
  }

  const pause = () => {
    if (!selectedCase || pauseBlockedReason) return setCaseActionError(pauseBlockedReason || 'This SLA cannot be paused right now.')
    setCaseActionError('')
    setCaseActionReason('')
    setCaseDialogError('')
    setCaseActionDialog('pause')
  }
  const submitPause = async () => {
    if (!selectedCase) return
    if (caseActionReason.trim().length < 3) return setCaseDialogError('Enter a clear reason using at least 3 characters.')
    setCaseDialogError(''); setCaseActionBusy(true)
    const response = await pauseRecruitmentHiringCase(selectedCase.id, caseActionReason.trim())
    setCaseActionBusy(false)
    if (!response.ok || !response.data) {
      const error = response.error || 'The SLA could not be paused.'
      setCaseDialogError(error); setCaseActionError(error); return
    }
    setCaseActionDialog(null); setSelectedCase(response.data); setCaseSyncedAt(Date.now()); await load()
  }
  const resume = async () => {
    if (!selectedCase || !activeStage?.isPaused) return setCaseActionError('This stage SLA is not paused.')
    setCaseActionError(''); setCaseActionBusy(true)
    const response = await resumeRecruitmentHiringCase(selectedCase.id)
    setCaseActionBusy(false)
    if (!response.ok || !response.data) return setCaseActionError(response.error || 'The SLA could not be resumed.')
    setSelectedCase(response.data); setCaseSyncedAt(Date.now()); await load()
  }
  const advance = () => {
    if (!selectedCase || moveBlockedReason) return setCaseActionError(moveBlockedReason || 'This hiring journey cannot move right now.')
    setCaseActionError('')
    setCaseActionReason('')
    setCaseDialogError('')
    setSelectedCaseOutcome(caseTransitions[0]?.outcomeCode ?? 'ADVANCE')
    setCaseActionDialog('advance')
  }
  const submitAdvance = async () => {
    if (!selectedCase) return
    if (caseTransitions.find(row => row.outcomeCode === selectedCaseOutcome)?.requiresReason && caseActionReason.trim().length < 3) return setCaseDialogError('Enter a clear reason using at least 3 characters.')
    setCaseDialogError(''); setCaseActionBusy(true)
    const response = await advanceRecruitmentHiringCase(selectedCase.id, caseActionReason.trim(), selectedCaseOutcome)
    setCaseActionBusy(false)
    if (!response.ok || !response.data) {
      const error = response.error || 'The stage could not be moved.'
      setCaseDialogError(error); setCaseActionError(error); return
    }
    const transitions = await getRecruitmentHiringCaseTransitions(response.data.id)
    setCaseActionDialog(null); setSelectedCase(response.data); setCaseTransitions(transitions); setSelectedCaseOutcome(transitions[0]?.outcomeCode ?? 'ADVANCE'); setCaseSyncedAt(Date.now()); await load()
    if (response.data.advanceStatus === 'Pending Approval') message.info(response.data.advanceMessage || 'Stage movement is pending in My Tasks.')
    else message.success(response.data.advanceMessage || 'Hiring case moved in the pipeline.')
  }

  const reloadProfileBatches = async () => {
    if (selectedCase) setProfileBatches(await getRecruitmentProfileBatches(selectedCase.id))
  }
  const createProfileBatch = async () => {
    if (!selectedCase || !selectedApplicationIds.length) return message.warning('Select at least one candidate application.')
    setBatchSaving(true)
    const response = await createRecruitmentProfileBatch(selectedCase.id, selectedApplicationIds)
    setBatchSaving(false)
    if (response.ok) { setSelectedApplicationIds([]); await reloadProfileBatches() }
  }
  const approveProfileBatch = async (id: number) => {
    setBatchSaving(true)
    const response = await approveRecruitmentProfileBatch(id)
    setBatchSaving(false)
    if (response.ok) await reloadProfileBatches()
  }
  const forwardProfileBatch = async (id: number) => {
    setBatchSaving(true)
    const response = await forwardRecruitmentProfileBatch(id)
    setBatchSaving(false)
    if (response.ok) await reloadProfileBatches()
  }

  const activeStage = selectedCase?.stages.find(stage => stage.status === 'Active')
  const selectedCaseTransition = caseTransitions.find(row => row.outcomeCode === selectedCaseOutcome)
  const selectedCaseMovingBack = selectedCaseOutcome.startsWith('MOVE_BACK_TO_')
  const liveStageSeconds = (stage: RecruitmentHiringCase['stages'][number]) => stage.activeDurationSeconds + (stage.status === 'Active' && !stage.isPaused ? Math.max(0, Math.floor((clockNow - caseSyncedAt) / 1000)) : 0)
  const activeStageSeconds = activeStage ? liveStageSeconds(activeStage) : 0
  const stageTimelineItems = selectedCase?.stages.map(stage => {
    const rejectedByDivision = isDivisionRejectionOutcome(stage.outcomeCode)
    return {
      color: rejectedByDivision ? 'red' : stage.isSlaBreached ? 'orange' : stage.status === 'Completed' ? 'green' : stage.status === 'Active' ? 'blue' : 'gray',
      children: <article className={stage.status === 'Active' ? 'active' : ''} data-testid={`hiring-stage-log-${stage.id}`}><div><Tag>{stage.stakeholderCode || 'No stakeholder'}</Tag><b>{stage.stageName}</b><span>{durationText(stage.targetOffsetMinutes)} target</span></div><p>{stage.status}{stage.outcomeCode ? ` · ${stage.outcomeCode}` : ''} · <b>{formatStageDuration(liveStageSeconds(stage))}</b> active time · due {dateTimeText(stage.dueAtUtc)}{stage.isPaused ? ' · SLA paused' : ''}</p>{rejectedByDivision && <Tag color="red">Rejected by Division</Tag>}{stage.isSlaBreached && <Tag color="orange">Business SLA breach</Tag>}{stage.pauseHistory.map(pauseRow => <small key={pauseRow.id}>Paused by {pauseRow.pausedByName} on {dateTimeText(pauseRow.pausedAtUtc)} — {pauseRow.reason}{pauseRow.resumedAtUtc ? ` · resumed ${dateTimeText(pauseRow.resumedAtUtc)}` : ' · still paused'}</small>)}</article>,
    }
  }) ?? []
  const caseEventItems = (selectedCase?.events ?? []).map(event => ({
    color: event.eventType === 'RejectedByDivision' ? 'red' : event.eventType === 'ProfilesReworkStarted' ? 'orange' : 'blue',
    children: <div><b>{event.eventTitle}</b><p>{event.eventDetails || 'No additional note.'}</p><small>{dateTimeText(event.createdAtUtc)} · {event.actorName || 'System'}</small></div>,
  })) ?? []
  const lastCohortRework = Math.max(0, ...(selectedCase?.events ?? []).filter(event => event.eventType === 'ProfilesReworkStarted').map(event => new Date(event.createdAtUtc).getTime()))
  const currentCohortDocument = (row: RecruitmentProcessDocument) => !['MOM', 'SIGNED_MOM'].includes(row.documentType) || !lastCohortRework || new Date(row.createdAtUtc).getTime() >= lastCohortRework
  const missingRequiredDocuments = activeStage?.processDocumentRequirements.filter(requirement => {
    if (!requirement.isRequired) return false
    const document = processDocuments.find(row => row.pipelineStageId === activeStage.pipelineStageId && row.documentType === requirement.documentType && currentCohortDocument(row))
    if (!document) return true
    return Boolean(requirement.requiresSignature && (document.status !== 'Signed' || (!document.hasFinalSignedAttachment && !document.capturedSignaturesComplete)))
  }) ?? []
  const pauseBlockedReason = historicalSelection ? 'This earlier journey is retained for history.' : !activeStage
    ? 'There is no active stage to pause.'
    : activeStage.isPaused
      ? 'This stage SLA is already paused.'
      : activeStage.allowPause === false
        ? 'SLA pause is disabled in this stage configuration.'
        : ''
  const moveBlockedReason = historicalSelection ? 'Open the currently linked journey to take action; this earlier journey is retained for history.' : selectedCase?.status !== 'Active'
    ? 'Only an active hiring journey can move to another stage.'
    : !activeStage
      ? 'There is no active stage to move.'
      : activeStage.isPaused
        ? 'Resume the SLA before moving this hiring journey.'
        : selectedCase.advanceStatus === 'Pending Approval'
          ? selectedCase.advanceMessage || 'This stage movement is already awaiting approval in My Tasks.'
          : missingRequiredDocuments.length
            ? `Complete the required stage document${missingRequiredDocuments.length === 1 ? '' : 's'} first: ${missingRequiredDocuments.map(row => row.documentType.replaceAll('_', ' ')).join(', ')}.`
            : ''
  return <section className="work-order-workspace" data-testid="recruitment-work-orders">
    {postInterview && <Card title="MoM signing & hiring-stage documents" extra={<Button onClick={() => void load()}>Refresh</Button>}>
      <Alert showIcon type="info" message="Open a job → Prepare MoM → Sign MoM → Finalize signed" description="Reuses the existing audited committee signatures and stage approvals. Signing is available when its configured stage is reached; candidate progress is preserved while vacancies are being filled. Negotiation is in the adjacent tab." />
      <DataTable rows={currentCases} getRowId={row => row.id} emptyText="No current hiring journeys." columns={[
        { key: 'positionName', label: 'Job' }, { key: 'workOrderNumber', label: 'Work order' },
        { key: 'currentStageName', label: 'Hiring stage' },
        { key: 'actions', label: 'Action', render: row => <Button type="primary" onClick={() => void viewCase(row)}>Open MoM / stage documents</Button> },
      ]} />
    </Card>}
    {!postInterview && <><div className="work-order-command-bar">
      <div><span>Client hiring demand</span><h2>Work orders</h2><p>Record the client order once, then create its role-wise Hiring Requests. SLA targets come automatically from the published pipeline.</p></div>
      <Space wrap>{!clientScopeManaged && <Select allowClear value={clientId || undefined} placeholder="All accessible clients" showSearch optionFilterProp="label" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={value => setClientId(value || 0)} />}<Input.Search value={query} placeholder="Work order or subject" onChange={event => setQuery(event.target.value)} onSearch={() => void load()} /><Button data-testid="work-order-add" type="primary" icon={<PlusOutlined />} onClick={openNew}>Add work order</Button></Space>
    </div>
    <div className="work-order-metrics">
      <Card><Statistic title="Active orders" value={stats.activeOrders} prefix={<FileProtectOutlined />} /></Card>
      <Card><Statistic title="Linked hiring requests" value={stats.positions} prefix={<TeamOutlined />} /></Card>
      <Card><Statistic title="Active journeys" value={stats.activeCases} prefix={<PlayCircleOutlined />} /></Card>
      <Card className={stats.breached ? 'risk' : ''}><Statistic title="Overdue journeys" value={stats.breached} prefix={<ClockCircleOutlined />} /></Card>
    </div>
    {displayMode !== 'table' && <div className="work-order-columns" data-testid="work-orders-pipeline-view">
      <Card title="Work orders" extra={<Tag>{workOrders.length} records</Tag>}>
        {!workOrders.length ? <Empty description="No work order has been entered for this client." /> : <div className="work-order-list">{workOrders.map(row => <article key={row.id}>
          <button type="button" onClick={() => void viewWorkOrder(row)}><div><span>{row.clientName}</span><h3>{row.workOrderNumber}</h3><p>{row.subject || 'No subject entered'}</p></div><Tag color={statusColor(row.status)}>{row.status}</Tag></button>
          <footer><span>{dateTimeText(row.receivedAtUtc)}</span><b>{row.lineCount} hiring request{row.lineCount === 1 ? '' : 's'}</b><span>Pipeline SLA applies on journey start</span><Button size="small" onClick={() => void openEdit(row)}>Edit</Button>{canDelete && <Popconfirm title="Delete this work order?" description="Delete its live cumulative pipeline cases first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeWorkOrder(row)}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</footer>
        </article>)}</div>}
      </Card>
      <Card title="Position-wise hiring progress" extra={<Tag color="purple">Stages & SLA</Tag>}>
        {!currentCases.length ? <Empty description="Open an order and start a published hiring pipeline for one role." /> : <div className="hiring-case-list">{currentCases.map(row => {
          const overdue = row.status === 'Active' && row.overallDueAtUtc && new Date(row.overallDueAtUtc).getTime() < Date.now()
          return <button type="button" key={row.id} className={overdue ? 'overdue' : ''} onClick={() => void viewCase(row)}><div><Tag color={statusColor(row.status)}>{row.status}</Tag><span>{row.currentStakeholderCode || 'Unassigned stakeholder'}</span></div><h3>{row.positionName}</h3><p>{row.workOrderNumber} · {row.pipelineName}</p><footer><span>{row.currentStageName || 'Completed'}</span><b><ClockCircleOutlined /> {remainingDuration(row.overallDueAtUtc, clockNow)}</b></footer></button>
        })}</div>}
      </Card>
    </div>}
    {displayMode !== 'pipeline' && <div className="work-order-table-stack" data-testid="work-orders-table-view">
      <Card size="small" title="Work orders"><DataTable
        rows={workOrders}
        getRowId={row => row.id}
        exportFileName="recruitment-work-orders"
        emptyText="No work order has been entered for this client."
        columns={[
          { key: 'workOrderNumber', label: 'Work order', width: '190px', render: row => <div className="pipeline-table-candidate"><strong>{row.workOrderNumber}</strong><small>{row.subject || 'No subject entered'}</small></div> },
          { key: 'clientName', label: 'Client', width: '180px' },
          { key: 'receivedAtUtc', label: 'Received', width: '170px', render: row => dateTimeText(row.receivedAtUtc) },
          { key: 'lineCount', label: 'Hiring requests', width: '130px' },
          { key: 'status', label: 'Status', width: '110px', render: row => <Tag color={statusColor(row.status)}>{row.status}</Tag> },
          { key: 'actions', label: 'Actions', width: '240px', sortable: false, filterable: false, render: row => <Space size={4} wrap><Button size="small" onClick={() => void viewWorkOrder(row)}>View</Button><Button size="small" onClick={() => void openEdit(row)}>Edit</Button>{canDelete && <Popconfirm title="Delete this work order?" description="Delete its live cumulative pipeline cases first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeWorkOrder(row)}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</Space> },
        ]}
      /></Card>
      <Card size="small" title="Position-wise hiring progress"><DataTable
        rows={currentCases}
        getRowId={row => row.id}
        exportFileName="recruitment-hiring-journeys"
        emptyText="No governed hiring journey has started for this client."
        columns={[
          { key: 'positionName', label: 'Position', width: '200px', render: row => <div className="pipeline-table-candidate"><strong>{row.positionName}</strong><small>{row.workOrderNumber}</small></div> },
          { key: 'pipelineName', label: 'Pipeline', width: '190px' },
          { key: 'currentStageName', label: 'Current stage', width: '180px', render: row => row.currentStageName || 'Completed' },
          { key: 'currentStakeholderCode', label: 'Stakeholder', width: '160px', render: row => row.currentStakeholderCode || 'Unassigned' },
          { key: 'overallDueAtUtc', label: 'Overall due', width: '170px', render: row => dateTimeText(row.overallDueAtUtc) },
          { key: 'status', label: 'Status', width: '110px', render: row => <Tag color={statusColor(row.status)}>{row.status}</Tag> },
          { key: 'actions', label: 'Actions', width: '170px', sortable: false, filterable: false, render: row => <Space size={4} wrap><Button size="small" onClick={() => void viewCase(row)}>Open</Button>{canDelete && <Popconfirm title="Delete this hiring journey?" description="Only empty test journeys can be deleted safely." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeHiringCase(row)}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</Space> },
        ]}
      /></Card>
    </div>}

    {historicalCases.length > 0 && <Card size="small" title="Earlier journeys — history retained"><Space wrap>{historicalCases.map(row => <Button key={row.id} onClick={() => void viewCase(row)}>{row.positionName} · Journey #{row.id}</Button>)}</Space></Card>}</>}

    <Drawer width={860} title={draft.id ? `Edit ${draft.workOrderNumber}` : 'New client work order'} open={editorOpen} onClose={() => setEditorOpen(false)} extra={<Button data-testid="work-order-save" type="primary" loading={saving} onClick={() => void save()}>Save work order</Button>}>
      <Alert showIcon type="info" message="Pipeline-driven SLA" description="Record the approved work order here. When a role journey starts, its cumulative SLA and stage targets are applied automatically from the published client pipeline." />
      <div className="work-order-derived-sla" data-testid="work-order-derived-sla">
        <div><span>Overall SLA</span><small>Read-only · from published client pipeline</small></div>
        <Space size={[6, 6]} wrap>
          {!draft.clientId
            ? <Tag>Select client</Tag>
            : pipelineSlaLoading
              ? <Tag color="processing">Loading pipeline…</Tag>
              : publishedPipelineSlas.length
                ? publishedPipelineSlas.map(row => <Tag key={row.id} color={row.slaMode === 'CumulativeFromAnchor' ? 'purple' : 'blue'}>{row.pipelineName} · v{row.versionNumber} · {row.overallSlaMinutes > 0 ? durationText(row.overallSlaMinutes) : 'Stage-based SLA'}</Tag>)
                : <Tag color="warning">No published Position/Hybrid pipeline</Tag>}
        </Space>
      </div>
      <Form layout="vertical" className="work-order-form">
        <div className="work-order-form-grid"><Form.Item label="Client" required><Select data-testid="work-order-client" value={draft.clientId || undefined} disabled={clientScopeManaged && initialClientId > 0} showSearch optionFilterProp="label" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={clientId => patchDraft({ clientId, receivedFrom: clients.find(row => row.id === clientId)?.name || '' })} /></Form.Item><Form.Item label="Work order number" required><Input data-testid="work-order-number" value={draft.workOrderNumber} onChange={event => patchDraft({ workOrderNumber: event.target.value })} /></Form.Item><Form.Item label="Received date & time" required><Input data-testid="work-order-received-at" type="datetime-local" value={draft.receivedAtUtc} onChange={event => patchDraft({ receivedAtUtc: event.target.value })} /></Form.Item><Form.Item label="Status"><Select data-testid="work-order-status" value={draft.status} options={['Draft', 'Active', 'On Hold', 'Completed', 'Cancelled'].map(value => ({ value }))} onChange={status => patchDraft({ status })} /></Form.Item><Form.Item className="wide" label="Internal note"><Input.TextArea data-testid="work-order-remarks" rows={2} value={draft.remarks} onChange={event => patchDraft({ remarks: event.target.value })} /></Form.Item></div>
      </Form>
    </Drawer>

    <Drawer width={980} title={selectedWorkOrder ? `${selectedWorkOrder.workOrderNumber} · ${selectedWorkOrder.clientName}` : 'Work order'} open={!!selectedWorkOrder} onClose={() => setSelectedWorkOrder(null)} extra={selectedWorkOrder && <Button data-testid="work-order-add-hiring-request" type="primary" icon={<PlusOutlined />} onClick={() => addHiringRequest(selectedWorkOrder)}>Add hiring request</Button>}>
      {selectedWorkOrder && <><div className="work-order-detail-strip"><div><span>Received</span><b>{dateTimeText(selectedWorkOrder.receivedAtUtc)}</b></div><div><span>Hiring requests</span><b>{selectedWorkOrder.lineCount}</b></div><div><span>Source</span><b>{selectedWorkOrder.receivedFrom || 'Manual entry'}</b></div><Tag color={statusColor(selectedWorkOrder.status)}>{selectedWorkOrder.status}</Tag></div>
        {!selectedWorkOrder.lines.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No hiring request has been added against this work order yet." />}
        <div className="work-order-detail-lines">{selectedWorkOrder.lines.map(line => { const hiringCase = cases.find(row => row.workOrderLineId === line.id); return <article key={line.id}><div><span>{line.requisitionId ? `Role ${line.lineNumber}` : 'Work order intake'}</span><h3>{line.requisitionId ? line.positionName : 'Hiring request pending'}</h3><p>{line.requisitionId ? [line.payBandLevelCode, line.division, line.location].filter(Boolean).join(' · ') || 'Role details not entered' : 'SLA is already running from the work-order received time.'}</p></div><div>{line.requisitionId && <Tag>{line.numberOfPositions} opening{line.numberOfPositions === 1 ? '' : 's'}</Tag>}<Button type="primary" onClick={() => hiringCase && line.requisitionId ? void viewCase(hiringCase) : void prepareStart(selectedWorkOrder, line)}>{hiringCase && line.requisitionId ? 'Open journey' : 'Add hiring request'}</Button></div></article> })}</div>
        <EntityAttachmentPanel entityType="RECRUITMENT_WORK_ORDER" entityId={selectedWorkOrder.id} clientId={selectedWorkOrder.clientId} moduleCode="RECRUITMENT" formCodes={['WORK_ORDER']} title="Original work order & JD annexure" description="Stored through the existing secured attachment service and storage policy." />
      </>}
    </Drawer>

    <Drawer rootClassName="hiring-journey-drawer" width="min(1180px, 96vw)" title={selectedCase ? `${selectedCase.positionName} · ${selectedCase.workOrderNumber}` : 'Hiring journey'} open={!!selectedCase} onClose={() => { setSelectedCase(null); setStageLogOpen(false); setProcessDocuments([]); setProfileBatches([]); setCandidateApplications([]); setSelectedApplicationIds([]); setCaseActionError(''); setCaseActionDialog(null); setCaseDialogError('') }} extra={selectedCase && <Space wrap><Button data-testid="hiring-case-time-log" icon={<HistoryOutlined />} onClick={() => setStageLogOpen(true)}>Stage time log</Button>{selectedCase.status === 'Active' && <>{activeStage?.isPaused ? <Button data-testid="hiring-case-resume" loading={caseActionBusy} icon={<PlayCircleOutlined />} onClick={() => void resume()}>Resume SLA</Button> : <Tooltip title={pauseBlockedReason}><span><Button data-testid="hiring-case-pause" loading={caseActionBusy} disabled={Boolean(pauseBlockedReason)} icon={<PauseCircleOutlined />} onClick={pause}>Pause SLA</Button></span></Tooltip>}<Tooltip title={moveBlockedReason}><span><Button data-testid="hiring-case-move" type="primary" loading={caseActionBusy} disabled={Boolean(moveBlockedReason)} onClick={advance}>{selectedCase.advanceStatus === 'Pending Approval' ? 'Approval pending' : activeStage?.isTerminal ? 'Complete journey' : <><ArrowLeftOutlined /> Move <ArrowRightOutlined /></>}</Button></span></Tooltip></>}{canDelete && <Popconfirm title="Delete this hiring case?" description="Its SLA history and generated process records will be removed. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeHiringCase(selectedCase)}><Button danger icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</Space>}>
      {selectedCase && <><div className="case-hero"><div><span>{selectedCase.pipelineName}</span><h2>{selectedCase.currentStageName || 'Pipeline complete'}</h2><p>SLA anchored at {dateTimeText(selectedCase.slaAnchorAtUtc)} · overall due {dateTimeText(selectedCase.overallDueAtUtc)}</p></div><Tag color={statusColor(selectedCase.status)}>{selectedCase.status}</Tag></div>
        {caseActionError && <Alert data-testid="hiring-case-action-error" showIcon closable type="error" message="Action could not be completed" description={caseActionError} onClose={() => setCaseActionError('')} />}
        {moveBlockedReason && selectedCase.advanceStatus !== 'Pending Approval' && <Alert data-testid="hiring-case-action-guidance" showIcon type="warning" message="Next action needed" description={moveBlockedReason} />}
        {selectedCase.advanceStatus === 'Pending Approval' && <Alert data-testid="hiring-case-approval-pending" showIcon type="info" message="Stage movement awaiting approval" description={selectedCase.advanceMessage || 'The configured approver can action this request from global My Tasks.'} />}
        <div className="case-live-timers" data-testid="hiring-case-live-timers"><div><span>Current stage active time</span><b><ClockCircleOutlined /> {formatStageDuration(activeStageSeconds)}</b><small>{activeStage?.isPaused ? 'Paused — inactive time is excluded' : activeStage ? 'Live timer' : 'Journey completed'}</small></div><div><span>Overall SLA</span><b>{remainingDuration(selectedCase.overallDueAtUtc, clockNow)}</b><small>Anchored at {dateTimeText(selectedCase.slaAnchorAtUtc)}</small></div><Button icon={<HistoryOutlined />} onClick={() => setStageLogOpen(true)}>View stage history</Button></div>
        <Card title="Documents for this stage" extra={<Tag color="purple">Stage requirements</Tag>}>
          <p className="work-order-document-guidance">Prepare the document, then collect committee signatures by typing, drawing or uploading a signature image. A separately signed final PDF can still be uploaded.</p>
          {!activeStage?.processDocumentRequirements?.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="This stage has no process-document requirement." />}
          {activeStage?.processDocumentRequirements?.map(requirement => {
            const document = processDocuments.find(row => row.pipelineStageId === activeStage.pipelineStageId && row.documentType === requirement.documentType && currentCohortDocument(row))
            return <div className="work-order-document" key={requirement.id} data-testid={`hiring-document-${requirement.documentType}`}>
              <header className="work-order-document-header"><div><b><FileProtectOutlined /> {requirement.documentType.replaceAll('_', ' ')}</b><span>{requirement.isRequired ? 'Required' : 'Optional'}{requirement.requiresSignature ? ' · signature required' : ''}</span></div><Space wrap className="work-order-document-actions">
              {!document ? <Button data-testid={`hiring-document-create-${requirement.documentType}`} loading={documentSaving} onClick={() => void prepareProcessDocument(requirement.documentType, requirement.templateId)}>Prepare</Button> : <Tag color={document.status === 'Signed' ? 'green' : 'blue'}>v{document.versionNumber} · {document.status}</Tag>}
              {document && requirement.templateId && document.status !== 'Signed' && <Button loading={documentSaving} onClick={() => void generateProcessDocument(document)}>{document.attachmentPublicId ? 'Regenerate PDF' : 'Generate PDF'}</Button>}
              {document && requirement.requiresSignature && document.status !== 'Signed' && <Button data-testid={`hiring-document-sign-${requirement.documentType}`} onClick={() => void openSignature(document)}>Sign MoM</Button>}
              {document && requirement.requiresSignature && document.status !== 'Signed' && (document.hasFinalSignedAttachment || document.capturedSignaturesComplete) && <Button type="primary" onClick={() => void markProcessDocumentSigned(document)}>Finalize signed</Button>}
              {document && requirement.requiresSignature && document.status !== 'Signed' && <Tag color={document.capturedSignaturesComplete ? 'green' : 'orange'}>{document.signatureCount || 0}/{document.requiredSignatureCount || 1} signatures</Tag>}
              </Space></header>
              {document && <EntityAttachmentPanel entityType="RECRUITMENT_PROCESS_DOCUMENT" entityId={document.id} clientId={selectedCase.clientId} moduleCode="RECRUITMENT" formCodes={['PROCESS_DOCUMENT']} title="Document file" description="Private, versioned storage with secure preview and download." singleFieldLabel={requirement.documentType.replaceAll('_', ' ')} singleFieldHelp={requirement.requiresSignature ? 'Optional alternative: upload a separately signed final PDF instead of using the signature capture.' : 'Upload the source document for this requirement. Use Generate PDF only when a template is configured.'} onChanged={() => void viewCase(selectedCase)} />}
            </div>
          })}
        </Card>
        <Card className="profile-batch-card" title="Approved candidate profile batches" extra={<Tag color="cyan">Client forwarding</Tag>}>
          {!selectedCase.positionId ? <Alert showIcon type="warning" message="Link this work-order line to its open position before batching candidates." description="Candidate applications are position-scoped, so the system will never mix profiles from another role or client." /> : <>
            <div className="profile-batch-compose">
              <Select data-testid="profile-batch-candidates" mode="multiple" allowClear showSearch optionFilterProp="label" value={selectedApplicationIds} placeholder="Select shortlisted candidates for this client batch" options={candidateApplications.filter(application => !['Rejected', 'Withdrawn', 'Joined'].includes(application.currentStage)).map(application => ({ value: application.id, label: `${application.candidateName} · ${application.applicationCode} · ${application.currentStage} · ATS ${application.atsScore ?? 'not scored'}` }))} onChange={setSelectedApplicationIds} />
              <Button data-testid="profile-batch-create" type="primary" loading={batchSaving} disabled={!selectedApplicationIds.length} onClick={() => void createProfileBatch()}>Create draft batch</Button>
            </div>
            {!candidateApplications.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No application is linked to this hiring-case position yet." />}
          </>}
          <div className="profile-batch-list">{profileBatches.map(batch => <article key={batch.id} data-testid={`profile-batch-${batch.id}`}>
            <header><div><span>{batch.batchNumber}</span><b>{batch.items.length} candidate{batch.items.length === 1 ? '' : 's'}</b></div><Tag color={batch.status === 'Forwarded' ? 'green' : batch.status === 'Approved' ? 'blue' : 'gold'}>{batch.status}</Tag></header>
            <div className="profile-batch-people">{batch.items.map(item => <div key={item.id}><span><b>{item.candidateName}</b>{item.atsScore != null && <Tag>ATS {item.atsScore}</Tag>}</span>{item.readinessStatus === 'Ready' ? <Tag color="green">Ready</Tag> : <Tag color="red">Missing: {item.missingFields || 'candidate information'}</Tag>}</div>)}</div>
            <footer><span>{batch.deliveries.length ? `${batch.deliveries.length} audited delivery queue record${batch.deliveries.length === 1 ? '' : 's'}` : `Created ${dateTimeText(batch.createdAtUtc)}`}</span><Space>{batch.status === 'Draft' && <Button data-testid={`profile-batch-approve-${batch.id}`} loading={batchSaving} onClick={() => void approveProfileBatch(batch.id)}>Approve complete profiles</Button>}{batch.status === 'Approved' && <Button data-testid={`profile-batch-forward-${batch.id}`} type="primary" loading={batchSaving} onClick={() => void forwardProfileBatch(batch.id)}>Forward to configured client recipients</Button>}</Space></footer>
          </article>)}</div>
        </Card>
      </>}
    </Drawer>

    <Modal
      width={720}
      open={!!signatureDocument}
      title="Sign committee MoM"
      okText="Capture signature"
      confirmLoading={signatureSaving}
      okButtonProps={{ disabled: signerName.trim().length < 2 || (signatureMethod !== 'Typed' && !signatureDataUrl) }}
      onOk={() => void captureSignature()}
      onCancel={() => { if (!signatureSaving) { setSignatureDocument(null); setSignatureDataUrl(''); setDocumentSignatures([]) } }}
      destroyOnClose
    >
      <Alert showIcon type="info" message="Audited electronic signature" description="Your signed-in user, method and timestamp are stored with this MoM. Every required panel member signs separately." />
      <div className="mom-signature-form">
        <Form.Item label="Signer name" required><Input value={signerName} onChange={event => setSignerName(event.target.value)} /></Form.Item>
        <Form.Item label="Signature method" required><Select value={signatureMethod} options={['Typed', 'Drawn', 'Image'].map(value => ({ value, label: value === 'Image' ? 'Upload PNG/JPG' : value }))} onChange={value => { setSignatureMethod(value); setSignatureDataUrl('') }} /></Form.Item>
        {signatureMethod === 'Typed' && <div className="mom-typed-signature" aria-label="Typed signature preview">{signerName || 'Your signature'}</div>}
        {signatureMethod === 'Drawn' && <SignaturePad onChange={setSignatureDataUrl} />}
        {signatureMethod === 'Image' && <div className="mom-signature-upload"><input type="file" accept="image/png,image/jpeg" onChange={event => uploadSignatureImage(event.target.files?.[0])} />{signatureDataUrl && <img src={signatureDataUrl} alt="Uploaded signature preview" />}</div>}
        {!!documentSignatures.length && <div className="mom-signature-list"><b>Captured signatures</b>{documentSignatures.map(row => <div key={row.id}><span>{row.signerName} · {row.signerRole}</span><Tag color="green">{row.signatureMethod} · {dateTimeText(row.signedAtUtc)}</Tag></div>)}</div>}
      </div>
    </Modal>

    <Modal className="stage-time-log-modal" width={820} open={stageLogOpen && !!selectedCase} title={selectedCase ? `Stage time log · ${selectedCase.positionName}` : 'Stage time log'} footer={null} onCancel={() => setStageLogOpen(false)} destroyOnClose>
      <p className="stage-time-log-help">Active time excludes every recorded pause. Completed stages stay fixed; the current stage updates live.</p>
      <Timeline className="case-timeline stage-time-log" items={stageTimelineItems} />
      {!!caseEventItems.length && <><Divider orientation="left">Process audit events</Divider><Timeline className="case-event-log" items={caseEventItems} /></>}
    </Modal>

    <Modal
      className="hiring-case-action-modal"
      open={caseActionDialog === 'pause'}
      title="Pause this stage SLA?"
      okText="Pause SLA"
      confirmLoading={caseActionBusy}
      cancelButtonProps={{ disabled: caseActionBusy }}
      onOk={() => void submitPause()}
      onCancel={() => { if (!caseActionBusy) { setCaseActionDialog(null); setCaseDialogError('') } }}
      destroyOnClose
    >
      <div className="case-action-confirm">
        <p>The SLA clock will pause. The reason is required and will remain in the journey audit history.</p>
        {caseDialogError && <Alert data-testid="hiring-case-dialog-error" showIcon type="error" message={caseDialogError} />}
        <Form.Item label="Reason for pause" required>
          <Input.TextArea data-testid="hiring-case-pause-reason" autoFocus rows={3} value={caseActionReason} placeholder="For example: awaiting documents from the client" onChange={event => { setCaseActionReason(event.target.value); setCaseDialogError('') }} />
        </Form.Item>
      </div>
    </Modal>

    <Modal
      className="hiring-case-action-modal"
      open={caseActionDialog === 'advance'}
      title={isDivisionRejectionOutcome(selectedCaseOutcome) ? 'Reject this hiring demand?' : activeStage?.isTerminal ? 'Complete this hiring journey?' : 'Move hiring journey?'}
      okText={isDivisionRejectionOutcome(selectedCaseOutcome) ? 'Rejected by Division' : activeStage?.requiresApproval && !selectedCaseMovingBack ? 'Send for approval' : activeStage?.isTerminal ? 'Complete journey' : 'Move'}
      confirmLoading={caseActionBusy}
      cancelButtonProps={{ disabled: caseActionBusy }}
      onOk={() => void submitAdvance()}
      onCancel={() => { if (!caseActionBusy) { setCaseActionDialog(null); setCaseDialogError('') } }}
      destroyOnClose
    >
      <div className="case-action-confirm">
        <Alert
          showIcon
          type={isDivisionRejectionOutcome(selectedCaseOutcome) ? 'warning' : activeStage?.requiresApproval && !selectedCaseMovingBack ? 'info' : 'success'}
          message={isDivisionRejectionOutcome(selectedCaseOutcome) ? 'Rejected by Division' : selectedCaseMovingBack ? 'Previous-stage movement will be audit logged' : activeStage?.requiresApproval ? 'Approval will be requested' : activeStage?.isTerminal ? 'This will complete the journey' : 'All required checks are complete'}
          description={isDivisionRejectionOutcome(selectedCaseOutcome) ? 'This is recorded as a process outcome. Business SLA breach remains an independent deadline-based status.' : selectedCaseMovingBack ? 'The original stage history remains unchanged and a new stage cycle starts.' : activeStage?.requiresApproval ? 'The configured approver will receive this action in My Tasks. The stage moves only after approval.' : undefined}
        />
        {caseDialogError && <Alert data-testid="hiring-case-dialog-error" showIcon type="error" message={caseDialogError} />}
        {!!caseTransitions.length && <Form.Item label="Stage action" required><Select data-testid="hiring-case-transition" value={selectedCaseOutcome} options={caseTransitions.map(row => ({ value: row.outcomeCode, label: hiringTransitionLabel(row) }))} onChange={setSelectedCaseOutcome} /></Form.Item>}
        <Form.Item label={selectedCaseTransition?.requiresReason ? 'Reason' : 'Movement note (optional)'} required={selectedCaseTransition?.requiresReason}>
          <Input.TextArea data-testid="hiring-case-move-note" rows={3} value={caseActionReason} placeholder="Add a short note for the audit history" onChange={event => { setCaseActionReason(event.target.value); setCaseDialogError('') }} />
        </Form.Item>
      </div>
    </Modal>
  </section>
}

function SignaturePad({ onChange }: { onChange: (value: string) => void }) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const drawing = useRef(false)
  const point = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current!
    const bounds = canvas.getBoundingClientRect()
    return { x: (event.clientX - bounds.left) * (canvas.width / bounds.width), y: (event.clientY - bounds.top) * (canvas.height / bounds.height) }
  }
  const start = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current!
    const current = point(event)
    drawing.current = true
    canvas.setPointerCapture(event.pointerId)
    const context = canvas.getContext('2d')!
    context.beginPath(); context.moveTo(current.x, current.y)
  }
  const move = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return
    const canvas = canvasRef.current!
    const current = point(event)
    const context = canvas.getContext('2d')!
    context.strokeStyle = '#17223b'; context.lineWidth = 2.4; context.lineCap = 'round'; context.lineJoin = 'round'
    context.lineTo(current.x, current.y); context.stroke()
  }
  const finish = () => {
    if (!drawing.current || !canvasRef.current) return
    drawing.current = false
    onChange(canvasRef.current.toDataURL('image/png'))
  }
  const clear = () => {
    const canvas = canvasRef.current
    if (!canvas) return
    canvas.getContext('2d')?.clearRect(0, 0, canvas.width, canvas.height)
    onChange('')
  }
  return <div className="mom-signature-pad"><canvas ref={canvasRef} width={640} height={180} onPointerDown={start} onPointerMove={move} onPointerUp={finish} onPointerCancel={finish} onPointerLeave={finish} /><Button size="small" onClick={clear}>Clear drawing</Button></div>
}
