import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { DownloadOutlined, UploadOutlined, MoreOutlined, PlusOutlined, AppstoreOutlined, ArrowRightOutlined, FileExcelOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Dropdown, Input, Segmented, Select } from 'antd'
import RecruitmentRecordList from './RecruitmentRecordList'
import type { Client, Component, Employee, Structure, StructureLine, WorkLocation } from '../types/payroll'
import { calculateSalaryDetails } from '../utils/salary'
import { PageHeaderPortal } from './layout/AppPageHeader'
import PayrollScopeSelect from './PayrollScopeSelect'
import { useAuthSession } from './AuthGate'
import { RecruitmentViewContext, useSessionPreference } from '../hooks/useRecruitmentPreferences'
import SalaryTemplateComponents from './SalaryTemplateComponents'
import SalaryFormulaInput from './SalaryFormulaInput'
import { salaryFormulaSuggestions } from '../utils/salaryFormulaSuggestions'
import { salaryComponentLibraryScope } from '../utils/salaryComponentLibraryScope'
import { moveSalaryTemplateLine } from '../utils/salaryTemplateComponentOrder'
import { validateSalaryTemplateFormula } from '../utils/salaryTemplateFormulaValidation'
import { salaryTemplateClientIds, scopeSalaryTemplates, preferredSalaryTemplate, templateForClient } from '../utils/salaryTemplateContext'
import { salaryTemplateReadiness } from '../utils/salaryTemplateReadiness'
import SalaryFieldHelp from './SalaryFieldHelp'
import PayrollAdvisor from './PayrollAdvisor'
import PayrollSimulation from './PayrollSimulation'
import SalaryTemplateExcelExtractor, { type SalaryTemplateExtractedDraft } from './SalaryTemplateExcelExtractor'
import SalaryTemplateExcelDownload from './SalaryTemplateExcelDownload'
import { reviewSnapshot } from '../services/payrollAdvisorService'
import { usePayrollCatalogue, valueOptions, valueHints, type SourceSalaryScenario } from '../services/payrollCalculationService'
import { attendanceHelp, formulaStyleHelp } from '../utils/salaryFieldGuides'
import '../TemplateDesigner.css'
import '../RecruitmentViewport.css'
import './SalaryTemplateWorkspace.css'

type FormulaMode = 'default' | 'fixed' | 'percent' | 'ceiling' | 'advanced'
const functionFormulaTokens = [
  { token: 'MIN(A, B)', meaning: 'Use the lower value.' },
  { token: 'MAX(A, B)', meaning: 'Use the higher value.' },
  { token: 'ROUND(A)', meaning: 'Round to nearest rupee.' },
  { token: 'ROUNDDOWN(A)', meaning: 'Round down.' },
  { token: 'ROUNDUP(A)', meaning: 'Round up.' }
]

type SalaryTemplateDesignerProps = {
  fixedClientId?: number
  clients: Client[]
  employees?: Pick<Employee, 'id' | 'clientId' | 'isActive'>[]
  locations?: WorkLocation[]
  canCreateComponentMasters?: boolean
  components: Component[]
  structure: Structure
  setStructure: (s: Structure) => void
  templates: Structure[]
  saveTemplate: (newComponents?: Component[]) => void | boolean | Promise<void | boolean>
  saving?: boolean
  templateDownloaded?: boolean
  onDownloadTemplate?: () => void
  onUploadTemplate?: (file: File | null) => void | Promise<void>
}

export default function SalaryTemplateDesigner({ fixedClientId, clients, employees = [], locations = [], canCreateComponentMasters = false, components: savedComponents, structure, setStructure, templates, saveTemplate, saving = false, templateDownloaded = false, onDownloadTemplate, onUploadTemplate }: SalaryTemplateDesignerProps) {
  const session = useAuthSession()
  const templateNameId = useId()
  const [draftComponents, setDraftComponents] = useState<Component[]>([])
  const components = useMemo(() => [...savedComponents, ...draftComponents.filter(row => !savedComponents.some(saved => String(saved.id) === String(row.id)))], [savedComponents, draftComponents])
  const [extractOpen, setExtractOpen] = useState(false)
  const [formulaDownloadOpen, setFormulaDownloadOpen] = useState(false)
  const [extractedScenario, setExtractedScenario] = useState<SourceSalaryScenario | null>(null)
  const [simulationSourceScenario, setSimulationSourceScenario] = useState<SourceSalaryScenario | undefined>()
  const idsFor = salaryTemplateClientIds
  const [chosenClientId, setChosenClientId] = useSessionPreference(`salary-templates.client:${session?.user.id}`, fixedClientId || idsFor(structure.clientId)[0] || 0)
  const clientId = fixedClientId || (clients.some(client => client.id === chosenClientId) ? chosenClientId : 0)
  const [view, setView] = useState<'Templates' | 'Designer'>('Templates')
  const [listView, setListView] = useSessionPreference<'Cards' | 'Table'>(`salary-templates.view:${session?.user.id}`, 'Cards')
  const [search, setSearch] = useSessionPreference(`salary-templates.search:${session?.user.id}:${clientId}`, '')
  const uploadInput = useRef<HTMLInputElement>(null)
  const editorBaseline = useRef(JSON.stringify(structure))
  const [openedTemplateId, setOpenedTemplateId] = useState('')
  const [editorIntent, setEditorIntent] = useState<'existing' | 'new'>('existing')
  const [rememberedTemplateId, setRememberedTemplateId] = useSessionPreference(`salary-templates.template:${session?.user.id}:${clientId}`, '')
  const contextClient = useRef(clientId)
  const [selectedLineId, setSelectedLineId] = useState('')
  const [builderOpen, setBuilderOpen] = useState(false)
  const [simulationOpen, setSimulationOpen] = useState(false)
  const [simulationTemplate, setSimulationTemplate] = useState<Structure | null>(null)
  const [simulationFromMaster, setSimulationFromMaster] = useState(false)
  const [formulaMode, setFormulaMode] = useState<FormulaMode>('default')
  const [baseToken, setBaseToken] = useState('')
  const [percent, setPercent] = useState('')
  const [ceiling, setCeiling] = useState('15000')
  const [tokenSearch, setTokenSearch] = useState('')

  const lines = structure.lines
  const calculated = calculateSalaryDetails(Number(structure.annualCtc || 0), components, { ...structure, lines })
  const playerTemplate = simulationTemplate ?? structure
  const calculationErrors = calculated.filter(row => row.error).map(row => `${row.component.code}: ${row.error}`)
  if (lines.some(line => !components.some(component => String(component.id) === String(line.componentId) && component.active))) calculationErrors.push('Template contains missing or inactive components.')
  if (new Set(lines.map(line => String(line.componentId))).size !== lines.length) calculationErrors.push('Template contains duplicate components.')
  const clientName = (id: string | number) => clients.find(client => String(client.id) === String(id).split(':')[0])?.name || (id ? `Client #${String(id).split(':')[0]}` : 'Default')
  const componentById = useMemo(() => new Map(components.map(component => [String(component.id), component])), [components])
  const selectedLine = lines.find(line => String(line.componentId) === selectedLineId) ?? lines[0]
  const selectedComponent = selectedLine ? componentById.get(String(selectedLine.componentId)) : undefined
  const editorReady = !!clientId && (editorIntent === 'new' || !!openedTemplateId) && idsFor(structure.clientId).includes(clientId)
  const includedComponentIds = lines.map(line => String(line.componentId))
  const catalogueComponents = components.filter(component => includedComponentIds.includes(String(component.id)))
  const catalogue = usePayrollCatalogue(catalogueComponents, view === 'Designer' && editorReady, includedComponentIds)
  const availableFormulaValues = salaryFormulaSuggestions(catalogue.values, '', { includedComponentIds, excludeComponentId: selectedComponent ? String(selectedComponent.id) : undefined, limit: Number.MAX_SAFE_INTEGER })
  const baseOptions = valueOptions(availableFormulaValues)
  const knownFormulaTokens = new Set(availableFormulaValues.map(value => value.token))
  const payrollFormulaTokens = availableFormulaValues.filter(v => !v.componentId).map(v => ({ token: v.token, meaning: v.meaning + ' Source: ' + v.source + '. ' + v.availability }))
  const libraryScope = salaryComponentLibraryScope(components, templates, clientId, clients, includedComponentIds)
  const exampleEarning = catalogueComponents.find(component => component.active && component.category === 'Earning' && String(component.id) !== String(selectedComponent?.id))
  const formulaExample = exampleEarning ? `MIN(${exampleEarning.code.toUpperCase()}_EARNED, 15000) * 12%` : 'ROUND(MONTHLY_CTC * 10%, 0)'


  const updateLine = (id: string, patch: Partial<StructureLine>) => setStructure({ ...structure, lines: lines.map(line => String(line.componentId) === String(id) ? { ...line, ...patch } : line) })
  const add = (id: string) => {
    const component = componentById.get(id)
    if (component && !lines.some(line => String(line.componentId) === id)) {
      const nextLine = { componentId: id, value: component.formula || component.value || '' }
      setStructure({ ...structure, lines: [...lines, nextLine] })
      setSelectedLineId(id)
    }
  }
  const remove = (id: string) => {
    const next = lines.filter(line => String(line.componentId) !== String(id))
    setStructure({ ...structure, lines: next })
    if (selectedLineId === id) setSelectedLineId(next[0]?.componentId ?? '')
  }
  const moveTo = (sourceId: string, targetId: string) => setStructure({ ...structure, lines: moveSalaryTemplateLine(lines, sourceId, targetId) })
  const openSimulation = (template: Structure, fromMaster = false) => {
    setSimulationFromMaster(fromMaster)
    setSimulationTemplate(template)
    const currentDraft = extractedScenario && !template.id && JSON.stringify(template) === JSON.stringify(structure)
    setSimulationSourceScenario(currentDraft ? { ...extractedScenario, ...(template.annualCtc.trim() ? { annualCtc: Number(template.annualCtc) } : {}), snapshot: reviewSnapshot(components, undefined, template, clientId),
      amountOverrides: Object.fromEntries(Object.entries(extractedScenario.amountOverrides).filter(([id]) => { const line = template.lines.find(row => row.componentId === id); const component = components.find(row => String(row.id) === id); return !!line && ['Fixed Amount', 'Daily Rate'].includes(line.calculationType || component?.calculationType || '') })) } : undefined)
    setSimulationOpen(true)
  }
  const formulaText = selectedLine?.formula || selectedLine?.value || selectedComponent?.formula || selectedComponent?.value || ''
  const formulaProblems = validateFormula(formulaText, knownFormulaTokens)
  const componentTokenRows = availableFormulaValues.filter(v => v.componentId && (!tokenSearch.trim() || (v.token + ' ' + v.meaning).toLowerCase().includes(tokenSearch.toLowerCase()))).map(v => ({ token: v.token, meaning: v.meaning + '. ' + v.availability, unavailable: false }))
  const insertFormulaToken = (token: string) => {
    if (!selectedLine) return
    const current = selectedLine.formula || selectedLine.value || ''
    const cleanToken = token.includes('(') ? token : `${token}`
    updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: current ? `${current} ${cleanToken}` : cleanToken, value: '' })
    setFormulaMode('advanced')
  }
  const applyGeneratedFormula = () => {
    if (!selectedLine) return
    if (formulaMode === 'default') {
      updateLine(selectedLine.componentId, { calculationType: '', formula: '', value: selectedComponent?.formula || selectedComponent?.value || '', baseComponent: '', proRataOverride: '', roundingMode: '' })
      return
    }
    if (formulaMode === 'fixed') {
      updateLine(selectedLine.componentId, { calculationType: 'Fixed Amount', value: percent, formula: '', baseComponent: '', proRataOverride: 'inherit' })
      return
    }
    const token = baseToken
    if (!token || !baseOptions.some(v => v.value === token && !v.disabled) || !percent.trim() || !Number.isFinite(Number(percent)) || (formulaMode === 'ceiling' && (!ceiling.trim() || Number(ceiling) <= 0))) return
    const generated = formulaMode === 'ceiling' ? `MIN(${token}, ${ceiling || 0}) * ${percent || 0}%` : `${token} * ${percent || 0}%`
    updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: generated, value: '', baseComponent: token.replace(/_(EARNED|MONTHLY)$/i, ''), proRataOverride: formulaMode === 'ceiling' ? 'false' : 'inherit' })
  }

  const scopeTemplates = scopeSalaryTemplates(templates, clientId)
  const templateReadiness = useMemo(() => new Map(templates.map(row => [String(row.id), salaryTemplateReadiness(row, savedComponents)])), [templates, savedComponents])
  const readinessFor = (row: Structure) => templateReadiness.get(String(row.id)) ?? salaryTemplateReadiness(row, savedComponents)
  const clientOptions = useMemo(() => {
    const employeesByClient = new Map<number, Set<number>>()
    for (const employee of employees) {
      if (!employee.isActive) continue
      const roster = employeesByClient.get(employee.clientId)
      if (roster) roster.add(employee.id)
      else employeesByClient.set(employee.clientId, new Set([employee.id]))
    }
    const configured = new Set(templates.filter(row => row.active && row.lines.length)
      .flatMap(row => salaryTemplateClientIds(row.clientId)))
    return clients.map(client => ({
      value: client.id, label: client.name, code: client.code,
      count: employeesByClient.get(client.id)?.size ?? 0, hasTemplate: configured.has(client.id),
    }))
  }, [clients, employees, templates])
  const newDraft = (id: number): Structure => ({ id: 0, clientId: String(id || ''), name: '', annualCtc: '', active: true, lines: [] })
  const hasUnsavedChanges = (editorIntent === 'new' || !!openedTemplateId) && JSON.stringify(structure) !== editorBaseline.current
  const canLeaveEditor = () => !saving && (!hasUnsavedChanges || window.confirm('Discard unsaved template changes?'))
  const resetDialogs = () => { setBuilderOpen(false); setSimulationOpen(false); setSimulationTemplate(null); setSimulationSourceScenario(undefined); setSelectedLineId(''); setExtractOpen(false); setFormulaDownloadOpen(false) }
  const clearExtractedDraft = () => { setDraftComponents([]); setExtractedScenario(null) }
  const editTemplate = (row: Structure) => {
    if (saving) return
    if (openedTemplateId === String(row.id) && editorReady) { setView('Designer'); return }
    if (!canLeaveEditor()) return
    const next = templateForClient(row, clientId)
    if (!next) return
    clearExtractedDraft()
    editorBaseline.current = JSON.stringify(next)
    setStructure(next)
    setOpenedTemplateId(String(row.id)); setRememberedTemplateId(String(row.id)); setEditorIntent('existing')
    resetDialogs(); setView('Designer')
  }
  const createTemplate = () => {
    if (!clientId || !canLeaveEditor()) return
    const next = newDraft(clientId)
    clearExtractedDraft()
    editorBaseline.current = JSON.stringify(next)
    setStructure(next); setOpenedTemplateId(''); setEditorIntent('new'); resetDialogs(); setView('Designer')
  }
  const changeClient = (id: number) => {
    if (!canLeaveEditor()) return
    const next = newDraft(id)
    clearExtractedDraft()
    editorBaseline.current = JSON.stringify(next)
    contextClient.current = id
    setChosenClientId(id); setStructure(next); setOpenedTemplateId(''); setEditorIntent('existing'); resetDialogs()
  }
  // Resolve only an unambiguous saved context; never replace a draft or guess a location.
  useEffect(() => {
    if (contextClient.current !== clientId) {
      contextClient.current = clientId
      const next = newDraft(clientId)
      clearExtractedDraft()
      editorBaseline.current = JSON.stringify(next)
      setStructure(next); setOpenedTemplateId(''); setEditorIntent('existing'); resetDialogs()
      return
    }
    if (view !== 'Designer' || editorIntent === 'new' || openedTemplateId || !clientId) return
    const row = preferredSalaryTemplate(templates, clientId, rememberedTemplateId)
    const next = row && templateForClient(row, clientId)
    if (!row || !next) return
    editorBaseline.current = JSON.stringify(next)
    setStructure(next); setOpenedTemplateId(String(row.id)); setSelectedLineId('')
  }, [clientId, view, editorIntent, openedTemplateId, templates, rememberedTemplateId]) // eslint-disable-line react-hooks/exhaustive-deps
  const configure = (id: string) => { const line = lines.find(row => String(row.componentId) === id); setSelectedLineId(id); setFormulaMode(line?.calculationType ? 'advanced' : 'default'); setBuilderOpen(true) }
  const saveAndClose = async () => { if (await saveTemplate(draftComponents) === true) { clearExtractedDraft(); setOpenedTemplateId(''); setEditorIntent('existing'); editorBaseline.current = JSON.stringify(newDraft(clientId)); resetDialogs(); setView('Templates') } }
  const useExtractedDraft = (draft: SalaryTemplateExtractedDraft) => {
    if (saving || Number(draft.structure.clientId) !== clientId || !canLeaveEditor() || draft.newComponents.length > 0 && !canCreateComponentMasters) return
    resetDialogs(); setDraftComponents(draft.newComponents); setExtractedScenario(draft.sourceScenario)
    editorBaseline.current = JSON.stringify(newDraft(clientId))
    setStructure(draft.structure); setOpenedTemplateId(''); setEditorIntent('new'); setView('Designer')
  }
  return <section className="salary-template-workspace recruitment-viewport">
    <PageHeaderPortal slot="salary-templates-client-controls">{!fixedClientId && <label className="salary-workspace-client"><span>Client</span><PayrollScopeSelect ariaLabel="Salary template client" value={clientId} options={clientOptions} disabled={saving} onChange={value => changeClient(Number(value))} /></label>}</PageHeaderPortal>
    <PageHeaderPortal slot="salary-templates-view-controls"><div className="salary-workspace-view-controls"><nav className="salary-workspace-nav" aria-label="Salary template workspace">{(['Templates', 'Designer'] as const).map(item => <button type="button" key={item} disabled={saving} aria-current={view === item ? 'page' : undefined} onClick={() => setView(item)}>{item === 'Designer' ? 'Components & formulas' : 'Saved templates'}</button>)}</nav>{view === 'Templates' && <Segmented aria-label="Salary template display view" options={[{ label: 'Card view', value: 'Cards' }, { label: 'Table view', value: 'Table' }]} value={listView} onChange={value => setListView(value as 'Cards' | 'Table')} />}</div></PageHeaderPortal>
    <PageHeaderPortal slot="salary-templates-page-controls"><div className="salary-workspace-actions">
      {view === 'Designer' && editorReady && <><Button disabled={!structure.lines.length} onClick={event => { event.currentTarget.blur(); openSimulation(structure, !!structure.id && JSON.stringify(structure) === editorBaseline.current) }}>Excel simulation</Button><PayrollAdvisor components={components} template={structure} clients={clients} fixedClientId={clientId || fixedClientId} onDisableProration={code => { const c = components.find(item => item.code === code); if (c) updateLine(String(c.id), { proRataOverride: 'false' }) }} /><Button type="primary" loading={saving} disabled={!clientId || !structure.name.trim() || !lines.length || calculationErrors.length > 0} onClick={() => void saveAndClose()}>Save template</Button></>}
      {(view === 'Templates' || !editorReady) && <Button type="primary" icon={<PlusOutlined />} disabled={!clientId || saving} onClick={createTemplate}>Create template</Button>}
      <Dropdown trigger={['click']} menu={{ items: [
        { key: 'extract-excel', icon: <FileExcelOutlined />, label: 'Extract from Excel', disabled: !clientId || saving, onClick: () => setExtractOpen(true) },
        { key: 'download-formulas', icon: <DownloadOutlined />, label: 'Download client Excel with formulas', disabled: !clientId || saving || !scopeTemplates.some(template => template.active && template.lines.length > 0), onClick: () => setFormulaDownloadOpen(true) },
        { type: 'divider' as const },
        { key: 'download', icon: <DownloadOutlined />, label: 'Download import template', disabled: !onDownloadTemplate, onClick: onDownloadTemplate },
        { key: 'upload', icon: <UploadOutlined />, label: 'Bulk upload templates', disabled: !onUploadTemplate || !templateDownloaded, onClick: () => uploadInput.current?.click() },
      ] }}><Button aria-label="Salary template tools" icon={<MoreOutlined />}>Tools</Button></Dropdown>
      <input ref={uploadInput} hidden type="file" accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void onUploadTemplate?.(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} />
    </div></PageHeaderPortal>
    <div className={`salary-workspace-scroll${clientId && view === 'Templates' ? ' is-template-list' : ''}`}>
      {!clientId ? <div className="salary-workspace-empty"><span className="salary-workspace-empty-icon"><AppstoreOutlined /></span><h2>Start with a client</h2><p>Choose a client above to see their saved templates and configure salary formulas.</p></div> : view === 'Templates' ? <>
        <RecruitmentViewContext.Provider value={{ view: listView, scope: `${session?.user.id}:${clientId}`, namespace: 'salary-templates.ui' }}>
          <RecruitmentRecordList rows={scopeTemplates} view={listView} title={row => row.name || 'Untitled template'}
            subtitle={row => idsFor(row.clientId).length === 1 ? clientName(row.clientId) : 'Shared template'}
            searchValue={search} onSearchChange={setSearch} searchPlaceholder="Search salary templates"
            exportFileName={`salary-templates-${clientId}`} hiddenCardColumns={['name', 'client', 'review']}
            cardSummaryColumns={['status', 'components', 'categories']}
            rowClassName={row => `salary-template-record is-${readinessFor(row).status.toLowerCase()}`}
            columns={[
              { key: 'name', label: 'Template', width: 300, wrap: true },
              { key: 'client', label: 'Client', value: row => idsFor(row.clientId).length === 1 ? clientName(row.clientId) : 'Shared template' },
              { key: 'status', label: 'Configuration', width: 140, value: row => readinessFor(row).status, render: row => <span className={`salary-template-state is-${readinessFor(row).status.toLowerCase()}`} title={readinessFor(row).reason}>{readinessFor(row).status}</span> },
              { key: 'components', label: 'Components', width: 120, value: row => row.lines.length },
              { key: 'categories', label: 'Component types', width: 220, wrap: true, value: row => [...new Set(row.lines.map(line => componentById.get(String(line.componentId))?.category).filter(Boolean))].join(' · ') || 'Not configured' },
              { key: 'review', label: 'Configuration details', width: 280, wrap: true, value: row => readinessFor(row).reason },
            ]}
            quickFilters={[
              { key: 'configured', label: 'Configured', tone: 'purple', matches: row => readinessFor(row).status === 'Configured' },
              { key: 'draft', label: 'Draft', tone: 'orange', matches: row => readinessFor(row).status === 'Draft' },
              { key: 'inactive', label: 'Inactive', tone: 'blue', matches: row => readinessFor(row).status === 'Inactive' },
            ]}
            filters={[{ key: 'configuration', label: 'Configuration', value: row => readinessFor(row).status, options: ['Configured', 'Draft', 'Inactive'] }]}
            cardExtra={row => readinessFor(row).status === 'Draft' ? <p className="salary-template-draft-reason">{readinessFor(row).reason}</p> : null}
            actionsWidth={240}
            actions={row => <><Button size="small" disabled={saving} onClick={() => editTemplate(row)}>{idsFor(row.clientId).length === 1 ? 'Edit formulas' : 'Use for client'}</Button><Button size="small" type="primary" disabled={saving || !row.lines.length} onClick={() => openSimulation(row, true)}>Simulate</Button></>}
            emptyText={scopeTemplates.length ? 'No salary templates match your search or filters.' : 'No saved templates for this client. Select Create template to add components and formulas.'}
          />
        </RecruitmentViewContext.Provider>
      </> : <div className="salary-template-designer salary-workspace-designer">
        <div className="salary-template-contextbar"><label><span>Saved template <small>{scopeTemplates.length} available</small></span><Select aria-label="Choose saved salary template" showSearch optionFilterProp="label" value={openedTemplateId || undefined} placeholder={editorIntent === 'new' ? 'New template — unsaved' : 'Select a template to view its components & formulas'} disabled={saving} options={scopeTemplates.map(row => ({ value: String(row.id), label: `${row.name}${row.active ? '' : ' · Inactive'}${idsFor(row.clientId).length === 1 ? '' : ' · Shared'}` }))} onChange={value => { const row = scopeTemplates.find(row => String(row.id) === value); if (row) editTemplate(row) }} /></label>{editorReady && <Button icon={<PlusOutlined />} disabled={saving} onClick={createTemplate}>New template</Button>}</div>
        {!editorReady ? <section className="salary-template-chooser"><div className="salary-template-chooser-heading"><span className="salary-template-icon"><AppstoreOutlined /></span><div><h2>{scopeTemplates.length ? 'Open an existing template' : 'No saved template for this client'}</h2><p>{scopeTemplates.length ? 'Choose the template you want to work on. Its saved components and formulas will open here.' : 'Create a template to add components and set their formulas.'}</p></div></div><div className="salary-template-choices">{scopeTemplates.map(row => <button type="button" className="salary-template-choice" key={row.id} onClick={() => editTemplate(row)}><span className="salary-template-choice-icon"><AppstoreOutlined /></span><span className="salary-template-choice-copy"><strong>{row.name}</strong><small>{row.lines.length} components{idsFor(row.clientId).length !== 1 ? ' · Shared — creates a client copy' : ''}</small></span><span className={`salary-template-state ${row.active ? 'is-active' : ''}`}>{row.active ? 'Active' : 'Inactive'}</span><ArrowRightOutlined /></button>)}</div></section> : <>
        <div className="salary-workspace-editor-heading">
          <label htmlFor={templateNameId} className={`salary-template-name-field${!structure.name.trim() ? ' is-required' : ''}`}>
            <span className="salary-template-name-label"><strong>Template name</strong><span className="salary-template-name-required">Required</span></span>
            <Input id={templateNameId} aria-label="Salary template name" aria-required="true" aria-describedby={!structure.name.trim() ? `${templateNameId}-help` : undefined} placeholder="Enter a name, e.g. Regular monthly salary" value={structure.name} disabled={saving} onChange={event => setStructure({ ...structure, name: event.target.value })} />
            {!structure.name.trim() && <span id={`${templateNameId}-help`} className="salary-template-name-help">Enter a template name before saving.</span>}
          </label>
          <span className={`salary-template-edit-state${hasUnsavedChanges ? ' is-dirty' : ''}`}>{hasUnsavedChanges ? 'Unsaved changes' : structure.id ? 'Saved template' : 'New client template'}</span>
          <Checkbox disabled={saving} checked={structure.active} onChange={event => setStructure({ ...structure, active: event.target.checked })}>Active</Checkbox>
        </div>
        {calculationErrors.length > 0 && <Alert type="error" showIcon message="Review template calculation" description={calculationErrors.join(' ')} />}
        {catalogue.error && <Alert type="warning" showIcon message="Formula suggestions are unavailable" description={catalogue.error} action={<Button onClick={catalogue.reload}>Retry</Button>} />}
        <SalaryTemplateComponents key={`${clientId}:${openedTemplateId || 'new'}`} components={components} lines={lines} formulaValues={catalogue.values} libraryPrimaryIds={libraryScope.primaryIds} componentUsageLabels={libraryScope.usageLabels} selectedLineId={selectedLineId} onSelect={setSelectedLineId} onAdd={add} onRemove={remove} onMove={moveTo} onUpdate={updateLine} onConfigure={configure} disabled={saving} />
        <details className="salary-workspace-advanced"><summary>Optional template simulation basis</summary><p>Employee simulation uses the employee’s saved salary and attendance. This optional value is used only by template-input simulation.</p><label>Test annual package<input aria-label="Template test annual package" inputMode="decimal" value={structure.annualCtc} onChange={event => setStructure({ ...structure, annualCtc: event.target.value.replace(/\D/g, '') })} /></label></details>
        {structure.excelSource && <details className="salary-workspace-advanced"><summary>Imported Excel source · {structure.excelSource.sheetName}</summary><p>{structure.excelSource.fileName} · Header row {structure.excelSource.headerRow} · {structure.excelSource.payPeriod}{structure.excelSource.workLocationLabel ? ` · ${structure.excelSource.workLocationLabel}` : ''}. Sample employee amounts are used only in the temporary simulation. Employee salary inputs and location assignments remain in their existing Master flows.</p><div className="salary-template-source-formulas">{structure.excelSource.columns.filter(column => column.originalFormula).map(column => <div key={column.columnIndex}><strong>{column.sourceHeader}</strong><code>Excel: {column.originalFormula}</code><code>Native: {column.nativeFormula || 'Control / source input'}</code></div>)}</div></details>}
        </>}
      </div>}
      {extractOpen && clientId > 0 && <SalaryTemplateExcelExtractor clientId={clientId} clientName={clientName(clientId)} components={savedComponents} locations={locations.filter(location => location.clientId === clientId && location.isActive)} canCreateComponentMasters={canCreateComponentMasters} onClose={() => setExtractOpen(false)} onUseDraft={useExtractedDraft} />}
      {formulaDownloadOpen && clientId > 0 && <SalaryTemplateExcelDownload key={`download:${clientId}`} clientId={clientId} clientName={clientName(clientId)} templates={templates} components={savedComponents} locations={locations} preferredTemplateId={openedTemplateId} onClose={() => setFormulaDownloadOpen(false)} />}
      {simulationOpen && <PayrollSimulation template={playerTemplate} components={components} clients={clients} fixedClientId={clientId || fixedClientId} clientEmployeeCounts={Object.fromEntries(clientOptions.map(option => [option.value, option.count]))} sourceScenario={simulationSourceScenario} preferEmployeeSalary={simulationFromMaster} onClose={() => setSimulationOpen(false)} />}
      {builderOpen && <div className="formula-modal-backdrop" role="dialog" aria-modal="true">
        <section className="salary-formula-builder formula-modal">
          <button type="button" className="formula-modal-close" onClick={() => setBuilderOpen(false)} aria-label="Close formula builder">x</button>
          <div className="formula-builder-title">
            <span>Formula builder</span>
            <b>{selectedComponent ? `${selectedComponent.code} - ${selectedComponent.name}` : 'Select a component'}</b>
            <p>Choose a calculation style, select values, and validate before saving the salary template.</p>
          </div>
          {!selectedComponent || !selectedLine ? <p className="formula-help">Add or select a template component to configure its calculation.</p> : selectedComponent.calculationType === 'Daily Rate' ? <div className="formula-builder-grid">
            <div className="formula-builder-field"><div className="formula-builder-field-heading"><label htmlFor={`${templateNameId}-daily-rate`}>Daily rate</label><SalaryFieldHelp label="Template daily rate" text="Overrides the component default for this template. Employee Master can supply a different per-day rate. Existing monthly components cannot be converted into daily rates." example="Rate 700 x 22 payable days = Rs 15,400. Zero payable days gives zero wages." /></div><input id={`${templateNameId}-daily-rate`} value={selectedLine.value || selectedComponent.value} onChange={event => updateLine(selectedLine.componentId, { value: event.target.value, formula: '', calculationType: 'Daily Rate', proRataOverride: 'false' })} /></div>
            <div className="formula-builder-field"><div className="formula-builder-field-heading"><label htmlFor={`${templateNameId}-payable-units`}>Payable units</label><SalaryFieldHelp label="Payable units" text="Uses attendance payable days once. The full-month equivalent is daily rate x payroll days; earned wages are daily rate x payable days." example="WAGES_MONTHLY at rate 700 for 30 days is 21,000; WAGES_EARNED for 22 days is 15,400." /></div><input id={`${templateNameId}-payable-units`} readOnly value="Attendance payable days" /></div>
            <button type="button" onClick={() => updateLine(selectedLine.componentId, { value: '', formula: '', calculationType: '', proRataOverride: '' })}>Use component default</button>
          </div> : <>
            <div className="formula-builder-grid">
              <div className="formula-builder-field"><div className="formula-builder-field-heading"><label htmlFor={`${templateNameId}-calculation-style`}>Calculation style</label><SalaryFieldHelp label="Calculation style" text="Choose how this template line calculates. The component master is not changed." example="Use a template override when one client needs a different calculation." options={formulaStyleHelp} /></div><Select id={`${templateNameId}-calculation-style`} aria-label="Calculation style" showSearch optionFilterProp="label" value={formulaMode} onChange={setFormulaMode} options={[
                { value: 'default', label: 'Use component master default' },
                { value: 'fixed', label: 'Fixed amount in template' },
                { value: 'percent', label: 'Percentage of another component' },
                { value: 'ceiling', label: 'Percentage with wage ceiling' },
                { value: 'advanced', label: 'Advanced formula' }
              ]} /></div>
              {formulaMode !== 'default' && formulaMode !== 'advanced' && <label>{formulaMode === 'fixed' ? 'Amount' : 'Percentage'}<input value={percent} onChange={event => setPercent(event.target.value.replace(/[^\d.]/g, ''))} placeholder={formulaMode === 'fixed' ? '15414' : '12'} /></label>}
              {(formulaMode === 'percent' || formulaMode === 'ceiling') && <div className="formula-builder-field"><div className="formula-builder-field-heading"><label htmlFor={`${templateNameId}-base-value`}>Base value</label><SalaryFieldHelp label="Base value" text="Choose whether the percentage uses a full-period value or an already-earned value. The list below explains every available token." example="Choose WAGES_EARNED for a percentage of wages payable after attendance." options={valueHints(availableFormulaValues)} /></div><Select id={`${templateNameId}-base-value`} aria-label="Base value" showSearch value={baseToken || undefined} placeholder="Select base component" options={baseOptions} optionFilterProp="label" onChange={setBaseToken} /></div>}
              {formulaMode === 'ceiling' && <label>Ceiling<input value={ceiling} onChange={event => setCeiling(event.target.value.replace(/[^\d.]/g, ''))} placeholder="15000" /></label>}
              <div className="formula-builder-field"><div className="formula-builder-field-heading"><label htmlFor={`${templateNameId}-pro-rata`}>Pro-rata</label><SalaryFieldHelp label="Template attendance adjustment" text="Decide whether to reduce this result for unpaid days. Already-earned formulas usually need no second adjustment." example="18,000 x 26/30 = 15,600; a formula based on WAGES_EARNED already uses attendance." options={[{ label:'Use component setting',meaning:'Inherit the master setting. Future changes to that default can affect this line.',example:'If the master says Yes, this line applies attendance adjustment.' }, { ...attendanceHelp[0], label:'Apply pro-rata' }, { ...attendanceHelp[1], label:'Do not pro-rate' }]} /></div><Select id={`${templateNameId}-pro-rata`} aria-label="Pro-rata" showSearch optionFilterProp="label" value={selectedLine.proRataOverride || 'inherit'} onChange={value => updateLine(selectedLine.componentId, { proRataOverride: value })} options={[{ value: 'inherit', label: 'Use component setting' }, { value: 'true', label: 'Apply pro-rata' }, { value: 'false', label: 'Do not pro-rate' }]} /></div>
            </div>
            {formulaMode === 'advanced' && <label className="formula-editor">Formula<SalaryFormulaInput label="Advanced salary formula" multiline value={selectedLine.formula || selectedLine.value || ''} values={catalogue.values} includedComponentIds={includedComponentIds} excludeComponentId={String(selectedComponent.id)} onChange={value => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: value, value: '' })} placeholder={`Example: ${formulaExample}`} /></label>}
            {formulaMode !== 'advanced' && <button type="button" className="formula-apply" onClick={applyGeneratedFormula}>{formulaMode === 'default' ? 'Reset to component default' : 'Apply formula'}</button>}
            {catalogue.loading && <Alert type="info" message="Loading engine inputs..." />}
            {catalogue.error && <Alert type="error" message={catalogue.error} action={<Button onClick={catalogue.reload}>Retry</Button>} />}
            <section className="formula-help-panel">
              <div className="formula-help-head">
                <div><b>Supported tokens</b><p>Use the exact code from a component added to this template. MONTHLY is before attendance; EARNED is after attendance.</p></div>
                <input value={tokenSearch} onChange={event => setTokenSearch(event.target.value)} placeholder="Search token or component..." />
              </div>
              <div className="formula-help-grid">
                <article>
                  <h4>Payroll values</h4>
                  {payrollFormulaTokens.map(row => <button type="button" key={row.token} onClick={() => insertFormulaToken(row.token)}><strong>{row.token}</strong><span>{row.meaning}</span></button>)}
                </article>
                <article>
                  <h4>Functions</h4>
                  {functionFormulaTokens.map(row => <button type="button" key={row.token} onClick={() => insertFormulaToken(row.token)}><strong>{row.token}</strong><span>{row.meaning}</span></button>)}
                </article>
                <article className="formula-component-token-list">
                  <h4>Component values</h4>
                  {componentTokenRows.map(row => <button type="button" disabled={row.unavailable} key={row.token} onClick={() => insertFormulaToken(row.token)}><strong>{row.token}</strong><span>{row.meaning}</span></button>)}
                  {!componentTokenRows.length && <p>No matching tokens.</p>}
                </article>
              </div>

            </section>
            <div className={`formula-check ${formulaProblems.length ? 'danger' : 'ok'}`}>
              <b>{catalogue.loading || catalogue.error ? 'Engine catalogue required' : formulaProblems.length ? 'Needs attention' : 'Known values; run simulation to validate'}</b>
              <p>{formulaProblems.length ? formulaProblems.join(' ') : 'Save the template to use this formula in the next payroll run.'}</p>
            </div>
            <div className="formula-modal-actions">
              <button type="button" className="formula-remove" onClick={() => { remove(selectedLine.componentId); setBuilderOpen(false) }}>Remove component</button>
              <button type="button" className="formula-apply" onClick={() => setBuilderOpen(false)}>Done</button>
            </div>
          </>}
        </section>
      </div>}
    </div>
  </section>
}

function validateFormula(text: string, knownTokens: Set<string>) {
  return validateSalaryTemplateFormula(text, knownTokens)
}
