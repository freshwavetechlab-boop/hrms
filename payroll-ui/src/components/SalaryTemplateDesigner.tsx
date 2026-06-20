import { useState } from 'react'
import DataTable from './DataTable'

type Client = { id: number; name: string }
type Component = { id: number; code: string; name: string; category: string; formula: string; value: string; calculationType: string; priority: string; active: boolean }
type Structure = { id: number; clientId: string; name: string; annualCtc: string; lines: { componentId: string; value: string }[]; active: boolean }

export default function SalaryTemplateDesigner({ clients, components, structure, setStructure, templates, saveTemplate }: { clients: Client[]; components: Component[]; structure: Structure; setStructure: (s: Structure) => void; templates: Structure[]; saveTemplate: () => void }) {
  const [tab, setTab] = useState<'Earning' | 'Deduction' | 'Reimbursement'>('Earning')
  const [dragId, setDragId] = useState('')
  const [dragLineId, setDragLineId] = useState('')
  const library = components.filter(x => x.active && x.category === tab)
  const add = (id: string) => !structure.lines.some(x => x.componentId === id) && setStructure({ ...structure, lines: [...structure.lines, { componentId: id, value: components.find(c => String(c.id) === id)?.formula || components.find(c => String(c.id) === id)?.value || '' }] })
  const isBasic = (id: string) => components.find(c => String(c.id) === id)?.code === 'BASIC'
  const lines = structure.lines.some(x => isBasic(x.componentId)) ? structure.lines : [{ componentId: String(components.find(c => c.code === 'BASIC')?.id ?? 101), value: 'CTC * 40%' }, ...structure.lines]
  const remove = (id: string) => !isBasic(id) && setStructure({ ...structure, lines: lines.filter(x => x.componentId !== id) })
  const setLine = (id: string, value: string) => setStructure({ ...structure, lines: structure.lines.map(x => x.componentId === id ? { ...x, value } : x) })
  const moveTo = (targetId: string) => { if (!dragLineId || dragLineId === targetId || isBasic(dragLineId)) return; const source = lines.find(x => x.componentId === dragLineId); const next = lines.filter(x => x.componentId !== dragLineId); const target = next.findIndex(x => x.componentId === targetId); if (!source) return; next.splice(Math.max(1, target), 0, source); setStructure({ ...structure, lines: next }) }
  const monthly = Number(structure.annualCtc || 0) / 12
  const values: Record<string, number> = {}
  const normalize = (text: string) => text.toUpperCase().replace(/\s+/g, '').replace(/(\d+(?:\.\d+)?)%OF(CTC|BASIC)/g, '$2*$1/100').replace(/(CTC|BASIC)\*(\d+(?:\.\d+)?)%/g, '$1*$2/100').replace(/(\d+(?:\.\d+)?)%/g, '$1/100')
  const toFormula = (text: string) => normalize(text).replace(/MIN\(/g, 'Math.min(').replace(/CTC/g, String(monthly)).replace(/BASIC/g, String(values.BASIC || 0)).replace(/[^0-9+\-*/().,Mathmin]/g, '')
  const evalFormula = (text: string) => {
    try { return Number(Function(`"use strict";return (${toFormula(text)})`)()) || 0 } catch { return 0 }
  }
  const calc = (c?: Component, lineValue?: string): number => {
    if (!c) return 0
    const source = lineValue || c.formula || c.value
    if (c.calculationType === 'Balancing Amount' || /balance/i.test(source)) {
      const used = Object.entries(values).filter(([code]) => components.find(x => x.code === code)?.category === 'Earning').reduce((s, [, v]) => s + v, 0)
      return Math.max(0, monthly - used)
    }
    if (c.calculationType === 'Flat Amount' && /^\d+/.test(source)) return Number(source)
    return evalFormula(source)
  }
  const calculated = lines.map(l => { const component = components.find(c => String(c.id) === l.componentId); const amount = calc(component, l.value); if (component) values[component.code] = amount; return { line: l, component, amount, annual: amount * 12 } }).filter(x => x.component)
  const preview = calculated.reduce((sum, x) => sum + (x.component?.category === 'Deduction' ? -x.amount : x.amount), 0)
  return <Card title="Enterprise salary template designer">
    <div className="template-head"><label>Client<select value={structure.clientId} onChange={e => setStructure({ ...structure, clientId: e.target.value })}><option value="">Select</option>{clients.map(c => <option value={`${c.id}:${c.name}`} key={c.id}>{c.name}</option>)}</select></label><label>Template<input value={structure.name} onChange={e => setStructure({ ...structure, name: e.target.value })} /></label><label>Annual CTC<input value={structure.annualCtc} onChange={e => setStructure({ ...structure, annualCtc: e.target.value.replace(/\D/g, '') })} /></label><button type="button" onClick={saveTemplate}>Save Template</button></div>
    <div className="designer-grid"><section className="component-palette"><div className="tabs">{(['Earning', 'Deduction', 'Reimbursement'] as const).map(x => <button type="button" className={tab === x ? 'on' : ''} onClick={() => setTab(x)} key={x}>{x}s</button>)}</div>{library.map(c => <div className="palette-item" draggable onDragStart={() => setDragId(String(c.id))} key={c.id}><b>{c.code}</b><span>{c.name}</span><small>{c.calculationType}</small><button type="button" onClick={() => add(String(c.id))}>Add</button></div>)}</section>
      <section className="template-canvas" onDragOver={e => e.preventDefault()} onDrop={() => dragId && add(dragId)}><h3>Template components</h3><div className="template-line head"><span /><span>Type</span><span>Code</span><span>Name</span><span>Formula</span><span>Monthly</span><span>Annual</span><span /></div>{calculated.map(x => x.component && <div className="template-line" draggable={x.component.code !== 'BASIC'} onDragStart={() => setDragLineId(String(x.component?.id))} onDragOver={e => e.preventDefault()} onDrop={() => moveTo(String(x.component?.id))} key={x.component.id}><span className="drag-handle">⋮⋮</span><span className={`pill ${x.component.category.toLowerCase()}`}>{x.component.category}</span><b>{x.component.code}</b><strong>{x.component.name}</strong><input value={x.line.value} onChange={e => setLine(String(x.component?.id), e.target.value)} placeholder={x.component.formula || x.component.value} disabled={x.component.code === 'BASIC'} /><output>Rs {Math.round(x.amount).toLocaleString('en-IN')}</output><output>Rs {Math.round(x.annual).toLocaleString('en-IN')}</output><button type="button" disabled={x.component.code === 'BASIC'} onClick={() => remove(String(x.component?.id))}>{x.component.code === 'BASIC' ? 'Locked' : 'Remove'}</button></div>)}<div className="template-preview"><b>Preview net</b><span>Monthly Rs {Math.round(preview).toLocaleString('en-IN')} | Annual Rs {Math.round(preview * 12).toLocaleString('en-IN')}</span></div></section></div>
    <DataTable rows={templates} onEdit={setStructure} columns={[{ key: 'name', label: 'Template' }, { key: 'clientId', label: 'Client' }, { key: 'annualCtc', label: 'Annual CTC' }, { key: 'active', label: 'Status', render: x => x.active ? 'Active' : 'Inactive' }]} />
  </Card>
}

function Card({ title, children }: { title: string; children: React.ReactNode }) { return <section className="card"><header><i className="blue"><svg className="ui-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l4 4L19 6" /></svg></i><div><h3>{title}</h3><p>Drag components, reorder and configure formulas.</p></div></header>{children}</section> }


