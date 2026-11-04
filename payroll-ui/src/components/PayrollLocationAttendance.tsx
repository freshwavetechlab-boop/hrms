import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Drawer, Input, Space, Spin, Tag } from 'antd'
import { DownloadOutlined, ReloadOutlined, UploadOutlined } from '@ant-design/icons'
import SearchSelect from './SearchSelect'
import DataTable from './DataTable'
import ManualAttendanceManager from './ManualAttendanceManager'
import { useAuthSession } from './AuthGate'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { getPayrollLocationAttendance, savePayrollLocationAttendance, type LocationAttendanceContext, type LocationAttendanceRequest } from '../services/payrollLocationService'
import { buildLocationAttendanceWorkbook, payrollLocationOptions, readLocationAttendanceUpload } from '../utils/payrollLocationAttendance'
import { parseImportPreviewSheets } from '../utils/importPreview'
import { downloadXlsx } from '../utils/xlsx'
import type { AttendanceGroup } from '../types/payroll'
import type { ToastType } from './ToastProvider'
import './PayrollLocationFlow.css'

type Props = { clientId: number; group: AttendanceGroup; groupIds: number[]; policyBatchId: string; reviewMonth: string; initialLocationId: number; clientControl: ReactNode; onMessage: (message: string, type?: ToastType) => void }
const currentMonth = () => { const date = new Date(); return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}` }

export default function PayrollLocationAttendance(p: Props) {
  const session = useAuthSession()
  const [month, setMonth] = useState(p.reviewMonth || currentMonth())
  const [locationId, setLocationId] = useSessionPreference(`attendance.location:${session?.user.id}:${p.clientId}:${p.policyBatchId || p.groupIds.join(',')}`, 0)
  const [context, setContext] = useState<LocationAttendanceContext | null>(null)
  const [legacyApi, setLegacyApi] = useState(false)
  const [error, setError] = useState(''), [loading, setLoading] = useState(false), [saving, setSaving] = useState(false)
  const [draft, setDraft] = useState<LocationAttendanceRequest | null>(null)
  const sequence = useRef(0), busy = useRef(false)
  const scope = JSON.stringify([p.clientId, month, p.policyBatchId, p.groupIds])
  const activeScope = useRef(scope)
  const [revision, setRevision] = useState(0)
  useEffect(() => { if (p.reviewMonth) setMonth(p.reviewMonth) }, [p.reviewMonth])
  useEffect(() => { if (p.initialLocationId > 0) setLocationId(p.initialLocationId) }, [p.initialLocationId]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const id = ++sequence.current
    setLoading(true); setContext(null); setLegacyApi(false); setDraft(null); setError('')
    void getPayrollLocationAttendance(p.clientId, month, p.policyBatchId, p.groupIds).then(result => {
      if (sequence.current !== id) return
      setLoading(false)
      if (result.ok && result.data) setContext(result.data)
      else if (result.status === 404 || result.status === 501) setLegacyApi(true)
      else setError(result.error || 'Location attendance is unavailable. Refresh before importing.')
    })
    return () => { sequence.current += 1 }
  }, [scope, revision]) // eslint-disable-line react-hooks/exhaustive-deps
  const options = useMemo(() => payrollLocationOptions(context), [context])
  const selectedLocation = options.some(option => option.value === locationId) ? locationId : 0
  activeScope.current = JSON.stringify([scope, selectedLocation, context?.version])
  const rows = context?.rows.filter(row => !selectedLocation || row.workLocationId === selectedLocation) || []
  const monthly = p.group.attendanceMode === 'Monthly' || context?.rows.some(row => row.nativePart)
  const importFile = async (file: File) => {
    if (!context || busy.current) return
    const requested = activeScope.current, version = context.version
    setError('')
    try {
      const sheets = await parseImportPreviewSheets(file)
      const sheet = sheets.find(sheet => sheet.name === 'Attendance') || sheets[0]
      if (!sheet) throw Error('No attendance worksheet found.')
      const input = readLocationAttendanceUpload(sheet, context, selectedLocation)
      if (activeScope.current !== requested || context.version !== version) return
      setDraft({ requestId: crypto.randomUUID().replaceAll('-', ''), clientId: p.clientId, month, policyBatchId: p.policyBatchId,
        groupIds: [...p.groupIds], workLocationId: selectedLocation, contextVersion: version, rows: input })
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to read this attendance template.') }
  }
  const apply = async () => {
    if (!draft || busy.current) return
    const requested = activeScope.current
    busy.current = true; setSaving(true)
    try {
      const result = await savePayrollLocationAttendance(draft)
      if (activeScope.current !== requested) return
      if (!result.ok || !result.data) throw Error(result.error || 'Save could not be confirmed. Retry this same review to check its receipt.')
      setContext(result.data.context); setDraft(null)
      p.onMessage(`${result.data.savedParts} location rows saved; ${result.data.publishedEmployees} employee totals ready.${result.data.pendingEmployees ? ` ${result.data.pendingEmployees} employees still need another location.` : ''}`)
    } catch (e) { setError(e instanceof Error ? e.message : 'Attendance save could not be confirmed.') }
    finally { busy.current = false; setSaving(false) }
  }
  const controls = <>{p.clientControl}<label className="attendance-client-control attendance-scope-field"><span>Location</span><SearchSelect value={selectedLocation} disabled={loading || saving || !context} options={options} onChange={value => { setLocationId(Number(value)); setDraft(null) }} /></label></>
  if (legacyApi) return <ManualAttendanceManager clientId={p.clientId} group={p.group} reviewMonth={month} onReviewMonthChange={setMonth} clientControl={p.clientControl} onMessage={p.onMessage} />
  if (context && (!monthly || !context.storageAvailable && !context.rows.some(row => row.nativePart))) return <ManualAttendanceManager clientId={p.clientId} group={{ ...p.group, workLocationId: selectedLocation,
    employeeIds: rows.map(row => row.employeeId) }} reviewMonth={month} onReviewMonthChange={setMonth} clientControl={controls} onMessage={p.onMessage} />
  return <section className="card payroll-location-attendance" style={{ display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 }}>
    <Space wrap style={{ padding: 12 }}>
      {controls}<label className="attendance-client-control"><span>Payroll month</span><Input type="month" value={month} disabled={saving} onChange={e => { setMonth(e.target.value); setDraft(null) }} /></label>
      <Button aria-label="Download template" icon={<DownloadOutlined />} disabled={!context || loading || saving || !rows.length} onClick={() => {
        try { downloadXlsx(`location-attendance-${month}-${selectedLocation || 'all'}.xlsx`, buildLocationAttendanceWorkbook(context!, selectedLocation)) }
        catch (e) { setError(e instanceof Error ? e.message : 'Unable to download template.') }
      }}>Download template</Button>
      <label className="attendance-file-action ant-btn ant-btn-default"><input style={{ display: 'none' }} aria-label="Upload location attendance" type="file" accept=".xlsx,.csv" disabled={!context?.storageAvailable || loading || saving} onChange={e => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void importFile(f) }} /><UploadOutlined />Upload &amp; review</label>
      <Button aria-label="Refresh location attendance" icon={<ReloadOutlined />} disabled={saving} onClick={() => setRevision(v => v + 1)} />
      <Tag>{rows.length} location parts</Tag>
    </Space>
    {error && <Alert type="error" showIcon message={error} />}
    {context && !context.storageAvailable && <Alert type="warning" showIcon message="Location attendance save needs the additive database migration." />}
    {rows.some(row => row.nativePart) && <Alert type="info" showIcon message="Each location keeps its wages and days. Employee monthly totals become ready after every location part is recorded." />}
    {loading ? <Spin /> : <DataTable rows={rows} fillHeight getRowId={row => row.key} exportDisabled columns={[
      { key: 'employeeName', label: 'Employee', value: row => `${row.employeeName} ${row.employeeCode}`, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> },
      { key: 'workLocationName', label: 'Location' }, { key: 'templateName', label: 'Salary template', width: 240 },
      { key: 'payrollDays', label: 'Payroll days' }, { key: 'presentDays', label: 'Present', value: row => row.presentDays ?? 'Pending' },
      { key: 'payableDays', label: 'Payable', value: row => row.payableDays ?? 'Pending' },
      { key: 'status', label: 'Status', value: row => row.lockedPayRunId ? `Saved payroll #${row.lockedPayRunId}` : row.issue || (row.recorded ? 'Recorded' : 'Pending') },
    ]} />}
    <Drawer title="Review location attendance" open={!!draft} onClose={() => { if (!saving) setDraft(null) }} width="min(820px,96vw)" maskClosable={!saving} closable={!saving}
      footer={<Space><Button disabled={saving} onClick={() => setDraft(null)}>Close</Button><Button type="primary" loading={saving} onClick={() => void apply()}>Apply attendance ({draft?.rows.length || 0})</Button></Space>}>
      <p>{month} · {draft?.rows.length || 0} reviewed location rows. Salary templates and saved payroll are retained.</p>
      {error && <Alert type="error" showIcon message={error} />}
      <DataTable rows={draft?.rows || []} getRowId={row => row.key} exportDisabled columns={[
        { key: 'employeeId', label: 'Employee', value: row => context?.rows.find(r => r.key === row.key)?.employeeCode },
        { key: 'workLocationId', label: 'Location', value: row => context?.rows.find(r => r.key === row.key)?.workLocationName },
        { key: 'payrollDays', label: 'Payroll days' }, { key: 'presentDays', label: 'Present' }, { key: 'payableDays', label: 'Payable' },
      ]} />
    </Drawer>
  </section>
}
