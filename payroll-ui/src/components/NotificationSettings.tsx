import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import {
  ApiOutlined,
  CheckCircleFilled,
  ClockCircleOutlined,
  CopyOutlined,
  EditOutlined,
  FileTextOutlined,
  HistoryOutlined,
  LockOutlined,
  MailOutlined,
  MessageOutlined,
  PlusOutlined,
  SafetyCertificateOutlined,
  SendOutlined,
  SettingOutlined,
  ThunderboltOutlined,
  WhatsAppOutlined
} from '@ant-design/icons'
import { Alert, Button, Card, Col, Divider, Drawer, Empty, Form, Input, InputNumber, Row, Select, Space, Switch, Tabs, Tag, Tooltip } from 'antd'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'
import { getJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import {
  communicationWebhookUrl,
  getCommunicationProviders,
  getCommunicationTemplates,
  saveCommunicationProvider,
  saveCommunicationTemplate,
  testCommunicationProvider
} from '../services/communicationSettingsService'
import type {
  CommunicationChannel,
  CommunicationProviderAccount,
  CommunicationTemplate,
  CommunicationTemplateVariable,
  SaveCommunicationProviderAccount
} from '../services/communicationSettingsService'
import { getNotificationSetup, retryNotification, saveNotificationRule, saveNotificationSmtp, saveNotificationTemplate, sendNotificationTest } from '../services/notificationService'
import type { Client, NotificationParameterMapping, NotificationRecipient, NotificationRule, NotificationSetup, NotificationSmtpSetting, NotificationTemplate } from '../types/payroll'
import './NotificationSettings.css'

type Activity = { id: number; activityCode: string; displayName: string; moduleCode: string; resourceType: string; description: string; isActive: boolean }
type ProviderChannel = Exclude<CommunicationChannel, 'Email'>

const smtp0: NotificationSmtpSetting = { id: 1, isEnabled: false, deliveryPaused: false, host: '', port: 587, userName: '', password: '', enableSsl: true, fromEmail: '', fromName: '' }
const template0: NotificationTemplate = { id: 0, code: '', name: '', subjectTemplate: '', bodyTemplate: '', isHtml: true, isActive: true }
const rule0: NotificationRule = { id: 0, name: '', eventCode: '', clientId: null, clientName: '', templateId: 0, templateName: '', isEnabled: true, conditionJson: '{}', recipients: [], parameters: [] }
const recipient0 = (): NotificationRecipient => ({ id: 0, ruleId: 0, recipientType: 'To', sourceType: 'StaticEmail', sourceValue: '', tableName: '', matchColumn: '', matchValueSource: 'resourceId', emailColumn: '', isActive: true })
const parameter0 = (): NotificationParameterMapping => ({ id: 0, ruleId: 0, parameterName: '', sourceType: 'Payload', payloadPath: '', tableName: '', matchColumn: '', matchValueSource: 'resourceId', valueColumn: '', defaultValue: '', isActive: true })

const communicationTemplate0: CommunicationTemplate = {
  id: 0,
  clientId: null,
  clientName: '',
  channel: 'Email',
  code: '',
  name: '',
  subjectTemplate: '',
  bodyTemplate: '',
  providerTemplateCode: '',
  languageCode: 'en',
  isHtml: false,
  isActive: true,
  variables: []
}

const variable0 = (position = 1): CommunicationTemplateVariable => ({
  id: 0,
  templateId: 0,
  position,
  variableKey: position === 1 ? 'employeeName' : '',
  label: position === 1 ? 'Employee name' : '',
  sourceCode: position === 1 ? 'Employee.FullName' : '',
  isRequired: position === 1,
  fallbackValue: position === 1 ? 'Employee' : ''
})

const provider0 = (channel: ProviderChannel): SaveCommunicationProviderAccount => ({
  id: 0,
  clientId: null,
  clientName: '',
  channel,
  providerCode: '',
  accountName: '',
  baseUrl: '',
  apiVersion: '',
  senderId: '',
  phoneNumberId: '',
  businessAccountId: '',
  defaultCountryCode: '+91',
  defaultLanguageCode: 'en',
  requestTimeoutSeconds: 30,
  maximumMessagesPerMinute: 60,
  isEnabled: true,
  deliveryPaused: false,
  healthStatus: 'NotConfigured',
  lastHealthMessage: '',
  lastTestedAtUtc: null,
  hasApiKey: false,
  hasAccessToken: false,
  hasWebhookSecret: false,
  apiKey: '',
  accessToken: '',
  webhookSecret: ''
})

const sourceOptions = [
  ['Employee.FullName', 'Employee full name'],
  ['Employee.FirstName', 'Employee first name'],
  ['Employee.EmployeeCode', 'Employee code'],
  ['Employee.WorkEmail', 'Work email'],
  ['Employee.Mobile', 'Mobile number'],
  ['Employee.Department', 'Department'],
  ['Employee.Designation', 'Designation'],
  ['Employee.WorkLocation', 'Work location'],
  ['Client.Name', 'Client name'],
  ['CurrentUser.DisplayName', 'Sender name']
].map(([value, label]) => ({ value, label }))

function providerStatus(provider?: CommunicationProviderAccount) {
  if (!provider) return { label: 'Not configured', color: 'default' }
  if (provider.deliveryPaused) return { label: 'Paused', color: 'gold' }
  if (!provider.isEnabled) return { label: 'Disabled', color: 'default' }
  const value = (provider.healthStatus || '').toLowerCase()
  if (value === 'healthy' || value === 'connected') return { label: 'Healthy', color: 'green' }
  if (value === 'failed' || value === 'unhealthy' || value === 'error') return { label: 'Needs attention', color: 'red' }
  if (!provider.lastTestedAtUtc) return { label: 'Needs test', color: 'blue' }
  return { label: provider.healthStatus || 'Configured', color: 'blue' }
}

function channelIcon(channel: CommunicationChannel) {
  if (channel === 'Email') return <MailOutlined />
  if (channel === 'Sms') return <MessageOutlined />
  return <WhatsAppOutlined />
}

function channelLabel(channel: CommunicationChannel) {
  return channel === 'Sms' ? 'SMS' : channel
}

function statusTime(value?: string | null) {
  return value ? new Date(value).toLocaleString('en-IN') : 'Not tested yet'
}

function drawerTitle(eyebrow: string, title: string, description: string) {
  return <div className="settings-drawer-title"><span>{eyebrow}</span><h3>{title}</h3><p>{description}</p></div>
}

function ChannelHealthCard(props: {
  channel: CommunicationChannel
  title: string
  status: { label: string; color: string }
  description: string
  meta: string
  onConfigure: () => void
  onTest?: () => void
  testDisabled?: boolean
}) {
  const slug = props.channel.toLowerCase()
  return <Card className={`communication-health-card channel-${slug}`} data-testid={`communication-channel-${slug}`}>
    <div className="communication-health-card-head">
      <span className="communication-channel-icon">{channelIcon(props.channel)}</span>
      <Tag color={props.status.color}>{props.status.label}</Tag>
    </div>
    <h3>{props.title}</h3>
    <p>{props.description}</p>
    <span className="communication-channel-meta"><ClockCircleOutlined /> {props.meta}</span>
    <div className="communication-health-actions">
      <Button data-testid={`communication-channel-${slug}-configure`} icon={<SettingOutlined />} onClick={props.onConfigure}>Configure</Button>
      {props.onTest && <Button data-testid={`communication-channel-${slug}-test`} icon={<ApiOutlined />} disabled={props.testDisabled} onClick={props.onTest}>Test</Button>}
    </div>
  </Card>
}

export default function NotificationSettings() {
  const [setup, setSetup] = useState<NotificationSetup>({ smtp: smtp0, templates: [], rules: [], queue: [], logs: [] })
  const [clients, setClients] = useState<Client[]>([])
  const [activities, setActivities] = useState<Activity[]>([])
  const [providers, setProviders] = useState<CommunicationProviderAccount[]>([])
  const [communicationTemplates, setCommunicationTemplates] = useState<CommunicationTemplate[]>([])
  const [smtp, setSmtp] = useState<NotificationSmtpSetting>(smtp0)
  const [template, setTemplate] = useState<NotificationTemplate>(template0)
  const [templateOpen, setTemplateOpen] = useState(false)
  const [rule, setRule] = useState<NotificationRule>(rule0)
  const [ruleOpen, setRuleOpen] = useState(false)
  const [testEmail, setTestEmail] = useState('')
  const [activeTab, setActiveTab] = useState('channels')
  const [loading, setLoading] = useState(true)
  const [provider, setProvider] = useState<SaveCommunicationProviderAccount>(provider0('Sms'))
  const [providerOpen, setProviderOpen] = useState(false)
  const [providerSaving, setProviderSaving] = useState(false)
  const [testingProviderId, setTestingProviderId] = useState(0)
  const [communicationTemplate, setCommunicationTemplate] = useState<CommunicationTemplate>(communicationTemplate0)
  const [communicationTemplateOpen, setCommunicationTemplateOpen] = useState(false)
  const [communicationTemplateSaving, setCommunicationTemplateSaving] = useState(false)

  const load = async () => {
    try {
      const [notificationSetup, clientRows, activityRows, providerRows, communicationTemplateRows] = await Promise.all([
        getNotificationSetup(),
        getClients(),
        getJson<Activity[]>('/api/workflows/activities/catalog', []),
        getCommunicationProviders(),
        getCommunicationTemplates()
      ])
      setSetup(notificationSetup)
      setSmtp(notificationSetup.smtp ?? smtp0)
      setClients(clientRows)
      setActivities(activityRows.filter(item => item.isActive))
      setProviders(providerRows)
      setCommunicationTemplates(communicationTemplateRows)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [])

  const templateOptions = setup.templates.filter(item => item.isActive).map(item => ({ value: item.id, label: `${item.name} - ${item.code}` }))
  const activityOptions = activities.map(item => ({ value: item.activityCode, label: `${item.displayName} - ${item.moduleCode}` }))
  const selectedActivity = activities.find(item => item.activityCode === rule.eventCode)
  const selectedTemplate = setup.templates.find(item => item.id === rule.templateId)
  const templateHints = useMemo(() => ['{{eventCode}}', '{{resourceType}}', '{{resourceId}}', '{{clientId}}', '{{requestedBy}}', '{{requestedByEmail}}', '{{now}}'], [])
  const smsProvider = providers.find(item => item.channel === 'Sms')
  const whatsAppProvider = providers.find(item => item.channel === 'WhatsApp')
  const emailStatus = smtp.deliveryPaused
    ? { label: 'Paused', color: 'gold' }
    : smtp.isEnabled && smtp.host && smtp.fromEmail
      ? { label: 'Ready', color: 'green' }
      : smtp.isEnabled ? { label: 'Setup incomplete', color: 'red' } : { label: 'Disabled', color: 'default' }

  const updateRecipient = (index: number, changes: Partial<NotificationRecipient>) => setRule(current => ({ ...current, recipients: current.recipients.map((item, position) => position === index ? { ...item, ...changes } : item) }))
  const updateParameter = (index: number, changes: Partial<NotificationParameterMapping>) => setRule(current => ({ ...current, parameters: current.parameters.map((item, position) => position === index ? { ...item, ...changes } : item) }))
  const openRule = (row?: NotificationRule) => { setRule(row ? { ...row, recipients: row.recipients.length ? row.recipients : [recipient0()], parameters: row.parameters ?? [] } : { ...rule0, recipients: [recipient0()], parameters: [] }); setRuleOpen(true) }
  const openTemplate = (row?: NotificationTemplate) => { setTemplate(row ? { ...row } : template0); setTemplateOpen(true) }
  const saveSmtp = async () => { const response = await saveNotificationSmtp(smtp); if (response.ok) void load() }
  const saveTemplate = async () => { const response = await saveNotificationTemplate(template); if (response.ok) { setTemplate(template0); setTemplateOpen(false); void load() } }
  const saveRule = async () => { const response = await saveNotificationRule(rule); if (response.ok) { setRule(rule0); setRuleOpen(false); void load() } }
  const testRule = async () => { if (!rule.id || !testEmail) return; const response = await sendNotificationTest(rule.id, testEmail); if (response.ok) void load() }

  const openProvider = (channel: ProviderChannel, row?: CommunicationProviderAccount) => {
    setProvider(row ? { ...row, apiKey: '', accessToken: '', webhookSecret: '' } : provider0(channel))
    setProviderOpen(true)
  }

  const saveProvider = async () => {
    setProviderSaving(true)
    try {
      const response = await saveCommunicationProvider(provider)
      if (response.ok) {
        setProviderOpen(false)
        await load()
      }
    } finally {
      setProviderSaving(false)
    }
  }

  const testProvider = async (row: CommunicationProviderAccount) => {
    setTestingProviderId(row.id)
    try {
      await testCommunicationProvider(row.id)
      await load()
    } finally {
      setTestingProviderId(0)
    }
  }

  const openCommunicationTemplate = (row?: CommunicationTemplate) => {
    setCommunicationTemplate(row
      ? { ...row, variables: row.variables.map(item => ({ ...item })) }
      : { ...communicationTemplate0, variables: [variable0()] })
    setCommunicationTemplateOpen(true)
  }

  const saveEmployeeTemplate = async () => {
    setCommunicationTemplateSaving(true)
    try {
      const normalized = {
        ...communicationTemplate,
        variables: communicationTemplate.variables.map((item, index) => ({ ...item, position: index + 1 }))
      }
      const response = await saveCommunicationTemplate(normalized)
      if (response.ok) {
        setCommunicationTemplateOpen(false)
        await load()
      }
    } finally {
      setCommunicationTemplateSaving(false)
    }
  }

  const updateVariable = (index: number, changes: Partial<CommunicationTemplateVariable>) => setCommunicationTemplate(current => ({
    ...current,
    variables: current.variables.map((item, position) => position === index ? { ...item, ...changes } : item)
  }))

  const removeVariable = (index: number) => setCommunicationTemplate(current => ({
    ...current,
    variables: current.variables.filter((_, position) => position !== index).map((item, position) => ({ ...item, position: position + 1 }))
  }))

  const configureEmail = () => {
    setActiveTab('channels')
    window.setTimeout(() => document.getElementById('communication-email-settings')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50)
  }

  const copyWebhook = async () => {
    const value = communicationWebhookUrl(provider)
    if (value) await navigator.clipboard.writeText(value)
  }

  const mainTabs = [
    {
      key: 'channels',
      label: <span data-testid="communication-settings-providers-tab"><SettingOutlined /> Channels</span>,
      children: <ChannelConfiguration
        smtp={smtp}
        setSmtp={setSmtp}
        saveSmtp={saveSmtp}
        providers={providers}
        onConfigure={openProvider}
        onTest={testProvider}
        testingProviderId={testingProviderId}
      />
    },
    {
      key: 'templates',
      label: <span data-testid="communication-settings-templates-tab"><FileTextOutlined /> Templates</span>,
      children: <Tabs className="communication-template-tabs" type="card" items={[
        {
          key: 'employee',
          label: 'Employee communication',
          children: <>
            <div className="component-table-head">
              <div><b>Employee communication templates</b><span>Reusable, channel-specific content for individual and bulk employee messages.</span></div>
              <Button data-testid="communication-template-add" type="primary" icon={<PlusOutlined />} onClick={() => openCommunicationTemplate()}>New template</Button>
            </div>
            {communicationTemplates.length
              ? <DataTable rows={communicationTemplates} columns={[
                { key: 'name', label: 'Template', render: row => <div className="communication-table-primary"><b>{row.name}</b><span>{row.code}</span></div> },
                { key: 'channel', label: 'Channel', render: row => <Tag color={row.channel === 'Email' ? 'blue' : row.channel === 'Sms' ? 'purple' : 'green'} icon={channelIcon(row.channel)}>{channelLabel(row.channel)}</Tag> },
                { key: 'clientName', label: 'Scope', value: row => row.clientName || 'All clients' },
                { key: 'bodyTemplate', label: 'Message', render: row => <span className="communication-message-preview">{row.bodyTemplate}</span> },
                { key: 'variables', label: 'Variables', value: row => `${row.variables.length}` },
                { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
              ]} actions={row => <Button size="small" icon={<EditOutlined />} onClick={() => openCommunicationTemplate(row)}>Edit</Button>} />
              : <Empty className="communication-empty" image={Empty.PRESENTED_IMAGE_SIMPLE} description="No employee communication templates yet"><Button type="primary" onClick={() => openCommunicationTemplate()}>Create the first template</Button></Empty>}
          </>
        },
        {
          key: 'automation',
          label: 'Automation email templates',
          children: <>
            <div className="component-table-head"><div><b>Automation email templates</b><span>Existing event-driven subject and body formats remain unchanged.</span></div><Button type="primary" onClick={() => openTemplate()}>Add email template</Button></div>
            <DataTable rows={setup.templates} columns={[
              { key: 'name', label: 'Template', render: row => <div className="communication-table-primary"><b>{row.name}</b><span>{row.code}</span></div> },
              { key: 'subjectTemplate', label: 'Subject' },
              { key: 'isHtml', label: 'Format', render: row => row.isHtml ? 'HTML' : 'Text' },
              { key: 'isActive', label: 'Status', render: row => <Tag color={row.isActive ? 'green' : 'default'}>{row.isActive ? 'Active' : 'Inactive'}</Tag> }
            ]} actions={row => <Button size="small" type="primary" onClick={() => openTemplate(row)}>Edit</Button>} />
          </>
        }
      ]} />
    },
    {
      key: 'rules',
      label: <span><ThunderboltOutlined /> Automation Rules</span>,
      children: <>
        <div className="component-table-head"><div><b>Event-driven notification rules</b><span>Existing workflow automation and recipient resolution remain unchanged.</span></div><Button type="primary" icon={<PlusOutlined />} onClick={() => openRule()}>Add rule</Button></div>
        <DataTable rows={setup.rules} columns={[
          { key: 'name', label: 'Rule', render: row => <div className="communication-table-primary"><b>{row.name}</b><span>{row.eventCode}</span></div> },
          { key: 'clientName', label: 'Client' },
          { key: 'templateName', label: 'Template' },
          { key: 'recipients', label: 'Recipients', value: row => `${row.recipients.length}` },
          { key: 'isEnabled', label: 'Status', render: row => <Tag color={row.isEnabled ? 'green' : 'default'}>{row.isEnabled ? 'Enabled' : 'Disabled'}</Tag> }
        ]} actions={row => <Button size="small" type="primary" onClick={() => openRule(row)}>Edit</Button>} />
      </>
    },
    {
      key: 'delivery',
      label: <span><HistoryOutlined /> Delivery Monitor</span>,
      children: <>
        <div className="component-table-head"><div><b>Email delivery queue</b><span>Live retry state for existing automated email notifications.</span></div></div>
        <DataTable rows={setup.queue} exportFileName="notification-queue" columns={[
          { key: 'createdAt', label: 'Queued', value: row => row.createdAt ? new Date(row.createdAt).toLocaleString('en-IN') : '-' },
          { key: 'eventCode', label: 'Event' },
          { key: 'resourceId', label: 'Resource', value: row => `${row.resourceType} #${row.resourceId}` },
          { key: 'subject', label: 'Subject' },
          { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Sent' ? 'green' : row.status === 'Failed' ? 'red' : 'blue'}>{row.status}</Tag> },
          { key: 'errorMessage', label: 'Error' }
        ]} actions={row => row.status !== 'Sent' ? <Button size="small" onClick={() => void retryNotification(row.id).then(load)}>Retry</Button> : null} />
        <Divider />
        <div className="component-table-head"><div><b>Email delivery log</b><span>Immutable provider outcomes for completed delivery attempts.</span></div></div>
        <DataTable rows={setup.logs} exportFileName="notification-logs" columns={[
          { key: 'createdAt', label: 'Time', value: row => row.createdAt ? new Date(row.createdAt).toLocaleString('en-IN') : '-' },
          { key: 'eventCode', label: 'Event' },
          { key: 'recipient', label: 'Recipient' },
          { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Sent' ? 'green' : row.status === 'Failed' ? 'red' : 'blue'}>{row.status}</Tag> },
          { key: 'errorMessage', label: 'Error' }
        ]} />
      </>
    }
  ]

  return <section className="notification-settings communication-settings" data-testid="communication-settings-root">
    <div className="communication-settings-hero">
      <div className="communication-hero-copy">
        <span className="communication-eyebrow"><ThunderboltOutlined /> Employee engagement hub</span>
        <h2>Communication Settings</h2>
        <p>Configure reliable employee outreach across email, SMS and WhatsApp—with secure credentials, reusable templates and delivery visibility in one workspace.</p>
        <Space wrap><Tag icon={<SafetyCertificateOutlined />} color="green">Encrypted provider secrets</Tag><Tag icon={<CheckCircleFilled />} color="blue">Employee-level delivery audit</Tag></Space>
      </div>
      <div className="communication-hero-visual" aria-hidden="true"><SendOutlined /><span>Send</span><small>Email · SMS · WhatsApp</small></div>
    </div>

    <Row gutter={[14, 14]} className="communication-health-grid">
      <Col xs={24} lg={8}><ChannelHealthCard channel="Email" title="Business email" status={emailStatus} description="Your existing SMTP delivery engine for workflow and employee email." meta={smtp.isEnabled ? `${smtp.fromEmail || 'Sender pending'} · Port ${smtp.port}` : 'SMTP delivery is disabled'} onConfigure={configureEmail} /></Col>
      <Col xs={24} lg={8}><ChannelHealthCard channel="Sms" title="SMS messaging" status={providerStatus(smsProvider)} description="Direct mobile updates through a configured provider adapter." meta={smsProvider ? `Last tested ${statusTime(smsProvider.lastTestedAtUtc)}` : 'Provider account required'} onConfigure={() => openProvider('Sms', smsProvider)} onTest={smsProvider ? () => void testProvider(smsProvider) : undefined} testDisabled={!smsProvider?.id || testingProviderId === smsProvider.id} /></Col>
      <Col xs={24} lg={8}><ChannelHealthCard channel="WhatsApp" title="WhatsApp Business" status={providerStatus(whatsAppProvider)} description="Template-led employee messages with inbound delivery events." meta={whatsAppProvider ? `Last tested ${statusTime(whatsAppProvider.lastTestedAtUtc)}` : 'Provider account required'} onConfigure={() => openProvider('WhatsApp', whatsAppProvider)} onTest={whatsAppProvider ? () => void testProvider(whatsAppProvider) : undefined} testDisabled={!whatsAppProvider?.id || testingProviderId === whatsAppProvider.id} /></Col>
    </Row>

    <Card loading={loading} className="settings-panel settings-table-panel communication-workspace-card">
      <Tabs activeKey={activeTab} onChange={setActiveTab} items={mainTabs} />
    </Card>

    <Drawer
      className="settings-master-drawer communication-provider-drawer"
      data-testid="communication-provider-drawer"
      title={drawerTitle('Secure channel setup', `${channelLabel(provider.channel)} provider`, 'Configure one provider account. Blank secret fields keep the currently encrypted values unchanged.')}
      open={providerOpen}
      width={860}
      onClose={() => setProviderOpen(false)}
      destroyOnClose
      footer={<Space><Button onClick={() => setProviderOpen(false)}>Cancel</Button>{provider.id > 0 && <Button data-testid="communication-provider-drawer-test" loading={testingProviderId === provider.id} onClick={() => void testProvider(provider)}>Test connection</Button>}<Button data-testid="communication-provider-save" type="primary" loading={providerSaving} disabled={!provider.accountName.trim() || !provider.providerCode.trim() || !provider.baseUrl.trim()} onClick={() => void saveProvider()}>Save securely</Button></Space>}
    >
      <Form component={false} layout="vertical" requiredMark={false} className="settings-quick-form communication-provider-form">
        <Alert className="communication-provider-alert" type="info" showIcon message="Provider adapter required" description="Enter the provider code and API base URL supported by your deployment. A saved account is only marked healthy after a successful backend connection test." />
        <Form.Item label="Configuration scope"><Select value={provider.clientId ?? 0} onChange={value => setProvider({ ...provider, clientId: value ? Number(value) : null })} options={[{ value: 0, label: 'All clients (global)' }, ...clients.filter(item => item.isActive).map(item => ({ value: item.id, label: item.name }))]} /></Form.Item>
        <Form.Item label="Channel"><Select disabled={provider.id > 0} value={provider.channel} onChange={value => setProvider({ ...provider, channel: value })} options={[{ value: 'Sms', label: 'SMS' }, { value: 'WhatsApp', label: 'WhatsApp' }]} /></Form.Item>
        <Form.Item label="Account name"><Input value={provider.accountName} onChange={event => setProvider({ ...provider, accountName: event.target.value })} placeholder="India employee messaging" /></Form.Item>
        <Form.Item label="Provider code" tooltip="Must match an adapter registered by the backend deployment."><Input value={provider.providerCode} onChange={event => setProvider({ ...provider, providerCode: event.target.value })} placeholder="Provider adapter code" /></Form.Item>
        <Form.Item className="wide" label="API base URL"><Input value={provider.baseUrl} onChange={event => setProvider({ ...provider, baseUrl: event.target.value })} placeholder="https://api.provider.com" /></Form.Item>
        <Form.Item label="API version"><Input value={provider.apiVersion} onChange={event => setProvider({ ...provider, apiVersion: event.target.value })} placeholder="Optional" /></Form.Item>
        {provider.channel === 'Sms'
          ? <Form.Item label="Sender ID"><Input value={provider.senderId} onChange={event => setProvider({ ...provider, senderId: event.target.value })} placeholder="FREVOHR" /></Form.Item>
          : <><Form.Item label="Phone number ID"><Input value={provider.phoneNumberId} onChange={event => setProvider({ ...provider, phoneNumberId: event.target.value })} /></Form.Item><Form.Item label="Business account ID"><Input value={provider.businessAccountId} onChange={event => setProvider({ ...provider, businessAccountId: event.target.value })} /></Form.Item></>}
        <Form.Item label="Default country code"><Input value={provider.defaultCountryCode} onChange={event => setProvider({ ...provider, defaultCountryCode: event.target.value })} placeholder="+91" /></Form.Item>
        <Form.Item label="Default language"><Input value={provider.defaultLanguageCode} onChange={event => setProvider({ ...provider, defaultLanguageCode: event.target.value })} placeholder="en" /></Form.Item>
        <Form.Item label="Request timeout (seconds)"><InputNumber min={5} max={120} value={provider.requestTimeoutSeconds} onChange={value => setProvider({ ...provider, requestTimeoutSeconds: Number(value || 30) })} /></Form.Item>
        <Form.Item label="Rate limit / minute"><InputNumber min={1} max={100000} value={provider.maximumMessagesPerMinute} onChange={value => setProvider({ ...provider, maximumMessagesPerMinute: Number(value || 60) })} /></Form.Item>

        <div className="communication-form-section wide"><span><LockOutlined /> Encrypted credentials</span><p>Secrets are write-only. Saved values are never returned to this screen.</p></div>
        <Form.Item label={<SecretLabel label="API key" saved={provider.hasApiKey} />}><Input.Password autoComplete="new-password" value={provider.apiKey} onChange={event => setProvider({ ...provider, apiKey: event.target.value })} placeholder={provider.hasApiKey ? 'Leave blank to keep saved key' : 'Enter API key'} /></Form.Item>
        <Form.Item label={<SecretLabel label="Access token" saved={provider.hasAccessToken} />}><Input.Password autoComplete="new-password" value={provider.accessToken} onChange={event => setProvider({ ...provider, accessToken: event.target.value })} placeholder={provider.hasAccessToken ? 'Leave blank to keep saved token' : 'Enter access token'} /></Form.Item>
        <Form.Item className="wide" label={<SecretLabel label="Webhook secret" saved={provider.hasWebhookSecret} />}><Input.Password autoComplete="new-password" value={provider.webhookSecret} onChange={event => setProvider({ ...provider, webhookSecret: event.target.value })} placeholder={provider.hasWebhookSecret ? 'Leave blank to keep saved webhook secret' : 'Create a strong webhook secret'} /></Form.Item>

        <div className="communication-webhook-card wide">
          <div><span><ApiOutlined /> Inbound delivery webhook</span><p>Give this callback to your provider. Requests are accepted only when the encrypted webhook secret matches.</p></div>
          {communicationWebhookUrl(provider)
            ? <div className="communication-webhook-copy"><Input readOnly value={communicationWebhookUrl(provider)} /><Tooltip title="Copy callback URL"><Button aria-label="Copy callback URL" icon={<CopyOutlined />} onClick={() => void copyWebhook()} /></Tooltip></div>
            : <Alert type="warning" showIcon message="Save this provider account to generate its secured callback URL." />}
          <div className="communication-webhook-meta"><Tag color={provider.hasWebhookSecret ? 'green' : 'gold'}>{provider.hasWebhookSecret ? 'Webhook secret saved' : 'Webhook secret required'}</Tag><span>Last test: {statusTime(provider.lastTestedAtUtc)}</span></div>
        </div>

        <Form.Item label="Channel enabled"><Switch checked={provider.isEnabled} onChange={value => setProvider({ ...provider, isEnabled: value })} /></Form.Item>
        <Form.Item label="Pause outbound delivery"><Switch checked={provider.deliveryPaused} onChange={value => setProvider({ ...provider, deliveryPaused: value })} /></Form.Item>
      </Form>
    </Drawer>

    <Drawer
      className="settings-master-drawer communication-template-drawer"
      data-testid="communication-template-drawer"
      title={drawerTitle('Employee communication', communicationTemplate.id ? 'Edit message template' : 'Create message template', 'Keep messages consistent while personalizing every employee delivery.')}
      open={communicationTemplateOpen}
      width={980}
      onClose={() => setCommunicationTemplateOpen(false)}
      destroyOnClose
      footer={<Space><Button onClick={() => setCommunicationTemplateOpen(false)}>Cancel</Button><Button data-testid="communication-template-save" type="primary" loading={communicationTemplateSaving} disabled={!communicationTemplate.code.trim() || !communicationTemplate.name.trim() || !communicationTemplate.bodyTemplate.trim() || (communicationTemplate.channel === 'Email' && !communicationTemplate.subjectTemplate.trim())} onClick={() => void saveEmployeeTemplate()}>{communicationTemplate.id ? 'Update template' : 'Save template'}</Button></Space>}
    >
      <Form component={false} layout="vertical" requiredMark={false} className="settings-quick-form communication-template-form">
        <Form.Item label="Configuration scope"><Select value={communicationTemplate.clientId ?? 0} onChange={value => setCommunicationTemplate({ ...communicationTemplate, clientId: value ? Number(value) : null })} options={[{ value: 0, label: 'All clients (global)' }, ...clients.filter(item => item.isActive).map(item => ({ value: item.id, label: item.name }))]} /></Form.Item>
        <Form.Item label="Channel"><Select value={communicationTemplate.channel} onChange={value => setCommunicationTemplate({ ...communicationTemplate, channel: value, isHtml: value === 'Email' ? communicationTemplate.isHtml : false })} options={[{ value: 'Email', label: 'Email' }, { value: 'Sms', label: 'SMS' }, { value: 'WhatsApp', label: 'WhatsApp' }]} /></Form.Item>
        <Form.Item label="Template code"><Input value={communicationTemplate.code} onChange={event => setCommunicationTemplate({ ...communicationTemplate, code: event.target.value.toUpperCase().replace(/\s+/g, '_') })} placeholder="POLICY_UPDATE" /></Form.Item>
        <Form.Item label="Template name"><Input value={communicationTemplate.name} onChange={event => setCommunicationTemplate({ ...communicationTemplate, name: event.target.value })} placeholder="Policy update" /></Form.Item>
        {communicationTemplate.channel === 'Email' && <Form.Item className="wide" label="Email subject"><Input value={communicationTemplate.subjectTemplate} onChange={event => setCommunicationTemplate({ ...communicationTemplate, subjectTemplate: event.target.value })} placeholder="An update for {{employeeName}}" /></Form.Item>}
        <Form.Item className="wide" label="Message"><Input.TextArea rows={8} value={communicationTemplate.bodyTemplate} onChange={event => setCommunicationTemplate({ ...communicationTemplate, bodyTemplate: event.target.value })} placeholder="Hello {{employeeName}}, ..." showCount maxLength={communicationTemplate.channel === 'Sms' ? 918 : undefined} /></Form.Item>
        {communicationTemplate.channel === 'WhatsApp' && <Form.Item label="Provider template code"><Input value={communicationTemplate.providerTemplateCode} onChange={event => setCommunicationTemplate({ ...communicationTemplate, providerTemplateCode: event.target.value })} placeholder="employee_policy_update" /></Form.Item>}
        <Form.Item label="Language code"><Input value={communicationTemplate.languageCode} onChange={event => setCommunicationTemplate({ ...communicationTemplate, languageCode: event.target.value })} placeholder="en" /></Form.Item>
        {communicationTemplate.channel === 'Email' && <Form.Item label="HTML email"><Switch checked={communicationTemplate.isHtml} onChange={value => setCommunicationTemplate({ ...communicationTemplate, isHtml: value })} /></Form.Item>}
        <Form.Item label="Active"><Switch checked={communicationTemplate.isActive} onChange={value => setCommunicationTemplate({ ...communicationTemplate, isActive: value })} /></Form.Item>

        <Card className="communication-variable-card wide" size="small" title={<div><b>Personalization fields</b><span>Each placeholder is mapped to an approved HRMS data source.</span></div>} extra={<Button size="small" icon={<PlusOutlined />} onClick={() => setCommunicationTemplate(current => ({ ...current, variables: [...current.variables, variable0(current.variables.length + 1)] }))}>Add field</Button>}>
          {communicationTemplate.variables.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No personalization fields. The message will be identical for everyone." />}
          {communicationTemplate.variables.map((item, index) => <div className="communication-variable-row" key={`${item.id}-${index}`}>
            <span className="communication-variable-position">{index + 1}</span>
            <label><span>Placeholder</span><Input value={item.variableKey} onChange={event => updateVariable(index, { variableKey: event.target.value.replace(/[^a-zA-Z0-9_]/g, '') })} placeholder="employeeName" /></label>
            <label><span>Display label</span><Input value={item.label} onChange={event => updateVariable(index, { label: event.target.value })} placeholder="Employee name" /></label>
            <label className="source"><span>HRMS source</span><Select value={item.sourceCode || undefined} onChange={value => updateVariable(index, { sourceCode: value })} options={sourceOptions} placeholder="Select approved source" /></label>
            <label><span>Fallback</span><Input value={item.fallbackValue} onChange={event => updateVariable(index, { fallbackValue: event.target.value })} placeholder="Optional" /></label>
            <label className="required"><span>Required</span><Switch checked={item.isRequired} onChange={value => updateVariable(index, { isRequired: value })} /></label>
            <Space direction="vertical" size={4}>
              <Button size="small" disabled={!item.variableKey} onClick={() => setCommunicationTemplate(current => ({ ...current, bodyTemplate: `${current.bodyTemplate}{{${item.variableKey}}}` }))}>Insert</Button>
              <Button size="small" danger onClick={() => removeVariable(index)}>Remove</Button>
            </Space>
          </div>)}
        </Card>
        <Alert className="wide" type="success" showIcon message="Personalized safely" description="The backend resolves only the approved sources above and creates one independent delivery per employee." />
      </Form>
    </Drawer>

    <Drawer className="settings-master-drawer notification-template-drawer" title={template.id ? 'Edit email template' : 'Add email template'} open={templateOpen} width={780} onClose={() => setTemplateOpen(false)} destroyOnClose footer={<Space><Button onClick={() => setTemplateOpen(false)}>Cancel</Button><Button type="primary" onClick={() => void saveTemplate()}>{template.id ? 'Update template' : 'Save template'}</Button></Space>}>
      <Form className="settings-quick-form notification-form notification-template-form" component={false} layout="vertical" requiredMark={false}>
        <Form.Item label="Template code"><Input value={template.code} onChange={event => setTemplate({ ...template, code: event.target.value.toUpperCase() })} placeholder="PAYRUN_LOCKED" /></Form.Item>
        <Form.Item label="Template name"><Input value={template.name} onChange={event => setTemplate({ ...template, name: event.target.value })} placeholder="Payroll locked notification" /></Form.Item>
        <Form.Item label="Subject"><Input value={template.subjectTemplate} onChange={event => setTemplate({ ...template, subjectTemplate: event.target.value })} placeholder="Payroll {{resourceId}} is locked" /></Form.Item>
        <Form.Item label="Body" className="wide"><Input.TextArea rows={10} value={template.bodyTemplate} onChange={event => setTemplate({ ...template, bodyTemplate: event.target.value })} /></Form.Item>
        <Form.Item label="Basic tokens" className="wide"><Space wrap>{templateHints.map(item => <Button size="small" key={item} onClick={() => setTemplate({ ...template, bodyTemplate: `${template.bodyTemplate}${item}` })}>{item}</Button>)}</Space></Form.Item>
        <Form.Item label="HTML"><Switch checked={template.isHtml} onChange={value => setTemplate({ ...template, isHtml: value })} /></Form.Item>
        <Form.Item label="Active"><Switch checked={template.isActive} onChange={value => setTemplate({ ...template, isActive: value })} /></Form.Item>
      </Form>
    </Drawer>

    <Drawer className="settings-master-drawer notification-rule-drawer" title={rule.id ? 'Edit notification rule' : 'Add notification rule'} open={ruleOpen} width="min(1180px, 96vw)" onClose={() => setRuleOpen(false)} destroyOnClose footer={<Space><Input className="notification-test-input" placeholder="test@email.com" value={testEmail} onChange={event => setTestEmail(event.target.value)} disabled={!rule.id} /><Button disabled={!rule.id || !testEmail} onClick={() => void testRule()}>Test</Button><Button onClick={() => setRuleOpen(false)}>Cancel</Button><Button type="primary" onClick={() => void saveRule()}>Save rule</Button></Space>}>
      <div className="settings-quick-form notification-form">
        <Card size="small" className="notification-rule-section" title="When should this mail go?">
          <Row gutter={12}>
            <Col xs={24} lg={12}><Form.Item label="Rule name"><Input value={rule.name} onChange={event => setRule({ ...rule, name: event.target.value })} /></Form.Item></Col>
            <Col xs={24} lg={12}><Form.Item label="Event / activity"><SearchSelect value={rule.eventCode} onChange={value => setRule({ ...rule, eventCode: value })} options={[{ value: '', label: 'Select event' }, ...activityOptions]} /></Form.Item></Col>
            <Col xs={24} lg={12}><Form.Item label="Client"><SearchSelect value={rule.clientId ?? ''} onChange={value => setRule({ ...rule, clientId: value ? Number(value) : null })} options={[{ value: '', label: 'All clients' }, ...clients.filter(item => item.isActive).map(item => ({ value: item.id, label: item.name }))]} /></Form.Item></Col>
            <Col xs={24} lg={12}><Form.Item label="Template"><SearchSelect value={rule.templateId || ''} onChange={value => setRule({ ...rule, templateId: Number(value || 0) })} options={[{ value: '', label: 'Select template' }, ...templateOptions]} /></Form.Item></Col>
            <Col xs={24} md={6}><Form.Item label="Enabled"><Switch checked={rule.isEnabled} onChange={value => setRule({ ...rule, isEnabled: value })} /></Form.Item></Col>
            <Col xs={24} md={6}><Form.Item label="Record type"><Input value={selectedActivity?.resourceType || ''} readOnly /></Form.Item></Col>
            <Col xs={24}><small>{selectedActivity?.description || 'Rules listen to configured workflow activities. First create an activity/start rule if the event is not listed.'}</small></Col>
          </Row>
        </Card>
        <Card size="small" className="notification-rule-section" title="Recipients">
          {rule.recipients.map((recipient, index) => <Card size="small" key={`${recipient.id}-${index}`} className="notification-line-card">
            <div className={`notification-recipient-grid ${recipient.sourceType === 'Lookup' ? 'lookup' : ''}`}>
              <label><span>Send as</span><Select value={recipient.recipientType} onChange={value => updateRecipient(index, { recipientType: value })} options={['To', 'Cc', 'Bcc'].map(value => ({ value, label: value }))} /></label>
              <label><span>Recipient source</span><Select value={recipient.sourceType} onChange={value => updateRecipient(index, { sourceType: value })} options={['StaticEmail', 'UserRole', 'RequestorEmail', 'ReportingManager', 'PayloadEmail', 'Lookup'].map(value => ({ value, label: value }))} /></label>
              {recipient.sourceType !== 'Lookup' && recipient.sourceType !== 'RequestorEmail' && recipient.sourceType !== 'ReportingManager' && <label className="wide"><span>{recipient.sourceType === 'UserRole' ? 'Role code/name' : recipient.sourceType === 'PayloadEmail' ? 'Payload email field' : 'Email address'}</span><Input value={recipient.sourceValue} onChange={event => updateRecipient(index, { sourceValue: event.target.value })} placeholder={recipient.sourceType === 'UserRole' ? 'payroll_approver' : recipient.sourceType === 'PayloadEmail' ? 'requestedByEmail' : 'email@company.com'} /></label>}
              {recipient.sourceType === 'Lookup' && <><label><span>Table</span><Input value={recipient.tableName} onChange={event => updateRecipient(index, { tableName: event.target.value })} placeholder="employees" /></label><label><span>Match column</span><Input value={recipient.matchColumn} onChange={event => updateRecipient(index, { matchColumn: event.target.value })} placeholder="Id" /></label><label><span>Match value</span><Input value={recipient.matchValueSource} onChange={event => updateRecipient(index, { matchValueSource: event.target.value })} placeholder="resourceId" /></label><label><span>Email column</span><Input value={recipient.emailColumn} onChange={event => updateRecipient(index, { emailColumn: event.target.value })} placeholder="WorkEmail" /></label></>}
              <Button danger onClick={() => setRule({ ...rule, recipients: rule.recipients.filter((_, position) => position !== index) })}>Remove</Button>
            </div>
          </Card>)}
          <Button onClick={() => setRule({ ...rule, recipients: [...rule.recipients, recipient0()] })}>Add recipient</Button>
        </Card>
        <Card size="small" className="notification-rule-section" title="Template parameters">
          <p className="muted-note">Use these mappings when a template needs extra values like client name or pay period. Lookup uses table name, match column, value source, and output column.</p>
          {rule.parameters.map((parameter, index) => <Card size="small" key={`${parameter.id}-${index}`} className="notification-line-card">
            <div className={`notification-parameter-grid ${parameter.sourceType === 'Lookup' ? 'lookup' : ''}`}>
              <label><span>Parameter</span><Input value={parameter.parameterName} onChange={event => updateParameter(index, { parameterName: event.target.value })} placeholder="clientName" /></label>
              <label><span>Source</span><Select value={parameter.sourceType} onChange={value => updateParameter(index, { sourceType: value })} options={['Payload', 'Lookup'].map(value => ({ value, label: value }))} /></label>
              {parameter.sourceType === 'Payload' && <label className="wide"><span>Payload field</span><Input value={parameter.payloadPath} onChange={event => updateParameter(index, { payloadPath: event.target.value })} placeholder="requestedByEmail" /></label>}
              {parameter.sourceType === 'Lookup' && <><label><span>Table</span><Input value={parameter.tableName} onChange={event => updateParameter(index, { tableName: event.target.value })} placeholder="payruns" /></label><label><span>Match column</span><Input value={parameter.matchColumn} onChange={event => updateParameter(index, { matchColumn: event.target.value })} placeholder="Id" /></label><label><span>Match value</span><Input value={parameter.matchValueSource} onChange={event => updateParameter(index, { matchValueSource: event.target.value })} placeholder="resourceId" /></label><label><span>Value column</span><Input value={parameter.valueColumn} onChange={event => updateParameter(index, { valueColumn: event.target.value })} placeholder="PayPeriod" /></label></>}
              <label><span>Default</span><Input value={parameter.defaultValue} onChange={event => updateParameter(index, { defaultValue: event.target.value })} placeholder="default" /></label>
              <Button danger onClick={() => setRule({ ...rule, parameters: rule.parameters.filter((_, position) => position !== index) })}>Remove</Button>
            </div>
          </Card>)}
          <Button onClick={() => setRule({ ...rule, parameters: [...rule.parameters, parameter0()] })}>Add parameter</Button>
        </Card>
        {selectedTemplate && <Card className="notification-rule-section notification-preview-card" size="small" title="Selected template preview"><b>{selectedTemplate.subjectTemplate}</b><div dangerouslySetInnerHTML={{ __html: selectedTemplate.bodyTemplate }} /></Card>}
      </div>
    </Drawer>
  </section>
}

function SecretLabel({ label, saved }: { label: string; saved: boolean }) {
  return <span className="communication-secret-label"><span>{label}</span><Tag color={saved ? 'green' : 'default'}>{saved ? 'Saved' : 'Not saved'}</Tag></span>
}

function ChannelConfiguration(props: {
  smtp: NotificationSmtpSetting
  setSmtp: (value: NotificationSmtpSetting) => void
  saveSmtp: () => Promise<void>
  providers: CommunicationProviderAccount[]
  onConfigure: (channel: ProviderChannel, row?: CommunicationProviderAccount) => void
  onTest: (row: CommunicationProviderAccount) => Promise<void>
  testingProviderId: number
}) {
  return <div className="communication-channel-workspace">
    <Card id="communication-email-settings" className="communication-config-card communication-email-config" title={<ChannelTitle icon={<MailOutlined />} title="Email / SMTP" description="Existing production email delivery configuration" />} extra={<Tag color={props.smtp.deliveryPaused ? 'gold' : props.smtp.isEnabled ? 'green' : 'default'}>{props.smtp.deliveryPaused ? 'Paused' : props.smtp.isEnabled ? 'Enabled' : 'Disabled'}</Tag>}>
      <div className="settings-quick-form notification-form">
        <Row gutter={[14, 4]}>
          <Col xs={24} md={6}><Form.Item label="Enable SMTP"><Switch checked={props.smtp.isEnabled} onChange={value => props.setSmtp({ ...props.smtp, isEnabled: value })} /></Form.Item></Col>
          <Col xs={24} md={6}><Form.Item label="Pause delivery"><Switch checked={props.smtp.deliveryPaused} onChange={value => props.setSmtp({ ...props.smtp, deliveryPaused: value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="SMTP host"><Input value={props.smtp.host} onChange={event => props.setSmtp({ ...props.smtp, host: event.target.value })} placeholder="smtp.office365.com" /></Form.Item></Col>
          <Col xs={24} md={4}><Form.Item label="Port"><InputNumber min={1} max={65535} value={props.smtp.port} onChange={value => props.setSmtp({ ...props.smtp, port: Number(value || 587) })} /></Form.Item></Col>
          <Col xs={24} md={4}><Form.Item label="SSL"><Switch checked={props.smtp.enableSsl} onChange={value => props.setSmtp({ ...props.smtp, enableSsl: value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="User name"><Input value={props.smtp.userName} onChange={event => props.setSmtp({ ...props.smtp, userName: event.target.value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="Password"><Input.Password value={props.smtp.password} onChange={event => props.setSmtp({ ...props.smtp, password: event.target.value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="From email"><Input value={props.smtp.fromEmail} onChange={event => props.setSmtp({ ...props.smtp, fromEmail: event.target.value })} /></Form.Item></Col>
          <Col xs={24} md={8}><Form.Item label="From name"><Input value={props.smtp.fromName} onChange={event => props.setSmtp({ ...props.smtp, fromName: event.target.value })} /></Form.Item></Col>
        </Row>
        <div className="communication-config-footer"><p><SafetyCertificateOutlined /> When SMTP is disabled or paused, existing emails remain queued without consuming retries.</p><Button type="primary" onClick={() => void props.saveSmtp()}>Save SMTP</Button></div>
      </div>
    </Card>

    {(['Sms', 'WhatsApp'] as ProviderChannel[]).map(channel => {
      const rows = props.providers.filter(item => item.channel === channel)
      return <Card key={channel} className={`communication-config-card communication-${channel.toLowerCase()}-config`} title={<ChannelTitle icon={channelIcon(channel)} title={`${channelLabel(channel)} provider accounts`} description="Client-specific or global secure delivery configuration" />} extra={<Button type="primary" size="small" icon={<PlusOutlined />} onClick={() => props.onConfigure(channel)}>Add account</Button>}>
        {rows.length === 0
          ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={`No ${channelLabel(channel)} provider configured`}><Button onClick={() => props.onConfigure(channel)}>Configure provider</Button></Empty>
          : <div className="communication-provider-list">{rows.map(row => {
            const status = providerStatus(row)
            return <div className="communication-provider-row" key={row.id}>
              <span className="communication-provider-logo">{channelIcon(channel)}</span>
              <div className="communication-provider-main"><b>{row.accountName}</b><span>{row.providerCode} · {row.clientName || 'All clients'}</span></div>
              <div className="communication-provider-health"><Tag color={status.color}>{status.label}</Tag><small>{row.lastHealthMessage || `Last tested ${statusTime(row.lastTestedAtUtc)}`}</small></div>
              <Space><Button size="small" icon={<ApiOutlined />} loading={props.testingProviderId === row.id} onClick={() => void props.onTest(row)}>Test</Button><Button size="small" type="primary" icon={<EditOutlined />} onClick={() => props.onConfigure(channel, row)}>Configure</Button></Space>
            </div>
          })}</div>}
      </Card>
    })}
  </div>
}

function ChannelTitle({ icon, title, description }: { icon: ReactNode; title: string; description: string }) {
  return <div className="communication-card-title"><span>{icon}</span><div><b>{title}</b><small>{description}</small></div></div>
}
