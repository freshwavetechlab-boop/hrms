import { useState } from 'react'
import { createPortal } from 'react-dom'
import { saveClient } from '../services/settingsService'
import { safeJsonObject } from '../shared/json'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { useToast } from './ToastProvider'

type Client = { id: number; name: string; code: string; contactPerson: string; email: string; phone: string; address: string; payScheduleJson: string; isActive: boolean }
type Schedule = { workWeek: string; salaryDays: string; fixedDays: string; payDay: string; firstPayPeriod: string }
const empty: Schedule = { workWeek: 'Monday - Friday', salaryDays: 'Actual days', fixedDays: '', payDay: 'Last working day', firstPayPeriod: new Date().toISOString().slice(0, 7) }
const read = (client: Client): Schedule => safeJsonObject(client.payScheduleJson, empty)
const fixedDaysHelp = 'Applicable if salary days are based on Fixed Days. Keep empty if not applicable.'

export default function ClientPayScheduleManager({ clients, reload }: { clients: Client[]; reload: () => Promise<void> }) {
  const toast = useToast()
  const [clientId, setClientId] = useState(0), [schedule, setSchedule] = useState<Schedule>(empty)
  const select = (id: number) => { const client = clients.find(row => row.id === id); setClientId(id); setSchedule(client ? read(client) : empty) }
  const save = async () => { const client = clients.find(row => row.id === clientId); if (!client) return toast('Select a client.', 'error'); const payload = { ...schedule, fixedDays: schedule.salaryDays === 'Fixed days' ? schedule.fixedDays : '' }; const response = await saveClient({ ...client, payScheduleJson: JSON.stringify(payload) }); toast(response.ok ? 'Client schedule saved.' : response.error || 'Unable to save schedule.', response.ok ? 'success' : 'error'); if (response.ok) await reload() }
  const remove = async () => { const client = clients.find(row => row.id === clientId); if (!client) return toast('Select a client.', 'error'); if (!window.confirm('Delete this client schedule?')) return; const response = await saveClient({ ...client, payScheduleJson: '{}' }); toast(response.ok ? 'Client schedule deleted.' : response.error || 'Unable to delete schedule.', response.ok ? 'success' : 'error'); if (response.ok) { setSchedule(empty); await reload() } }
  return <section className="schedule-crud"><div className="grid"><label><span>Client</span><SearchSelect value={clientId} onChange={value => select(Number(value))} options={selectOptions(clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name })), 'Select client', 0)} /></label><label><span>Work week</span><SearchSelect value={schedule.workWeek} onChange={value => setSchedule({ ...schedule, workWeek: value })} options={selectOptions(['Monday - Friday', 'Monday - Saturday', 'All days'])} /></label><label><span>Salary days</span><SearchSelect value={schedule.salaryDays} onChange={value => setSchedule({ ...schedule, salaryDays: value, fixedDays: value === 'Fixed days' ? schedule.fixedDays : '' })} options={selectOptions(['Actual days', 'Fixed days'])} /></label><label><span className="field-label">Fixed days<HelpTip text={fixedDaysHelp} /></span><input value={schedule.fixedDays} disabled={schedule.salaryDays !== 'Fixed days'} placeholder={schedule.salaryDays === 'Fixed days' ? 'e.g. 30' : ''} onChange={event => setSchedule({ ...schedule, fixedDays: event.target.value.replace(/\D/g, '').slice(0, 2) })} /></label><label><span>Pay day</span><SearchSelect value={schedule.payDay} onChange={value => setSchedule({ ...schedule, payDay: value })} options={selectOptions(['Last working day', 'Last day of month', '1st of next month', '5th of next month'])} /></label><label><span>First period</span><input type="month" value={schedule.firstPayPeriod} onChange={event => setSchedule({ ...schedule, firstPayPeriod: event.target.value })} /></label></div><div className="schedule-actions"><button type="button" onClick={() => void save()}>Save schedule</button><button type="button" className="secondary" onClick={() => void remove()}>Delete schedule</button></div><DataTable rows={clients.filter(client => client.payScheduleJson && client.payScheduleJson !== '{}')} exportFileName="client-pay-schedules" columns={[{ key: 'name', label: 'Client' }, { key: 'workWeek', label: 'Work week', value: client => read(client).workWeek }, { key: 'salaryDaysText', label: 'Salary days', value: client => { const value = read(client); return value.salaryDays === 'Fixed days' ? `Fixed: ${value.fixedDays}` : value.salaryDays } }, { key: 'payDay', label: 'Pay day', value: client => read(client).payDay }]} actions={client => <button type="button" onClick={() => select(client.id)}>Edit</button>} /></section>
}

function HelpTip({ text }: { text: string }) {
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null)
  const open = (target: HTMLElement) => {
    const box = target.getBoundingClientRect()
    setPos({ top: box.bottom + 8, left: Math.min(Math.max(12, box.left + box.width / 2), window.innerWidth - 12) })
  }
  return <>
    <span className="field-help" tabIndex={0} aria-label={text} onMouseEnter={event => open(event.currentTarget)} onMouseLeave={() => setPos(null)} onFocus={event => open(event.currentTarget)} onBlur={() => setPos(null)}>?</span>
    {pos && createPortal(<small className="field-help-popover" style={{ top: pos.top, left: pos.left }}>{text}</small>, document.body)}
  </>
}
