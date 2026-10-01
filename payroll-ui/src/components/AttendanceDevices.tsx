import { useEffect, useState } from 'react'
import { Alert, Button, Checkbox, Drawer, Form, Input, InputNumber, Modal, Select, Space, Tag, Tooltip } from 'antd'
import { useNavigate, useSearchParams } from 'react-router-dom'
import DataTable from './DataTable'
import { getWorkLocations } from '../services/settingsService'
import { attendanceActionsChanged, generateAttendanceToken, getAttendanceDevices, revokeAttendanceToken, saveAttendanceDevice, type AttendanceDevice } from '../services/attendanceIntegrationService'
import type { WorkLocation } from '../types/payroll'

const tokenActive = (device: AttendanceDevice) => device.isActive && device.hasToken && !!device.tokenExpiresAt && new Date(device.tokenExpiresAt + (device.tokenExpiresAt.endsWith('Z') ? '' : 'Z')) > new Date()

export default function AttendanceDevices({ clientId, open = true, onClose = () => {}, mode = 'keys' }: { clientId: number; open?: boolean; onClose?: () => void; mode?: 'settings' | 'keys' }) {
  const blank: AttendanceDevice = { deviceId: '', name: '', clientId, workLocationId: 0, isActive: true }
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const [modal, contextHolder] = Modal.useModal()
  const [devices, setDevices] = useState<AttendanceDevice[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([])
  const [form, setForm] = useState(blank)
  const [days, setDays] = useState(90)
  const [selectedDevice, setSelectedDevice] = useState('')
  const [generateOpen, setGenerateOpen] = useState(false)
  const [generated, setGenerated] = useState<Record<string, string>>({})
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const refresh = async () => { const response = await getAttendanceDevices(clientId); setDevices(response.data); setError(response.error) }
  useEffect(() => {
    let active = true
    setForm(blank); setGenerated({}); setGenerateOpen(false); setError(''); setDevices([])
    if (open) {
      void getAttendanceDevices(clientId).then(response => { if (active) { setDevices(response.data); setError(response.error) } })
      if (mode === 'settings') void getWorkLocations().then(rows => { if (active) setLocations(rows) })
    }
    return () => { active = false }
  }, [open, clientId, mode])
  useEffect(() => { if (mode === 'settings' && params.get('devices') === '1') document.getElementById('attendance-devices')?.scrollIntoView({ block: 'start' }) }, [mode, clientId, params])
  const save = async () => { setBusy(true); const result = await saveAttendanceDevice({ ...form, clientId }); setBusy(false); if (result.ok) { setForm(blank); await refresh(); attendanceActionsChanged() } else setError(result.error) }
  const generate = async (device: AttendanceDevice) => {
    setBusy(true); const result = await generateAttendanceToken(device, days); setBusy(false)
    if (result.ok) { setGenerated(current => ({ ...current, [device.deviceId]: result.data.token })); setGenerateOpen(false); await refresh(); attendanceActionsChanged() } else setError(result.error)
  }
  const requestKey = (device: AttendanceDevice) => device.hasToken ? modal.confirm({ zIndex: 1200, title: 'Replace this API key?', content: 'The previous key for this machine will stop working. Update the machine with the new key.', okText: 'Generate new key', onOk: () => generate(device) }) : void generate(device)
  const revoke = (device: AttendanceDevice) => modal.confirm({ zIndex: 1200, title: 'Revoke this API key?', content: 'This machine will stop sending attendance until a new key is configured.', okText: 'Revoke key', okButtonProps: { danger: true }, onOk: async () => { setBusy(true); const result = await revokeAttendanceToken(device); setBusy(false); if (result.ok) { setGenerated(current => { const next = { ...current }; delete next[device.deviceId]; return next }); await refresh(); attendanceActionsChanged() } else setError(result.error) } })
  const activeDevices = devices.filter(device => device.isActive)
  const content = <>
    {contextHolder}
    {error && <Alert type="error" showIcon message={error} />}
    {mode === 'settings' ? <Form layout="vertical" onFinish={() => void save()}>
      <div className="grid">
        <Form.Item label="Machine / device ID" required><Input aria-label="Machine device ID" required maxLength={100} value={form.deviceId} onChange={e => setForm({ ...form, deviceId: e.target.value })} /></Form.Item>
        <Form.Item label="Device name" required><Input aria-label="Device name" required maxLength={120} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></Form.Item>
        <Form.Item label="Work location" required><Select aria-label="Device work location" value={form.workLocationId || undefined} showSearch optionFilterProp="label" options={locations.filter(l => l.clientId === clientId && l.isActive).map(l => ({ value: l.id, label: l.name }))} onChange={value => setForm({ ...form, workLocationId: value })} /></Form.Item>
      </div>
      <Space wrap><Checkbox checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })}>Device active</Checkbox><Button type="primary" htmlType="submit" loading={busy} disabled={!form.workLocationId}>Save device</Button><Button onClick={() => setForm(blank)}>New device</Button><Button onClick={() => navigate('/workflows/api-catalog?clientId=' + clientId + '&keys=1')}>Generate API key</Button></Space>
    </Form> : <>
      <p>Generate a key for your punch machine, then copy it from the table. The key authorizes attendance punches for that machine.</p>
      <Space wrap style={{ marginBottom: 12 }}><Button type="primary" loading={busy} disabled={!activeDevices.length} onClick={() => { setError(''); setSelectedDevice(activeDevices[0]?.deviceId || ''); setDays(90); setGenerateOpen(true) }}>Generate API key</Button><Button onClick={() => { onClose(); navigate('/settings/leave-attendance/attendance?clientId=' + clientId + '&devices=1') }}>Manage punch machines</Button></Space>
      {!activeDevices.length && <Alert type="info" showIcon message="Add a punch machine in Attendance settings, then generate its API key here." />}
      {Object.keys(generated).length > 0 && <Alert type="success" showIcon message="API key generated" description="The full key is visible in the table until you close this panel. Copy it for the machine; after closing, only the masked key is shown." />}
    </>}
    <DataTable rows={devices} getRowId={row => row.deviceId} columns={mode === 'settings' ? [
      { key: 'deviceId', label: 'Device ID' }, { key: 'name', label: 'Name' },
      { key: 'workLocationId', label: 'Work location', value: row => locations.find(l => l.id === row.workLocationId)?.name || String(row.workLocationId) },
      { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Disabled' },
    ] : [
      { key: 'name', label: 'Name' }, { key: 'deviceId', label: 'Machine' },
      { key: 'tokenHint', label: 'API key', value: row => row.tokenHint || (row.hasToken ? 'att_••••••' : 'Not generated'), render: row => <code className="attendance-api-key">{row.hasToken && generated[row.deviceId] || row.tokenHint || (row.hasToken ? 'att_••••••' : 'Not generated')}</code> },
      { key: 'tokenExpiresAt', label: 'Expires', value: row => row.tokenExpiresAt?.slice(0, 10) || '—' },
      { key: 'hasToken', label: 'Status', value: row => tokenActive(row) ? 'Active' : row.hasToken ? 'Expired / disabled' : 'No active key', render: row => <Tag color={tokenActive(row) ? 'green' : 'orange'}>{tokenActive(row) ? 'Active' : row.hasToken ? 'Expired / disabled' : 'No active key'}</Tag> },
    ]} actionsWidth={mode === 'settings' ? 100 : 230} actions={row => mode === 'settings' ? <Button size="small" onClick={() => setForm(row)}>Edit</Button> : <Space wrap>
      <Tooltip title={generated[row.deviceId] ? 'Copy full API key' : 'Full keys are shown once after generation'}><Button size="small" disabled={!generated[row.deviceId] || !row.hasToken} onClick={() => void navigator.clipboard.writeText(generated[row.deviceId]).catch(() => setError('Select and copy the displayed key manually.'))}>Copy</Button></Tooltip>
      <Button size="small" disabled={busy || !row.isActive} onClick={() => requestKey(row)}>{row.hasToken ? 'Replace' : 'Generate'}</Button><Button size="small" danger disabled={busy || !row.hasToken} onClick={() => revoke(row)}>Revoke</Button>
    </Space>} exportFileName={mode === 'settings' ? 'attendance-devices' : 'attendance-api-keys'} />
    <Modal zIndex={1200} title="Generate attendance API key" open={generateOpen} onCancel={() => setGenerateOpen(false)} onOk={() => { const device = activeDevices.find(row => row.deviceId === selectedDevice); if (device) requestKey(device) }} confirmLoading={busy} okText="Generate key" okButtonProps={{ disabled: !selectedDevice }}>
      {error && <Alert type="error" showIcon message={error} />}
      <Form layout="vertical"><Form.Item label="Punch machine" required><Select aria-label="API key punch machine" value={selectedDevice} options={activeDevices.map(device => ({ value: device.deviceId, label: device.name + ' (' + device.deviceId + ')' }))} onChange={setSelectedDevice} /></Form.Item><Form.Item label="Valid for (days)"><InputNumber aria-label="API key validity" min={1} max={365} precision={0} value={days} onChange={value => setDays(value || 90)} /></Form.Item></Form>
    </Modal>
  </>
  return mode === 'settings' ? <div id="attendance-devices" className="attendance-device-settings">{content}</div> : <Drawer className="attendance-api-keys" zIndex={1100} title="Attendance API keys" open={open} onClose={() => { setGenerated({}); onClose() }} width="min(1040px, 96vw)">{content}</Drawer>
}
