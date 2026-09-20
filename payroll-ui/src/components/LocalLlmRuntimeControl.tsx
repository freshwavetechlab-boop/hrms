import { useCallback, useEffect, useState } from 'react'
import { Button, Popconfirm, Space, Tag, Typography } from 'antd'
import { useAuthSession } from './AuthGate'
import { getJsonResult, postJson } from '../services/apiClient'

type Runtime = { state: string; code: string; message: string; canStart: boolean }

export default function LocalLlmRuntimeControl({ modelId, endpointUrl, disabled }: {
  modelId: number; endpointUrl: string; disabled: boolean
}) {
  const user = useAuthSession()?.user
  const allowed = user?.clientId == null && Boolean(user?.roles.some(role => role.toLowerCase() === 'super_admin'))
  const [status, setStatus] = useState<Runtime | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const refresh = useCallback(async () => {
    if (!allowed) return
    setBusy(true); setError('')
    const result = await getJsonResult<Runtime | null>(`/api/integrations/ai/${modelId}/runtime`, null,
      { loader: false, toast: false, timeoutMs: 30000 })
    setStatus(result.ok ? result.data : null)
    setError(result.ok ? '' : result.error || 'Unable to check local LLM status.')
    setBusy(false)
  }, [modelId, endpointUrl, allowed])
  useEffect(() => { setStatus(null); setError(''); void refresh() }, [refresh])
  const start = async () => {
    if (!allowed || busy || !status?.canStart) return
    setBusy(true); setError('')
    const result = await postJson(`/api/integrations/ai/${modelId}/start-runtime`, {}, null as Runtime | null,
      { loader: false, toast: false, timeoutMs: 60000, timeoutMessage: 'Start outcome is unknown. Refresh runtime status before retrying.' })
    setStatus(result.ok ? result.data : null)
    setError(result.ok ? '' : result.error || 'Start outcome is unknown. Refresh status before retrying.')
    setBusy(false)
  }
  if (!allowed) return null
  return <Space direction="vertical" size={4} style={{ maxWidth: 300 }}>
    <Space wrap>
      <Button size="small" disabled={disabled || busy} loading={busy} onClick={() => void refresh()}>
        {status ? 'Refresh LLM status' : 'Check LLM status'}
      </Button>
      <Popconfirm title="Start local LLM on the server?"
        description="Runs only the existing LLM task. CPU/website watchdog limits stay unchanged. This does not change your active AI provider."
        okText="Start LLM" onConfirm={start} disabled={disabled || busy || !status?.canStart}>
        <Button size="small" disabled={disabled || busy || !status?.canStart}>Start local LLM</Button>
      </Popconfirm>
    </Space>
    {status && <div role="status">
      <Tag color={status.state === 'Running' ? 'green' : status.state === 'Starting' ? 'blue' : 'default'}>{status.state}</Tag>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>{status.message}</Typography.Text>
    </div>}
    {error && <Typography.Text type="danger" role="alert">{error}</Typography.Text>}
  </Space>
}
