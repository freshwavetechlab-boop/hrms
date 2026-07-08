import { useEffect, useMemo, useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'
import { DownloadOutlined, UploadOutlined } from '@ant-design/icons'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Drawer, Form, Input, Row, Space } from 'antd'
import { deleteHoliday, downloadHolidayImportTemplate, getHolidayImportJob, getHolidays, saveHoliday, startHolidayImport } from '../services/leaveAttendanceService'
import { getWorkLocations } from '../services/settingsService'
import type { BulkImportStatus } from '../services/settingsService'
import type { Holiday, WorkLocation } from '../types/payroll'
import { parseImportPreviewFile, validateImportPreview, type ImportPreviewIssue, type ImportPreviewRules } from '../utils/importPreview'
import { previewToXlsxFile } from '../utils/previewFile'
import BulkUploadPreviewModal, { emptyBulkUploadPreview, type BulkUploadPreviewState } from './BulkUploadPreviewModal'
import BulkUploadProgressModal, { type BulkUploadState, type BulkUploadSummary } from './BulkUploadProgressModal'
import DataTable from './DataTable'
import PageTabs from './PageTabs'
import SearchSelect, { selectOptions } from './SearchSelect'

const today = new Date().toISOString().slice(0, 10)
const holidayTypes = ['Holiday', 'Restricted Holiday'] as const
const blank: Holiday = { id: 0, clientId: 0, name: '', holidayType: 'Holiday', startDate: today, endDate: today, description: '', allLocations: true, workLocationIds: [], workLocations: 'All locations' }
const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const holidayViews = ['Table', 'Calendar'] as const
const holidayPreviewRules: ImportPreviewRules = {
  required: ['Holiday Name', 'Holiday Type', 'Start Date'],
  unique: [['Id']],
  booleans: ['All Locations'],
  dates: ['Start Date', 'End Date'],
  enums: { 'Holiday Type': [...holidayTypes, 'Restricted', 'RH'] },
  custom: (row, rowNumber) => {
    const issues: ImportPreviewIssue[] = []
    if (row.Id && (!/^\d+$/.test(row.Id) || Number(row.Id) <= 0)) issues.push({ rowNumber, column: 'Id', message: 'Id must be a positive number.' })
    const start = previewDateMs(row['Start Date'])
    const end = previewDateMs(row['End Date'] || row['Start Date'])
    if (start !== null && end !== null && end < start) issues.push({ rowNumber, column: 'End Date', message: 'End Date cannot be before Start Date.' })
    if (row['Work Location Ids']?.trim()) {
      row['Work Location Ids'].split(/[;,|]/).map(item => item.trim()).filter(Boolean).forEach(item => {
        if (!/^\d+$/.test(item)) issues.push({ rowNumber, column: 'Work Location Ids', message: `Work Location Id "${item}" must be numeric.` })
      })
    }
    return issues
  }
}
const wait = (ms: number) => new Promise(resolve => window.setTimeout(resolve, ms))
type HolidayBulkUpload = { open: boolean; state: BulkUploadState; percent: number; summary: BulkUploadSummary }
type ImportStart = (file: File) => Promise<{ ok: boolean; data: BulkImportStatus; error: string; status: number }>
type ImportStatus = (jobId: string) => Promise<BulkImportStatus>

export default function HolidayManager({ clientId, onMessage }: { clientId: number; onMessage: (message: string) => void }) {
  const currentYear = new Date().getFullYear()
  const [rows, setRows] = useState<Holiday[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [form, setForm] = useState<Holiday>(blank)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [errors, setErrors] = useState<string[]>([])
  const [year, setYear] = useState(currentYear)
  const [workLocationId, setWorkLocationId] = useState(0)
  const [view, setView] = useState<'Table' | 'Calendar'>('Table')
  const [templateDownloaded, setTemplateDownloaded] = useState(false)
  const [upload, setUpload] = useState<HolidayBulkUpload>({ open: false, state: 'uploading', percent: 0, summary: { totalRows: 0 } })
  const [preview, setPreview] = useState<BulkUploadPreviewState>(emptyBulkUploadPreview)
  const [previewImporting, setPreviewImporting] = useState(false)
  const [previewConfirm, setPreviewConfirm] = useState<((preview: BulkUploadPreviewState) => Promise<void>) | null>(null)
  const years = Array.from({ length: 7 }, (_, index) => currentYear - 3 + index)
  const calendar = useMemo(() => monthNames.map((month, index) => ({ month, holidays: rows.filter(row => new Date(row.startDate).getMonth() === index || new Date(row.endDate).getMonth() === index) })), [rows])
  const load = async () => {
    const [holidays, workLocations] = await Promise.all([getHolidays(clientId, year, workLocationId || undefined), getWorkLocations()])
    const activeLocations = workLocations.filter(location => location.isActive && Number(location.clientId) === Number(clientId))
    if (workLocationId && !activeLocations.some(location => Number(location.id) === Number(workLocationId))) {
      setWorkLocationId(0)
      return
    }
    setRows(holidays)
    setLocations(activeLocations)
    setForm(current => current.id ? current : { ...blank, clientId })
  }

  useEffect(() => {
    setWorkLocationId(0)
    setForm({ ...blank, clientId })
    setErrors([])
  }, [clientId])

  useEffect(() => { void load() }, [clientId, year, workLocationId])

  const set = <K extends keyof Holiday>(key: K, value: Holiday[K]) => setForm(current => ({ ...current, [key]: value }))
  const toggleLocation = (id: number) => set('workLocationIds', form.workLocationIds.includes(id) ? form.workLocationIds.filter(item => item !== id) : [...form.workLocationIds, id])
  const validLocationIds = () => form.workLocationIds.filter(id => locations.some(location => Number(location.id) === Number(id)))
  const validate = () => {
    const next = []
    if (!form.name.trim()) next.push('Holiday name is required.')
    if (!holidayTypes.includes(form.holidayType)) next.push('Select a valid holiday type.')
    if (form.endDate < form.startDate) next.push('End date cannot be before start date.')
    setErrors(next)
    return next.length === 0
  }
  const save = async () => {
    if (!validate()) return
    const selectedLocationIds = validLocationIds()
    const appliesToAll = form.allLocations || selectedLocationIds.length === 0
    const response = await saveHoliday({ ...form, clientId, allLocations: appliesToAll, workLocationIds: appliesToAll ? [] : selectedLocationIds })
    if (response.ok) {
      setForm({ ...blank, clientId })
      setDrawerOpen(false)
      setErrors([])
      onMessage('Holiday saved.')
      await load()
    } else setErrors([response.error || 'Unable to save holiday.'])
  }
  const edit = (row: Holiday) => {
    setForm({ ...blank, ...row, startDate: String(row.startDate).slice(0, 10), endDate: String(row.endDate).slice(0, 10), workLocationIds: row.workLocationIds || [] })
    setErrors([])
    setDrawerOpen(true)
  }
  const remove = async (row: Holiday) => {
    if (!window.confirm(`Delete ${row.name}?`)) return
    const response = await deleteHoliday(clientId, row.id)
    if (response.ok) { onMessage('Holiday deleted.'); await load() }
    else setErrors([response.error || 'Unable to delete holiday.'])
  }
  const fail = (items: string[]) => { setErrors(items); return false }
  const runBulkUploadJob = async (file: File, setBulkUpload: Dispatch<SetStateAction<HolidayBulkUpload>>, startImport: ImportStart, getImportJob: ImportStatus, failureText: string) => {
    setBulkUpload({ open: true, state: 'uploading', percent: 1, summary: { totalRows: 0 } })
    const start = await startImport(file)
    if (!start.ok || !start.data.jobId) {
      setBulkUpload({ open: true, state: 'error', percent: 100, summary: { ...start.data, errors: start.data.errors?.length ? start.data.errors : [start.error || 'Upload failed.'] } })
      return
    }
    let job = start.data
    while (job.state === 'Queued' || job.state === 'Processing') {
      const percent = job.totalRows ? Math.min(99, Math.round((job.completedRows / job.totalRows) * 100)) : 5
      setBulkUpload({ open: true, state: 'uploading', percent, summary: job })
      await wait(700)
      job = await getImportJob(job.jobId)
    }
    if (job.state === 'Completed') {
      setBulkUpload({ open: true, state: 'success', percent: 100, summary: job })
      await load()
      return
    }
    const percent = job.totalRows ? Math.round((job.completedRows / job.totalRows) * 100) : 100
    setBulkUpload({ open: true, state: 'error', percent, summary: { ...job, errors: job.errors?.length ? job.errors : [failureText] } })
  }
  const previewBulkUpload = async (file: File, onConfirm: (file: File) => Promise<void>) => {
    try {
      const data = await parseImportPreviewFile(file)
      const issues = validateImportPreview(data, holidayPreviewRules)
      setPreview({ open: true, title: 'Holiday bulk upload preview', fileName: file.name, headers: data.headers, rows: data.rows, issues })
      setPreviewConfirm(() => async (preview: BulkUploadPreviewState) => onConfirm(previewToXlsxFile(preview, file.name)))
    } catch (error) {
      fail([error instanceof Error ? error.message : 'Unable to preview import file.'])
    }
  }
  const confirmPreview = async (preview: BulkUploadPreviewState) => {
    if (!previewConfirm) return
    const action = previewConfirm
    setPreviewImporting(true)
    setPreview(emptyBulkUploadPreview)
    setPreviewConfirm(null)
    try {
      await action(preview)
    } finally {
      setPreviewImporting(false)
    }
  }
  const downloadTemplate = async () => {
    if (!clientId) return fail(['Select a client before downloading holiday template.'])
    const response = await downloadHolidayImportTemplate(clientId)
    if (!response.ok || !response.data) return fail([response.error || 'Unable to download holiday template.'])
    saveBlob(response.data, 'holiday-import-template.xlsx')
    setTemplateDownloaded(true)
    onMessage('Holiday import template downloaded.')
    return true
  }
  const uploadTemplate = async (file: File | null) => {
    if (!file) return
    if (!templateDownloaded) {
      const next = ['Download the holiday template before uploading.']
      setUpload({ open: true, state: 'error', percent: 0, summary: { totalRows: 0, errors: next } })
      fail(next)
      return
    }
    await previewBulkUpload(file, selected => runBulkUploadJob(selected, setUpload, item => startHolidayImport(clientId, item), getHolidayImportJob, 'Holiday import failed. No rows were saved.'))
  }

  return <section className="holiday-manager">
    <AntCard className="settings-panel settings-table-panel holiday-list-card" size="small" title="Holiday Management">
      <div className="component-table-head"><div><b>Holiday master</b><span>Maintain holidays and restricted holidays by year and work-location applicability.</span></div><Space className="settings-master-actions" size={8} wrap><Button className="settings-toolbar-secondary" icon={<DownloadOutlined />} onClick={() => void downloadTemplate()}>Template</Button><label className={`settings-upload-action ${!templateDownloaded ? 'disabled' : ''}`} title={templateDownloaded ? 'Upload Excel or CSV' : 'Download template first'}><input type="file" disabled={!templateDownloaded} accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv" onChange={event => { void uploadTemplate(event.target.files?.[0] ?? null); event.currentTarget.value = '' }} /><UploadOutlined />Bulk upload</label><Button type="primary" onClick={() => { setForm({ ...blank, clientId }); setErrors([]); setDrawerOpen(true) }}>Add holiday</Button></Space></div>
      <Row gutter={12} className="holiday-toolbar">
        <Col xs={24} sm={8} md={6} lg={5} className="holiday-year-field"><Form.Item label="Year"><SearchSelect value={year} onChange={value => setYear(Number(value))} options={years.map(value => ({ value, label: String(value) }))} /></Form.Item></Col>
        <Col xs={24} sm={16} md={12} lg={10} className="holiday-location-field"><Form.Item label="Work Location"><SearchSelect value={workLocationId} onChange={value => setWorkLocationId(Number(value))} options={selectOptions(locations.map(location => ({ value: location.id, label: `${location.name}${location.city ? ` - ${location.city}` : ''}` })), 'All locations', 0)} /></Form.Item></Col>
      </Row>
      <PageTabs items={holidayViews} value={view} onChange={setView} label="Holiday views" />
      {view === 'Table' ? <HolidayTable rows={rows} edit={edit} remove={remove} /> : <div className="holiday-calendar">{calendar.map(item => <article key={item.month}><h4>{item.month}</h4>{item.holidays.length ? item.holidays.map(holiday => <button type="button" key={holiday.id} onClick={() => edit(holiday)}><strong>{holiday.name}</strong><span>{holiday.holidayType}</span><span>{dateRange(holiday)}</span><small>{holiday.workLocations}</small></button>) : <p>No holidays</p>}</article>)}</div>}
    </AntCard>
    <Drawer className="settings-master-drawer" title={form.id ? 'Edit holiday' : 'Add holiday'} open={drawerOpen} width={620} onClose={() => { setDrawerOpen(false); setForm({ ...blank, clientId }); setErrors([]) }} destroyOnClose>
      <HolidayForm form={form} locations={locations} errors={errors} set={set} toggleLocation={toggleLocation} save={save} cancel={() => { setDrawerOpen(false); setForm({ ...blank, clientId }); setErrors([]) }} />
    </Drawer>
    <BulkUploadProgressModal open={upload.open} title="Holiday bulk upload" state={upload.state} percent={upload.percent} summary={upload.summary} onClose={() => setUpload(current => ({ ...current, open: false }))} />
    <BulkUploadPreviewModal preview={preview} importing={previewImporting} onCancel={() => { setPreview(emptyBulkUploadPreview); setPreviewConfirm(null) }} onConfirm={preview => void confirmPreview(preview)} />
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
  ]} actions={row => <Space size={6}><Button size="small" type="primary" onClick={() => edit(row)}>Edit</Button><Button size="small" danger onClick={() => void remove(row)}>Delete</Button></Space>} />
}

function HolidayForm(p: { form: Holiday; locations: WorkLocation[]; errors: string[]; set: <K extends keyof Holiday>(key: K, value: Holiday[K]) => void; toggleLocation: (id: number) => void; save: () => void; cancel: () => void }) {
  return <div className="holiday-form">
    <Form className="settings-quick-form" component={false} layout="vertical" requiredMark={false}>
      {p.errors.length > 0 && <Alert type="error" showIcon message={p.errors.join(' ')} />}
      <Row gutter={12}>
        <Col xs={24} md={12}><Form.Item label="Holiday name" required><Input value={p.form.name} onChange={event => p.set('name', event.target.value)} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Holiday type" required><SearchSelect value={p.form.holidayType} onChange={value => p.set('holidayType', normalizeHolidayType(value))} options={selectOptions([...holidayTypes])} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Start date" required><Input type="date" value={p.form.startDate} onChange={event => p.set('startDate', event.target.value)} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="End date" required><Input type="date" value={p.form.endDate} onChange={event => p.set('endDate', event.target.value)} /></Form.Item></Col>
        <Col xs={24}><Form.Item label="Description"><Input value={p.form.description} onChange={event => p.set('description', event.target.value)} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Applicable locations" extra="If no location is selected, holiday applies to all locations."><SearchSelect value={p.form.allLocations ? 'all' : 'selected'} onChange={value => p.set('allLocations', value === 'all')} options={[{ value: 'all', label: 'All locations' }, { value: 'selected', label: 'Selected locations only' }]} /></Form.Item></Col>
      </Row>
      {!p.form.allLocations && <div className="location-picker">{p.locations.map(location => <label className={p.form.workLocationIds.includes(location.id) ? 'selected' : ''} key={location.id}><AntCheckbox checked={p.form.workLocationIds.includes(location.id)} onChange={() => p.toggleLocation(location.id)} /><span>{location.name}</span><small>{location.city}, {location.state}</small></label>)}</div>}
      <Divider />
      <Row justify="end"><Space><Button onClick={p.cancel}>Cancel</Button><Button type="primary" onClick={() => void p.save()}>{p.form.id ? 'Update holiday' : 'Save holiday'}</Button></Space></Row>
    </Form>
  </div>
}

function normalize(value: string) { return value.trim().toLowerCase().replace(/\s+/g, '') }
function normalizeHolidayType(value: string): Holiday['holidayType'] { return ['restrictedholiday', 'restricted', 'rh'].includes(normalize(value)) ? 'Restricted Holiday' : 'Holiday' }
function parsePreviewFlag(value: string, fallback: boolean) { return value.trim() ? ['true', 'yes', 'active', '1'].includes(value.trim().toLowerCase()) : fallback }
function previewDateMs(text: string) {
  const clean = text.trim()
  if (!clean) return null
  const serial = Number(clean)
  if (Number.isFinite(serial) && serial >= 20000 && serial <= 80000) return Date.UTC(1899, 11, 30) + serial * 86400000
  const parsed = Date.parse(clean)
  return Number.isNaN(parsed) ? null : parsed
}
function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}
function formatDate(value: string) { return new Date(value).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) }
function dateRange(holiday: Holiday) { return holiday.startDate === holiday.endDate ? formatDate(holiday.startDate) : `${formatDate(holiday.startDate)} - ${formatDate(holiday.endDate)}` }
