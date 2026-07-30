import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { ArrowDownOutlined, ArrowRightOutlined, ArrowUpOutlined, DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Collapse, Drawer, Empty, Form, Input, InputNumber, List, Modal, Select, Space, Switch, Tag, message } from 'antd'
import { getClients } from '../services/payrollService'
import { loadSecurityData } from '../services/securityService'
import RecruitmentMasterSelect from './RecruitmentMasterSelect'
import {
  getRecruitmentOrchestrationLookups, getRecruitmentPipeline, getRecruitmentPipelines, getRecruitmentPipelineVersion, getRecruitmentPipelineVersions,
  publishRecruitmentPipelineVersion, saveRecruitmentInterviewCompetency, saveRecruitmentPipelineDefinition, saveRecruitmentPipelineVersion,
} from '../services/recruitmentOrchestrationService'
import type { AuthRole, AuthUser, Client, Drop } from '../types/payroll'
import type {
  RecruitmentOrchestrationLookups, RecruitmentPipelineDefinition, RecruitmentPipelineStage,
  RecruitmentPipelineStageAction, RecruitmentPipelineTransition, RecruitmentPipelineVersion,
  RecruitmentPipelineTransitionRule, RecruitmentStageAttachmentRequirement, RecruitmentStageProcessDocumentRequirement,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'

type Props = { initialClientId?: number; onSaved?: (pipeline: RecruitmentPipelineDefinition) => void; dropdowns?: Drop[]; onDropdownsChange?: (rows: Drop[]) => void }
const emptyLookups: RecruitmentOrchestrationLookups = { lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [] }
const stageTypes = ['Screening', 'ATS', 'ExternalForm', 'Documents', 'Interview', 'HR', 'Approval', 'PreOnboarding', 'Offer', 'Joining', 'Rejected', 'Withdrawn', 'Completed']
const stageColors: Record<string, string> = { Screening: '#6b4eff', ATS: '#2563eb', Interview: '#0e9f6e', HR: '#9333ea', Approval: '#d97706', ExternalForm: '#7c3aed', Documents: '#0891b2', Offer: '#db2777', PreOnboarding: '#4f46e5', Joining: '#16a34a', Rejected: '#b42318', Withdrawn: '#667085', Completed: '#15803d' }
const localId = () => -Math.floor(Date.now() + Math.random() * 100000)
const code = (value: string) => value.toUpperCase().trim().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '')

export default function RecruitmentPipelineDesigner({ initialClientId = 0, onSaved, dropdowns = [], onDropdownsChange = () => undefined }: Props) {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(initialClientId)
  const [loadedClientId, setLoadedClientId] = useState(0)
  const [pipelines, setPipelines] = useState<RecruitmentPipelineDefinition[]>([])
  const [lookups, setLookups] = useState(emptyLookups)
  const [users, setUsers] = useState<AuthUser[]>([])
  const [roles, setRoles] = useState<AuthRole[]>([])
  const [definition, setDefinition] = useState<RecruitmentPipelineDefinition | null>(null)
  const [version, setVersion] = useState<RecruitmentPipelineVersion | null>(null)
  const [selectedStageId, setSelectedStageId] = useState<number | null>(null)
  const [stageDrawer, setStageDrawer] = useState(false)
  const [saving, setSaving] = useState(false)
  const [publishDialogOpen, setPublishDialogOpen] = useState(false)
  const [publishing, setPublishing] = useState(false)
  const [publishError, setPublishError] = useState('')
  const [competencyOpen, setCompetencyOpen] = useState(false)
  const [competencySaving, setCompetencySaving] = useState(false)
  const [competencyDraft, setCompetencyDraft] = useState({ competencyCode: '', competencyName: '', description: '' })
  const stageFlowRef = useRef<HTMLDivElement | null>(null)
  const loadRequestRef = useRef(0)

  const load = async (scope: number) => {
    if (!scope) return
    const requestId = ++loadRequestRef.current
    const [rows, options] = await Promise.all([getRecruitmentPipelines(scope), getRecruitmentOrchestrationLookups(scope)])
    if (requestId !== loadRequestRef.current) return
    setPipelines(rows); setLookups(options); setLoadedClientId(scope)
  }
  useEffect(() => {
    void Promise.all([getClients(), loadSecurityData()]).then(([rows, security]) => {
      setClients(rows); setUsers(security.users); setRoles(security.roles)
      if (rows.length) setClientId(current => current || rows[0].id)
    })
  }, [])
  useEffect(() => {
    setLoadedClientId(0)
    if (clientId) void load(clientId)
    else loadRequestRef.current += 1
    setDefinition(null); setVersion(null)
  }, [clientId])

  const readOnly = version?.status === 'Published' || version?.status === 'Retired'
  const selectedStage = version?.stages.find(row => row.id === selectedStageId) ?? null
  const selectPipeline = async (id: number) => {
    const [detail, versions] = await Promise.all([getRecruitmentPipeline(id), getRecruitmentPipelineVersions(id)])
    if (!detail) return message.error('Unable to load pipeline.')
    const row = detail.definition
    const ordered = [...(detail.versions.length ? detail.versions : versions)].sort((a, b) => b.versionNumber - a.versionNumber)
    const selectedHeader = ordered.find(item => item.status === 'Draft')
      ?? ordered.find(item => item.id === row.currentPublishedVersionId)
      ?? ordered[0]
      ?? blankVersion(row.id)
    const selected = selectedHeader.id ? await getRecruitmentPipelineVersion(selectedHeader.id) ?? selectedHeader : selectedHeader
    setDefinition(row); setVersion(selected); setSelectedStageId(selected.stages[0]?.id ?? null)
  }
  const startNew = () => {
    if (!clientId) return message.warning('Select a client first.')
    const row: RecruitmentPipelineDefinition = { id: 0, clientId, clientName: clients.find(item => item.id === clientId)?.name ?? '', pipelineCode: '', pipelineName: '', description: '', currentPublishedVersionId: null, isActive: true, versions: [] }
    const draft = blankVersion(0); setDefinition(row); setVersion(draft); setSelectedStageId(draft.stages[0]?.id ?? null)
  }
  const beginRevision = () => {
    if (!definition || !version) return
    const draft = cloneVersion(version, definition.id); setVersion(draft); setSelectedStageId(draft.stages[0]?.id ?? null)
  }
  const patchDefinition = (patch: Partial<RecruitmentPipelineDefinition>) => setDefinition(current => current ? { ...current, ...patch } : current)
  const patchVersion = (patch: Partial<RecruitmentPipelineVersion>) => setVersion(current => current ? { ...current, ...patch } : current)
  const patchStage = (stageId: number, patch: Partial<RecruitmentPipelineStage>) => patchVersion({ stages: version!.stages.map(row => row.id === stageId ? { ...row, ...patch } : row) })
  const saveCompetency = async () => {
    if (!clientId || !competencyDraft.competencyCode.trim() || !competencyDraft.competencyName.trim()) return message.warning('Enter component code and name.')
    setCompetencySaving(true)
    const response = await saveRecruitmentInterviewCompetency({ id: 0, clientId, competencyCode: code(competencyDraft.competencyCode), competencyName: competencyDraft.competencyName.trim(), description: competencyDraft.description.trim(), isActive: true })
    setCompetencySaving(false)
    if (!response.ok) return
    await load(clientId)
    setCompetencyDraft({ competencyCode: '', competencyName: '', description: '' })
    setCompetencyOpen(false)
  }
  const addStage = () => {
    if (!version || readOnly) return
    const stage = newStage(version.stages.length + 1)
    const previous = version.stages.at(-1)
    const carryTerminal = Boolean(previous?.isTerminal)
    const stages = [...version.stages.map(row => carryTerminal && row.id === previous?.id ? { ...row, isTerminal: false } : row), { ...stage, isTerminal: carryTerminal }]
    const transitions = previous
      ? [...version.transitions, newTransition(version.id, previous, stage, version.transitions.length + 1)]
      : version.transitions
    patchVersion({ stages, transitions })
    setSelectedStageId(stage.id)
    setStageDrawer(true)
    window.setTimeout(() => stageFlowRef.current?.scrollTo({ left: stageFlowRef.current.scrollWidth, behavior: 'smooth' }), 0)
  }
  const removeStage = (stageId: number) => Modal.confirm({ title: 'Delete pipeline stage?', content: 'Draft transitions connected to this stage will also be removed.', okText: 'Delete', okButtonProps: { danger: true }, onOk: () => { const stages = version!.stages.filter(row => row.id !== stageId).map((row, index) => ({ ...row, stageNumber: index + 1, displayOrder: index + 1 })); patchVersion({ stages, transitions: version!.transitions.filter(row => row.fromStageId !== stageId && row.toStageId !== stageId) }); setSelectedStageId(stages[0]?.id ?? null); setStageDrawer(false) } })
  const moveStage = (stageId: number, delta: number) => { const rows = [...version!.stages]; const index = rows.findIndex(row => row.id === stageId); const target = index + delta; if (index < 0 || target < 0 || target >= rows.length) return; [rows[index], rows[target]] = [rows[target], rows[index]]; patchVersion({ stages: rows.map((row, rowIndex) => ({ ...row, stageNumber: rowIndex + 1, displayOrder: rowIndex + 1 })) }) }
  const setInitial = (stageId: number) => patchVersion({ stages: version!.stages.map(row => ({ ...row, isInitial: row.id === stageId })) })
  const addTransition = () => { if (!version || version.stages.length < 2 || readOnly) return; const from = selectedStage ?? version.stages[0]; const to = version.stages.find(row => row.id !== from.id)!; patchVersion({ transitions: [...version.transitions, newTransition(version.id, from, to, version.transitions.length + 1)] }) }
  const useStandardFlow = () => {
    if (!version || readOnly) return
    const next = standardHiringFlow(version.id)
    patchVersion(next)
    setSelectedStageId(next.stages[0].id)
    message.success('Standard hiring stages prepared. Review them and save the draft when ready.')
    window.setTimeout(() => stageFlowRef.current?.scrollTo({ left: 0, behavior: 'smooth' }), 50)
  }
  const patchTransition = (id: number, patch: Partial<RecruitmentPipelineTransition>) => patchVersion({ transitions: version!.transitions.map(row => row.id === id ? { ...row, ...patch } : row) })
  const removeTransition = (id: number) => patchVersion({ transitions: version!.transitions.filter(row => row.id !== id).map((row, index) => ({ ...row, displayOrder: index + 1 })) })

  const save = async () => {
    if (!definition || !version || readOnly) return
    const error = validate(definition, version); if (error) return message.warning(error)
    setSaving(true)
    const definitionResponse = await saveRecruitmentPipelineDefinition({ id: definition.id, clientId: definition.clientId, pipelineCode: code(definition.pipelineCode || definition.pipelineName), pipelineName: definition.pipelineName.trim(), description: definition.description.trim(), isActive: definition.isActive })
    if (!definitionResponse.ok || !definitionResponse.data) { setSaving(false); return }
    const versionResponse = await saveRecruitmentPipelineVersion(definitionResponse.data.id, { ...version, pipelineDefinitionId: definitionResponse.data.id })
    setSaving(false)
    if (!versionResponse.ok || !versionResponse.data) return
    onSaved?.(definitionResponse.data); await load(definition.clientId); await selectPipeline(definitionResponse.data.id)
  }
  const publish = () => {
    if (!definition || !version?.id || readOnly) {
      const error = readOnly
        ? 'This pipeline version is already published. Create the next version to make changes.'
        : 'Save the pipeline draft successfully before publishing.'
      setPublishError(error)
      return message.error(error)
    }
    const error = validate(definition, version)
    if (error) {
      setPublishError(error)
      return message.error(error)
    }
    setPublishError('')
    setPublishDialogOpen(true)
  }
  const confirmPublish = async () => {
    if (!definition || !version?.id) return
    setPublishing(true)
    const response = await publishRecruitmentPipelineVersion(version.id)
    setPublishing(false)
    if (!response.ok) {
      setPublishError(response.error || 'Pipeline version could not be published.')
      return
    }
    setPublishDialogOpen(false)
    setPublishError('')
    await load(definition.clientId)
    await selectPipeline(definition.id)
  }

  const transitionOptions = version?.stages.map(row => ({ value: row.id, label: `${row.displayOrder}. ${row.stageName}` })) ?? []
  const transitionWorkflowOptions = lookups.workflows
    .filter(row => row.isActive && row.resourceType === 'RecruitmentPipelineTransition' && (row.clientId == null || row.clientId === clientId))
    .map(row => ({ value: row.id, label: row.name }))
  return <section className="orchestration-shell">
    <div className="orchestration-toolbar"><div><span className="orchestration-kicker">Recruitment setup</span><h2 className="orchestration-title">Hiring Pipeline Designer</h2><p className="orchestration-subtitle">Normalized stages, approvals, forms, secure documents, interviews and SLA controls.</p></div><div><Select data-testid="pipeline-client" value={clientId || undefined} placeholder="Select client" options={clients.map(row => ({ value: row.id, label: row.name }))} onChange={value => { setLoadedClientId(0); setClientId(value) }} showSearch optionFilterProp="label" /><Button data-testid="pipeline-competencies" disabled={!clientId} onClick={() => setCompetencyOpen(true)}>Score components</Button><Button data-testid="pipeline-new" type="primary" icon={<PlusOutlined />} onClick={startNew}>New pipeline</Button></div></div>
    <div className="pipeline-designer-layout"><Card size="small" className="form-builder-library" data-testid="pipeline-library" data-loaded-client-id={loadedClientId} title={`Pipelines (${pipelines.length})`}><List dataSource={pipelines} locale={{ emptyText: 'No pipelines for this client.' }} renderItem={row => <List.Item className={definition?.id === row.id ? 'active' : ''} onClick={() => void selectPipeline(row.id)}><List.Item.Meta title={row.pipelineName} description={<><span>{row.pipelineCode}</span><br /><Tag color={row.currentPublishedVersionId ? 'green' : 'orange'}>{row.currentPublishedVersionId ? 'Published' : 'Draft only'}</Tag></>} /></List.Item>} /></Card>
      {!definition || !version ? <Card><Empty description="Select a pipeline or create a new one." /></Card> : <div className="form-builder-canvas">
        <Card size="small"><div className="orchestration-toolbar"><Space wrap><Tag color={version.status === 'Published' ? 'green' : version.status === 'Retired' ? 'default' : 'gold'}>v{version.versionNumber} · {version.status}</Tag><Switch disabled={readOnly} checked={definition.isActive} onChange={isActive => patchDefinition({ isActive })} checkedChildren="Active" unCheckedChildren="Inactive" /></Space><Space>{readOnly && <Button onClick={beginRevision}>Create next version</Button>}<Button data-testid="pipeline-save" loading={saving} disabled={readOnly} onClick={() => void save()}>Save draft</Button><Button data-testid="pipeline-publish" type="primary" disabled={readOnly} onClick={publish}>Publish</Button></Space></div><div className="form-builder-meta"><Form.Item label="Pipeline name" required><Input data-testid="pipeline-name" disabled={readOnly} value={definition.pipelineName} onChange={event => patchDefinition({ pipelineName: event.target.value })} /></Form.Item><Form.Item label="Pipeline code" required><Input data-testid="pipeline-code" disabled={readOnly} value={definition.pipelineCode} onChange={event => patchDefinition({ pipelineCode: code(event.target.value) })} /></Form.Item><Form.Item label="Pipeline scope" required><Select data-testid="pipeline-scope" disabled={readOnly} value={version.scopeType ?? 'Application'} options={[{ value: 'Application', label: 'Candidate / application' }, { value: 'Position', label: 'Work-order position' }, { value: 'Hybrid', label: 'Position + candidate flow' }]} onChange={scopeType => patchVersion({ scopeType })} /></Form.Item><Form.Item label="SLA basis" required><Select data-testid="pipeline-sla-mode" disabled={readOnly} value={version.slaMode ?? 'StageEntry'} options={[{ value: 'StageEntry', label: 'From each stage entry' }, { value: 'CumulativeFromAnchor', label: 'Cumulative from work order' }]} onChange={slaMode => patchVersion({ slaMode })} /></Form.Item>{version.slaMode === 'CumulativeFromAnchor' && <Form.Item label="Overall SLA (days)" required><InputNumber data-testid="pipeline-overall-sla" disabled={readOnly} min={0.01} precision={2} value={(version.overallSlaMinutes ?? 0) / 1440 || undefined} onChange={value => patchVersion({ overallSlaMinutes: Math.round(Number(value || 0) * 1440) })} /></Form.Item>}<Form.Item className="wide" label="Description"><Input data-testid="pipeline-description" disabled={readOnly} value={definition.description} onChange={event => patchDefinition({ description: event.target.value })} /></Form.Item></div></Card>
        {publishError && <Alert type="error" showIcon closable message="Pipeline version cannot be published" description={publishError} onClose={() => setPublishError('')} />}
        <Card size="small" title="Ordered stages" extra={<Space><Button disabled={readOnly} onClick={useStandardFlow}>Use standard flow</Button><Button data-testid="pipeline-add-stage" icon={<PlusOutlined />} disabled={readOnly} onClick={addStage}>Add stage</Button></Space>}><div ref={stageFlowRef} className="pipeline-stage-flow" data-testid="pipeline-stage-flow">{version.stages.map((stage, index) => <Card key={stage.id} data-testid={`pipeline-stage-${stage.stageCode || index + 1}`} size="small" style={{ '--stage-color': stageColors[stage.stageType] || '#6b4eff' } as CSSProperties} className={`pipeline-stage-card ${selectedStageId === stage.id ? 'active' : ''}`} onClick={() => setSelectedStageId(stage.id)}><div><span className="stage-order">{index + 1}</span><strong>{stage.stageName}</strong></div><p>{stage.stageType}{stage.isInitial ? ' · Initial' : ''}{stage.isTerminal ? ' · Terminal' : ''}</p><div className="stage-facts"><span>SLA <b>{formatDuration(stage.slaDurationMinutes)}</b></span><span>{stage.requiresApproval ? 'Approval' : 'No approval'}</span></div><Space wrap onClick={event => event.stopPropagation()}><Button size="small" icon={<EditOutlined />} onClick={() => { setSelectedStageId(stage.id); setStageDrawer(true) }}>Configure</Button><Button size="small" aria-label={`Move ${stage.stageName} up`} icon={<ArrowUpOutlined />} disabled={readOnly || !index} onClick={() => moveStage(stage.id, -1)} /><Button size="small" aria-label={`Move ${stage.stageName} down`} icon={<ArrowDownOutlined />} disabled={readOnly || index === version.stages.length - 1} onClick={() => moveStage(stage.id, 1)} /><Button size="small" danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => removeStage(stage.id)}>Delete</Button></Space></Card>)}</div></Card>
        <Card size="small" title="Allowed transitions" extra={<Button icon={<PlusOutlined />} disabled={readOnly || version.stages.length < 2} onClick={addTransition}>Add transition</Button>}>
          {!version.transitions.length && <Alert type="info" showIcon message="Add controlled transitions. Candidates cannot skip an unconfigured stage." />}
          {version.transitions.map(transition => <div className="pipeline-transition-card" key={transition.id}>
            <div className="pipeline-transition-row">
              <Select disabled={readOnly} value={transition.fromStageId} options={transitionOptions} onChange={fromStageId => patchTransition(transition.id, { fromStageId, fromStageCode: version.stages.find(row => row.id === fromStageId)?.stageCode ?? '' })} />
              <ArrowRightOutlined />
              <Select disabled={readOnly} value={transition.toStageId} options={transitionOptions} onChange={toStageId => patchTransition(transition.id, { toStageId, toStageCode: version.stages.find(row => row.id === toStageId)?.stageCode ?? '' })} />
              <Input disabled={readOnly} value={transition.actionLabel} onChange={event => patchTransition(transition.id, { actionLabel: event.target.value })} placeholder="Action label" />
              <Input disabled={readOnly} value={transition.outcomeCode} onChange={event => patchTransition(transition.id, { outcomeCode: code(event.target.value) })} placeholder="Outcome code" />
              <Select disabled={readOnly} allowClear placeholder="Approval workflow" value={transition.approvalWorkflowId || undefined} onChange={approvalWorkflowId => patchTransition(transition.id, { approvalWorkflowId: approvalWorkflowId || null })} options={transitionWorkflowOptions} />
              <Checkbox disabled={readOnly} checked={transition.requiresReason} onChange={event => patchTransition(transition.id, { requiresReason: event.target.checked })}>Reason</Checkbox>
              <Button disabled={readOnly} icon={<PlusOutlined />} onClick={() => patchTransition(transition.id, { rules: [...transition.rules, newTransitionRule(transition.id, transition.rules.length + 1)] })}>Rule</Button>
              <Button danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => removeTransition(transition.id)} />
            </div>
            <TransitionRuleEditor readOnly={readOnly} rules={transition.rules} onChange={rules => patchTransition(transition.id, { rules })} />
          </div>)}
        </Card>
      </div>}
    </div>
    <Modal open={publishDialogOpen} title="Publish pipeline version?" okText="Publish version" confirmLoading={publishing} onOk={() => void confirmPublish()} onCancel={() => setPublishDialogOpen(false)}>
      <p>Assigned job postings keep their current immutable version. New applications use this published version.</p>
    </Modal>
    <Drawer width={760} title="Pipeline stage" open={stageDrawer && !!selectedStage} onClose={() => setStageDrawer(false)}>{selectedStage && <StageProperties stage={selectedStage} lookups={lookups} users={users} roles={roles} clientId={definition?.clientId ?? 0} clientName={clients.find(row => row.id === definition?.clientId)?.name || ''} cumulativeSla={version?.slaMode === 'CumulativeFromAnchor'} dropdowns={dropdowns} onDropdownsChange={onDropdownsChange} readOnly={readOnly} patch={value => patchStage(selectedStage.id, value)} makeInitial={() => setInitial(selectedStage.id)} />}</Drawer>
    <Modal title="Interview score components" open={competencyOpen} onCancel={() => setCompetencyOpen(false)} onOk={() => void saveCompetency()} okText="Save component" confirmLoading={competencySaving}>
      <Alert showIcon type="info" message="Client-scoped scoring library" description="Create normalized components once, then reuse them in any interview stage with stage-specific maximum and minimum scores." />
      <div style={{ margin: '16px 0' }} data-testid="pipeline-competency-library"><Space wrap>{(lookups.interviewCompetencies ?? []).filter(row => row.isActive).map(row => <Tag key={row.id}>{row.competencyName} ({row.competencyCode})</Tag>)}</Space></div>
      <Form layout="vertical"><Form.Item label="Component code" required><Input data-testid="pipeline-competency-code" value={competencyDraft.competencyCode} onChange={event => setCompetencyDraft(current => ({ ...current, competencyCode: code(event.target.value) }))} /></Form.Item><Form.Item label="Component name" required><Input data-testid="pipeline-competency-name" value={competencyDraft.competencyName} onChange={event => setCompetencyDraft(current => ({ ...current, competencyName: event.target.value }))} /></Form.Item><Form.Item label="Description"><Input.TextArea data-testid="pipeline-competency-description" rows={3} value={competencyDraft.description} onChange={event => setCompetencyDraft(current => ({ ...current, description: event.target.value }))} /></Form.Item></Form>
    </Modal>
  </section>
}

function StageProperties({ stage, lookups, users, roles, clientId, clientName, cumulativeSla, dropdowns, onDropdownsChange, readOnly, patch, makeInitial }: { stage: RecruitmentPipelineStage; lookups: RecruitmentOrchestrationLookups; users: AuthUser[]; roles: AuthRole[]; clientId: number; clientName: string; cumulativeSla: boolean; dropdowns: Drop[]; onDropdownsChange: (rows: Drop[]) => void; readOnly: boolean; patch: (value: Partial<RecruitmentPipelineStage>) => void; makeInitial: () => void }) {
  const addDocument = () => patch({ attachmentRequirements: [...stage.attachmentRequirements, { id: localId(), pipelineStageId: stage.id, attachmentFieldConfigurationId: 0, isRequired: true, minimumFileCount: 1, maximumFileCount: 1, requiresVerification: false, displayOrder: stage.attachmentRequirements.length + 1 }] })
  const patchDocument = (id: number, value: Partial<RecruitmentStageAttachmentRequirement>) => patch({ attachmentRequirements: stage.attachmentRequirements.map(row => row.id === id ? { ...row, ...value } : row) })
  const removeDocument = (id: number) => patch({ attachmentRequirements: stage.attachmentRequirements.filter(row => row.id !== id).map((row, index) => ({ ...row, displayOrder: index + 1 })) })
  const addProcessDocument = () => patch({ processDocumentRequirements: [...(stage.processDocumentRequirements ?? []), { id: localId(), pipelineStageId: stage.id, documentType: '', templateId: null, isRequired: true, requiresSignature: false, displayOrder: (stage.processDocumentRequirements?.length ?? 0) + 1 }] })
  const patchProcessDocument = (id: number, value: Partial<RecruitmentStageProcessDocumentRequirement>) => patch({ processDocumentRequirements: (stage.processDocumentRequirements ?? []).map(row => row.id === id ? { ...row, ...value } : row) })
  const removeProcessDocument = (id: number) => patch({ processDocumentRequirements: (stage.processDocumentRequirements ?? []).filter(row => row.id !== id).map((row, index) => ({ ...row, displayOrder: index + 1 })) })
  const addAction = () => patch({ actions: [...stage.actions, { id: localId(), pipelineStageId: stage.id, triggerEvent: 'OnEntry', actionCode: 'SEND_NOTIFICATION', executionOrder: stage.actions.length + 1, isBlocking: false, workflowId: null, templateId: null, isActive: true, recipients: [] }] })
  const patchAction = (id: number, value: Partial<RecruitmentPipelineStageAction>) => patch({ actions: stage.actions.map(row => row.id === id ? { ...row, ...value } : row) })
  const removeAction = (id: number) => patch({ actions: stage.actions.filter(row => row.id !== id).map((row, index) => ({ ...row, executionOrder: index + 1 })) })
  const moveAction = (id: number, delta: number) => { const rows = [...stage.actions]; const index = rows.findIndex(row => row.id === id); const target = index + delta; if (index < 0 || target < 0 || target >= rows.length) return; [rows[index], rows[target]] = [rows[target], rows[index]]; patch({ actions: rows.map((row, rowIndex) => ({ ...row, executionOrder: rowIndex + 1 })) }) }
  const addRecipient = (action: RecruitmentPipelineStageAction) => patchAction(action.id, { recipients: [...(action.recipients ?? []), { id: localId(), stageActionId: action.id, recipientType: 'Candidate', userId: null, roleCode: '', emailAddress: '', displayOrder: (action.recipients?.length ?? 0) + 1, isActive: true }] })
  const patchRecipient = (action: RecruitmentPipelineStageAction, recipientId: number, value: Partial<NonNullable<RecruitmentPipelineStageAction['recipients']>[number]>) => patchAction(action.id, { recipients: (action.recipients ?? []).map(row => row.id === recipientId ? { ...row, ...value } : row) })
  const removeRecipient = (action: RecruitmentPipelineStageAction, recipientId: number) => patchAction(action.id, { recipients: (action.recipients ?? []).filter(row => row.id !== recipientId).map((row, index) => ({ ...row, displayOrder: index + 1 })) })
  const formVersions = lookups.forms.filter(form => form.currentPublishedVersionId).map(form => ({ value: form.currentPublishedVersionId!, label: `${form.formName} (${form.formCode})` }))
  const needsDocuments = ['Documents', 'PreOnboarding'].includes(stage.stageType)
  const external = stage.externalFormConfiguration
  const interview = stage.interviewConfiguration
  const ats = stage.atsConfiguration
  const offer = stage.offerConfiguration
  const defaultPanelMembers = stage.defaultPanelMembers ?? []
  const workflowOptions = lookups.workflows
    .filter(row => row.isActive && row.resourceType === 'RecruitmentPipelineTransition' && (row.clientId == null || row.clientId === clientId))
    .map(row => ({ value: row.id, label: `${row.name} (${row.code})` }))
  const stageActionWorkflowOptions = lookups.workflows
    .filter(row => row.isActive && row.resourceType === 'RecruitmentPipelineStageAction' && (row.clientId == null || row.clientId === clientId))
    .map(row => ({ value: row.id, label: `${row.name} (${row.code})` }))
  const offerWorkflowOptions = lookups.workflows
    .filter(row => row.isActive && row.resourceType === 'RecruitmentOffer' && (row.clientId == null || row.clientId === clientId))
    .map(row => ({ value: row.id, label: `${row.name} (${row.code})` }))
  const availableUsers = users.filter(row => row.isActive && (!row.clientId || Number(row.clientId) === clientId))
  return <Form layout="vertical">
    <div className="recruitment-config-heading"><b>Stage essentials</b><span>Name the step, choose its purpose and set the expected completion time.</span></div>
    <div className="field-property-grid"><Form.Item label="Stage name" required><Input data-testid="pipeline-stage-name" disabled={readOnly} value={stage.stageName} onChange={event => patch({ stageName: event.target.value, stageCode: stage.id < 0 ? code(event.target.value) : stage.stageCode })} /></Form.Item><Form.Item label="Stage type"><Select data-testid="pipeline-stage-type" disabled={readOnly} showSearch optionFilterProp="label" value={stage.stageType} options={stageTypes.map(value => ({ value, label: value.replace(/([A-Z])/g, ' $1').trim() }))} onChange={stageType => patch(stageTypePatch(stage, stageType))} /></Form.Item>{cumulativeSla ? <Form.Item label="Cumulative target (days)" required><InputNumber data-testid="pipeline-stage-target-days" disabled={readOnly} min={0} precision={2} value={stage.targetOffsetMinutes == null ? undefined : stage.targetOffsetMinutes / 1440} onChange={value => patch({ targetOffsetMinutes: value == null ? null : Math.round(Number(value) * 1440) })} /></Form.Item> : <Form.Item label="Complete within (hours)"><InputNumber disabled={readOnly} min={0} value={stage.slaDurationMinutes / 60} onChange={value => patch({ slaDurationMinutes: Math.round(Number(value || 0) * 60) })} /></Form.Item>}</div>
    <Collapse className="recruitment-advanced-collapse"><Collapse.Panel key="stage-controls" header="Advanced stage controls"><div className="field-property-grid"><Form.Item label="Stage code" required><Input data-testid="pipeline-stage-code" disabled={readOnly} value={stage.stageCode} onChange={event => patch({ stageCode: code(event.target.value) })} /></Form.Item><Form.Item label="Stakeholder / owner"><Input data-testid="pipeline-stage-stakeholder" disabled={readOnly} value={stage.stakeholderCode ?? ''} placeholder="GAD, UIDAI, CLIENT_HR" onChange={event => patch({ stakeholderCode: code(event.target.value) })} /></Form.Item><Form.Item label="Warning before due (hours)"><InputNumber disabled={readOnly} min={0} value={stage.slaWarningMinutes / 60} onChange={value => patch({ slaWarningMinutes: Math.round(Number(value || 0) * 60) })} /></Form.Item><Form.Item label="Pause effect"><Select data-testid="pipeline-stage-pause-behavior" disabled={readOnly || stage.allowPause === false} value={stage.pauseBehavior ?? 'ShiftStageAndOverall'} options={[{ value: 'ShiftStageAndOverall', label: 'Shift stage + overall due' }, { value: 'ShiftStageOnly', label: 'Shift this stage only' }, { value: 'NoShift', label: 'Audit pause; do not shift due' }]} onChange={pauseBehavior => patch({ pauseBehavior })} /></Form.Item><Form.Item className="wide" label="Approval workflow"><Select data-testid="pipeline-stage-approval-workflow" disabled={readOnly} allowClear showSearch optionFilterProp="label" value={stage.approvalWorkflowId || undefined} onChange={approvalWorkflowId => patch({ approvalWorkflowId: approvalWorkflowId || null, requiresApproval: Boolean(approvalWorkflowId) })} options={workflowOptions} placeholder="No approval required" /></Form.Item><Form.Item className="wide"><Space direction="vertical"><Checkbox disabled={readOnly || stage.isInitial} checked={stage.isInitial} onChange={event => event.target.checked && makeInitial()}>Initial stage</Checkbox><Checkbox data-testid="pipeline-stage-terminal" disabled={readOnly} checked={stage.isTerminal} onChange={event => patch({ isTerminal: event.target.checked })}>Terminal stage</Checkbox><Checkbox disabled={readOnly} checked={stage.calendarEnabled} onChange={event => patch({ calendarEnabled: event.target.checked })}>Calendar enabled</Checkbox><Checkbox disabled={readOnly} checked={stage.allowSkip} onChange={event => patch({ allowSkip: event.target.checked })}>Allow controlled skip</Checkbox><Checkbox data-testid="pipeline-stage-allow-pause" disabled={readOnly} checked={stage.allowPause ?? true} onChange={event => patch({ allowPause: event.target.checked })}>Allow SLA pause with reason</Checkbox><Checkbox disabled={readOnly} checked={stage.isActive} onChange={event => patch({ isActive: event.target.checked })}>Active</Checkbox></Space></Form.Item></div></Collapse.Panel></Collapse>
    {stage.stageType === 'ATS' && ats && <Card size="small" title="ATS stage"><div className="field-property-grid"><Form.Item className="wide" label="Scoring profile"><Select data-testid="pipeline-ats-profile" disabled={readOnly} allowClear value={ats.scoringProfileId || undefined} onChange={scoringProfileId => patch({ atsConfiguration: { ...ats, scoringProfileId: scoringProfileId || null } })} options={(lookups.atsProfiles ?? []).filter(row => row.isActive).map(row => ({ value: row.id, label: `${row.profileName} (${row.profileCode})` }))} /></Form.Item><Form.Item label="Advance score"><InputNumber data-testid="pipeline-ats-advance-score" disabled={readOnly} min={0} max={100} value={ats.minimumAdvanceScore} onChange={value => patch({ atsConfiguration: { ...ats, minimumAdvanceScore: Number(value || 0) } })} /></Form.Item><Form.Item label="Reject up to"><InputNumber data-testid="pipeline-ats-reject-score" disabled={readOnly} min={0} max={100} value={ats.maximumRejectScore} onChange={value => patch({ atsConfiguration: { ...ats, maximumRejectScore: Number(value || 0) } })} /></Form.Item><Form.Item className="wide"><Space wrap><Checkbox data-testid="pipeline-ats-auto-score" disabled={readOnly} checked={ats.autoScoreOnEntry} onChange={event => patch({ atsConfiguration: { ...ats, autoScoreOnEntry: event.target.checked } })}>Auto-score</Checkbox><Checkbox data-testid="pipeline-ats-auto-advance" disabled={readOnly} checked={ats.autoAdvance} onChange={event => patch({ atsConfiguration: { ...ats, autoAdvance: event.target.checked } })}>Auto-advance</Checkbox><Checkbox data-testid="pipeline-ats-auto-reject" disabled={readOnly} checked={ats.autoReject} onChange={event => patch({ atsConfiguration: { ...ats, autoReject: event.target.checked } })}>Auto-reject</Checkbox><Checkbox data-testid="pipeline-ats-human-confirmation" disabled={readOnly} checked={ats.requireHumanConfirmation} onChange={event => patch({ atsConfiguration: { ...ats, requireHumanConfirmation: event.target.checked } })}>Human confirmation</Checkbox></Space></Form.Item></div></Card>}
    {external && <Card size="small" title={needsDocuments ? 'Secure document collection form' : 'Secure external form'}><div className="field-property-grid"><Form.Item className="wide" label="Published form version" required><Select disabled={readOnly} showSearch optionFilterProp="label" value={external.formVersionId || undefined} onChange={formVersionId => patch({ externalFormConfiguration: { ...external, formVersionId } })} options={formVersions} /></Form.Item><Form.Item label="Token validity (hours)"><InputNumber disabled={readOnly} min={1} value={external.actionTokenValidityMinutes / 60} onChange={value => patch({ externalFormConfiguration: { ...external, actionTokenValidityMinutes: Math.round(Number(value || 1) * 60) } })} /></Form.Item><Form.Item label="Maximum completed submissions"><InputNumber disabled={readOnly} min={1} value={external.actionTokenMaximumUses} onChange={value => patch({ externalFormConfiguration: { ...external, actionTokenMaximumUses: Number(value || 1) } })} /></Form.Item><Form.Item className="wide"><Space wrap><Checkbox disabled={readOnly} checked={external.submissionRequired} onChange={event => patch({ externalFormConfiguration: { ...external, submissionRequired: event.target.checked } })}>Submission required before stage exit</Checkbox><Checkbox disabled={readOnly} checked={external.allowSaveDraft} onChange={event => patch({ externalFormConfiguration: { ...external, allowSaveDraft: event.target.checked } })}>Allow candidate to save draft</Checkbox></Space></Form.Item></div>{needsDocuments && <Alert showIcon type="info" message="The selected form must contain upload fields bound to every requested global document below." />}</Card>}
    {stage.stageType === 'Interview' && interview && <Card size="small" title="Interview round">
      <div className="field-property-grid"><Form.Item label="Round number"><InputNumber disabled={readOnly} min={1} value={interview.roundNumber} onChange={value => patch({ interviewConfiguration: { ...interview, roundNumber: Number(value || 1) } })} /></Form.Item><Form.Item label="Interview type"><RecruitmentMasterSelect masterType="Interview Type" clientId={clientId} clientName={clientName} value={interview.interviewType} values={Array.from(new Set(dropdowns.filter(row => row.isActive && row.type === 'Interview Type' && (row.clientId === 0 || row.clientId === clientId)).map(row => row.value)))} dropdowns={dropdowns} onDropdownsChange={onDropdownsChange} onChange={interviewType => patch({ interviewConfiguration: { ...interview, interviewType } })} disabled={readOnly} testId="pipeline-interview-type" /></Form.Item><Form.Item label="Duration (minutes)"><InputNumber disabled={readOnly} min={5} step={5} value={interview.defaultDurationMinutes} onChange={value => patch({ interviewConfiguration: { ...interview, defaultDurationMinutes: Number(value || 30) } })} /></Form.Item><Form.Item label="Minimum panel"><InputNumber disabled={readOnly} min={1} value={interview.minimumPanelCount} onChange={value => patch({ interviewConfiguration: { ...interview, minimumPanelCount: Number(value || 1) } })} /></Form.Item><Form.Item label="Passing score"><InputNumber disabled={readOnly} min={0} max={100} value={interview.minimumPassingScore} onChange={value => patch({ interviewConfiguration: { ...interview, minimumPassingScore: Number(value || 0) } })} /></Form.Item><Form.Item label="Score input"><Select data-testid="pipeline-interview-score-mode" disabled={readOnly} value={interview.scoreInputMode || 'PercentageWeighted'} onChange={scoreInputMode => patch({ interviewConfiguration: { ...interview, scoreInputMode } })} options={[{ value: 'Points', label: 'Points out of configured maximum' }, { value: 'PercentageWeighted', label: 'Percentage with weights' }]} /></Form.Item><Form.Item label="Panel aggregation"><Select data-testid="pipeline-interview-aggregation" disabled={readOnly} value={interview.panelAggregationMethod || 'Average'} onChange={panelAggregationMethod => patch({ interviewConfiguration: { ...interview, panelAggregationMethod } })} options={[{ value: 'Average', label: 'Average of all panel members' }, { value: 'Median', label: 'Median panel score' }, { value: 'Chairperson', label: 'Chairperson score' }]} /></Form.Item><Form.Item className="wide" label="Default panel members"><Select data-testid="pipeline-default-panel" disabled={readOnly} mode="multiple" showSearch optionFilterProp="label" value={(stage.defaultPanelMembers ?? []).map(row => row.panelUserId)} options={availableUsers.map(user => ({ value: user.id, label: `${user.displayName || user.email} (${user.email})` }))} onChange={ids => patch({ defaultPanelMembers: ids.map((panelUserId, index) => { const current = (stage.defaultPanelMembers ?? []).find(row => row.panelUserId === panelUserId); return current ? { ...current, displayOrder: index + 1 } : { id: localId(), pipelineStageId: stage.id, panelUserId, panelUserName: availableUsers.find(user => user.id === panelUserId)?.displayName ?? '', panelRole: 'Panelist', isRequired: true, displayOrder: index + 1 } }) })} /></Form.Item><Form.Item className="wide" label="Competencies"><Select disabled={readOnly} mode="multiple" value={interview.competencies.map(row => row.competencyId)} onChange={ids => patch({ interviewConfiguration: { ...interview, competencies: equalCompetencies(ids, interview.id, lookups) } })} options={(lookups.interviewCompetencies ?? []).filter(row => row.isActive).map(row => ({ value: row.id, label: row.competencyName }))} /></Form.Item></div>
      {!!defaultPanelMembers.length && <div className="interview-competency-config"><div className="interview-competency-heading"><span>Panel member</span><span>Committee role</span><span>Required</span></div>{defaultPanelMembers.map(panel => <div className="interview-competency-row" key={panel.id}><strong>{panel.panelUserName}</strong><Input data-testid={`pipeline-panel-role-${panel.panelUserId}`} disabled={readOnly} value={panel.panelRole} placeholder="Chairperson / Member / SME" onChange={event => patch({ defaultPanelMembers: defaultPanelMembers.map(row => row.id === panel.id ? { ...row, panelRole: event.target.value } : row) })} /><Checkbox disabled={readOnly} checked={panel.isRequired} onChange={event => patch({ defaultPanelMembers: defaultPanelMembers.map(row => row.id === panel.id ? { ...row, isRequired: event.target.checked } : row) })}>Required</Checkbox></div>)}</div>}
      {!!interview.competencies.length && <div className="interview-competency-config"><div className="interview-competency-heading"><span>Competency</span><span>Weight %</span><span>Minimum score</span></div>{interview.competencies.map(competency => <div className="interview-competency-row" key={competency.id}><strong>{competency.competencyName}</strong><InputNumber disabled={readOnly} min={0.01} max={100} precision={2} value={competency.weightPercent} onChange={weightPercent => patch({ interviewConfiguration: { ...interview, competencies: interview.competencies.map(row => row.id === competency.id ? { ...row, weightPercent: Number(weightPercent || 0) } : row) } })} /><InputNumber disabled={readOnly} min={0} max={100} precision={2} value={competency.minimumScore} onChange={minimumScore => patch({ interviewConfiguration: { ...interview, competencies: interview.competencies.map(row => row.id === competency.id ? { ...row, minimumScore: Number(minimumScore || 0) } : row) } })} /></div>)}</div>}
      <Form.Item><Space><Checkbox disabled={readOnly} checked={interview.feedbackRequired} onChange={event => patch({ interviewConfiguration: { ...interview, feedbackRequired: event.target.checked } })}>Feedback required</Checkbox><Checkbox disabled={readOnly} checked={interview.allowReschedule} onChange={event => patch({ interviewConfiguration: { ...interview, allowReschedule: event.target.checked } })}>Allow reschedule</Checkbox></Space></Form.Item>
    </Card>}
    {stage.stageType === 'Offer' && offer && <Card size="small" title="Offer controls"><div className="field-property-grid">
      <Form.Item className="wide" label="Offer template"><Select disabled={readOnly} allowClear value={offer.offerTemplateId || undefined} onChange={offerTemplateId => patch({ offerConfiguration: { ...offer, offerTemplateId: offerTemplateId || null } })} options={(lookups.templates ?? []).filter(row => row.isActive && row.templateType.toLowerCase().includes('offer')).map(row => ({ value: row.id, label: row.templateName }))} /></Form.Item>
      <Form.Item label="Budget basis"><Select disabled={readOnly} value={offer.budgetBasis} onChange={budgetBasis => patch({ offerConfiguration: { ...offer, budgetBasis } })} options={[{ value: 'ApprovedMaximum', label: 'Approved budget per position' }, { value: 'ApprovedTotal', label: 'Approved total requisition budget' }, { value: 'SalaryRangeMaximum', label: 'Approved salary-range maximum' }]} /></Form.Item>
      <Form.Item label="Maximum variance %"><InputNumber disabled={readOnly} min={0} precision={2} value={offer.maximumVariancePercent} onChange={value => patch({ offerConfiguration: { ...offer, maximumVariancePercent: Number(value || 0) } })} /></Form.Item>
      <Form.Item label="Offer approval workflow"><Select disabled={readOnly} allowClear value={offer.approvalWorkflowId || undefined} onChange={approvalWorkflowId => patch({ offerConfiguration: { ...offer, approvalWorkflowId: approvalWorkflowId || null } })} options={offerWorkflowOptions} /></Form.Item>
      <Form.Item label="Variance approval workflow"><Select disabled={readOnly} allowClear value={offer.varianceApprovalWorkflowId || undefined} onChange={varianceApprovalWorkflowId => patch({ offerConfiguration: { ...offer, varianceApprovalWorkflowId: varianceApprovalWorkflowId || null } })} options={offerWorkflowOptions} /></Form.Item>
      <Form.Item label="Candidate response (days)"><InputNumber disabled={readOnly} min={1} max={365} value={offer.candidateResponseValidityDays} onChange={value => patch({ offerConfiguration: { ...offer, candidateResponseValidityDays: Number(value || 1) } })} /></Form.Item>
      <Form.Item className="wide"><Space direction="vertical"><Checkbox disabled={readOnly} checked={offer.requireApprovalWhenVarianceExceeded} onChange={event => patch({ offerConfiguration: { ...offer, requireApprovalWhenVarianceExceeded: event.target.checked } })}>Use variance workflow when configured tolerance is exceeded</Checkbox><Checkbox disabled={readOnly} checked={offer.requireAcceptedOfferToAdvance} onChange={event => patch({ offerConfiguration: { ...offer, requireAcceptedOfferToAdvance: event.target.checked } })}>Accepted offer required to advance</Checkbox></Space></Form.Item>
    </div></Card>}
    <Card size="small" title="Process documents" extra={<Button data-testid="pipeline-add-process-document" disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addProcessDocument}>Add requirement</Button>}>
      {!(stage.processDocumentRequirements ?? []).length && <Alert showIcon type="info" message="Add only the client-approved MoM, score annexure, proposal, offer or joining documents required at this stage." />}
      {(stage.processDocumentRequirements ?? []).map(document => <div className="field-option-row" key={document.id} style={{ gridTemplateColumns: '1fr 1.4fr auto auto auto' }}>
        <Select data-testid="pipeline-process-document-type" disabled={readOnly} placeholder="Document type" value={document.documentType || undefined} onChange={documentType => patchProcessDocument(document.id, { documentType })} options={['WORK_ORDER', 'JD_ANNEXURE', 'MOM', 'SIGNED_MOM', 'SCORE_ANNEXURE', 'HR_PROPOSAL', 'JOINING_INTIMATION', 'CANDIDATE_PACK'].map(value => ({ value, label: value.replaceAll('_', ' ') }))} />
        <Select data-testid="pipeline-process-document-template" disabled={readOnly} allowClear showSearch optionFilterProp="label" placeholder="Manual attachment / no template" value={document.templateId || undefined} onChange={templateId => patchProcessDocument(document.id, { templateId: templateId || null })} options={(lookups.templates ?? []).filter(row => row.isActive).map(row => ({ value: row.id, label: `${row.templateName} (${row.templateCode})` }))} />
        <Checkbox data-testid="pipeline-process-document-required" disabled={readOnly} checked={document.isRequired} onChange={event => patchProcessDocument(document.id, { isRequired: event.target.checked })}>Required</Checkbox>
        <Checkbox data-testid="pipeline-process-document-signature" disabled={readOnly} checked={document.requiresSignature} onChange={event => patchProcessDocument(document.id, { requiresSignature: event.target.checked })}>Signature</Checkbox>
        <Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeProcessDocument(document.id)} />
      </div>)}
    </Card>
    {needsDocuments && <Card size="small" title="Requested global documents" extra={<Button disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addDocument}>Add document</Button>}>{!stage.attachmentRequirements.length && <Alert showIcon type="info" message="Bind global attachment field configurations; their file limits and permissions remain authoritative." />}{stage.attachmentRequirements.map(document => <div className="field-option-row" key={document.id} style={{ gridTemplateColumns: '1fr 100px 100px auto auto' }}><Select disabled={readOnly} showSearch optionFilterProp="label" value={document.attachmentFieldConfigurationId || undefined} placeholder="Attachment field configuration" onChange={attachmentFieldConfigurationId => { const config = lookups.attachmentFieldConfigurations.find(row => row.id === attachmentFieldConfigurationId); patchDocument(document.id, { attachmentFieldConfigurationId, maximumFileCount: config?.maximumFileCount ?? 1, minimumFileCount: config?.minimumFileCount ?? 1 }) }} options={lookups.attachmentFieldConfigurations.filter(row => row.isActive && (row.clientId === 0 || row.clientId === clientId)).map(row => ({ value: row.id, label: `${row.fieldLabel || row.attributeName} (${row.attributeCode})` }))} /><InputNumber disabled={readOnly} min={0} value={document.minimumFileCount} onChange={minimumFileCount => patchDocument(document.id, { minimumFileCount: Number(minimumFileCount || 0) })} /><InputNumber disabled={readOnly} min={1} value={document.maximumFileCount} onChange={maximumFileCount => patchDocument(document.id, { maximumFileCount: Number(maximumFileCount || 1) })} /><Checkbox disabled={readOnly} checked={document.requiresVerification} onChange={event => patchDocument(document.id, { requiresVerification: event.target.checked })}>Verify</Checkbox><Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeDocument(document.id)} /></div>)}</Card>}
    <Collapse className="recruitment-advanced-collapse"><Collapse.Panel key="automation" header={`Advanced automation (${stage.actions.length})`}><Card bordered={false} size="small" extra={<Button data-testid="pipeline-add-action" disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addAction}>Add action</Button>}>
      {!stage.actions.length && <p className="orchestration-subtitle">Configure relational stage hooks. Mail uses notification templates, approvals use the existing workflow engine, and SLA hooks run automatically.</p>}
      {stage.actions.map((action, actionIndex) => {
        const candidateFacing = ['ExternalForm', 'Documents', 'PreOnboarding', 'Offer'].includes(stage.stageType)
        const actionOptions = ['SEND_NOTIFICATION', 'START_WORKFLOW', ...(candidateFacing ? ['GENERATE_ACTION_LINK'] : []), ...(stage.stageType === 'ATS' ? ['RUN_ATS_SCORE'] : [])]
        const triggerOptions = ['OnEntry', 'OnExit', 'OnSlaWarning', 'OnSlaBreach', 'OnApproval', 'OnProfileBatchForward', ...(candidateFacing ? ['OnSubmission'] : [])]
        return <div className="stage-action-block" key={action.id}><div className="stage-action-row">
          <Select data-testid="pipeline-action-trigger" disabled={readOnly} value={action.triggerEvent} onChange={triggerEvent => patchAction(action.id, { triggerEvent, isBlocking: ['OnEntry', 'OnSubmission'].includes(triggerEvent) ? action.isBlocking : false })} options={triggerOptions.map(value => ({ value, label: value.replace(/([A-Z])/g, ' $1').trim() }))} />
          <Select data-testid="pipeline-action-code" disabled={readOnly} value={action.actionCode} onChange={actionCode => patchAction(action.id, { actionCode, workflowId: actionCode === 'START_WORKFLOW' ? action.workflowId : null, templateId: actionCode === 'SEND_NOTIFICATION' ? action.templateId : null })} options={actionOptions.map(value => ({ value, label: value.replaceAll('_', ' ') }))} />
          {action.actionCode === 'START_WORKFLOW' ? <Select disabled={readOnly} showSearch optionFilterProp="label" placeholder="Approval workflow" value={action.workflowId || undefined} onChange={workflowId => patchAction(action.id, { workflowId: workflowId || null })} options={stageActionWorkflowOptions} /> : action.actionCode === 'SEND_NOTIFICATION' ? <Select data-testid="pipeline-action-template" disabled={readOnly} showSearch optionFilterProp="label" placeholder="Notification template" value={action.templateId || undefined} onChange={templateId => patchAction(action.id, { templateId: templateId || null })} options={(lookups.templates ?? []).filter(row => row.isActive).map(row => ({ value: row.id, label: `${row.templateName} (${row.templateCode})` }))} /> : <span className="stage-action-runtime-note">No extra configuration</span>}
          <Checkbox disabled={readOnly || !['OnEntry', 'OnSubmission'].includes(action.triggerEvent)} checked={action.isBlocking} onChange={event => patchAction(action.id, { isBlocking: event.target.checked })}>Blocking</Checkbox>
          <Space.Compact><Button disabled={readOnly || actionIndex === 0} icon={<ArrowUpOutlined />} onClick={() => moveAction(action.id, -1)} /><Button disabled={readOnly || actionIndex === stage.actions.length - 1} icon={<ArrowDownOutlined />} onClick={() => moveAction(action.id, 1)} /><Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeAction(action.id)} /></Space.Compact>
        </div>{action.actionCode === 'SEND_NOTIFICATION' && <div className="stage-action-recipient-panel"><div><b>Recipients</b><Button size="small" disabled={readOnly} icon={<PlusOutlined />} onClick={() => addRecipient(action)}>Add recipient</Button></div>{!(action.recipients ?? []).length && <Alert showIcon type="info" message="No recipient row yet; existing pipelines retain candidate-email compatibility." />}{(action.recipients ?? []).map(recipient => <div className="stage-action-recipient-row" key={recipient.id}><Select data-testid="pipeline-action-recipient-type" disabled={readOnly} value={recipient.recipientType} options={[{ value: 'Candidate', label: 'Candidate' }, { value: 'InterviewPanelMembers', label: 'Actual interview panel' }, { value: 'StageDefaultPanelMembers', label: 'Default stage panel' }, { value: 'SpecificUser', label: 'Specific user' }, { value: 'UserRole', label: 'Users in role' }, { value: 'HiringRequester', label: 'Hiring requester' }, { value: 'PositionRecruiter', label: 'Position recruiter' }, { value: 'StaticEmail', label: 'Static email' }]} onChange={recipientType => patchRecipient(action, recipient.id, { recipientType, userId: null, roleCode: '', emailAddress: '' })} />{recipient.recipientType === 'SpecificUser' && <Select data-testid="pipeline-recipient-user" disabled={readOnly} showSearch optionFilterProp="label" value={recipient.userId || undefined} placeholder="Select user" options={availableUsers.map(user => ({ value: user.id, label: `${user.displayName || user.email} (${user.email})` }))} onChange={userId => patchRecipient(action, recipient.id, { userId })} />}{recipient.recipientType === 'UserRole' && <Select data-testid="pipeline-recipient-role" disabled={readOnly} showSearch optionFilterProp="label" value={recipient.roleCode || undefined} placeholder="Select role" options={roles.map(role => ({ value: role.code, label: `${role.name} (${role.code})` }))} onChange={roleCode => patchRecipient(action, recipient.id, { roleCode })} />}{recipient.recipientType === 'StaticEmail' && <Input data-testid="pipeline-recipient-email" disabled={readOnly} type="email" value={recipient.emailAddress} placeholder="name@client.org" onChange={event => patchRecipient(action, recipient.id, { emailAddress: event.target.value })} />}{!['SpecificUser', 'UserRole', 'StaticEmail'].includes(recipient.recipientType) && <span className="stage-action-runtime-note">Resolved from live pipeline data</span>}<Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeRecipient(action, recipient.id)} /></div>)}</div>}</div>
      })}
    </Card></Collapse.Panel></Collapse>
  </Form>
}

const transitionRuleTypes = [
  { value: 'ATS_SCORE', label: 'ATS score', valueType: 'number' },
  { value: 'RESUME_REQUIRED', label: 'Resume available', valueType: 'boolean' },
  { value: 'MANDATORY_DOCUMENTS_COMPLETE', label: 'Mandatory documents complete', valueType: 'boolean' },
  { value: 'OFFER_ACCEPTED', label: 'Offer accepted', valueType: 'boolean' },
  { value: 'INTERVIEW_RESULT', label: 'Latest interview result', valueType: 'text' },
  { value: 'INTERVIEW_SCORE', label: 'Latest interview score', valueType: 'number' },
  { value: 'PANEL_FEEDBACK_COMPLETE', label: 'Panel feedback complete', valueType: 'boolean' },
] as const

function TransitionRuleEditor({ rules, readOnly, onChange }: { rules: RecruitmentPipelineTransitionRule[]; readOnly: boolean; onChange: (rules: RecruitmentPipelineTransitionRule[]) => void }) {
  const patchRule = (id: number, value: Partial<RecruitmentPipelineTransitionRule>) => onChange(rules.map(rule => rule.id === id ? { ...rule, ...value } : rule))
  const removeRule = (id: number) => onChange(rules.filter(rule => rule.id !== id).map((rule, index) => ({ ...rule, displayOrder: index + 1 })))
  const changeType = (rule: RecruitmentPipelineTransitionRule, ruleType: string) => {
    const valueType = transitionRuleTypes.find(option => option.value === ruleType)?.valueType
    patchRule(rule.id, {
      ruleType,
      comparisonOperator: valueType === 'number' ? 'GTE' : 'EQ',
      decimalValue: valueType === 'number' ? 0 : null,
      integerValue: null,
      textValue: valueType === 'text' ? 'Passed' : null,
      booleanValue: valueType === 'boolean' ? true : null,
    })
  }
  if (!rules.length) return null
  return <div className="transition-rule-list">
    {rules.map((rule, index) => {
      const valueType = transitionRuleTypes.find(option => option.value === rule.ruleType)?.valueType ?? 'text'
      const operators = valueType === 'number' ? ['EQ', 'NE', 'GT', 'GTE', 'LT', 'LTE'] : valueType === 'text' ? ['EQ', 'NE', 'CONTAINS', 'NOT_CONTAINS'] : ['EQ', 'NE']
      return <div className="transition-rule-row" key={rule.id}>
        <Tag color="purple">Rule {index + 1}</Tag>
        <Select disabled={readOnly} value={rule.ruleType} options={transitionRuleTypes.map(({ value, label }) => ({ value, label }))} onChange={value => changeType(rule, value)} />
        <Select disabled={readOnly} value={rule.comparisonOperator} options={operators.map(value => ({ value, label: value.replace('_', ' ') }))} onChange={comparisonOperator => patchRule(rule.id, { comparisonOperator })} />
        {valueType === 'number' && <InputNumber disabled={readOnly} precision={2} value={rule.decimalValue ?? rule.integerValue ?? 0} onChange={decimalValue => patchRule(rule.id, { decimalValue: Number(decimalValue ?? 0), integerValue: null })} />}
        {valueType === 'text' && <Input disabled={readOnly} value={rule.textValue ?? ''} placeholder="Expected value" onChange={event => patchRule(rule.id, { textValue: event.target.value })} />}
        {valueType === 'boolean' && <Select disabled={readOnly} value={rule.booleanValue ?? true} options={[{ value: true, label: 'Yes' }, { value: false, label: 'No' }]} onChange={booleanValue => patchRule(rule.id, { booleanValue })} />}
        <Input disabled={readOnly} value={rule.errorMessage} placeholder="Message shown when rule fails" onChange={event => patchRule(rule.id, { errorMessage: event.target.value })} />
        <Checkbox disabled={readOnly} checked={rule.isMandatory} onChange={event => patchRule(rule.id, { isMandatory: event.target.checked })}>Blocking</Checkbox>
        <Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeRule(rule.id)} />
      </div>
    })}
  </div>
}

function blankVersion(pipelineDefinitionId: number): RecruitmentPipelineVersion {
  const first = newStage(1, true); const terminal = { ...newStage(2), stageCode: 'REJECTED', stageName: 'Rejected', stageType: 'Rejected', isTerminal: true }
  return { id: 0, pipelineDefinitionId, versionNumber: 1, status: 'Draft', scopeType: 'Application', slaMode: 'StageEntry', overallSlaMinutes: 0, stages: [first, terminal], transitions: [newTransition(0, first, terminal, 1)] }
}
function standardHiringFlow(pipelineVersionId: number): Pick<RecruitmentPipelineVersion, 'stages' | 'transitions'> {
  const specs = [
    { name: 'Application Screening', type: 'Screening', hours: 24 },
    { name: 'ATS Resume Match', type: 'ATS', hours: 4 },
    { name: 'Technical Interview', type: 'Interview', hours: 48 },
    { name: 'HR Review', type: 'HR', hours: 24 },
    { name: 'Offer', type: 'Offer', hours: 48 },
    { name: 'Hired', type: 'Completed', hours: 0 },
  ]
  const stages = specs.map((spec, index) => {
    const source = newStage(index + 1, index === 0)
    const base = { ...source, stageName: spec.name, stageCode: code(spec.name), slaDurationMinutes: spec.hours * 60, slaWarningMinutes: spec.hours > 4 ? 4 * 60 : 60, isInitial: index === 0, isTerminal: index === specs.length - 1 }
    return { ...base, ...stageTypePatch(base, spec.type), isInitial: index === 0, isTerminal: index === specs.length - 1 }
  })
  const labels = ['Review resume match', 'Shortlist for technical interview', 'Move to HR review', 'Prepare offer', 'Mark as hired']
  const transitions = stages.slice(0, -1).map((stage, index) => ({ ...newTransition(pipelineVersionId, stage, stages[index + 1], index + 1), actionLabel: labels[index] }))
  return { stages, transitions }
}
function newStage(displayOrder: number, isInitial = false): RecruitmentPipelineStage {
  return { id: localId(), pipelineVersionId: 0, stageCode: isInitial ? 'NEW' : `STAGE_${displayOrder}`, stageName: isInitial ? 'New applications' : `Stage ${displayOrder}`, stageType: 'Screening', stageNumber: displayOrder, displayOrder, slaDurationMinutes: 48 * 60, slaWarningMinutes: 4 * 60, targetOffsetMinutes: null, stakeholderCode: '', allowPause: true, pauseBehavior: 'ShiftStageAndOverall', approvalWorkflowId: null, requiresApproval: false, calendarEnabled: false, allowSkip: false, isInitial, isTerminal: false, isActive: true, actions: [], atsConfiguration: null, externalFormConfiguration: null, attachmentRequirements: [], processDocumentRequirements: [], offerConfiguration: null, interviewConfiguration: null, defaultPanelMembers: [] }
}
function newTransition(pipelineVersionId: number, from: RecruitmentPipelineStage, to: RecruitmentPipelineStage, displayOrder: number): RecruitmentPipelineTransition {
  return { id: localId(), pipelineVersionId, fromStageId: from.id, toStageId: to.id, fromStageCode: from.stageCode, toStageCode: to.stageCode, outcomeCode: `TO_${to.stageCode}`, actionLabel: `Move to ${to.stageName}`, approvalWorkflowId: null, requiresReason: false, isActive: true, displayOrder, rules: [] }
}
function newTransitionRule(transitionId: number, displayOrder: number): RecruitmentPipelineTransitionRule {
  return { id: localId(), transitionId, ruleType: 'ATS_SCORE', comparisonOperator: 'GTE', decimalValue: 60, integerValue: null, textValue: null, booleanValue: null, isMandatory: true, errorMessage: '', displayOrder }
}
function stageTypePatch(stage: RecruitmentPipelineStage, stageType: string): Partial<RecruitmentPipelineStage> {
  const candidateFacing = ['ExternalForm', 'Documents', 'PreOnboarding', 'Offer'].includes(stageType)
  return {
    stageType, isTerminal: ['Rejected', 'Withdrawn', 'Completed'].includes(stageType), calendarEnabled: stageType === 'Interview' || stage.calendarEnabled,
    actions: stage.actions.filter(action => (action.actionCode !== 'GENERATE_ACTION_LINK' || candidateFacing) && (action.actionCode !== 'RUN_ATS_SCORE' || stageType === 'ATS') && (action.triggerEvent !== 'OnSubmission' || candidateFacing)),
    atsConfiguration: stageType === 'ATS' ? stage.atsConfiguration ?? { id: 0, pipelineStageId: stage.id, scoringProfileId: null, minimumAdvanceScore: 60, maximumRejectScore: 30, autoScoreOnEntry: true, autoAdvance: false, autoReject: false, requireHumanConfirmation: true, advanceOutcomeCode: 'SHORTLIST', rejectOutcomeCode: 'REJECT' } : null,
    externalFormConfiguration: ['ExternalForm', 'Documents', 'PreOnboarding'].includes(stageType) ? stage.externalFormConfiguration ?? { id: 0, pipelineStageId: stage.id, formVersionId: 0, submissionRequired: true, allowSaveDraft: true, actionTokenValidityMinutes: 10080, actionTokenMaximumUses: 20 } : null,
    interviewConfiguration: stageType === 'Interview' ? stage.interviewConfiguration ?? { id: 0, pipelineStageId: stage.id, roundNumber: stage.stageNumber, interviewType: 'Technical', defaultDurationMinutes: 60, minimumPanelCount: 1, minimumPassingScore: 60, scoreInputMode: 'PercentageWeighted', panelAggregationMethod: 'Average', feedbackRequired: true, calendarEnabled: true, allowReschedule: true, competencies: [] } : null,
    defaultPanelMembers: stageType === 'Interview' ? stage.defaultPanelMembers ?? [] : [],
    offerConfiguration: stageType === 'Offer' ? stage.offerConfiguration ?? { id: 0, pipelineStageId: stage.id, offerTemplateId: null, approvalWorkflowId: null, budgetBasis: 'ApprovedMaximum', maximumVariancePercent: 0, requireApprovalWhenVarianceExceeded: false, varianceApprovalWorkflowId: null, candidateResponseValidityDays: 7, requireAcceptedOfferToAdvance: true } : null,
    attachmentRequirements: ['Documents', 'PreOnboarding'].includes(stageType) ? stage.attachmentRequirements : [],
  }
}
function cloneVersion(source: RecruitmentPipelineVersion, pipelineDefinitionId: number): RecruitmentPipelineVersion {
  const idMap = new Map<number, number>(); source.stages.forEach(stage => idMap.set(stage.id, localId()))
  const stages = source.stages.map((stage, index) => { const id = idMap.get(stage.id)!; return { ...stage, id, pipelineVersionId: 0, stageNumber: index + 1, displayOrder: index + 1, actions: stage.actions.map((action, actionIndex) => { const actionId = localId(); return { ...action, id: actionId, pipelineStageId: id, executionOrder: actionIndex + 1, recipients: (action.recipients ?? []).map((recipient, recipientIndex) => ({ ...recipient, id: localId(), stageActionId: actionId, displayOrder: recipientIndex + 1 })) } }), atsConfiguration: stage.atsConfiguration ? { ...stage.atsConfiguration, id: 0, pipelineStageId: id } : null, externalFormConfiguration: stage.externalFormConfiguration ? { ...stage.externalFormConfiguration, id: 0, pipelineStageId: id } : null, attachmentRequirements: stage.attachmentRequirements.map((item, itemIndex) => ({ ...item, id: localId(), pipelineStageId: id, displayOrder: itemIndex + 1 })), processDocumentRequirements: (stage.processDocumentRequirements ?? []).map((item, itemIndex) => ({ ...item, id: localId(), pipelineStageId: id, displayOrder: itemIndex + 1 })), offerConfiguration: stage.offerConfiguration ? { ...stage.offerConfiguration, id: 0, pipelineStageId: id } : null, interviewConfiguration: stage.interviewConfiguration ? { ...stage.interviewConfiguration, id: 0, pipelineStageId: id, competencies: stage.interviewConfiguration.competencies.map((item, itemIndex) => ({ ...item, id: localId(), interviewStageConfigurationId: 0, displayOrder: itemIndex + 1 })) } : null, defaultPanelMembers: (stage.defaultPanelMembers ?? []).map((panel, panelIndex) => ({ ...panel, id: localId(), pipelineStageId: id, displayOrder: panelIndex + 1 })) } })
  const transitions = source.transitions.map((transition, index) => ({ ...transition, id: localId(), pipelineVersionId: 0, fromStageId: idMap.get(transition.fromStageId)!, toStageId: idMap.get(transition.toStageId)!, displayOrder: index + 1, rules: transition.rules.map((rule, ruleIndex) => ({ ...rule, id: localId(), transitionId: 0, displayOrder: ruleIndex + 1 })) }))
  return { id: 0, pipelineDefinitionId, versionNumber: source.versionNumber + 1, status: 'Draft', scopeType: source.scopeType, slaMode: source.slaMode, overallSlaMinutes: source.overallSlaMinutes, stages, transitions }
}
function validate(definition: RecruitmentPipelineDefinition, version: RecruitmentPipelineVersion) {
  if (!definition.clientId || !definition.pipelineName.trim() || !code(definition.pipelineCode || definition.pipelineName)) return 'Client, pipeline name and code are required.'
  if (version.stages.length < 2) return 'A pipeline needs at least two stages.'
  if (version.stages.filter(row => row.isInitial).length !== 1) return 'Select exactly one initial stage.'
  if (!version.stages.some(row => row.isTerminal)) return 'Add at least one terminal stage.'
  if (new Set(version.stages.map(row => code(row.stageCode))).size !== version.stages.length) return 'Stage codes must be unique.'
  if (version.slaMode === 'CumulativeFromAnchor') {
    if (!version.overallSlaMinutes || version.overallSlaMinutes <= 0) return 'A cumulative pipeline needs an overall SLA.'
    const targets = [...version.stages].filter(stage => stage.isActive).sort((a, b) => a.displayOrder - b.displayOrder).map(stage => stage.targetOffsetMinutes)
    if (targets.some(value => value == null || value < 0)) return 'Every active cumulative stage needs a target day; Day 0 is valid.'
    if (targets.some((value, index) => index > 0 && Number(value) < Number(targets[index - 1]))) return 'Cumulative stage targets must follow the configured order.'
    if (Math.max(...targets.map(Number)) > version.overallSlaMinutes) return 'A stage target cannot exceed the overall SLA.'
  }
  if (version.transitions.some(row => row.fromStageId === row.toStageId || !row.actionLabel.trim() || !code(row.outcomeCode))) return 'Every transition needs distinct stages, an action label and an outcome code.'
  if (version.transitions.some(transition => transition.rules.some(rule => !rule.ruleType || !rule.comparisonOperator))) return 'Every transition rule needs a type and comparison.'
  if (version.transitions.some(transition => transition.rules.some(rule => rule.ruleType === 'INTERVIEW_RESULT' && !rule.textValue?.trim()))) return 'Interview-result rules need an expected result.'
  if (version.transitions.some(transition => transition.rules.some(rule => ['RESUME_REQUIRED', 'MANDATORY_DOCUMENTS_COMPLETE', 'OFFER_ACCEPTED', 'PANEL_FEEDBACK_COMPLETE'].includes(rule.ruleType) && rule.booleanValue == null))) return 'Yes/no transition rules need an expected value.'
  if (version.stages.some(stage => ['Documents', 'PreOnboarding'].includes(stage.stageType) && stage.attachmentRequirements.some(item => !item.attachmentFieldConfigurationId))) return 'Every requested document needs a global attachment field configuration.'
  if (version.stages.some(stage => stage.stageType === 'Documents' && !stage.attachmentRequirements.length && !(stage.processDocumentRequirements ?? []).length)) return 'Every Documents stage needs at least one candidate attachment or process-document requirement.'
  if (version.stages.some(stage => ['ExternalForm', 'Documents', 'PreOnboarding'].includes(stage.stageType) && !stage.externalFormConfiguration?.formVersionId)) return 'Every candidate-facing form or document stage needs a published form version.'
  if (version.stages.some(stage => (stage.processDocumentRequirements ?? []).some(document => !document.documentType) || new Set((stage.processDocumentRequirements ?? []).map(document => document.documentType)).size !== (stage.processDocumentRequirements ?? []).length)) return 'Every process-document requirement needs a unique document type within its stage.'
  if (version.stages.some(stage => stage.slaDurationMinutes > 0 && stage.slaWarningMinutes > stage.slaDurationMinutes)) return 'An SLA warning cannot exceed its stage duration.'
  if (version.stages.some(stage => stage.requiresApproval && !stage.approvalWorkflowId)) return 'Every approval-enabled stage needs a workflow.'
  if (version.stages.some(stage => stage.offerConfiguration?.requireApprovalWhenVarianceExceeded && !stage.offerConfiguration.varianceApprovalWorkflowId)) return 'Select a variance approval workflow for the offer stage.'
  if (version.stages.some(stage => stage.stageType === 'Interview' && stage.interviewConfiguration && stage.interviewConfiguration.competencies.length > 0 && Math.abs(stage.interviewConfiguration.competencies.reduce((total, row) => total + Number(row.weightPercent || 0), 0) - 100) > 0.01)) return 'Interview competency weights must total exactly 100% in every interview round.'
  if (version.stages.some(stage => stage.stageType === 'Interview' && (stage.defaultPanelMembers?.length ?? 0) > 0 && (stage.defaultPanelMembers?.length ?? 0) < (stage.interviewConfiguration?.minimumPanelCount ?? 1))) return 'Each configured default panel must meet the stage minimum panel count.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'START_WORKFLOW' && !action.workflowId))) return 'Select a workflow for every START WORKFLOW stage action.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'SEND_NOTIFICATION' && !action.templateId))) return 'Select a notification template for every SEND NOTIFICATION stage action.'
  if (version.stages.some(stage => stage.actions.some(action => (action.recipients ?? []).some(recipient => recipient.recipientType === 'SpecificUser' && !recipient.userId)))) return 'Choose a user for every specific-user notification recipient.'
  if (version.stages.some(stage => stage.actions.some(action => (action.recipients ?? []).some(recipient => recipient.recipientType === 'UserRole' && !recipient.roleCode.trim())))) return 'Choose a role for every role-based notification recipient.'
  if (version.stages.some(stage => stage.actions.some(action => (action.recipients ?? []).some(recipient => recipient.recipientType === 'StaticEmail' && !/^\S+@\S+\.\S+$/.test(recipient.emailAddress))))) return 'Enter a valid static notification recipient email.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'GENERATE_ACTION_LINK' && (action.triggerEvent !== 'OnEntry' || !['ExternalForm', 'Documents', 'PreOnboarding', 'Offer'].includes(stage.stageType))))) return 'Candidate action links require On Entry on a candidate-facing stage.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'RUN_ATS_SCORE' && stage.stageType !== 'ATS'))) return 'RUN ATS SCORE can be configured only on an ATS stage.'
  if (version.stages.some(stage => stage.actions.some(action => action.isBlocking && !['OnEntry', 'OnSubmission'].includes(action.triggerEvent)))) return 'Blocking stage actions must run on entry or candidate submission.'
  if (version.stages.some(stage => Array.from(new Set(stage.actions.map(action => action.triggerEvent))).some(trigger => { const orders = stage.actions.filter(action => action.triggerEvent === trigger).map(action => action.executionOrder); return new Set(orders).size !== orders.length }))) return 'Stage action order must be unique within each trigger.'
  if (version.stages.some(stage => Array.from(new Set(stage.actions.map(action => action.triggerEvent))).some(trigger => { const rows = stage.actions.filter(action => action.triggerEvent === trigger); const link = rows.find(action => action.actionCode === 'GENERATE_ACTION_LINK'); const notification = rows.find(action => action.actionCode === 'SEND_NOTIFICATION'); return link && notification && notification.executionOrder <= link.executionOrder }))) return 'Generate candidate links before sending their notification.'
  const nonTerminal = version.stages.filter(row => !row.isTerminal && row.isActive)
  if (nonTerminal.some(stage => !version.transitions.some(row => row.fromStageId === stage.id && row.isActive))) return 'Every active non-terminal stage needs at least one outgoing transition.'
  return ''
}
function formatDuration(minutes: number) { if (!minutes) return 'None'; if (minutes % 1440 === 0) return `${minutes / 1440}d`; if (minutes % 60 === 0) return `${minutes / 60}h`; return `${minutes}m` }
function equalCompetencies(ids: number[], configurationId: number, lookups: RecruitmentOrchestrationLookups) {
  if (!ids.length) return []
  const base = Math.floor(10000 / ids.length) / 100
  return ids.map((competencyId, index) => { const option = (lookups.interviewCompetencies ?? []).find(row => row.id === competencyId); return { id: localId(), interviewStageConfigurationId: configurationId, competencyId, competencyName: option?.competencyName ?? '', weightPercent: index === 0 ? Number((100 - base * (ids.length - 1)).toFixed(2)) : base, minimumScore: 0, displayOrder: index + 1 } })
}
