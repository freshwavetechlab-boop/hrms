import { useEffect, useMemo, useState } from 'react'
import { getJson, postJson } from '../services/apiClient'
import { getPayRun, getPayRunDiagnostics } from '../services/payrollService'
import type { PayRun, PayRunDiagnostics } from '../types/payroll'
import DataTable from './DataTable'
import { PayRunReview } from './PayRunsPanel'

type Task = { id: number; instanceId: number; stageName: string; resourceType: string; resourceId: string; payloadJson: string; createdAt: string }

const details = (payload: string) => {
  try {
    const value = JSON.parse(payload) as Record<string, unknown>
    return Object.entries(value)
      .filter(([, item]) => item !== null && item !== '')
      .map(([key, item]) => [key.replace(/([A-Z])/g, ' $1').replace(/^./, char => char.toUpperCase()), typeof item === 'object' ? JSON.stringify(item) : String(item)])
  } catch {
    return []
  }
}

export default function WorkflowTasks() {
  const [rows, setRows] = useState<Task[]>([])
  const [selected, setSelected] = useState<Task | null>(null)
  const [payRun, setPayRun] = useState<PayRun | null>(null)
  const [diagnostics, setDiagnostics] = useState<PayRunDiagnostics | null>(null)
  const [loadingPayRun, setLoadingPayRun] = useState(false)
  const [remark, setRemark] = useState('')
  const [message, setMessage] = useState('')
  const load = () => getJson<Task[]>('/api/workflows/tasks/pending', []).then(setRows)
  const materialVarianceCount = useMemo(() => payRun?.employees.filter(employee => !employee.isSkipped && (Math.abs(employee.variancePercent || 0) >= 10 || Math.abs(employee.netPayVariance || 0) >= 5000)).length ?? 0, [payRun])

  useEffect(() => { void load() }, [])
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
      load()
    }
  }
  const approvalControls = <><label><span>Remarks</span><textarea value={remark} onChange={event => setRemark(event.target.value)} placeholder="Add approval, rejection, or send-back remarks..." /></label><div className="workflow-review-actions"><button type="button" onClick={() => void action('Approved')}>Approve</button><button type="button" className="secondary" onClick={() => void action('Sent Back')}>Send back</button><button type="button" className="danger" onClick={() => void action('Rejected')}>Reject</button></div></>

  return (
    <section className="card workflow-admin">
      <header><div><h3>My Tasks</h3><p>Review the request details, add remarks, then take an approval action.</p></div></header>
      {message && <p className="form-warning">{message}</p>}
      <DataTable
        rows={rows}
        emptyText="No approval tasks are assigned to you."
        exportFileName="workflow-tasks"
        columns={[
          { key: 'resourceType', label: 'Resource' },
          { key: 'resourceId', label: 'Reference' },
          { key: 'stageName', label: 'Stage' },
          { key: 'createdAtText', label: 'Received', value: row => new Date(row.createdAt).toLocaleString('en-IN') }
        ]}
        actions={row => <button type="button" onClick={() => setSelected(row)}>Review</button>}
      />
      {selected && <section className="workflow-review"><header><div><h3>{selected.resourceType} <small>#{selected.resourceId}</small></h3><p>{selected.stageName}</p></div><button type="button" className="secondary" onClick={() => setSelected(null)}>Close</button></header>{selected.resourceType === 'PayRun' ? <>{loadingPayRun && <p className="form-warning">Loading payroll review...</p>}{payRun ? <PayRunReview selected={payRun} diagnostics={diagnostics} busy={false} materialVarianceCount={materialVarianceCount} actions={false} /> : !loadingPayRun && <p className="empty">Payroll run data is not available.</p>}{approvalControls}</> : <><div className="request-details">{details(selected.payloadJson).map(([key, value]) => <div key={key}><span>{key}</span><b>{value}</b></div>)}{!details(selected.payloadJson).length && <p className="empty">No additional request details were recorded.</p>}</div>{approvalControls}</>}</section>}
    </section>
  )
}
