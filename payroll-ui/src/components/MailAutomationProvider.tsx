import { useCallback, useEffect, useMemo, useState } from 'react'
import { Alert, Button, Divider, Drawer, Empty, Select, Spin, Switch, Tag } from 'antd'
import { ApiOutlined, CheckCircleOutlined, MailOutlined, SettingOutlined, TeamOutlined } from '@ant-design/icons'
import type { Client, NotificationAutomationEvent, NotificationRecipient, NotificationRule, NotificationSetup, NotificationStakeholderOption } from '../types/payroll'
import { getClients } from '../services/payrollService'
import { getNotificationAutomationCatalog, getNotificationSetup, previewNotificationStakeholders, saveNotificationRule } from '../services/notificationService'
import './MailAutomationProvider.css'

type CapturedAction = {
  label: string
  eventCode: string
  resourceType: string
  resourceId: string
  clientId: number | null
}

type RecipientKind = 'To' | 'Cc' | 'Bcc'
type RecipientState = Record<RecipientKind, string[]>

const emptyRecipients = (): RecipientState => ({ To: [], Cc: [], Bcc: [] })
const automationPrefix = 'Action mail · '

export default function MailAutomationProvider({ enabled }: { enabled: boolean }) {
  const [open, setOpen] = useState(false)
  const [captured, setCaptured] = useState<CapturedAction>({ label: '', eventCode: '', resourceType: '', resourceId: '', clientId: null })
  const [events, setEvents] = useState<NotificationAutomationEvent[]>([])
  const [setup, setSetup] = useState<NotificationSetup | null>(null)
  const [clients, setClients] = useState<Client[]>([])
  const [selectedEventCode, setSelectedEventCode] = useState('')
  const [clientId, setClientId] = useState<number | null>(null)
  const [templateId, setTemplateId] = useState<number | undefined>()
  const [recipients, setRecipients] = useState<RecipientState>(emptyRecipients)
  const [stakeholders, setStakeholders] = useState<NotificationStakeholderOption[]>([])
  const [isEnabled, setIsEnabled] = useState(true)
  const [loading, setLoading] = useState(false)
  const [previewing, setPreviewing] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  const selectedEvent = useMemo(() => events.find(item => item.eventCode === selectedEventCode), [events, selectedEventCode])
  const activeTemplates = useMemo(() => setup?.templates.filter(item => item.isActive) ?? [], [setup])
  const existingRule = useMemo(() => setup?.rules.find(rule => rule.name.startsWith(automationPrefix) && rule.eventCode === selectedEventCode && (rule.clientId ?? null) === clientId), [clientId, selectedEventCode, setup])

  const hydrateRule = useCallback((rule?: NotificationRule) => {
    if (!rule) {
      setTemplateId(undefined)
      setRecipients(emptyRecipients())
      setIsEnabled(true)
      return
    }
    setTemplateId(rule.templateId)
    setIsEnabled(rule.isEnabled)
    const next = emptyRecipients()
    rule.recipients.filter(item => item.sourceType === 'Stakeholder' && item.isActive).forEach(item => {
      if (item.recipientType in next) next[item.recipientType].push(item.sourceValue)
    })
    setRecipients(next)
  }, [])

  useEffect(() => {
    if (!enabled) return
    const onContextMenu = (event: MouseEvent) => {
      const target = event.target instanceof Element ? event.target.closest<HTMLElement>('button,[role="button"],a[data-mail-event]') : null
      if (!target || target.closest('.mail-automation-drawer')) return
      event.preventDefault()
      const numericClientId = Number(target.dataset.mailClientId)
      const next: CapturedAction = {
        label: target.dataset.mailActionLabel || target.getAttribute('aria-label') || target.textContent?.replace(/\s+/g, ' ').trim() || 'Selected action',
        eventCode: target.dataset.mailEvent || '',
        resourceType: target.dataset.mailResourceType || '',
        resourceId: target.dataset.mailResourceId || '',
        clientId: Number.isFinite(numericClientId) && numericClientId > 0 ? numericClientId : null,
      }
      setCaptured(next)
      setSelectedEventCode(next.eventCode)
      setClientId(next.clientId)
      setMessage(null)
      setOpen(true)
    }
    document.addEventListener('contextmenu', onContextMenu, true)
    return () => document.removeEventListener('contextmenu', onContextMenu, true)
  }, [enabled])

  useEffect(() => {
    if (!open) return
    let cancelled = false
    setLoading(true)
    void Promise.all([getNotificationAutomationCatalog(), getNotificationSetup(), getClients()]).then(([catalog, notificationSetup, clientRows]) => {
      if (cancelled) return
      setEvents(catalog.events)
      setSetup(notificationSetup)
      setClients(clientRows)
    }).finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [open])

  useEffect(() => {
    if (!setup || !selectedEventCode) return
    hydrateRule(setup.rules.find(rule => rule.name.startsWith(automationPrefix) && rule.eventCode === selectedEventCode && (rule.clientId ?? null) === clientId))
  }, [clientId, hydrateRule, selectedEventCode, setup])

  useEffect(() => {
    if (!open || !selectedEventCode || !selectedEvent) {
      setStakeholders([])
      return
    }
    let cancelled = false
    setPreviewing(true)
    void previewNotificationStakeholders({
      eventCode: selectedEventCode,
      resourceType: captured.resourceType || selectedEvent.resourceType,
      resourceId: captured.resourceId,
      clientId,
    }).then(result => {
      if (!cancelled) setStakeholders(result.ok ? result.data.stakeholders : [])
    }).finally(() => { if (!cancelled) setPreviewing(false) })
    return () => { cancelled = true }
  }, [captured.resourceId, captured.resourceType, clientId, open, selectedEvent, selectedEventCode])

  const recipientOptions = stakeholders.map(item => ({
    value: item.code,
    label: `${item.label}${item.people.length ? ` — ${item.people.map(person => `${person.displayName} <${person.email}>`).join(', ')}` : ' — resolves when action runs'}`,
  }))

  const updateRecipients = (kind: RecipientKind, values: string[]) => setRecipients(current => ({ ...current, [kind]: values }))
  const makeRecipient = (kind: RecipientKind, code: string): NotificationRecipient => ({ id: 0, ruleId: existingRule?.id ?? 0, recipientType: kind, sourceType: 'Stakeholder', sourceValue: code, tableName: '', matchColumn: '', matchValueSource: 'resourceId', emailColumn: '', isActive: true })

  const save = async () => {
    if (!selectedEvent || !templateId || !recipients.To.length) {
      setMessage({ type: 'error', text: 'Choose an event, template, and at least one To recipient.' })
      return
    }
    setSaving(true)
    setMessage(null)
    const selectedClient = clients.find(item => item.id === clientId)
    const rule: NotificationRule = {
      id: existingRule?.id ?? 0,
      name: `${automationPrefix}${selectedEvent.displayName}${clientId ? ` · ${selectedClient?.name || `Client ${clientId}`}` : ' · All clients'}`,
      eventCode: selectedEvent.eventCode,
      clientId,
      clientName: selectedClient?.name || 'All clients',
      templateId,
      templateName: activeTemplates.find(item => item.id === templateId)?.name || '',
      isEnabled,
      conditionJson: '{}',
      recipients: (Object.entries(recipients) as Array<[RecipientKind, string[]]>).flatMap(([kind, codes]) => codes.map(code => makeRecipient(kind, code))),
      parameters: existingRule?.parameters ?? [],
    }
    const result = await saveNotificationRule(rule)
    if (result.ok) {
      setSetup(current => current ? { ...current, rules: [...current.rules.filter(item => item.id !== result.data.id), result.data] } : current)
      setMessage({ type: 'success', text: 'Mail automation saved. It will queue only after the business API succeeds.' })
    } else setMessage({ type: 'error', text: result.error || 'Unable to save mail automation.' })
    setSaving(false)
  }

  if (!enabled) return null

  return <Drawer
    className="mail-automation-drawer"
    width={560}
    open={open}
    onClose={() => setOpen(false)}
    title={<div className="mail-automation-title"><i><MailOutlined /></i><div><strong>Action mail automation</strong><span>Attach a governed email to a successful business event</span></div></div>}
    footer={<div className="mail-automation-footer"><Button onClick={() => setOpen(false)}>Cancel</Button><Button type="primary" icon={<CheckCircleOutlined />} loading={saving} onClick={() => void save()}>Save automation</Button></div>}
  >
    {loading ? <div className="mail-automation-loading"><Spin /><span>Loading events, templates and clients...</span></div> : <div className="mail-automation-body">
      <Alert type="info" showIcon message="Admin shortcut" description="Right-click opened this panel. Mail is tied to the selected server event, so failed API calls never send a message." />
      <section className="mail-action-context">
        <i><ApiOutlined /></i>
        <div><span>Selected screen action</span><strong>{captured.label}</strong><small>{captured.resourceId ? `Record ${captured.resourceId}` : 'Choose the matching business event below'}</small></div>
        {captured.eventCode && <Tag color="blue">Registered</Tag>}
      </section>

      <Field label="Business event" required hint="The successful API/workflow event that will queue the email.">
        <Select
          showSearch
          value={selectedEventCode || undefined}
          placeholder="Search action or event code"
          optionFilterProp="label"
          onChange={setSelectedEventCode}
          options={events.map(item => ({ value: item.eventCode, label: `${item.displayName} · ${item.eventCode}` }))}
        />
      </Field>
      {selectedEvent && <div className="mail-event-detail"><Tag>{selectedEvent.moduleCode || 'Automation'}</Tag><b>{selectedEvent.lifecycle}</b><span>{selectedEvent.description}</span>{selectedEvent.pathPattern && <code>{selectedEvent.httpMethod} {selectedEvent.pathPattern}</code>}</div>}

      <div className="mail-automation-grid">
        <Field label="Client scope" hint="Keep global only when the same rule must apply to every client.">
          <Select value={clientId ?? 0} onChange={value => setClientId(value === 0 ? null : value)} options={[{ value: 0, label: 'All clients' }, ...clients.map(client => ({ value: client.id, label: client.name }))]} />
        </Field>
        <Field label="Email template" required hint="Templates are managed in Settings > Integrations > Notifications.">
          <Select showSearch optionFilterProp="label" value={templateId} placeholder="Select template" onChange={setTemplateId} options={activeTemplates.map(template => ({ value: template.id, label: template.name }))} />
        </Field>
      </div>

      <Divider orientation="left"><TeamOutlined /> Process stakeholders</Divider>
      <p className="mail-stakeholder-help">Only roles involved in this process are offered. Names and emails below are a live preview; the role is resolved again when the action runs.</p>
      {previewing ? <div className="mail-stakeholder-loading"><Spin size="small" /> Resolving this record's stakeholders...</div> : stakeholders.length ? <>
        <RecipientField label="To" required values={recipients.To} options={recipientOptions} onChange={values => updateRecipients('To', values)} />
        <RecipientField label="Cc" values={recipients.Cc} options={recipientOptions} onChange={values => updateRecipients('Cc', values)} />
        <RecipientField label="Bcc" values={recipients.Bcc} options={recipientOptions} onChange={values => updateRecipients('Bcc', values)} />
        <div className="mail-resolved-preview">
          {stakeholders.filter(item => [...recipients.To, ...recipients.Cc, ...recipients.Bcc].includes(item.code)).map(item => <article key={item.code}>
            <div><strong>{item.label}</strong><span>{item.description}</span></div>
            {item.people.length ? <ul>{item.people.map(person => <li key={`${item.code}-${person.email}`}><b>{person.displayName}</b><span>{person.email}</span></li>)}</ul> : <Tag>Resolved at run time</Tag>}
          </article>)}
        </div>
      </> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Select a business event to load its process stakeholders." />}

      <section className="mail-automation-state"><div><SettingOutlined /><span><b>Automation enabled</b><small>Disabling keeps the rule saved without queueing new mail.</small></span></div><Switch checked={isEnabled} onChange={setIsEnabled} /></section>
      {message && <Alert showIcon type={message.type} message={message.text} />}
      <Button type="link" className="mail-template-link" onClick={() => { window.location.href = '/settings/notifications' }}>Manage or create email templates</Button>
    </div>}
  </Drawer>
}

function Field({ label, required, hint, children }: { label: string; required?: boolean; hint?: string; children: React.ReactNode }) {
  return <label className="mail-automation-field"><span>{label}{required && <em>*</em>}</span>{children}{hint && <small>{hint}</small>}</label>
}

function RecipientField({ label, required, values, options, onChange }: { label: string; required?: boolean; values: string[]; options: Array<{ value: string; label: string }>; onChange: (values: string[]) => void }) {
  return <Field label={label} required={required}><Select mode="multiple" showSearch optionFilterProp="label" maxTagCount="responsive" value={values} placeholder={`Select ${label.toLowerCase()} stakeholders`} onChange={onChange} options={options} /></Field>
}
