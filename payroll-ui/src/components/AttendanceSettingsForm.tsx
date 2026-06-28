import { useEffect, useState } from 'react'
import { getAttendanceSettings, saveAttendanceSettings } from '../services/leaveAttendanceService'
import type { AttendanceSettings } from '../types/payroll'
import SearchSelect, { selectOptions } from './SearchSelect'

const initial: AttendanceSettings = { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 }

export default function AttendanceSettingsForm({ clientId, onSaved }: { clientId: number; onSaved: (message: string) => void }) {
  const [form, setForm] = useState<AttendanceSettings>(initial), [errors, setErrors] = useState<string[]>([]), [saving, setSaving] = useState(false)
  useEffect(() => { void getAttendanceSettings(clientId).then(data => setForm(normalizeTimes(data))) }, [clientId])
  const set = <K extends keyof AttendanceSettings>(key: K, value: AttendanceSettings[K]) => { setErrors([]); setForm(current => ({ ...current, [key]: value })) }
  const validate = () => {
    const next: string[] = []
    if (!form.checkInTime || !form.checkOutTime) next.push('Check-in and check-out time are required.')
    if (form.checkOutTime <= form.checkInTime) next.push('Check-out time must be after check-in time.')
    if (form.minimumHoursForHalfDay <= 0 || form.minimumHoursForFullDay <= 0 || form.maximumHoursAllowedForFullDay <= 0) next.push('Workday duration hours must be greater than zero.')
    if (form.minimumHoursForHalfDay > form.minimumHoursForFullDay) next.push('Half-day minimum hours cannot exceed full-day minimum hours.')
    if (form.minimumHoursForFullDay > form.maximumHoursAllowedForFullDay) next.push('Full-day minimum hours cannot exceed maximum full-day hours.')
    if (form.regularizationWindow === 'Limited by past days' && form.pastDaysAllowed < 0) next.push('Past days allowed cannot be negative.')
    if (form.restrictRegularizationRequestsPerMonth && form.maxRegularizationRequestsPerMonth <= 0) next.push('Max regularization requests per month must be greater than zero.')
    setErrors(next)
    return next.length === 0
  }
  const save = async () => {
    if (!validate()) return
    setSaving(true)
    const response = await saveAttendanceSettings({ ...form, clientId })
    setSaving(false)
    if (response.ok) { setForm(normalizeTimes(response.data)); onSaved('Attendance settings saved.') } else setErrors([response.error || 'Unable to save attendance settings.'])
  }
  return <section className="card attendance-settings">
    <header><i className="blue">A</i><div><h3>Attendance Management</h3><p>Configure shift times, workday duration and regularization controls.</p></div></header>
    {errors.length > 0 && <div className="form-errors sticky-errors">{errors.map(error => <p key={error}>{error}</p>)}</div>}
    <h3>Work Shift Time</h3>
    <div className="grid"><label><span>Check-in time</span><input type="time" value={timeValue(form.checkInTime)} onChange={event => set('checkInTime', `${event.target.value}:00`)} /></label><label><span>Check-out time</span><input type="time" value={timeValue(form.checkOutTime)} onChange={event => set('checkOutTime', `${event.target.value}:00`)} /></label></div>
    <h3>Working Hours Calculation</h3>
    <div className="grid"><label className="wide"><span>Calculation method</span><SearchSelect value={form.workingHoursCalculation} onChange={value => set('workingHoursCalculation', value as AttendanceSettings['workingHoursCalculation'])} options={selectOptions(['First check-in and last check-out', 'Every valid check-in and check-out'])} /></label></div>
    <h3>Workday Duration</h3>
    <div className="grid"><label><span>Minimum hours for half-day</span><input type="number" step="0.25" value={form.minimumHoursForHalfDay} onChange={event => set('minimumHoursForHalfDay', Number(event.target.value))} /></label><label><span>Minimum hours for full-day</span><input type="number" step="0.25" value={form.minimumHoursForFullDay} onChange={event => set('minimumHoursForFullDay', Number(event.target.value))} /></label><label><span>Maximum hours allowed for full-day</span><input type="number" step="0.25" value={form.maximumHoursAllowedForFullDay} onChange={event => set('maximumHoursAllowedForFullDay', Number(event.target.value))} /></label></div>
    <h3>Regularization Settings</h3>
    <div className="grid"><Check label="Allow regularization requests" value={form.allowRegularizationRequests} set={value => set('allowRegularizationRequests', value)} /><label><span>Request window</span><SearchSelect disabled={!form.allowRegularizationRequests} value={form.regularizationWindow} onChange={value => set('regularizationWindow', value as AttendanceSettings['regularizationWindow'])} options={selectOptions(['Anytime', 'Limited by past days'])} /></label>{form.regularizationWindow === 'Limited by past days' && <label><span>Number of past days allowed</span><input type="number" disabled={!form.allowRegularizationRequests} value={form.pastDaysAllowed} onChange={event => set('pastDaysAllowed', Number(event.target.value))} /></label>}<Check label="Restrict regularization requests per month" value={form.restrictRegularizationRequestsPerMonth} set={value => set('restrictRegularizationRequestsPerMonth', value)} />{form.restrictRegularizationRequestsPerMonth && <label><span>Max requests per month</span><input type="number" disabled={!form.allowRegularizationRequests} value={form.maxRegularizationRequestsPerMonth} onChange={event => set('maxRegularizationRequestsPerMonth', Number(event.target.value))} /></label>}</div>
    <div className="actions"><p>Rules are applied during attendance calculation and payroll cut-off processing.</p><button type="button" disabled={saving} onClick={() => void save()}>{saving ? 'Saving...' : 'Save attendance settings'}</button></div>
  </section>
}

function Check({ label, value, set }: { label: string; value: boolean; set: (value: boolean) => void }) {
  return <label><span>{label}</span><input type="checkbox" checked={value} onChange={event => set(event.target.checked)} /></label>
}

function timeValue(value: string) { return value?.slice(0, 5) || '' }
function normalizeTimes(settings: AttendanceSettings) {
  return { ...initial, ...settings, checkInTime: settings.checkInTime?.slice(0, 8) || initial.checkInTime, checkOutTime: settings.checkOutTime?.slice(0, 8) || initial.checkOutTime }
}
