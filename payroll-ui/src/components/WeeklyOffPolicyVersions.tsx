import { useEffect, useState } from 'react'
import { Alert, Button, Divider, Drawer, Form, Input, Select, Space, Tag } from 'antd'
import { getJsonResult, postJson } from '../services/apiClient'
import { useAuthSession } from './AuthGate'
import DataTable from './DataTable'

type WeeklyOffMode = 'Paid' | 'Unpaid' | 'BothAdjacentAbsent' | 'EitherAdjacentAbsent'
type RuleVersion = { versionId: string; versionNumber: number; effectiveFrom: string; mode: WeeklyOffMode; reason: string; publishedBy: string; publishedAt: string }
const modes: { value: WeeklyOffMode; label: string }[] = [
  { value: 'Paid', label: 'Paid weekly off' },
  { value: 'Unpaid', label: 'Unpaid weekly off' },
  { value: 'BothAdjacentAbsent', label: 'Unpaid when both adjoining working days are absent' },
  { value: 'EitherAdjacentAbsent', label: 'Unpaid when either adjoining working day is absent' },
]
const day = (value: string) => value.slice(0, 10)
const today = () => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}` }
const endpoint = '/api/leave-attendance/weekly-off-rules'

export default function WeeklyOffPolicyVersions({ clientId, onSaved }: { clientId: number; onSaved: (message: string) => void }) {
  const session = useAuthSession()
  const canManage = !!session?.user.permissions.some(permission => ['settings.manage', 'client.settings.manage'].includes(permission))
  const [versions, setVersions] = useState<RuleVersion[]>([]), [loading, setLoading] = useState(true), [error, setError] = useState('')
  const [loaded, setLoaded] = useState(false), [reload, setReload] = useState(0)
  const [open, setOpen] = useState(false), [saving, setSaving] = useState(false)
  const [effectiveFrom, setEffectiveFrom] = useState(today), [mode, setMode] = useState<WeeklyOffMode>('Paid'), [reason, setReason] = useState('')
  useEffect(() => {
    let active = true
    setLoading(true); setLoaded(false); setError(''); setVersions([]); setOpen(false)
    void getJsonResult<RuleVersion[]>(`${endpoint}?clientId=${clientId}`, []).then(response => {
      if (!active) return
      if (response.ok) { setVersions(response.data); setLoaded(true) }
      else setError(response.error || 'Unable to load weekly-off rule history.')
      setLoading(false)
    })
    return () => { active = false }
  }, [clientId, reload])
  const latest = [...versions].sort((a, b) => b.versionNumber - a.versionNumber)[0]
  const current = [...versions].filter(version => day(version.effectiveFrom) <= today()).sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom))[0]
  const newVersion = () => { setEffectiveFrom(today()); setMode(latest?.mode ?? 'Paid'); setReason(''); setError(''); setOpen(true) }
  const save = async () => {
    if (!canManage || saving || loading) return
    if (!effectiveFrom) return setError('Select an effective date.')
    if (latest && effectiveFrom <= day(latest.effectiveFrom)) return setError('The new effective date must be after the last published version.')
    if (!reason.trim()) return setError('Enter a reason for this rule change.')
    setSaving(true)
    try {
      const response = await postJson(endpoint, { clientId, effectiveFrom, mode, reason: reason.trim(), expectedLatestVersion: latest?.versionNumber ?? 0 }, [] as RuleVersion[])
      if (!response.ok) return setError(response.error || 'Unable to publish weekly-off rule.')
      setVersions(response.data); setOpen(false); onSaved('Weekly-off rule version saved. Existing attendance and payroll were not recalculated.')
    } finally { setSaving(false) }
  }
  return <section aria-label="Weekly-off payment rules">
    <Divider orientation="left">Weekly-off payment rules</Divider>
    <Space wrap style={{ marginBottom: 12 }}>
      <Tag>{current ? `Current: v${current.versionNumber}` : 'Existing rule: paid weekly off'}</Tag>
      {canManage && <Button disabled={loading || !loaded || clientId <= 0} onClick={newVersion}>New rule version</Button>}
      <Button disabled={loading || saving} onClick={() => setReload(value => value + 1)}>Refresh rules</Button>
    </Space>
    <Alert type="info" showIcon message="Rules follow this client's configured weekly offs and adjoining working days. Approved paid leave and half days are not treated as full absence; missing attendance is not assumed absent. Holidays keep their own treatment." style={{ marginBottom: 12 }} />
    {!open && error && <Alert type="error" showIcon message={error} />}
    <DataTable rows={[...versions].sort((a, b) => b.versionNumber - a.versionNumber)} getRowId={row => row.versionId} title="Weekly-off rule history" exportFileName="weekly-off-rule-history" emptyText={loading ? 'Loading rules…' : 'No versions configured. Existing attendance behavior continues.'} columns={[
      { key: 'versionNumber', label: 'Version', render: row => <Tag>v{row.versionNumber}</Tag> },
      { key: 'effectiveFrom', label: 'Effective from', value: row => day(row.effectiveFrom) },
      { key: 'mode', label: 'Rule', value: row => modes.find(option => option.value === row.mode)?.label ?? row.mode },
      { key: 'reason', label: 'Change reason' },
      { key: 'publishedBy', label: 'Published by' },
      { key: 'publishedAt', label: 'Published', value: row => new Date(row.publishedAt.endsWith('Z') ? row.publishedAt : `${row.publishedAt}Z`).toLocaleString() },
    ]} pageSizeOptions={[5, 10, 25]} />
    <Drawer className="settings-master-drawer" title="New weekly-off rule version" width="min(680px, 96vw)" open={open} onClose={() => !saving && setOpen(false)} closable={!saving} destroyOnClose footer={<Space><Button disabled={saving} onClick={() => setOpen(false)}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void save()}>Save new version</Button></Space>}>
      <Form layout="vertical" className="settings-quick-form" disabled={saving}>
        {error && <Alert type="error" showIcon message={error} />}
        <Form.Item label="Effective from" required><Input aria-label="Weekly-off effective from" type="date" value={effectiveFrom} onChange={event => setEffectiveFrom(event.target.value)} /></Form.Item>
        <Form.Item label="Weekly-off payment" required><Select aria-label="Weekly-off payment rule" value={mode} options={modes} onChange={setMode} /></Form.Item>
        <Form.Item label="Change reason" required><Input.TextArea aria-label="Weekly-off change reason" rows={3} maxLength={500} value={reason} onChange={event => setReason(event.target.value)} /></Form.Item>
        <Alert type="info" showIcon message="Saving publishes a new immutable version. Earlier dates retain their earlier rule; saved payroll is not recalculated automatically." />
      </Form>
    </Drawer>
  </section>
}
