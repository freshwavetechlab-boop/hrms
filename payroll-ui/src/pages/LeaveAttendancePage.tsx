import { useEffect, useState } from 'react'
import AttendanceSettingsForm from '../components/AttendanceSettingsForm'
import GeoFenceManager from '../components/GeoFenceManager'
import HolidayManager from '../components/HolidayManager'
import LeaveAttendancePreferencesForm from '../components/LeaveAttendancePreferencesForm'
import LeaveBalanceImportManager from '../components/LeaveBalanceImportManager'
import LeaveTypesManager from '../components/LeaveTypesManager'
import SearchSelect from '../components/SearchSelect'
import { useToast } from '../components/ToastProvider'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'

export type LeaveAttendanceMenu = 'Preferences' | 'Leave Types' | 'Holiday' | 'Attendance' | 'Geo-Fencing' | 'Import Balance'

export default function LeaveAttendancePage({ activeMenu }: { activeMenu: LeaveAttendanceMenu; onSelectMenu: (menu: LeaveAttendanceMenu) => void }) {
  const toast = useToast()
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(0)
<<<<<<< HEAD
=======
  const [clientSearch, setClientSearch] = useState('')
  const [message, setMessage] = useState('Select a client to manage Leave & Attendance settings.')
>>>>>>> b607099 (Added Attendance Geofencing Module)

  useEffect(() => {
    void getClients().then(rows => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(current => current || active[0]?.id || 0)
      setClientSearch(current => current || active[0]?.name || '')
    })
  }, [])

  if (!clientId) return <section className="leave-attendance empty-state"><div><span className="eyebrow purple">Leave & Attendance</span><h3>No active client</h3><p>Create an active client before configuring Leave & Attendance.</p></div></section>

<<<<<<< HEAD
  const showMessage = (text: string) => toast(text, /error|unable|failed|required|resolve|select/i.test(text) ? 'error' : 'success')
  const clientFilter = <div className="card leave-client-filter"><label><span>Client</span><SearchSelect value={clientId} onChange={value => setClientId(Number(value))} options={clients.map(client => ({ value: client.id, label: client.name }))} /></label></div>
  const content = activeMenu === 'Preferences' ? <LeaveAttendancePreferencesForm clientId={clientId} onSaved={showMessage} /> : activeMenu === 'Leave Types' ? <LeaveTypesManager clientId={clientId} onMessage={showMessage} /> : activeMenu === 'Holiday' ? <HolidayManager clientId={clientId} onMessage={showMessage} /> : activeMenu === 'Attendance' ? <AttendanceSettingsForm clientId={clientId} onSaved={showMessage} /> : <LeaveBalanceImportManager clientId={clientId} onMessage={showMessage} />
=======
  const selectedClient = clients.find(client => client.id === clientId)
  const pickClient = (value: string) => {
    setClientSearch(value)
    const match = clients.find(client => `${client.name} (${client.code})`.toLowerCase() === value.toLowerCase() || client.name.toLowerCase() === value.toLowerCase())
    if (match) setClientId(match.id)
  }
  const clientFilter = <div className="card leave-client-filter"><label><span>Client</span><input list="leave-client-options" value={clientSearch} onChange={event => pickClient(event.target.value)} onBlur={() => setClientSearch(selectedClient?.name || '')} placeholder="Search client..." /><datalist id="leave-client-options">{clients.map(client => <option value={`${client.name} (${client.code})`} key={client.id} />)}</datalist></label></div>
  const content = activeMenu === 'Preferences' ? <LeaveAttendancePreferencesForm clientId={clientId} onSaved={setMessage} /> : activeMenu === 'Leave Types' ? <LeaveTypesManager clientId={clientId} onMessage={setMessage} /> : activeMenu === 'Holiday' ? <HolidayManager clientId={clientId} onMessage={setMessage} /> : activeMenu === 'Attendance' ? <AttendanceSettingsForm clientId={clientId} onSaved={setMessage} /> : activeMenu === 'Geo-Fencing' ? <GeoFenceManager clientId={clientId} clientName={selectedClient?.name || ''} onMessage={setMessage} /> : <LeaveBalanceImportManager clientId={clientId} onMessage={setMessage} />
>>>>>>> b607099 (Added Attendance Geofencing Module)

  return <section className="leave-attendance">{clientFilter}{content}</section>
}
