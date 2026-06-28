import { useEffect, useMemo, useState } from 'react'
import { deleteHoliday, getHolidays, saveHoliday } from '../services/leaveAttendanceService'
import { getWorkLocations } from '../services/settingsService'
import type { Holiday, WorkLocation } from '../types/payroll'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import PageTabs from './PageTabs'
import SearchSelect, { selectOptions } from './SearchSelect'

const today = new Date().toISOString().slice(0, 10)
const holidayTypes = ['Holiday', 'Restricted Holiday'] as const
const blank: Holiday = { id: 0, clientId: 0, name: '', holidayType: 'Holiday', startDate: today, endDate: today, description: '', allLocations: true, workLocationIds: [], workLocations: 'All locations' }
const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const holidayViews = ['Table', 'Calendar'] as const

type HolidayImportRow = Holiday & { rowNumber: number; errors: string[] }

export default function HolidayManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const currentYear = new Date().getFullYear()
  const [rows, setRows] = useState<Holiday[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [form, setForm] = useState<Holiday>(blank)
  const [errors, setErrors] = useState<string[]>([])
  const [year, setYear] = useState(currentYear)
  const [workLocationId, setWorkLocationId] = useState(0)
  const [view, setView] = useState<'Table' | 'Calendar'>('Table')
  const years = Array.from({ length: 7 }, (_, index) => currentYear - 3 + index)
  const calendar = useMemo(() => monthNames.map((month, index) => ({ month, holidays: rows.filter(row => new Date(row.startDate).getMonth() === index || new Date(row.endDate).getMonth() === index) })), [rows])
  const load = async () => {
    const [holidays, workLocations] = await Promise.all([getHolidays(clientId, year, workLocationId || undefined), getWorkLocations()])
    setRows(holidays)
    setLocations(workLocations.filter(location => location.isActive))
    setForm(current => current.id ? current : { ...blank, clientId })
  }

  useEffect(() => { void load() }, [clientId, year, workLocationId])

  const set = <K extends keyof Holiday>(key: K, value: Holiday[K]) => setForm(current => ({ ...current, [key]: value }))
  const toggleLocation = (id: number) => set('workLocationIds', form.workLocationIds.includes(id) ? form.workLocationIds.filter(item => item !== id) : [...form.workLocationIds, id])
  const validate = () => {
    const next = []
    if (!form.name.trim()) next.push('Holiday name is required.')
    if (!holidayTypes.includes(form.holidayType)) next.push('Select a valid holiday type.')
    if (form.endDate < form.startDate) next.push('End date cannot be before start date.')
    if (!form.allLocations && form.workLocationIds.length === 0) next.push('Select at least one work location.')
    setErrors(next)
    return next.length === 0
  }
  const save = async () => {
    if (!validate()) return
    const response = await saveHoliday({ ...form, clientId })
    if (response.ok) {
      setForm({ ...blank, clientId })
      setErrors([])
      onMessage('Holiday saved.')
      await load()
    } else setErrors([response.error || 'Unable to save holiday.'])
  }
  const edit = (row: Holiday) => {
    setForm({ ...blank, ...row, startDate: String(row.startDate).slice(0, 10), endDate: String(row.endDate).slice(0, 10), workLocationIds: row.workLocationIds || [] })
    setErrors([])
  }
  const remove = async (row: Holiday) => {
    if (!window.confirm(`Delete ${row.name}?`)) return
    const response = await deleteHoliday(clientId, row.id)
    if (response.ok) { onMessage('Holiday deleted.'); await load() }
    else setErrors([response.error || 'Unable to delete holiday.'])
  }

  return <section className="holiday-manager">
    <div className="card">
      <header><i className="blue">H</i><div><h3>Holiday Management</h3><p>Maintain location-wise holidays and prevent duplicate date overlaps.</p></div></header>
      <div className="holiday-toolbar"><label><span>Year</span><SearchSelect value={year} onChange={value => setYear(Number(value))} options={years.map(value => ({ value, label: String(value) }))} /></label><label><span>Work Location</span><SearchSelect value={workLocationId} onChange={value => setWorkLocationId(Number(value))} options={selectOptions(locations.map(location => ({ value: location.id, label: location.name })), 'All locations', 0)} /></label></div>
      <PageTabs items={holidayViews} value={view} onChange={setView} label="Holiday views" />
      {view === 'Table' ? <HolidayTable rows={rows} edit={edit} remove={remove} /> : <div className="holiday-calendar">{calendar.map(item => <article key={item.month}><h4>{item.month}</h4>{item.holidays.length ? item.holidays.map(holiday => <button type="button" key={holiday.id} onClick={() => edit(holiday)}><strong>{holiday.name}</strong><span>{holiday.holidayType}</span><span>{dateRange(holiday)}</span><small>{holiday.workLocations}</small></button>) : <p>No holidays</p>}</article>)}</div>}
    </div>
    <HolidayForm form={form} locations={locations} errors={errors} set={set} toggleLocation={toggleLocation} save={save} cancel={() => { setForm({ ...blank, clientId }); setErrors([]) }} />
    <HolidayBulkUpload clientId={clientId} locations={locations} onImported={async message => { onMessage(message); await load() }} />
  </section>
}

function HolidayTable({ rows, edit, remove }: { rows: Holiday[]; edit: (row: Holiday) => void; remove: (row: Holiday) => void }) {
  return <DataTable rows={rows} emptyText="No holidays configured for this filter." exportFileName="holidays" columns={[
    { key: 'name', label: 'Holiday Name' },
    { key: 'holidayType', label: 'Type' },
    { key: 'startDate', label: 'Start Date', value: row => formatDate(row.startDate) },
    { key: 'endDate', label: 'End Date', value: row => formatDate(row.endDate) },
    { key: 'workLocations', label: 'Work Locations' },
    { key: 'description', label: 'Description' }
  ]} actions={row => <><button type="button" onClick={() => edit(row)}>Edit</button><button type="button" className="danger" onClick={() => void remove(row)}>Delete</button></>} />
}

function HolidayForm(p: { form: Holiday; locations: WorkLocation[]; errors: string[]; set: <K extends keyof Holiday>(key: K, value: Holiday[K]) => void; toggleLocation: (id: number) => void; save: () => void; cancel: () => void }) {
  return <section className="card holiday-form"><header><i className="blue">{p.form.id ? 'E' : '+'}</i><div><h3>{p.form.id ? 'Edit Holiday' : 'Add Holiday'}</h3><p>Single-day holidays use the same start and end date.</p></div></header><div className="grid"><label><span>Holiday name</span><input value={p.form.name} onChange={event => p.set('name', event.target.value)} /></label><label><span>Holiday type</span><SearchSelect value={p.form.holidayType} onChange={value => p.set('holidayType', normalizeHolidayType(value))} options={selectOptions([...holidayTypes])} /></label><label><span>Start date</span><input type="date" value={p.form.startDate} onChange={event => p.set('startDate', event.target.value)} /></label><label><span>End date</span><input type="date" value={p.form.endDate} onChange={event => p.set('endDate', event.target.value)} /></label><label className="wide"><span>Description</span><input value={p.form.description} onChange={event => p.set('description', event.target.value)} /></label><label><span>Applicable locations</span><SearchSelect value={p.form.allLocations ? 'all' : 'selected'} onChange={value => p.set('allLocations', value === 'all')} options={[{ value: 'all', label: 'All locations' }, { value: 'selected', label: 'Multiple selected locations' }]} /></label></div>{!p.form.allLocations && <div className="location-picker">{p.locations.map(location => <label className={p.form.workLocationIds.includes(location.id) ? 'selected' : ''} key={location.id}><input type="checkbox" checked={p.form.workLocationIds.includes(location.id)} onChange={() => p.toggleLocation(location.id)} /><span>{location.name}</span><small>{location.city}, {location.state}</small></label>)}</div>}{p.errors.length > 0 && <div className="form-errors">{p.errors.map(error => <p key={error}>{error}</p>)}</div>}<div className="actions"><p>RH rows are maintained as Restricted Holiday records and can be location-wise or all-location.</p><span><button type="button" className="secondary" onClick={p.cancel}>Cancel</button><button type="button" onClick={() => void p.save()}>{p.form.id ? 'Update holiday' : 'Save holiday'}</button></span></div></section>
}

function HolidayBulkUpload(p: { clientId: number; locations: WorkLocation[]; onImported: (message: string) => Promise<void> }) {
  const [fileName, setFileName] = useState('')
  const [preview, setPreview] = useState<HolidayImportRow[]>([])
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const validRows = preview.filter(row => row.errors.length === 0)

  const pickFile = async (file: File) => {
    setFileName(file.name)
    setErrors([])
    const rows = parseHolidayCsv(await file.text(), p.clientId, p.locations)
    setPreview(rows)
    if (rows.length === 0) setErrors(['No holiday rows found in the file.'])
  }
  const importRows = async () => {
    if (validRows.length === 0) { setErrors(['No valid rows to import.']); return }
    setBusy(true)
    const failed: string[] = []
    let imported = 0
    for (const row of validRows) {
      const response = await saveHoliday(row)
      if (response.ok) imported += 1
      else failed.push(`Row ${row.rowNumber}: ${response.error || 'Unable to save holiday.'}`)
    }
    setBusy(false)
    setErrors(failed.slice(0, 8))
    await p.onImported(`Imported ${imported} holidays. Skipped ${preview.length - imported}.`)
  }
  const downloadSample = () => {
    const csv = ['name,holidayType,startDate,endDate,description,allLocations,workLocations', 'Republic Day,Holiday,2026-01-26,2026-01-26,National holiday,true,', 'Local Foundation Day,Restricted Holiday,2026-04-12,2026-04-12,Optional local holiday,false,"Mumbai Office; Bengaluru Office"'].join('\n')
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url
    link.download = 'holiday-import-sample.csv'
    link.click()
    URL.revokeObjectURL(url)
  }

  return <section className="card holiday-import">
    <header><i className="blue">I</i><div><h3>Bulk Upload Holidays</h3><p>Upload all-location or location-wise holiday rows from CSV.</p></div></header>
    <div className="grid"><label className="wide"><span>Select CSV</span><FileDropZone accept=".csv,text/csv" fileName={fileName} title="Drop holiday CSV here or browse" hint="Columns: name, holidayType, startDate, endDate, description, allLocations, workLocations." onFile={file => void pickFile(file)} /></label></div>
    {preview.length > 0 && <div className="holiday-import-summary"><strong>{validRows.length} valid</strong><span>{preview.length - validRows.length} with errors</span><span>{preview.length} total rows</span></div>}
    {preview.length > 0 && <DataTable rows={preview.slice(0, 20)} getRowId={row => row.rowNumber} emptyText="No import rows." rowClassName={row => row.errors.length ? 'error' : ''} exportFileName="holiday-import-preview" columns={[
      { key: 'rowNumber', label: 'Row' },
      { key: 'name', label: 'Holiday Name' },
      { key: 'holidayType', label: 'Type' },
      { key: 'startDate', label: 'Start Date' },
      { key: 'endDate', label: 'End Date' },
      { key: 'workLocations', label: 'Work Locations' },
      { key: 'errors', label: 'Errors', value: row => row.errors.join('; ') }
    ]} />}
    {preview.length > 20 && <p className="empty">Showing first 20 rows.</p>}
    {errors.length > 0 && <div className="form-errors">{errors.map(error => <p key={error}>{error}</p>)}</div>}
    <div className="actions"><p>Use Holiday, Restricted Holiday, or RH in holidayType. Blank workLocations means all locations.</p><span><button type="button" className="secondary" onClick={downloadSample}>Download Sample</button><button type="button" disabled={busy || validRows.length === 0} onClick={() => void importRows()}>{busy ? 'Importing...' : 'Import holidays'}</button></span></div>
  </section>
}

function parseHolidayCsv(text: string, clientId: number, locations: WorkLocation[]) {
  const [headerRow, ...dataRows] = parseCsv(text).filter(row => row.some(cell => cell.trim()))
  if (!headerRow) return []
  const header = headerRow.map(cell => normalize(cell))
  const locationMap = new Map(locations.flatMap(location => [[normalize(location.name), location], [String(location.id), location]]))
  return dataRows.map((row, index) => {
    const read = (name: string) => row[header.indexOf(normalize(name))] || ''
    const name = read('name').trim()
    const holidayType = normalizeHolidayType(read('holidayType'))
    const startDate = read('startDate').trim()
    const endDate = (read('endDate').trim() || startDate)
    const description = read('description').trim()
    const locationText = read('workLocations').trim()
    const allLocations = parseBoolean(read('allLocations')) || !locationText
    const selected = locationText ? locationText.split(';').map(item => locationMap.get(normalize(item))).filter(Boolean) as WorkLocation[] : []
    const errors: string[] = []
    if (!name) errors.push('Holiday name is required.')
    if (!isDate(startDate)) errors.push('Start date must be YYYY-MM-DD.')
    if (!isDate(endDate)) errors.push('End date must be YYYY-MM-DD.')
    if (isDate(startDate) && isDate(endDate) && endDate < startDate) errors.push('End date cannot be before start date.')
    if (!allLocations && selected.length === 0) errors.push('No matching work locations found.')
    return { ...blank, id: 0, clientId, name, holidayType, startDate, endDate, description, allLocations, workLocationIds: allLocations ? [] : selected.map(location => location.id), workLocations: allLocations ? 'All locations' : selected.map(location => location.name).join(', '), rowNumber: index + 2, errors }
  })
}

function parseCsv(text: string) {
  const rows: string[][] = []
  let row: string[] = []
  let cell = ''
  let quoted = false
  for (let index = 0; index < text.length; index += 1) {
    const char = text[index]
    const next = text[index + 1]
    if (char === '"' && quoted && next === '"') { cell += '"'; index += 1; continue }
    if (char === '"') { quoted = !quoted; continue }
    if (char === ',' && !quoted) { row.push(cell); cell = ''; continue }
    if ((char === '\n' || char === '\r') && !quoted) {
      if (char === '\r' && next === '\n') index += 1
      row.push(cell)
      rows.push(row)
      row = []
      cell = ''
      continue
    }
    cell += char
  }
  row.push(cell)
  rows.push(row)
  return rows
}

function normalize(value: string) { return value.trim().toLowerCase().replace(/\s+/g, '') }
function normalizeHolidayType(value: string): Holiday['holidayType'] { return ['restrictedholiday', 'restricted', 'rh'].includes(normalize(value)) ? 'Restricted Holiday' : 'Holiday' }
function parseBoolean(value: string) { return ['true', 'yes', 'y', '1', 'all'].includes(value.trim().toLowerCase()) }
function isDate(value: string) { return /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(new Date(`${value}T00:00:00`).getTime()) }
function formatDate(value: string) { return new Date(value).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) }
function dateRange(holiday: Holiday) { return holiday.startDate === holiday.endDate ? formatDate(holiday.startDate) : `${formatDate(holiday.startDate)} - ${formatDate(holiday.endDate)}` }
