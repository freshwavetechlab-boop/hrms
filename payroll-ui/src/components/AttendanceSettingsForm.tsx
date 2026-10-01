import { useEffect, useState } from 'react'
import { Alert, Button, Card as AntCard, Checkbox as AntCheckbox, Col, Divider, Form, Input, InputNumber, Row, Space } from 'antd'
import { getAttendanceSettings, saveAttendanceSettings } from '../services/leaveAttendanceService'
import type { AttendanceSettings } from '../types/payroll'
import SearchSelect, { selectOptions } from './SearchSelect'
import DataTable from './DataTable'
import AttendanceConfigurationGaps from './AttendanceConfigurationGaps'
import AttendanceDevices from './AttendanceDevices'

const ruleFields = [
  { id: 1, key: 'lateGraceMinutes', label: 'Late-coming grace (minutes)', max: 1440 },
  { id: 2, key: 'earlyGraceMinutes', label: 'Early-going grace (minutes)', max: 1440 },
  { id: 3, key: 'monthlyMissPunchLimit', label: 'Monthly miss-punch request limit', max: 31 },
  { id: 4, key: 'monthlyOdLimit', label: 'Monthly OD request limit', max: 31 },
] as const
const emptyRules = { lateGraceMinutes: null, earlyGraceMinutes: null, monthlyMissPunchLimit: null, monthlyOdLimit: null }

const initial: AttendanceSettings = { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 }

export default function AttendanceSettingsForm({ clientId, onSaved }: { clientId: number; onSaved: (message: string) => void }) {
  const [form, setForm] = useState<AttendanceSettings>(initial), [errors, setErrors] = useState<string[]>([]), [saving, setSaving] = useState(false)
  useEffect(() => { let active = true; void getAttendanceSettings(clientId).then(data => { if (active) setForm(normalizeTimes(data)) }); return () => { active = false } }, [clientId])
  const set = <K extends keyof AttendanceSettings>(key: K, value: AttendanceSettings[K]) => { setErrors([]); setForm(current => ({ ...current, [key]: value })) }
  const validate = () => {
    const next: string[] = []
    if (!form.checkInTime || !form.checkOutTime) next.push('Check-in and check-out time are required.')
    if (form.checkOutTime === form.checkInTime) next.push('Check-in and check-out times must be different.')
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
  return <AntCard className="attendance-settings settings-panel settings-form-panel" size="small" title="Attendance Management">
    <Form className="settings-quick-form" component={false} layout="vertical" requiredMark={false}>
      {errors.length > 0 && <Alert type="error" showIcon message={errors.join(' ')} />}
      <AttendanceConfigurationGaps clientId={clientId} />
      <Divider orientation="left">Work Shift Time</Divider>
      <Row gutter={12}>
        <Col xs={24} md={12}><Form.Item label="Check-in time" required><Input type="time" value={timeValue(form.checkInTime)} onChange={event => set('checkInTime', `${event.target.value}:00`)} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Check-out time" required><Input type="time" value={timeValue(form.checkOutTime)} onChange={event => set('checkOutTime', `${event.target.value}:00`)} /></Form.Item></Col>
      </Row>
      {form.checkOutTime < form.checkInTime && <Alert type="info" message="Overnight shift: punch out belongs to the shift's start date." />}
      <Divider orientation="left">Working Hours Calculation</Divider>
      <Row gutter={12}><Col xs={24} md={14}><Form.Item label="Calculation method"><SearchSelect value={form.workingHoursCalculation} onChange={value => set('workingHoursCalculation', value as AttendanceSettings['workingHoursCalculation'])} options={selectOptions(['First check-in and last check-out', 'Every valid check-in and check-out'])} /></Form.Item></Col></Row>
      <Divider orientation="left">Workday Duration</Divider>
      <Row gutter={12}>
        <Col xs={24} md={8}><Form.Item label="Minimum hours for half-day"><InputNumber step={0.25} value={form.minimumHoursForHalfDay} onChange={value => set('minimumHoursForHalfDay', Number(value || 0))} style={{ width: '100%' }} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Minimum hours for full-day"><InputNumber step={0.25} value={form.minimumHoursForFullDay} onChange={value => set('minimumHoursForFullDay', Number(value || 0))} style={{ width: '100%' }} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Maximum hours allowed for full-day"><InputNumber step={0.25} value={form.maximumHoursAllowedForFullDay} onChange={value => set('maximumHoursAllowedForFullDay', Number(value || 0))} style={{ width: '100%' }} /></Form.Item></Col>
      </Row>
      <Divider orientation="left">Regularization Settings</Divider>
      <DataTable rows={[...ruleFields]} columns={[
        { key: 'label', label: 'Client rule' },
        { key: 'key', label: 'Value', value: row => form.rules?.[row.key] ?? '', render: row => <InputNumber aria-label={row.label} min={0} max={row.max} value={form.rules?.[row.key]} placeholder="Not configured" onChange={value => set('rules', { ...emptyRules, ...form.rules, [row.key]: value })} /> },
        { key: 'max', label: 'Status', value: row => form.rules?.[row.key] == null ? 'Missing configuration' : 'Configured' },
      ]} exportFileName="client-attendance-rules" />
      <Row gutter={12}>
        <Check label="Allow regularization requests" value={form.allowRegularizationRequests} set={value => set('allowRegularizationRequests', value)} />
        <Col xs={24} md={12}><Form.Item label="Request window"><SearchSelect disabled={!form.allowRegularizationRequests} value={form.regularizationWindow} onChange={value => set('regularizationWindow', value as AttendanceSettings['regularizationWindow'])} options={selectOptions(['Anytime', 'Limited by past days'])} /></Form.Item></Col>
        {form.regularizationWindow === 'Limited by past days' && <Col xs={24} md={12}><Form.Item label="Number of past days allowed"><InputNumber disabled={!form.allowRegularizationRequests} value={form.pastDaysAllowed} onChange={value => set('pastDaysAllowed', Number(value || 0))} style={{ width: '100%' }} /></Form.Item></Col>}
        <Check label="Restrict regularization requests per month" value={form.restrictRegularizationRequestsPerMonth} set={value => set('restrictRegularizationRequestsPerMonth', value)} />
        {form.restrictRegularizationRequestsPerMonth && <Col xs={24} md={12}><Form.Item label="Max requests per month"><InputNumber disabled={!form.allowRegularizationRequests} value={form.maxRegularizationRequestsPerMonth} onChange={value => set('maxRegularizationRequestsPerMonth', Number(value || 0))} style={{ width: '100%' }} /></Form.Item></Col>}
      </Row>
      <Divider />
      <Row justify="end"><Space><Button type="primary" disabled={form.clientId !== clientId} loading={saving} onClick={() => void save()}>Save attendance settings</Button></Space></Row>
    </Form>
    <Divider orientation="left">Punch machines & work locations</Divider>
    <AttendanceDevices clientId={clientId} mode="settings" />
  </AntCard>
}

function Check({ label, value, set }: { label: string; value: boolean; set: (value: boolean) => void }) {
  return <Col xs={24} md={12}><Form.Item><AntCheckbox checked={value} onChange={event => set(event.target.checked)}>{label}</AntCheckbox></Form.Item></Col>
}

function timeValue(value: string) { return value?.slice(0, 5) || '' }
function normalizeTimes(settings: AttendanceSettings) {
  return { ...initial, ...settings, checkInTime: settings.checkInTime?.slice(0, 8) || initial.checkInTime, checkOutTime: settings.checkOutTime?.slice(0, 8) || initial.checkOutTime }
}
