import { useEffect, useMemo, useState } from 'react'
import type { Task, User } from '../types'
import { essApi } from '../services/essApi'

type TaskAction = 'Approved' | 'Sent Back' | 'Rejected'
type TaskView = 'pending' | 'actioned'

export function TasksPage({ user }: { user: User }) {
  const [rows, setRows] = useState<Task[]>([])
  const [view, setView] = useState<TaskView>('pending')
  const [selected, setSelected] = useState<Task | null>(null)
  const [remark, setRemark] = useState('')
  const [busy, setBusy] = useState('')
  const [message, setMessage] = useState('')

  const load = (nextView = view) => essApi.tasks(nextView).then(items => {
    setRows(items)
    setSelected(current => current ? items.find(item => item.id === current.id) ?? null : null)
  })

  useEffect(() => { void load(view) }, [user.email, view])
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Approvals', title: view === 'pending' ? 'Pending approval tasks' : 'Actioned tasks' } }))
  }, [view])

  const detailRows = useMemo(() => selected ? taskDetails(selected) : [], [selected])

  const takeAction = async (action: TaskAction) => {
    if (!selected) return
    setBusy(action)
    setMessage('')
    try {
      await essApi.taskAction(selected.id, action, remark.trim())
      setMessage(`Task ${action.toLowerCase()}.`)
      setRemark('')
      setSelected(null)
      await load()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Unable to update task.')
    } finally {
      setBusy('')
    }
  }

  return <section className="leave-workspace task-workspace">
    <div className="task-tabs">
      <button type="button" className={view === 'pending' ? 'active' : ''} onClick={() => { setView('pending'); setSelected(null); setMessage('') }}>Pending</button>
      <button type="button" className={view === 'actioned' ? 'active' : ''} onClick={() => { setView('actioned'); setSelected(null); setMessage('') }}>Actioned by me</button>
    </div>
    {message && <p className={message.includes('Unable') ? 'task-message error' : 'task-message'}>{message}</p>}
    <div className="task-table-card">
      <div className="task-table-scroll">
        <table className="task-table mobile-card-table">
          <thead><tr><th>Resource</th><th>Reference</th><th>Stage</th><th>Received</th>{view === 'actioned' && <th>Actioned on</th>}<th>Status</th><th>Action</th></tr></thead>
          <tbody>
            {rows.map(task => <tr key={task.id}>
              <td data-label="Resource">{resourceName(task.resourceType)}</td>
              <td data-label="Reference"><div><b>{referenceText(task)}</b><small>Task #{task.id}</small></div></td>
              <td data-label="Stage">{task.stageName}</td>
              <td data-label="Received">{formatDate(task.createdAt)}</td>
              {view === 'actioned' && <td data-label="Actioned on">{formatDate(task.actionedAt || '')}</td>}
              <td data-label="Status"><span className={`task-status ${statusTone(task.status || (view === 'pending' ? 'Pending' : ''))}`}>{task.status || (view === 'pending' ? 'Pending' : '-')}</span></td>
              <td data-label="Action"><button type="button" onClick={() => { setSelected(task); setRemark(''); setMessage('') }}>{view === 'pending' ? 'Process' : 'View'}</button></td>
            </tr>)}
          </tbody>
        </table>
        {!rows.length && <div className="empty-work"><b>{view === 'pending' ? 'No approval tasks are assigned to you.' : 'No actioned tasks found.'}</b><span>{view === 'pending' ? 'Tasks from Leave, Payroll, and other modules will appear here when assigned.' : 'Approved, rejected, and sent-back tasks will appear here after you act on them.'}</span></div>}
      </div>
    </div>
    {selected && <div className="task-modal-backdrop" onClick={() => setSelected(null)}>
      <section className="task-review task-modal" role="dialog" aria-modal="true" aria-label="Process workflow task" onClick={event => event.stopPropagation()}>
        <TaskModalHeader task={selected} rows={detailRows} onClose={() => setSelected(null)} />
        <TaskDetailView task={selected} rows={detailRows} />
        {view === 'pending' ? <>
          <label className="task-remarks"><span>Remarks</span><textarea value={remark} onChange={event => setRemark(event.target.value)} placeholder="Add approval, rejection, or send-back remarks..." /></label>
          <div className="task-actions">
            <button type="button" disabled={!!busy} onClick={() => void takeAction('Approved')}>{busy === 'Approved' ? 'Approving...' : 'Approve'}</button>
            <button type="button" className="secondary" disabled={!!busy} onClick={() => void takeAction('Sent Back')}>{busy === 'Sent Back' ? 'Sending...' : 'Send back'}</button>
            <button type="button" className="danger" disabled={!!busy} onClick={() => void takeAction('Rejected')}>{busy === 'Rejected' ? 'Rejecting...' : 'Reject'}</button>
          </div>
        </> : <div className="task-actions"><button type="button" className="secondary" onClick={() => setSelected(null)}>Close</button></div>}
      </section>
    </div>}
  </section>
}

function TaskModalHeader({ task, rows, onClose }: { task: Task; rows: [string, string][]; onClose: () => void }) {
  const expense = expenseApproval(task)
  const travel = travelApproval(task)
  const get = (name: string) => detailValue(rows, name)
  const leaveType = get('Leave Type')
  const leaveCode = get('Leave Code')
  const dateRange = rangeText(get('From Date'), get('To Date'))
  const days = get('Days')
  const status = get('Status') || 'Pending'
  const actionedOn = task.actionedAt ? `Actioned ${formatDate(task.actionedAt)}` : ''
  return <header className="task-modal-head">
    <div>
      <span className="eyebrow">{task.actionedAt ? 'Task history' : 'Process task'}</span>
      <h4>{resourceName(task.resourceType)} approval</h4>
      <p>{expense
        ? [expense.claimNumber, expense.employeeName, expense.expenseType, actionedOn].filter(Boolean).join(' / ')
        : travel
          ? [travel.requestNumber, travel.employeeName, travel.priority, actionedOn].filter(Boolean).join(' / ')
        : [leaveType && `${leaveType}${leaveCode ? ` (${leaveCode})` : ''}`, dateRange, days && `${days} day${days === '1' ? '' : 's'}`, actionedOn].filter(Boolean).join(' / ')}</p>
    </div>
    <div className="task-modal-head-actions"><span className={`task-status-chip ${statusTone(status)}`}>{status}</span><button type="button" onClick={onClose}>Close</button></div>
  </header>
}

function resourceName(value: string) {
  if (value === 'PayRun') return 'Payroll'
  if (value === 'LeaveRequest') return 'Leave'
  return value.replace(/([A-Z])/g, ' $1').trim()
}

function TaskDetailView({ task, rows }: { task: Task; rows: [string, string][] }) {
  const expense = expenseApproval(task)
  const travel = travelApproval(task)
  const reason = detailValue(rows, 'Reason')
  const visibleRows = rows.filter(([label]) => !['Id', 'Reference', 'Resource', 'Reason', 'Status', 'From Date', 'To Date', 'Leave Code', 'Leave Type', 'Days', 'Created At', 'Lines', 'Legs', 'Cities', 'Accommodation Details', 'Local Travel Details', 'Policy Validation Json'].includes(label))
  return <div className="task-detail-view">
    <div className="task-summary-band">
      <article><span>Stage</span><b>{task.stageName}</b></article>
      <article><span>Received</span><b>{formatDate(task.createdAt)}</b></article>
      <article><span>Date range</span><b>{rangeText(detailValue(rows, 'From Date'), detailValue(rows, 'To Date')) || '-'}</b></article>
    </div>
    {reason && <section className="task-reason-block"><span>Reason</span><p>{reason}</p></section>}
    {task.comment && <section className="task-reason-block"><span>Your remarks</span><p>{task.comment}</p></section>}
    {expense && <ExpenseApprovalReview expense={expense} />}
    {travel && <TravelApprovalReview travel={travel} />}
    {!expense && !travel && <section className="task-detail-table">
      {visibleRows.map(([label, item]) => <div key={label}><span>{label}</span><b>{item}</b></div>)}
      {!visibleRows.length && !reason && <div><span>Details</span><b>No request details were recorded with this task.</b></div>}
    </section>}
  </div>
}

type TravelApprovalLeg = { fromLocation: string; toLocation: string; startDateTime: string; endDateTime: string; travelMode: string; travelClass: string; bookingAction: string; remarks: string }
type TravelApprovalStay = { city: string; checkInDateTime: string; checkOutDateTime: string; occupancy: string; roomPreference: string; bookingAction: string; remarks: string }
type TravelApprovalRide = { city: string; travelDateTime: string; fromLocation: string; toLocation: string; travelMode: string; bookingAction: string; remarks: string }
type TravelApproval = {
  requestNumber: string
  employeeName: string
  department: string
  designation: string
  purpose: string
  customer: string
  project: string
  costCenter: string
  travelType: string
  priority: string
  estimatedCost: number
  policyName: string
  remarks: string
  policyMessages: string[]
  legs: TravelApprovalLeg[]
  accommodationRequired: boolean
  localConveyanceRequired: boolean
  accommodationDetails: TravelApprovalStay[]
  localTravelDetails: TravelApprovalRide[]
}

function TravelApprovalReview({ travel }: { travel: TravelApproval }) {
  return <section className="expense-approval-review travel-approval-review" data-testid="travel-approval-review">
    <div className="expense-approval-context">
      <article><span>Employee</span><b>{travel.employeeName || '-'}</b><small>{[travel.department, travel.designation].filter(Boolean).join(' / ')}</small></article>
      <article><span>Purpose</span><b>{travel.purpose || '-'}</b><small>{[travel.customer, travel.project, travel.costCenter].filter(Boolean).join(' / ')}</small></article>
      <article><span>Policy</span><b>{travel.policyName || 'Applicable client policy'}</b><small>{[travel.travelType, travel.priority].filter(Boolean).join(' / ')}</small></article>
    </div>
    {travel.estimatedCost > 0 && <div className="expense-approval-kpis single"><article><span>Estimated trip cost</span><b>{money(travel.estimatedCost)}</b></article></div>}
    {!!travel.policyMessages.length && <div className="expense-policy-notes"><b>Policy review</b>{travel.policyMessages.map((message, index) => <p key={`${message}-${index}`}>{message}</p>)}</div>}
    <div className="expense-approval-lines" data-testid="travel-approval-legs">
      <div className="expense-approval-lines-head"><b>Travel itinerary</b><span>{travel.legs.length} leg{travel.legs.length === 1 ? '' : 's'}</span></div>
      {travel.legs.map((leg, index) => <article className="expense-approval-line" key={`${leg.fromLocation}-${leg.toLocation}-${index}`}>
        <div className="expense-line-title"><div><b>{leg.fromLocation || '-'} to {leg.toLocation || '-'}</b><span>{formatDate(leg.startDateTime)} - {formatDate(leg.endDateTime)}</span></div><span className="expense-policy-pill">{[leg.travelMode, leg.travelClass].filter(Boolean).join(' / ') || 'Travel leg'}</span></div>
        {leg.bookingAction && <div className="expense-policy-chips"><span>Travel Desk: {leg.bookingAction}</span></div>}
        {leg.remarks && <p className="expense-line-description">{leg.remarks}</p>}
      </article>)}
    </div>
    {(travel.accommodationRequired || travel.accommodationDetails.length > 0) && <div className="expense-approval-lines" data-testid="travel-approval-accommodation">
      <div className="expense-approval-lines-head"><b>Accommodation</b><span>{travel.accommodationDetails.length} stay{travel.accommodationDetails.length === 1 ? '' : 's'}</span></div>
      {travel.accommodationDetails.map((stay, index) => <article className="expense-approval-line" key={`${stay.city}-${stay.checkInDateTime}-${index}`}>
        <div className="expense-line-title"><div><b>{stay.city || 'Stay location'}</b><span>{formatDate(stay.checkInDateTime)} - {formatDate(stay.checkOutDateTime)}</span></div><span className="expense-policy-pill">{stay.occupancy || 'Accommodation'}</span></div>
        <div className="expense-policy-chips"><span>Travel Desk: {stay.bookingAction || 'Book by myself'}</span>{stay.roomPreference && <span>{stay.roomPreference}</span>}</div>
        {stay.remarks && <p className="expense-line-description">{stay.remarks}</p>}
      </article>)}
      {!travel.accommodationDetails.length && <article className="expense-approval-line"><p className="expense-line-description">Accommodation is required; no stay rows were provided.</p></article>}
    </div>}
    {(travel.localConveyanceRequired || travel.localTravelDetails.length > 0) && <div className="expense-approval-lines" data-testid="travel-approval-local-travel">
      <div className="expense-approval-lines-head"><b>Local travel</b><span>{travel.localTravelDetails.length} ride{travel.localTravelDetails.length === 1 ? '' : 's'}</span></div>
      {travel.localTravelDetails.map((ride, index) => <article className="expense-approval-line" key={`${ride.city}-${ride.travelDateTime}-${index}`}>
        <div className="expense-line-title"><div><b>{ride.fromLocation || ride.city || '-'} to {ride.toLocation || '-'}</b><span>{formatDate(ride.travelDateTime)}{ride.city ? ` / ${ride.city}` : ''}</span></div><span className="expense-policy-pill">{ride.travelMode || 'Local travel'}</span></div>
        {ride.bookingAction && <div className="expense-policy-chips"><span>Travel Desk: {ride.bookingAction}</span></div>}
        {ride.remarks && <p className="expense-line-description">{ride.remarks}</p>}
      </article>)}
      {!travel.localTravelDetails.length && <article className="expense-approval-line"><p className="expense-line-description">Local travel is required; no ride rows were provided.</p></article>}
    </div>}
    {travel.remarks && <section className="task-reason-block"><span>Employee remarks</span><p>{travel.remarks}</p></section>}
  </section>
}

function travelApproval(task: Task): TravelApproval | null {
  if (task.resourceType.toLowerCase() !== 'travelrequest') return null
  let raw: Record<string, unknown>
  try { raw = JSON.parse(task.payloadJson || '{}') as Record<string, unknown> } catch { return null }
  const legs = asArray(read(raw, 'legs')).length ? asArray(read(raw, 'legs')) : asArray(read(raw, 'cities'))
  const accommodationDetails = asArray(read(raw, 'accommodationDetails'))
  const localTravelDetails = asArray(read(raw, 'localTravelDetails'))
  const policyMessages = asArray(read(raw, 'policyValidationJson')).map(item => typeof item === 'string' ? item : String(read(asObject(item), 'message') || '')).filter(Boolean)
  return {
    requestNumber: text(read(raw, 'requestNumber')),
    employeeName: text(read(raw, 'employeeName')),
    department: text(read(raw, 'department')),
    designation: text(read(raw, 'designation')),
    purpose: text(read(raw, 'purpose')),
    customer: text(read(raw, 'customer')),
    project: text(read(raw, 'project')),
    costCenter: text(read(raw, 'costCenter')),
    travelType: text(read(raw, 'travelType')),
    priority: text(read(raw, 'priority')),
    estimatedCost: numeric(read(raw, 'estimatedCost')),
    policyName: text(read(raw, 'policyName')),
    remarks: text(read(raw, 'remarks')),
    policyMessages,
    legs: legs.map(item => {
      const leg = asObject(item)
      return {
        fromLocation: text(read(leg, 'fromLocation')),
        toLocation: text(read(leg, 'toLocation')),
        startDateTime: text(read(leg, 'startDateTime')),
        endDateTime: text(read(leg, 'endDateTime')),
        travelMode: text(read(leg, 'travelMode')),
        travelClass: text(read(leg, 'travelClass')),
        bookingAction: text(read(leg, 'bookingAction')),
        remarks: text(read(leg, 'remarks')),
      }
    }),
    accommodationRequired: Boolean(read(raw, 'accommodationRequired')),
    localConveyanceRequired: Boolean(read(raw, 'localConveyanceRequired')),
    accommodationDetails: accommodationDetails.map(item => {
      const stay = asObject(item)
      return {
        city: text(read(stay, 'city')),
        checkInDateTime: text(read(stay, 'checkInDateTime')),
        checkOutDateTime: text(read(stay, 'checkOutDateTime')),
        occupancy: text(read(stay, 'occupancy')),
        roomPreference: text(read(stay, 'roomPreference')),
        bookingAction: text(read(stay, 'bookingAction')),
        remarks: text(read(stay, 'remarks')),
      }
    }),
    localTravelDetails: localTravelDetails.map(item => {
      const ride = asObject(item)
      return {
        city: text(read(ride, 'city')),
        travelDateTime: text(read(ride, 'travelDateTime')),
        fromLocation: text(read(ride, 'fromLocation')),
        toLocation: text(read(ride, 'toLocation')),
        travelMode: text(read(ride, 'travelMode')),
        bookingAction: text(read(ride, 'bookingAction')),
        remarks: text(read(ride, 'remarks')),
      }
    }),
  }
}

type ExpenseApprovalLine = {
  id: number
  expenseDate: string
  categoryName: string
  vendorName: string
  location: string
  amount: number
  approvedAmount: number
  currency: string
  receiptAttached: boolean
  receiptFileName: string
  description: string
  cityCategory: string
  distanceKm: number
  dutyHours: number
  lodgingClaimed: boolean
  lodgingIncludesFood: boolean
  alternativeStay: boolean
  overnightStay: boolean
  entitlementLabel: string
  entitlementMessage: string
}

type ExpenseApproval = {
  claimNumber: string
  claimDate: string
  employeeName: string
  department: string
  designation: string
  expenseType: string
  purpose: string
  customer: string
  project: string
  costCenter: string
  currency: string
  totalClaimAmount: number
  totalApprovedAmount: number
  remarks: string
  policyMessages: string[]
  lines: ExpenseApprovalLine[]
}

function ExpenseApprovalReview({ expense }: { expense: ExpenseApproval }) {
  const difference = Math.max(0, expense.totalClaimAmount - expense.totalApprovedAmount)
  return <section className="expense-approval-review" data-testid="expense-approval-review">
    <div className="expense-approval-kpis">
      <article><span>Claimed amount</span><b>{money(expense.totalClaimAmount, expense.currency)}</b></article>
      <article className="eligible"><span>Eligible reimbursement</span><b>{money(expense.totalApprovedAmount, expense.currency)}</b></article>
      <article className={difference > 0 ? 'adjusted' : ''}><span>Policy adjustment</span><b>{money(difference, expense.currency)}</b></article>
    </div>
    <div className="expense-approval-context">
      <article><span>Employee</span><b>{expense.employeeName || '-'}</b><small>{[expense.department, expense.designation].filter(Boolean).join(' / ')}</small></article>
      <article><span>Purpose</span><b>{expense.purpose || '-'}</b><small>{[expense.customer, expense.project, expense.costCenter].filter(Boolean).join(' / ')}</small></article>
      <article><span>Claim</span><b>{expense.claimNumber || '-'}</b><small>{formatDate(expense.claimDate)}</small></article>
    </div>
    {!!expense.policyMessages.length && <div className="expense-policy-notes">
      <b>Policy review</b>
      {expense.policyMessages.map((message, index) => <p key={`${message}-${index}`}>{message}</p>)}
    </div>}
    <div className="expense-approval-lines" data-testid="expense-approval-lines">
      <div className="expense-approval-lines-head"><b>Expense lines</b><span>{expense.lines.length} item{expense.lines.length === 1 ? '' : 's'}</span></div>
      {expense.lines.map((line, index) => <article className="expense-approval-line" key={line.id || index} data-testid={`expense-approval-line-${index}`}>
        <div className="expense-line-title">
          <div><b>{line.categoryName || `Expense ${index + 1}`}</b><span>{formatDate(line.expenseDate)}{line.vendorName ? ` / ${line.vendorName}` : ''}</span></div>
          <span className={line.approvedAmount < line.amount ? 'expense-policy-pill adjusted' : 'expense-policy-pill'}>{line.entitlementLabel || 'Within policy'}</span>
        </div>
        <div className="expense-line-values">
          <span>Claimed <b>{money(line.amount, line.currency || expense.currency)}</b></span>
          <span>Eligible <b>{money(line.approvedAmount, line.currency || expense.currency)}</b></span>
          <span>Receipt <b>{line.receiptAttached ? line.receiptFileName || 'Attached' : 'Not attached'}</b></span>
          {line.location && <span>Location <b>{line.location}</b></span>}
        </div>
        <div className="expense-policy-chips">
          {line.cityCategory && <span>{line.cityCategory}</span>}
          {line.distanceKm > 0 && <span>{number(line.distanceKm)} km</span>}
          {line.dutyHours > 0 && <span>{number(line.dutyHours)} duty hours</span>}
          {line.lodgingClaimed && <span>Lodging claimed</span>}
          {line.lodgingIncludesFood && <span>Lodging includes food</span>}
          {line.alternativeStay && <span>Alternative stay</span>}
          {line.overnightStay && <span>Overnight stay</span>}
        </div>
        {line.entitlementMessage && <p className="expense-entitlement-message">{line.entitlementMessage}</p>}
        {line.description && <p className="expense-line-description">{line.description}</p>}
      </article>)}
    </div>
    {expense.remarks && <section className="task-reason-block"><span>Employee remarks</span><p>{expense.remarks}</p></section>}
  </section>
}

function expenseApproval(task: Task): ExpenseApproval | null {
  if (task.resourceType.toLowerCase() !== 'expenseclaim') return null
  let raw: Record<string, unknown>
  try {
    raw = JSON.parse(task.payloadJson || '{}') as Record<string, unknown>
  } catch {
    return null
  }
  const list = asArray(read(raw, 'lines'))
  const policyMessages = asArray(read(raw, 'policyValidationJson')).map(item => typeof item === 'string' ? item : String(read(asObject(item), 'message') || '')).filter(Boolean)
  return {
    claimNumber: text(read(raw, 'claimNumber')),
    claimDate: text(read(raw, 'claimDate')),
    employeeName: text(read(raw, 'employeeName')),
    department: text(read(raw, 'department')),
    designation: text(read(raw, 'designation')),
    expenseType: text(read(raw, 'expenseType')),
    purpose: text(read(raw, 'purpose')),
    customer: text(read(raw, 'customer')),
    project: text(read(raw, 'project')),
    costCenter: text(read(raw, 'costCenter')),
    currency: text(read(raw, 'currency')) || 'INR',
    totalClaimAmount: numeric(read(raw, 'totalClaimAmount')),
    totalApprovedAmount: numeric(read(raw, 'totalApprovedAmount')),
    remarks: text(read(raw, 'remarks')),
    policyMessages,
    lines: list.map(item => {
      const line = asObject(item)
      return {
        id: numeric(read(line, 'id')),
        expenseDate: text(read(line, 'expenseDate')),
        categoryName: text(read(line, 'categoryName')),
        vendorName: text(read(line, 'vendorName')),
        location: text(read(line, 'location')),
        amount: numeric(read(line, 'amount')),
        approvedAmount: numeric(read(line, 'approvedAmount')),
        currency: text(read(line, 'currency')),
        receiptAttached: Boolean(read(line, 'receiptAttached')),
        receiptFileName: text(read(line, 'receiptFileName')),
        description: text(read(line, 'description')),
        cityCategory: text(read(line, 'cityCategory')),
        distanceKm: numeric(read(line, 'distanceKm')),
        dutyHours: numeric(read(line, 'dutyHours')),
        lodgingClaimed: Boolean(read(line, 'lodgingClaimed')),
        lodgingIncludesFood: Boolean(read(line, 'lodgingIncludesFood')),
        alternativeStay: Boolean(read(line, 'alternativeStay')),
        overnightStay: Boolean(read(line, 'overnightStay')),
        entitlementLabel: text(read(line, 'entitlementLabel')),
        entitlementMessage: text(read(line, 'entitlementMessage')),
      }
    }),
  }
}

function read(source: Record<string, unknown>, key: string) {
  const match = Object.keys(source).find(item => item.toLowerCase() === key.toLowerCase())
  return match ? source[match] : undefined
}

function asObject(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function asArray(value: unknown): unknown[] {
  if (Array.isArray(value)) return value
  if (typeof value !== 'string' || !value.trim()) return []
  try {
    const parsed = JSON.parse(value) as unknown
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

function text(value: unknown) { return value === null || value === undefined ? '' : String(value) }
function numeric(value: unknown) { const parsed = Number(value); return Number.isFinite(parsed) ? parsed : 0 }
function number(value: number) { return value.toLocaleString('en-IN', { maximumFractionDigits: 2 }) }
function money(value: number, currency = 'INR') {
  return new Intl.NumberFormat('en-IN', { style: 'currency', currency: currency || 'INR', maximumFractionDigits: 2 }).format(value)
}

function detailValue(rows: [string, string][], name: string) {
  return rows.find(([label]) => label.toLowerCase() === name.toLowerCase())?.[1] || ''
}

function rangeText(from: string, to: string) {
  if (from && to) return from === to ? from : `${from} - ${to}`
  return from || to
}

function referenceText(task: Task) {
  if (task.resourceType === 'PayRun') return `PayRun #${task.resourceId}`
  if (task.resourceType === 'LeaveRequest') return `Leave request #${task.resourceId}`
  return `${task.resourceType} #${task.resourceId}`
}

function taskDetails(task: Task) {
  const rows: [string, string][] = [['Resource', task.resourceType], ['Reference', task.resourceId]]
  try {
    const data = JSON.parse(task.payloadJson || '{}') as Record<string, unknown>
    Object.entries(data).forEach(([key, value]) => {
      if (value === null || value === undefined || value === '') return
      rows.push([label(key), formatValue(value)])
    })
  } catch {
    if (task.payloadJson) rows.push(['Payload', task.payloadJson])
  }
  return rows
}

function label(value: string) {
  return value.replace(/([A-Z])/g, ' $1').replace(/[_-]+/g, ' ').replace(/^./, char => char.toUpperCase()).trim()
}

function formatValue(value: unknown): string {
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (typeof value === 'number') return value.toLocaleString('en-IN')
  if (typeof value === 'object') return JSON.stringify(value)
  const text = String(value)
  return /^\d{4}-\d{2}-\d{2}/.test(text) ? text.slice(0, 10) : text
}

function formatDate(value: string) {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value || '-'
  const day = String(date.getDate()).padStart(2, '0')
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const year = date.getFullYear()
  const hour = String(date.getHours()).padStart(2, '0')
  const minute = String(date.getMinutes()).padStart(2, '0')
  return `${day}-${month}-${year} ${hour}:${minute}`
}

function statusTone(status: string) {
  const clean = status.toLowerCase()
  if (clean.includes('approved')) return 'approved'
  if (clean.includes('reject')) return 'rejected'
  if (clean.includes('sent')) return 'sent-back'
  return ''
}
