import { useEffect, useState } from 'react'
import { getJson, postJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'

type Approver = { id: number; displayName: string; email: string; clientId?: number | null }
type Assignment = { id: number; department: string; userName: string }

export default function DepartmentHeadAssignments() {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(0)
  const [departments, setDepartments] = useState<string[]>([])
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [approvers, setApprovers] = useState<Approver[]>([])
  const [department, setDepartment] = useState('')
  const [userId, setUserId] = useState('')
  const [message, setMessage] = useState('')

  useEffect(() => {
    void getClients().then(setClients)
    void getJson<Approver[]>('/api/workflows/approvers', []).then(setApprovers)
  }, [])

  const load = (id: number) => {
    void getJson<string[]>(`/api/workflows/departments?clientId=${id}`, []).then(setDepartments)
    void getJson<Assignment[]>(`/api/workflows/department-heads?clientId=${id}`, []).then(setAssignments)
  }

  const selectClient = (id: number) => {
    setClientId(id)
    setDepartment('')
    setUserId('')
    if (id) load(id)
    else {
      setDepartments([])
      setAssignments([])
    }
  }

  const save = async () => {
    if (!clientId || !department || !userId) {
      setMessage('Choose a client, department, and user.')
      return
    }
    const response = await postJson('/api/workflows/department-heads', { clientId, department, userId: Number(userId) }, null)
    if (!response.ok) {
      setMessage('Unable to save assignment.')
      return
    }
    setMessage('Department head assignment saved.')
    setDepartment('')
    setUserId('')
    load(clientId)
  }

  const eligibleUsers = approvers.filter(user => !user.clientId || user.clientId === clientId)

  return (
    <section className="card department-heads">
      <header>
        <div>
          <h3>Department Head Assignments</h3>
          <p>Map each department to its approval user. The employee designation is not used.</p>
        </div>
      </header>
      {message && <p className="form-warning">{message}</p>}
      <div className="department-heads-form">
        <label><span>Client</span><SearchSelect value={clientId} onChange={value => selectClient(Number(value))} options={selectOptions(clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name })), 'Select client', 0)} /></label>
        <label><span>Department</span><SearchSelect value={department} onChange={setDepartment} disabled={!clientId} options={selectOptions(departments, 'Select department')} /></label>
        <label><span>Department head user</span><SearchSelect value={userId} onChange={setUserId} disabled={!clientId} options={selectOptions(eligibleUsers.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` })), 'Select user')} /></label>
        <button type="button" onClick={() => void save()}>Save assignment</button>
      </div>
      {clientId > 0 && (
        <DataTable
          rows={assignments}
          emptyText="No department heads mapped for this client."
          exportFileName="department-heads"
          columns={[{ key: 'department', label: 'Department' }, { key: 'userName', label: 'Assigned user' }]}
        />
      )}
    </section>
  )
}
