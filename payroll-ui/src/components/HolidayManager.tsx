import { useEffect, useMemo, useState } from 'react'
import { deleteHoliday, getHolidays, saveHoliday } from '../services/leaveAttendanceService'
import { getWorkLocations } from '../services/settingsService'
import type { Holiday, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import PageTabs from './PageTabs'
import SearchSelect, { selectOptions } from './SearchSelect'

const today = new Date().toISOString().slice(0, 10)
const blank: Holiday = { id: 0, clientId: 0, name: '', startDate: today, endDate: today, description: '', allLocations: true, workLocationIds: [], workLocations: 'All locations' }
const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const holidayViews = ['Table', 'Calendar'] as const

export default function HolidayManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const currentYear = new Date().getFullYear()
  const [rows, setRows] = useState<Holiday[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [form, setForm] = useState<Holiday>(blank), [errors, setErrors] = useState<string[]>([])
  const [year, setYear] = useState(currentYear), [workLocationId, setWorkLocationId] = useState(0), [view, setView] = useState<'Table' | 'Calendar'>('Table')
  const years = Array.from({ length: 7 }, (_, index) => currentYear - 3 + index)
  const calendar = useMemo(() => monthNames.map((month, index) => ({ month, holidays: rows.filter(row => new Date(row.startDate).getMonth() === index || new Date(row.endDate).getMonth() === index) })), [rows])
  const load = async () => { const [holidays, workLocations] = await Promise.all([getHolidays(clientId, year, workLocationId || undefined), getWorkLocations()]); setRows(holidays); setLocations(workLocations.filter(location => location.isActive)); setForm(current => current.id ? current : { ...blank, clientId }) }
  useEffect(() => { void load() }, [clientId, year, workLocationId])
  const set = <K extends keyof Holiday>(key: K, value: Holiday[K]) => setForm(current => ({ ...current, [key]: value }))
  const toggleLocation = (id: number) => set('workLocationIds', form.workLocationIds.includes(id) ? form.workLocationIds.filter(item => item !== id) : [...form.workLocationIds, id])
  const validate = () => { const next = []; if (!form.name.trim()) next.push('Holiday name is required.'); if (form.endDate < form.startDate) next.push('End date cannot be before start date.'); if (!form.allLocations && form.workLocationIds.length === 0) next.push('Select at least one work location.'); setErrors(next); return next.length === 0 }
  const save = async () => { if (!validate()) return; const response = await saveHoliday({ ...form, clientId }); if (response.ok) { setForm({ ...blank, clientId }); setErrors([]); onMessage('Holiday saved.'); await load() } else setErrors([response.error || 'Unable to save holiday.']) }
  const edit = (row: Holiday) => { setForm({ ...blank, ...row, startDate: String(row.startDate).slice(0, 10), endDate: String(row.endDate).slice(0, 10), workLocationIds: row.workLocationIds || [] }); setErrors([]) }
  const remove = async (row: Holiday) => { if (!window.confirm(`Delete ${row.name}?`)) return; const response = await deleteHoliday(clientId, row.id); if (response.ok) { onMessage('Holiday deleted.'); await load() } }

  return <section className="holiday-manager">
    <div className="card">
      <header><i className="blue">H</i><div><h3>Holiday Management</h3><p>Maintain location-wise holidays and prevent duplicate date overlaps.</p></div></header>
      <div className="holiday-toolbar"><label><span>Year</span><SearchSelect value={year} onChange={value => setYear(Number(value))} options={years.map(value => ({ value, label: String(value) }))} /></label><label><span>Work Location</span><SearchSelect value={workLocationId} onChange={value => setWorkLocationId(Number(value))} options={selectOptions(locations.map(location => ({ value: location.id, label: location.name })), 'All locations', 0)} /></label></div>
      <PageTabs items={holidayViews} value={view} onChange={setView} label="Holiday views" />
      {view === 'Table' ? <HolidayTable rows={rows} edit={edit} remove={remove} /> : <div className="holiday-calendar">{calendar.map(item => <article key={item.month}><h4>{item.month}</h4>{item.holidays.length ? item.holidays.map(holiday => <button type="button" key={holiday.id} onClick={() => edit(holiday)}><strong>{holiday.name}</strong><span>{dateRange(holiday)}</span><small>{holiday.workLocations}</small></button>) : <p>No holidays</p>}</article>)}</div>}
    </div>
    <HolidayForm form={form} locations={locations} errors={errors} set={set} toggleLocation={toggleLocation} save={save} cancel={() => { setForm(blank); setErrors([]) }} />
  </section>
}

function HolidayTable({ rows, edit, remove }: { rows: Holiday[]; edit: (row: Holiday) => void; remove: (row: Holiday) => void }) {
  return <DataTable rows={rows} emptyText="No holidays configured for this filter." exportFileName="holidays" columns={[
    { key: 'name', label: 'Holiday Name' },
    { key: 'startDate', label: 'Start Date', value: row => formatDate(row.startDate) },
    { key: 'endDate', label: 'End Date', value: row => formatDate(row.endDate) },
    { key: 'workLocations', label: 'Work Locations' },
    { key: 'description', label: 'Description' }
  ]} actions={row => <><button type="button" onClick={() => edit(row)}>Edit</button><button type="button" className="danger" onClick={() => void remove(row)}>Delete</button></>} />
}

function HolidayForm(p: { form: Holiday; locations: WorkLocation[]; errors: string[]; set: <K extends keyof Holiday>(key: K, value: Holiday[K]) => void; toggleLocation: (id: number) => void; save: () => void; cancel: () => void }) {
  return <section className="card holiday-form"><header><i className="blue">{p.form.id ? 'E' : '+'}</i><div><h3>{p.form.id ? 'Edit Holiday' : 'Add Holiday'}</h3><p>Single-day holidays use the same start and end date.</p></div></header><div className="grid"><label><span>Holiday name</span><input value={p.form.name} onChange={event => p.set('name', event.target.value)} /></label><label><span>Start date</span><input type="date" value={p.form.startDate} onChange={event => p.set('startDate', event.target.value)} /></label><label><span>End date</span><input type="date" value={p.form.endDate} onChange={event => p.set('endDate', event.target.value)} /></label><label className="wide"><span>Description</span><input value={p.form.description} onChange={event => p.set('description', event.target.value)} /></label><label><span>Applicable locations</span><SearchSelect value={p.form.allLocations ? 'all' : 'selected'} onChange={value => p.set('allLocations', value === 'all')} options={[{ value: 'all', label: 'All locations' }, { value: 'selected', label: 'Multiple selected locations' }]} /></label></div>{!p.form.allLocations && <div className="location-picker">{p.locations.map(location => <label className={p.form.workLocationIds.includes(location.id) ? 'selected' : ''} key={location.id}><input type="checkbox" checked={p.form.workLocationIds.includes(location.id)} onChange={() => p.toggleLocation(location.id)} /><span>{location.name}</span><small>{location.city}, {location.state}</small></label>)}</div>}{p.errors.length > 0 && <div className="form-errors">{p.errors.map(error => <p key={error}>{error}</p>)}</div>}<div className="actions"><p>Duplicate holidays are blocked for overlapping date ranges and locations.</p><span><button type="button" className="secondary" onClick={p.cancel}>Cancel</button><button type="button" onClick={() => void p.save()}>{p.form.id ? 'Update holiday' : 'Save holiday'}</button></span></div></section>
}

function formatDate(value: string) { return new Date(value).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) }
function dateRange(holiday: Holiday) { return holiday.startDate === holiday.endDate ? formatDate(holiday.startDate) : `${formatDate(holiday.startDate)} - ${formatDate(holiday.endDate)}` }
