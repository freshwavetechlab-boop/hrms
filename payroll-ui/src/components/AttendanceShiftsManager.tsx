import { useEffect, useState } from 'react'
import { PlusOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Col, Drawer, Form, Input, InputNumber, Popconfirm, Row, Select, Space, Switch } from 'antd'
import { getAttendanceShifts, saveAttendanceShift, deactivateAttendanceShift } from '../services/leaveAttendanceService'
import type { AttendanceShift } from '../types/payroll'
import { useAuthSession } from './AuthGate'
import { useToast } from './ToastProvider'
import { PageHeaderPortal } from './layout/AppPageHeader'
import RecruitmentRecordList from './RecruitmentRecordList'

const today = () => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}` }
const blank = (clientId: number): AttendanceShift => ({ id: 0, clientId, shiftCode: '', shiftName: '', shiftType: 'Fixed', startTime: '09:00:00', endTime: '18:00:00', isOvernight: false, graceMinutes: 0, breakMinutes: 0, minimumFullDayHours: 8, minimumHalfDayHours: 4, effectiveFrom: today(), effectiveTo: null, isActive: true })
const time = (value?: string | null) => value?.slice(0, 5) || ''

export default function AttendanceShiftsManager({ clientId, clientName }: { clientId: number; clientName: string }) {
  const session = useAuthSession(), toast = useToast()
  const canManage = !!session?.user.permissions.some(permission => ['settings.manage', 'client.settings.manage'].includes(permission))
  const [rows, setRows] = useState<AttendanceShift[]>([]), [loading, setLoading] = useState(true)
  const [form, setForm] = useState<AttendanceShift>(() => blank(clientId)), [open, setOpen] = useState(false), [saving, setSaving] = useState(false), [error, setError] = useState('')
  useEffect(() => {
    let active = true; setOpen(false); setRows([]); setLoading(true)
    void getAttendanceShifts(clientId).then(data => { if (active) { setRows(data); setLoading(false) } })
    return () => { active = false }
  }, [clientId])
  const set = <K extends keyof AttendanceShift>(key: K, value: AttendanceShift[K]) => { setError(''); setForm(current => ({ ...current, [key]: value })) }
  const edit = (shift?: AttendanceShift) => { setForm(shift ? { ...shift, effectiveFrom: shift.effectiveFrom.slice(0, 10), effectiveTo: shift.effectiveTo?.slice(0, 10) || null } : blank(clientId)); setError(''); setOpen(true) }
  const save = async () => {
    if (saving || form.clientId !== clientId) return
    if (!form.shiftCode.trim() || !form.shiftName.trim()) return setError('Shift code and shift name are required.')
    if ((form.shiftType === 'Fixed' || form.isOvernight) && (!form.startTime || !form.endTime)) return setError('Start/end times are required for fixed or overnight shifts.')
    if (form.minimumHalfDayHours <= 0 || form.minimumFullDayHours <= form.minimumHalfDayHours || form.minimumFullDayHours > 24) return setError('Full-day hours must exceed positive half-day hours and cannot exceed 24.')
    if (!form.effectiveFrom || form.effectiveTo && form.effectiveTo < form.effectiveFrom) return setError('Effective-to cannot be before effective-from; effective-from is required.')
    setSaving(true)
    try {
      const response = await saveAttendanceShift(form)
      if (!response.ok) return setError(response.error || 'Unable to save shift.')
      setOpen(false); setRows(await getAttendanceShifts(clientId)); toast('Shift saved.', 'success')
    } finally { setSaving(false) }
  }
  const deactivate = async (shift: AttendanceShift) => {
    const response = await deactivateAttendanceShift(shift.id, clientId)
    if (response.ok) { setRows(await getAttendanceShifts(clientId)); toast('Shift deactivated.', 'success') }
  }
  return <section className="attendance-groups">
    {canManage && <PageHeaderPortal slot="attendance-settings-page-controls"><Button type="primary" icon={<PlusOutlined />} onClick={() => edit()}>Add shift</Button></PageHeaderPortal>}
    <Card className="settings-panel settings-master-list-card" size="small"><RecruitmentRecordList rows={rows.filter(row => row.clientId === clientId)} loading={loading} title={row => row.shiftName} subtitle={row => row.shiftCode} searchPlaceholder="Search shifts by code or name" exportFileName="attendance-shifts" emptyText="No shifts configured for this client."
      filters={[{ key: 'type', label: 'Shift type', value: row => row.shiftType }, { key: 'active', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }]}
      columns={[{ key: 'shiftCode', label: 'Shift code' }, { key: 'shiftName', label: 'Shift name' }, { key: 'shiftType', label: 'Type' }, { key: 'startTime', label: 'Start', value: row => time(row.startTime) || '-' }, { key: 'endTime', label: 'End', value: row => time(row.endTime) || '-' }, { key: 'isOvernight', label: 'Overnight', value: row => row.isOvernight ? 'Yes' : 'No' }, { key: 'graceMinutes', label: 'Grace (min)' }, { key: 'breakMinutes', label: 'Break (min)' }, { key: 'minimumFullDayHours', label: 'Full-day hours' }, { key: 'minimumHalfDayHours', label: 'Half-day hours' }, { key: 'effectiveFrom', label: 'Effective from', value: row => row.effectiveFrom.slice(0, 10) }, { key: 'effectiveTo', label: 'Effective to', value: row => row.effectiveTo?.slice(0, 10) || 'No end date' }, { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }]}
      cardSummaryColumns={['shiftType', 'startTime', 'endTime', 'minimumFullDayHours', 'minimumHalfDayHours', 'isActive']}
      actions={canManage ? row => <Space><Button size="small" onClick={() => edit(row)}>Edit</Button>{row.isActive && <Popconfirm title="Deactivate shift?" description="Assigned employees will use the client default or previous attendance rules." onConfirm={() => deactivate(row)}><Button size="small" danger>Deactivate</Button></Popconfirm>}</Space> : undefined} /></Card>
    <Drawer className="settings-master-drawer" title={form.id ? 'Edit shift' : 'Add shift'} width="min(720px, 96vw)" open={open} onClose={() => !saving && setOpen(false)} closable={!saving} destroyOnClose footer={<Space><Button disabled={saving} onClick={() => setOpen(false)}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void save()}>Save shift</Button></Space>}>
      <Form layout="vertical" className="settings-quick-form" disabled={saving}>
        {error && <Alert type="error" showIcon message={error} />}
        <Form.Item label="Client" required><Input readOnly value={clientName} /></Form.Item>
        <Row gutter={12}><Col xs={24} md={12}><Form.Item label="Shift code" required><Input aria-label="Shift code" maxLength={50} value={form.shiftCode} onChange={event => set('shiftCode', event.target.value)} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Shift name" required><Input aria-label="Shift name" maxLength={180} value={form.shiftName} onChange={event => set('shiftName', event.target.value)} /></Form.Item></Col></Row>
        <Form.Item label="Shift type"><Select aria-label="Shift type" value={form.shiftType} options={[{ value: 'Fixed', label: 'Fixed' }, { value: 'Flexible', label: 'Flexible' }]} onChange={value => set('shiftType', value)} /></Form.Item>
        <Row gutter={12}><Col xs={24} md={12}><Form.Item label="Start time" required={form.shiftType === 'Fixed' || form.isOvernight}><Input aria-label="Start time" type="time" value={time(form.startTime)} onChange={event => set('startTime', event.target.value ? `${event.target.value}:00` : null)} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="End time" required={form.shiftType === 'Fixed' || form.isOvernight}><Input aria-label="End time" type="time" value={time(form.endTime)} onChange={event => set('endTime', event.target.value ? `${event.target.value}:00` : null)} /></Form.Item></Col></Row>
        <Form.Item label="Overnight shift"><Switch aria-label="Overnight shift" checked={form.isOvernight} onChange={value => set('isOvernight', value)} /></Form.Item>
        <Row gutter={12}>{(['graceMinutes', 'breakMinutes', 'minimumFullDayHours', 'minimumHalfDayHours'] as const).map(key => <Col xs={24} md={12} key={key}><Form.Item label={{ graceMinutes: 'Grace minutes', breakMinutes: 'Break minutes', minimumFullDayHours: 'Minimum full-day hours', minimumHalfDayHours: 'Minimum half-day hours' }[key]} required><InputNumber aria-label={key} min={0} max={key.endsWith('Minutes') ? 1440 : 24} step={key.endsWith('Minutes') ? 1 : .25} precision={key.endsWith('Minutes') ? 0 : 2} value={form[key]} onChange={value => set(key, Number(value ?? 0))} style={{ width: '100%' }} /></Form.Item></Col>)}</Row>
        <Row gutter={12}><Col xs={24} md={12}><Form.Item label="Effective from" required><Input aria-label="Effective from" type="date" value={form.effectiveFrom} onChange={event => set('effectiveFrom', event.target.value)} /></Form.Item></Col><Col xs={24} md={12}><Form.Item label="Effective to"><Input aria-label="Effective to" type="date" value={form.effectiveTo || ''} onChange={event => set('effectiveTo', event.target.value || null)} /></Form.Item></Col></Row>
        <Form.Item label="Active"><Switch aria-label="Active" checked={form.isActive} onChange={value => set('isActive', value)} /></Form.Item>
      </Form>
    </Drawer>
  </section>
}
