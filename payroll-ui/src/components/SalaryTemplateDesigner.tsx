import { useMemo, useState } from 'react'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import { Button, Select, Space } from 'antd'
import DataTable from './DataTable'
import type { Client, Component, Structure, StructureLine } from '../types/payroll'
import { calculateSalaryDetails, calculateSalaryTotals } from '../utils/salary'
import PageTabs from './PageTabs'
import '../TemplateDesigner.css'

const componentTabs = ['Earning', 'Deduction', 'Reimbursement', 'Benefit'] as const
type ComponentTab = (typeof componentTabs)[number]
type FormulaMode = 'default' | 'fixed' | 'percent' | 'ceiling' | 'advanced'
const payrollFormulaTokens = [
  { token: 'CTC', meaning: 'Monthly CTC amount for the template.' },
  { token: 'MONTHLY_CTC', meaning: 'Monthly CTC amount.' },
  { token: 'ANNUAL_CTC', meaning: 'Annual CTC amount.' },
  { token: 'GROSS', meaning: 'Gross value available during payroll calculation.' },
  { token: 'GROSS_EARNED', meaning: 'Gross earned value after payable-day impact.' },
  { token: 'PAYROLL_DAYS', meaning: 'Total payroll days for the pay period.' },
  { token: 'PAYABLE_DAYS', meaning: 'Payable days after LOP/unpaid impact.' },
  { token: 'PRESENT_DAYS', meaning: 'Present days from attendance.' },
  { token: 'LOP_DAYS', meaning: 'Loss of pay days.' }
]
const functionFormulaTokens = [
  { token: 'MIN(A, B)', meaning: 'Use the lower value. Example: MIN(PLRS_RATE_EARNED, 15000).' },
  { token: 'MAX(A, B)', meaning: 'Use the higher value.' },
  { token: 'ROUND(A)', meaning: 'Round to nearest rupee.' },
  { token: 'ROUNDDOWN(A)', meaning: 'Round down.' },
  { token: 'ROUNDUP(A)', meaning: 'Round up.' }
]

type SalaryTemplateDesignerProps = {
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

export default function SalaryTemplateDesigner({ clients, components, structure, setStructure, templates, saveTemplate, saving = false, templateDownloaded = false, onDownloadTemplate, onUploadTemplate }: SalaryTemplateDesignerProps) {
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
  const [playerPayrollDays, setPlayerPayrollDays] = useState('30')
  const [playerPayableDays, setPlayerPayableDays] = useState('30')

  const library = components.filter(component => component.active && component.category === tab)
  const selectedClientIds = String(structure.clientId || '').split(/[;,|]/).map(item => item.split(':')[0].trim()).filter(Boolean)
  const clientOptions = clients.map(client => ({ value: String(client.id), label: client.name }))
  const lines = structure.lines
  const calculated = calculateSalaryDetails(Number(structure.annualCtc || 0), components, { ...structure, lines })
  const playerTemplate = simulationTemplate ?? structure
  const playerRows = calculateSalaryDetails(Number(playerTemplate.annualCtc || 0), components, playerTemplate, {}, { payrollDays: Number(playerPayrollDays || 30), payableDays: Number(playerPayableDays || playerPayrollDays || 30), presentDays: Number(playerPayableDays || playerPayrollDays || 30) })
  const preview = calculateSalaryTotals(calculated).net
  const playerGross = playerRows.filter(row => ['Earning', 'Reimbursement'].includes(row.component.category) && !['GROSS_EARNED', 'NET_PAY', 'EMPLOYER_COST'].includes(row.component.code.toUpperCase())).reduce((sum, row) => sum + (row.earned ?? row.monthly), 0)
  const playerDeductions = playerRows.filter(row => row.component.category === 'Deduction' && row.component.code.toUpperCase() !== 'NET_PAY').reduce((sum, row) => sum + (row.earned ?? row.monthly), 0)
  const playerBenefits = playerRows.filter(row => row.component.category === 'Benefit' || row.component.componentRole === 'Employer Contribution').reduce((sum, row) => sum + (row.earned ?? row.monthly), 0)
  const playerNet = Math.max(0, playerGross - playerDeductions)
  const clientName = (id: string | number) => clients.find(client => String(client.id) === String(id).split(':')[0])?.name || (id ? `Client #${String(id).split(':')[0]}` : 'Default')
  const componentById = useMemo(() => new Map(components.map(component => [String(component.id), component])), [components])
  const selectedLine = lines.find(line => line.componentId === selectedLineId) ?? lines[0]
  const selectedComponent = selectedLine ? componentById.get(String(selectedLine.componentId)) : undefined
  const activeTokens = useMemo(() => components.filter(component => component.active).map(component => ({ value: component.code.toUpperCase(), label: `${component.code} - ${component.name}` })), [components])
  const knownFormulaTokens = useMemo(() => new Set(['CTC', 'MONTHLY_CTC', 'ANNUAL_CTC', 'GROSS', 'GROSS_EARNED', 'PAYROLL_DAYS', 'PAYABLE_DAYS', 'PRESENT_DAYS', 'LOP_DAYS', ...components.flatMap(component => {
    const code = component.code.toUpperCase()
    return [code, `${code}_MONTHLY`, `${code}_EARNED`]
  })]), [components])

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
    setPlayerPayrollDays('30')
    setPlayerPayableDays('30')
    setSimulationOpen(true)
  }
  const formulaText = selectedLine?.formula || selectedLine?.value || selectedComponent?.formula || selectedComponent?.value || ''
  const formulaProblems = validateFormula(formulaText, knownFormulaTokens)
  const componentTokenRows = useMemo(() => components.filter(component => component.active).flatMap(component => {
    const code = component.code.toUpperCase()
    return [
      { token: code, meaning: `${component.name}: full month value from component/template.` },
      { token: `${code}_MONTHLY`, meaning: `${component.name}: explicit full month value.` },
      { token: `${code}_EARNED`, meaning: `${component.name}: earned/pro-rated value for payroll days.` }
    ]
  }).filter(row => {
    const query = tokenSearch.trim().toUpperCase()
    return !query || row.token.includes(query) || row.meaning.toUpperCase().includes(query)
  }).slice(0, 36), [components, tokenSearch])
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
    const token = baseToken || activeTokens[0]?.value || 'BASIC'
    const generated = formulaMode === 'ceiling' ? `MIN(${token}, ${ceiling || 0}) * ${percent || 0}%` : `${token} * ${percent || 0}%`
    updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: generated, value: '', baseComponent: token.replace(/_(EARNED|MONTHLY)$/i, ''), proRataOverride: formulaMode === 'ceiling' ? 'false' : 'inherit' })
  }

  return <Card title="Enterprise salary template designer">
    <div className="salary-template-designer">
      <div className="salary-template-head">
        <label>Clients<Select mode="multiple" className="app-search-select salary-template-client-select" popupClassName="app-search-select-dropdown" showSearch allowClear maxTagCount="responsive" value={selectedClientIds} placeholder="Select clients" optionFilterProp="label" filterOption={(input, option) => String(option?.label ?? '').toLowerCase().includes(input.toLowerCase())} options={clientOptions} onChange={values => setStructure({ ...structure, clientId: values.join(',') })} /></label>
        <label>Template<input value={structure.name} onChange={event => setStructure({ ...structure, name: event.target.value })} /></label>
        <label>Annual CTC<input value={structure.annualCtc} onChange={event => setStructure({ ...structure, annualCtc: event.target.value.replace(/\D/g, '') })} /></label>
        <button type="button" disabled={saving} onClick={() => void saveTemplate()}>{saving ? 'Saving...' : 'Save Template'}</button>
      </div>
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
          <div className="salary-line-grid salary-line-head"><span /><span>Component</span><span>Name</span><span>Formula / value</span><span>Monthly</span><span>Annual</span><span /></div>
          <div className="salary-line-list">{calculated.map(row => <div className={`salary-line-grid salary-line ${selectedLineId === String(row.component.id) ? 'selected' : ''}`} draggable onDragStart={() => setDragLineId(String(row.component.id))} onDragOver={event => event.preventDefault()} onDrop={() => moveTo(String(row.component.id))} key={row.component.id}>
            <span className="salary-drag-handle">::</span>
            <div className="salary-code-cell"><span className={`salary-badge ${row.component.category.toLowerCase()}`}>{row.component.category}</span><b title={row.component.code}>{row.component.code}</b></div>
            <strong title={row.component.name}>{row.component.name}</strong>
            <input value={row.line.formula || row.line.value || ''} onFocus={() => setSelectedLineId(String(row.component.id))} onChange={event => updateLine(String(row.component.id), { calculationType: 'Formula', formula: event.target.value, value: '' })} placeholder={row.component.formula || row.component.value} title={row.line.formula || row.line.value || row.component.formula || row.component.value} />
            <output>Rs {Math.round(row.monthly).toLocaleString('en-IN')}</output>
            <output>Rs {Math.round(row.annual).toLocaleString('en-IN')}</output>
            <button type="button" onClick={() => { setSelectedLineId(String(row.component.id)); setFormulaMode(row.line.calculationType ? 'advanced' : 'default'); setBuilderOpen(true) }}>Configure</button>
          </div>)}</div>
          <div className="salary-template-preview"><b>Preview net</b><span>Monthly Rs {Math.round(preview).toLocaleString('en-IN')}</span><span>Annual Rs {Math.round(preview * 12).toLocaleString('en-IN')}</span></div>
        </section>
      </div>
      {simulationOpen && <div className="formula-modal-backdrop" role="dialog" aria-modal="true">
        <section className="salary-calculation-player salary-player-modal">
          <button type="button" className="formula-modal-close" onClick={() => setSimulationOpen(false)} aria-label="Close salary simulation">x</button>
          <div className="salary-player-head">
            <div>
              <span className="salary-player-kicker">Salary simulation</span>
              <h3>{playerTemplate.name || 'Salary template'}</h3>
              <p>Enter payable days to verify how this template will calculate monthly, earned, deduction, benefit, and net values.</p>
            </div>
            <div className="salary-player-days">
              <label>Payroll days<input value={playerPayrollDays} onChange={event => setPlayerPayrollDays(event.target.value.replace(/[^\d.]/g, ''))} /></label>
              <label>Payable days<input value={playerPayableDays} onChange={event => setPlayerPayableDays(event.target.value.replace(/[^\d.]/g, ''))} /></label>
            </div>
          </div>
          <div className="salary-player-summary">
            <article><span>Gross earned</span><b>Rs {Math.round(playerGross).toLocaleString('en-IN')}</b></article>
            <article><span>Deductions</span><b>Rs {Math.round(playerDeductions).toLocaleString('en-IN')}</b></article>
            <article><span>Net payable</span><b>Rs {Math.round(playerNet).toLocaleString('en-IN')}</b></article>
            <article><span>Employer benefits</span><b>Rs {Math.round(playerBenefits).toLocaleString('en-IN')}</b></article>
          </div>
          <div className="salary-player-table">
            <div className="salary-player-row salary-player-row-head"><span>Component</span><span>Type</span><span>Formula / source</span><span>Monthly</span><span>Earned</span><span>Pro-rata</span></div>
            {playerRows.map(row => <div className="salary-player-row" key={row.component.id}>
              <div><b>{row.component.code}</b><small>{row.component.name}</small></div>
              <span>{row.component.category}</span>
              <code title={row.source}>{row.source || row.calculationType}</code>
              <output>Rs {Math.round(row.monthly).toLocaleString('en-IN')}</output>
              <output>Rs {Math.round(row.earned ?? row.monthly).toLocaleString('en-IN')}</output>
              <span>{row.proRata ? 'Yes' : 'No'}</span>
            </div>)}
            {!playerRows.length && <p className="salary-player-empty">Add components to the template to preview calculation.</p>}
          </div>
        </section>
      </div>}
      {builderOpen && <div className="formula-modal-backdrop" role="dialog" aria-modal="true">
        <section className="salary-formula-builder formula-modal">
          <button type="button" className="formula-modal-close" onClick={() => setBuilderOpen(false)} aria-label="Close formula builder">x</button>
          <div className="formula-builder-title">
            <span>Formula builder</span>
            <b>{selectedComponent ? `${selectedComponent.code} - ${selectedComponent.name}` : 'Select a component'}</b>
            <p>Choose a calculation style, select values, and validate before saving the salary template.</p>
          </div>
          {!selectedComponent || !selectedLine ? <p className="formula-help">Add or select a template component to configure its calculation.</p> : <>
            <div className="formula-builder-grid">
              <label>Calculation style<Select value={formulaMode} onChange={setFormulaMode} options={[
                { value: 'default', label: 'Use component master default' },
                { value: 'fixed', label: 'Fixed amount in template' },
                { value: 'percent', label: 'Percentage of another component' },
                { value: 'ceiling', label: 'Percentage with wage ceiling' },
                { value: 'advanced', label: 'Advanced formula' }
              ]} /></label>
              {formulaMode !== 'default' && formulaMode !== 'advanced' && <label>{formulaMode === 'fixed' ? 'Amount' : 'Percentage'}<input value={percent} onChange={event => setPercent(event.target.value.replace(/[^\d.]/g, ''))} placeholder={formulaMode === 'fixed' ? '15414' : '12'} /></label>}
              {(formulaMode === 'percent' || formulaMode === 'ceiling') && <label>Base value<Select showSearch value={baseToken || undefined} placeholder="Select base component" options={tokenOptions(activeTokens)} optionFilterProp="label" onChange={setBaseToken} /></label>}
              {formulaMode === 'ceiling' && <label>Ceiling<input value={ceiling} onChange={event => setCeiling(event.target.value.replace(/[^\d.]/g, ''))} placeholder="15000" /></label>}
              <label>Pro-rata<Select value={selectedLine.proRataOverride || 'inherit'} onChange={value => updateLine(selectedLine.componentId, { proRataOverride: value })} options={[{ value: 'inherit', label: 'Use component setting' }, { value: 'true', label: 'Apply pro-rata' }, { value: 'false', label: 'Do not pro-rate' }]} /></label>
            </div>
            {formulaMode === 'advanced' && <label className="formula-editor">Formula<textarea value={selectedLine.formula || selectedLine.value || ''} onChange={event => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: event.target.value, value: '' })} placeholder="Example: MIN(PLRS_RATE_EARNED, 15000) * 12%" /></label>}
            {formulaMode !== 'advanced' && <button type="button" className="formula-apply" onClick={applyGeneratedFormula}>{formulaMode === 'default' ? 'Reset to component default' : 'Apply formula'}</button>}
            <div className="formula-token-bank">
              <span>Available values</span>
              <button type="button" onClick={() => setBaseToken(`${selectedComponent.code.toUpperCase()}_EARNED`)}>{selectedComponent.code}_EARNED</button>
              <button type="button" onClick={() => setBaseToken(selectedComponent.code.toUpperCase())}>{selectedComponent.code}_MONTHLY</button>
              <button type="button" onClick={() => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: `MIN(${selectedComponent.code.toUpperCase()}_EARNED, 15000) * 12%`, value: '', proRataOverride: 'false' })}>PF style</button>
            </div>
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
                  {componentTokenRows.map(row => <button type="button" key={row.token} onClick={() => insertFormulaToken(row.token)}><strong>{row.token}</strong><span>{row.meaning}</span></button>)}
                  {!componentTokenRows.length && <p>No matching tokens.</p>}
                </article>
              </div>
              <div className="formula-examples">
                <b>Examples</b>
                <button type="button" onClick={() => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: 'MIN(PLRS_RATE_EARNED, 15000) * 12%', value: '', proRataOverride: 'false' })}>PF: MIN(PLRS_RATE_EARNED, 15000) * 12%</button>
                <button type="button" onClick={() => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: 'PLRS_RATE_EARNED * 0.75%', value: '', proRataOverride: 'false' })}>ESIC: PLRS_RATE_EARNED * 0.75%</button>
                <button type="button" onClick={() => updateLine(selectedLine.componentId, { calculationType: 'Formula', formula: 'ROUND(BASIC_EARNED * 40%)', value: '', proRataOverride: 'false' })}>Allowance: ROUND(BASIC_EARNED * 40%)</button>
              </div>
            </section>
            <div className={`formula-check ${formulaProblems.length ? 'danger' : 'ok'}`}>
              <b>{formulaProblems.length ? 'Needs attention' : 'Formula looks valid'}</b>
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
    <DataTable rows={templates} actions={row => <><Button size="small" onClick={() => setStructure(row)}>Edit</Button><Button size="small" className="salary-simulate-button" onClick={() => openSimulation(row)}>Simulate</Button></>} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client', value: row => clientName(row.clientId) }, { key: 'annualCtc', label: 'Annual CTC' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} />
  </Card>
}

function tokenOptions(items: { value: string; label: string }[]) {
  return items.flatMap(item => [
    item,
    { value: `${item.value}_EARNED`, label: `${item.value}_EARNED - earned/pro-rated value` },
    { value: `${item.value}_MONTHLY`, label: `${item.value}_MONTHLY - full month value` }
  ])
}

function validateFormula(text: string, knownTokens: Set<string>) {
  const value = text.trim().toUpperCase()
  if (!value) return ['Formula or value is blank.']
  const tokens = Array.from(value.matchAll(/\b[A-Z][A-Z0-9_]*\b/g)).map(match => match[0]).filter(token => !['MIN', 'MAX', 'ROUND', 'ROUNDDOWN', 'ROUNDUP', 'IF', 'TRUE', 'FALSE'].includes(token))
  const missing = tokens.filter(token => !knownTokens.has(token))
  if (missing.length) return [`Unknown value: ${Array.from(new Set(missing)).join(', ')}.`]
  const opens = (value.match(/\(/g) || []).length
  const closes = (value.match(/\)/g) || []).length
  if (opens !== closes) return ['Opening and closing brackets do not match.']
  return []
}

function Card({ title, children }: { title: string; children: React.ReactNode }) { return <section className="card"><header><i className="blue"><svg className="ui-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l4 4L19 6" /></svg></i><div><h3>{title}</h3><p>Drag components, reorder and configure formulas.</p></div></header>{children}</section> }
