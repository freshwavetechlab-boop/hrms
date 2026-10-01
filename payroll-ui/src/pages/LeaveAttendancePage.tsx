import { useEffect, useState } from 'react'
import AttendanceSettingsForm from '../components/AttendanceSettingsForm'
import AttendanceGroupsManager from '../components/AttendanceGroupsManager'
import GeoFenceManager from '../components/GeoFenceManager'
import HolidayManager from '../components/HolidayManager'
import LeaveBalanceImportManager from '../components/LeaveBalanceImportManager'
import LeaveTypesManager from '../components/LeaveTypesManager'
import SearchSelect from '../components/SearchSelect'
import { useToast } from '../components/ToastProvider'
import { getClients } from '../services/payrollService'
import type { Client } from '../types/payroll'
import { useAuthSession } from '../components/AuthGate'
import { useSearchParams } from 'react-router-dom'
import { RecruitmentViewContext, useSessionPreference, type RecruitmentView } from '../hooks/useRecruitmentPreferences'
import { PageHeaderPortal } from '../components/layout/AppPageHeader'
import { Segmented } from 'antd'
import './AttendanceSettingsWorkspace.css'

export type LeaveAttendanceMenu = 'Attendance Policies' | 'Leave Types' | 'Holiday' | 'Attendance' | 'Geo-Fencing' | 'Import Balance'

export default function LeaveAttendancePage({ activeMenu }: { activeMenu: LeaveAttendanceMenu; onSelectMenu: (menu: LeaveAttendanceMenu) => void }) {
  const toast = useToast()
  const session = useAuthSession()
  const scopedClientId = Number(session?.user.clientId || 0)
  const clientScoped = scopedClientId > 0 && !session?.user.permissions.includes('settings.manage')
  const [clients, setClients] = useState<Client[]>([])
  const [searchParams] = useSearchParams()
  const [clientId, setClientId] = useSessionPreference(`attendance.settings.client:${session?.user.id}`, 0)
  const [view, setView] = useSessionPreference<RecruitmentView>(`attendance.settings.view:${session?.user.id}:${clientId}:${activeMenu}`, 'Table')
  useEffect(() => { const requested = Number(searchParams.get('clientId')); if (requested > 0 && clients.some(c => c.id === requested) && (!clientScoped || requested === scopedClientId)) setClientId(requested) }, [searchParams, clients, clientScoped, scopedClientId])

  useEffect(() => {
    void getClients().then(rows => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(current => clientScoped ? scopedClientId : current || active[0]?.id || 0)
    })
  }, [clientScoped, scopedClientId])

  if (!clientId) return <section className="leave-attendance empty-state"><div><span className="eyebrow purple">Leave & Attendance</span><h3>No active client</h3><p>Create an active client before configuring Leave & Attendance.</p></div></section>

  const showMessage = (text: string) => toast(text, /error|unable|failed|required|resolve|select|cannot|must|invalid|at least/i.test(text) ? 'error' : 'success')
  const clientFilter = clientScoped ? null : <PageHeaderPortal slot="attendance-settings-client-controls"><SearchSelect testId="attendance-client" value={clientId} onChange={value => setClientId(Number(value))} options={clients.map(client => ({ value: client.id, label: client.name }))} /></PageHeaderPortal>
  const content = activeMenu === 'Attendance Policies' ? <AttendanceGroupsManager fixedClientId={clientId} onMessage={showMessage} /> : activeMenu === 'Leave Types' ? <LeaveTypesManager clientId={clientId} onMessage={showMessage} /> : activeMenu === 'Holiday' ? <HolidayManager clientId={clientId} onMessage={showMessage} /> : activeMenu === 'Attendance' ? <AttendanceSettingsForm clientId={clientId} onSaved={showMessage} /> : activeMenu === 'Geo-Fencing' ? <GeoFenceManager clients={clients} clientId={clientId} fixedClientId={clientId} onClientChange={setClientId} onMessage={showMessage} /> : <LeaveBalanceImportManager clientId={clientId} onMessage={showMessage} />

  const recordList = !['Attendance', 'Import Balance'].includes(activeMenu)
  return <RecruitmentViewContext.Provider value={{ view, scope: `${session?.user.id}:${clientId}`, namespace: 'attendance.settings.ui' }}><section className={recordList ? 'leave-attendance attendance-settings-record-page recruitment-monitor-page recruitment-experience recruitment-viewport' : 'leave-attendance'}>{clientFilter}{recordList && <PageHeaderPortal slot="attendance-settings-view-controls"><Segmented value={view} options={[{ label: 'Card view', value: 'Cards' }, { label: 'Table view', value: 'Table' }]} onChange={value => setView(value as RecruitmentView)} /></PageHeaderPortal>}{content}</section></RecruitmentViewContext.Provider>
}
