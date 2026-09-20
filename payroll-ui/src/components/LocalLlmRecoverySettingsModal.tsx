import { useEffect, useState } from 'react'
import { Alert, Form, Input, Modal, Switch, Tag } from 'antd'
import { getJsonResult, putJson } from '../services/apiClient'

type Settings = {
  enabled: boolean; controlEndpointUrl: string; inferenceEndpointUrl: string
  source: string; credentialStatus: string; version: string
}
type Draft = { enabled: boolean; controlEndpointUrl: string; apiKey?: string }

export default function LocalLlmRecoverySettingsModal({ modelId, endpointUrl, onClose, onSaved }: {
  modelId: number; endpointUrl: string; onClose: () => void; onSaved: () => void
}) {
  const [form] = Form.useForm<Draft>()
  const [settings, setSettings] = useState<Settings | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    let current = true
    void getJsonResult<Settings | null>(`/api/integrations/ai/${modelId}/recovery-settings`, null,
      { loader: false, toast: false }).then(result => {
      if (!current) return
      setSettings(result.ok ? result.data : null)
      setError(result.ok ? '' : result.error || 'Unable to load recovery settings.')
      if (result.ok && result.data) form.setFieldsValue({ enabled: result.data.enabled,
        controlEndpointUrl: result.data.controlEndpointUrl, apiKey: '' })
      setLoading(false)
    })
    return () => { current = false; form.resetFields() }
  }, [modelId, form])
  const save = async () => {
    if (!settings || saving) return
    let values: Draft
    try { values = await form.validateFields() } catch { return }
    setSaving(true); setError('')
    const result = await putJson(`/api/integrations/ai/${modelId}/recovery-settings`, {
      enabled: values.enabled, controlEndpointUrl: values.controlEndpointUrl.trim(),
      inferenceEndpointUrl: endpointUrl, apiKey: values.apiKey?.trim() || '', version: settings.version,
    }, null as Settings | null)
    // Never retain a typed secret after submission, even on failure.
    form.setFieldsValue({ apiKey: '' })
    setSaving(false)
    if (!result.ok) { setError(result.error || 'Recovery settings were not saved.'); return }
    onSaved(); onClose()
  }
  return <Modal open title="Local LLM recovery settings" width={620} okText="Save recovery settings"
    confirmLoading={saving} okButtonProps={{ disabled: loading || !settings }}
    cancelButtonProps={{ disabled: saving }} closable={!saving} maskClosable={!saving}
    onCancel={() => { form.resetFields(); onClose() }} onOk={() => void save()}>
    <Alert type="info" showIcon message="Shared, encrypted database settings"
      description="Applies to this gateway across localhost and production using the same database and integration encryption configuration. Saving does not start the LLM or change your active model." />
    {settings && <p><Tag>{settings.source}</Tag><Tag color={settings.credentialStatus === 'Ready' ? 'green' : 'orange'}>
      Control key: {settings.credentialStatus}</Tag></p>}
    {settings?.credentialStatus === 'Unreadable' && <Alert type="warning" showIcon
      message="This API cannot decrypt the saved key. Match the integration encryption configuration across environments before re-entering the key." />}
    {error && <Alert type="error" showIcon message={error} style={{ marginTop: 12 }} />}
    <Form form={form} layout="vertical" disabled={loading || saving} preserve={false} style={{ marginTop: 16 }}>
      <Form.Item name="enabled" label="Enable manual server recovery" valuePropName="checked"><Switch /></Form.Item>
      <Form.Item name="controlEndpointUrl" label="Control endpoint (HTTPS)" rules={[{ required: true, type: 'url', message: 'Enter the HTTPS recovery endpoint.' }]}>
        <Input placeholder="Separate recovery control URL" autoComplete="off" />
      </Form.Item>
      <Form.Item label="Inference endpoint" extra="Bound to this saved model; control must use the same HTTPS host.">
        <Input value={endpointUrl} readOnly />
      </Form.Item>
      <Form.Item name="apiKey" label="Separate control API key"
        extra="Not the inference API key. Leave blank to keep the saved key. Stored encrypted, never returned to the browser."
        rules={[{ validator: (_, value?: string) => !value?.trim() || /^[a-fA-F0-9]{64}$/.test(value.trim())
          ? Promise.resolve() : Promise.reject(new Error('Enter exactly 64 hex characters, without Bearer or quotes.')) }]}>
        <Input.Password autoComplete="new-password" placeholder="Paste the control key privately" />
      </Form.Item>
    </Form>
  </Modal>
}
