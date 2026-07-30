import { useEffect, useState } from 'react'
import { Button, Card, Checkbox, Divider, Drawer, Form, Input, Row, Select, Space } from 'antd'
import DataTable from './DataTable'
import { useToast } from './ToastProvider'
import { getEssClientSettings, saveEssClientSetting } from '../services/settingsService'
import type { EssClientSetting } from '../types/payroll'

const initialPasswordModeOptions = ['App Default', 'Random', 'Aadhaar', 'EmployeeCode', 'Fixed']

export default function EssSettings() {
  const notify = useToast()
  const [settings, setSettings] = useState<EssClientSetting[]>([])
  const [draft, setDraft] = useState<EssClientSetting | null>(null)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    const timer = window.setTimeout(() => void getEssClientSettings().then(setSettings), 0)
    return () => window.clearTimeout(timer)
  }, [])

  const edit = (row: EssClientSetting) => {
    setDraft({ ...row, initialPasswordMode: row.initialPasswordMode || 'App Default', fixedPassword: row.fixedPassword || '' })
    setDrawerOpen(true)
  }
  const patch = (value: Partial<EssClientSetting>) => setDraft(current => current ? { ...current, ...value } : current)
  const close = () => {
    setDrawerOpen(false)
    setDraft(null)
  }
  const save = async () => {
    if (!draft) return
    if ((draft.initialPasswordMode || 'App Default') === 'Fixed' && !draft.fixedPassword.trim()) {
      notify('Enter fixed password or select another initial password mode.', 'warning')
      return
    }
    setSaving(true)
    const payload = { ...draft, fixedPassword: draft.initialPasswordMode === 'Fixed' ? draft.fixedPassword : '' }
    const response = await saveEssClientSetting(payload)
    setSaving(false)
    if (!response.ok) {
      notify(response.error || 'Unable to save ESS settings.', 'error')
      return
    }
    setSettings(current => current.map(item => item.id === response.data.id ? response.data : item))
    close()
  }

  return <section className="ess-settings-page">
    <Card title="ESS Settings" size="small" className="settings-panel settings-table-panel ess-settings-panel">
      <div className="component-table-head">
        <div><b>Client self-service policy</b><span>Configure ESS behavior client-wise. Open a client, change controls, then save once.</span></div>
      </div>
      <div className="ess-settings-summary">
        <article><b>{settings.length}</b><span>Configured clients</span></article>
        <article><b>{settings.filter(row => row.allowProfileEdit).length}</b><span>Profile update enabled</span></article>
        <article><b>{settings.filter(row => (row.initialPasswordMode || 'App Default') !== 'App Default').length}</b><span>Custom password policies</span></article>
      </div>
      <DataTable rows={settings} columns={[
        { key: 'clientName', label: 'Client' },
        { key: 'allowProfileEdit', label: 'Profile update', render: row => row.allowProfileEdit ? 'Allowed' : 'Blocked' },
        { key: 'initialPasswordMode', label: 'Initial password', render: row => row.initialPasswordMode || 'App Default' },
        { key: 'isActive', label: 'Status', render: row => row.isActive ? 'Active' : 'Inactive' }
      ]} actions={row => <Button size="small" type="primary" onClick={() => edit(row)}>Configure</Button>} />
      <Drawer className="settings-master-drawer ess-settings-drawer" title={<div className="settings-drawer-title"><span>ESS Client Policy</span><h3>{draft?.clientName || 'Client settings'}</h3><p>Changes are saved only when you click Save policy.</p></div>} open={drawerOpen} width={720} onClose={close} destroyOnClose>
        {draft && <Form component="div" layout="vertical" className="settings-quick-form ess-settings-form">
          <Form.Item label="Client"><Input value={draft.clientName} disabled /></Form.Item>
          <Form.Item label="ESS setting status"><Checkbox checked={draft.isActive} onChange={event => patch({ isActive: event.target.checked })}>Active for this client</Checkbox></Form.Item>
          <Form.Item label="Employee profile update"><Checkbox checked={draft.allowProfileEdit} onChange={event => patch({ allowProfileEdit: event.target.checked })}>Allow employees to update basic, contact, address, PAN, Aadhaar and bank information from ESS</Checkbox></Form.Item>
          <Form.Item label="Initial password mode" required><Select value={draft.initialPasswordMode || 'App Default'} options={initialPasswordModeOptions.map(value => ({ value, label: value === 'EmployeeCode' ? 'Employee code' : value }))} onChange={value => patch({ initialPasswordMode: value, fixedPassword: value === 'Fixed' ? draft.fixedPassword : '' })} /></Form.Item>
          {(draft.initialPasswordMode || 'App Default') === 'Fixed' && <Form.Item label="Fixed initial password" required><Input.Password value={draft.fixedPassword || ''} onChange={event => patch({ fixedPassword: event.target.value })} placeholder="Enter fixed initial password" /></Form.Item>}
          <div className="ess-settings-note"><b>Login rule</b><span>Username remains employee code. Welcome email is queued only when a valid work email exists. If Aadhaar mode is selected and Aadhaar is missing, the system falls back to a generated temporary password.</span></div>
          <Divider />
          <Row justify="end"><Space><Button onClick={close}>Cancel</Button><Button type="primary" loading={saving} onClick={() => void save()}>Save policy</Button></Space></Row>
        </Form>}
      </Drawer>
    </Card>
  </section>
}
