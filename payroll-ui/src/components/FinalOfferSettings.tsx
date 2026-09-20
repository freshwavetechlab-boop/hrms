import { useEffect, useState } from 'react'
import { Alert, Button, Drawer, Form, Select, Space, Spin, Switch } from 'antd'
import { SettingOutlined } from '@ant-design/icons'
import type { Client, WorkflowApprover } from '../types/payroll'
import { getJsonResult, postJson } from '../services/apiClient'
import './FinalOfferSettings.css'

type Settings = { clientId: number; enabled: boolean; finalApproverUserId: number | null; source: string }

export default function FinalOfferSettings({ clients, initialClientId, reload }: {
  clients: Client[]; initialClientId: number; reload: () => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [clientId, setClientId] = useState(initialClientId)
  const [settings, setSettings] = useState<Settings | null>(null)
  const [users, setUsers] = useState<WorkflowApprover[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    let active = true
    setSettings(null); setUsers([]); setError(''); setLoading(false)
    if (open && clientId > 0) {
      setLoading(true)
      void Promise.all([
        getJsonResult<Settings | null>(`/api/recruitment/clients/${clientId}/final-offer-settings`, null),
        getJsonResult<WorkflowApprover[]>(`/api/recruitment/budget-approvers?clientId=${clientId}`, []),
      ]).then(([policy, people]) => {
        if (!active) return
        if (policy.ok && people.ok) { setSettings(policy.data); setUsers(people.data) }
        else setError(policy.error || people.error || 'Unable to load signing configuration.')
        setLoading(false)
      })
    }
    return () => { active = false }
  }, [open, clientId])
  const save = async () => {
    if (!settings || settings.clientId !== clientId || saving) return
    setSaving(true); setError('')
    try {
      const result = await postJson(`/api/recruitment/clients/${clientId}/final-offer-settings`, {
        enabled: settings.enabled, finalApproverUserId: settings.finalApproverUserId,
      }, null as Settings | null)
      if (!result.ok) { setError(result.error || 'Unable to save signing configuration.'); return }
      setOpen(false); await reload()
    } finally { setSaving(false) }
  }
  return <>
    <Button icon={<SettingOutlined />} onClick={() => { setClientId(initialClientId); setOpen(true) }}>Final offer signatory</Button>
    <Drawer title="Final offer signatory" open={open} width={560} destroyOnClose
      onClose={() => { if (!saving) setOpen(false) }} closable={!saving} maskClosable={!saving}
      footer={<Space><Button disabled={saving} onClick={() => setOpen(false)}>Cancel</Button><Button type="primary" loading={saving}
        disabled={loading || !settings || settings.clientId !== clientId || (settings.enabled && !settings.finalApproverUserId)} onClick={() => void save()}>Save configuration</Button></Space>}>
      <Form layout="vertical">
        <Form.Item label="Client" htmlFor="final-offer-client" required>
          <Select id="final-offer-client" aria-label="Signing client" showSearch optionFilterProp="label" value={clientId || undefined} disabled={saving}
            placeholder="Select client" onChange={setClientId} options={clients.map(client => ({ value: client.id, label: client.name }))} />
        </Form.Item>
        {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />}
        {loading ? <Spin /> : settings && <>
          <Form.Item label="Signed final offer after candidate acceptance">
            <Switch checked={settings.enabled} disabled={saving} onChange={enabled => setSettings({ ...settings, enabled })} />
          </Form.Item>
          <Form.Item label="Authorized final approver" htmlFor="final-offer-approver" required={settings.enabled}
            extra="The selected user receives the departmental approval task. Approval is required before the organization seal/signature is applied.">
            <Select id="final-offer-approver" aria-label="Authorized final approver" showSearch allowClear optionFilterProp="label" popupClassName="final-offer-signatory-options"
              value={settings.finalApproverUserId || undefined} disabled={saving} placeholder="Select approver"
              onChange={value => setSettings({ ...settings, finalApproverUserId: value || null })}
              options={users.map(user => ({ value: user.id, label: `${user.displayName} · ${user.email}` }))} />
          </Form.Item>
          <Alert type="info" showIcon message="GAD / UIDAI branded final offer"
            description="Applies to this client's new acceptances. Existing accepted offers can request final approval separately. Original letters are preserved; saving this setting does not approve, sign or email an offer. Pending approvals must be completed before changing the approver." />
        </>}
      </Form>
    </Drawer>
  </>
}
