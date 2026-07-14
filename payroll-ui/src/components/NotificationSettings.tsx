import { useEffect, useMemo, useState } from 'react'
import { Button, Card, Col, Divider, Drawer, Form, Input, InputNumber, Row, Select, Space, Switch, Tabs } from 'antd'
import DataTable from './DataTable'
import SearchSelect from './SearchSelect'
import { getJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import { getNotificationSetup, retryNotification, saveNotificationRule, saveNotificationSmtp, saveNotificationTemplate, sendNotificationTest } from '../services/notificationService'
import type { Client, NotificationParameterMapping, NotificationRecipient, NotificationRule, NotificationSetup, NotificationSmtpSetting, NotificationTemplate } from '../types/payroll'

type Activity = { id: number; activityCode: string; displayName: string; moduleCode: string; resourceType: string; description: string; isActive: boolean }

const smtp0: NotificationSmtpSetting = { id: 1, isEnabled: false, deliveryPaused: false, host: '', port: 587, userName: '', password: '', enableSsl: true, fromEmail: '', fromName: '' }
const template0: NotificationTemplate = { id: 0, code: '', name: '', subjectTemplate: '', bodyTemplate: '', isHtml: true, isActive: true }
const rule0: NotificationRule = { id: 0, name: '', eventCode: '', clientId: null, clientName: '', templateId: 0, templateName: '', isEnabled: true, conditionJson: '{}', recipients: [], parameters: [] }
const recipient0 = (): NotificationRecipient => ({ id: 0, ruleId: 0, recipientType: 'To', sourceType: 'StaticEmail', sourceValue: '', tableName: '', matchColumn: '', matchValueSource: 'resourceId', emailColumn: '', isActive: true })
const parameter0 = (): NotificationParameterMapping => ({ id: 0, ruleId: 0, parameterName: '', sourceType: 'Payload', payloadPath: '', tableName: '', matchColumn: '', matchValueSource: 'resourceId', valueColumn: '', defaultValue: '', isActive: true })

export default function NotificationSettings() {
  const [setup, setSetup] = useState<NotificationSetup>({ smtp: smtp0, templates: [], rules: [], queue: [], logs: [] })
  const [clients, setClients] = useState<Client[]>([])
  const [activities, setActivities] = useState<Activity[]>([])
  const [smtp, setSmtp] = useState<NotificationSmtpSetting>(smtp0)
  const [template, setTemplate] = useState<NotificationTemplate>(template0)
  const [templateOpen, setTemplateOpen] = useState(false)
  const [rule, setRule] = useState<NotificationRule>(rule0)
  const [ruleOpen, setRuleOpen] = useState(false)
  const [testEmail, setTestEmail] = useState('')

  const load = async () => {
    const [notificationSetup, clientRows, activityRows] = await Promise.all([getNotificationSetup(), getClients(), getJson<Activity[]>('/api/workflows/activities/catalog', [])])
    setSetup(notificationSetup)
    setSmtp(notificationSetup.smtp ?? smtp0)
    setClients(clientRows)
    setActivities(activityRows.filter(item => item.isActive))
  }

  useEffect(() => { void load() }, [])

  const templateOptions = setup.templates.filter(item => item.isActive).map(item => ({ value: item.id, label: `${item.name} - ${item.code}` }))
  const activityOptions = activities.map(item => ({ value: item.activityCode, label: `${item.displayName} - ${item.moduleCode}` }))
  const selectedActivity = activities.find(item => item.activityCode === rule.eventCode)
  const selectedTemplate = setup.templates.find(item => item.id === rule.templateId)
  const templateHints = useMemo(() => ['{{eventCode}}', '{{resourceType}}', '{{resourceId}}', '{{clientId}}', '{{requestedBy}}', '{{requestedByEmail}}', '{{now}}'], [])

  const updateRecipient = (index: number, changes: Partial<NotificationRecipient>) => setRule(current => ({ ...current, recipients: current.recipients.map((item, position) => position === index ? { ...item, ...changes } : item) }))
  const updateParameter = (index: number, changes: Partial<NotificationParameterMapping>) => setRule(current => ({ ...current, parameters: current.parameters.map((item, position) => position === index ? { ...item, ...changes } : item) }))
  const openRule = (row?: NotificationRule) => { setRule(row ? { ...row, recipients: row.recipients.length ? row.recipients : [recipient0()], parameters: row.parameters ?? [] } : { ...rule0, recipients: [recipient0()], parameters: [] }); setRuleOpen(true) }
  const openTemplate = (row?: NotificationTemplate) => { setTemplate(row ? { ...row } : template0); setTemplateOpen(true) }
  const saveSmtp = async () => { const response = await saveNotificationSmtp(smtp); if (response.ok) void load() }
  const saveTemplate = async () => { const response = await saveNotificationTemplate(template); if (response.ok) { setTemplate(template0); setTemplateOpen(false); void load() } }
  const saveRule = async () => { const response = await saveNotificationRule(rule); if (response.ok) { setRule(rule0); setRuleOpen(false); void load() } }
  const testRule = async () => { if (!rule.id || !testEmail) return; const response = await sendNotificationTest(rule.id, testEmail); if (response.ok) void load() }

  return <section className="notification-settings">
    <Card title="Notification Settings" size="small" className="settings-panel settings-table-panel">
      <Tabs items={[
        { key: 'smtp', label: 'SMTP', children: <div className="settings-quick-form notification-form">
          <Row gutter={12}>
            <Col xs={24} md={6}><Form.Item label="Enable SMTP"><Switch checked={smtp.isEnabled} onChange={value => setSmtp({ ...smtp, isEnabled: value })} /></Form.Item></Col>
            <Col xs={24} md={6}><Form.Item label="Pause delivery"><Switch checked={smtp.deliveryPaused} onChange={value => setSmtp({ ...smtp, deliveryPaused: value })} /></Form.Item></Col>
            <Col xs={24} md={8}><Form.Item label="SMTP host"><Input value={smtp.host} onChange={event => setSmtp({ ...smtp, host: event.target.value })} placeholder="smtp.office365.com" /></Form.Item></Col>
            <Col xs={24} md={4}><Form.Item label="Port"><InputNumber min={1} max={65535} value={smtp.port} onChange={value => setSmtp({ ...smtp, port: Number(value || 587) })} /></Form.Item></Col>
            <Col xs={24} md={4}><Form.Item label="SSL"><Switch checked={smtp.enableSsl} onChange={value => setSmtp({ ...smtp, enableSsl: value })} /></Form.Item></Col>
            <Col xs={24} md={8}><Form.Item label="User name"><Input value={smtp.userName} onChange={event => setSmtp({ ...smtp, userName: event.target.value })} /></Form.Item></Col>
            <Col xs={24} md={8}><Form.Item label="Password"><Input.Password value={smtp.password} onChange={event => setSmtp({ ...smtp, password: event.target.value })} /></Form.Item></Col>
            <Col xs={24} md={8}><Form.Item label="From email"><Input value={smtp.fromEmail} onChange={event => setSmtp({ ...smtp, fromEmail: event.target.value })} /></Form.Item></Col>
            <Col xs={24} md={8}><Form.Item label="From name"><Input value={smtp.fromName} onChange={event => setSmtp({ ...smtp, fromName: event.target.value })} /></Form.Item></Col>
          </Row>
          <p className="form-helper-text">When SMTP is disabled or delivery is paused, emails remain queued as Pending. Retry counts are not consumed.</p>
          <Row justify="end"><Button type="primary" onClick={() => void saveSmtp()}>Save SMTP</Button></Row>
        </div> },
        { key: 'templates', label: 'Templates', children: <>
          <div className="component-table-head"><div><b>Email templates</b><span>Maintain reusable subject/body formats with dynamic parameters.</span></div><Space className="settings-master-actions" size={8} wrap><Button type="primary" onClick={() => openTemplate()}>Add template</Button></Space></div>
          <DataTable rows={setup.templates} columns={[{ key: 'name', label: 'Template', render: row => <>{row.name}<small>{row.code}</small></> }, { key: 'subjectTemplate', label: 'Subject' }, { key: 'isHtml', label: 'Format', render: row => row.isHtml ? 'HTML' : 'Text' }, { key: 'isActive', label: 'Status', render: row => row.isActive ? 'Active' : 'Inactive' }]} actions={row => <Button size="small" type="primary" onClick={() => openTemplate(row)}>Edit</Button>} />
        </> },
        { key: 'rules', label: 'Rules', children: <>
          <Row justify="end" className="settings-master-actions"><Button type="primary" onClick={() => openRule()}>Add rule</Button></Row>
          <DataTable rows={setup.rules} columns={[
            { key: 'name', label: 'Rule', render: row => <>{row.name}<small>{row.eventCode}</small></> },
            { key: 'clientName', label: 'Client' },
            { key: 'templateName', label: 'Template' },
            { key: 'recipients', label: 'Recipients', value: row => `${row.recipients.length}` },
            { key: 'isEnabled', label: 'Status', render: row => row.isEnabled ? 'Enabled' : 'Disabled' }
          ]} actions={row => <Button size="small" type="primary" onClick={() => openRule(row)}>Edit</Button>} />
        </> },
        { key: 'queue', label: 'Queue & Logs', children: <>
          <DataTable rows={setup.queue} exportFileName="notification-queue" columns={[
            { key: 'createdAt', label: 'Queued', value: row => row.createdAt ? new Date(row.createdAt).toLocaleString('en-IN') : '-' },
            { key: 'eventCode', label: 'Event' },
            { key: 'resourceId', label: 'Resource', value: row => `${row.resourceType} #${row.resourceId}` },
            { key: 'subject', label: 'Subject' },
            { key: 'status', label: 'Status' },
            { key: 'errorMessage', label: 'Error' }
          ]} actions={row => row.status !== 'Sent' ? <Button size="small" onClick={() => void retryNotification(row.id).then(load)}>Retry</Button> : null} />
          <Divider />
          <DataTable rows={setup.logs} exportFileName="notification-logs" columns={[
            { key: 'createdAt', label: 'Time', value: row => row.createdAt ? new Date(row.createdAt).toLocaleString('en-IN') : '-' },
            { key: 'eventCode', label: 'Event' },
            { key: 'recipient', label: 'Recipient' },
            { key: 'status', label: 'Status' },
            { key: 'errorMessage', label: 'Error' }
          ]} />
        </> }
      ]} />
    </Card>
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
