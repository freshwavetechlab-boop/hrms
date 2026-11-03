import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { Alert, Button, Checkbox, Drawer, Form, Input, InputNumber, Modal, Popconfirm, Segmented, Select, Space, Switch, Tabs, Tag } from 'antd'
import { DeleteOutlined, EditOutlined, PlusOutlined, ReloadOutlined, SyncOutlined, ThunderboltOutlined } from '@ant-design/icons'
import { useAuthSession } from './AuthGate'
import { PageHeaderPortal } from './layout/AppPageHeader'
import RecruitmentRecordList from './RecruitmentRecordList'
import DataTable, { type Column } from './DataTable'
import { RecruitmentViewContext, useSessionPreference, type RecruitmentView } from '../hooks/useRecruitmentPreferences'
import { getJsonResult } from '../services/apiClient'
import { getAttendanceDevices, type AttendanceDevice } from '../services/attendanceIntegrationService'
import { attendanceAdapterSetupIssues, attendanceAdapterStatus, deleteAttendanceAdapter, emptyAttendanceAdapter, getAttendanceAdapters, idmsAttendanceAdapter, runAttendanceAdapter, saveAttendanceAdapter, type AdapterPunch, type AdapterRun, type AttendanceApiAdapter } from '../services/attendanceApiAdapterService'
import type { Client } from '../types/payroll'
import '../RecruitmentViewport.css'
import './AttendanceApiIntegrator.css'

const pretty = (value: object) => JSON.stringify(value, null, 2)
const dateLabel = (value?: string) => value ? new Date(value).toLocaleString('en-IN') : 'Never'
const today = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date())
function stringMap(text: string, label: string): Record<string, string> {
  const value: unknown = JSON.parse(text)
  if (!value || Array.isArray(value) || typeof value !== 'object' || Object.values(value).some(v => typeof v !== 'string')) throw new Error(`${label} must be a JSON object with string values.`)
  return value as Record<string, string>
}

export default function AttendanceApiIntegrator() {
  const session = useAuthSession()
  const scope = `attendance-adapters:${session?.user.id || 0}`
  const [clientId, setClientId] = useSessionPreference<number>(`${scope}:client`, Number(new URLSearchParams(window.location.search).get('clientId')) || 0)
  const [view, setView] = useSessionPreference<RecruitmentView>(`${scope}:${clientId}:view`, 'Cards')
  const [clients, setClients] = useState<Client[]>([])
  const [feed, setFeed] = useState<{ clientId: number; devices: AttendanceDevice[]; rows: AttendanceApiAdapter[] }>({ clientId: 0, devices: [], rows: [] })
  const devices = feed.clientId === clientId ? feed.devices : []
  const rows = feed.clientId === clientId ? feed.rows : []
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [editor, setEditor] = useState<AttendanceApiAdapter | null>(null)
  const [editorTab, setEditorTab] = useState('connection')
  const [editorError, setEditorError] = useState('')
  const [secret, setSecret] = useState('')
  const [token, setToken] = useState('')
  const [clearSecret, setClearSecret] = useState(false)
  const [clearToken, setClearToken] = useState(false)
  const [headers, setHeaders] = useState('{}')
  const [query, setQuery] = useState('{}')
  const [employees, setEmployees] = useState('{}')
  const [inValues, setInValues] = useState('IN')
  const [outValues, setOutValues] = useState('OUT')
  const [target, setTarget] = useState<{ row: AttendanceApiAdapter; preview: boolean } | null>(null)
  const [fromDate, setFromDate] = useState(today)
  const [toDate, setToDate] = useState(today)
  const [result, setResult] = useState<AdapterRun | null>(null)
  const [runError, setRunError] = useState('')

  useEffect(() => {
    let current = true
    void getJsonResult<Client[]>('/api/clients', [], { loader: false }).then(response => {
      if (!current) return
      if (!response.ok) { setMessage(response.error); return }
      setClients(response.data.filter(c => c.isActive))
    })
    return () => { current = false }
  }, [])
  useEffect(() => {
    if (clients.length && !clients.some(c => c.id === clientId)) setClientId(clients[0].id)
  }, [clients, clientId, setClientId])

  const load = useCallback(async () => {
    if (!clientId) return
    const response = await getAttendanceAdapters(clientId)
    setFeed(current => current.clientId === clientId ? { ...current, rows: response.ok ? response.data : [] } : current)
    if (!response.ok) setMessage(response.error)
  }, [clientId])
  useEffect(() => {
    let current = true
    if (!clientId) return
    void Promise.all([getAttendanceAdapters(clientId), getAttendanceDevices(clientId)]).then(([adapters, machines]) => {
      if (!current) return
      setFeed({ clientId, rows: adapters.ok ? adapters.data : [], devices: machines.ok ? machines.data.filter(d => d.isActive) : [] })
      if (!adapters.ok || !machines.ok) setMessage(adapters.error || machines.error)
    }).finally(() => { if (current) setLoading(false) })
    return () => { current = false }
  }, [clientId])

  const changeClient = (value: number) => { setClientId(value); setEditor(null); setTarget(null); setMessage('') }

  const edit = (row: AttendanceApiAdapter) => {
    setEditor(structuredClone(row)); setEditorTab('connection'); setEditorError(''); setSecret(''); setToken(''); setClearSecret(false); setClearToken(false)
    setHeaders(pretty(row.headers)); setQuery(pretty(row.query)); setEmployees(pretty(row.employeeMap)); setInValues(row.inValues.join(', ')); setOutValues(row.outValues.join(', '))
  }
  const update = <K extends keyof AttendanceApiAdapter>(key: K, value: AttendanceApiAdapter[K]) => setEditor(current => current ? { ...current, [key]: value } : null)
  const save = async () => {
    if (!editor) return
    setEditorError(''); setBusy(true)
    try {
      const body = { ...editor, headers: stringMap(headers, 'Headers'), query: stringMap(query, 'Query parameters'), employeeMap: stringMap(employees, 'Employee mapping'), inValues: inValues.split(',').map(v => v.trim()).filter(Boolean), outValues: outValues.split(',').map(v => v.trim()).filter(Boolean) }
      JSON.parse(body.bodyTemplate)
      const response = await saveAttendanceAdapter(body, secret, token, clearSecret, clearToken)
      if (!response.ok) { setEditorError(response.error); return }
      setEditor(null); setSecret(''); setToken(''); setMessage('Adapter saved. Use Test to check the response before syncing attendance.'); await load()
    } catch (e) { setEditorError(e instanceof Error ? e.message : 'Check the JSON configuration.') }
    finally { setBusy(false) }
  }
  const remove = async (row: AttendanceApiAdapter) => {
    setBusy(true)
    try { const response = await deleteAttendanceAdapter(row); if (!response.ok) setMessage(response.error); else { setMessage('Adapter deleted.'); await load() } }
    finally { setBusy(false) }
  }
  const openRun = (row: AttendanceApiAdapter, preview: boolean) => { setTarget({ row, preview }); setResult(null); setRunError('') }
  const run = async () => {
    if (!target) return
    setBusy(true); setResult(null); setRunError('')
    try {
      const response = await runAttendanceAdapter(target.row, target.preview, fromDate, toDate)
      if (!response.ok) setRunError(response.error)
      else setResult(response.data)
      await load()
    } finally { setBusy(false) }
  }
  const actions = (row: AttendanceApiAdapter) => <Space wrap size={4}>
    <Button size="small" aria-label="Edit" icon={<EditOutlined />} disabled={busy} onClick={() => edit(row)}>Edit</Button>
    <Button size="small" aria-label="Test" title={attendanceAdapterSetupIssues(row).length ? `Complete setup: ${attendanceAdapterSetupIssues(row).join(", ")}` : "Fetch and preview punches"} icon={<ThunderboltOutlined />} disabled={busy || attendanceAdapterSetupIssues(row).length > 0} onClick={() => openRun(row, true)}>Test</Button>
    <Button size="small" aria-label="Sync" icon={<SyncOutlined />} disabled={busy || !row.isActive} onClick={() => openRun(row, false)}>Sync</Button>
    <Popconfirm title={`Delete ${row.name}?`} description="Saved attendance will be retained." onConfirm={() => remove(row)}><Button size="small" danger disabled={busy} icon={<DeleteOutlined />} aria-label={`Delete ${row.name}`} /></Popconfirm>
  </Space>
  const columns: Column<AttendanceApiAdapter>[] = [
    { key: 'name', label: 'Adapter', value: r => r.name },
    { key: 'deviceId', label: 'Machine', value: r => devices.find(d => d.deviceId === r.deviceId)?.name || r.deviceId, render: r => <span>{devices.find(d => d.deviceId === r.deviceId)?.name || r.deviceId}<small className="adapter-secondary">{r.sourceDeviceId}</small></span> },
    { key: 'isActive', label: 'Status', value: attendanceAdapterStatus, render: r => <Tag color={r.isActive ? 'green' : attendanceAdapterStatus(r) === 'Draft' ? 'orange' : 'default'}>{attendanceAdapterStatus(r)}</Tag> },
    { key: 'url', label: 'API endpoint', value: r => `${r.method} ${r.url}` },
    { key: 'syncIntervalMinutes', label: 'Schedule', value: r => r.syncIntervalMinutes ? `Every ${r.syncIntervalMinutes} min / ${r.lookbackDays} days` : 'Manual' },
    { key: 'lastSuccessAt', label: 'Last successful sync', value: r => dateLabel(r.lastSuccessAt) },
    { key: 'lastRun', label: 'Last result', value: r => r.lastRun?.message || 'Not tested', render: r => <span>{r.lastRun && <Tag color={r.lastRun.success ? 'green' : 'orange'}>{r.lastRun.preview ? 'Test' : 'Sync'}</Tag>}{r.lastRun?.message || 'Not tested'}</span> },
  ]
  const previewColumns: Column<AdapterPunch>[] = [
    { key: 'employeeCode', label: 'Employee code' }, { key: 'action', label: 'IN / OUT' }, { key: 'capturedAt', label: 'Punch time with timezone' },
  ]
  const textField = (key: keyof AttendanceApiAdapter, label: string, help?: string) => editor && <Form.Item label={label} help={help}><Input aria-label={label} value={String(editor[key] ?? '')} onChange={e => update(key, e.target.value as never)} /></Form.Item>
  const jsonField = (label: string, value: string, change: (value: string) => void, help: string) => <Form.Item label={label} help={help}><Input.TextArea aria-label={label} className="adapter-json" rows={5} value={value} onChange={e => change(e.target.value)} spellCheck={false} /></Form.Item>

  return <RecruitmentViewContext.Provider value={{ view, scope: `${session?.user.id}:${clientId}`, namespace: 'attendance-api-adapters.ui' }}>
    <section className="recruitment-viewport attendance-adapter-workspace" data-testid="attendance-api-integrator">
      <PageHeaderPortal slot="attendance-adapters-client-controls"><Select aria-label="Attendance adapter client" showSearch optionFilterProp="label" value={clientId || undefined} disabled={busy} onChange={changeClient} placeholder="Select client" options={clients.map(c => ({ value: c.id, label: c.name }))} style={{ minWidth: 180, maxWidth: '100%' }} /></PageHeaderPortal>
      <PageHeaderPortal slot="attendance-adapters-view-controls"><Segmented aria-label="Adapter view" options={['Cards', 'Table']} value={view} onChange={v => setView(v as RecruitmentView)} /></PageHeaderPortal>
      <PageHeaderPortal slot="attendance-adapters-page-controls"><Space wrap><Button aria-label="Refresh" icon={<ReloadOutlined />} disabled={!clientId || busy} onClick={() => { setLoading(true); void load().finally(() => setLoading(false)) }}>Refresh</Button><Button aria-label="Add adapter" type="primary" icon={<PlusOutlined />} disabled={!clientId || busy} onClick={() => edit(emptyAttendanceAdapter(clientId))}>Add adapter</Button><Button disabled={!clientId || busy} onClick={() => edit(idmsAttendanceAdapter(clientId))}>Use iDMS preset</Button></Space></PageHeaderPortal>
      {message && <Alert type="info" showIcon message={message} closable onClose={() => setMessage('')} />}
      {!loading && feed.clientId === clientId && clientId > 0 && devices.length === 0 && <Alert type="info" showIcon message={<>Register a punch machine for this client in <Link to={`/settings/leave-attendance/attendance?clientId=${clientId}`}>Attendance settings</Link>, then generate its key in <Link to="/workflows/api-catalog">API Catalog</Link>.</>} />}
      <RecruitmentRecordList rows={rows} columns={columns} view={view} title={r => r.name} subtitle={r => `${r.method} · ${r.sourceDeviceId}`} filters={[{ key: 'state', label: 'Status', value: attendanceAdapterStatus }]} quickFilters={[{ key: 'enabled', label: 'Enabled', tone: 'green', matches: r => r.isActive }, { key: 'paused', label: 'Paused / Draft', tone: 'gray', matches: r => !r.isActive }]} actions={actions} hiddenCardColumns={['name']} cardSummaryColumns={['deviceId', 'isActive', 'syncIntervalMinutes', 'lastSuccessAt']} exportFileName="attendance-api-adapters" emptyText={clientId ? 'No attendance adapters. Add a generic API or use the iDMS preset.' : 'Select a client to configure attendance APIs.'} searchPlaceholder="Search adapters or machines" loading={loading || (clientId > 0 && feed.clientId !== clientId)} actionsWidth={300} />
    </section>
    <Drawer aria-label={editor?.id ? 'Edit attendance API adapter' : 'Add attendance API adapter'} title={editor?.id ? 'Edit attendance API adapter' : 'Add attendance API adapter'} width="min(1000px, 96vw)" open={!!editor} destroyOnClose maskClosable={!busy} closable={!busy} onClose={() => { if (!busy) { setEditor(null); setSecret(''); setToken('') } }} footer={<Space><Button disabled={busy} onClick={() => { setEditor(null); setSecret(''); setToken('') }}>Cancel</Button><Button type="primary" loading={busy} onClick={() => void save()}>Save adapter</Button></Space>}>
      {editor && <Form layout="vertical" className="attendance-adapter-editor">
        {editorError && <Alert type="error" showIcon message={editorError} />}
        {!editor.isActive && <Alert type="info" showIcon message="Save a paused draft while machine details or verified IN/OUT values are pending. Complete setup before testing or enabling sync." />}
        <Tabs activeKey={editorTab} onChange={setEditorTab} items={[
          { key: 'connection', label: 'Connection', children: <div className="adapter-form-grid">
            {textField('name', 'Adapter name')}
            <Form.Item label="Registered HRMS machine" help="Defines the client and work location where punches are allowed."><Select aria-label="Registered HRMS machine" showSearch optionFilterProp="label" value={editor.deviceId || undefined} options={devices.map(d => ({ value: d.deviceId, label: `${d.name} (${d.deviceId})` }))} onChange={value => { update('deviceId', value); if (!editor.sourceDeviceId) update('sourceDeviceId', value) }} /></Form.Item>
            {textField('sourceDeviceId', 'Vendor device identifier', 'Used by {{deviceId}} and the response device filter.')}
            <Form.Item label="Enabled"><Switch checked={editor.isActive} onChange={v => update('isActive', v)} checkedChildren="Enabled" unCheckedChildren="Paused" /></Form.Item>
            <div className="adapter-wide">{textField('url', 'API endpoint URL')}</div>
            <Form.Item label="HTTP method"><Select value={editor.method} options={['GET', 'POST'].map(value => ({ value, label: value }))} onChange={v => update('method', v)} /></Form.Item>
            <Form.Item label="Authentication"><Select value={editor.authType} options={[{ value: 'None', label: 'No authentication' }, { value: 'ApiKey', label: 'API key header' }, { value: 'Bearer', label: 'Bearer token' }, { value: 'Basic', label: 'Basic authentication' }]} onChange={v => update('authType', v)} /></Form.Item>
            {editor.authType === 'ApiKey' && textField('authHeader', 'API key header name')}
            {editor.authType === 'Basic' && textField('username', 'Username')}
            <Form.Item label="Vendor API credential" help={editor.hasSecret ? 'A credential is saved. Leave blank to retain it.' : 'Used for authentication or the {{secret}} placeholder; stored encrypted.'}><Input.Password autoComplete="new-password" value={secret} disabled={clearSecret} onChange={e => setSecret(e.target.value)} /><Checkbox checked={clearSecret} onChange={e => setClearSecret(e.target.checked)}>Remove saved credential</Checkbox></Form.Item>
            <Form.Item label="HRMS machine API key" help={editor.hasDeviceToken ? 'A key is saved. Leave blank to retain it.' : 'Generate for the selected machine in Workflows → API Catalog. Stored encrypted.'}><Input.Password autoComplete="new-password" value={token} disabled={clearToken} onChange={e => setToken(e.target.value)} /><Checkbox checked={clearToken} onChange={e => setClearToken(e.target.checked)}>Remove saved machine key</Checkbox></Form.Item>
            <Form.Item label="API timeout (seconds)"><InputNumber min={3} max={60} value={editor.timeoutSeconds} onChange={v => update('timeoutSeconds', v ?? 20)} /></Form.Item>
            <Form.Item label="Private network access" help="Requests run from the HRMS API server, which must reach the client's LAN/VPN. Localhost and metadata services are blocked."><Switch checked={editor.allowPrivateNetwork} onChange={v => update('allowPrivateNetwork', v)} /></Form.Item>
          </div> },
          { key: 'request', label: 'Request & pagination', children: <div className="adapter-form-grid">
            <Alert className="adapter-wide" type="info" message="Placeholders: {{fromDate}}, {{toDate}}, {{toDateExclusive}}, {{deviceId}}, {{page}}, {{offset}}, {{pageSize}}, {{secret}}. Put placeholders inside JSON strings; page/offset/pageSize become JSON numbers." />
            {textField('requestDateFormat', 'Request date format', 'For example yyyy-MM-dd or yyyy-MM-dd HH:mm:ss (.NET format).')}
            <Form.Item label="Pagination"><Select value={editor.pagination} options={['None', 'Page', 'Offset'].map(value => ({ value, label: value }))} onChange={v => update('pagination', v)} /></Form.Item>
            <div className="adapter-wide">{jsonField('JSON request body', editor.bodyTemplate, value => update('bodyTemplate', value), 'POST only. Keep credentials in {{secret}}, rather than entering them in the template.')}</div>
            {jsonField('Query parameters', query, setQuery, 'JSON object, for example {"page":"{{page}}","limit":"{{pageSize}}"}.')}
            {jsonField('Extra headers', headers, setHeaders, 'JSON object. Use {{secret}} for a credential value. Content-Type is set to application/json.')}
            {editor.pagination !== 'None' && <><Form.Item label="First page"><Select value={editor.firstPage} options={[{ value: 0, label: '0' }, { value: 1, label: '1' }]} onChange={v => update('firstPage', v)} /></Form.Item><Form.Item label="Page size"><InputNumber min={1} max={1000} value={editor.pageSize} onChange={v => update('pageSize', v ?? 500)} /></Form.Item><Form.Item label="Maximum pages"><InputNumber min={1} max={100} value={editor.maxPages} onChange={v => update('maxPages', v ?? 20)} /></Form.Item><Alert type="info" message="Set the provider's page-size parameter to {{pageSize}}. Fetching stops at a short/empty page; reaching the limit prevents import." /></>}
          </div> },
          { key: 'mapping', label: 'Response mapping', children: <div className="adapter-form-grid">
            <Alert className="adapter-wide" type="info" message="Paths use exact JSON field names, e.g. Data or data.items. Use $ for a root array; a numeric segment selects an array item." />
            {textField('recordsPath', 'Records array path')}{textField('successPath', 'Success field path', 'Leave blank if the provider has no success flag.')}{textField('successValue', 'Expected success value')}{textField('employeePath', 'Employee / user ID path')}{textField('devicePath', 'Device ID path', 'Leave blank only when the API exclusively returns this machine.')}{textField('timePath', 'Punch timestamp path')}{textField('actionPath', 'IN/OUT field path')}{textField('punchIdPath', 'Unique punch ID path (optional)', 'When absent, identical device/user/time/direction records use the same generated ID.')}
            <Form.Item label="Vendor values meaning IN" help="Comma-separated, e.g. IN, CheckIn. Confirm numeric values with the vendor."><Input value={inValues} onChange={e => setInValues(e.target.value)} /></Form.Item>
            <Form.Item label="Vendor values meaning OUT" help="iDMS documents a sample 0, but does not define its meaning. Configure verified values."><Input value={outValues} onChange={e => setOutValues(e.target.value)} /></Form.Item>
            {textField('timeZoneId', 'Timezone for timestamps without an offset', 'For example Asia/Kolkata. Explicit offsets in the response take precedence.')}{textField('punchTimeFormat', 'Punch timestamp format (optional)', 'Leave blank for standard ISO timestamps; custom formats use .NET date/time notation.')}
            <div className="adapter-wide">{jsonField('Vendor user ID → HRMS employee code', employees, setEmployees, 'Optional JSON object, e.g. {"369839":"EMP001"}. Unlisted IDs are treated as employee codes and validated within the machine’s client/work location.')}</div>
          </div> },
          { key: 'schedule', label: 'Automatic sync', children: <div className="adapter-form-grid">
            <Form.Item label="Run every (minutes)" help="0 = manual only. Automatic intervals: 5–1440 minutes. Runs only while enabled."><InputNumber min={0} max={1440} value={editor.syncIntervalMinutes} onChange={v => update('syncIntervalMinutes', v ?? 0)} /></Form.Item>
            <Form.Item label="Look back (days)" help="Re-fetch recent days to catch delayed/offline machine logs. Duplicate punches are ignored."><InputNumber min={1} max={7} value={editor.lookbackDays} onChange={v => update('lookbackDays', v ?? 2)} /></Form.Item>
            <Alert className="adapter-wide" type="info" message="Save the adapter, run Test, verify employee and IN/OUT mapping, then enable syncing. Manual Test never imports attendance. Sync uses existing payroll locks and attendance rules." />
          </div> },
        ]} />
      </Form>}
    </Drawer>
    <Modal title={`${target?.preview ? 'Test' : 'Sync'} attendance API${target ? ` · ${target.row.name}` : ''}`} open={!!target} width="min(1000px, 96vw)" maskClosable={!busy} closable={!busy} onCancel={() => { if (!busy) setTarget(null) }} footer={<Space><Button disabled={busy} onClick={() => setTarget(null)}>Close</Button><Button type="primary" loading={busy} disabled={!fromDate || !toDate} onClick={() => void run()}>{target?.preview ? 'Test & preview' : 'Sync attendance'}</Button></Space>}>
      <div className="attendance-adapter-editor">
        <Alert type="info" showIcon message={target?.preview ? 'Fetches and maps data without changing attendance. Preview shows up to 20 punches.' : 'Imports matching punches through the registered machine. Accepted and duplicate records are reported separately.'} />
        <div className="adapter-form-grid"><label>From date<Input aria-label="From date" type="date" value={fromDate} onChange={e => setFromDate(e.target.value)} disabled={busy} /></label><label>To date<Input aria-label="To date" type="date" value={toDate} onChange={e => setToDate(e.target.value)} disabled={busy} /></label></div>
        {runError && <Alert type="error" showIcon message={runError} />}
        {result && <><Alert type={result.success ? 'success' : 'error'} showIcon message={result.message} description={`HTTP ${result.httpStatus ?? '—'} · ${result.fetched} fetched · ${result.mapped} mapped · ${result.accepted} accepted · ${result.duplicates} duplicates · ${result.rejected} rejected · ${(result.durationMs / 1000).toFixed(1)}s`} />
          {!!result.errors.length && <ul className="adapter-errors">{result.errors.map((e, i) => <li key={i}>{e}</li>)}</ul>}
          {!!result.samples.length && <DataTable rows={result.samples} columns={previewColumns} getRowId={r => r.punchId} title="Mapped punch preview" exportFileName="attendance-api-preview" />}
        </>}
      </div>
    </Modal>
  </RecruitmentViewContext.Provider>
}
