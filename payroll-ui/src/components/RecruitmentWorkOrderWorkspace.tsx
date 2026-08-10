import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card, Drawer, Empty, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Statistic, Tag, Timeline, Tooltip, message } from 'antd'
import { ClockCircleOutlined, DeleteOutlined, FileProtectOutlined, PauseCircleOutlined, PlayCircleOutlined, PlusOutlined, TeamOutlined } from '@ant-design/icons'
import { useAuthSession } from './AuthGate'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { getClients } from '../services/payrollService'
import { getRecruitmentPipelineVersions, getRecruitmentPipelines } from '../services/recruitmentOrchestrationService'
import { advanceRecruitmentHiringCase, approveRecruitmentProfileBatch, createRecruitmentProfileBatch, deleteRecruitmentHiringCase, deleteRecruitmentWorkOrder, forwardRecruitmentProfileBatch, generateRecruitmentProcessDocument, getRecruitmentHiringCase, getRecruitmentHiringCases, getRecruitmentProcessDocuments, getRecruitmentProfileBatches, getRecruitmentWorkOrder, getRecruitmentWorkOrders, pauseRecruitmentHiringCase, resumeRecruitmentHiringCase, saveRecruitmentProcessDocument, saveRecruitmentWorkOrder, startRecruitmentHiringCase } from '../services/recruitmentCaseService'
import { getApplications } from '../services/recruitmentTalentService'
import { getRecruitmentOpenPositions } from '../services/recruitmentService'
import type { Client, RecruitmentCandidateApplication, RecruitmentOpenPosition } from '../types/payroll'
import type { RecruitmentPipelineVersion } from '../types/recruitmentOrchestration'
import type { RecruitmentHiringCase, RecruitmentProcessDocument, RecruitmentProfileSubmissionBatch, RecruitmentWorkOrder, SaveRecruitmentWorkOrder } from '../types/recruitmentCases'
import './RecruitmentWorkOrderWorkspace.css'

type WorkOrderDraft = SaveRecruitmentWorkOrder & { overallSlaDays: number | null }
type PipelineOption = RecruitmentPipelineVersion & { label: string }

const blankLine = (lineNumber: number) => ({ id: 0, lineNumber, positionName: '', payBandLevelCode: '', numberOfPositions: 1, location: '', division: '', requisitionId: null, positionId: null, status: 'Open' })
const blankWorkOrder = (): WorkOrderDraft => ({ id: 0, clientId: 0, workOrderNumber: '', receivedAtUtc: '', receivedFrom: '', subject: '', remarks: '', status: 'Draft', overallSlaMinutes: 0, overallSlaDays: null, lines: [blankLine(1)] })
const dateTimeText = (value?: string | null) => value ? new Date(value).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' }) : 'Not set'
const durationText = (minutes?: number | null) => minutes == null ? 'No target' : minutes === 0 ? 'Day 0' : `${(minutes / 1440).toFixed(minutes % 1440 ? 1 : 0)} days`
const statusColor = (status: string) => status === 'Completed' ? 'green' : status === 'Active' ? 'blue' : status === 'On Hold' ? 'orange' : status === 'Cancelled' ? 'red' : 'default'

export default function RecruitmentWorkOrderWorkspace() {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const [clients, setClients] = useState<Client[]>([])
  const [openPositions, setOpenPositions] = useState<RecruitmentOpenPosition[]>([])
  const [clientId, setClientId] = useState(0)
  const [query, setQuery] = useState('')
  const [workOrders, setWorkOrders] = useState<RecruitmentWorkOrder[]>([])
  const [cases, setCases] = useState<RecruitmentHiringCase[]>([])
  const [draft, setDraft] = useState<WorkOrderDraft>(blankWorkOrder)
  const [editorOpen, setEditorOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [selectedWorkOrder, setSelectedWorkOrder] = useState<RecruitmentWorkOrder | null>(null)
  const [selectedCase, setSelectedCase] = useState<RecruitmentHiringCase | null>(null)
  const [pipelineOptions, setPipelineOptions] = useState<PipelineOption[]>([])
  const [startLineId, setStartLineId] = useState(0)
  const [startPipelineId, setStartPipelineId] = useState(0)
  const [startOpen, setStartOpen] = useState(false)
  const [processDocuments, setProcessDocuments] = useState<RecruitmentProcessDocument[]>([])
  const [documentSaving, setDocumentSaving] = useState(false)
  const [candidateApplications, setCandidateApplications] = useState<RecruitmentCandidateApplication[]>([])
  const [profileBatches, setProfileBatches] = useState<RecruitmentProfileSubmissionBatch[]>([])
  const [selectedApplicationIds, setSelectedApplicationIds] = useState<number[]>([])
  const [batchSaving, setBatchSaving] = useState(false)
  const [caseActionBusy, setCaseActionBusy] = useState(false)
  const [caseActionError, setCaseActionError] = useState('')
  const [caseActionDialog, setCaseActionDialog] = useState<'pause' | 'advance' | null>(null)
  const [caseActionReason, setCaseActionReason] = useState('')
  const [caseDialogError, setCaseDialogError] = useState('')

  const load = async () => {
    const [orders, hiringCases] = await Promise.all([getRecruitmentWorkOrders(clientId, query), getRecruitmentHiringCases(clientId)])
    setWorkOrders(orders); setCases(hiringCases)
  }
  useEffect(() => { void Promise.all([getClients(), getRecruitmentOpenPositions()]).then(([clientRows, positionRows]) => { setClients(clientRows); setOpenPositions(positionRows) }) }, [])
  useEffect(() => { void load() }, [clientId])

  const stats = useMemo(() => ({
    activeOrders: workOrders.filter(row => row.status === 'Active').length,
    positions: workOrders.reduce((total, row) => total + row.lineCount, 0),
    activeCases: cases.filter(row => row.status === 'Active').length,
    breached: cases.filter(row => row.status === 'Active' && row.overallDueAtUtc && new Date(row.overallDueAtUtc).getTime() < Date.now()).length,
  }), [cases, workOrders])

  const openNew = () => { setDraft(blankWorkOrder()); setEditorOpen(true) }
  const openEdit = async (row: RecruitmentWorkOrder) => {
    const detail = await getRecruitmentWorkOrder(row.id)
    if (!detail) return message.error('Unable to load this work order.')
    setDraft({ ...detail, receivedAtUtc: new Date(detail.receivedAtUtc).toISOString().slice(0, 16), overallSlaDays: detail.overallSlaMinutes ? detail.overallSlaMinutes / 1440 : null, lines: detail.lines.map(line => ({ ...line })) })
    setEditorOpen(true)
  }
  const patchDraft = (patch: Partial<WorkOrderDraft>) => setDraft(current => ({ ...current, ...patch }))
  const patchLine = (index: number, patch: Partial<WorkOrderDraft['lines'][number]>) => patchDraft({ lines: draft.lines.map((line, rowIndex) => rowIndex === index ? { ...line, ...patch } : line) })
  const addLine = () => patchDraft({ lines: [...draft.lines, blankLine(draft.lines.length + 1)] })
  const removeLine = (index: number) => patchDraft({ lines: draft.lines.filter((_, rowIndex) => rowIndex !== index).map((line, rowIndex) => ({ ...line, lineNumber: rowIndex + 1 })) })

  const save = async () => {
    if (!draft.clientId) return message.warning('Select the client that issued this work order.')
    if (!draft.workOrderNumber.trim() || !draft.receivedAtUtc) return message.warning('Work order number and received time are required.')
    if (!draft.overallSlaDays || draft.overallSlaDays <= 0) return message.warning('Enter the agreed overall pipeline SLA in days.')
    if (!draft.lines.length || draft.lines.some(line => !line.positionName.trim() || line.numberOfPositions <= 0)) return message.warning('Complete every position line.')
    setSaving(true)
    const response = await saveRecruitmentWorkOrder({ ...draft, receivedAtUtc: new Date(draft.receivedAtUtc).toISOString(), overallSlaMinutes: Math.round(draft.overallSlaDays * 1440) })
    setSaving(false)
    if (!response.ok || !response.data) return
    setEditorOpen(false); await load(); setSelectedWorkOrder(response.data)
  }

  const viewWorkOrder = async (row: RecruitmentWorkOrder) => setSelectedWorkOrder(await getRecruitmentWorkOrder(row.id))
  const removeWorkOrder = async (row: RecruitmentWorkOrder) => {
    const response = await deleteRecruitmentWorkOrder(row.id)
    if (!response.ok) return
    if (selectedWorkOrder?.id === row.id) setSelectedWorkOrder(null)
    await load()
  }
  const viewCase = async (row: RecruitmentHiringCase) => {
    setCaseActionError('')
    const [detail, documents, batches] = await Promise.all([getRecruitmentHiringCase(row.id), getRecruitmentProcessDocuments(row.id), getRecruitmentProfileBatches(row.id)])
    const applications = detail?.positionId ? await getApplications({ positionId: detail.positionId }) : []
    if (!detail) setCaseActionError('This hiring journey could not be loaded. Refresh the page and try again.')
    setSelectedCase(detail); setProcessDocuments(documents); setProfileBatches(batches); setCandidateApplications(applications); setSelectedApplicationIds([])
  }
  const removeHiringCase = async (row: RecruitmentHiringCase) => {
    const response = await deleteRecruitmentHiringCase(row.id)
    if (!response.ok) return
    if (selectedCase?.id === row.id) setSelectedCase(null)
    await load()
  }

  const prepareStart = async (workOrder: RecruitmentWorkOrder, lineId: number) => {
    const definitions = await getRecruitmentPipelines(workOrder.clientId)
    const versionGroups = await Promise.all(definitions.map(async definition => ({ definition, versions: await getRecruitmentPipelineVersions(definition.id) })))
    const options = versionGroups.flatMap(group => group.versions
      .filter(version => version.status === 'Published' && ['Position', 'Hybrid'].includes(version.scopeType ?? 'Application'))
      .map(version => ({ ...version, label: `${group.definition.pipelineName} · v${version.versionNumber} · ${version.slaMode === 'CumulativeFromAnchor' ? 'Cumulative SLA' : 'Stage SLA'}` })))
    if (!options.length) return message.warning('Publish a Position or Hybrid pipeline for this client first.')
    setPipelineOptions(options); setStartLineId(lineId); setStartPipelineId(0); setStartOpen(true)
  }
  const startCase = async () => {
    if (!startPipelineId) return message.warning('Select the published pipeline version.')
    const response = await startRecruitmentHiringCase(startLineId, startPipelineId)
    if (!response.ok || !response.data) return
    setStartOpen(false); setSelectedCase(response.data); setProcessDocuments([]); setSelectedWorkOrder(null); await load()
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
    setCaseActionDialog(null); setSelectedCase(response.data); await load()
  }
  const resume = async () => {
    if (!selectedCase || !activeStage?.isPaused) return setCaseActionError('This stage SLA is not paused.')
    setCaseActionError(''); setCaseActionBusy(true)
    const response = await resumeRecruitmentHiringCase(selectedCase.id)
    setCaseActionBusy(false)
    if (!response.ok || !response.data) return setCaseActionError(response.error || 'The SLA could not be resumed.')
    setSelectedCase(response.data); await load()
  }
  const advance = () => {
    if (!selectedCase || moveBlockedReason) return setCaseActionError(moveBlockedReason || 'This hiring journey cannot move right now.')
    setCaseActionError('')
    setCaseActionReason('')
    setCaseDialogError('')
    setCaseActionDialog('advance')
  }
  const submitAdvance = async () => {
    if (!selectedCase) return
    setCaseDialogError(''); setCaseActionBusy(true)
    const response = await advanceRecruitmentHiringCase(selectedCase.id, caseActionReason.trim())
    setCaseActionBusy(false)
    if (!response.ok || !response.data) {
      const error = response.error || 'The stage could not be moved.'
      setCaseDialogError(error); setCaseActionError(error); return
    }
    setCaseActionDialog(null); setSelectedCase(response.data); await load()
    if (response.data.advanceStatus === 'Pending Approval') message.info(response.data.advanceMessage || 'Stage movement is pending in My Tasks.')
    else message.success(response.data.advanceMessage || 'Hiring case moved to the next stage.')
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
  const missingRequiredDocuments = activeStage?.processDocumentRequirements.filter(requirement => {
    if (!requirement.isRequired) return false
    const document = processDocuments.find(row => row.pipelineStageId === activeStage.pipelineStageId && row.documentType === requirement.documentType)
    if (!document) return true
    return Boolean(requirement.requiresSignature && (document.status !== 'Signed' || !document.hasFinalSignedAttachment))
  }) ?? []
  const pauseBlockedReason = !activeStage
    ? 'There is no active stage to pause.'
    : activeStage.isPaused
      ? 'This stage SLA is already paused.'
      : activeStage.allowPause === false
        ? 'SLA pause is disabled in this stage configuration.'
        : ''
  const moveBlockedReason = selectedCase?.status !== 'Active'
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
    <div className="work-order-command-bar">
      <div><span>Client hiring demand</span><h2>Hiring orders</h2><p>Record the approved roles received from a client, then start and track each role's hiring journey.</p></div>
      <Space wrap><Select allowClear value={clientId || undefined} placeholder="All accessible clients" showSearch optionFilterProp="label" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={value => setClientId(value || 0)} /><Input.Search value={query} placeholder="Work order or subject" onChange={event => setQuery(event.target.value)} onSearch={() => void load()} /><Button data-testid="work-order-add" type="primary" icon={<PlusOutlined />} onClick={openNew}>Add work order</Button></Space>
    </div>
    <div className="work-order-metrics">
      <Card><Statistic title="Active orders" value={stats.activeOrders} prefix={<FileProtectOutlined />} /></Card>
      <Card><Statistic title="Roles requested" value={stats.positions} prefix={<TeamOutlined />} /></Card>
      <Card><Statistic title="Active journeys" value={stats.activeCases} prefix={<PlayCircleOutlined />} /></Card>
      <Card className={stats.breached ? 'risk' : ''}><Statistic title="Overdue journeys" value={stats.breached} prefix={<ClockCircleOutlined />} /></Card>
    </div>
    <div className="work-order-columns">
      <Card title="Client hiring orders" extra={<Tag>{workOrders.length} records</Tag>}>
        {!workOrders.length ? <Empty description="No work order has been entered for this client." /> : <div className="work-order-list">{workOrders.map(row => <article key={row.id}>
          <button type="button" onClick={() => void viewWorkOrder(row)}><div><span>{row.clientName}</span><h3>{row.workOrderNumber}</h3><p>{row.subject || 'No subject entered'}</p></div><Tag color={statusColor(row.status)}>{row.status}</Tag></button>
          <footer><span>{dateTimeText(row.receivedAtUtc)}</span><b>{row.lineCount} position line{row.lineCount === 1 ? '' : 's'}</b><span>{durationText(row.overallSlaMinutes)} overall</span><Button size="small" onClick={() => void openEdit(row)}>Edit</Button>{canDelete && <Popconfirm title="Delete this work order?" description="Delete its live cumulative pipeline cases first. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeWorkOrder(row)}><Button danger size="small" icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</footer>
        </article>)}</div>}
      </Card>
      <Card title="Active hiring journeys" extra={<Tag color="purple">Role-wise SLA</Tag>}>
        {!cases.length ? <Empty description="Open an order and start a published hiring pipeline for one role." /> : <div className="hiring-case-list">{cases.map(row => {
          const overdue = row.status === 'Active' && row.overallDueAtUtc && new Date(row.overallDueAtUtc).getTime() < Date.now()
          return <button type="button" key={row.id} className={overdue ? 'overdue' : ''} onClick={() => void viewCase(row)}><div><Tag color={statusColor(row.status)}>{row.status}</Tag><span>{row.currentStakeholderCode || 'Unassigned stakeholder'}</span></div><h3>{row.positionName}</h3><p>{row.workOrderNumber} · {row.pipelineName}</p><footer><span>{row.currentStageName || 'Completed'}</span><b>{row.overallDueAtUtc ? `Due ${dateTimeText(row.overallDueAtUtc)}` : 'No overall due date'}</b></footer></button>
        })}</div>}
      </Card>
    </div>

    <Drawer width={860} title={draft.id ? `Edit ${draft.workOrderNumber}` : 'New client work order'} open={editorOpen} onClose={() => setEditorOpen(false)} extra={<Button data-testid="work-order-save" type="primary" loading={saving} onClick={() => void save()}>Save work order</Button>}>
      <Alert showIcon type="info" message="Manual intake only" description="Inbound email parsing is intentionally not used. Record the approved work order here and upload the original order/JD annexure after saving." />
      <Form layout="vertical" className="work-order-form">
        <div className="work-order-form-grid"><Form.Item label="Client" required><Select data-testid="work-order-client" value={draft.clientId || undefined} showSearch optionFilterProp="label" options={clients.map(client => ({ value: client.id, label: client.name }))} onChange={clientId => patchDraft({ clientId })} /></Form.Item><Form.Item label="Work order number" required><Input data-testid="work-order-number" value={draft.workOrderNumber} onChange={event => patchDraft({ workOrderNumber: event.target.value })} /></Form.Item><Form.Item label="Received date & time" required><Input data-testid="work-order-received-at" type="datetime-local" value={draft.receivedAtUtc} onChange={event => patchDraft({ receivedAtUtc: event.target.value })} /></Form.Item><Form.Item label="Overall SLA (days)" required><InputNumber data-testid="work-order-overall-sla" min={0.01} precision={2} value={draft.overallSlaDays} onChange={overallSlaDays => patchDraft({ overallSlaDays: overallSlaDays == null ? null : Number(overallSlaDays) })} /></Form.Item><Form.Item label="Received from"><Input data-testid="work-order-received-from" value={draft.receivedFrom} placeholder="Client stakeholder / source" onChange={event => patchDraft({ receivedFrom: event.target.value })} /></Form.Item><Form.Item label="Status"><Select data-testid="work-order-status" value={draft.status} options={['Draft', 'Active', 'On Hold', 'Completed', 'Cancelled'].map(value => ({ value }))} onChange={status => patchDraft({ status })} /></Form.Item><Form.Item className="wide" label="Subject"><Input data-testid="work-order-subject" value={draft.subject} onChange={event => patchDraft({ subject: event.target.value })} /></Form.Item><Form.Item className="wide" label="Internal note"><Input.TextArea data-testid="work-order-remarks" rows={2} value={draft.remarks} onChange={event => patchDraft({ remarks: event.target.value })} /></Form.Item></div>
        <div className="work-order-line-heading"><div><h3>Roles requested</h3><p>Each role starts one independently tracked hiring journey.</p></div><Button data-testid="work-order-add-position" icon={<PlusOutlined />} onClick={addLine}>Add role</Button></div>
        {draft.lines.map((line, index) => <Card size="small" key={`${line.id}-${index}`} title={`Line ${index + 1}`} extra={draft.lines.length > 1 && <Button danger size="small" onClick={() => removeLine(index)}>Remove</Button>}><div className="work-order-line-grid"><Form.Item label="Position / posting name" required><Input data-testid={`work-order-position-${index}`} value={line.positionName} onChange={event => patchLine(index, { positionName: event.target.value })} /></Form.Item><Form.Item label="Pay band / level"><Input data-testid={`work-order-pay-band-${index}`} value={line.payBandLevelCode} placeholder="A–H or client scale" onChange={event => patchLine(index, { payBandLevelCode: event.target.value })} /></Form.Item><Form.Item label="No. of positions" required><InputNumber data-testid={`work-order-count-${index}`} min={1} value={line.numberOfPositions} onChange={numberOfPositions => patchLine(index, { numberOfPositions: Number(numberOfPositions || 1) })} /></Form.Item><Form.Item label="Location"><Input data-testid={`work-order-location-${index}`} value={line.location} onChange={event => patchLine(index, { location: event.target.value })} /></Form.Item><Form.Item label="Division"><Input data-testid={`work-order-division-${index}`} value={line.division} onChange={event => patchLine(index, { division: event.target.value })} /></Form.Item><Form.Item className="wide-position-link" label="Existing open position (link when available)"><Select data-testid={`work-order-open-position-${index}`} allowClear showSearch optionFilterProp="label" value={line.positionId || undefined} options={openPositions.filter(position => !draft.clientId || position.clientId === draft.clientId).map(position => ({ value: position.id, label: `${position.positionCode} · ${position.positionTitle} · ${position.status}` }))} onChange={positionId => { const position = openPositions.find(row => row.id === positionId); patchLine(index, { positionId: position?.id ?? null, requisitionId: position?.requisitionId ?? null }) }} /></Form.Item></div></Card>)}
      </Form>
    </Drawer>

    <Drawer width={980} title={selectedWorkOrder ? `${selectedWorkOrder.workOrderNumber} · ${selectedWorkOrder.clientName}` : 'Work order'} open={!!selectedWorkOrder} onClose={() => setSelectedWorkOrder(null)}>
      {selectedWorkOrder && <><div className="work-order-detail-strip"><div><span>Received</span><b>{dateTimeText(selectedWorkOrder.receivedAtUtc)}</b></div><div><span>Overall due</span><b>{dateTimeText(selectedWorkOrder.dueAtUtc)}</b></div><div><span>Source</span><b>{selectedWorkOrder.receivedFrom || 'Manual entry'}</b></div><Tag color={statusColor(selectedWorkOrder.status)}>{selectedWorkOrder.status}</Tag></div>
        <div className="work-order-detail-lines">{selectedWorkOrder.lines.map(line => <article key={line.id}><div><span>Role {line.lineNumber}</span><h3>{line.positionName}</h3><p>{[line.payBandLevelCode, line.division, line.location].filter(Boolean).join(' · ') || 'Role details not entered'}</p></div><div><Tag>{line.numberOfPositions} opening{line.numberOfPositions === 1 ? '' : 's'}</Tag><Button type="primary" disabled={cases.some(row => row.workOrderLineId === line.id)} onClick={() => void prepareStart(selectedWorkOrder, line.id)}>{cases.some(row => row.workOrderLineId === line.id) ? 'Journey started' : 'Start hiring journey'}</Button></div></article>)}</div>
        <EntityAttachmentPanel entityType="RECRUITMENT_WORK_ORDER" entityId={selectedWorkOrder.id} clientId={selectedWorkOrder.clientId} moduleCode="RECRUITMENT" formCodes={['WORK_ORDER']} title="Original work order & JD annexure" description="Stored through the existing secured attachment service and storage policy." />
      </>}
    </Drawer>

    <Modal open={startOpen} title="Start position hiring case" okText="Start SLA clock" onOk={() => void startCase()} onCancel={() => setStartOpen(false)}><Alert showIcon type="warning" message="The SLA anchor is the work order received timestamp." /><Form.Item label="Published Position / Hybrid pipeline" required style={{ marginTop: 18 }}><Select data-testid="hiring-case-pipeline" value={startPipelineId || undefined} options={pipelineOptions.map(row => ({ value: row.id, label: row.label }))} onChange={setStartPipelineId} /></Form.Item></Modal>

    <Drawer width={1040} title={selectedCase ? `${selectedCase.positionName} · ${selectedCase.workOrderNumber}` : 'Hiring journey'} open={!!selectedCase} onClose={() => { setSelectedCase(null); setProcessDocuments([]); setProfileBatches([]); setCandidateApplications([]); setSelectedApplicationIds([]); setCaseActionError(''); setCaseActionDialog(null); setCaseDialogError('') }} extra={selectedCase && <Space>{selectedCase.status === 'Active' && <>{activeStage?.isPaused ? <Button data-testid="hiring-case-resume" loading={caseActionBusy} icon={<PlayCircleOutlined />} onClick={() => void resume()}>Resume SLA</Button> : <Tooltip title={pauseBlockedReason}><span><Button data-testid="hiring-case-pause" loading={caseActionBusy} disabled={Boolean(pauseBlockedReason)} icon={<PauseCircleOutlined />} onClick={pause}>Pause SLA</Button></span></Tooltip>}<Tooltip title={moveBlockedReason}><span><Button data-testid="hiring-case-move" type="primary" loading={caseActionBusy} disabled={Boolean(moveBlockedReason)} onClick={advance}>{selectedCase.advanceStatus === 'Pending Approval' ? 'Approval pending' : activeStage?.isTerminal ? 'Complete journey' : 'Move to next stage'}</Button></span></Tooltip></>}{canDelete && <Popconfirm title="Delete this hiring case?" description="Its SLA history and generated process records will be removed. This cannot be undone." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeHiringCase(selectedCase)}><Button danger icon={<DeleteOutlined />}>Delete</Button></Popconfirm>}</Space>}>
      {selectedCase && <><div className="case-hero"><div><span>{selectedCase.pipelineName}</span><h2>{selectedCase.currentStageName || 'Pipeline complete'}</h2><p>SLA anchored at {dateTimeText(selectedCase.slaAnchorAtUtc)} · overall due {dateTimeText(selectedCase.overallDueAtUtc)}</p></div><Tag color={statusColor(selectedCase.status)}>{selectedCase.status}</Tag></div>
        {caseActionError && <Alert data-testid="hiring-case-action-error" showIcon closable type="error" message="Action could not be completed" description={caseActionError} onClose={() => setCaseActionError('')} />}
        {moveBlockedReason && selectedCase.advanceStatus !== 'Pending Approval' && <Alert data-testid="hiring-case-action-guidance" showIcon type="warning" message="Next action needed" description={moveBlockedReason} />}
        {selectedCase.advanceStatus === 'Pending Approval' && <Alert data-testid="hiring-case-approval-pending" showIcon type="info" message="Stage movement awaiting approval" description={selectedCase.advanceMessage || 'The configured approver can action this request from global My Tasks.'} />}
        <Timeline className="case-timeline" items={selectedCase.stages.map(stage => ({ color: stage.isSlaBreached ? 'red' : stage.status === 'Completed' ? 'green' : stage.status === 'Active' ? 'blue' : 'gray', children: <article className={stage.status === 'Active' ? 'active' : ''}><div><Tag>{stage.stakeholderCode || 'No stakeholder'}</Tag><b>{stage.stageName}</b><span>{durationText(stage.targetOffsetMinutes)} target</span></div><p>{stage.status} · due {dateTimeText(stage.dueAtUtc)}{stage.isPaused ? ' · SLA paused' : ''}</p>{stage.pauseHistory.map(pauseRow => <small key={pauseRow.id}>Paused by {pauseRow.pausedByName} on {dateTimeText(pauseRow.pausedAtUtc)} — {pauseRow.reason}{pauseRow.resumedAtUtc ? ` · resumed ${dateTimeText(pauseRow.resumedAtUtc)}` : ''}</small>)}</article> } ))} />
        <Card title="Stage documents & signatures" extra={<Tag color="purple">Normalized requirements</Tag>}>
          {!activeStage?.processDocumentRequirements?.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="This stage has no process-document requirement." />}
          {activeStage?.processDocumentRequirements?.map(requirement => {
            const document = processDocuments.find(row => row.pipelineStageId === activeStage.pipelineStageId && row.documentType === requirement.documentType)
            return <div className="work-order-document" key={requirement.id} data-testid={`hiring-document-${requirement.documentType}`}>
              <div><b>{requirement.documentType.replaceAll('_', ' ')}</b><span>{requirement.isRequired ? 'Required' : 'Optional'}{requirement.requiresSignature ? ' · signature required' : ''}</span></div>
              {!document ? <Button data-testid={`hiring-document-create-${requirement.documentType}`} loading={documentSaving} onClick={() => void prepareProcessDocument(requirement.documentType, requirement.templateId)}>Prepare</Button> : <Tag color={document.status === 'Signed' ? 'green' : 'blue'}>v{document.versionNumber} · {document.status}</Tag>}
              {document && requirement.templateId && document.status !== 'Signed' && <Button loading={documentSaving} onClick={() => void generateProcessDocument(document)}>{document.attachmentPublicId ? 'Regenerate PDF' : 'Generate PDF'}</Button>}
              {document && requirement.requiresSignature && document.status !== 'Signed' && document.hasFinalSignedAttachment && <Button onClick={() => void markProcessDocumentSigned(document)}>Mark signed</Button>}
              {document && requirement.requiresSignature && document.status !== 'Signed' && !document.hasFinalSignedAttachment && <Tag color="orange">Upload signed final</Tag>}
              {document && <EntityAttachmentPanel entityType="RECRUITMENT_PROCESS_DOCUMENT" entityId={document.id} clientId={selectedCase.clientId} moduleCode="RECRUITMENT" formCodes={['PROCESS_DOCUMENT']} title={`${requirement.documentType.replaceAll('_', ' ')} attachment`} description="Generate the draft, obtain committee signatures, then upload the signed final through the secured attachment service." onChanged={() => void viewCase(selectedCase)} />}
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
      title={activeStage?.isTerminal ? 'Complete this hiring journey?' : 'Move to the next stage?'}
      okText={activeStage?.requiresApproval ? 'Send for approval' : activeStage?.isTerminal ? 'Complete journey' : 'Move stage'}
      confirmLoading={caseActionBusy}
      cancelButtonProps={{ disabled: caseActionBusy }}
      onOk={() => void submitAdvance()}
      onCancel={() => { if (!caseActionBusy) { setCaseActionDialog(null); setCaseDialogError('') } }}
      destroyOnClose
    >
      <div className="case-action-confirm">
        <Alert
          showIcon
          type={activeStage?.requiresApproval ? 'info' : 'success'}
          message={activeStage?.requiresApproval ? 'Approval will be requested' : activeStage?.isTerminal ? 'This will complete the journey' : 'All required checks are complete'}
          description={activeStage?.requiresApproval ? 'The configured approver will receive this action in My Tasks. The stage moves only after approval.' : undefined}
        />
        {caseDialogError && <Alert data-testid="hiring-case-dialog-error" showIcon type="error" message={caseDialogError} />}
        <Form.Item label="Movement note (optional)">
          <Input.TextArea data-testid="hiring-case-move-note" rows={3} value={caseActionReason} placeholder="Add a short note for the audit history" onChange={event => { setCaseActionReason(event.target.value); setCaseDialogError('') }} />
        </Form.Item>
      </div>
    </Modal>
  </section>
}
