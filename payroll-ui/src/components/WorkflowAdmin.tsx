import { useEffect, useState } from 'react'
import { api, getJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'
import DataTable from './DataTable'

type Stage = { id: number; stageOrder: number; name: string; approverType: string; approverUserId?: number | null }
type Flow = { id: number; clientId?: number | null; code: string; name: string; resourceType: string; isActive: boolean; stages: Stage[] }
type Approver = { id: number; displayName: string; clientId?: number | null; clientName: string }
const newWorkflow = (): Flow => ({ id: 0, code: '', name: '', resourceType: '', isActive: true, stages: [{ id: 0, stageOrder: 1, name: 'Manager approval', approverType: 'Reporting Manager' }] })

export default function WorkflowAdmin() {
  const [rows, setRows] = useState<Flow[]>([])
  const [flow, setFlow] = useState<Flow>(newWorkflow)
  const [clients, setClients] = useState<Client[]>([])
  const [approvers, setApprovers] = useState<Approver[]>([])
  const [message, setMessage] = useState('')
  const load = () => fetch(`${api}/api/workflows`).then(response => response.ok ? response.json() : []).then(setRows)

  useEffect(() => {
    void load()
    void getClients().then(setClients)
    void getJson<Approver[]>('/api/workflows/approvers', []).then(setApprovers)
  }, [])

  const updateStage = (index: number, changes: Partial<Stage>) => setFlow(current => ({ ...current, stages: current.stages.map((stage, position) => position === index ? { ...stage, ...changes } : stage) }))
  const visibleApprovers = approvers.filter(user => !flow.clientId || !user.clientId || user.clientId === flow.clientId)
  const edit = (row: Flow) => { setFlow({ ...row, stages: row.stages.map((stage, index) => ({ ...stage, stageOrder: index + 1 })) }); setMessage(`Editing ${row.name}.`) }
  const cancel = () => { setFlow(newWorkflow()); setMessage('') }

  const save = async () => {
    if (flow.stages.some(stage => stage.approverType === 'Specific User' && !stage.approverUserId)) {
      setMessage('Select an assigned user for every Specific User stage.')
      return
    }
    const response = await fetch(`${api}/api/workflows`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ...flow, stages: flow.stages.map((stage, index) => ({ ...stage, stageOrder: index + 1 })) }) })
    if (!response.ok) {
      setMessage('Unable to save workflow. Check the details and try again.')
      return
    }
    setMessage(flow.id ? 'Workflow updated.' : 'Workflow created.')
    setFlow(newWorkflow())
    load()
  }

  return (
    <section className="card workflow-admin">
      <header><div><h3>{flow.id ? 'Edit workflow' : 'Workflow Engine'}</h3><p>Reusable sequential approvals for any future HRMS resource type.</p></div></header>
      {message && <p className="form-warning">{message}</p>}
      <div className="grid">
        <label><span>Applies to</span><select value={flow.clientId ?? ''} onChange={event => setFlow({ ...flow, clientId: event.target.value ? Number(event.target.value) : null })}><option value="">All clients (global default)</option>{clients.filter(client => client.isActive).map(client => <option key={client.id} value={client.id}>{client.name}</option>)}</select><small>Client workflow takes priority over the global default.</small></label>
        <label><span>Workflow code</span><input value={flow.code} onChange={event => setFlow({ ...flow, code: event.target.value.toUpperCase() })} placeholder="LEAVE_REQUEST" /></label>
        <label><span>Workflow name</span><input value={flow.name} onChange={event => setFlow({ ...flow, name: event.target.value })} placeholder="Leave request approval" /></label>
        <label><span>Resource type</span><input value={flow.resourceType} onChange={event => setFlow({ ...flow, resourceType: event.target.value })} placeholder="LeaveRequest" /></label>
        <label><span>Status</span><select value={flow.isActive ? 'active' : 'inactive'} onChange={event => setFlow({ ...flow, isActive: event.target.value === 'active' })}><option value="active">Active</option><option value="inactive">Inactive</option></select></label>
      </div>
      <section className="approval-stages"><div className="approval-stages-heading"><div><h3>Approval stages</h3><p>Requests move through these stages in the order shown.</p></div><span>{flow.stages.length} {flow.stages.length === 1 ? 'stage' : 'stages'}</span></div><div className="stage-list">
        {flow.stages.map((stage, index) => <article className="workflow-stage" key={stage.id || index}><div className="stage-number"><b>{index + 1}</b><span>{index === 0 ? 'Starts here' : 'Then'}</span></div><label><span>Stage name</span><input value={stage.name} onChange={event => updateStage(index, { name: event.target.value })} placeholder={`Approval ${index + 1}`} /></label><label><span>Approver type</span><select value={stage.approverType} onChange={event => updateStage(index, { approverType: event.target.value, approverUserId: event.target.value === 'Specific User' ? stage.approverUserId : null })}><option>Reporting Manager</option><option>HR Manager</option><option>Specific User</option><option>Department Head</option></select></label>{stage.approverType === 'Specific User' && <label className="specific-user"><span>Assigned user</span><select value={stage.approverUserId ?? ''} onChange={event => updateStage(index, { approverUserId: event.target.value ? Number(event.target.value) : null })}><option value="">Select a user</option>{visibleApprovers.map(user => <option key={user.id} value={user.id}>{user.displayName} - {user.clientName}</option>)}</select></label>}<button type="button" className="stage-remove" onClick={() => setFlow({ ...flow, stages: flow.stages.filter((_, position) => position !== index) })} disabled={flow.stages.length === 1}>Remove</button></article>)}
      </div><button type="button" className="add-stage" onClick={() => setFlow({ ...flow, stages: [...flow.stages, { id: 0, stageOrder: flow.stages.length + 1, name: `Approval ${flow.stages.length + 1}`, approverType: 'HR Manager' }] })}>+ Add approval stage</button></section>
      <div className="actions">{flow.id > 0 && <button type="button" className="secondary" onClick={cancel}>Cancel</button>}<button type="button" onClick={() => void save()} disabled={!flow.code || !flow.name || !flow.resourceType}>{flow.id ? 'Update workflow' : 'Save workflow'}</button></div>
      <DataTable rows={rows} emptyText="No workflows have been configured yet." exportFileName="workflows" columns={[
        { key: 'workflow', label: 'Workflow', value: row => row.name, render: row => <>{row.name}<small>{row.code}</small></> },
        { key: 'clientName', label: 'Applies to', value: row => row.clientId ? clients.find(client => client.id === row.clientId)?.name ?? `Client #${row.clientId}` : 'All clients' },
        { key: 'resourceType', label: 'Resource' },
        { key: 'stagesText', label: 'Stages', value: row => row.stages.map(stage => stage.name).join(' -> ') },
        { key: 'status', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
      ]} actions={row => <button type="button" className="secondary" onClick={() => edit(row)}>Edit</button>} />
    </section>
  )
}
