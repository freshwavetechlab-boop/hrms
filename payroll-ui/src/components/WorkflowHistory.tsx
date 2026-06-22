import { useEffect, useState } from 'react'
import { getJson } from '../services/apiClient'
import DataTable from './DataTable'

type Instance = { id: number; workflowName: string; resourceType: string; resourceId: string; payloadJson: string; requestorName: string; status: string; createdAt: string }
type Event = { id: number; action: string; actor: string; comment: string; createdAt?: string }

const formatDate = (value?: string) => {
  const date = value ? new Date(value) : null
  return date && !Number.isNaN(date.valueOf()) ? date.toLocaleString('en-IN') : 'Not recorded'
}

const details = (payload: string) => {
  try {
    return Object.entries(JSON.parse(payload) as Record<string, unknown>)
      .filter(([, value]) => value !== null && value !== '')
      .map(([key, value]) => [key.replace(/([A-Z])/g, ' $1').replace(/^./, char => char.toUpperCase()), typeof value === 'object' ? JSON.stringify(value) : String(value)])
  } catch {
    return []
  }
}

export default function WorkflowHistory() {
  const [rows, setRows] = useState<Instance[]>([])
  const [selected, setSelected] = useState<Instance | null>(null)
  const [events, setEvents] = useState<Event[]>([])

  useEffect(() => { void getJson<Instance[]>('/api/workflows/history', []).then(setRows) }, [])

  const show = async (item: Instance) => {
    setSelected(item)
    setEvents(await getJson<Event[]>(`/api/workflows/${item.id}/history`, []))
  }

  return (
    <section className="card workflow-history">
      <header><div><h3>Workflow History</h3><p>All workflow requests, their current outcome, and the approval trail.</p></div></header>
      <DataTable
        rows={rows}
        emptyText="No workflow requests have been started yet."
        exportFileName="workflow-history"
        columns={[
          { key: 'workflowName', label: 'Workflow' },
          { key: 'request', label: 'Request', value: row => `${row.resourceType} ${row.resourceId}`, render: row => <>{row.resourceType}<small>{row.resourceId}</small></> },
          { key: 'requestorName', label: 'Requestor' },
          { key: 'status', label: 'Status' },
          { key: 'createdAtText', label: 'Started', value: row => formatDate(row.createdAt) }
        ]}
        actions={row => <button type="button" onClick={() => void show(row)}>View details</button>}
      />
      {selected && <section className="workflow-trail"><header><div><h3>{selected.workflowName} <small>#{selected.id}</small></h3><p>{selected.resourceType} - {selected.resourceId}</p></div></header><h4>Request details</h4><div className="request-details">{details(selected.payloadJson).map(([key, value]) => <div key={key}><span>{key}</span><b>{value}</b></div>)}</div><h4>Approval trail</h4>{events.map(event => <div className="trail-event" key={event.id}><b>{event.action}</b><span>{event.actor} - {formatDate(event.createdAt)}</span>{event.comment && <p>{event.comment}</p>}</div>)}{!events.length && <p className="empty">No events recorded.</p>}</section>}
    </section>
  )
}
