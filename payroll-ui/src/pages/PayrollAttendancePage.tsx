import { useEffect, useMemo, useState } from 'react'
import ManualAttendanceManager from '../components/ManualAttendanceManager'
import SearchSelect from '../components/SearchSelect'
import { useToast, type ToastType } from '../components/ToastProvider'
import { getClients } from '../services/payrollService'
import { getAttendanceGroups } from '../services/leaveAttendanceService'
import type { AttendanceGroup, Client } from '../types/payroll'

export default function PayrollAttendancePage() {
  const [clients, setClients] = useState<Client[]>([])
  const [groups, setGroups] = useState<AttendanceGroup[]>([])
  const [clientId, setClientId] = useState(0)
  const [reviewScope, setReviewScope] = useState('cycle')
  const toast = useToast()

  useEffect(() => {
    void getClients().then((rows) => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(current => current || active[0]?.id || 0)
    })
  }, [])

  useEffect(() => {
    if (!clientId) return
    void getAttendanceGroups(clientId).then(rows => {
      const active = rows.filter(row => row.isActive)
      setGroups(active)
      setReviewScope(current => current === 'cycle' || active.some(group => `group:${group.id}` === current) ? current : 'cycle')
    })
  }, [clientId])

  const selectedGroup = useMemo(() => groups.find(group => `group:${group.id}` === reviewScope) || null, [groups, reviewScope])

  if (!clientId) return <section className="pay-runs"><div className="card report-empty"><p>Create an active client before entering payroll attendance.</p></div></section>
  const clientControl = <>
    <label className="attendance-client-control attendance-client-field"><span>Client</span><SearchSelect value={clientId} onChange={value => { setClientId(Number(value)); setReviewScope('cycle') }} options={clients.map(client => ({ value: client.id, label: client.name }))} /></label>
    <label className="attendance-client-control attendance-scope-field"><span>Review scope</span><SearchSelect value={reviewScope} onChange={setReviewScope} options={[{ value: 'cycle', label: 'Attendance cycle' }, ...groups.map(group => ({ value: `group:${group.id}`, label: `Group: ${group.name} - ${group.workLocationName || 'Location'}` }))]} /></label>
  </>

  return <section className="pay-runs payroll-attendance-page">
    <ManualAttendanceManager clientId={clientId} group={selectedGroup} clientControl={clientControl} onMessage={(message, type: ToastType = 'success') => toast(message, type)} />
  </section>
}
