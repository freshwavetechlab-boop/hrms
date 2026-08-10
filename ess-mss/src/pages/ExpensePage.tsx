import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Button, Checkbox, Select } from 'antd'
import { AdminRecordMaintenanceModal } from '../components/AdminRecordMaintenanceModal'
import type { AdminMaintenanceAction } from '../components/AdminRecordMaintenanceModal'
import { essApi } from '../services/essApi'
import type { ExpenseCategoryOption, ExpenseClaim, ExpenseDashboard, ExpenseLine, ExpenseOptions, LoadState, SaveExpenseClaim, User, WorkflowTrail } from '../types'
import { showToast, statusClass } from '../utils/ui'
import { canMaintainTravelExpense } from '../utils/access'

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
  customer: '',
  location: '',
  paymentMethod: options?.paymentMethods?.[0] ?? 'Employee Paid',
  receiptAttached: false,
  receiptFileName: '',
  description: '',
  status: 'Draft',
  cityCategory: '',
  distanceKm: 0,
  dutyHours: 0,
  lodgingClaimed: false,
  lodgingIncludesFood: false,
  alternativeStay: false,
  overnightStay: false,
  entitlementLabel: '',
  entitlementMessage: '',
})

const draft0 = (options?: ExpenseOptions | null): SaveExpenseClaim => ({
  id: 0,
  expenseType: expenseTypeFromOptions(options),
  purpose: '',
  customer: '',
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
    customer: '',
    project: '',
    costCenter: trip?.costCenter || '',
    currency: options?.currencies?.[0] ?? 'INR',
    remarks: trip ? `Claim against ${trip.requestNumber}` : '',
    lines: [{ ...emptyLine(options, travelType), expenseDate: trip ? dateInput(trip.startDateTime) : today, categoryId: first?.id ?? 0, categoryCode: first?.categoryCode ?? '', categoryName: first?.categoryName ?? '' }],
  }
}

export function ExpensePage({ user }: { user: User }) {
  const adminMaintenance = canMaintainTravelExpense(user)
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
  const [maintenanceClaim, setMaintenanceClaim] = useState<ExpenseClaim | null>(null)

  const load = () => adminMaintenance
    ? essApi.adminExpenseClaims().then(nextClaims => {
      setOptions(null)
      setDashboard(null)
      setClaims(nextClaims)
      setEditor(false)
      setState('ready')
    }).catch(() => setState('error'))
    : Promise.all([essApi.expenseOptions(), essApi.expenseDashboard(), essApi.expenseClaims()])
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
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Travel & expense', title: adminMaintenance ? 'Expense claim administration' : editor ? (form.id ? 'Edit expense claim' : 'Other expense claim') : 'Expense claims' } }))
  }, [adminMaintenance, editor, form.id])
  useEffect(() => {
    const open = () => { if (!adminMaintenance) { setForm(draft0(options)); setEditor(true) } }
    const openTravel = (event: Event) => { if (!adminMaintenance) { const id = Number((event as CustomEvent<{ travelRequestId?: number }>).detail?.travelRequestId || 0); setForm(draftFromTravel(options, id)); setEditor(true) } }
    const list = () => setEditor(false)
    window.addEventListener('ess:expense:new', open)
    window.addEventListener('ess:expense:from-travel', openTravel as EventListener)
    window.addEventListener('ess:expense:list', list)
    return () => {
      window.removeEventListener('ess:expense:new', open)
      window.removeEventListener('ess:expense:from-travel', openTravel as EventListener)
      window.removeEventListener('ess:expense:list', list)
    }
  }, [adminMaintenance, options])

  const openNew = () => { setForm(draft0(options)); setEditor(true) }
  const openEdit = (claim: ExpenseClaim) => {
    setForm({
      id: claim.id,
      travelRequestId: claim.travelRequestId,
      expenseType: claim.expenseType || expenseTypeFromOptions(options),
      purpose: claim.purpose,
      customer: '',
      project: '',
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
  const maintain = async (action: AdminMaintenanceAction, reason: string) => {
    if (!maintenanceClaim) return
    try {
      const result = action === 'revert'
        ? await essApi.adminRevertExpenseClaim(maintenanceClaim.id, reason)
        : await essApi.adminDeleteExpenseClaim(maintenanceClaim.id, reason)
      showToast(result.message, 'success')
      setMaintenanceClaim(null)
      await load()
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to clean this expense claim.', 'error')
      throw error
    }
  }

  const statuses = ['All', ...Array.from(new Set(claims.map(item => item.status)))]
  const filtered = claims.filter(item => (status === 'All' || item.status === status) && (!query || `${item.claimNumber} ${item.purpose} ${item.expenseType} ${item.costCenter} ${item.status}`.toLowerCase().includes(query.toLowerCase())))

  if (state === 'loading') return <section className="travel-workspace"><div className="empty-work"><span>Loading expense workspace...</span></div></section>
  if (state === 'error') return <section className="travel-workspace"><div className="empty-work"><b>Expense workspace is unavailable.</b><span>Contact HR if this continues.</span></div></section>
  if (editor && !adminMaintenance) return <ExpenseEditor form={form} options={options} setForm={setForm} onSave={save} onBack={() => setEditor(false)} />

  return <section className="travel-workspace expense-workspace">
    <div className="travel-head">{adminMaintenance ? <div className="admin-maintenance-heading"><b>Expense claim cleanup</b><span>Revert approved test records to Draft or permanently remove records that have not been consumed by payroll.</span></div> : <button type="button" onClick={openNew}>New expense claim</button>}</div>
    {adminMaintenance && <div className="admin-maintenance-banner"><b>Admin safety is active</b><span>Payroll-consumed claims and settled travel advances cannot be deleted. The system will identify the blocking dependency.</span></div>}
    {(options?.validationMessages ?? []).length > 0 && <div className="travel-warning">{options?.validationMessages.map(item => <span key={item}>{item}</span>)}</div>}
    <div className="travel-kpis">{dashboard && Object.entries({ Draft: dashboard.draftClaims, Pending: dashboard.pendingApproval, Approved: dashboard.approved, Rejected: dashboard.rejected, 'Pending payroll': dashboard.pendingPayroll, 'Approved value': formatMoney(dashboard.approvedAmount) }).map(([label, value]) => <article key={label}><span>{label}</span><strong>{value}</strong></article>)}</div>
    <section className="travel-table-card"><div className="request-list-head"><h3>{adminMaintenance ? 'All accessible claims' : 'Claims'}</h3><div><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search expense claims" /><Select aria-label="Expense claim status" value={status} onChange={setStatus} options={statuses.map(item => ({ label: item, value: item }))} /></div></div><div className="travel-table-scroll"><table className="travel-table"><thead><tr><th>Claim</th><th>Expense type</th><th>Purpose</th><th>Lines</th><th>Amount</th><th>Payroll</th><th>Status</th><th>Actions</th></tr></thead><tbody>{filtered.map(claim => <tr key={claim.id}><td><b>{claim.claimNumber || 'Draft'}</b><small>{adminMaintenance ? claim.employeeName || 'Employee unavailable' : dateText(claim.claimDate)}</small>{adminMaintenance && <small>{dateText(claim.claimDate)}</small>}</td><td><b>{claim.expenseType || '-'}</b><small>{claim.travelRequestNumber ? `Travel: ${claim.travelRequestNumber}` : 'Standalone'}</small></td><td><b>{claim.purpose}</b><small>{claim.costCenter || 'Employee claim'}</small></td><td><b>{claim.lines.length} line(s)</b><small>Policy checked</small></td><td><b>{formatMoney(claim.totalClaimAmount)}</b><small>GST {formatMoney(claim.totalGstAmount)}</small></td><td><b>{claim.payrollStatus}</b><small>{claim.payrollRunId ? `Run #${claim.payrollRunId}` : claim.reimbursementComponentCode}</small></td><td><span className={`task-status ${statusClass(claim.status)}`}>{claim.status}</span></td><td><div className="travel-row-actions">{adminMaintenance ? <button type="button" className="admin-cleanup" onClick={() => setMaintenanceClaim(claim)}>{claim.status === 'Draft' ? 'Delete' : 'Revert / Delete'}</button> : <>{['Draft', 'Sent Back'].includes(claim.status) && <button type="button" onClick={() => openEdit(claim)}>Edit</button>}{['Draft', 'Sent Back'].includes(claim.status) && <button type="button" onClick={() => void submit(claim)}>Submit</button>}<button type="button" onClick={() => void openTrail(claim)}>Trail</button></>}</div></td></tr>)}{!filtered.length && <tr><td colSpan={8}>No expense claims found.</td></tr>}</tbody></table></div></section>
    {trailClaim && <ExpenseTrailModal claim={trailClaim} trail={trail} onClose={() => { setTrailClaim(null); setTrail(null) }} />}
    {maintenanceClaim && <AdminRecordMaintenanceModal open recordType="Expense claim" recordLabel={maintenanceClaim.claimNumber || `Claim #${maintenanceClaim.id}`} status={maintenanceClaim.status} onClose={() => setMaintenanceClaim(null)} onConfirm={maintain} />}
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
    setForm(current => ({ ...current, travelRequestId: id || undefined, purpose: trip?.purpose || current.purpose, customer: '', project: '', costCenter: trip?.costCenter || current.costCenter }))
  }
  const addLine = () => setForm(current => ({ ...current, lines: [...current.lines, { ...emptyLine(options, current.expenseType), categoryId: visibleCategories[0]?.id ?? 0, categoryCode: visibleCategories[0]?.categoryCode ?? '', categoryName: visibleCategories[0]?.categoryName ?? '', expenseDate: minExpenseDate || today }] }))
  const removeLine = (index: number) => setForm(current => ({ ...current, lines: current.lines.length <= 1 ? current.lines : current.lines.filter((_, lineIndex) => lineIndex !== index) }))
  const total = form.lines.reduce((sum, line) => sum + Number(line.amount || 0), 0)

  return <section className="travel-editor-page expense-editor-page">
    <form className="travel-full-form" onSubmit={onSave}>
      <section className="travel-form-section"><div className="travel-section-title"><div><h4>Claim details</h4><p>Your client is identified automatically from your employee login.</p></div></div><div className="travel-form-grid"><label><span>Expense type</span><Select aria-label="Expense type" showSearch optionFilterProp="label" placeholder="Select expense type" value={form.expenseType || undefined} onChange={selectExpenseType} options={(options?.headers ?? []).map(item => ({ label: item.categoryName, value: item.expenseType || item.categoryName }))} /></label>{isTravelExpense && <label><span>Linked travel request</span><Select aria-label="Linked travel request" showSearch optionFilterProp="label" placeholder="Select travel request" value={form.travelRequestId || undefined} onChange={value => selectTravel(Number(value || 0))} options={(options?.travelRequests ?? []).map(item => ({ label: `${item.requestNumber} - ${item.purpose}`, value: item.id }))} /></label>}<label className="wide"><span>Purpose</span><input required value={form.purpose} onChange={event => set('purpose', event.target.value)} /></label><label><span>Cost center</span><input value={form.costCenter} onChange={event => set('costCenter', event.target.value)} /></label><label><span>Currency</span><Select aria-label="Claim currency" showSearch optionFilterProp="label" value={form.currency} onChange={value => set('currency', value)} options={(options?.currencies ?? []).map(item => ({ label: item, value: item }))} /></label></div>{linkedTrip && <p className="travel-warning compact">Claim dates allowed from {dateText(linkedTrip.startDateTime)} to {dateText(linkedTrip.endDateTime)}.</p>}</section>
      <ExpenseLinesEditor lines={form.lines} categories={visibleCategories} locations={options?.locations ?? []} minDate={minExpenseDate} maxDate={maxExpenseDate} total={total} onAdd={addLine} onRemove={removeLine} onUpdate={updateLine} onSelectCategory={selectCategory} />
      <section className="travel-form-section"><h4>Remarks</h4><textarea value={form.remarks} onChange={event => set('remarks', event.target.value)} /></section>
      <footer className="travel-editor-actions"><button type="button" className="secondary" onClick={onBack}>Cancel</button><button disabled={!visibleCategories.length}>Save draft</button></footer>
    </form>
  </section>
}

function ExpenseLinesEditor({ lines, categories, locations, minDate, maxDate, total, onAdd, onRemove, onUpdate, onSelectCategory }: {
  lines: ExpenseLine[]
  categories: ExpenseCategoryOption[]
  locations: string[]
  minDate: string
  maxDate: string
  total: number
  onAdd: () => void
  onRemove: (index: number) => void
  onUpdate: (index: number, patch: Partial<ExpenseLine>) => void
  onSelectCategory: (index: number, id: number) => void
}) {
  return <section className="travel-form-section">
    <div className="travel-section-title compact"><div><h4>Expense lines</h4><small>Eligible reimbursement is calculated from the assigned travel policy.</small></div><Button type="primary" disabled={!categories.length} onClick={onAdd}>Add line</Button></div>
    <div className="expense-line-editor-list">{lines.map((line, index) => {
      const category = categories.find(item => item.id === Number(line.categoryId))
      const kind = expenseEntitlementKind(line.categoryCode, line.categoryName)
      return <article className="expense-line-editor-card" data-testid={`expense-line-${index}`} key={line.id ?? index}>
        <header className="expense-line-editor-head"><div><i>{String(index + 1).padStart(2, '0')}</i><div><b>{line.categoryName || `Expense item ${index + 1}`}</b><span>{category?.receiptMandatory ? 'Receipt required' : 'Receipt optional'}{category?.maximumClaim ? ` · Maximum ${formatMoney(category.maximumClaim)}` : ''}</span></div></div><Button danger disabled={lines.length <= 1} onClick={() => onRemove(index)}>Remove</Button></header>
        <div className="expense-line-fields">
          <label><span>Date</span><input type="date" min={minDate} max={maxDate} value={dateInput(line.expenseDate)} onChange={event => onUpdate(index, { expenseDate: event.target.value })} /></label>
          <label className="wide"><span>Category</span><Select aria-label={`Expense category ${index + 1}`} showSearch optionFilterProp="label" placeholder="Select category" value={line.categoryId || undefined} onChange={value => onSelectCategory(index, Number(value || 0))} options={categories.map(item => ({ label: item.categoryName, value: item.id }))} /></label>
          <label className="wide"><span>Vendor</span><input aria-label={`Vendor ${index + 1}`} value={line.vendorName} onChange={event => onUpdate(index, { vendorName: event.target.value })} /></label>
          <label><span>Bill number</span><input value={line.billNumber} onChange={event => onUpdate(index, { billNumber: event.target.value })} placeholder="Bill no." /></label>
          <label><span>Invoice number</span><input value={line.invoiceNumber} onChange={event => onUpdate(index, { invoiceNumber: event.target.value })} placeholder="Invoice no." /></label>
          <label><span>Claim amount</span><input aria-label={`Claim amount ${index + 1}`} type="number" min={0} value={line.amount} onChange={event => onUpdate(index, { amount: Number(event.target.value || 0) })} /></label>
          <label><span>GST amount</span><input type="number" min={0} value={line.gstAmount} onChange={event => onUpdate(index, { gstAmount: Number(event.target.value || 0) })} disabled={!category?.gstApplicable} /></label>
          <label><span>Location</span><Select aria-label={`Expense location ${index + 1}`} showSearch allowClear optionFilterProp="label" placeholder="Select location" value={line.location || undefined} onChange={value => onUpdate(index, { location: value ?? '' })} options={locations.map(item => ({ label: item, value: item }))} /></label>
          <div className="expense-receipt-field"><span>Receipt</span><Checkbox aria-label={`Receipt attached ${index + 1}`} checked={line.receiptAttached} onChange={event => onUpdate(index, { receiptAttached: event.target.checked })}>Attached</Checkbox><input value={line.receiptFileName} onChange={event => onUpdate(index, { receiptFileName: event.target.value, receiptAttached: Boolean(event.target.value) || line.receiptAttached })} placeholder="File name" /></div>
          <label className="description"><span>Description</span><textarea value={line.description} onChange={event => onUpdate(index, { description: event.target.value })} /></label>
        </div>
        {kind !== 'actual' && <div className="expense-entitlement-card" data-testid={`expense-entitlement-${index}`}>
          <div className="expense-entitlement-title"><b>Policy entitlement</b><span>{kind === 'halting' ? 'Halting allowance' : kind === 'lodging' ? 'Lodging limit' : 'Local conveyance'}</span></div>
          <div className="expense-entitlement-fields">
            {(kind === 'halting' || kind === 'lodging') && <label><span>City class</span><Select aria-label={`City class ${index + 1}`} allowClear placeholder="Auto from location" value={line.cityCategory || undefined} onChange={value => onUpdate(index, { cityCategory: value ?? '' })} options={[{ label: 'Metro', value: 'Metro' }, { label: 'Non-Metro', value: 'Non-Metro' }]} /></label>}
            {(kind === 'halting' || (kind === 'local' && !line.receiptAttached)) && <label><span>Distance (km)</span><input aria-label={`Distance km ${index + 1}`} type="number" min={0} step="0.1" value={line.distanceKm || 0} onChange={event => onUpdate(index, { distanceKm: Number(event.target.value || 0) })} /></label>}
            {kind === 'halting' && <label><span>Duty hours</span><input aria-label={`Duty hours ${index + 1}`} type="number" min={0} step="0.25" value={line.dutyHours || 0} onChange={event => onUpdate(index, { dutyHours: Number(event.target.value || 0) })} /></label>}
          </div>
          {kind === 'halting' && <div className="expense-entitlement-checks"><Checkbox checked={Boolean(line.lodgingClaimed)} onChange={event => onUpdate(index, { lodgingClaimed: event.target.checked, lodgingIncludesFood: event.target.checked ? line.lodgingIncludesFood : false })}>Lodging claimed</Checkbox><Checkbox checked={Boolean(line.lodgingIncludesFood)} onChange={event => onUpdate(index, { lodgingIncludesFood: event.target.checked, lodgingClaimed: event.target.checked || line.lodgingClaimed })}>Lodging includes food</Checkbox><Checkbox checked={Boolean(line.alternativeStay)} onChange={event => onUpdate(index, { alternativeStay: event.target.checked })}>Alternative stay</Checkbox><Checkbox checked={Boolean(line.overnightStay)} onChange={event => onUpdate(index, { overnightStay: event.target.checked })}>Overnight stay</Checkbox></div>}
          {(line.entitlementLabel || line.approvedAmount > 0) && <div className="expense-entitlement-result"><span>{line.entitlementLabel || 'Calculated eligibility'}</span><b>{formatMoney(line.approvedAmount)}</b><small>{line.entitlementMessage}</small></div>}
        </div>}
      </article>
    })}</div>
    <div className="expense-total">Total claim amount <b>{formatMoney(total)}</b></div>
  </section>
}

function ExpenseTrailModal({ claim, trail, onClose }: { claim: ExpenseClaim; trail: WorkflowTrail | null; onClose: () => void }) {
  return <div className="ess-modal-backdrop" onClick={onClose}><section className="trail-modal" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow">Approval trail</span><h3>{claim.claimNumber || 'Expense claim'}</h3></div><small className={`trail-status ${statusClass(claim.status)}`}>{claim.status}</small><button type="button" onClick={onClose}>x</button></header>{!trail && <div className="empty-work"><span>Loading trail...</span></div>}{trail && !trail.events.length && <div className="empty-work"><b>No workflow trail found.</b><span>This claim may not have entered workflow.</span></div>}{trail && trail.events.length > 0 && <div className="trail-list">{trail.events.map((event, index) => <article className={event.isPending ? 'pending' : ''} key={`${event.action}-${event.createdAt}-${index}`}><i>{index + 1}</i><div><b>{event.action}</b><span>{event.stageName}</span>{event.comment && <small>{event.comment}</small>}</div><div><b>{event.actor}</b><span>{new Date(event.createdAt).toLocaleString('en-IN')}</span></div></article>)}</div>}</section></div>
}

function normalizeClaim(form: SaveExpenseClaim, options: ExpenseOptions | null): SaveExpenseClaim {
  return {
    ...form,
    expenseType: form.expenseType || expenseTypeFromOptions(options),
    customer: '',
    project: '',
    lines: form.lines.filter(line => line.categoryId || line.amount || line.description || line.vendorName).map(line => ({ ...line, expenseDate: dateInput(line.expenseDate), currency: line.currency || form.currency || 'INR', exchangeRate: Number(line.exchangeRate || 1), amount: Number(line.amount || 0), gstAmount: Number(line.gstAmount || 0), customer: '', project: '', costCenter: line.costCenter || form.costCenter })),
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
  const codes = ['MEALS', 'HALTING_ALLOWANCE', 'PER_DIEM', 'HA']
  const mode = (trip.travelMode || '').toLowerCase()
  if (!mode || mode.includes('air') || mode.includes('flight')) codes.push('AIR_FARE')
  if (!mode || mode.includes('train') || mode.includes('rail')) codes.push('TRAIN_FARE')
  if (!mode || mode.includes('bus')) codes.push('BUS_FARE')
  if (mode.includes('cab') || mode.includes('taxi')) codes.push('CAB_TAXI')
  if (mode.includes('own') || mode.includes('car')) codes.push('FUEL', 'PARKING', 'TOLL')
  if (trip.accommodationRequired) codes.push('HOTEL_STAY', 'LODGING')
  if (trip.localConveyanceRequired) codes.push('CAB_TAXI', 'FUEL', 'PARKING', 'TOLL', 'METRO', 'LOCAL_CONVEYANCE', 'MILEAGE')
  return Array.from(new Set(codes))
}

function expenseEntitlementKind(code = '', name = ''): 'halting' | 'lodging' | 'local' | 'actual' {
  const identity = `${code} ${name}`.toUpperCase()
  if (identity.includes('HALTING') || identity.includes('PER DIEM') || identity.includes('PER_DIEM') || identity.trim() === 'HA') return 'halting'
  if (identity.includes('HOTEL') || identity.includes('LODGING')) return 'lodging'
  if (['LOCAL', 'CAB', 'TAXI', 'MILEAGE', 'FUEL', 'METRO'].some(token => identity.includes(token))) return 'local'
  return 'actual'
}
