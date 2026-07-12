import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { essApi } from '../services/essApi'
import type { ExpenseClaim, ExpenseDashboard, ExpenseLine, ExpenseOptions, LoadState, SaveExpenseClaim, User, WorkflowTrail } from '../types'
import { showToast, statusClass } from '../utils/ui'

const today = new Date().toISOString().slice(0, 10)

const expenseTypeFromOptions = (options?: ExpenseOptions | null) => options?.headers?.[0]?.expenseType || options?.headers?.[0]?.categoryName || ''
const allowedCategories = (options: ExpenseOptions | null | undefined, expenseType: string) => (options?.categories ?? []).filter(item => !item.isClaimHeader && (!expenseType || item.expenseType === expenseType))

const emptyLine = (options?: ExpenseOptions | null, expenseType = expenseTypeFromOptions(options)): ExpenseLine => ({
  expenseDate: today,
  categoryId: allowedCategories(options, expenseType)?.[0]?.id ?? 0,
  categoryCode: allowedCategories(options, expenseType)?.[0]?.categoryCode ?? '',
  categoryName: allowedCategories(options, expenseType)?.[0]?.categoryName ?? '',
  subCategory: '',
  vendorName: '',
  billNumber: '',
  invoiceNumber: '',
  amount: 0,
  currency: options?.currencies?.[0] ?? 'INR',
  exchangeRate: 1,
  gstAmount: 0,
  approvedAmount: 0,
  costCenter: '',
  project: '',
  customer: options?.clientName ?? '',
  location: '',
  paymentMethod: options?.paymentMethods?.[0] ?? 'Employee Paid',
  receiptAttached: false,
  receiptFileName: '',
  description: '',
  status: 'Draft',
})

const draft0 = (options?: ExpenseOptions | null): SaveExpenseClaim => ({
  id: 0,
  expenseType: expenseTypeFromOptions(options),
  purpose: '',
  customer: options?.clientName ?? '',
  project: '',
  costCenter: '',
  currency: options?.currencies?.[0] ?? 'INR',
  remarks: '',
  lines: [emptyLine(options, expenseTypeFromOptions(options))],
})

const draftFromTravel = (options: ExpenseOptions | null | undefined, travelRequestId: number): SaveExpenseClaim => {
  const trip = options?.travelRequests.find(item => item.id === travelRequestId)
  const travelType = (options?.headers ?? []).find(item => item.expenseType.toLowerCase().includes('travel'))?.expenseType || 'Travel Expense'
  const categories = trip ? allowedCategories(options, travelType).filter(item => travelAllowedCodes(trip).includes(item.categoryCode)) : allowedCategories(options, travelType)
  const first = categories[0]
  return {
    id: 0,
    travelRequestId,
    expenseType: travelType,
    purpose: trip?.purpose || '',
    customer: trip?.customer || options?.clientName || '',
    project: trip?.project || '',
    costCenter: trip?.costCenter || '',
    currency: options?.currencies?.[0] ?? 'INR',
    remarks: trip ? `Claim against ${trip.requestNumber}` : '',
    lines: [{ ...emptyLine(options, travelType), expenseDate: trip ? dateInput(trip.startDateTime) : today, categoryId: first?.id ?? 0, categoryCode: first?.categoryCode ?? '', categoryName: first?.categoryName ?? '' }],
  }
}

export function ExpensePage({ user }: { user: User }) {
  const [state, setState] = useState<LoadState>('loading')
  const [options, setOptions] = useState<ExpenseOptions | null>(null)
  const [dashboard, setDashboard] = useState<ExpenseDashboard | null>(null)
  const [claims, setClaims] = useState<ExpenseClaim[]>([])
  const [form, setForm] = useState<SaveExpenseClaim>(draft0())
  const [editor, setEditor] = useState(false)
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('All')
  const [trailClaim, setTrailClaim] = useState<ExpenseClaim | null>(null)
  const [trail, setTrail] = useState<WorkflowTrail | null>(null)

  const load = () => Promise.all([essApi.expenseOptions(), essApi.expenseDashboard(), essApi.expenseClaims()])
    .then(([nextOptions, nextDashboard, nextClaims]) => {
      setOptions(nextOptions)
      setDashboard(nextDashboard)
      setClaims(nextClaims)
      setForm(current => current.id || editor ? current : draft0(nextOptions))
      setState('ready')
    })
    .catch(() => setState('error'))

  useEffect(() => { void load() }, [user.email])
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Travel & expense', title: editor ? (form.id ? 'Edit expense claim' : 'Other expense claim') : 'Expense claims' } }))
  }, [editor, form.id])
  useEffect(() => {
    const open = () => { setForm(draft0(options)); setEditor(true) }
    const openTravel = (event: Event) => { const id = Number((event as CustomEvent<{ travelRequestId?: number }>).detail?.travelRequestId || 0); setForm(draftFromTravel(options, id)); setEditor(true) }
    const list = () => setEditor(false)
    window.addEventListener('ess:expense:new', open)
    window.addEventListener('ess:expense:from-travel', openTravel as EventListener)
    window.addEventListener('ess:expense:list', list)
    return () => {
      window.removeEventListener('ess:expense:new', open)
      window.removeEventListener('ess:expense:from-travel', openTravel as EventListener)
      window.removeEventListener('ess:expense:list', list)
    }
  }, [options])

  const openNew = () => { setForm(draft0(options)); setEditor(true) }
  const openEdit = (claim: ExpenseClaim) => {
    setForm({
      id: claim.id,
      travelRequestId: claim.travelRequestId,
      expenseType: claim.expenseType || expenseTypeFromOptions(options),
      purpose: claim.purpose,
      customer: claim.customer || options?.clientName || '',
      project: claim.project,
      costCenter: claim.costCenter,
      currency: claim.currency || 'INR',
      remarks: claim.remarks,
      lines: claim.lines.length ? claim.lines : [emptyLine(options, claim.expenseType || expenseTypeFromOptions(options))],
    })
    setEditor(true)
  }
  const save = async (event: FormEvent) => {
    event.preventDefault()
    try {
      const saved = await essApi.saveExpenseClaim(normalizeClaim(form, options))
      showToast('Expense claim draft saved.', 'success')
      setForm(current => ({ ...current, id: saved.id }))
      setEditor(false)
      void load()
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to save expense claim.', 'error')
    }
  }
  const submit = async (claim: ExpenseClaim) => {
    try {
      await essApi.submitExpenseClaim(claim.id)
      showToast('Expense claim submitted for approval.', 'success')
      void load()
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to submit expense claim.', 'error')
    }
  }
  const openTrail = async (claim: ExpenseClaim) => {
    setTrailClaim(claim)
    setTrail(null)
    try { setTrail(await essApi.expenseTrail(claim.id)) }
    catch { showToast('Unable to load approval trail.', 'error') }
  }

  const statuses = ['All', ...Array.from(new Set(claims.map(item => item.status)))]
  const filtered = claims.filter(item => (status === 'All' || item.status === status) && (!query || `${item.claimNumber} ${item.purpose} ${item.customer} ${item.project} ${item.status}`.toLowerCase().includes(query.toLowerCase())))

  if (state === 'loading') return <section className="travel-workspace"><div className="empty-work"><span>Loading expense workspace...</span></div></section>
  if (state === 'error') return <section className="travel-workspace"><div className="empty-work"><b>Expense workspace is unavailable.</b><span>Contact HR if this continues.</span></div></section>
  if (editor) return <ExpenseEditor form={form} options={options} setForm={setForm} onSave={save} onBack={() => setEditor(false)} />

  return <section className="travel-workspace expense-workspace">
    <div className="travel-head"><button type="button" onClick={openNew}>New expense claim</button></div>
    {(options?.validationMessages ?? []).length > 0 && <div className="travel-warning">{options?.validationMessages.map(item => <span key={item}>{item}</span>)}</div>}
    <div className="travel-kpis">{dashboard && Object.entries({ Draft: dashboard.draftClaims, Pending: dashboard.pendingApproval, Approved: dashboard.approved, Rejected: dashboard.rejected, 'Pending payroll': dashboard.pendingPayroll, 'Approved value': formatMoney(dashboard.approvedAmount) }).map(([label, value]) => <article key={label}><span>{label}</span><strong>{value}</strong></article>)}</div>
    <section className="travel-table-card"><div className="request-list-head"><h3>Claims</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search expense claims" /><select value={status} onChange={event => setStatus(event.target.value)}>{statuses.map(item => <option key={item}>{item}</option>)}</select></div></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>Claim</th><th>Expense type</th><th>Purpose</th><th>Lines</th><th>Amount</th><th>Payroll</th><th>Status</th><th>Actions</th></tr></thead><tbody>{filtered.map(claim => <tr key={claim.id}><td><b>{claim.claimNumber || 'Draft'}</b><small>{dateText(claim.claimDate)}</small></td><td><b>{claim.expenseType || '-'}</b><small>{claim.travelRequestNumber ? `Travel: ${claim.travelRequestNumber}` : 'Standalone'}</small></td><td><b>{claim.purpose}</b><small>{claim.customer || '-'}</small></td><td><b>{claim.lines.length} line(s)</b><small>{claim.project || claim.costCenter || '-'}</small></td><td><b>{formatMoney(claim.totalClaimAmount)}</b><small>GST {formatMoney(claim.totalGstAmount)}</small></td><td><b>{claim.payrollStatus}</b><small>{claim.payrollRunId ? `Run #${claim.payrollRunId}` : claim.reimbursementComponentCode}</small></td><td><span className={`task-status ${statusClass(claim.status)}`}>{claim.status}</span></td><td><div className="travel-row-actions">{['Draft', 'Sent Back'].includes(claim.status) && <button type="button" onClick={() => openEdit(claim)}>Edit</button>}{['Draft', 'Sent Back'].includes(claim.status) && <button type="button" onClick={() => void submit(claim)}>Submit</button>}<button type="button" onClick={() => void openTrail(claim)}>Trail</button></div></td></tr>)}{!filtered.length && <tr><td colSpan={8}>No expense claims found.</td></tr>}</tbody></table></div></section>
    {trailClaim && <ExpenseTrailModal claim={trailClaim} trail={trail} onClose={() => { setTrailClaim(null); setTrail(null) }} />}
  </section>
}

function ExpenseEditor({ form, options, setForm, onSave, onBack }: { form: SaveExpenseClaim; options: ExpenseOptions | null; setForm: (updater: SaveExpenseClaim | ((current: SaveExpenseClaim) => SaveExpenseClaim)) => void; onSave: (event: FormEvent) => Promise<void>; onBack: () => void }) {
  const set = <K extends keyof SaveExpenseClaim>(key: K, value: SaveExpenseClaim[K]) => setForm(current => ({ ...current, [key]: value }))
  const updateLine = (index: number, patch: Partial<ExpenseLine>) => setForm(current => ({ ...current, lines: current.lines.map((line, lineIndex) => lineIndex === index ? { ...line, ...patch } : line) }))
  const categories = allowedCategories(options, form.expenseType)
  const isTravelExpense = form.expenseType.toLowerCase().includes('travel')
  const linkedTrip = options?.travelRequests.find(item => item.id === Number(form.travelRequestId))
  const visibleCategories = linkedTrip && isTravelExpense ? categories.filter(item => travelAllowedCodes(linkedTrip).includes(item.categoryCode)) : categories
  const minExpenseDate = linkedTrip ? dateInput(linkedTrip.startDateTime) : ''
  const maxExpenseDate = linkedTrip ? dateInput(linkedTrip.endDateTime) : ''
  const selectExpenseType = (value: string) => setForm(current => ({ ...current, expenseType: value, travelRequestId: value.toLowerCase().includes('travel') ? current.travelRequestId : undefined, lines: [emptyLine(options, value)] }))
  const selectCategory = (index: number, id: number) => {
    const category = visibleCategories.find(item => item.id === id)
    updateLine(index, { categoryId: id, categoryCode: category?.categoryCode ?? '', categoryName: category?.categoryName ?? '', receiptAttached: category?.receiptMandatory ? true : form.lines[index]?.receiptAttached ?? false })
  }
  const selectTravel = (id: number) => {
    const trip = options?.travelRequests.find(item => item.id === id)
    setForm(current => ({ ...current, travelRequestId: id || undefined, purpose: trip?.purpose || current.purpose, customer: trip?.customer || current.customer, project: trip?.project || current.project, costCenter: trip?.costCenter || current.costCenter }))
  }
  const addLine = () => setForm(current => ({ ...current, lines: [...current.lines, { ...emptyLine(options, current.expenseType), categoryId: visibleCategories[0]?.id ?? 0, categoryCode: visibleCategories[0]?.categoryCode ?? '', categoryName: visibleCategories[0]?.categoryName ?? '', expenseDate: minExpenseDate || today }] }))
  const removeLine = (index: number) => setForm(current => ({ ...current, lines: current.lines.length <= 1 ? current.lines : current.lines.filter((_, lineIndex) => lineIndex !== index) }))
  const total = form.lines.reduce((sum, line) => sum + Number(line.amount || 0), 0)

  return <section className="travel-editor-page expense-editor-page">
    <form className="travel-full-form" onSubmit={onSave}>
      <section className="travel-form-section"><h4>Claim header</h4><div className="travel-form-grid"><label><span>Expense type</span><select required value={form.expenseType} onChange={event => selectExpenseType(event.target.value)}><option value="">Select expense type</option>{options?.headers.map(item => <option key={item.id} value={item.expenseType || item.categoryName}>{item.categoryName}</option>)}</select></label>{isTravelExpense && <label><span>Linked travel request</span><select required value={form.travelRequestId ?? ''} onChange={event => selectTravel(Number(event.target.value || 0))}><option value="">Select travel request</option>{options?.travelRequests.map(item => <option key={item.id} value={item.id}>{item.requestNumber} - {item.purpose}</option>)}</select></label>}<label className="wide"><span>Purpose</span><input required value={form.purpose} onChange={event => set('purpose', event.target.value)} /></label><label><span>Customer / Client</span><input value={form.customer || options?.clientName || ''} onChange={event => set('customer', event.target.value)} /></label><label><span>Project</span><input value={form.project} onChange={event => set('project', event.target.value)} /></label><label><span>Cost center</span><input value={form.costCenter} onChange={event => set('costCenter', event.target.value)} /></label><label><span>Currency</span><select value={form.currency} onChange={event => set('currency', event.target.value)}>{options?.currencies.map(item => <option key={item}>{item}</option>)}</select></label></div>{linkedTrip && <p className="travel-warning compact">Claim dates allowed from {dateText(linkedTrip.startDateTime)} to {dateText(linkedTrip.endDateTime)}.</p>}</section>
      <section className="travel-form-section"><div className="travel-section-title compact"><div><h4>Expense lines</h4></div><button type="button" disabled={!visibleCategories.length} onClick={addLine}>Add line</button></div><div className="travel-trip-table-wrap"><table className="travel-trip-table expense-line-table"><thead><tr><th>Date</th><th>Category</th><th>Vendor</th><th>Bill / invoice</th><th>Amount</th><th>GST</th><th>Location</th><th>Receipt</th><th>Description</th><th></th></tr></thead><tbody>{form.lines.map((line, index) => { const category = visibleCategories.find(item => item.id === Number(line.categoryId)); return <tr key={index}><td><input type="date" min={minExpenseDate} max={maxExpenseDate} value={dateInput(line.expenseDate)} onChange={event => updateLine(index, { expenseDate: event.target.value })} /></td><td><select value={line.categoryId || ''} onChange={event => selectCategory(index, Number(event.target.value || 0))}><option value="">Select</option>{visibleCategories.map(item => <option key={item.id} value={item.id}>{item.categoryName}</option>)}</select><small>{category?.receiptMandatory ? 'Receipt mandatory' : 'Receipt optional'}{category?.maximumClaim ? ` / Max ${formatMoney(category.maximumClaim)}` : ''}</small></td><td><input value={line.vendorName} onChange={event => updateLine(index, { vendorName: event.target.value })} /></td><td><input value={line.billNumber} onChange={event => updateLine(index, { billNumber: event.target.value })} placeholder="Bill no." /><input value={line.invoiceNumber} onChange={event => updateLine(index, { invoiceNumber: event.target.value })} placeholder="Invoice no." /></td><td><input type="number" min={0} value={line.amount} onChange={event => updateLine(index, { amount: Number(event.target.value || 0) })} /></td><td><input type="number" min={0} value={line.gstAmount} onChange={event => updateLine(index, { gstAmount: Number(event.target.value || 0) })} disabled={!category?.gstApplicable} /></td><td><select value={line.location} onChange={event => updateLine(index, { location: event.target.value })}><option value="">Select</option>{options?.locations.map(item => <option key={item}>{item}</option>)}</select></td><td><label className="receipt-check"><input type="checkbox" checked={line.receiptAttached} onChange={event => updateLine(index, { receiptAttached: event.target.checked })} /> Attached</label><input value={line.receiptFileName} onChange={event => updateLine(index, { receiptFileName: event.target.value, receiptAttached: Boolean(event.target.value) || line.receiptAttached })} placeholder="File name" /></td><td><textarea value={line.description} onChange={event => updateLine(index, { description: event.target.value })} /></td><td><button type="button" className="trip-remove" disabled={form.lines.length <= 1} onClick={() => removeLine(index)}>Remove</button></td></tr> })}</tbody></table></div><div className="expense-total">Total claim amount <b>{formatMoney(total)}</b></div></section>
      <section className="travel-form-section"><h4>Remarks</h4><textarea value={form.remarks} onChange={event => set('remarks', event.target.value)} /></section>
      <footer className="travel-editor-actions"><button type="button" className="secondary" onClick={onBack}>Cancel</button><button disabled={!visibleCategories.length}>Save draft</button></footer>
    </form>
  </section>
}

function ExpenseTrailModal({ claim, trail, onClose }: { claim: ExpenseClaim; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{claim.claimNumber || 'Expense claim'}</h3></div><small className={`trail-status ${statusClass(claim.status)}`}>{claim.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This claim may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}

function normalizeClaim(form: SaveExpenseClaim, options: ExpenseOptions | null): SaveExpenseClaim {
  return {
    ...form,
    expenseType: form.expenseType || expenseTypeFromOptions(options),
    customer: form.customer || options?.clientName || '',
    lines: form.lines.filter(line => line.categoryId || line.amount || line.description || line.vendorName).map(line => ({ ...line, expenseDate: dateInput(line.expenseDate), currency: line.currency || form.currency || 'INR', exchangeRate: Number(line.exchangeRate || 1), amount: Number(line.amount || 0), gstAmount: Number(line.gstAmount || 0), customer: line.customer || form.customer || options?.clientName || '', project: line.project || form.project, costCenter: line.costCenter || form.costCenter })),
  }
}

function formatMoney(value: number | string) {
  const amount = Number(value || 0)
  return `Rs ${amount.toLocaleString('en-IN', { maximumFractionDigits: 2 })}`
}

function dateText(value?: string) {
  if (!value) return '-'
  return new Date(value).toLocaleDateString('en-IN')
}

function dateInput(value?: string) {
  if (!value) return today
  return value.slice(0, 10)
}

function travelAllowedCodes(trip: { travelMode?: string; accommodationRequired?: boolean; localConveyanceRequired?: boolean }) {
  const codes = ['MEALS']
  const mode = (trip.travelMode || '').toLowerCase()
  if (!mode || mode.includes('air') || mode.includes('flight')) codes.push('AIR_FARE')
  if (!mode || mode.includes('train') || mode.includes('rail')) codes.push('TRAIN_FARE')
  if (!mode || mode.includes('bus')) codes.push('BUS_FARE')
  if (mode.includes('cab') || mode.includes('taxi')) codes.push('CAB_TAXI')
  if (mode.includes('own') || mode.includes('car')) codes.push('FUEL', 'PARKING', 'TOLL')
  if (trip.accommodationRequired) codes.push('HOTEL_STAY')
  if (trip.localConveyanceRequired) codes.push('CAB_TAXI', 'FUEL', 'PARKING', 'TOLL', 'METRO')
  return Array.from(new Set(codes))
}
