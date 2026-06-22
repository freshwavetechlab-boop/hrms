import { useEffect, useState } from 'react'
import AttendanceSettingsForm from '../components/AttendanceSettingsForm'
import HolidayManager from '../components/HolidayManager'
import LeaveAttendancePreferencesForm from '../components/LeaveAttendancePreferencesForm'
import LeaveBalanceImportManager from '../components/LeaveBalanceImportManager'
import LeaveTypesManager from '../components/LeaveTypesManager'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'

export type LeaveAttendanceMenu = 'Preferences' | 'Leave Types' | 'Holiday' | 'Attendance' | 'Import Balance'

export default function LeaveAttendancePage({ activeMenu }: { activeMenu: LeaveAttendanceMenu; onSelectMenu: (menu: LeaveAttendanceMenu) => void }) {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(0)
  const [message, setMessage] = useState('Select a client to manage Leave & Attendance settings.')

  useEffect(() => {
    void getClients().then(rows => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(current => current || active[0]?.id || 0)
    })
  }, [])

  if (!clientId) return <section className="leave-attendance empty-state"><div><span className="eyebrow purple">Leave & Attendance</span><h3>No active client</h3><p>Create an active client before configuring Leave & Attendance.</p></div></section>

  const clientFilter = <div className="card leave-client-filter"><label><span>Client</span><select value={clientId} onChange={event => setClientId(Number(event.target.value))}>{clients.map(client => <option value={client.id} key={client.id}>{client.name}</option>)}</select></label></div>
  const content = activeMenu === 'Preferences' ? <LeaveAttendancePreferencesForm clientId={clientId} onSaved={setMessage} /> : activeMenu === 'Leave Types' ? <LeaveTypesManager clientId={clientId} onMessage={setMessage} /> : activeMenu === 'Holiday' ? <HolidayManager clientId={clientId} onMessage={setMessage} /> : activeMenu === 'Attendance' ? <AttendanceSettingsForm clientId={clientId} onSaved={setMessage} /> : <LeaveBalanceImportManager clientId={clientId} onMessage={setMessage} />

  return <section className="leave-attendance"><div className="pay-run-intro leave-page-head"><div><span className="eyebrow purple">Leave & Attendance / {activeMenu}</span><h3>{activeMenu}</h3><p>{message}</p></div></div>{clientFilter}{content}</section>
}
