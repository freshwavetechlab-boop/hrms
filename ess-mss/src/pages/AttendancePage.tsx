import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Card, Drawer, Form, Input, Segmented, Select, Space, Table, Tag } from 'antd'
import type { DailyAttendance, User, View } from '../types'
import { essApi, essFetch } from '../services/essApi'
import { showToast } from '../utils/ui'
import './AttendancePage.css'

type Options = { settings: { id: number; allowRegularizationRequests: boolean; rules: { monthlyMissPunchLimit: number | null; monthlyOdLimit: number | null } }; leaveTypes: { code: string; name: string; allowHalfDay: boolean }[] }

export function AttendancePage({ user, setView }: { user: User; setView: (view: View) => void }) {
  const [month, setMonth] = useState(() => new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Kolkata' }).format(new Date()).slice(0, 7))
  const [rows, setRows] = useState<DailyAttendance[]>([])
  const [options, setOptions] = useState<Options | null>(null)
  const [view, setListView] = useState(() => sessionStorage.getItem(`attendance.view:${user.id}`) || 'Table')
  const [error, setError] = useState('')
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [query, setQuery] = useState('')
  const [kind, setKind] = useState('MissPunch')
  const [form] = Form.useForm()
  const leaveCode = Form.useWatch('leaveCode', form)
  const body = useRef<HTMLDivElement>(null)
  const [height, setHeight] = useState(300)
  const load = async () => {
    setBusy(true)
    try {
      const response = await essFetch(`/api/ess/attendance/history?month=${month}&scope=calendar-month`)
      if (!response.ok) throw new Error('Unable to load attendance.')
      const data = await response.json() as { records: DailyAttendance[] }; setRows(data.records); setError('')
      const config = await essFetch('/api/ess/attendance/regularization-options'); if (!config.ok) throw new Error('Unable to load regularization policy.'); setOptions(await config.json())
    } catch (err) { setError(err instanceof Error ? err.message : 'Unable to load attendance.') } finally { setBusy(false) }
  }
  useEffect(() => { void load() }, [month, user.id])
  useEffect(() => { sessionStorage.setItem(`attendance.view:${user.id}`, view) }, [view, user.id])
  useEffect(() => { if (!body.current) return; const observer = new ResizeObserver(([entry]) => setHeight(Math.max(120, entry.contentRect.height - 112))); observer.observe(body.current); return () => observer.disconnect() }, [view])
  const request = (date?: string) => { form.resetFields(); setKind('MissPunch'); form.setFieldsValue({ fromDate: date?.slice(0, 10), toDate: date?.slice(0, 10), leaveCode: options?.leaveTypes[0]?.code, dayType: 'Full Day' }); setOpen(true) }
  const submit = async (values: Record<string, string>) => {
    setBusy(true)
    try { await essApi.createLeaveRequest({ leaveCode: values.leaveCode, fromDate: values.fromDate, dayType: values.dayType || 'Full Day', reason: values.reason, toDate: kind === 'MissPunch' ? values.fromDate : values.toDate || values.fromDate, regularizationKind: kind, checkInTime: kind === 'MissPunch' ? values.checkInTime + ':00' : undefined, checkOutTime: kind === 'MissPunch' ? values.checkOutTime + ':00' : undefined }); setOpen(false); showToast('Request sent to your configured approver.', 'success'); await load() }
    catch (err) { showToast(err instanceof Error ? err.message : 'Unable to submit request.', 'error') } finally { setBusy(false) }
  }
  const allowed = Boolean(options?.settings.id && options.settings.allowRegularizationRequests && options.leaveTypes.length)
  const limit = kind === 'MissPunch' ? options?.settings.rules.monthlyMissPunchLimit : options?.settings.rules.monthlyOdLimit
  const visible = rows.filter(row => (!query || `${row.attendanceDate} ${row.status} ${row.remarks}`.toLowerCase().includes(query.toLowerCase())) && (view === 'Table' || !status || row.status === status))
  return <section className="ess-attendance-workspace">
    <div className="ess-attendance-controls"><Space wrap><Segmented value={view} options={[{ label: 'Card view', value: 'Cards' }, { label: 'Table view', value: 'Table' }]} onChange={value => setListView(String(value))} /><Input aria-label="Attendance month" type="month" value={month} onChange={e => setMonth(e.target.value)} /></Space><Space wrap><Button onClick={() => setView('Leave')}>Request history</Button><Button type="primary" disabled={!allowed} onClick={() => request()}>Request regularization</Button></Space></div>
    {error && <Alert type="error" showIcon message={error} />}
    {!allowed && options && <Alert type="warning" message="HR needs to configure attendance settings and a Mark as present request type before regularization is available." />}
    <div className="ess-attendance-records-layout">
      <div className="ess-attendance-records"><Input.Search allowClear placeholder="Search attendance" value={query} onChange={e => setQuery(e.target.value)} />
        <div className="ess-attendance-records-body" ref={body}>{view === 'Table' ? <Table rowKey="attendanceDate" loading={busy} dataSource={visible} scroll={{ x: 750, y: height }} pagination={{ pageSize: 31, showSizeChanger: false }} columns={[
          { title: 'Date', dataIndex: 'attendanceDate', render: (value: string) => value.slice(0, 10), sorter: (a, b) => a.attendanceDate.localeCompare(b.attendanceDate) },
          { title: 'Status', dataIndex: 'status', filters: [...new Set(rows.map(r => r.status))].map(value => ({ text: value, value })), onFilter: (value, row) => row.status === value },
          { title: 'In', dataIndex: 'checkInTime', render: (value?: string) => value?.slice(0, 5) || 'Missing' }, { title: 'Out', dataIndex: 'checkOutTime', render: (value?: string) => value?.slice(0, 5) || 'Missing' },
          { title: 'Hours', dataIndex: 'totalHours', sorter: (a, b) => (a.totalHours || 0) - (b.totalHours || 0) }, { title: 'Payable', dataIndex: 'payableValue' }, { title: 'Details', dataIndex: 'remarks' },
          { title: 'Action', fixed: 'right', width: 130, render: (_, row) => <Button size="small" disabled={!allowed} onClick={() => request(row.attendanceDate)}>Regularize</Button> },
        ]} /> : <div className="ess-attendance-cards">{visible.map(row => <Card size="small" key={row.attendanceDate}><Space><b>{row.attendanceDate.slice(0, 10)}</b><Tag>{row.status}</Tag><Tag>{row.payableValue} payable</Tag></Space><p>In: {row.checkInTime?.slice(0, 5) || 'Missing'} · Out: {row.checkOutTime?.slice(0, 5) || 'Missing'} · {row.totalHours || 0} hours</p><p>{row.remarks}</p><Button size="small" disabled={!allowed} onClick={() => request(row.attendanceDate)}>Regularize</Button></Card>)}{!visible.length && <p>No attendance recorded for this month.</p>}</div>}</div>
      </div>
      {view === 'Cards' && <aside className="ess-attendance-filters"><b>Quick filters</b><Select allowClear placeholder="All statuses" value={status || undefined} options={[...new Set(rows.map(r => r.status))].map(value => ({ value, label: value }))} onChange={value => setStatus(value || '')} /><Button onClick={() => { setStatus(''); setQuery('') }}>Reset filters</Button></aside>}
    </div>
    <Drawer title="Request attendance regularization" open={open} width="min(500px, 96vw)" onClose={() => setOpen(false)}>
      {limit == null && <Alert type="warning" message={`Ask HR to configure the monthly ${kind} request limit before submitting.`} />}
      <Form form={form} layout="vertical" onFinish={values => void submit(values)}>
        <Form.Item label="Reason type"><Select value={kind} onChange={setKind} options={[{ label: 'Miss punch / time correction', value: 'MissPunch' }, { label: 'Outdoor duty (OD)', value: 'OD' }]} /></Form.Item>
        <Form.Item name="leaveCode" label="Attendance request type" rules={[{ required: true }]}><Select options={options?.leaveTypes.map(type => ({ value: type.code, label: type.name }))} /></Form.Item>
        <Form.Item name="fromDate" label="Attendance date" rules={[{ required: true }]}><Input type="date" /></Form.Item>
        {kind === 'OD' && <Form.Item name="toDate" label="To date"><Input type="date" /></Form.Item>}
        {kind === 'OD' && options?.leaveTypes.find(type => type.code === leaveCode)?.allowHalfDay && <Form.Item name="dayType" label="Day type"><Select options={[{ label: 'Full day', value: 'Full Day' }, { label: 'First half', value: 'First Half' }, { label: 'Second half', value: 'Second Half' }]} /></Form.Item>}
        {kind === 'MissPunch' && <><Form.Item name="checkInTime" label="Correct check-in time" rules={[{ required: true }]}><Input type="time" /></Form.Item><Form.Item name="checkOutTime" label="Correct check-out time" rules={[{ required: true }]}><Input type="time" /></Form.Item></>}
        <Form.Item name="reason" label="Reason" rules={[{ required: true }]}><Input.TextArea maxLength={1000} /></Form.Item>
        <Button type="primary" htmlType="submit" loading={busy} disabled={!allowed || limit == null || limit === 0}>Submit for approval</Button>
      </Form>
    </Drawer>
  </section>
}
