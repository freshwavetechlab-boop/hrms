import { useEffect, useState } from 'react'
import AttendanceSettingsForm from '../components/AttendanceSettingsForm'
import GeoFenceManager from '../components/GeoFenceManager'
import HolidayManager from '../components/HolidayManager'
import LeaveAttendancePreferencesForm from '../components/LeaveAttendancePreferencesForm'
import LeaveBalanceImportManager from '../components/LeaveBalanceImportManager'
import LeaveTypesManager from '../components/LeaveTypesManager'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'

export type LeaveAttendanceMenu = 'Preferences' | 'Leave Types' | 'Holiday' | 'Attendance' | 'Geo-Fencing' | 'Import Balance'

export default function LeaveAttendancePage({ activeMenu }: { activeMenu: LeaveAttendanceMenu; onSelectMenu: (menu: LeaveAttendanceMenu) => void }) {
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(0)
  const [message, setMessage] = useState('Select a client to manage Leave & Attendance settings.')

  useEffect(() => {
    void getClients().then(rows => {
      setClients(rows)
      setClientId(current => {
        if (rows.some(row => row.id === current)) return current
        return rows.find(row => row.isActive)?.id || rows[0]?.id || 0
      })
    })
  }, [])

  if (!clientId) return <section className="leave-attendance empty-state"><div><span className="eyebrow purple">Leave & Attendance</span><h3>No client found</h3><p>Create a client before configuring Leave & Attendance.</p></div></section>

  const selectedClient = clients.find(client => client.id === clientId)
  const clientFilter = <div className="card leave-client-filter"><label className="leave-dropdown-field"><span>Client</span><div className="leave-select-shell leave-select-shell-display"><select value={clientId} onChange={event => setClientId(Number(event.target.value))} aria-label="Select client"><option value="0" disabled>Select client</option>{clients.map(client => <option value={client.id} key={client.id}>{client.name} ({client.code}){client.isActive ? '' : ' · Inactive'}</option>)}</select><span className="leave-select-display-value">{selectedClient?.name || 'Select client'}</span></div></label></div>
  const content = activeMenu === 'Preferences' ? <LeaveAttendancePreferencesForm clientId={clientId} onSaved={setMessage} /> : activeMenu === 'Leave Types' ? <LeaveTypesManager clientId={clientId} onMessage={setMessage} /> : activeMenu === 'Holiday' ? <HolidayManager clientId={clientId} onMessage={setMessage} /> : activeMenu === 'Attendance' ? <AttendanceSettingsForm clientId={clientId} onSaved={setMessage} /> : activeMenu === 'Geo-Fencing' ? <GeoFenceManager clientId={clientId} clientName={selectedClient?.name || ''} onMessage={setMessage} /> : <LeaveBalanceImportManager clientId={clientId} onMessage={setMessage} />

  return <section className="leave-attendance"><div className="pay-run-intro leave-page-head"><div><span className="eyebrow purple">Leave & Attendance / {activeMenu}</span><h3>{activeMenu}</h3><p>{message}</p></div></div>{clientFilter}{content}</section>
}
