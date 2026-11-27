import { useMemo, useState, type DragEvent, type KeyboardEvent } from 'react'
import { ArrowDownOutlined, ArrowUpOutlined, CheckOutlined, DeleteOutlined, HolderOutlined, PlusOutlined, SearchOutlined, SettingOutlined } from '@ant-design/icons'
import { Button, Drawer, Input } from 'antd'
import type { Component, StructureLine } from '../types/payroll'
import type { PayrollValue } from '../services/payrollCalculationService'
import SalaryFormulaInput from './SalaryFormulaInput'
import { SALARY_COMPONENT_DRAG_TYPE, salaryComponentDragPayload, salaryComponentDropAction } from '../utils/salaryTemplateComponentOrder'
import './SalaryTemplateComponents.css'

type Props = {
  components: Component[]
  lines: StructureLine[]
  selectedLineId: string
  onSelect: (id: string) => void
  onAdd: (id: string) => void
  onRemove: (id: string) => void
  onMove: (sourceId: string, targetId: string) => void
  onUpdate: (id: string, patch: Partial<StructureLine>) => void
  onConfigure: (id: string) => void
  formulaValues?: PayrollValue[]
  reviewTokens?: Set<string>
  onCreateComponent?: (category: string, onCreated: (component: Component) => void) => void
  disabled?: boolean
}

const categories = [{ key: 'All', label: 'All' }, { key: 'Earning', label: 'Earnings' }, { key: 'Deduction', label: 'Deductions' }, { key: 'Reimbursement', label: 'Reimbursements' }, { key: 'Benefit', label: 'Benefits' }, { key: 'Correction', label: 'Corrections' }] as const

export default function SalaryTemplateComponents({ components, lines, selectedLineId, onSelect, onAdd, onRemove, onMove, onUpdate, onConfigure, formulaValues = [], reviewTokens, onCreateComponent, disabled = false }: Props) {
  const [category, setCategory] = useState<string>('All'), [search, setSearch] = useState('')
  const [lineSearch, setLineSearch] = useState(''), [libraryOpen, setLibraryOpen] = useState(false)
  const [drag, setDrag] = useState<{ kind: 'library' | 'line'; id: string } | null>(null), [dropTarget, setDropTarget] = useState('')
  const [announcement, setAnnouncement] = useState('')
  const componentMap = useMemo(() => new Map(components.map(component => [String(component.id), component])), [components])
  const activeComponents = components.filter(component => component.active)
  const ids = lines.map(line => String(line.componentId)), included = new Set(ids)
  const library = activeComponents.filter(component => (category === 'All' || component.category === category) && (!search.trim() || `${component.code} ${component.name}`.toLowerCase().includes(search.trim().toLowerCase())))
  const visibleLines = lines.map((line, index) => ({ line, index })).filter(({ line }) => {
    const component = componentMap.get(String(line.componentId))
    return !lineSearch.trim() || `${component?.code || line.componentId} ${component?.name || 'Missing component'} ${line.formula || line.value || component?.formula || component?.value || ''}`.toLowerCase().includes(lineSearch.trim().toLowerCase())
  })
  const clearDrag = () => { setDrag(null); setDropTarget('') }
  const start = (event: DragEvent<HTMLElement>, kind: 'library' | 'line', id: string) => {
    if (disabled || kind === 'library' && included.has(id)) { event.preventDefault(); return }
    event.dataTransfer.effectAllowed = kind === 'library' ? 'copy' : 'move'
    event.dataTransfer.setData(SALARY_COMPONENT_DRAG_TYPE, salaryComponentDragPayload(kind, id))
    setDrag({ kind, id }); onSelect(id)
  }
  const accepts = (event: DragEvent<HTMLElement>) => !disabled && event.dataTransfer.types.includes(SALARY_COMPONENT_DRAG_TYPE)
  const over = (event: DragEvent<HTMLElement>, id = 'canvas') => {
    if (!accepts(event)) return
    event.preventDefault(); event.stopPropagation()
    event.dataTransfer.dropEffect = drag?.kind === 'line' ? 'move' : 'copy'
    setDropTarget(id)
  }
  const drop = (event: DragEvent<HTMLElement>, targetId?: string) => {
    if (!accepts(event)) return
    event.preventDefault(); event.stopPropagation()
    const action = salaryComponentDropAction(event.dataTransfer.getData(SALARY_COMPONENT_DRAG_TYPE), activeComponents.map(component => String(component.id)), ids, targetId ?? ids.at(-1))
    clearDrag()
    if (action?.type === 'add') { onAdd(action.id); setAnnouncement(`${componentMap.get(action.id)?.name || action.id} added to the template.`) }
    if (action?.type === 'move') { onMove(action.sourceId, action.targetId); setAnnouncement(`${componentMap.get(action.sourceId)?.name || action.sourceId} moved in the calculation order.`) }
  }
  const move = (id: string, targetId: string | undefined) => {
    if (disabled || !targetId) return
    onMove(id, targetId); setAnnouncement(`${componentMap.get(id)?.name || id} moved in the calculation order.`)
  }
  const keyboardMove = (event: KeyboardEvent<HTMLButtonElement>, id: string, index: number) => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return
    event.preventDefault(); move(id, ids[index + (event.key === 'ArrowUp' ? -1 : 1)])
  }
  const add = (id: string) => { if (!disabled && !included.has(id)) { onAdd(id); setAnnouncement(`${componentMap.get(id)?.name || id} added to the template.`) } }
  const closeLibrary = () => { setLibraryOpen(false); clearDrag() }
  const createComponent = () => onCreateComponent?.(category === 'All' ? 'Earning' : category, saved => {
    if (!saved.active) return
    setCategory(saved.category)
    setSearch(saved.code)
  })

  return <div className="salary-template-components">
    <section className={`salary-component-workspace${drag ? ' is-dragging' : ''}${dropTarget === 'canvas' ? ' is-drop-target' : ''}`} aria-label="Template components" onDragOver={event => over(event)} onDrop={event => drop(event)} onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropTarget('') }}>
      <header>
        <div className="salary-component-heading"><h3>Components <span>{lines.length}</span></h3><p>Calculated in the order shown. Type a component code or a function such as IF, MIN or MAX in the ƒx box.</p></div>
        <div className="salary-component-toolbar"><Input aria-label="Find template component" placeholder="Find a component or formula" prefix={<SearchOutlined />} allowClear value={lineSearch} onChange={event => setLineSearch(event.target.value)} /><Button icon={<PlusOutlined />} onClick={() => setLibraryOpen(true)} disabled={disabled}>Add components</Button></div>
      </header>
      {!!lines.length && <div className="salary-component-summary">{categories.filter(item => item.key !== 'All').map(item => { const count = lines.filter(line => componentMap.get(String(line.componentId))?.category === item.key).length; return count ? <span key={item.key} className={`salary-component-summary-item is-${item.key.toLowerCase()}`}><b>{count}</b> {item.label}</span> : null })}<span className="salary-component-order-label">Drag a handle or use the arrows to reorder</span></div>}
      {!!lines.length && <div className="salary-component-line-head" aria-hidden="true"><span>#</span><span>Component</span><span>Type</span><span>Formula / value</span><span>Actions</span></div>}
      <div className="salary-component-lines">
        {visibleLines.map(({ line, index }) => {
          const id = String(line.componentId), component = componentMap.get(id)
          const unavailable = !component || !component.active
          const name = component?.name || `Missing component ${id}`
          const effectiveType = line.calculationType || component?.calculationType || ''
          const daily = effectiveType === 'Daily Rate'
          const formula = line.formula || line.value || ''
          const masterDefault = component?.formula || component?.value || ''
          const requiresEmployeeAmount = effectiveType === 'Fixed Amount' && !formula.trim() && !masterDefault.trim()
          let amountHint = formula ? 'Template setting' : `Component default${masterDefault ? ` · ${masterDefault}` : ''}`
          if (requiresEmployeeAmount) amountHint = 'Employee Master input · sample used only in simulation'
          if (daily) amountHint = Number(formula || masterDefault) > 0 ? 'Per payable day' : 'Enter a positive daily rate'
          const insertion = dropTarget === id && drag?.kind === 'line' ? ids.indexOf(drag.id) < index ? ' is-drop-after' : ' is-drop-before' : ''
          return <article data-component-id={id} className={`salary-component-template-line${selectedLineId === id ? ' is-selected' : ''}${unavailable ? ' is-unavailable' : ''}${drag?.kind === 'line' && drag.id === id ? ' is-drag-source' : ''}${dropTarget === id ? ' is-drop-target' : ''}${insertion}`} key={`${id}:${ids.slice(0, index).filter(value => value === id).length}`} onDragOver={event => over(event, id)} onDrop={event => drop(event, id)}>
            <div className="salary-component-order-cell"><button type="button" className="salary-component-drag-handle" draggable={!disabled} disabled={disabled} onDragStart={event => start(event, 'line', id)} onDragEnd={clearDrag} onKeyDown={event => keyboardMove(event, id, index)} aria-label={`Move ${name}; use up or down arrow keys`} title="Drag to reorder, or use ↑ and ↓"><HolderOutlined /></button><span>{index + 1}</span></div>
            <div className="salary-component-line-name"><strong>{name}</strong><span>{component?.code || id}</span>{unavailable && <small role="status">{component ? 'Inactive component — review before saving' : 'Component not found — review before saving'}</small>}</div>
            <span className={`salary-component-category is-${(component?.category || 'missing').toLowerCase()}`}>{component?.componentRole === 'Information' ? 'Information' : component?.category || 'Missing'}</span>
            <div className="salary-component-formula"><span className="salary-component-mobile-label">{daily ? 'Daily rate' : 'Formula / value'}</span>
              {daily ? <input aria-label={`${name} daily rate`} disabled={disabled || unavailable} value={formula || masterDefault} placeholder="0" title="Daily rate × attendance payable days" onFocus={event => { onSelect(id); if (event.target.value === '0') event.target.select() }} onChange={event => onUpdate(id, { calculationType: 'Daily Rate', value: event.target.value || '0', formula: '', proRataOverride: 'false' })} />
                : <SalaryFormulaInput reviewSyntax={selectedLineId === id} reviewTokens={selectedLineId === id ? reviewTokens : undefined} defaultOnClear label={`${name} formula or value`} disabled={disabled || unavailable} value={formula} values={formulaValues} includedComponentIds={ids} excludeComponentId={id} title={requiresEmployeeAmount ? 'Each employee supplies their own salary amount in Employee Master; the imported sample is not a shared default.' : formula || masterDefault} onFocus={() => onSelect(id)} onChange={value => onUpdate(id, { calculationType: 'Formula', formula: value, value: '' })} />}
              <span>{amountHint}</span></div>
            <div className="salary-component-line-actions"><Button size="small" type="text" icon={<SettingOutlined />} title={`Configure ${name}`} aria-label={`Configure ${name}`} disabled={disabled || unavailable} onClick={() => { onSelect(id); onConfigure(id) }} /><span className="salary-component-order-actions"><Button size="small" type="text" icon={<ArrowUpOutlined />} disabled={disabled || index === 0} aria-label={`Move ${name} up`} title="Move up" onClick={() => move(id, ids[index - 1])} /><Button size="small" type="text" icon={<ArrowDownOutlined />} disabled={disabled || index === lines.length - 1} aria-label={`Move ${name} down`} title="Move down" onClick={() => move(id, ids[index + 1])} /><Button size="small" type="text" danger icon={<DeleteOutlined />} disabled={disabled} aria-label={`Remove ${name}`} title={`Remove ${name}`} onClick={() => { onRemove(id); setAnnouncement(`${name} removed from the template.`) }} /></span></div>
          </article>
        })}
        {!!lines.length && !visibleLines.length && <div className="salary-components-empty"><SearchOutlined /><strong>No matching components</strong><p>Try a different name, code or formula.</p><Button onClick={() => setLineSearch('')}>Clear search</Button></div>}
        {!lines.length && <div className={`salary-components-empty salary-components-dropzone${drag?.kind === 'library' ? ' is-active' : ''}`}><PlusOutlined /><h3>Add salary components</h3><p>Choose the earnings, deductions and benefits for this template.</p><Button icon={<PlusOutlined />} onClick={() => setLibraryOpen(true)} disabled={disabled}>Browse components</Button></div>}
      </div>
      {drag && <div className="salary-component-drop-hint" aria-hidden="true">{drag.kind === 'library' ? 'Drop to add this component once' : 'Drop on a component to change its order'}</div>}
      <footer className="salary-component-footer"><span>{lineSearch.trim() ? `${visibleLines.length} of ${lines.length} components` : `${lines.length} components`}</span><span>Changes apply when you save the template.</span></footer>
    </section>
    <Drawer title="Add components" open={libraryOpen} onClose={closeLibrary} width={460} rootClassName="salary-component-library-drawer" mask={false} extra={<Button onClick={closeLibrary}>Done</Button>}>
      <aside className="salary-component-library" aria-label="Salary component library">
        <header>
          <h3>Component library <span className="salary-component-count">{activeComponents.length}</span></h3>
          {onCreateComponent && <Button icon={<PlusOutlined />} disabled={disabled} onClick={createComponent}>Create {category === 'All' ? 'component' : category.toLowerCase()}</Button>}
        </header>
        <Input aria-label="Search salary components" placeholder="Search name or code" prefix={<SearchOutlined />} allowClear value={search} onChange={event => setSearch(event.target.value)} />
        <nav className="salary-component-categories" aria-label="Component category">{categories.map(item => <button type="button" key={item.key} aria-pressed={category === item.key} onClick={() => setCategory(item.key)}>{item.label}</button>)}</nav>
        <div className="salary-component-library-list">
          {library.map(component => {
            const id = String(component.id), added = included.has(id)
            return <article key={id} className={`salary-component-library-item${added ? ' is-added' : ''}`}>
              <button type="button" className="salary-component-drag-handle" draggable={!disabled && !added} disabled={disabled || added} onDragStart={event => start(event, 'library', id)} onDragEnd={clearDrag} aria-label={`Drag ${component.name} to template`} title="Drag to add"><HolderOutlined /></button>
              <div><strong>{component.name}</strong><span>{component.code}</span><small>{component.componentRole === 'Information' ? 'Information' : component.category} · {component.calculationType}</small></div>
              <Button size="small" type={added ? 'text' : 'default'} icon={added ? <CheckOutlined /> : <PlusOutlined />} disabled={disabled || added} onClick={() => add(id)} aria-label={added ? `${component.name} already added` : `Add ${component.name}`}>{added ? 'Added' : 'Add'}</Button>
            </article>
          })}
          {!library.length && <div className="salary-components-empty"><SearchOutlined /><strong>No matching components</strong><p>Try another name, code or category.</p></div>}
        </div>
      </aside>
    </Drawer>
    <span className="salary-component-announcement" role="status" aria-live="polite">{announcement}</span>
  </div>
}
