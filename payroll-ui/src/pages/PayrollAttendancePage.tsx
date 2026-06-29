import { useEffect, useState } from 'react'
import ManualAttendanceManager from '../components/ManualAttendanceManager'
import SearchSelect from '../components/SearchSelect'
import { useToast, type ToastType } from '../components/ToastProvider'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'

export default function PayrollAttendancePage() {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(0)
  const toast = useToast()

  useEffect(() => {
    void getClients().then(rows => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(current => current || active[0]?.id || 0)
    })
  }, [])

  if (!clientId) return <section className="pay-runs"><div className="card report-empty"><p>Create an active client before entering payroll attendance.</p></div></section>

  const clientControl = <label className="attendance-client-control"><span>Client</span><SearchSelect value={clientId} onChange={value => setClientId(Number(value))} options={clients.map(client => ({ value: client.id, label: client.name }))} /></label>

  return <section className="pay-runs payroll-attendance-page">
    <ManualAttendanceManager clientId={clientId} clientControl={clientControl} onMessage={(message, type: ToastType = 'success') => toast(message, type)} />
  </section>
}
