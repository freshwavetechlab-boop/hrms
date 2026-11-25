import { clientChoice } from './ClientOption'
import RecruitmentOfferClientSelect from './RecruitmentOfferClientSelect'
import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { ArrowDownOutlined, ArrowRightOutlined, ArrowUpOutlined, CopyOutlined, DeleteOutlined, EditOutlined, PlusOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Checkbox, Collapse, Drawer, Empty, Form, Input, InputNumber, Modal, Popconfirm, Segmented, Select, Space, Switch, Tag, message } from 'antd'
import DataTable from './DataTable'
import { useAuthSession } from './AuthGate'
import { getClients } from '../services/payrollService'
import { getJsonResult } from '../services/apiClient'
import { loadSecurityData } from '../services/securityService'
import RecruitmentMasterSelect from './RecruitmentMasterSelect'
import RecruitmentRollbackFields from './RecruitmentRollbackFields'
import {
  deleteRecruitmentPipelineDefinition, getRecruitmentOrchestrationLookups, getRecruitmentPipeline, getRecruitmentPipelines, getRecruitmentPipelineVersion, getRecruitmentPipelineVersions,
  createSimilarRecruitmentPipeline, saveRecruitmentInterviewCompetency, saveRecruitmentPipelineConfiguration,
} from '../services/recruitmentOrchestrationService'
import type { AuthRole, AuthUser, Client, Drop } from '../types/payroll'
import type {
  RecruitmentOrchestrationLookups, RecruitmentPipelineDefinition, RecruitmentPipelineStage,
  RecruitmentPipelineStageAction, RecruitmentPipelineTransition, RecruitmentPipelineVersion,
  RecruitmentPipelineTransitionRule, RecruitmentStageAttachmentRequirement, RecruitmentStageProcessDocumentRequirement,
} from '../types/recruitmentOrchestration'
import './RecruitmentOrchestration.css'
import './RecruitmentPipelineDesigner.css'
import { pipelineOwnershipLegend, pipelineStageOwnership } from '../utils/recruitmentPipelineOwnership'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { isSelectedClientUser } from '../utils/clientAccess'
import { attachmentConfigurationLabel } from '../utils/attachmentConfigurationLabel'

type Props = { initialClientId?: number; initialPipelineDefinitionId?: number; initialPipelineVersionId?: number; onSaved?: (pipeline: RecruitmentPipelineDefinition) => void; dropdowns?: Drop[]; onDropdownsChange?: (rows: Drop[]) => void }
const emptyLookups: RecruitmentOrchestrationLookups = { lookupSources: [], attachmentConfigurations: [], attachmentFieldConfigurations: [], workflows: [], forms: [], positions: [], atsProfiles: [] }
const stageTypes = ['Screening', 'ATS', 'ExternalForm', 'Documents', 'Interview', 'HR', 'Approval', 'PreOnboarding', 'Offer', 'Joining', 'Rejected', 'Withdrawn', 'Completed']
const localId = () => -Math.floor(Date.now() + Math.random() * 100000)
const code = (value: string) => value.toUpperCase().trim().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '')
type SimilarPipelineDraft = { sourceClientId: number; source: RecruitmentPipelineDefinition | null; sourceVersionId: number; sourceScopeType?: RecruitmentPipelineVersion['scopeType']; targetClientId: number; pipelineName: string; pipelineCode: string }

export default function RecruitmentPipelineDesigner({ initialClientId = 0, initialPipelineDefinitionId = 0, initialPipelineVersionId = 0, onSaved, dropdowns = [], onDropdownsChange = () => undefined }: Props) {
  const session = useAuthSession()
  const canDelete = Boolean(session?.user.permissions.includes('settings.manage'))
  const scopeUser = session?.originalUser || session?.user
  const boundClientId = isSelectedClientUser(scopeUser) ? 0 : Number(scopeUser?.clientId || 0)
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(boundClientId || initialClientId)
  const [loadedClientId, setLoadedClientId] = useState(-1)
  const [pipelines, setPipelines] = useState<RecruitmentPipelineDefinition[]>([])
  const [pipelineScopes, setPipelineScopes] = useState<Record<number, RecruitmentPipelineVersion['scopeType']>>({})
  const [lookups, setLookups] = useState(emptyLookups)
  const [users, setUsers] = useState<AuthUser[]>([])
  const [roles, setRoles] = useState<AuthRole[]>([])
  const [definition, setDefinition] = useState<RecruitmentPipelineDefinition | null>(null)
  const [version, setVersion] = useState<RecruitmentPipelineVersion | null>(null)
  const [cloneReviewed, setCloneReviewed] = useState(false)
  const [editing, setEditing] = useState(false)
  const savedRecord = useRef<{ definition: RecruitmentPipelineDefinition; version: RecruitmentPipelineVersion } | null>(null)
  const [selectingPipelineId, setSelectingPipelineId] = useState<number | null>(null)
  const [selectedStageId, setSelectedStageId] = useState<number | null>(null)
  const [stageDrawer, setStageDrawer] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saveValidationError, setSaveValidationError] = useState('')
  const [competencyOpen, setCompetencyOpen] = useState(false)
  const [competencySaving, setCompetencySaving] = useState(false)
  const [competencyDraft, setCompetencyDraft] = useState({ competencyCode: '', competencyName: '', description: '' })
  const [libraryOpen, setLibraryOpen] = useState(true)
  const [libraryView, setLibraryView] = useSessionPreference<'Cards' | 'Table'>(`recruitment.pipeline-library:${session?.user.id ?? 0}:${clientId}:view`, 'Cards')
  const [pipelineSearch, setPipelineSearch] = useSessionPreference(`recruitment.pipeline-library:${session?.user.id ?? 0}:${clientId}:search`, '')
  const [similar, setSimilar] = useState<SimilarPipelineDraft | null>(null)
  const [similarSources, setSimilarSources] = useState<RecruitmentPipelineDefinition[]>([])
  const [copySourcesLoading, setCopySourcesLoading] = useState(false)
  const [copyConfigurationLoading, setCopyConfigurationLoading] = useState(false)
  const [copySourceError, setCopySourceError] = useState('')
  const similarRequestRef = useRef(0)
  const similarAbortRef = useRef<AbortController | null>(null)
  const [copying, setCopying] = useState(false)
  const [copyError, setCopyError] = useState('')
  const openedInitialDefinition = useRef('')
  const pendingDefinitionOpen = useRef<{ id: number; clientId: number; preferredVersionId?: number; edit: boolean } | null>(null)
  const stageFlowRef = useRef<HTMLDivElement | null>(null)
  const loadRequestRef = useRef(0)
  const selectionRequestRef = useRef(0)

  const load = async (scope: number) => {
    const requestId = ++loadRequestRef.current
    // The pipeline library is useful immediately; the larger form/template lookup
    // catalog must not hold it behind unrelated settings requests.
    if (scope) void getRecruitmentOrchestrationLookups(scope).then(options => {
      if (requestId === loadRequestRef.current) setLookups(options)
    })
    const rows = await getRecruitmentPipelines(scope)
    if (requestId !== loadRequestRef.current) return
    setPipelines(rows); setLoadedClientId(scope)
    const kinds = await Promise.all(rows.map(async row => {
      const versions = row.versions?.length ? row.versions : await getRecruitmentPipelineVersions(row.id)
      const active = versions.find(item => item.id === row.currentPublishedVersionId) ?? versions.find(item => item.status === 'Draft') ?? versions[0]
      return [row.id, active?.scopeType] as const
    }))
    if (requestId === loadRequestRef.current) setPipelineScopes(Object.fromEntries(kinds))
  }
  useEffect(() => {
    let active = true
    void getClients(true, true).then(rows => { if (active) setClients(rows) })
    void loadSecurityData().then(security => {
      if (active) { setUsers(security.users); setRoles(security.roles) }
    })
    return () => { active = false }
  }, [])
  useEffect(() => {
    setLoadedClientId(-1)
    setPipelines([]); setLookups(emptyLookups)
    selectionRequestRef.current += 1
    setSelectingPipelineId(null)
    void load(clientId)
    setDefinition(null); setVersion(null); setSelectedStageId(null); setStageDrawer(false)
    setEditing(false); savedRecord.current = null; setCloneReviewed(false)
    setPipelineScopes({})
  }, [clientId])
  useEffect(() => () => { selectionRequestRef.current += 1 }, [])

  const readOnly = !editing
  const dirty = Boolean(definition && version && (!savedRecord.current || pipelineEditSnapshot(definition, version) !== pipelineEditSnapshot(savedRecord.current.definition, savedRecord.current.version)))
  const selectedStage = version?.stages.find(row => row.id === selectedStageId) ?? null
  const selectPipeline = async (id: number, preferredVersionId?: number, edit = false) => {
    const known = pipelines.find(row => row.id === id)
    if (known && known.clientId !== clientId) {
      pendingDefinitionOpen.current = { id, clientId: known.clientId, preferredVersionId, edit }
      setClientId(known.clientId)
      return
    }
    const requestId = ++selectionRequestRef.current
    setSelectingPipelineId(id)
    setDefinition(null); setVersion(null); setSelectedStageId(null); setStageDrawer(false)
    try {
      const [detail, versions] = await Promise.all([getRecruitmentPipeline(id), getRecruitmentPipelineVersions(id)])
      if (requestId !== selectionRequestRef.current) return
      if (!detail) {
        message.error('Unable to load pipeline.')
        return
      }
      const row = detail.definition
      if (row.clientId !== clientId) {
        pendingDefinitionOpen.current = { id, clientId: row.clientId, preferredVersionId, edit }
        setClientId(row.clientId)
        return
      }
      const ordered = [...(detail.versions.length ? detail.versions : versions)].sort((a, b) => b.versionNumber - a.versionNumber)
      // CRUD always edits the current saved configuration. Internal version IDs
      // remain only to preserve existing job/application mappings.
      const selectedHeader = ordered.find(item => item.id === row.currentPublishedVersionId)
        ?? ordered.find(item => item.status === 'Draft')
        ?? ordered.find(item => item.id === preferredVersionId)
        ?? ordered[0]
        ?? blankVersion(row.id)
      const selected = selectedHeader.id ? await getRecruitmentPipelineVersion(selectedHeader.id) ?? selectedHeader : selectedHeader
      if (requestId !== selectionRequestRef.current) return
      const normalized = { ...selected, stages: normalizeLoadedStageScopes(selected) }
      setDefinition(row); setVersion(normalized); setSelectedStageId(normalized.stages[0]?.id ?? null)
      savedRecord.current = structuredClone({ definition: row, version: normalized })
      setEditing(edit); setSaveValidationError('')
      setCloneReviewed(false)
      setLibraryOpen(false)
    } catch {
      if (requestId === selectionRequestRef.current) message.error('Unable to load pipeline.')
    } finally {
      if (requestId === selectionRequestRef.current) setSelectingPipelineId(null)
    }
  }
  useEffect(() => {
    const key = `${clientId}:${initialPipelineDefinitionId}:${initialPipelineVersionId}`
    const pending = pendingDefinitionOpen.current
    if (loadedClientId === clientId && pending?.clientId === clientId && pipelines.some(row => row.id === pending.id)) {
      pendingDefinitionOpen.current = null
      openedInitialDefinition.current = key
      void selectPipeline(pending.id, pending.preferredVersionId, pending.edit)
      return
    }
    if (loadedClientId !== clientId || !initialPipelineDefinitionId || openedInitialDefinition.current === key || !pipelines.some(row => row.id === initialPipelineDefinitionId)) return
    openedInitialDefinition.current = key
    void selectPipeline(initialPipelineDefinitionId, initialPipelineVersionId)
  }, [loadedClientId, pipelines, clientId, initialPipelineDefinitionId, initialPipelineVersionId]) // eslint-disable-line react-hooks/exhaustive-deps
  const cancelSimilarReads = () => { ++similarRequestRef.current; similarAbortRef.current?.abort(); similarAbortRef.current = null }
  useEffect(() => () => { ++similarRequestRef.current; similarAbortRef.current?.abort() }, [])
  const selectSimilarSource = async (source: RecruitmentPipelineDefinition | undefined) => {
    cancelSimilarReads()
    setCopyError(''); setCopySourceError(''); setCopySourcesLoading(false)
    setSimilar(current => current ? { ...current, source: source ?? null, sourceVersionId: 0, sourceScopeType: undefined, pipelineName: source ? `${source.pipelineName} copy` : '', pipelineCode: source ? `${source.pipelineCode}_COPY` : '' } : null)
    if (!source) { setCopyConfigurationLoading(false); return }
    const request = similarRequestRef.current
    const controller = new AbortController(); similarAbortRef.current = controller
    setCopyConfigurationLoading(true)
    const result = await getJsonResult<RecruitmentPipelineVersion[]>(`/api/recruitment-orchestration/pipelines/${source.id}/versions`, [], { signal: controller.signal, toast: false, loader: false })
    if (request !== similarRequestRef.current || controller.signal.aborted) return
    setCopyConfigurationLoading(false)
    if (!result.ok) { setCopySourceError(result.error || 'The source pipeline configuration could not be loaded.'); return }
    const ordered = [...result.data].sort((left, right) => right.versionNumber - left.versionNumber)
    const selected = ordered.find(row => row.id === source.currentPublishedVersionId) ?? ordered.find(row => row.status !== 'Retired') ?? ordered[0]
    if (!selected?.id) { setCopySourceError('This pipeline has no saved configuration to copy. Choose another source pipeline.'); return }
    setSimilar(current => current?.source?.id === source.id ? { ...current, sourceVersionId: selected.id, sourceScopeType: selected.scopeType } : current)
  }
  const loadSimilarSources = async (sourceClientId: number, preselected?: RecruitmentPipelineDefinition) => {
    cancelSimilarReads()
    const request = similarRequestRef.current
    const controller = new AbortController(); similarAbortRef.current = controller
    setCopyError(''); setCopySourceError(''); setSimilarSources([]); setCopySourcesLoading(true); setCopyConfigurationLoading(false)
    setSimilar(current => current ? { ...current, sourceClientId, source: null, sourceVersionId: 0, sourceScopeType: undefined, pipelineName: '', pipelineCode: '' } : null)
    const query = sourceClientId ? `?clientId=${sourceClientId}` : ''
    const result = await getJsonResult<RecruitmentPipelineDefinition[]>(`/api/recruitment-orchestration/pipelines${query}`, [], { signal: controller.signal, toast: false, loader: false })
    if (request !== similarRequestRef.current || controller.signal.aborted) return
    setCopySourcesLoading(false)
    if (!result.ok) { setCopySourceError(result.error || 'The source pipelines could not be loaded.'); return }
    const rows = result.data.filter(row => !sourceClientId || row.clientId === sourceClientId)
    setSimilarSources(rows)
    if (preselected) {
      const source = rows.find(row => row.id === preselected.id)
      if (source) await selectSimilarSource(source)
      else setCopySourceError('The selected source pipeline is no longer available in your permitted clients.')
    }
  }
  const openSimilar = (source?: RecruitmentPipelineDefinition) => {
    cancelSimilarReads()
    const sourceClientId = source?.clientId || boundClientId || 0
    setCopyError(''); setCopySourceError('')
    setSimilar({ sourceClientId, source: null, sourceVersionId: 0, targetClientId: clientId || source?.clientId || boundClientId || 0, pipelineName: '', pipelineCode: '' })
    void loadSimilarSources(sourceClientId, source)
  }
  const closeSimilar = () => {
    if (copying) return
    cancelSimilarReads(); setSimilar(null); setSimilarSources([]); setCopySourcesLoading(false); setCopyConfigurationLoading(false); setCopySourceError(''); setCopyError('')
  }
  const createSimilar = async () => {
    if (!similar?.source || !similar.sourceVersionId || copySourcesLoading || copyConfigurationLoading || !similar.targetClientId || !similar.pipelineName.trim() || !code(similar.pipelineCode)) return
    setCopying(true); setCopyError('')
    const response = await createSimilarRecruitmentPipeline(similar.source.id, { sourceVersionId: similar.sourceVersionId, targetClientId: similar.targetClientId, pipelineName: similar.pipelineName.trim(), pipelineCode: code(similar.pipelineCode), description: similar.source.description })
    setCopying(false)
    if (!response.ok || !response.data) return setCopyError(response.error || 'The pipeline could not be copied.')
    const copied = response.data
    cancelSimilarReads()
    pendingDefinitionOpen.current = null
    setSimilar(null); setLibraryOpen(false)
    if (copied.definition.clientId !== clientId) {
      setClientId(copied.definition.clientId)
      openedInitialDefinition.current = `${copied.definition.clientId}:${initialPipelineDefinitionId}:${initialPipelineVersionId}`
    }
    await load(copied.definition.clientId)
    setDefinition(copied.definition); setVersion(copied.version); setCloneReviewed(false); setSelectedStageId(copied.version.stages[0]?.id ?? null)
    savedRecord.current = structuredClone({ definition: copied.definition, version: copied.version }); setEditing(true)
    if (copied.warnings.length) Modal.info({ title: 'Pipeline copied · review client references', content: <div>{copied.warnings.map((note, index) => <p key={index}>{note}</p>)}</div> })
    else message.success('A separate pipeline is ready to edit. Save when ready to use it.')
    onSaved?.(copied.definition)
  }
  const startNew = () => {
    if (!clientId) return message.warning('Select a client first.')
    selectionRequestRef.current += 1
    setSelectingPipelineId(null)
    const row: RecruitmentPipelineDefinition = { id: 0, clientId, clientName: clients.find(item => item.id === clientId)?.name ?? '', pipelineCode: '', pipelineName: '', description: '', currentPublishedVersionId: null, isActive: true, versions: [] }
    const draft = blankVersion(0); setDefinition(row); setVersion(draft); setCloneReviewed(false); setSelectedStageId(draft.stages[0]?.id ?? null)
    savedRecord.current = null; setEditing(true)
    setLibraryOpen(false)
  }
  const beginEdit = () => { setEditing(true); setSaveValidationError('') }
  const cancelEdit = () => {
    const saved = savedRecord.current
    if (saved) { const restored = structuredClone(saved); setDefinition(restored.definition); setVersion(restored.version); setSelectedStageId(restored.version.stages[0]?.id ?? null) }
    else { setDefinition(null); setVersion(null); setSelectedStageId(null); setLibraryOpen(true) }
    setEditing(false); setStageDrawer(false); setCloneReviewed(false); setSaveValidationError('')
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
    const stage = newStage(version.stages.length + 1, false, defaultNewStageScope(version))
    const previous = version.stages.at(-1)
    const carryTerminal = Boolean(previous?.isTerminal)
    const stages = [...version.stages.map(row => carryTerminal && row.id === previous?.id ? { ...row, isTerminal: false } : row), { ...stage, isTerminal: carryTerminal }]
    const transitions = previous
      ? [...version.transitions, newTransition(version.id, previous, stage, version.transitions.length + 1)]
      : version.transitions
    patchVersion({ stages, transitions })
    setSelectedStageId(stage.id)
    setStageDrawer(true)
    window.setTimeout(() => stageFlowRef.current?.lastElementChild?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }), 0)
  }
  const removeStage = (stageId: number) => Modal.confirm({ title: 'Remove pipeline stage?', content: 'Connected transitions will also be removed when you save. Stages used by existing jobs or candidate history are protected.', okText: 'Remove', okButtonProps: { danger: true }, onOk: () => { const stages = version!.stages.filter(row => row.id !== stageId).map((row, index) => ({ ...row, stageNumber: index + 1, displayOrder: index + 1 })); patchVersion({ stages, transitions: version!.transitions.filter(row => row.fromStageId !== stageId && row.toStageId !== stageId) }); setSelectedStageId(stages[0]?.id ?? null); setStageDrawer(false) } })
  const moveStage = (stageId: number, delta: number) => { const rows = [...version!.stages]; const index = rows.findIndex(row => row.id === stageId); const target = index + delta; if (index < 0 || target < 0 || target >= rows.length) return; [rows[index], rows[target]] = [rows[target], rows[index]]; patchVersion({ stages: rows.map((row, rowIndex) => ({ ...row, stageNumber: rowIndex + 1, displayOrder: rowIndex + 1 })) }) }
  const setInitial = (stageId: number) => patchVersion({ stages: version!.stages.map(row => ({ ...row, isInitial: row.id === stageId })) })
  const addTransition = () => { if (!version || version.stages.length < 2 || readOnly) return; const from = selectedStage ?? version.stages[0]; const to = version.stages.find(row => row.id !== from.id)!; patchVersion({ transitions: [...version.transitions, newTransition(version.id, from, to, version.transitions.length + 1)] }) }
  const useStandardFlow = () => {
    if (!version || readOnly) return
    const next = standardHiringFlow(version.id, version.scopeType ?? 'Application')
    patchVersion(next)
    setSelectedStageId(next.stages[0].id)
    message.success('Standard hiring stages prepared. Review them and save when ready.')
    window.setTimeout(() => stageFlowRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }), 50)
  }
  const patchTransition = (id: number, patch: Partial<RecruitmentPipelineTransition>) => patchVersion({ transitions: version!.transitions.map(row => row.id === id ? { ...row, ...patch } : row) })
  const removeTransition = (id: number) => patchVersion({ transitions: version!.transitions.filter(row => row.id !== id).map((row, index) => ({ ...row, displayOrder: index + 1 })) })

  const save = async () => {
    if (!definition || !version || readOnly) return
    if (version.cloneWarnings?.length && !cloneReviewed) return setSaveValidationError('Review the copied client mappings and access, then acknowledge the review before saving.')
    const error = validate(definition, version)
    if (error) {
      setSaveValidationError(error)
      return
    }
    setSaveValidationError('')
    setSaving(true)
    const response = await saveRecruitmentPipelineConfiguration({
      definition: { id: definition.id, clientId: definition.clientId, pipelineCode: code(definition.pipelineCode || definition.pipelineName), pipelineName: definition.pipelineName.trim(), description: definition.description.trim(), isActive: definition.isActive },
      configuration: { ...version, pipelineDefinitionId: definition.id, acknowledgeCloneWarnings: cloneReviewed },
      expectedConfigurationHash: savedRecord.current?.version.configurationHash,
    })
    setSaving(false)
    if (!response.ok || !response.data) {
      setSaveValidationError(response.error || 'The pipeline could not be saved. No partial changes were applied.')
      return
    }
    const saved = { definition: response.data.definition, version: response.data.version }
    setDefinition(saved.definition); setVersion(saved.version); savedRecord.current = structuredClone(saved)
    setEditing(false); setCloneReviewed(false); setSaveValidationError('')
    setSelectedStageId(saved.version.stages[0]?.id ?? null)
    onSaved?.(saved.definition); await load(saved.definition.clientId)
  }

  const removeDefinition = async (row: RecruitmentPipelineDefinition) => {
    if (selectingPipelineId === row.id) {
      selectionRequestRef.current += 1
      setSelectingPipelineId(null)
    }
    const response = await deleteRecruitmentPipelineDefinition(row.id, row.clientId)
    if (!response.ok) return
    if (definition?.id === row.id) {
      setDefinition(null); setVersion(null); setSelectedStageId(null); setStageDrawer(false); setEditing(false); savedRecord.current = null
    }
    await load(row.clientId)
  }

  const transitionOptions = version?.stages.map(row => ({ value: row.id, label: `${row.displayOrder}. ${row.stageName}` })) ?? []
  const transitionWorkflowOptions = lookups.workflows
    .filter(row => row.isActive && row.resourceType === 'RecruitmentPipelineTransition' && (row.clientId == null || row.clientId === clientId))
    .map(row => ({ value: row.id, label: row.name }))
  const visiblePipelines = pipelines.filter(row => `${row.pipelineName} ${row.pipelineCode} ${row.clientName} ${row.description}`.toLowerCase().includes(pipelineSearch.trim().toLowerCase()))
  const libraryLoading = loadedClientId !== clientId
  const libraryActions = (row: RecruitmentPipelineDefinition) => <Space size={6} wrap>
    <Button size="small" loading={selectingPipelineId === row.id} icon={<EditOutlined />} onClick={() => void selectPipeline(row.id, undefined, true)}>Edit</Button>
    <Button size="small" icon={<CopyOutlined />} onClick={() => void openSimilar(row)}>Create similar</Button>
    {canDelete && <Popconfirm title="Delete this pipeline?" description="Only unused definitions can be removed." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void removeDefinition(row)}><Button aria-label={`Delete ${row.pipelineName}`} size="small" danger type="text" icon={<DeleteOutlined />} /></Popconfirm>}
  </Space>
  const hiringStages = version?.stages.filter(stage => stage.cardScope === 'Position').length ?? 0
  const candidateStages = version?.stages.filter(stage => stage.cardScope !== 'Position').length ?? 0
  return <section className={`orchestration-shell pipeline-manager-v2 ${definition ? 'has-editor' : ''}`}>
    <div className="pipeline-manager-command">
      <div className="pipeline-manager-context"><label>Client<RecruitmentOfferClientSelect includeSetup aria-label="Pipeline configuration client" data-testid="pipeline-client" allowClear={!boundClientId} disabled={Boolean(boundClientId)} loading={!clients.length} value={clientId || undefined} placeholder="All accessible clients" options={clients.map(clientChoice)} onChange={value => { pendingDefinitionOpen.current = null; setLoadedClientId(-1); setClientId(Number(value || 0)); setLibraryOpen(true) }} /></label><label>Pipeline<Select aria-label="Selected pipeline definition" loading={libraryLoading} disabled={libraryLoading} value={definition?.id || undefined} placeholder="Choose a pipeline" options={pipelines.map(row => ({ value: row.id, label: `${row.pipelineName}${!clientId ? ` · ${row.clientName || clients.find(client => client.id === row.clientId)?.name || ''}` : ''}${pipelineScopes[row.id] ? ` · ${scopeLabel(pipelineScopes[row.id])}` : ''}` }))} onChange={id => void selectPipeline(id)} showSearch optionFilterProp="label" /></label></div>
      <Space wrap><Button onClick={() => setLibraryOpen(value => !value)}>{libraryOpen ? 'Hide library' : 'Browse pipelines'}</Button><Button data-testid="pipeline-competencies" disabled={!clientId} onClick={() => setCompetencyOpen(true)}>Score components</Button><Button data-testid="pipeline-create-similar-top" icon={<CopyOutlined />} disabled={copying} onClick={() => openSimilar()}>Create similar</Button><Button data-testid="pipeline-new" type="primary" icon={<PlusOutlined />} onClick={startNew}>New pipeline</Button></Space>
    </div>
    {(libraryOpen || !definition) && <section className="pipeline-manager-library" data-testid="pipeline-library" data-loaded-client-id={loadedClientId}>
      <div className="pipeline-library-heading"><div><strong>Pipelines <span>{libraryLoading ? "…" : pipelines.length}</span></strong><p>Hiring manages vacancies. Candidate pipelines manage individual applications. A combined pipeline contains both.</p></div><Segmented aria-label="Pipeline library display" value={libraryView} onChange={value => setLibraryView(value as 'Cards' | 'Table')} options={['Cards','Table']} /></div>
      {libraryLoading ? <Card size="small" loading aria-busy="true" aria-label="Loading pipelines" /> : libraryView === 'Cards' ? <><Input aria-label="Find pipeline" prefix={<SearchOutlined />} placeholder="Find a pipeline by name or code" value={pipelineSearch} onChange={event => setPipelineSearch(event.target.value)} allowClear /><div className="pipeline-library-grid">{visiblePipelines.length ? visiblePipelines.map(row => <article className={`pipeline-library-card ${definition?.id === row.id ? 'is-selected' : ''}`} key={row.id}><div><strong>{row.pipelineName}</strong><span>{row.pipelineCode}</span><small>{row.clientName || clients.find(client => client.id === row.clientId)?.name}</small></div><Space wrap size={4}><Tag color={!row.isActive ? 'default' : row.currentPublishedVersionId ? 'green' : 'orange'}>{!row.isActive ? 'Inactive' : row.currentPublishedVersionId ? 'Active' : 'Needs setup'}</Tag>{pipelineScopes[row.id] && <Tag>{scopeLabel(pipelineScopes[row.id])}</Tag>}</Space>{row.description && <p>{row.description}</p>}{libraryActions(row)}</article>) : <Empty description={clientId ? 'No matching pipeline for this client.' : 'No pipelines are configured for your permitted clients.'} />}</div></> : <DataTable rows={pipelines} getRowId={row => row.id} columns={[{ key: 'pipelineName', label: 'Pipeline', wrap: true },{ key: 'pipelineCode', label: 'Code' },{ key: 'clientName', label: 'Client', value: row => row.clientName || clients.find(client => client.id === row.clientId)?.name || '' },{key:'scope',label:'Journey type',value:row=>pipelineScopes[row.id] ? scopeLabel(pipelineScopes[row.id]) : 'Not configured'},{ key: 'isActive', label: 'Status', value: row => !row.isActive ? 'Inactive' : row.currentPublishedVersionId ? 'Active' : 'Needs setup', render: row => <Tag color={!row.isActive ? 'default' : row.currentPublishedVersionId ? 'green' : 'orange'}>{!row.isActive ? 'Inactive' : row.currentPublishedVersionId ? 'Active' : 'Needs setup'}</Tag> },{ key: 'description', label: 'Description', wrap: true }]} actions={libraryActions} actionsWidth={270} emptyText={clientId ? 'No pipelines for this client.' : 'No pipelines are configured for your permitted clients.'} exportFileName="Recruitment pipelines" />}
    </section>}
    {selectingPipelineId !== null ? <Card data-testid="pipeline-selection-loading" loading aria-busy="true" aria-label="Loading selected pipeline" /> : !definition || !version ? null : <section className="pipeline-manager-editor">
      <header className="pipeline-editor-toolbar"><div><strong>{definition.pipelineName || 'New pipeline'}</strong><Space size={4} wrap><Tag color={!definition.id || dirty ? 'gold' : !definition.currentPublishedVersionId ? 'orange' : definition.isActive ? 'green' : 'default'}>{!definition.id ? 'New pipeline' : dirty ? 'Unsaved changes' : !definition.currentPublishedVersionId ? 'Save to use this pipeline' : definition.isActive ? 'Saved · Active' : 'Saved · Inactive'}</Tag><span>{scopeLabel(version.scopeType)}</span></Space></div><Space wrap>{readOnly ? <Button data-testid="pipeline-edit" type="primary" ghost icon={<EditOutlined />} onClick={beginEdit}>Edit pipeline</Button> : <Button data-testid="pipeline-cancel" disabled={saving} onClick={cancelEdit}>Cancel</Button>}<Button data-testid="pipeline-create-similar" icon={<CopyOutlined />} disabled={!definition.id || !version.id || dirty} title={dirty ? 'Save changes before creating a similar pipeline' : undefined} onClick={() => void openSimilar(definition)}>Create similar</Button><Button data-testid="pipeline-save" type="primary" loading={saving} disabled={readOnly} onClick={() => void save()}>Save</Button></Space></header>
      <div className="pipeline-editor-content">
        {!!version.cloneWarnings?.length && <Alert type="warning" showIcon message="Copied pipeline · client mapping review required" description={<div><ul>{version.cloneWarnings.map((warning,index)=><li key={index}>{warning}</li>)}</ul><Checkbox data-testid="pipeline-clone-reviewed" checked={cloneReviewed} onChange={event=>setCloneReviewed(event.target.checked)} disabled={readOnly}>I reviewed the target-client mappings and stage access.</Checkbox><p>Correct missing references and save to use this pipeline.</p></div>}/>}
        <div className="pipeline-manager-summary"><span><b>{version.stages.length}</b> total stages</span><span><b>{hiringStages}</b> hiring / vacancy</span><span><b>{candidateStages}</b> candidate</span><span><b>{version.transitions.length}</b> transitions</span></div>
        <div className="pipeline-scope-explanation">{version.scopeType === 'Hybrid' ? 'This is one combined definition. Hiring-demand stages operate on vacancies; candidate stages operate on each application after the hand-off.' : version.scopeType === 'Position' ? 'This definition tracks a work-order vacancy through its hiring SLA. Individual applicant steps belong to the assigned candidate pipeline.' : 'This definition tracks each candidate. The separate hiring / vacancy definition tracks the overall work-order position.'}</div>
        <Collapse className="pipeline-definition-details" defaultActiveKey={definition.id ? [] : ['details']} key={`details-${definition.id}`}><Collapse.Panel key="details" header="Pipeline details & SLA" extra={<Switch size="small" disabled={readOnly} checked={definition.isActive} onClick={(_,event) => event.stopPropagation()} onChange={isActive => patchDefinition({isActive})} checkedChildren="Active" unCheckedChildren="Inactive" />}><div className="form-builder-meta"><Form.Item label="Pipeline name" required><Input data-testid="pipeline-name" disabled={readOnly} value={definition.pipelineName} onChange={event => patchDefinition({ pipelineName: event.target.value })} /></Form.Item><Form.Item label="Pipeline code" required><Input data-testid="pipeline-code" disabled={readOnly} value={definition.pipelineCode} onChange={event => patchDefinition({ pipelineCode: code(event.target.value) })} /></Form.Item><Form.Item label="Pipeline scope" required><Select data-testid="pipeline-scope" disabled={readOnly} value={version.scopeType ?? 'Application'} options={[{ value: 'Application', label: 'Candidate / application' }, { value: 'Position', label: 'Work-order position' }, { value: 'Hybrid', label: 'Position + candidate flow' }]} onChange={scopeType => patchVersion({ scopeType, stages: assignStageScopes(version.stages, scopeType) })} /></Form.Item><Form.Item label="SLA basis" required><Select data-testid="pipeline-sla-mode" disabled={readOnly} value={version.slaMode ?? 'StageEntry'} options={[{ value: 'StageEntry', label: 'From each stage entry' }, { value: 'CumulativeFromAnchor', label: 'Cumulative from work order' }]} onChange={slaMode => patchVersion({ slaMode })} /></Form.Item>{version.slaMode === 'CumulativeFromAnchor' && <Form.Item label="Overall SLA (days)" required><InputNumber data-testid="pipeline-overall-sla" disabled={readOnly} min={0.01} precision={2} value={(version.overallSlaMinutes ?? 0) / 1440 || undefined} onChange={value => patchVersion({ overallSlaMinutes: Math.round(Number(value || 0) * 1440) })} /></Form.Item>}<Form.Item className="wide" label="Description"><Input data-testid="pipeline-description" disabled={readOnly} value={definition.description} onChange={event => patchDefinition({ description: event.target.value })} /></Form.Item></div></Collapse.Panel></Collapse>
        {version.stages.some(stage => stage.cardScope === 'Position') && <RecruitmentRollbackFields value={version.allRejectedRollback} stages={version.stages} disabled={readOnly} onChange={allRejectedRollback => patchVersion({ allRejectedRollback })} />}
        {editing && <Alert data-testid="pipeline-editing-notice" type="info" showIcon message="Save changes to this pipeline" description="Existing jobs, applications and history keep their mappings. Removing a stage that is already in use is protected." />}
        {saveValidationError && <Alert data-testid="pipeline-validation-error" type="error" showIcon closable message="Pipeline needs attention" description={saveValidationError} onClose={() => setSaveValidationError('')} />}

        <section className="pipeline-stages-section"><div className="pipeline-section-heading"><div><h3>Stages & responsibility</h3><p>Configure who owns each step and who can see or work on its cards.</p></div><Space wrap><Button disabled={readOnly} onClick={useStandardFlow}>Use standard flow</Button><Button data-testid="pipeline-add-stage" icon={<PlusOutlined />} disabled={readOnly} onClick={addStage}>Add stage</Button></Space></div><div className="pipeline-owner-legend">{pipelineOwnershipLegend.map(owner => <span key={owner.ownerType}><i style={{background:owner.color}} />{owner.label}</span>)}</div><div ref={stageFlowRef} className="pipeline-stage-flow" data-testid="pipeline-stage-flow">{version.stages.map((stage,index) => { const owner=pipelineStageOwnership(stage); return <article key={stage.id} data-testid={`pipeline-stage-${stage.stageCode || index+1}`} style={{'--stage-color':owner.color} as CSSProperties} className={`pipeline-stage-card ${selectedStageId === stage.id ? 'active' : ''}`}>
          <span className="stage-order">{index+1}</span><div className="pipeline-stage-identity"><strong>{stage.stageName}</strong><div><span>{stage.cardScope === 'Position' ? 'Hiring / vacancy' : 'Candidate'}</span><span>{stage.stageType}</span>{stage.isInitial && <Tag>Initial</Tag>}{stage.isTerminal && <Tag>Terminal</Tag>}</div></div><div className="pipeline-stage-owner"><i style={{background:owner.color}}/><span>{owner.label}<small>{stage.accessPolicy?.restricted ? 'Assigned roles / users only' : 'Existing module access'}</small></span></div><div className="pipeline-stage-sla"><small>SLA</small><b>{formatDuration(stage.slaDurationMinutes)}</b></div><Space className="pipeline-stage-actions" size={4} wrap><Button size="small" icon={<EditOutlined />} onClick={() => {setSelectedStageId(stage.id);setStageDrawer(true)}}>{readOnly ? 'View stage' : 'Configure'}</Button><Button size="small" aria-label={`Move ${stage.stageName} up`} icon={<ArrowUpOutlined />} disabled={readOnly || !index} onClick={() => moveStage(stage.id,-1)}/><Button size="small" aria-label={`Move ${stage.stageName} down`} icon={<ArrowDownOutlined />} disabled={readOnly || index === version.stages.length-1} onClick={() => moveStage(stage.id,1)}/><Button size="small" aria-label={`Delete ${stage.stageName}`} danger icon={<DeleteOutlined />} disabled={readOnly} onClick={() => removeStage(stage.id)}/></Space>
        </article>})}</div></section>
        <Collapse className="pipeline-transitions-collapse"><Collapse.Panel key="transitions" header={`Transitions & decision rules (${version.transitions.length})`}><div className="pipeline-section-heading"><p>Only configured transitions can advance a card.</p><Button icon={<PlusOutlined/>} disabled={readOnly || version.stages.length<2} onClick={addTransition}>Add transition</Button></div>
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

        </Collapse.Panel></Collapse>
      </div>
    </section>}
    <Modal open={!!similar} title="Create a similar pipeline" okText="Create copy" confirmLoading={copying} onOk={() => void createSimilar()} onCancel={closeSimilar} okButtonProps={{ disabled: !similar?.source || !similar.sourceVersionId || copySourcesLoading || copyConfigurationLoading || !similar.targetClientId || !similar.pipelineName.trim() || !code(similar.pipelineCode) }}>
      {similar && <Form layout="vertical" className="pipeline-similar-form">
        <Alert type="info" showIcon message={similar.source ? `Copy ${similar.source.pipelineName}` : 'Choose an existing pipeline to copy'} description="Copies stages, transitions, SLA and configuration into a separate pipeline. Review the copy and save to use it. Existing jobs and history stay attached to the original pipeline." />
        <div className="pipeline-copy-source-fields">
          <Form.Item label="Source client"><RecruitmentOfferClientSelect includeSetup data-testid="pipeline-copy-source-client" aria-label="Copy source client" disabled={copying || Boolean(boundClientId)} allowClear={!boundClientId} loading={!clients.length} value={similar.sourceClientId || undefined} placeholder="All permitted source clients" options={clients.map(clientChoice)} onChange={sourceClientId => void loadSimilarSources(Number(sourceClientId || 0))} /></Form.Item>
          <Form.Item label="Source pipeline" required><Select data-testid="pipeline-copy-source-pipeline" aria-label="Copy source pipeline" showSearch allowClear optionFilterProp="label" disabled={copying || copySourcesLoading} loading={copySourcesLoading || copyConfigurationLoading} value={similar.source?.id} placeholder={copySourcesLoading ? 'Loading source pipelines...' : 'Choose a saved hiring or candidate pipeline'} notFoundContent={copySourcesLoading ? 'Loading source pipelines...' : 'No saved pipelines in this client scope.'} options={similarSources.map(row => ({ value: row.id, label: `${row.pipelineName}${pipelineScopes[row.id] || similar.source?.id === row.id && similar.sourceScopeType ? ` · ${scopeLabel(pipelineScopes[row.id] || similar.sourceScopeType)}` : ''}${!similar.sourceClientId ? ` · ${row.clientName || clients.find(client => client.id === row.clientId)?.name || ''}` : ''}` }))} onChange={sourceId => void selectSimilarSource(similarSources.find(row => row.id === sourceId))} /></Form.Item>
          {copyConfigurationLoading && <span className="pipeline-copy-source-status" role="status">Loading the saved source configuration...</span>}
          {similar.source && !copyConfigurationLoading && similar.sourceVersionId > 0 && <div className="pipeline-copy-source-status"><Tag color="purple">{scopeLabel(similar.sourceScopeType)}</Tag><span>{similar.source.clientName || clients.find(row => row.id === similar.source?.clientId)?.name}</span></div>}
          {copySourceError && <Alert type="error" showIcon message="Source pipeline unavailable" description={copySourceError} action={<Button size="small" disabled={copying} onClick={() => void loadSimilarSources(similar.sourceClientId)}>Retry</Button>} />}
        </div>
        <Form.Item label="Target client" required><RecruitmentOfferClientSelect includeSetup data-testid="pipeline-copy-target-client" aria-label="Copy target client" disabled={copying || Boolean(boundClientId)} allowClear={!boundClientId} value={similar.targetClientId || undefined} placeholder="Choose the client receiving the copy" options={clients.map(clientChoice)} onChange={targetClientId => { setCopyError(''); setSimilar({ ...similar, targetClientId: Number(targetClientId || 0) }) }} /></Form.Item>
        <Form.Item label="New pipeline name" required><Input aria-label="Similar pipeline name" disabled={copying} maxLength={200} value={similar.pipelineName} onChange={event => { setCopyError(''); setSimilar({ ...similar, pipelineName: event.target.value }) }} /></Form.Item>
        <Form.Item label="New pipeline code" required><Input aria-label="Similar pipeline code" disabled={copying} maxLength={80} value={similar.pipelineCode} onChange={event => { setCopyError(''); setSimilar({ ...similar, pipelineCode: code(event.target.value) }) }} /></Form.Item>
        {similar.source && similar.targetClientId > 0 && similar.targetClientId !== similar.source.clientId && <Alert type="warning" showIcon message="Review references for the new client" description="Client-specific users, workflows and templates may need to be selected again. The copy will list anything requiring review." />}
        {copyError && <Alert type="error" showIcon message={copyError} />}
      </Form>}
    </Modal>
    <Drawer rootClassName="recruitment-pipeline-stage-drawer pipeline-stage-editor-v2" width="min(860px, 96vw)" title={selectedStage ? `${selectedStage.displayOrder}. ${selectedStage.stageName}` : 'Pipeline stage'} open={stageDrawer && !!selectedStage} onClose={() => setStageDrawer(false)} footer={<div className="pipeline-stage-footer"><span>{readOnly ? 'Select Edit pipeline to make changes.' : 'Changes apply when you save the pipeline.'}</span><Button type="primary" onClick={()=>setStageDrawer(false)}>Done</Button></div>}>{selectedStage && <StageProperties stage={selectedStage} pipelineStages={version?.stages ?? []} lookups={lookups} users={users} roles={roles} clientId={definition?.clientId ?? 0} clientName={clients.find(row => row.id === definition?.clientId)?.name || ''} cumulativeSla={version?.slaMode === 'CumulativeFromAnchor'} dropdowns={dropdowns} onDropdownsChange={onDropdownsChange} readOnly={readOnly} canConfigureAccess={canDelete} patch={value => patchStage(selectedStage.id, value)} makeInitial={() => setInitial(selectedStage.id)} />}</Drawer>
    <Modal title="Interview score components" open={competencyOpen} onCancel={() => setCompetencyOpen(false)} onOk={() => void saveCompetency()} okText="Save component" confirmLoading={competencySaving}>
      <Alert showIcon type="info" message="Client-scoped scoring library" description="Create normalized components once, then reuse them in any interview stage with stage-specific maximum and minimum scores." />
      <div style={{ margin: '16px 0' }} data-testid="pipeline-competency-library"><Space wrap>{(lookups.interviewCompetencies ?? []).filter(row => row.isActive).map(row => <Tag key={row.id}>{row.competencyName} ({row.competencyCode})</Tag>)}</Space></div>
      <Form layout="vertical"><Form.Item label="Component code" required><Input data-testid="pipeline-competency-code" value={competencyDraft.competencyCode} onChange={event => setCompetencyDraft(current => ({ ...current, competencyCode: code(event.target.value) }))} /></Form.Item><Form.Item label="Component name" required><Input data-testid="pipeline-competency-name" value={competencyDraft.competencyName} onChange={event => setCompetencyDraft(current => ({ ...current, competencyName: event.target.value }))} /></Form.Item><Form.Item label="Description"><Input.TextArea data-testid="pipeline-competency-description" rows={3} value={competencyDraft.description} onChange={event => setCompetencyDraft(current => ({ ...current, description: event.target.value }))} /></Form.Item></Form>
    </Modal>
  </section>
}

function StageProperties({ stage, pipelineStages, lookups, users, roles, clientId, clientName, cumulativeSla, dropdowns, onDropdownsChange, readOnly, canConfigureAccess, patch, makeInitial }: { stage: RecruitmentPipelineStage; pipelineStages: RecruitmentPipelineStage[]; lookups: RecruitmentOrchestrationLookups; users: AuthUser[]; roles: AuthRole[]; clientId: number; clientName: string; cumulativeSla: boolean; dropdowns: Drop[]; onDropdownsChange: (rows: Drop[]) => void; readOnly: boolean; canConfigureAccess: boolean; patch: (value: Partial<RecruitmentPipelineStage>) => void; makeInitial: () => void }) {
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
  const access = { ownerType: 'Unassigned' as const, departmentLabel: '', restricted: false, allowedRoleCodes: [] as string[], allowedUserIds: [] as number[], ...stage.accessPolicy }
  const patchAccess = (value: Partial<NonNullable<RecruitmentPipelineStage['accessPolicy']>>) => patch({accessPolicy:{...access,...value}})
  return <Form layout="vertical">
    <Card size="small" className="pipeline-stage-access-card" title="Responsibility & access"><div className="field-property-grid"><Form.Item label="Responsible team"><Select data-testid="pipeline-stage-owner-type" disabled={readOnly || !canConfigureAccess} value={access.ownerType} options={pipelineOwnershipLegend.map(owner=>({value:owner.ownerType,label:owner.label}))} onChange={ownerType=>patchAccess({ownerType})}/></Form.Item><Form.Item label="Department / team name"><Input data-testid="pipeline-stage-owner-label" disabled={readOnly || !canConfigureAccess} value={access.departmentLabel} maxLength={100} placeholder="For example: Project Cell, GAD Recruitment" onChange={event=>patchAccess({departmentLabel:event.target.value})}/></Form.Item><Form.Item className="wide"><Checkbox data-testid="pipeline-stage-restricted" disabled={readOnly || !canConfigureAccess} checked={access.restricted} onChange={event=>patchAccess({restricted:event.target.checked})}>Only assigned roles or users can view and operate this stage</Checkbox></Form.Item>{access.restricted && <><Form.Item className="wide" label="Allowed roles"><Select data-testid="pipeline-stage-access-roles" disabled={readOnly || !canConfigureAccess} mode="multiple" showSearch optionFilterProp="label" value={access.allowedRoleCodes} options={roles.map(role=>({value:role.code,label:`${role.name} (${role.code})`}))} onChange={allowedRoleCodes=>patchAccess({allowedRoleCodes})} placeholder="Select permitted roles"/></Form.Item><Form.Item className="wide" label="Allowed users"><Select data-testid="pipeline-stage-access-users" disabled={readOnly || !canConfigureAccess} mode="multiple" showSearch optionFilterProp="label" value={access.allowedUserIds} options={availableUsers.map(user=>({value:user.id,label:`${user.displayName || user.email} (${user.email})`}))} onChange={allowedUserIds=>patchAccess({allowedUserIds})} placeholder="Select permitted users"/></Form.Item></>}</div><Alert showIcon type={access.restricted && !access.allowedRoleCodes.length && !access.allowedUserIds.length ? 'warning' : 'info'} message={access.restricted ? !access.allowedRoleCodes.length && !access.allowedUserIds.length ? 'No grants selected · platform administrators only' : 'Assigned roles / users still need module and client access' : 'Existing module and client permissions apply'} description={canConfigureAccess ? 'Color identifies the responsible team. Access is enforced separately by the API; platform administrators retain visibility within their permitted clients.' : 'A platform administrator manages stage ownership and grants. Color alone does not grant permission.'}/></Card>
    <div className="recruitment-config-heading"><b>Stage essentials</b><span>Name the step, choose its purpose and set the expected completion time.</span></div>
    <div className="field-property-grid"><Form.Item label="Stage name" required><Input data-testid="pipeline-stage-name" disabled={readOnly} value={stage.stageName} onChange={event => patch({ stageName: event.target.value, stageCode: stage.id < 0 ? code(event.target.value) : stage.stageCode })} /></Form.Item><Form.Item label="Stage type"><Select data-testid="pipeline-stage-type" disabled={readOnly} showSearch optionFilterProp="label" value={stage.stageType} options={stageTypes.map(value => ({ value, label: value.replace(/([A-Z])/g, ' $1').trim() }))} onChange={stageType => patch(stageTypePatch(stage, stageType))} /></Form.Item><Form.Item label="Cards shown in this stage" required><Select data-testid="pipeline-stage-card-scope" disabled={readOnly} value={stage.cardScope ?? 'Application'} options={[{ value: 'Position', label: 'Hiring demand / work-order role' }, { value: 'Application', label: 'Candidate / application' }]} onChange={cardScope => patch({ cardScope })} /></Form.Item>{cumulativeSla ? <Form.Item label="Cumulative target (days)" required><InputNumber data-testid="pipeline-stage-target-days" disabled={readOnly} min={0} precision={2} value={stage.targetOffsetMinutes == null ? undefined : stage.targetOffsetMinutes / 1440} onChange={value => patch({ targetOffsetMinutes: value == null ? null : Math.round(Number(value) * 1440) })} /></Form.Item> : <Form.Item label="Complete within (hours)"><InputNumber disabled={readOnly} min={0} value={stage.slaDurationMinutes / 60} onChange={value => patch({ slaDurationMinutes: Math.round(Number(value || 0) * 60) })} /></Form.Item>}</div>
    <Collapse className="recruitment-advanced-collapse"><Collapse.Panel key="stage-controls" header="Advanced stage controls"><div className="field-property-grid"><Form.Item label="Stage code" required><Input data-testid="pipeline-stage-code" disabled={readOnly} value={stage.stageCode} onChange={event => patch({ stageCode: code(event.target.value) })} /></Form.Item><Form.Item label="SLA stakeholder code"><Input data-testid="pipeline-stage-stakeholder" disabled={readOnly} value={stage.stakeholderCode ?? ''} placeholder="GAD, UIDAI, CLIENT_HR" onChange={event => patch({ stakeholderCode: code(event.target.value) })} /></Form.Item><Form.Item label="Warning before due (hours)"><InputNumber disabled={readOnly} min={0} value={stage.slaWarningMinutes / 60} onChange={value => patch({ slaWarningMinutes: Math.round(Number(value || 0) * 60) })} /></Form.Item><Form.Item label="Pause effect"><Select data-testid="pipeline-stage-pause-behavior" disabled={readOnly || stage.allowPause === false} value={stage.pauseBehavior ?? 'ShiftStageAndOverall'} options={[{ value: 'ShiftStageAndOverall', label: 'Shift stage + overall due' }, { value: 'ShiftStageOnly', label: 'Shift this stage only' }, { value: 'NoShift', label: 'Audit pause; do not shift due' }]} onChange={pauseBehavior => patch({ pauseBehavior })} /></Form.Item><Form.Item className="wide" label="Approval workflow"><Select data-testid="pipeline-stage-approval-workflow" disabled={readOnly} allowClear showSearch optionFilterProp="label" value={stage.approvalWorkflowId || undefined} onChange={approvalWorkflowId => patch({ approvalWorkflowId: approvalWorkflowId || null, requiresApproval: Boolean(approvalWorkflowId) })} options={workflowOptions} placeholder="No approval required" /></Form.Item><Form.Item className="wide"><Space direction="vertical"><Checkbox disabled={readOnly || stage.isInitial} checked={stage.isInitial} onChange={event => event.target.checked && makeInitial()}>Initial stage</Checkbox><Checkbox data-testid="pipeline-stage-terminal" disabled={readOnly} checked={stage.isTerminal} onChange={event => patch({ isTerminal: event.target.checked })}>Terminal stage</Checkbox><Checkbox disabled={readOnly} checked={stage.calendarEnabled} onChange={event => patch({ calendarEnabled: event.target.checked })}>Calendar enabled</Checkbox><Checkbox disabled={readOnly} checked={stage.allowSkip} onChange={event => patch({ allowSkip: event.target.checked })}>Allow controlled skip</Checkbox><Checkbox data-testid="pipeline-stage-allow-pause" disabled={readOnly} checked={stage.allowPause ?? true} onChange={event => patch({ allowPause: event.target.checked })}>Allow SLA pause with reason</Checkbox><Checkbox disabled={readOnly} checked={stage.isActive} onChange={event => patch({ isActive: event.target.checked })}>Active</Checkbox></Space></Form.Item></div></Collapse.Panel></Collapse>
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
      <Form.Item className="wide"><Checkbox data-testid="pipeline-negotiation-sla-enabled" disabled={readOnly} checked={offer.negotiationSlaExtensionEnabled ?? false} onChange={event => patch({ offerConfiguration: { ...offer, negotiationSlaExtensionEnabled: event.target.checked } })}>Extend SLA automatically when offer negotiation starts</Checkbox></Form.Item>
      {offer.negotiationSlaExtensionEnabled && <>
        <Form.Item label="Negotiation threshold %" tooltip="Uses the CTC flexibility selected on the hiring request."><InputNumber data-testid="pipeline-negotiation-threshold" disabled={readOnly} min={0} max={100} precision={2} value={offer.negotiationThresholdPercent ?? 30} onChange={value => patch({ offerConfiguration: { ...offer, negotiationThresholdPercent: Number(value ?? 30) } })} /></Form.Item>
        <Form.Item label="Extra SLA days"><InputNumber data-testid="pipeline-negotiation-extra-days" disabled={readOnly} min={0.01} max={365} precision={2} value={(offer.negotiationSlaExtensionMinutes ?? 0) / 1440 || undefined} onChange={value => patch({ offerConfiguration: { ...offer, negotiationSlaExtensionMinutes: Math.round(Number(value || 0) * 1440) } })} /></Form.Item>
        <Form.Item className="wide" label="Also extend stage targets" tooltip="Overall hiring SLA is always extended. Select MOA/approval stages whose individual due date must move too."><Select data-testid="pipeline-negotiation-stage-targets" disabled={readOnly} mode="multiple" showSearch optionFilterProp="label" placeholder="Select MOA / approval stages" value={(offer.negotiationSlaStageCodes ?? '').split(',').filter(Boolean)} options={pipelineStages.filter(row => row.id !== stage.id).map(row => ({ value: row.stageCode, label: `${row.displayOrder}. ${row.stageName}` }))} onChange={values => patch({ offerConfiguration: { ...offer, negotiationSlaStageCodes: values.join(',') } })} /></Form.Item>
      </>}
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
    {needsDocuments && <Card size="small" title="Requested global documents" extra={<Button disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addDocument}>Add document</Button>}>{!stage.attachmentRequirements.length && <Alert showIcon type="info" message="Bind global attachment field configurations; their file limits and permissions remain authoritative." />}{stage.attachmentRequirements.map(document => <div className="field-option-row" key={document.id} style={{ gridTemplateColumns: '1fr 100px 100px auto auto' }}><Select disabled={readOnly} showSearch optionFilterProp="label" value={document.attachmentFieldConfigurationId || undefined} placeholder="Attachment field configuration" onChange={attachmentFieldConfigurationId => { const config = lookups.attachmentFieldConfigurations.find(row => row.id === attachmentFieldConfigurationId); patchDocument(document.id, { attachmentFieldConfigurationId, maximumFileCount: config?.maximumFileCount ?? 1, minimumFileCount: config?.minimumFileCount ?? 1 }) }} options={lookups.attachmentFieldConfigurations.filter(row => row.isActive && (row.clientId === 0 || row.clientId === clientId)).map(row => ({ value: row.id, label: attachmentConfigurationLabel(row) }))} /><InputNumber disabled={readOnly} min={0} value={document.minimumFileCount} onChange={minimumFileCount => patchDocument(document.id, { minimumFileCount: Number(minimumFileCount || 0) })} /><InputNumber disabled={readOnly} min={1} value={document.maximumFileCount} onChange={maximumFileCount => patchDocument(document.id, { maximumFileCount: Number(maximumFileCount || 1) })} /><Checkbox disabled={readOnly} checked={document.requiresVerification} onChange={event => patchDocument(document.id, { requiresVerification: event.target.checked })}>Verify</Checkbox><Button disabled={readOnly} danger icon={<DeleteOutlined />} onClick={() => removeDocument(document.id)} /></div>)}</Card>}
    <Collapse className="recruitment-advanced-collapse"><Collapse.Panel key="automation" header={`Advanced automation (${stage.actions.length})`}><Card bordered={false} size="small" extra={<Button data-testid="pipeline-add-action" disabled={readOnly} size="small" icon={<PlusOutlined />} onClick={addAction}>Add action</Button>}>
      {!stage.actions.length && <p className="orchestration-subtitle">Configure relational stage hooks. Mail uses notification templates, approvals use the existing workflow engine, and SLA hooks run automatically.</p>}
      {stage.actions.map((action, actionIndex) => {
        const candidateFacing = ['ExternalForm', 'Documents', 'PreOnboarding', 'Offer'].includes(stage.stageType)
        const canAutoReject = stage.cardScope === 'Application' && !stage.isTerminal
        const actionOptions = ['SEND_NOTIFICATION', 'START_WORKFLOW', ...(candidateFacing ? ['GENERATE_ACTION_LINK'] : []), ...(stage.stageType === 'ATS' ? ['RUN_ATS_SCORE'] : []), ...(canAutoReject ? ['AUTO_REJECT'] : [])]
        const triggerOptions = action.actionCode === 'AUTO_REJECT'
          ? ['OnSlaBreach']
          : ['OnEntry', 'OnExit', 'OnSlaWarning', 'OnSlaBreach', 'OnApproval', 'OnProfileBatchForward', ...(candidateFacing ? ['OnSubmission'] : []), ...(stage.stageType === 'Interview' ? ['OnInterviewScheduled', 'OnInterviewRescheduled'] : [])]
        return <div className="stage-action-block" key={action.id}><div className="stage-action-row">
          <Select data-testid="pipeline-action-trigger" disabled={readOnly} value={action.triggerEvent} onChange={triggerEvent => patchAction(action.id, { triggerEvent, isBlocking: ['OnEntry', 'OnSubmission'].includes(triggerEvent) ? action.isBlocking : false })} options={triggerOptions.map(value => ({ value, label: value.replace(/([A-Z])/g, ' $1').trim() }))} />
          <Select data-testid="pipeline-action-code" disabled={readOnly} value={action.actionCode} onChange={actionCode => patchAction(action.id, { actionCode, triggerEvent: actionCode === 'AUTO_REJECT' ? 'OnSlaBreach' : action.triggerEvent, isBlocking: actionCode === 'AUTO_REJECT' ? false : action.isBlocking, workflowId: actionCode === 'START_WORKFLOW' ? action.workflowId : null, templateId: actionCode === 'SEND_NOTIFICATION' ? action.templateId : null })} options={actionOptions.map(value => ({ value, label: value.replaceAll('_', ' ') }))} />
          {action.actionCode === 'START_WORKFLOW' ? <Select disabled={readOnly} showSearch optionFilterProp="label" placeholder="Approval workflow" value={action.workflowId || undefined} onChange={workflowId => patchAction(action.id, { workflowId: workflowId || null })} options={stageActionWorkflowOptions} /> : action.actionCode === 'SEND_NOTIFICATION' ? <Select data-testid="pipeline-action-template" disabled={readOnly} showSearch optionFilterProp="label" placeholder="Notification template" value={action.templateId || undefined} onChange={templateId => patchAction(action.id, { templateId: templateId || null })} options={(lookups.templates ?? []).filter(row => row.isActive).map(row => ({ value: row.id, label: `${row.templateName} (${row.templateCode})` }))} /> : <span className="stage-action-runtime-note">{action.actionCode === 'AUTO_REJECT' ? 'Reject only when no human activity is logged before SLA breach' : 'No extra configuration'}</span>}
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
  const first = newStage(1, true, 'Application'); const terminal = { ...newStage(2, false, 'Application'), stageCode: 'REJECTED', stageName: 'Rejected', stageType: 'Rejected', isTerminal: true }
  const reject = { ...newTransition(0, first, terminal, 1), outcomeCode: 'REJECT', actionLabel: 'Reject candidate', requiresReason: true }
  const restore = { ...newTransition(0, terminal, first, 2), outcomeCode: 'RETURN_TO_INTAKE', actionLabel: 'Return to candidate intake', requiresReason: true }
  return { id: 0, pipelineDefinitionId, versionNumber: 1, status: 'Draft', scopeType: 'Application', slaMode: 'StageEntry', overallSlaMinutes: 0, stages: [first, terminal], transitions: [reject, restore] }
}
function standardHiringFlow(pipelineVersionId: number, scopeType: NonNullable<RecruitmentPipelineVersion['scopeType']>): Pick<RecruitmentPipelineVersion, 'stages' | 'transitions'> & Partial<Pick<RecruitmentPipelineVersion, 'slaMode' | 'overallSlaMinutes'>> {
  const specs = scopeType === 'Position'
    ? [
        { name: 'Order for Hiring (Work Order issued)', stageCode: 'ORDER_FOR_HIRING', type: 'Screening', hours: 0, targetDays: 0, cardScope: 'Position' as const },
        { name: 'Sharing of Profiles for Interview', stageCode: 'SHARING_PROFILES', type: 'Screening', hours: 25 * 24, targetDays: 25, cardScope: 'Position' as const },
        { name: 'Interview / Panel Assessment', stageCode: 'PANEL_ASSESSMENT', type: 'Interview', hours: 5 * 24, targetDays: 30, cardScope: 'Position' as const },
        { name: 'Signing of MOM', stageCode: 'SIGNING_MOM', type: 'Approval', hours: 3 * 24, targetDays: 33, cardScope: 'Position' as const },
        { name: 'Negotiation with Selected Candidate(s) & Sharing of MOM with HR Division', stageCode: 'NEGOTIATION_AND_MOM_TO_HR', type: 'HR', hours: 5 * 24, targetDays: 38, cardScope: 'Position' as const },
        { name: 'Conveying of Approval by HR Division', stageCode: 'HR_DIVISION_APPROVAL', type: 'Approval', hours: 2 * 24, targetDays: 40, cardScope: 'Position' as const },
        { name: 'Issuance of Offer to Selected Candidate', stageCode: 'OFFER_ISSUANCE', type: 'Offer', hours: 3 * 24, targetDays: 43, cardScope: 'Position' as const },
        { name: 'Conveying of Joining Date', stageCode: 'JOINING_DATE', type: 'Completed', hours: 2 * 24, targetDays: 45, cardScope: 'Position' as const },
        { name: 'Rejected', stageCode: 'REJECTED', type: 'Rejected', hours: 0, targetDays: 45, cardScope: 'Position' as const },
      ]
    : scopeType === 'Hybrid'
      ? [
          { name: 'Work Order Intake', type: 'Screening', hours: 24, cardScope: 'Position' as const },
          { name: 'Hiring Request and Approval', type: 'Approval', hours: 48, cardScope: 'Position' as const },
          { name: 'JD and Publishing', type: 'Screening', hours: 48, cardScope: 'Position' as const },
          { name: 'Resume Screening', type: 'ATS', hours: 24, cardScope: 'Application' as const },
          { name: 'Stakeholder Review', type: 'Screening', hours: 48, cardScope: 'Application' as const },
          { name: 'Interview Coordination', type: 'Interview', hours: 48, cardScope: 'Application' as const },
          { name: 'Decision and Offer', type: 'Offer', hours: 48, cardScope: 'Application' as const },
          { name: 'Joining Readiness', type: 'Joining', hours: 48, cardScope: 'Application' as const },
          { name: 'Hired', type: 'Completed', hours: 0, cardScope: 'Application' as const },
          { name: 'Not Selected', type: 'Rejected', hours: 0, cardScope: 'Application' as const },
        ]
      : [
          { name: 'Application Screening', type: 'Screening', hours: 24, cardScope: 'Application' as const },
          { name: 'ATS Resume Match', type: 'ATS', hours: 4, cardScope: 'Application' as const },
          { name: 'Stakeholder Review', type: 'Screening', hours: 24, cardScope: 'Application' as const },
          { name: 'Technical Interview', type: 'Interview', hours: 48, cardScope: 'Application' as const },
          { name: 'HR Review', type: 'HR', hours: 24, cardScope: 'Application' as const },
          { name: 'Offer', type: 'Offer', hours: 48, cardScope: 'Application' as const },
          { name: 'Hired', type: 'Completed', hours: 0, cardScope: 'Application' as const },
          { name: 'Not Selected', type: 'Rejected', hours: 0, cardScope: 'Application' as const },
        ]
  const stages = specs.map((spec, index) => {
    const source = newStage(index + 1, index === 0, spec.cardScope)
    const terminal = ['Rejected', 'Withdrawn', 'Completed'].includes(spec.type)
    const base = { ...source, cardScope: spec.cardScope, stageName: spec.name, stageCode: 'stageCode' in spec ? spec.stageCode : code(spec.name), slaDurationMinutes: spec.hours * 60, slaWarningMinutes: spec.hours > 4 ? 4 * 60 : spec.hours ? 60 : 0, targetOffsetMinutes: 'targetDays' in spec ? spec.targetDays * 1440 : null, isInitial: index === 0, isTerminal: terminal }
    return { ...base, ...stageTypePatch(base, spec.type), isInitial: index === 0, isTerminal: terminal }
  })
  const firstCandidateStage = stages.find(stage => stage.cardScope === 'Application' && !stage.isTerminal)
  if (firstCandidateStage && firstCandidateStage.slaDurationMinutes > 0) {
    firstCandidateStage.actions = [{
      id: localId(), pipelineStageId: firstCandidateStage.id, triggerEvent: 'OnSlaBreach', actionCode: 'AUTO_REJECT',
      executionOrder: 1, isBlocking: false, workflowId: null, templateId: null, isActive: true, recipients: [],
    }]
  }
  const rejected = stages.find(stage => stage.stageType === 'Rejected')
  const progressStages = stages.filter(stage => stage.stageType !== 'Rejected')
  const transitions = progressStages.slice(0, -1).map((stage, index) => ({ ...newTransition(pipelineVersionId, stage, progressStages[index + 1], index + 1), actionLabel: `Move to ${progressStages[index + 1].stageName}` }))
  if (rejected) {
    for (const stage of progressStages.filter(stage => stage.cardScope === rejected.cardScope && !stage.isTerminal)) {
      transitions.push({ ...newTransition(pipelineVersionId, stage, rejected, transitions.length + 1), outcomeCode: 'REJECT', actionLabel: rejected.cardScope === 'Position' ? 'Close hiring demand as rejected' : 'Reject candidate', requiresReason: true })
    }
    const recoveryTarget = progressStages.find(stage => stage.cardScope === rejected.cardScope && !stage.isTerminal)
    if (recoveryTarget) transitions.push({ ...newTransition(pipelineVersionId, rejected, recoveryTarget, transitions.length + 1), outcomeCode: 'RETURN_TO_INTAKE', actionLabel: rejected.cardScope === 'Position' ? 'Reopen hiring demand' : 'Return to candidate intake', requiresReason: true })
  }
  return scopeType === 'Position'
    ? { stages, transitions, slaMode: 'CumulativeFromAnchor', overallSlaMinutes: 45 * 1440 }
    : { stages, transitions }
}
function newStage(displayOrder: number, isInitial = false, cardScope: 'Position' | 'Application' = 'Application'): RecruitmentPipelineStage {
  const candidate = cardScope === 'Application'
  return { id: localId(), pipelineVersionId: 0, stageCode: isInitial ? (candidate ? 'NEW' : 'WORK_ORDER_INTAKE') : `STAGE_${displayOrder}`, stageName: isInitial ? (candidate ? 'New applications' : 'Work Order Intake') : `Stage ${displayOrder}`, stageType: 'Screening', cardScope, stageNumber: displayOrder, displayOrder, slaDurationMinutes: 48 * 60, slaWarningMinutes: 4 * 60, targetOffsetMinutes: null, stakeholderCode: '', allowPause: true, pauseBehavior: 'ShiftStageAndOverall', approvalWorkflowId: null, requiresApproval: false, calendarEnabled: false, allowSkip: false, isInitial, isTerminal: false, isActive: true, actions: [], atsConfiguration: null, externalFormConfiguration: null, attachmentRequirements: [], processDocumentRequirements: [], offerConfiguration: null, interviewConfiguration: null, defaultPanelMembers: [] }
}
function newTransition(pipelineVersionId: number, from: RecruitmentPipelineStage, to: RecruitmentPipelineStage, displayOrder: number): RecruitmentPipelineTransition {
  return { id: localId(), pipelineVersionId, fromStageId: from.id, toStageId: to.id, fromStageCode: from.stageCode, toStageCode: to.stageCode, outcomeCode: `TO_${to.stageCode}`, actionLabel: `Move to ${to.stageName}`, approvalWorkflowId: null, requiresReason: false, isActive: true, displayOrder, rules: [] }
}
function newTransitionRule(transitionId: number, displayOrder: number): RecruitmentPipelineTransitionRule {
  return { id: localId(), transitionId, ruleType: 'ATS_SCORE', comparisonOperator: 'GTE', decimalValue: 60, integerValue: null, textValue: null, booleanValue: null, isMandatory: true, errorMessage: '', displayOrder }
}
function stageTypePatch(stage: RecruitmentPipelineStage, stageType: string): Partial<RecruitmentPipelineStage> {
  const candidateFacing = ['ExternalForm', 'Documents', 'PreOnboarding', 'Offer'].includes(stageType)
  const canAutoReject = stage.cardScope === 'Application' && !['Rejected', 'Withdrawn', 'Completed'].includes(stageType)
  return {
    stageType, isTerminal: ['Rejected', 'Withdrawn', 'Completed'].includes(stageType), calendarEnabled: stageType === 'Interview' || stage.calendarEnabled,
    actions: stage.actions.filter(action => (action.actionCode !== 'GENERATE_ACTION_LINK' || candidateFacing) && (action.actionCode !== 'RUN_ATS_SCORE' || stageType === 'ATS') && (action.actionCode !== 'AUTO_REJECT' || canAutoReject) && (action.triggerEvent !== 'OnSubmission' || candidateFacing)),
    atsConfiguration: stageType === 'ATS' ? stage.atsConfiguration ?? { id: 0, pipelineStageId: stage.id, scoringProfileId: null, minimumAdvanceScore: 60, maximumRejectScore: 30, autoScoreOnEntry: true, autoAdvance: false, autoReject: false, requireHumanConfirmation: true, advanceOutcomeCode: 'SHORTLIST', rejectOutcomeCode: 'REJECT' } : null,
    externalFormConfiguration: ['ExternalForm', 'Documents', 'PreOnboarding'].includes(stageType) ? stage.externalFormConfiguration ?? { id: 0, pipelineStageId: stage.id, formVersionId: 0, submissionRequired: true, allowSaveDraft: true, actionTokenValidityMinutes: 10080, actionTokenMaximumUses: 20 } : null,
    interviewConfiguration: stageType === 'Interview' ? stage.interviewConfiguration ?? { id: 0, pipelineStageId: stage.id, roundNumber: stage.stageNumber, interviewType: 'Technical', defaultDurationMinutes: 60, minimumPanelCount: 1, minimumPassingScore: 60, scoreInputMode: 'PercentageWeighted', panelAggregationMethod: 'Average', feedbackRequired: true, calendarEnabled: true, allowReschedule: true, competencies: [] } : null,
    defaultPanelMembers: stageType === 'Interview' ? stage.defaultPanelMembers ?? [] : [],
    offerConfiguration: stageType === 'Offer' ? stage.offerConfiguration ?? { id: 0, pipelineStageId: stage.id, offerTemplateId: null, approvalWorkflowId: null, budgetBasis: 'ApprovedMaximum', maximumVariancePercent: 0, requireApprovalWhenVarianceExceeded: false, varianceApprovalWorkflowId: null, candidateResponseValidityDays: 7, requireAcceptedOfferToAdvance: true, negotiationSlaExtensionEnabled: true, negotiationThresholdPercent: 30, negotiationSlaExtensionMinutes: 5 * 1440, negotiationSlaStageCodes: 'HR_DIVISION_APPROVAL,OFFER_ISSUANCE,JOINING_DATE' } : null,
    attachmentRequirements: ['Documents', 'PreOnboarding'].includes(stageType) ? stage.attachmentRequirements : [],
  }
}
function defaultNewStageScope(version: RecruitmentPipelineVersion): 'Position' | 'Application' {
  if (version.scopeType === 'Position') return 'Position'
  if (version.scopeType === 'Application') return 'Application'
  return version.stages.some(stage => (stage.cardScope ?? inferStageCardScope(stage)) === 'Application') ? 'Application' : 'Position'
}
function assignStageScopes(stages: RecruitmentPipelineStage[], scopeType: NonNullable<RecruitmentPipelineVersion['scopeType']>) {
  if (scopeType === 'Application') return stages.map(stage => ({ ...stage, cardScope: 'Application' as const }))
  if (scopeType === 'Position') return stages.map(stage => ({ ...stage, cardScope: 'Position' as const }))
  let applicationFlowStarted = false
  return stages.map((stage, index) => {
    if (inferStageCardScope(stage) === 'Application' || (!applicationFlowStarted && index >= Math.max(1, Math.floor(stages.length / 2)))) applicationFlowStarted = true
    return { ...stage, cardScope: applicationFlowStarted ? 'Application' as const : 'Position' as const }
  })
}
function normalizeLoadedStageScopes(version: RecruitmentPipelineVersion) {
  if (version.stages.every(stage => stage.cardScope === 'Position' || stage.cardScope === 'Application')) return version.stages
  return assignStageScopes(version.stages, version.scopeType ?? 'Application')
}
function inferStageCardScope(stage: RecruitmentPipelineStage): 'Position' | 'Application' {
  const value = `${stage.stageName} ${stage.stageCode} ${stage.stageType}`.toLowerCase()
  return /(candidate|application|resume|profile|ats|interview|offer|onboard|joining|hired|reject|withdraw)/.test(value) ? 'Application' : 'Position'
}
function validate(definition: RecruitmentPipelineDefinition, version: RecruitmentPipelineVersion) {
  if (!definition.clientId || !definition.pipelineName.trim() || !code(definition.pipelineCode || definition.pipelineName)) return 'Client, pipeline name and code are required.'
  if (version.stages.length < 2) return 'A pipeline needs at least two stages.'
  if (version.stages.filter(row => row.isInitial).length !== 1) return 'Select exactly one initial stage.'
  if (!version.stages.some(row => row.isTerminal)) return 'Add at least one terminal stage.'
  if (new Set(version.stages.map(row => code(row.stageCode))).size !== version.stages.length) return 'Stage codes must be unique.'
  const scopes = version.stages.map(stage => stage.cardScope ?? (version.scopeType === 'Position' ? 'Position' : 'Application'))
  if (version.scopeType === 'Application' && scopes.some(scope => scope !== 'Application')) return 'An Application pipeline can contain only candidate stages.'
  if (version.scopeType === 'Position' && scopes.some(scope => scope !== 'Position')) return 'A Position pipeline can contain only hiring-demand stages.'
  if (version.scopeType === 'Hybrid') {
    if (!scopes.includes('Position') || !scopes.includes('Application')) return 'A Hybrid pipeline needs at least one hiring-demand stage and one candidate stage.'
    const firstApplication = scopes.indexOf('Application')
    if (scopes.slice(firstApplication).includes('Position')) return 'In a Hybrid pipeline, keep all hiring-demand stages before candidate stages.'
  }
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
  if (version.stages.some(stage => stage.offerConfiguration?.negotiationSlaExtensionEnabled && (stage.offerConfiguration.negotiationThresholdPercent < 0 || stage.offerConfiguration.negotiationThresholdPercent > 100 || stage.offerConfiguration.negotiationSlaExtensionMinutes <= 0))) return 'Complete the negotiation SLA threshold and extra days on the offer stage.'
  if (version.stages.some(stage => stage.stageType === 'Interview' && stage.interviewConfiguration && stage.interviewConfiguration.competencies.length > 0 && Math.abs(stage.interviewConfiguration.competencies.reduce((total, row) => total + Number(row.weightPercent || 0), 0) - 100) > 0.01)) return 'Interview competency weights must total exactly 100% in every interview round.'
  if (version.stages.some(stage => stage.stageType === 'Interview' && (stage.defaultPanelMembers?.length ?? 0) > 0 && (stage.defaultPanelMembers?.length ?? 0) < (stage.interviewConfiguration?.minimumPanelCount ?? 1))) return 'Each configured default panel must meet the stage minimum panel count.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'START_WORKFLOW' && !action.workflowId))) return 'Select a workflow for every START WORKFLOW stage action.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'SEND_NOTIFICATION' && !action.templateId))) return 'Select a notification template for every SEND NOTIFICATION stage action.'
  if (version.stages.some(stage => stage.actions.some(action => (action.recipients ?? []).some(recipient => recipient.recipientType === 'SpecificUser' && !recipient.userId)))) return 'Choose a user for every specific-user notification recipient.'
  if (version.stages.some(stage => stage.actions.some(action => (action.recipients ?? []).some(recipient => recipient.recipientType === 'UserRole' && !recipient.roleCode.trim())))) return 'Choose a role for every role-based notification recipient.'
  if (version.stages.some(stage => stage.actions.some(action => (action.recipients ?? []).some(recipient => recipient.recipientType === 'StaticEmail' && !/^\S+@\S+\.\S+$/.test(recipient.emailAddress))))) return 'Enter a valid static notification recipient email.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'GENERATE_ACTION_LINK' && (action.triggerEvent !== 'OnEntry' || !['ExternalForm', 'Documents', 'PreOnboarding', 'Offer'].includes(stage.stageType))))) return 'Candidate action links require On Entry on a candidate-facing stage.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'RUN_ATS_SCORE' && stage.stageType !== 'ATS'))) return 'RUN ATS SCORE can be configured only on an ATS stage.'
  if (version.stages.some(stage => stage.actions.some(action => action.actionCode === 'AUTO_REJECT' && (action.triggerEvent !== 'OnSlaBreach' || stage.cardScope !== 'Application' || stage.isTerminal)))) return 'AUTO REJECT requires SLA breach on a non-terminal candidate stage.'
  if (version.stages.some(stage => stage.actions.some(action => action.isBlocking && !['OnEntry', 'OnSubmission'].includes(action.triggerEvent)))) return 'Blocking stage actions must run on entry or candidate submission.'
  if (version.stages.some(stage => Array.from(new Set(stage.actions.map(action => action.triggerEvent))).some(trigger => { const orders = stage.actions.filter(action => action.triggerEvent === trigger).map(action => action.executionOrder); return new Set(orders).size !== orders.length }))) return 'Stage action order must be unique within each trigger.'
  if (version.stages.some(stage => Array.from(new Set(stage.actions.map(action => action.triggerEvent))).some(trigger => { const rows = stage.actions.filter(action => action.triggerEvent === trigger); const link = rows.find(action => action.actionCode === 'GENERATE_ACTION_LINK'); const notification = rows.find(action => action.actionCode === 'SEND_NOTIFICATION'); return link && notification && notification.executionOrder <= link.executionOrder }))) return 'Generate candidate links before sending their notification.'
  const nonTerminal = version.stages.filter(row => !row.isTerminal && row.isActive)
  if (nonTerminal.some(stage => !version.transitions.some(row => row.fromStageId === stage.id && row.isActive))) return 'Every active non-terminal stage needs at least one outgoing transition.'
  return ''
}
function pipelineEditSnapshot(definition: RecruitmentPipelineDefinition, configuration: RecruitmentPipelineVersion) {
  return JSON.stringify({
    definition: { clientId: definition.clientId, pipelineCode: definition.pipelineCode, pipelineName: definition.pipelineName, description: definition.description, isActive: definition.isActive },
    configuration: { id: configuration.id, scopeType: configuration.scopeType, slaMode: configuration.slaMode, overallSlaMinutes: configuration.overallSlaMinutes, allRejectedRollback: configuration.allRejectedRollback, stages: configuration.stages, transitions: configuration.transitions },
  })
}

function formatDuration(minutes: number) { if (!minutes) return 'None'; if (minutes % 1440 === 0) return `${minutes / 1440}d`; if (minutes % 60 === 0) return `${minutes / 60}h`; return `${minutes}m` }
function scopeLabel(scope: RecruitmentPipelineVersion['scopeType']) { return scope === 'Position' ? 'Hiring / vacancy' : scope === 'Hybrid' ? 'Combined hiring + candidates' : 'Candidate journey' }
function equalCompetencies(ids: number[], configurationId: number, lookups: RecruitmentOrchestrationLookups) {
  if (!ids.length) return []
  const base = Math.floor(10000 / ids.length) / 100
  return ids.map((competencyId, index) => { const option = (lookups.interviewCompetencies ?? []).find(row => row.id === competencyId); return { id: localId(), interviewStageConfigurationId: configurationId, competencyId, competencyName: option?.competencyName ?? '', weightPercent: index === 0 ? Number((100 - base * (ids.length - 1)).toFixed(2)) : base, minimumScore: 0, displayOrder: index + 1 } })
}
