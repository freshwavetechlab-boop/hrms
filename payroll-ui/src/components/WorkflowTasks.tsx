import { useEffect, useState } from 'react'
import { api } from '../services/apiClient'
import DataTable from './DataTable'

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
  const [remark, setRemark] = useState('')
  const [message, setMessage] = useState('')
  const load = () => fetch(`${api}/api/workflows/tasks/pending`).then(response => response.ok ? response.json() : []).then(setRows)

  useEffect(() => { void load() }, [])

  const action = async (actionName: string) => {
    if (!selected) return
    const response = await fetch(`${api}/api/workflows/tasks/${selected.id}/${actionName}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ comment: remark.trim() }) })
    setMessage(response.ok ? `Task ${actionName.toLowerCase()}.` : 'Unable to update task.')
    if (response.ok) {
      setSelected(null)
      setRemark('')
      load()
    }
  }

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
      {selected && <section className="workflow-review"><header><div><h3>{selected.resourceType} <small>#{selected.resourceId}</small></h3><p>{selected.stageName}</p></div><button type="button" className="secondary" onClick={() => setSelected(null)}>Close</button></header><div className="request-details">{details(selected.payloadJson).map(([key, value]) => <div key={key}><span>{key}</span><b>{value}</b></div>)}{!details(selected.payloadJson).length && <p className="empty">No additional request details were recorded.</p>}</div><label><span>Remarks</span><textarea value={remark} onChange={event => setRemark(event.target.value)} placeholder="Add approval, rejection, or send-back remarks..." /></label><div className="workflow-review-actions"><button type="button" onClick={() => void action('Approved')}>Approve</button><button type="button" className="secondary" onClick={() => void action('Sent Back')}>Send back</button><button type="button" className="danger" onClick={() => void action('Rejected')}>Reject</button></div></section>}
    </section>
  )
}
