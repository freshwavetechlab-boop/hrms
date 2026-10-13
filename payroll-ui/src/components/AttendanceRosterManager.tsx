import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Col, Form, Input, Popconfirm, Row, Select, Space, Table } from 'antd'
import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import type { AttendanceShift, AttendanceShiftAssignment } from '../shared/attendanceShift'
import { deleteJson, getJsonResult, postJson } from '../services/apiClient'
import AttendanceFieldHelp from './AttendanceFieldHelp'
import './AttendanceScheduling.css'

const today = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` }
type Person = { id: number; code: string; name: string }
export default function AttendanceRosterManager({ clientId, shifts }: { clientId: number; shifts: AttendanceShift[] }) {
  const [from, setFrom] = useState(today), [to, setTo] = useState(today), [shiftId, setShiftId] = useState<number>(), [days, setDays] = useState([0, 1, 2, 3, 4, 5, 6])
  const [reason, setReason] = useState(''), [search, setSearch] = useState(''), [people, setPeople] = useState<Person[]>([]), [ids, setIds] = useState<number[]>([])
  const [rows, setRows] = useState<AttendanceShiftAssignment[]>([]), [error, setError] = useState(''), [message, setMessage] = useState(''), [busy, setBusy] = useState(false)
  const lookup = useRef(0)
  useEffect(() => () => { lookup.current++ }, [])
  const path = `/api/leave-attendance/shift-assignments`
  const load = async () => {
    if (!from || !to || to < from) { setError('Select a valid period.'); return }
    setBusy(true); setError(''); const result = await getJsonResult<AttendanceShiftAssignment[]>(`${path}?${new URLSearchParams({ clientId: String(clientId), from, to })}`, []); setBusy(false)
    if (result.ok) setRows(result.data); else { setRows([]); setError(result.error || 'Unable to load roster.') }
  }
  const find = async () => {
    if (!shiftId) return
    const version = ++lookup.current; setError('')
    const result = await getJsonResult<Person[]>(`${path}/employees?${new URLSearchParams({ clientId: String(clientId), shiftId: String(shiftId), search })}`, [])
    if (version !== lookup.current) return
    if (result.ok) setPeople(result.data); else setError(result.error || 'Unable to load eligible employees.')
  }
  const save = async () => {
    if (!shiftId || !ids.length || !days.length || !reason.trim() || !from || !to || to < from) { setError('Select shift, dates, weekdays and employees, and enter a reason.'); return }
    setBusy(true); setError(''); setMessage('')
    const result = await postJson(path, { clientId, shiftId, employeeIds: ids, effectiveFrom: from, effectiveTo: to, weekDays: [...days].sort().join(','), reason }, { assigned: 0 })
    setBusy(false)
    if (!result.ok) { setError(result.error || 'Unable to save roster.'); return }
    setMessage(`${result.data.assigned} employee assignments saved.`); setIds([]); await load()
  }
  const cancel = async (id: number) => {
    setBusy(true); setError(''); const result = await deleteJson(`${path}/${id}?clientId=${clientId}`, null); setBusy(false)
    if (result.ok) await load(); else setError(result.error || 'Unable to cancel assignment.')
  }
  const weekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
  return <Form component="div" className="settings-quick-form attendance-roster-form" layout="vertical" disabled={busy}>
    {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 12 }} />}{message && <Alert type="success" message={message} style={{ marginBottom: 12 }} />}
    <Row gutter={12}><Col xs={24} md={8}><Form.Item label="From date"><Input aria-label="Roster from date" type="date" value={from} onChange={e => { setFrom(e.target.value); setRows([]) }} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="To date"><Input aria-label="Roster to date" type="date" value={to} onChange={e => { setTo(e.target.value); setRows([]) }} /></Form.Item></Col><Col xs={24} md={8}><Form.Item label="Assignments"><Button icon={<ReloadOutlined />} onClick={() => void load()}>View roster</Button></Form.Item></Col></Row>
    <Form.Item label={<AttendanceFieldHelp label="Assign shift" text="Overrides the policy default for selected dates/weekdays. Employees must have a punch-based policy that allows this shift. Existing attendance and processed payroll are protected." example="Assign Night for 1-7 November, then Day for 8-14 November. For split duty, use one shift with split working intervals." />}><Select aria-label="Roster shift" value={shiftId} showSearch optionFilterProp="label" options={shifts.filter(s => s.isActive && s.clientId === clientId).map(s => ({ value: s.id, label: `${s.shiftCode} - ${s.shiftName}` }))} onChange={id => { lookup.current++; setShiftId(id); setIds([]); setPeople([]) }} /></Form.Item>
    <Form.Item label="Applicable weekdays"><Checkbox.Group options={weekdays.map((label, value) => ({ label, value }))} value={days} onChange={values => setDays(values as number[])} /></Form.Item>
    <Form.Item label={<AttendanceFieldHelp label="Eligible employees" text="Searches employees whose attendance policy allows the selected shift. Up to 200 matches are listed; narrow the search for larger teams. Assignment is saved only after Assign is clicked." example="Search an employee code, select employees, and assign the same shift to the team." />}><Space.Compact block><Input aria-label="Roster employee search" value={search} onChange={e => setSearch(e.target.value)} onPressEnter={() => void find()} placeholder="Employee code or name" /><Button disabled={!shiftId} icon={<SearchOutlined />} onClick={() => void find()}>Find employees</Button></Space.Compact></Form.Item>
    <Table size="small" rowKey="id" dataSource={people} pagination={{ pageSize: 10 }} rowSelection={{ selectedRowKeys: ids, preserveSelectedRowKeys: true, onChange: keys => setIds(keys as number[]) }} columns={[{ title: 'Employee code', dataIndex: 'code' }, { title: 'Employee name', dataIndex: 'name' }]} />
    <Form.Item label="Reason" required><Input aria-label="Roster reason" maxLength={500} value={reason} onChange={e => setReason(e.target.value)} /></Form.Item>
    <Button type="primary" loading={busy} disabled={!ids.length} onClick={() => void save()}>Assign ({ids.length})</Button>
    <Table style={{ marginTop: 20 }} size="small" rowKey="id" dataSource={rows} scroll={{ x: 900 }} pagination={{ pageSize: 10 }} columns={[
      { title: 'Employee', render: (_, r) => `${r.employeeCode} - ${r.employeeName}` }, { title: 'Shift', dataIndex: 'shiftName' },
      { title: 'From', render: (_, r) => r.effectiveFrom.slice(0, 10) }, { title: 'To', render: (_, r) => r.effectiveTo.slice(0, 10) },
      { title: 'Weekdays', render: (_, r) => r.weekDays.split(',').map(d => weekdays[Number(d)]).join(', ') }, { title: 'Reason', dataIndex: 'reason' },
      { title: 'Status', render: (_, r) => r.isActive ? 'Active' : 'Cancelled' }, { title: 'Action', render: (_, r) => r.isActive && <Popconfirm title="Cancel this assignment?" description="Saved attendance and payroll cannot be changed through roster cancellation." onConfirm={() => cancel(r.id)}><Button size="small" danger>Cancel</Button></Popconfirm> }
    ]} />
  </Form>
}
