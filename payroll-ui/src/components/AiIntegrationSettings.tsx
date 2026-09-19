import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ApiOutlined,
  CheckCircleFilled,
  DeleteOutlined,
  EditOutlined,
  PlusOutlined,
  SafetyCertificateOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons'
import { Alert, Button, Card, Col, Form, Input, InputNumber, Popconfirm, Progress, Row, Select, Space, Switch, Table, Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  activateAiIntegration,
  deleteAiIntegration,
  emptyAiIntegration,
  emptyAiProviderPool,
  getAiIntegration,
  saveAiIntegration,
  testAiIntegration,
  updateAiAutoSwitch,
} from '../services/aiIntegrationService'
import type { RecruitmentAiProviderPool, RecruitmentAiScoringSettings } from '../types/payroll'
import { aiModelEndpointLabel, canMakeAiModelActive, localPrimaryModel } from './aiModelActions'

const providerOptions: Array<{ value: RecruitmentAiScoringSettings['providerCode']; label: string }> = [
  { value: 'Gemini', label: 'Google Gemini' },
  { value: 'OpenAI', label: 'OpenAI' },
  { value: 'Anthropic', label: 'Anthropic Claude' },
  { value: 'Groq', label: 'Groq Cloud' },
  { value: 'Grok', label: 'xAI Grok' },
  { value: 'OpenAICompatible', label: 'OpenAI-compatible provider' },
  { value: 'LocalOpenAICompatible', label: 'Local LLM (OpenAI-compatible pilot)' },
]

const modelExamples: Record<RecruitmentAiScoringSettings['providerCode'], string> = {
  Gemini: 'e.g. gemini-2.5-flash',
  OpenAI: 'e.g. gpt-4o-mini',
  Anthropic: 'e.g. claude-3-5-haiku-latest',
  Groq: 'e.g. groq/compound-mini',
  Grok: 'e.g. grok-3-mini',
  OpenAICompatible: 'Enter the provider model ID',
  LocalOpenAICompatible: 'e.g. hrms-local',
}

const healthColor = (status: string) => status === 'Healthy' ? 'green' : status === 'RateLimited' ? 'orange' : status === 'Unhealthy' ? 'red' : 'default'
const formatDate = (value?: string | null) => value ? new Date(value).toLocaleString() : 'Never'
const formatNumber = (value: number) => new Intl.NumberFormat('en-IN', { notation: value >= 100_000 ? 'compact' : 'standard', maximumFractionDigits: 1 }).format(value)
const editModel = (row: RecruitmentAiScoringSettings): RecruitmentAiScoringSettings => ({ ...row, apiKey: '' })

export default function AiIntegrationSettings() {
  const [pool, setPool] = useState<RecruitmentAiProviderPool>(emptyAiProviderPool())
  const [editor, setEditor] = useState<RecruitmentAiScoringSettings>(emptyAiIntegration())
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [testingId, setTestingId] = useState(0)
  const [activatingId, setActivatingId] = useState(0)
  const [switching, setSwitching] = useState(false)

  const load = useCallback(async () => {
    const response = await getAiIntegration()
    setPool(response)
    return response
  }, [])

  useEffect(() => {
    void load().finally(() => setLoading(false))
  }, [load])

  const nextPriority = (models = pool.models) => (models.length ? models[models.length - 1].priority : 90) + 10
  const resetEditor = (models = pool.models) => setEditor({ ...emptyAiIntegration(), priority: nextPriority(models) })

  const changeProvider = (providerCode: RecruitmentAiScoringSettings['providerCode']) => {
    const local = providerCode === 'LocalOpenAICompatible'
    setEditor({
      ...editor,
      providerCode,
      modelName: local ? 'hrms-local' : providerCode === 'OpenAI' ? 'gpt-4o-mini' : providerCode === 'Groq' ? 'groq/compound-mini' : '',
      endpointUrl: local ? 'https://eeslindia.org/llm-api.php' : providerCode === 'OpenAICompatible' ? editor.endpointUrl : '',
      requestTimeoutSeconds: local ? 210 : editor.providerCode === 'LocalOpenAICompatible' ? emptyAiIntegration().requestTimeoutSeconds : editor.requestTimeoutSeconds,
      enableAiScoring: local ? false : editor.enableAiScoring,
      isActive: local ? false : editor.isActive,
      apiKey: '',
      hasApiKey: false,
      credentialStatus: 'Missing',
    })
  }

  const save = async () => {
    setSaving(true)
    try {
      const response = await saveAiIntegration({ ...editor, isActive: editor.enableAiScoring })
      if (response.ok && response.data) {
        const refreshed = await load()
        resetEditor(refreshed.models)
      }
    } finally {
      setSaving(false)
    }
  }

  const test = async (row: RecruitmentAiScoringSettings) => {
    setTestingId(row.id)
    try {
      await testAiIntegration(row.id, row.providerCode === 'LocalOpenAICompatible'
        ? Math.min(600, Math.max(60, row.requestTimeoutSeconds || 210)) + 45
        : undefined)
      await load()
    } finally {
      setTestingId(0)
    }
  }

  const activate = async (row: RecruitmentAiScoringSettings) => {
    if (activatingId || !canMakeAiModelActive(row)) return
    setActivatingId(row.id)
    try {
      if (row.providerCode === 'LocalOpenAICompatible') {
        const request = localPrimaryModel(row)
        if (!request) return
        const response = await saveAiIntegration(request)
        if (response.ok && response.data) {
          await load()
          setEditor(previous => previous.id === row.id
            ? { ...previous, enableAiScoring: true, isActive: true, isPrimary: true }
            : previous)
        }
      } else {
        const response = await activateAiIntegration(row.id)
        if (response.ok && response.data) setPool(response.data)
      }
    } finally {
      setActivatingId(0)
    }
  }

  const remove = async (row: RecruitmentAiScoringSettings) => {
    const response = await deleteAiIntegration(row.id)
    if (response.ok) {
      if (editor.id === row.id) resetEditor()
      await load()
    }
  }

  const changeAutoSwitch = async (autoSwitchEnabled: boolean) => {
    setSwitching(true)
    try {
      const response = await updateAiAutoSwitch(autoSwitchEnabled)
      if (response.ok && response.data) setPool(response.data)
    } finally {
      setSwitching(false)
    }
  }

  const columns = useMemo<ColumnsType<RecruitmentAiScoringSettings>>(() => [
    {
      title: 'Provider & model',
      key: 'model',
      width: 200,
      render: (_, row) => <Space direction="vertical" size={3}>
        <Space wrap>
          <Typography.Text strong>{providerOptions.find(option => option.value === row.providerCode)?.label ?? row.providerCode}</Typography.Text>
          {row.isPrimary && <Tag color="blue" icon={<CheckCircleFilled />}>ACTIVE</Tag>}
        </Space>
        <Typography.Text>{row.modelName}</Typography.Text>
        {aiModelEndpointLabel(row) && <Typography.Text type="secondary" ellipsis style={{ maxWidth: 240 }}>{aiModelEndpointLabel(row)}</Typography.Text>}
      </Space>,
    },
    {
      title: 'Availability',
      key: 'availability',
      width: 120,
      render: (_, row) => <Space direction="vertical" size={4}>
        <Tag color={healthColor(row.healthStatus)}>{row.healthStatus || 'Not tested'}</Tag>
        {row.credentialStatus === 'Unreadable' && <Typography.Text type="danger">Re-enter API key</Typography.Text>}
        <Typography.Text type="secondary">{row.enableAiScoring && row.isActive ? `Ready · order ${row.priority}` : 'Disabled'}</Typography.Text>
        {row.consecutiveFailureCount > 0 && <Typography.Text type="danger">{row.consecutiveFailureCount} recent failure{row.consecutiveFailureCount === 1 ? '' : 's'}</Typography.Text>}
      </Space>,
    },
    {
      title: 'Account',
      dataIndex: 'accountEmail',
      key: 'accountEmail',
      width: 190,
      render: value => <Typography.Text ellipsis title={value || 'Not specified'}>{value || 'Not specified'}</Typography.Text>,
    },
    {
      title: 'Usage & live quota',
      key: 'usage',
      width: 250,
      render: (_, row) => <div style={{ minWidth: 170 }}>
        <Progress
          percent={Math.min(100, Number(row.usagePercent || 0))}
          status={row.usagePercent >= 100 ? 'exception' : 'normal'}
          strokeColor={row.usagePercent >= 80 ? '#fa8c16' : '#5b48e8'}
          size="small"
          showInfo={false}
        />
        <Typography.Text strong style={{ fontSize: 12 }}>{formatNumber(row.usageRequestCount)} / {formatNumber(row.monthlyRequestLimit)} requests</Typography.Text><br />
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {formatNumber(row.usageInputTokens + row.usageOutputTokens)} tracked tokens · {row.usagePeriod || 'current month'}
        </Typography.Text>
        {row.providerCode === 'Groq' && <div style={{ marginTop: 6 }}>
          {row.providerRequestLimit != null ? <>
            <Typography.Text strong style={{ fontSize: 12 }}>Groq live: {formatNumber(row.providerRequestsRemaining || 0)} / {formatNumber(row.providerRequestLimit)} requests/day</Typography.Text><br />
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{formatNumber(row.providerTokensRemaining || 0)} / {formatNumber(row.providerTokenLimit || 0)} tokens/min</Typography.Text>
          </> : <Typography.Text type="secondary" style={{ fontSize: 12 }}>Test once to read Groq live limits</Typography.Text>}
        </div>}
      </div>,
    },
    {
      title: 'Last activity',
      key: 'activity',
      width: 160,
      render: (_, row) => <Space direction="vertical" size={2}>
        <Typography.Text>Used: {formatDate(row.lastUsedAt)}</Typography.Text>
        <Typography.Text type="secondary">Tested: {formatDate(row.lastTestedAt)}</Typography.Text>
      </Space>,
    },
    {
      title: 'Actions',
      key: 'actions',
      width: 200,
      render: (_, row) => <Space wrap>
        <Button size="small" icon={<EditOutlined />} disabled={activatingId > 0} onClick={() => setEditor(editModel(row))}>Edit</Button>
        <Button size="small" loading={testingId === row.id} disabled={!row.hasApiKey || testingId > 0 || activatingId > 0} onClick={() => row.credentialStatus === 'Unreadable' ? setEditor(editModel(row)) : void test(row)}>Test</Button>
        {!row.isPrimary && (row.providerCode === 'LocalOpenAICompatible'
          ? <Popconfirm
              title={`Make ${row.modelName} active for Frevo?`}
              description={<div style={{ maxWidth: 390 }}>This enables the saved local model and selects it for resume/JD parsing, ATS AI and FrevoPilot. It affects every API using this database, not only localhost. Deploy the latest backend to all API/worker instances before confirming. Test the model first. Auto Switch and other models are unchanged.</div>}
              okText="Enable & make active"
              disabled={!canMakeAiModelActive(row) || activatingId > 0 || saving || testingId > 0}
              onConfirm={() => activate(row)}
            >
              <Button size="small" type="primary" ghost loading={activatingId === row.id} disabled={!canMakeAiModelActive(row) || activatingId > 0 || saving || testingId > 0}>Make active</Button>
            </Popconfirm>
          : <Button size="small" type="primary" ghost loading={activatingId === row.id} disabled={!canMakeAiModelActive(row) || activatingId > 0 || saving || testingId > 0} onClick={() => void activate(row)}>Make active</Button>)}
        <Popconfirm title={`Remove ${row.modelName}?`} description="Its encrypted API key and usage history will be removed." okText="Remove" okButtonProps={{ danger: true }} onConfirm={() => void remove(row)}>
          <Button size="small" danger disabled={activatingId > 0} icon={<DeleteOutlined />} aria-label={`Remove ${row.modelName}`} />
        </Popconfirm>
      </Space>,
    },
  ], [testingId, activatingId, saving, editor.id, pool.models])

  const localProvider = editor.providerCode === 'LocalOpenAICompatible'
  const hasCustomEndpoint = localProvider || editor.providerCode === 'OpenAICompatible'
  const saveDisabled = !editor.modelName.trim()
    || (hasCustomEndpoint && !editor.endpointUrl.trim())
    || (localProvider && (!Number.isInteger(editor.requestTimeoutSeconds) || editor.requestTimeoutSeconds < 60 || editor.requestTimeoutSeconds > 600))
    || (editor.credentialStatus !== 'Ready' && !editor.apiKey?.trim())

  return <section className="orchestration-shell ai-integration-settings" data-testid="ai-integration-settings" style={{ minWidth: 0 }}>
    <Card
      size="small"
      loading={loading}
      style={{ minWidth: 0, overflow: 'hidden' }}
      title={<Space><ApiOutlined />AI model pool</Space>}
      extra={<Space>
        <Typography.Text strong>Auto Switch</Typography.Text>
        <Switch data-testid="ai-auto-switch" loading={switching} disabled={activatingId > 0} checked={pool.autoSwitchEnabled} onChange={value => void changeAutoSwitch(value)} />
      </Space>}
    >
      <Alert
        type="info"
        showIcon
        icon={<SafetyCertificateOutlined />}
        message="One reusable AI pool for Frevo"
        description="The active model is tried first. With Auto Switch enabled, quota, timeout or provider failures continue on the next enabled model. Keys stay encrypted and never return to the browser."
        style={{ marginBottom: 16 }}
      />
      {pool.models.some(model => model.credentialStatus === 'Unreadable') && <Alert
        type="warning"
        showIcon
        message="Migrate the existing AI credentials once"
        description="These models still use the legacy key-ring format. Edit each affected model, enter its provider API key, and save. The new portable encrypted credential will then work from both local and production APIs."
        style={{ marginBottom: 16 }}
      />}

      <Card
        size="small"
        type="inner"
        title={editor.id > 0 ? `Edit ${editor.modelName}` : 'Add AI model'}
        extra={editor.id > 0
          ? <Button size="small" icon={<PlusOutlined />} onClick={() => resetEditor()}>Add another model</Button>
          : <Tag color="purple">Encrypted connection</Tag>}
        style={{ marginBottom: 18 }}
      >
        <Form component="div" layout="vertical">
          <Row gutter={16} align="bottom">
            <Col xs={24} md={6}><Form.Item label="Provider" required><Select value={editor.providerCode} options={providerOptions} onChange={changeProvider} /></Form.Item></Col>
            <Col xs={24} md={6}><Form.Item label="Model" required><Input data-testid="ai-scoring-model" value={editor.modelName} placeholder={modelExamples[editor.providerCode]} onChange={event => setEditor({ ...editor, modelName: event.target.value })} /></Form.Item></Col>
            <Col xs={24} md={6}><Form.Item label={editor.providerCode === 'Gemini' ? 'Google / Gmail ID' : 'Account email'} extra="Identifies this API key in the model pool."><Input type="email" autoComplete="off" value={editor.accountEmail} placeholder="account@example.com" onChange={event => setEditor({ ...editor, accountEmail: event.target.value })} /></Form.Item></Col>
            <Col xs={24} md={7}><Form.Item label="API key" required={editor.credentialStatus !== 'Ready'} extra={editor.credentialStatus === 'Unreadable' ? 'The saved key cannot be decrypted here. Enter it again.' : editor.hasApiKey ? 'Leave blank to keep the saved encrypted key.' : 'Encrypted before storage.'}><Input.Password data-testid="ai-scoring-api-key" autoComplete="new-password" status={editor.credentialStatus === 'Unreadable' ? 'error' : undefined} value={editor.apiKey || ''} placeholder={editor.credentialStatus === 'Unreadable' ? 'Re-enter provider API key' : editor.hasApiKey ? 'Configured securely' : 'Paste provider API key'} onChange={event => setEditor({ ...editor, apiKey: event.target.value })} /></Form.Item></Col>
            <Col xs={12} md={3}><Form.Item label="Monthly cap" extra="Requests"><InputNumber min={1} max={10_000_000} controls={false} value={editor.monthlyRequestLimit} onChange={value => setEditor({ ...editor, monthlyRequestLimit: Number(value || 1) })} style={{ width: '100%' }} /></Form.Item></Col>
            <Col xs={12} md={2}><Form.Item label="Order" extra="Lower first"><InputNumber min={1} max={9999} controls={false} value={editor.priority} onChange={value => setEditor({ ...editor, priority: Number(value || 100) })} style={{ width: '100%' }} /></Form.Item></Col>
            {hasCustomEndpoint && <Col xs={24} md={localProvider ? 16 : 24}><Form.Item label={localProvider ? 'HTTPS endpoint URL' : 'HTTPS base URL'} required extra={localProvider ? 'Exact chat endpoint. Frevo sends requests to this URL without appending /chat/completions.' : 'Example: https://ai.company.com/v1'}><Input value={editor.endpointUrl} placeholder={localProvider ? 'https://eeslindia.org/llm-api.php' : 'https://provider.example/v1'} onChange={event => setEditor({ ...editor, endpointUrl: event.target.value })} /></Form.Item></Col>}
            {localProvider && <Col xs={24} md={8}><Form.Item label="Request timeout (seconds)" required extra="60–600 seconds. The gateway/server has its own timeout; increasing this does not change that limit."><InputNumber data-testid="ai-local-request-timeout" min={60} max={600} precision={0} controls={false} value={editor.requestTimeoutSeconds} onChange={value => setEditor({ ...editor, requestTimeoutSeconds: Number(value || 0) })} style={{ width: '100%' }} /></Form.Item></Col>}
          </Row>
          {localProvider && <Alert
            type="warning"
            showIcon
            message="Local LLM pilot — opt in when ready"
            description="This provider can be made active for resume parsing, JD extraction, ATS AI analysis and FrevoPilot. Responses are currently capped at 256 output tokens. Long documents or dashboard requests may exceed input/output capacity; an incomplete or failed response is not a valid result. Save and test first, then choose Make active in Saved models to enable and activate it together. Deploy the latest backend to all API/worker instances before activating a shared configuration. Cloud fallback is used only when Auto Switch is enabled and cloud models are enabled. API keys use the same encrypted storage."
            style={{ marginBottom: 16 }}
          />}
          <Row justify="space-between" align="middle" gutter={[16, 12]}>
            <Col><Space><Switch data-testid="enable-ai-scoring" checked={editor.enableAiScoring} onChange={enableAiScoring => setEditor({ ...editor, enableAiScoring, isActive: enableAiScoring })} /><span>Available to Frevo and automatic failover</span></Space></Col>
            <Col><Space>
              {editor.id > 0 && <Button onClick={() => resetEditor()}>Cancel</Button>}
              <Button type="primary" icon={<ThunderboltOutlined />} loading={saving} disabled={saveDisabled || activatingId > 0} onClick={() => void save()}>{editor.id > 0 ? 'Update model' : 'Save model'}</Button>
            </Space></Col>
          </Row>
        </Form>
      </Card>

      <Space direction="vertical" size={4} style={{ marginBottom: 12 }}>
        <Typography.Title level={5} style={{ margin: 0 }}>Saved models</Typography.Title>
        <Typography.Text type="secondary">Usage is Frevo's monthly request/token telemetry against your configured cap; provider billing balance remains in the provider portal.</Typography.Text>
      </Space>
      <div style={{ minWidth: 0, maxWidth: '100%', overflow: 'hidden' }}>
        <Table<RecruitmentAiScoringSettings>
          data-testid="ai-model-pool-table"
          rowKey="id"
          size="middle"
          columns={columns}
          dataSource={pool.models}
          pagination={false}
          tableLayout="fixed"
          scroll={{ x: 1090 }}
          locale={{ emptyText: 'No AI model saved yet. Add Gemini, OpenAI, Claude, Grok or a compatible provider above.' }}
        />
      </div>
    </Card>
  </section>
}
