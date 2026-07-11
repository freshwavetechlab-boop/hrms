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
    <div className="feature-heading">
      <span className="eyebrow">My tasks</span>
      <h3>Approvals assigned to you</h3>
      <p>Review pending approvals and revisit requests where you already took action.</p>
    </div>
    <div className="task-tabs">
      <button type="button" className={view === 'pending' ? 'active' : ''} onClick={() => { setView('pending'); setSelected(null); setMessage('') }}>Pending</button>
      <button type="button" className={view === 'actioned' ? 'active' : ''} onClick={() => { setView('actioned'); setSelected(null); setMessage('') }}>Actioned by me</button>
    </div>
    {message && <p className={message.includes('Unable') ? 'task-message error' : 'task-message'}>{message}</p>}
    <div className="task-table-card">
      <div className="task-table-scroll">
        <table className="task-table">
          <thead><tr><th>Resource</th><th>Reference</th><th>Stage</th><th>Received</th>{view === 'actioned' && <th>Actioned on</th>}<th>Status</th><th>Action</th></tr></thead>
          <tbody>
            {rows.map(task => <tr key={task.id}>
              <td>{resourceName(task.resourceType)}</td>
              <td><b>{referenceText(task)}</b><small>Task #{task.id}</small></td>
              <td>{task.stageName}</td>
              <td>{formatDate(task.createdAt)}</td>
              {view === 'actioned' && <td>{formatDate(task.actionedAt || '')}</td>}
              <td><span className={`task-status ${statusTone(task.status || (view === 'pending' ? 'Pending' : ''))}`}>{task.status || (view === 'pending' ? 'Pending' : '-')}</span></td>
              <td><button type="button" onClick={() => { setSelected(task); setRemark(''); setMessage('') }}>{view === 'pending' ? 'Process' : 'View'}</button></td>
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
      <p>{[leaveType && `${leaveType}${leaveCode ? ` (${leaveCode})` : ''}`, dateRange, days && `${days} day${days === '1' ? '' : 's'}`, actionedOn].filter(Boolean).join(' / ')}</p>
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
  const reason = detailValue(rows, 'Reason')
  const visibleRows = rows.filter(([label]) => !['Id', 'Reference', 'Resource', 'Reason', 'Status', 'From Date', 'To Date', 'Leave Code', 'Leave Type', 'Days', 'Created At'].includes(label))
  return <div className="task-detail-view">
    <div className="task-summary-band">
      <article><span>Stage</span><b>{task.stageName}</b></article>
      <article><span>Received</span><b>{formatDate(task.createdAt)}</b></article>
      <article><span>Date range</span><b>{rangeText(detailValue(rows, 'From Date'), detailValue(rows, 'To Date')) || '-'}</b></article>
    </div>
    {reason && <section className="task-reason-block"><span>Reason</span><p>{reason}</p></section>}
    {task.comment && <section className="task-reason-block"><span>Your remarks</span><p>{task.comment}</p></section>}
    <section className="task-detail-table">
      {visibleRows.map(([label, item]) => <div key={label}><span>{label}</span><b>{item}</b></div>)}
      {!visibleRows.length && !reason && <div><span>Details</span><b>No request details were recorded with this task.</b></div>}
    </section>
  </div>
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
