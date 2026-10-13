import { useMemo, useState } from 'react'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import { Alert, Button, Select, Space } from 'antd'
import DataTable from './DataTable'
import type { Client, Component, Structure, StructureLine } from '../types/payroll'
import { calculateSalaryDetails, calculateSalaryTotals } from '../utils/salary'
import PageTabs from './PageTabs'
import SalaryFieldHelp from './SalaryFieldHelp'
import PayrollAdvisor from './PayrollAdvisor'
import PayrollSimulation from './PayrollSimulation'
import { usePayrollCatalogue, valueOptions, valueHints } from '../services/payrollCalculationService'
import { attendanceHelp, formulaStyleHelp } from '../utils/salaryFieldGuides'
import '../TemplateDesigner.css'

const componentTabs = ['Earning', 'Deduction', 'Reimbursement', 'Benefit'] as const
const salaryAmount = (value: number) => Number.isFinite(value) ? value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : '-'
type ComponentTab = (typeof componentTabs)[number]
type FormulaMode = 'default' | 'fixed' | 'percent' | 'ceiling' | 'advanced'
const functionFormulaTokens = [
  { token: 'MIN(A, B)', meaning: 'Use the lower value. Example: MIN(PLRS_RATE_EARNED, 15000).' },
  { token: 'MAX(A, B)', meaning: 'Use the higher value.' },
  { token: 'ROUND(A)', meaning: 'Round to nearest rupee.' },
  { token: 'ROUNDDOWN(A)', meaning: 'Round down.' },
  { token: 'ROUNDUP(A)', meaning: 'Round up.' }
]

type SalaryTemplateDesignerProps = {
  fixedClientId?: number
  clients: Client[]
  components: Component[]
  structure: Structure
  setStructure: (s: Structure) => void
  templates: Structure[]
  saveTemplate: () => void | Promise<void>
  saving?: boolean
  templateDownloaded?: boolean
  onDownloadTemplate?: () => void
  onUploadTemplate?: (file: File | null) => void | Promise<void>
}

export default function SalaryTemplateDesigner({ fixedClientId, clients, components, structure, setStructure, templates, saveTemplate, saving = false, templateDownloaded = false, onDownloadTemplate, onUploadTemplate }: SalaryTemplateDesignerProps) {
  const [tab, setTab] = useState<ComponentTab>('Earning')
  const [dragId, setDragId] = useState('')
  const [dragLineId, setDragLineId] = useState('')
  const [selectedLineId, setSelectedLineId] = useState('')
  const [builderOpen, setBuilderOpen] = useState(false)
  const [simulationOpen, setSimulationOpen] = useState(false)
  const [simulationTemplate, setSimulationTemplate] = useState<Structure | null>(null)
  const [formulaMode, setFormulaMode] = useState<FormulaMode>('default')
  const [baseToken, setBaseToken] = useState('')
  const [percent, setPercent] = useState('')
  const [ceiling, setCeiling] = useState('15000')
  const [tokenSearch, setTokenSearch] = useState('')

  const library = components.filter(component => component.active && component.category === tab)
  const selectedClientIds = String(structure.clientId || '').split(/[;,|]/).map(item => item.split(':')[0].trim()).filter(Boolean)
  const clientOptions = clients.map(client => ({ value: String(client.id), label: client.name }))
  const lines = structure.lines
  const calculated = calculateSalaryDetails(Number(structure.annualCtc || 0), components, { ...structure, lines })
  const hasDailyRates = calculated.some(row => row.dailyRate !== undefined)
  const playerTemplate = simulationTemplate ?? structure
  const preview = calculateSalaryTotals(calculated).net
  const calculationErrors = calculated.filter(row => row.error).map(row => `${row.component.code}: ${row.error}`)
  if (lines.some(line => !components.some(component => String(component.id) === String(line.componentId) && component.active))) calculationErrors.push('Template contains missing or inactive components.')
  if (new Set(lines.map(line => String(line.componentId))).size !== lines.length) calculationErrors.push('Template contains duplicate components.')
  const clientName = (id: string | number) => clients.find(client => String(client.id) === String(id).split(':')[0])?.name || (id ? `Client #${String(id).split(':')[0]}` : 'Default')
  const componentById = useMemo(() => new Map(components.map(component => [String(component.id), component])), [components])
  const selectedLine = lines.find(line => line.componentId === selectedLineId) ?? lines[0]
  const selectedComponent = selectedLine ? componentById.get(String(selectedLine.componentId)) : undefined
  const catalogue = usePayrollCatalogue(components, builderOpen, lines.map(l => String(l.componentId)), selectedComponent ? String(selectedComponent.id) : undefined)
  const baseOptions = valueOptions(catalogue.values)
  const knownFormulaTokens = useMemo(() => new Set(catalogue.values.filter(v => !v.availability.startsWith('Missing from')).map(v => v.token)), [catalogue.values])
  const payrollFormulaTokens = catalogue.values.filter(v => !v.componentId).map(v => ({ token: v.token, meaning: v.meaning + ' Source: ' + v.source + '. ' + v.availability }))


  const updateLine = (id: string, patch: Partial<StructureLine>) => setStructure({ ...structure, lines: lines.map(line => line.componentId === id ? { ...line, ...patch } : line) })
  const add = (id: string) => {
    const component = componentById.get(id)
    if (component && !lines.some(line => line.componentId === id)) {
      const nextLine = { componentId: id, value: component.formula || component.value || '' }
      setStructure({ ...structure, lines: [...lines, nextLine] })
      setSelectedLineId(id)
    }
  }
  const remove = (id: string) => {
    const next = lines.filter(line => line.componentId !== id)
    setStructure({ ...structure, lines: next })
    if (selectedLineId === id) setSelectedLineId(next[0]?.componentId ?? '')
  }
  const moveTo = (targetId: string) => {
    if (!dragLineId || dragLineId === targetId) return
    const source = lines.find(line => line.componentId === dragLineId)
    const next = lines.filter(line => line.componentId !== dragLineId)
    const target = next.findIndex(line => line.componentId === targetId)
    if (!source) return
    next.splice(Math.max(0, target), 0, source)
    setStructure({ ...structure, lines: next })
  }
  const openSimulation = (template: Structure) => {
    setSimulationTemplate(template)
    setSimulationOpen(true)
  }
  const formulaText = selectedLine?.formula || selectedLine?.value || selectedComponent?.formula || selectedComponent?.value || ''
  const formulaProblems = validateFormula(formulaText, knownFormulaTokens)
  const componentTokenRows = catalogue.values.filter(v => v.componentId && (!tokenSearch.trim() || (v.token + ' ' + v.meaning).toLowerCase().includes(tokenSearch.toLowerCase()))).map(v => ({ token: v.token, meaning: v.meaning + '. ' + v.availability, unavailable: v.availability.startsWith('Missing from') }))
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

  return <Card title="Enterprise salary template designer">
    <div className="salary-template-designer">
      <div className="salary-template-head">
        {!fixedClientId && <label>Clients<Select mode="multiple" className="app-search-select salary-template-client-select" popupClassName="app-search-select-dropdown" showSearch allowClear maxTagCount="responsive" value={selectedClientIds} placeholder="Select clients" optionFilterProp="label" filterOption={(input, option) => String(option?.label ?? '').toLowerCase().includes(input.toLowerCase())} options={clientOptions} onChange={values => setStructure({ ...structure, clientId: values.join(',') })} /></label>}
        <label>Template<input value={structure.name} onChange={event => setStructure({ ...structure, name: event.target.value })} /></label>
        <label>Annual CTC<input value={structure.annualCtc} onChange={event => setStructure({ ...structure, annualCtc: event.target.value.replace(/\D/g, '') })} /></label>
        <button type="button" disabled={saving || calculationErrors.length > 0} onClick={() => void saveTemplate()}>{saving ? 'Saving...' : 'Save Template'}</button>
      </div>
      <div className="salary-template-actions"><Button disabled={!structure.lines.length} onClick={() => openSimulation(structure)}>Simulate draft</Button><PayrollAdvisor components={components} template={structure} clients={clients} fixedClientId={fixedClientId}
        onDisableProration={code => { const c = components.find(item => item.code === code); if (c) updateLine(String(c.id), { proRataOverride: 'false' }) }} /></div>
      {onDownloadTemplate && onUploadTemplate && <div className="salary-template-actions">
        <Space size={8} wrap>
          <Button className="salary-template-action-button" icon={<DownloadOutlined />} onClick={onDownloadTemplate}>Template</Button>
          <label className={`settings-upload-action ${!templateDownloaded ? 'disabled' : ''}`} title={templateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}>
            <input type="file" disabled={!templateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void onUploadTemplate(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} />
            <UploadOutlined />
            Bulk upload
          </label>
        </Space>
      </div>}
      <div className="salary-template-workbench">
        <section className="salary-component-palette">
          <PageTabs items={componentTabs} value={tab} onChange={setTab} label="Salary template component categories" className="salary-tabs" getLabel={item => `${item}s`} />
          <div className="salary-palette-list">{library.map(component => <article className="salary-palette-item" draggable onDragStart={() => setDragId(String(component.id))} key={component.id}>
            <div><b title={component.code}>{component.code}</b><span title={component.name}>{component.name}</span><small>{component.calculationType}</small></div>
            <button type="button" onClick={() => add(String(component.id))}>Add</button>
          </article>)}</div>
        </section>
        <section className="salary-template-canvas" onDragOver={event => event.preventDefault()} onDrop={() => dragId && add(dragId)}>
          <div className="salary-canvas-head">
            <div><h3>Template components</h3><p>{calculated.length} components in calculation order</p></div>
            <span>Click Configure for formula builder</span>
          </div>
          {calculationErrors.length > 0 && <Alert type="error" showIcon message="Calculation needs correction" description={calculationErrors.join(' ')} />}
          <div className="salary-line-grid salary-line-head"><span /><span>Component</span><span>Name</span><span>Formula / value</span><span>{hasDailyRates ? '30-day equivalent' : 'Monthly'}</span><span>{hasDailyRates ? 'Annual equivalent' : 'Annual'}</span><span /></div>
          <div className="salary-line-list">{calculated.map(row => <div className={`salary-line-grid salary-line ${selectedLineId === String(row.component.id) ? 'selected' : ''}`} draggable onDragStart={() => setDragLineId(String(row.component.id))} onDragOver={event => event.preventDefault()} onDrop={() => moveTo(String(row.component.id))} key={row.component.id}>
            <span className="salary-drag-handle">::</span>
            <div className="salary-code-cell"><span className={`salary-badge ${row.component.category.toLowerCase()}`}>{row.component.category}</span><b title={row.component.code}>{row.component.code}</b></div>
            <strong title={row.component.name}>{row.component.name}</strong>
            <input value={row.line.formula || row.line.value || ''} onFocus={() => setSelectedLineId(String(row.component.id))} onChange={event => updateLine(String(row.component.id), row.component.calculationType === 'Daily Rate' ? { calculationType: 'Daily Rate', value: event.target.value, formula: '', proRataOverride: 'false' } : { calculationType: 'Formula', formula: event.target.value, value: '' })} placeholder={row.component.formula || row.component.value} title={row.component.calculationType === 'Daily Rate' ? 'Daily rate; payroll multiplies by attendance payable days' : row.line.formula || row.line.value || row.component.formula || row.component.value} />
            <output>Rs {salaryAmount(row.monthly)}</output>
            <output>Rs {salaryAmount(row.annual)}</output>
            <button type="button" onClick={() => { setSelectedLineId(String(row.component.id)); setFormulaMode(row.line.calculationType ? 'advanced' : 'default'); setBuilderOpen(true) }}>Configure</button>
          </div>)}</div>
          <div className="salary-template-preview"><b>Preview net</b><span>{hasDailyRates ? '30-day equivalent' : 'Monthly'} Rs {salaryAmount(preview)}</span><span>{hasDailyRates ? 'Annual equivalent' : 'Annual'} Rs {salaryAmount(preview * 12)}</span></div>
        </section>
      </div>
      {simulationOpen && <PayrollSimulation template={playerTemplate} components={components} clients={clients} fixedClientId={fixedClientId} onClose={() => setSimulationOpen(false)} />}
      {builderOpen && <div className="formula-modal-backdrop" role="dialog" aria-modal="true">
        <section className="salary-formula-builder formula-modal">
          <button type="button" className="formula-modal-close" onClick={() => setBuilderOpen(false)} aria-label="Close formula builder">x</button>
          <div className="formula-builder-title">
            <span>Formula builder</span>
            <b>{selectedComponent ? `${selectedComponent.code} - ${selectedComponent.name}` : 'Select a component'}</b>
            <p>Choose a calculation style, select values, and validate before saving the salary template.</p>
          </div>
          {!selectedComponent || !selectedLine ? <p className="formula-help">Add or select a template component to configure its calculation.</p> : selectedComponent.calculationType === 'Daily Rate' ? <div className="formula-builder-grid">
            <label>Daily rate<SalaryFieldHelp label="Template daily rate" text="Overrides the component default for this template. Employee Master can supply a different per-day rate. Existing monthly components cannot be converted into daily rates." example="Rate 700 x 22 payable days = Rs 15,400. Zero payable days gives zero wages." /><input value={selectedLine.value || selectedComponent.value} onChange={event => updateLine(selectedLine.componentId, { value: event.target.value, formula: '', calculationType: 'Daily Rate', proRataOverride: 'false' })} /></label>
            <label>Payable units<SalaryFieldHelp label="Payable units" text="Uses attendance payable days once. The full-month equivalent is daily rate x payroll days; earned wages are daily rate x payable days." example="WAGES_MONTHLY at rate 700 for 30 days is 21,000; WAGES_EARNED for 22 days is 15,400." /><input readOnly value="Attendance payable days" /></label>
            <button type="button" onClick={() => updateLine(selectedLine.componentId, { value: '', formula: '', calculationType: '', proRataOverride: '' })}>Use component default</button>
          </div> : <>
            <div className="formula-builder-grid">
              <label>Calculation style<SalaryFieldHelp label="Calculation style" text="Choose how this template line calculates. The component master is not changed." example="Use a template override when one client needs a different calculation." options={formulaStyleHelp} /><Select value={formulaMode} onChange={setFormulaMode} options={[
                { value: 'default', label: 'Use component master default' },
                { value: 'fixed', label: 'Fixed amount in template' },
                { value: 'percent', label: 'Percentage of another component' },
                { value: 'ceiling', label: 'Percentage with wage ceiling' },
                { value: 'advanced', label: 'Advanced formula' }
              ]} /></label>
              {formulaMode !== 'default' && formulaMode !== 'advanced' && <label>{formulaMode === 'fixed' ? 'Amount' : 'Percentage'}<input value={percent} onChange={event => setPercent(event.target.value.replace(/[^\d.]/g, ''))} placeholder={formulaMode === 'fixed' ? '15414' : '12'} /></label>}
              {(formulaMode === 'percent' || formulaMode === 'ceiling') && <label>Base value<SalaryFieldHelp label="Base value" text="Choose whether the percentage uses a full-period value or an already-earned value. The list below explains every available token." example="Choose WAGES_EARNED for a percentage of wages payable after attendance." options={valueHints(catalogue.values)} /><Select showSearch value={baseToken || undefined} placeholder="Select base component" options={baseOptions} optionFilterProp="label" onChange={setBaseToken} /></label>}
              {formulaMode === 'ceiling' && <label>Ceiling<input value={ceiling} onChange={event => setCeiling(event.target.value.replace(/[^\d.]/g, ''))} placeholder="15000" /></label>}
              <label>Pro-rata<SalaryFieldHelp label="Template attendance adjustment" text="Decide whether to reduce this result for unpaid days. Already-earned formulas usually need no second adjustment." example="18,000 x 26/30 = 15,600; a formula based on WAGES_EARNED already uses attendance." options={[{ label:'Use component setting',meaning:'Inherit the master setting. Future changes to that default can affect this line.',example:'If the master says Yes, this line applies attendance adjustment.' }, { ...attendanceHelp[0], label:'Apply pro-rata' }, { ...attendanceHelp[1], label:'Do not pro-rate' }]} /><Select value={selectedLine.proRataOverride || 'inherit'} onChange={value => updateLine(selectedLine.componentId, { proRataOverride: value })} options={[{ value: 'inherit', label: 'Use component setting' }, { value: 'true', label: 'Apply pro-rata' }, { value: 'false', label: 'Do not pro-rate' }]} /></label>
            </div>
            {formulaMode === 'advanced' && <label className="formula-editor">Formula<textarea value={selectedLine.formula || selectedLine.value || ''} onChange={event => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: event.target.value, value: '' })} placeholder="Example: MIN(PLRS_RATE_EARNED, 15000) * 12%" /></label>}
            {formulaMode !== 'advanced' && <button type="button" className="formula-apply" onClick={applyGeneratedFormula}>{formulaMode === 'default' ? 'Reset to component default' : 'Apply formula'}</button>}
            {catalogue.loading && <Alert type="info" message="Loading engine inputs..." />}
            {catalogue.error && <Alert type="error" message={catalogue.error} action={<Button onClick={catalogue.reload}>Retry</Button>} />}
            <section className="formula-help-panel">
              <div className="formula-help-head">
                <div><b>Supported tokens</b><p>Use these names in formulas. Component tokens are generated from active salary component codes.</p></div>
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
    <DataTable rows={templates} actions={row => <><Button size="small" onClick={() => setStructure(fixedClientId ? { ...row, clientId: String(fixedClientId) } : row)}>Edit</Button><Button size="small" className="salary-simulate-button" onClick={() => openSimulation(row)}>Simulate</Button></>} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client', value: row => clientName(row.clientId) }, { key: 'annualCtc', label: 'Annual CTC' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} />
  </Card>
}

function validateFormula(text: string, knownTokens: Set<string>) {
  const value = text.trim().toUpperCase()
  if (!value) return ['Formula or value is blank.']
  const tokens = Array.from(value.matchAll(/\b[A-Z][A-Z0-9_]*\b/g)).map(match => match[0]).filter(token => !['MIN', 'MAX', 'ROUND', 'ROUNDDOWN', 'ROUNDUP', 'SUM', 'EARNINGS', 'FIXED', 'OF'].includes(token))
  const missing = tokens.filter(token => !knownTokens.has(token))
  if (missing.length) return [`Unknown value: ${Array.from(new Set(missing)).join(', ')}.`]
  const opens = (value.match(/\(/g) || []).length
  const closes = (value.match(/\)/g) || []).length
  if (opens !== closes) return ['Opening and closing brackets do not match.']
  return []
}

function Card({ title, children }: { title: string; children: React.ReactNode }) { return <section className="card"><header><i className="blue"><svg className="ui-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l4 4L19 6" /></svg></i><div><h3>{title}</h3><p>Drag components, reorder and configure formulas.</p></div></header>{children}</section> }
