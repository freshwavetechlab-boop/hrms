import { useEffect, useState } from 'react'
import DataTable from './DataTable'
import type { Client, Component, Structure } from '../types/payroll'
import { calculateSalaryDetails, calculateSalaryTotals } from '../utils/salary'
import PageTabs from './PageTabs'
import SearchSelect, { selectOptions } from './SearchSelect'
import '../TemplateDesigner.css'

const componentTabs = ['Earning', 'Deduction', 'Reimbursement'] as const

export default function SalaryTemplateDesigner({ clients, components, structure, setStructure, templates, saveTemplate, saving = false }: { clients: Client[]; components: Component[]; structure: Structure; setStructure: (s: Structure) => void; templates: Structure[]; saveTemplate: () => void | Promise<void>; saving?: boolean }) {
  const [tab, setTab] = useState<'Earning' | 'Deduction' | 'Reimbursement'>('Earning')
  const [dragId, setDragId] = useState('')
  const [dragLineId, setDragLineId] = useState('')
  const library = components.filter(component => component.active && component.category === tab)
  const basic = components.find(component => component.code === 'BASIC')
  const hasDraft = !!(structure.clientId || structure.name || structure.annualCtc || structure.lines.length)
  const hasBasic = basic ? structure.lines.some(line => line.componentId === String(basic.id)) : true
  const lines = hasDraft && basic && !hasBasic ? [{ componentId: String(basic.id), value: basic.formula || basic.value || 'CTC * 40%' }, ...structure.lines] : structure.lines
  const calculated = calculateSalaryDetails(Number(structure.annualCtc || 0), components, { ...structure, lines })
  const preview = calculateSalaryTotals(calculated).net

  useEffect(() => {
    if (hasDraft && basic && !hasBasic) setStructure({ ...structure, lines })
  }, [basic, hasBasic, hasDraft, lines, setStructure, structure])

  const isBasic = (id: string) => components.find(component => String(component.id) === id)?.code === 'BASIC'
  const add = (id: string) => {
    const component = components.find(item => String(item.id) === id)
    if (component && !lines.some(line => line.componentId === id)) setStructure({ ...structure, lines: [...lines, { componentId: id, value: component.formula || component.value || '' }] })
  }
  const remove = (id: string) => !isBasic(id) && setStructure({ ...structure, lines: lines.filter(line => line.componentId !== id) })
  const setLine = (id: string, value: string) => setStructure({ ...structure, lines: lines.map(line => line.componentId === id ? { ...line, value } : line) })
  const moveTo = (targetId: string) => {
    if (!dragLineId || dragLineId === targetId || isBasic(dragLineId)) return
    const source = lines.find(line => line.componentId === dragLineId)
    const next = lines.filter(line => line.componentId !== dragLineId)
    const target = next.findIndex(line => line.componentId === targetId)
    if (!source) return
    next.splice(Math.max(1, target), 0, source)
    setStructure({ ...structure, lines: next })
  }

  return <Card title="Enterprise salary template designer">
    <div className="salary-template-designer">
      <div className="salary-template-head">
        <label>Client<SearchSelect value={structure.clientId} onChange={value => setStructure({ ...structure, clientId: value })} options={selectOptions(clients.map(client => ({ value: `${client.id}:${client.name}`, label: client.name })), 'Select client')} /></label>
        <label>Template<input value={structure.name} onChange={event => setStructure({ ...structure, name: event.target.value })} /></label>
        <label>Annual CTC<input value={structure.annualCtc} onChange={event => setStructure({ ...structure, annualCtc: event.target.value.replace(/\D/g, '') })} /></label>
        <button type="button" disabled={saving} onClick={() => void saveTemplate()}>{saving ? 'Saving...' : 'Save Template'}</button>
      </div>
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
            <span>Drag rows to reorder</span>
          </div>
          <div className="salary-line-grid salary-line-head"><span /><span>Component</span><span>Name</span><span>Formula / value</span><span>Monthly</span><span>Annual</span><span /></div>
          <div className="salary-line-list">{calculated.map(row => <div className="salary-line-grid salary-line" draggable={row.component.code !== 'BASIC'} onDragStart={() => setDragLineId(String(row.component.id))} onDragOver={event => event.preventDefault()} onDrop={() => moveTo(String(row.component.id))} key={row.component.id}>
            <span className="salary-drag-handle">⋮⋮</span>
            <div className="salary-code-cell"><span className={`salary-badge ${row.component.category.toLowerCase()}`}>{row.component.category}</span><b title={row.component.code}>{row.component.code}</b></div>
            <strong title={row.component.name}>{row.component.name}</strong>
            <input value={row.line.value} onChange={event => setLine(String(row.component.id), event.target.value)} placeholder={row.component.formula || row.component.value} disabled={row.component.code === 'BASIC'} title={row.line.value || row.component.formula || row.component.value} />
            <output>Rs {Math.round(row.monthly).toLocaleString('en-IN')}</output>
            <output>Rs {Math.round(row.annual).toLocaleString('en-IN')}</output>
            <button type="button" disabled={row.component.code === 'BASIC'} onClick={() => remove(String(row.component.id))}>{row.component.code === 'BASIC' ? 'Locked' : 'Remove'}</button>
          </div>)}</div>
          <div className="salary-template-preview"><b>Preview net</b><span>Monthly Rs {Math.round(preview).toLocaleString('en-IN')}</span><span>Annual Rs {Math.round(preview * 12).toLocaleString('en-IN')}</span></div>
        </section>
      </div>
    </div>
    <DataTable rows={templates} onEdit={setStructure} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client' }, { key: 'annualCtc', label: 'Annual CTC' }, { key: 'active', label: 'Status', render: item => item.active ? 'Active' : 'Inactive' }]} />
  </Card>
}

function Card({ title, children }: { title: string; children: React.ReactNode }) { return <section className="card"><header><i className="blue"><svg className="ui-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l4 4L19 6" /></svg></i><div><h3>{title}</h3><p>Drag components, reorder and configure formulas.</p></div></header>{children}</section> }
