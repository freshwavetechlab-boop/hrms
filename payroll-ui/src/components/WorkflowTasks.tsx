import { useEffect, useMemo, useState } from 'react'
import { getJson, postJson } from '../services/apiClient'
import { getPayRun, getPayRunDiagnostics } from '../services/payrollService'
import type { PayRun, PayRunDiagnostics } from '../types/payroll'
import { PayRunReview } from './PayRunsPanel'

type Task = {
  id: number
  instanceId: number
  stageName: string
  resourceType: string
  resourceId: string
  payloadJson: string
  approverName?: string
  actorName?: string
  status?: string
  comment?: string
  createdAt: string
  actionedAt?: string
}

type TaskView = 'pending' | 'actioned'

const formatTaskDate = (value?: string) => {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value || '-'
  const day = String(date.getDate()).padStart(2, '0')
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const year = date.getFullYear()
  let hour = date.getHours()
  const suffix = hour >= 12 ? 'PM' : 'AM'
  hour = hour % 12 || 12
  const minute = String(date.getMinutes()).padStart(2, '0')
  const second = String(date.getSeconds()).padStart(2, '0')
  return `${day}-${month}-${year} ${String(hour).padStart(2, '0')}:${minute}:${second} ${suffix}`
}

const referenceText = (row: Task) => {
  if (row.resourceType === 'PayRun') return `PayRun #${row.resourceId}`
  if (row.resourceType === 'LeaveRequest') return `Leave request #${row.resourceId}`
  return `${row.resourceType} #${row.resourceId}`
}

const statusClass = (status?: string) => {
  const value = (status || '').toLowerCase()
  if (value.includes('approved')) return 'approved'
  if (value.includes('reject')) return 'rejected'
  if (value.includes('sent')) return 'sent-back'
  return 'pending'
}

const details = (payload: string) => {
  try {
    const value = JSON.parse(payload) as Record<string, unknown>
    return Object.entries(value)
      .filter(([, item]) => item !== null && item !== '')
      .map(([key, item]) => [
        key.replace(/([A-Z])/g, ' $1').replace(/^./, char => char.toUpperCase()),
        typeof item === 'object' ? JSON.stringify(item) : String(item)
      ])
  } catch {
    return []
  }
}

export default function WorkflowTasks() {
  const [rows, setRows] = useState<Task[]>([])
  const [view, setView] = useState<TaskView>('pending')
  const [selected, setSelected] = useState<Task | null>(null)
  const [payRun, setPayRun] = useState<PayRun | null>(null)
  const [diagnostics, setDiagnostics] = useState<PayRunDiagnostics | null>(null)
  const [loadingPayRun, setLoadingPayRun] = useState(false)
  const [remark, setRemark] = useState('')
  const [message, setMessage] = useState('')
  const materialVarianceCount = useMemo(() => payRun?.employees.filter(employee => !employee.isSkipped && (Math.abs(employee.variancePercent || 0) >= 10 || Math.abs(employee.netPayVariance || 0) >= 5000)).length ?? 0, [payRun])

  const load = (nextView = view) =>
    getJson<Task[]>(nextView === 'actioned' ? '/api/workflows/tasks/actioned?scope=all' : '/api/workflows/tasks/pending', []).then(setRows)

  useEffect(() => { void load(view) }, [view])

  useEffect(() => {
    let cancelled = false
    setPayRun(null)
    setDiagnostics(null)
    if (selected?.resourceType !== 'PayRun') return
    const payRunId = Number(selected.resourceId)
    if (!Number.isFinite(payRunId) || payRunId <= 0) return
    setLoadingPayRun(true)
    void Promise.all([getPayRun(payRunId), getPayRunDiagnostics(payRunId)]).then(([run, diagnosticRows]) => {
      if (!cancelled) {
        setPayRun(run)
        setDiagnostics(diagnosticRows)
      }
    }).finally(() => {
      if (!cancelled) setLoadingPayRun(false)
    })
    return () => { cancelled = true }
  }, [selected?.id, selected?.resourceId, selected?.resourceType])

  const action = async (actionName: string) => {
    if (!selected) return
    const response = await postJson(`/api/workflows/tasks/${selected.id}/${actionName}`, { comment: remark.trim() }, null)
    setMessage(response.ok ? `Task ${actionName.toLowerCase()}.` : response.error || 'Unable to update task.')
    if (response.ok) {
      setSelected(null)
      setPayRun(null)
      setDiagnostics(null)
      setRemark('')
      await load()
    }
  }

  const openTask = (task: Task) => {
    setSelected(task)
    setRemark('')
    setMessage('')
  }

  if (selected) {
    const payloadDetails = details(selected.payloadJson)
    return <section className="card workflow-admin workflow-task-detail-page">
      <header className="workflow-detail-head">
        <div>
          <button type="button" className="secondary" onClick={() => setSelected(null)}>Back to tasks</button>
          <span className="eyebrow">{view === 'pending' ? 'Task review' : 'Task history'}</span>
          <h3>{referenceText(selected)}</h3>
          <p>{[selected.stageName, selected.status || (view === 'pending' ? 'Pending' : ''), selected.actorName ? `By ${selected.actorName}` : '', selected.actionedAt ? `Actioned ${formatTaskDate(selected.actionedAt)}` : ''].filter(Boolean).join(' / ')}</p>
        </div>
        <em className={`workflow-task-status ${statusClass(selected.status || (view === 'pending' ? 'Pending' : ''))}`}>{selected.status || (view === 'pending' ? 'Pending' : '-')}</em>
      </header>
      {message && <p className="form-warning">{message}</p>}
      <section className="workflow-task-meta-grid">
        <article><span>Resource</span><b>{selected.resourceType}</b></article>
        <article><span>Reference</span><b>{selected.resourceId}</b></article>
        <article><span>Received</span><b>{formatTaskDate(selected.createdAt)}</b></article>
        <article><span>Approver</span><b>{selected.approverName || '-'}</b></article>
        {view === 'actioned' && <article><span>Action by</span><b>{selected.actorName || selected.approverName || '-'}</b></article>}
        {view === 'actioned' && <article><span>Actioned on</span><b>{formatTaskDate(selected.actionedAt)}</b></article>}
      </section>
      {selected.resourceType === 'PayRun' ? <>
        {loadingPayRun && <p className="form-warning">Loading payroll review...</p>}
        {payRun ? <PayRunReview selected={payRun} diagnostics={diagnostics} busy={false} materialVarianceCount={materialVarianceCount} actions={false} /> : !loadingPayRun && <p className="empty">Payroll run data is not available.</p>}
        {selected.comment && <section className="workflow-task-remarks"><span>Remarks</span><p>{selected.comment}</p></section>}
        {view === 'pending' && <ApprovalControls remark={remark} setRemark={setRemark} action={action} />}
      </> : <>
        <section className="workflow-task-detail-table">
          {payloadDetails.map(([key, value]) => <div key={key}><span>{key}</span><b>{value}</b></div>)}
          {selected.actorName && <div><span>Action by</span><b>{selected.actorName}</b></div>}
          {selected.comment && <div><span>Remarks</span><b>{selected.comment}</b></div>}
          {!payloadDetails.length && !selected.comment && <p className="empty">No additional request details were recorded.</p>}
        </section>
        {view === 'pending' && <ApprovalControls remark={remark} setRemark={setRemark} action={action} />}
      </>}
    </section>
  }

  return <section className="card workflow-admin">
    <header className="workflow-task-head"><div><h3>My Tasks</h3><p>Review pending approvals and revisit completed workflow actions.</p></div></header>
    <nav className="workflow-task-tabs" aria-label="Task status tabs">
      <button type="button" className={view === 'pending' ? 'active' : ''} onClick={() => { setView('pending'); setSelected(null); setMessage('') }}>Pending <b>{view === 'pending' ? rows.length : ''}</b></button>
      <button type="button" className={view === 'actioned' ? 'active' : ''} onClick={() => { setView('actioned'); setSelected(null); setMessage('') }}>Actioned <b>{view === 'actioned' ? rows.length : ''}</b></button>
    </nav>
    {message && <p className="form-warning">{message}</p>}
    <div className="workflow-task-table-card">
      <table className="workflow-task-table">
        <thead><tr><th>Resource</th><th>Reference</th><th>Stage</th><th>Received</th>{view === 'actioned' && <th>Actioned on</th>}<th>{view === 'actioned' ? 'Action by' : 'Status'}</th><th>Action</th></tr></thead>
        <tbody>
          {rows.map(row => <tr key={row.id}>
            <td><b>{row.resourceType}</b></td>
            <td><b>{referenceText(row)}</b><small>Task #{row.id}</small></td>
            <td>{row.stageName}</td>
            <td>{formatTaskDate(row.createdAt)}</td>
            {view === 'actioned' && <td>{formatTaskDate(row.actionedAt)}</td>}
            <td>{view === 'actioned' && <small>{row.actorName || row.approverName || '-'}</small>}<em className={`workflow-task-status ${statusClass(row.status || (view === 'pending' ? 'Pending' : ''))}`}>{row.status || (view === 'pending' ? 'Pending' : '-')}</em></td>
            <td><button type="button" onClick={() => openTask(row)}>{view === 'pending' ? 'Review' : 'View'}</button></td>
          </tr>)}
        </tbody>
      </table>
      {!rows.length && <div className="workflow-task-empty"><b>{view === 'pending' ? 'No pending tasks.' : 'No actioned tasks found.'}</b><span>{view === 'pending' ? 'New approvals assigned to you will appear here.' : 'Approved, rejected, and sent-back requests will appear here once workflow action is recorded.'}</span></div>}
    </div>
  </section>
}

function ApprovalControls({ remark, setRemark, action }: { remark: string; setRemark: (value: string) => void; action: (actionName: string) => Promise<void> }) {
  return <>
    <label className="workflow-task-approval-remarks"><span>Remarks</span><textarea value={remark} onChange={event => setRemark(event.target.value)} placeholder="Add approval, rejection, or send-back remarks..." /></label>
    <div className="workflow-review-actions">
      <button type="button" onClick={() => void action('Approved')}>Approve</button>
      <button type="button" className="secondary" onClick={() => void action('Sent Back')}>Send back</button>
      <button type="button" className="danger" onClick={() => void action('Rejected')}>Reject</button>
    </div>
  </>
}
