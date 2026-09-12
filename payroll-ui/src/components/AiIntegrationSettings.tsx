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

const providerOptions: Array<{ value: RecruitmentAiScoringSettings['providerCode']; label: string }> = [
  { value: 'Gemini', label: 'Google Gemini' },
  { value: 'OpenAI', label: 'OpenAI' },
  { value: 'Anthropic', label: 'Anthropic Claude' },
  { value: 'Groq', label: 'Groq Cloud' },
  { value: 'Grok', label: 'xAI Grok' },
  { value: 'OpenAICompatible', label: 'OpenAI-compatible provider' },
]

const modelExamples: Record<RecruitmentAiScoringSettings['providerCode'], string> = {
  Gemini: 'e.g. gemini-2.5-flash',
  OpenAI: 'e.g. gpt-4o-mini',
  Anthropic: 'e.g. claude-3-5-haiku-latest',
  Groq: 'e.g. groq/compound-mini',
  Grok: 'e.g. grok-3-mini',
  OpenAICompatible: 'Enter the provider model ID',
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
      await testAiIntegration(row.id)
      await load()
    } finally {
      setTestingId(0)
    }
  }

  const activate = async (row: RecruitmentAiScoringSettings) => {
    const response = await activateAiIntegration(row.id)
    if (response.ok && response.data) setPool(response.data)
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
        {row.endpointUrl && <Typography.Text type="secondary" ellipsis style={{ maxWidth: 240 }}>{row.endpointUrl}</Typography.Text>}
      </Space>,
    },
    {
      title: 'Availability',
      key: 'availability',
      width: 120,
      render: (_, row) => <Space direction="vertical" size={4}>
        <Tag color={healthColor(row.healthStatus)}>{row.healthStatus || 'Not tested'}</Tag>
        <Typography.Text type="secondary">{row.enableAiScoring && row.isActive ? `Ready · order ${row.priority}` : 'Disabled'}</Typography.Text>
        {row.consecutiveFailureCount > 0 && <Typography.Text type="danger">{row.consecutiveFailureCount} recent failure{row.consecutiveFailureCount === 1 ? '' : 's'}</Typography.Text>}
      </Space>,
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
        <Button size="small" icon={<EditOutlined />} onClick={() => setEditor(editModel(row))}>Edit</Button>
        <Button size="small" loading={testingId === row.id} disabled={!row.hasApiKey || testingId > 0} onClick={() => void test(row)}>Test</Button>
        {!row.isPrimary && <Button size="small" type="primary" ghost disabled={!row.enableAiScoring || !row.isActive} onClick={() => void activate(row)}>Make active</Button>}
        <Popconfirm title={`Remove ${row.modelName}?`} description="Its encrypted API key and usage history will be removed." okText="Remove" okButtonProps={{ danger: true }} onConfirm={() => void remove(row)}>
          <Button size="small" danger icon={<DeleteOutlined />} aria-label={`Remove ${row.modelName}`} />
        </Popconfirm>
      </Space>,
    },
  ], [testingId, editor.id, pool.models])

  const saveDisabled = !editor.modelName.trim()
    || (editor.providerCode === 'OpenAICompatible' && !editor.endpointUrl.trim())
    || (!editor.hasApiKey && !editor.apiKey?.trim())

  return <section className="orchestration-shell ai-integration-settings" data-testid="ai-integration-settings" style={{ minWidth: 0 }}>
    <Card
      size="small"
      loading={loading}
      style={{ minWidth: 0, overflow: 'hidden' }}
      title={<Space><ApiOutlined />AI model pool</Space>}
      extra={<Space>
        <Typography.Text strong>Auto Switch</Typography.Text>
        <Switch data-testid="ai-auto-switch" loading={switching} checked={pool.autoSwitchEnabled} onChange={value => void changeAutoSwitch(value)} />
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
            <Col xs={24} md={6}><Form.Item label="Provider" required><Select value={editor.providerCode} options={providerOptions} onChange={providerCode => setEditor({ ...editor, providerCode, modelName: providerCode === 'OpenAI' ? 'gpt-4o-mini' : providerCode === 'Groq' ? 'groq/compound-mini' : '', endpointUrl: providerCode === 'OpenAICompatible' ? editor.endpointUrl : '', apiKey: '', hasApiKey: false })} /></Form.Item></Col>
            <Col xs={24} md={6}><Form.Item label="Model" required><Input data-testid="ai-scoring-model" value={editor.modelName} placeholder={modelExamples[editor.providerCode]} onChange={event => setEditor({ ...editor, modelName: event.target.value })} /></Form.Item></Col>
            <Col xs={24} md={7}><Form.Item label="API key" required={!editor.hasApiKey} extra={editor.hasApiKey ? 'Leave blank to keep the saved encrypted key.' : 'Encrypted before storage.'}><Input.Password data-testid="ai-scoring-api-key" autoComplete="new-password" value={editor.apiKey || ''} placeholder={editor.hasApiKey ? 'Configured securely' : 'Paste provider API key'} onChange={event => setEditor({ ...editor, apiKey: event.target.value })} /></Form.Item></Col>
            <Col xs={12} md={3}><Form.Item label="Monthly cap" extra="Requests"><InputNumber min={1} max={10_000_000} controls={false} value={editor.monthlyRequestLimit} onChange={value => setEditor({ ...editor, monthlyRequestLimit: Number(value || 1) })} style={{ width: '100%' }} /></Form.Item></Col>
            <Col xs={12} md={2}><Form.Item label="Order" extra="Lower first"><InputNumber min={1} max={9999} controls={false} value={editor.priority} onChange={value => setEditor({ ...editor, priority: Number(value || 100) })} style={{ width: '100%' }} /></Form.Item></Col>
            {editor.providerCode === 'OpenAICompatible' && <Col xs={24}><Form.Item label="HTTPS base URL" required extra="Example: https://ai.company.com/v1"><Input value={editor.endpointUrl} placeholder="https://provider.example/v1" onChange={event => setEditor({ ...editor, endpointUrl: event.target.value })} /></Form.Item></Col>}
          </Row>
          <Row justify="space-between" align="middle" gutter={[16, 12]}>
            <Col><Space><Switch data-testid="enable-ai-scoring" checked={editor.enableAiScoring} onChange={enableAiScoring => setEditor({ ...editor, enableAiScoring, isActive: enableAiScoring })} /><span>Available to Frevo and automatic failover</span></Space></Col>
            <Col><Space>
              {editor.id > 0 && <Button onClick={() => resetEditor()}>Cancel</Button>}
              <Button type="primary" icon={<ThunderboltOutlined />} loading={saving} disabled={saveDisabled} onClick={() => void save()}>{editor.id > 0 ? 'Update model' : 'Save model'}</Button>
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
          scroll={{ x: 900 }}
          locale={{ emptyText: 'No AI model saved yet. Add Gemini, OpenAI, Claude, Grok or a compatible provider above.' }}
        />
      </div>
    </Card>
  </section>
}
