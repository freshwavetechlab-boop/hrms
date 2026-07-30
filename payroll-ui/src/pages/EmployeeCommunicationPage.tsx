import { useCallback, useEffect, useMemo, useRef, useState, type Key } from 'react'
import DOMPurify from 'dompurify'
import {
  CheckCircleFilled,
  ClockCircleOutlined,
  DeleteOutlined,
  DownloadOutlined,
  EyeOutlined,
  FileOutlined,
  HistoryOutlined,
  InboxOutlined,
  MailOutlined,
  MessageOutlined,
  MoreOutlined,
  PaperClipOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
  SendOutlined,
  TeamOutlined,
  WhatsAppOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Avatar,
  Badge,
  Button,
  Drawer,
  Empty,
  Input,
  List,
  Modal,
  Progress,
  Segmented,
  Select,
  Skeleton,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
  Upload,
} from 'antd'
import type { ColumnsType, TableRowSelection } from 'antd/es/table/interface'
import CommunicationRichEditor from '../components/CommunicationRichEditor'
import DataTable, { type Column } from '../components/DataTable'
import { useAuthSession } from '../components/AuthGate'
import { useToast } from '../components/ToastProvider'
import {
  deleteEntityAttachment,
  getEffectiveAttachmentConfigurations,
  openAttachmentWithTicket,
  uploadEntityAttachment,
} from '../services/attachmentService'
import {
  createEmployeeCommunicationDraft,
  getCommunicationRecipients,
  getEmployeeCommunicationCampaign,
  getEmployeeCommunicationCampaigns,
  getEmployeeCommunicationTemplates,
  getEmployeeConversation,
  getEmployeeConversations,
  previewEmployeeCommunication,
  replyToEmployeeConversation,
  retryFailedEmployeeCommunication,
  sendEmployeeCommunication,
} from '../services/employeeCommunicationService'
import { getClients } from '../services/payrollService'
import type { Client, EntityAttachment } from '../types/payroll'
import type {
  CommunicationCampaignStatus,
  CommunicationChannel,
  CommunicationRecipientStatus,
  CommunicationTemplate,
  EmployeeCommunicationCampaign,
  EmployeeCommunicationCampaignRecipient,
  EmployeeCommunicationPreview,
  EmployeeCommunicationRecipient,
  EmployeeCommunicationSelection,
  EmployeeConversation,
  EmployeeConversationDetail,
  EmployeeConversationMessage,
} from '../types/employeeCommunication'
import './EmployeeCommunicationPage.css'

type RecipientField = 'to' | 'cc' | 'bcc'
type RecipientBuckets = Record<RecipientField, number[]>
type MailFolder = 'all' | 'Email' | 'Sms' | 'WhatsApp' | 'unread'

const emptyBuckets = (): RecipientBuckets => ({ to: [], cc: [], bcc: [] })
const channelLabel = (channel: CommunicationChannel) => channel === 'Sms' ? 'SMS' : channel
const channelIcon = (channel: CommunicationChannel) => channel === 'Email' ? <MailOutlined /> : channel === 'Sms' ? <MessageOutlined /> : <WhatsAppOutlined />
const channelColor = (channel: CommunicationChannel) => channel === 'Email' ? '#2563eb' : channel === 'Sms' ? '#7557d6' : '#128c7e'
const idempotencyKey = () => globalThis.crypto?.randomUUID?.() ?? String(Date.now()) + Math.random().toString(16).slice(2)
const initials = (name: string) => name.split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase() || 'E'
const stripHtml = (value: string) => value.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim()
const dateTime = (value?: string | null) => {
  if (!value) return ''
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' })
}
const shortDate = (value?: string | null) => {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toDateString() === new Date().toDateString()
    ? date.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' })
}
const formatBytes = (bytes: number) => bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(bytes / 1024)) + ' KB'

const campaignStatusColor: Record<CommunicationCampaignStatus, string> = {
  Draft: 'default', Queued: 'blue', Processing: 'processing', Sent: 'success', PartiallySent: 'warning', Failed: 'error',
}
const recipientStatusColor: Record<CommunicationRecipientStatus, string> = {
  Pending: 'default', Queued: 'blue', Processing: 'processing', Sent: 'cyan', Delivered: 'green', Read: 'success', Failed: 'error', Excluded: 'warning',
}

export default function EmployeeCommunicationPage() {
  const session = useAuthSession()
  const notify = useToast()
  const permissions = session?.user.permissions ?? []
  const canView = permissions.includes('employee.communication.view')
  const canSend = permissions.includes('employee.communication.send')
  const fixedClientId = Number(session?.user.clientId || 0)
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState(fixedClientId)
  const [recipients, setRecipients] = useState<EmployeeCommunicationRecipient[]>([])
  const [recipientLoading, setRecipientLoading] = useState(false)
  const [templates, setTemplates] = useState<CommunicationTemplate[]>([])
  const [conversations, setConversations] = useState<EmployeeConversation[]>([])
  const [conversationLoading, setConversationLoading] = useState(false)
  const [selectedConversationId, setSelectedConversationId] = useState<number | null>(null)
  const [conversationDetail, setConversationDetail] = useState<EmployeeConversationDetail | null>(null)
  const [conversationDetailLoading, setConversationDetailLoading] = useState(false)
  const [folder, setFolder] = useState<MailFolder>('all')
  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState('Open')
  const [composeOpen, setComposeOpen] = useState(false)
  const [composeMode, setComposeMode] = useState<'new' | 'reply'>('new')
  const [replyConversationId, setReplyConversationId] = useState<number | null>(null)
  const [channel, setChannel] = useState<CommunicationChannel>('Email')
  const [buckets, setBuckets] = useState<RecipientBuckets>(emptyBuckets)
  const [showCc, setShowCc] = useState(false)
  const [showBcc, setShowBcc] = useState(false)
  const [recipientSearch, setRecipientSearch] = useState<Record<RecipientField, string>>({ to: '', cc: '', bcc: '' })
  const [draggedRecipient, setDraggedRecipient] = useState<{ field: RecipientField; employeeId: number } | null>(null)
  const [dropField, setDropField] = useState<RecipientField | null>(null)
  const [templateId, setTemplateId] = useState<number | null>(null)
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [draftId, setDraftId] = useState<number | null>(null)
  const [attachments, setAttachments] = useState<EntityAttachment[]>([])
  const [attachmentFieldId, setAttachmentFieldId] = useState(0)
  const [attachmentUploading, setAttachmentUploading] = useState(false)
  const [composePreparing, setComposePreparing] = useState(false)
  const [preview, setPreview] = useState<EmployeeCommunicationPreview | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [sending, setSending] = useState(false)
  const [directoryOpen, setDirectoryOpen] = useState(false)
  const [directorySearch, setDirectorySearch] = useState('')
  const [directorySelected, setDirectorySelected] = useState<number[]>([])
  const [historyOpen, setHistoryOpen] = useState(false)
  const [campaigns, setCampaigns] = useState<EmployeeCommunicationCampaign[]>([])
  const [campaignLoading, setCampaignLoading] = useState(false)
  const [campaignDetail, setCampaignDetail] = useState<EmployeeCommunicationCampaign | null>(null)
  const [campaignDetailLoading, setCampaignDetailLoading] = useState(false)
  const [retrying, setRetrying] = useState(false)
  const [quickReply, setQuickReply] = useState('')
  const [quickReplying, setQuickReplying] = useState(false)
  const conversationRequest = useRef(0)

  useEffect(() => {
    if (!canView) return
    void getClients().then(rows => {
      setClients(rows)
      setClientId(current => fixedClientId || current || rows[0]?.id || 0)
    })
  }, [canView, fixedClientId])

  const loadRecipients = useCallback(async () => {
    if (!clientId) return setRecipients([])
    setRecipientLoading(true)
    const response = await getCommunicationRecipients({ clientId, limit: 2500 })
    setRecipientLoading(false)
    if (!response.ok) return notify(response.error || 'Employee contacts could not be loaded.', 'error')
    setRecipients(response.data.items)
  }, [clientId, notify])

  const loadConversations = useCallback(async () => {
    if (!clientId) return setConversations([])
    setConversationLoading(true)
    const channelFilter = folder === 'Email' || folder === 'Sms' || folder === 'WhatsApp' ? folder : ''
    const response = await getEmployeeConversations({ clientId, channel: channelFilter, status: statusFilter, search: search.trim() })
    setConversationLoading(false)
    if (!response.ok) return notify(response.error || 'Inbox could not be loaded.', 'error')
    const rows = response.data.items.filter(item => folder !== 'unread' || item.unreadCount > 0)
    setConversations(rows)
    setSelectedConversationId(current => current && rows.some(item => item.id === current) ? current : rows[0]?.id ?? null)
  }, [clientId, folder, notify, search, statusFilter])

  const loadCampaigns = useCallback(async () => {
    if (!clientId) return
    setCampaignLoading(true)
    const response = await getEmployeeCommunicationCampaigns({ clientId, page: 1, pageSize: 100 })
    setCampaignLoading(false)
    if (response.ok) setCampaigns(response.data.items)
    else notify(response.error || 'Campaign history could not be loaded.', 'error')
  }, [clientId, notify])

  useEffect(() => { if (canView) void loadRecipients() }, [canView, loadRecipients])
  useEffect(() => {
    if (!canView) return
    const timer = window.setTimeout(() => void loadConversations(), 220)
    return () => window.clearTimeout(timer)
  }, [canView, loadConversations])

  useEffect(() => {
    if (!selectedConversationId) { setConversationDetail(null); return }
    const request = ++conversationRequest.current
    setConversationDetailLoading(true)
    setConversations(current => current.map(item => item.id === selectedConversationId ? { ...item, unreadCount: 0 } : item))
    void getEmployeeConversation(selectedConversationId).then(response => {
      if (request !== conversationRequest.current) return
      setConversationDetailLoading(false)
      if (response.ok) setConversationDetail(response.data)
      else notify(response.error || 'Conversation could not be opened.', 'error')
    })
  }, [notify, selectedConversationId])

  useEffect(() => {
    if (!clientId || !channel) return
    void Promise.all([
      getEmployeeCommunicationTemplates(clientId, channel),
      getEffectiveAttachmentConfigurations(clientId, 'EMPLOYEE', 'EMPLOYEE_COMMUNICATION'),
    ]).then(([templateResponse, configs]) => {
      setTemplates(templateResponse.ok ? templateResponse.data.filter(item => item.isActive) : [])
      setAttachmentFieldId(configs.find(item => item.fieldKey === 'MESSAGE_ATTACHMENTS')?.id || 0)
    })
  }, [channel, clientId])

  if (!canView) return <section className="communication-access-denied"><Empty description="Employee Communication is restricted" /><p>Ask a security administrator to assign employee.communication.view to your role.</p></section>

  const employeeById = new Map(recipients.map(item => [item.employeeId, item]))
  const selectedIds = Array.from(new Set([...buckets.to, ...buckets.cc, ...buckets.bcc]))
  const unreadTotal = conversations.reduce((sum, item) => sum + item.unreadCount, 0)
  const visibleDirectory = recipients.filter(item => {
    const needle = directorySearch.trim().toLowerCase()
    return !needle || [item.employeeName, item.employeeCode, item.department, item.designation, item.workEmail, item.mobile].join(' ').toLowerCase().includes(needle)
  })

  const createDraft = async (draftChannel: CommunicationChannel) => {
    if (!clientId) return null
    setComposePreparing(true)
    const response = await createEmployeeCommunicationDraft(clientId, draftChannel)
    setComposePreparing(false)
    if (!response.ok || !response.data) { notify(response.error || 'Secure attachment draft could not be created.', 'error'); return null }
    setDraftId(response.data.id)
    return response.data.id
  }

  const openNewMessage = async (initialChannel: CommunicationChannel = 'Email') => {
    setComposeMode('new'); setReplyConversationId(null); setChannel(initialChannel); setBuckets(emptyBuckets())
    setShowCc(false); setShowBcc(false); setTemplateId(null); setSubject(''); setBody(''); setAttachments([]); setDraftId(null)
    setComposeOpen(true)
    await createDraft(initialChannel)
  }

  const openReplyComposer = async (conversation: EmployeeConversationDetail) => {
    setComposeMode('reply'); setReplyConversationId(conversation.id); setChannel(conversation.channel)
    setBuckets({ to: [conversation.employeeId], cc: [], bcc: [] }); setShowCc(false); setShowBcc(false)
    const latestSubject = [...conversation.messages].reverse().find(item => item.subject)?.subject || ''
    setSubject(latestSubject && !/^re:/i.test(latestSubject) ? 'Re: ' + latestSubject : latestSubject)
    setTemplateId(null); setBody(''); setAttachments([]); setDraftId(null); setComposeOpen(true)
    await createDraft(conversation.channel)
  }

  const resetCompose = () => {
    setComposeOpen(false); setPreview(null); setDraftId(null); setAttachments([]); setBuckets(emptyBuckets())
    setTemplateId(null); setSubject(''); setBody(''); setReplyConversationId(null)
  }

  const moveRecipient = (source: RecipientField, target: RecipientField, employeeId: number) => {
    if (source === target) return
    setBuckets(current => ({
      ...current,
      [source]: current[source].filter(id => id !== employeeId),
      [target]: Array.from(new Set([...current[target], employeeId])),
    }))
    if (target === 'cc') setShowCc(true)
    if (target === 'bcc') setShowBcc(true)
  }

  const recipientField = (field: RecipientField, label: string) => {
    const value = buckets[field]
    return <div
      data-testid={'communication-recipient-' + field}
      className={'mail-recipient-row ' + (dropField === field ? 'is-drop-target' : '')}
      onDragOver={event => { if (draggedRecipient?.field !== field) { event.preventDefault(); setDropField(field) } }}
      onDragLeave={() => setDropField(null)}
      onDrop={event => {
        event.preventDefault()
        if (draggedRecipient) moveRecipient(draggedRecipient.field, field, draggedRecipient.employeeId)
        setDraggedRecipient(null); setDropField(null)
      }}
    >
      <span className="mail-recipient-label">{label}</span>
      <Select
        mode="multiple"
        bordered={false}
        value={value}
        searchValue={recipientSearch[field]}
        onSearch={next => setRecipientSearch(current => ({ ...current, [field]: next }))}
        onChange={values => {
          const incoming = values.map(Number)
          setBuckets(current => {
            const other = (Object.keys(current) as RecipientField[]).filter(key => key !== field).flatMap(key => current[key])
            return { ...current, [field]: incoming.filter(id => !other.includes(id)) }
          })
          setRecipientSearch(current => ({ ...current, [field]: '' }))
        }}
        optionFilterProp="label"
        placeholder={field === 'to' ? 'Search employees by name, code, email or mobile' : 'Add optional recipients'}
        options={recipients.map(employee => ({
          value: employee.employeeId,
          label: [employee.employeeName, employee.employeeCode, channel === 'Email' ? employee.workEmail : employee.mobile].filter(Boolean).join(' · '),
        }))}
        tagRender={({ value: tagValue, closable, onClose }) => {
          const employee = employeeById.get(Number(tagValue))
          if (!employee) return <span />
          return <Tag
            data-testid={'communication-recipient-chip-' + employee.employeeId}
            closable={closable}
            onClose={onClose}
            className="mail-recipient-chip"
            onMouseDown={event => event.stopPropagation()}
          >
            <span
              draggable
              title="Drag between recipient fields. Double-click to edit."
              onDragStart={event => {
                setDraggedRecipient({ field, employeeId: employee.employeeId })
                event.dataTransfer.effectAllowed = 'move'
              }}
              onDragEnd={() => { setDraggedRecipient(null); setDropField(null) }}
              onDoubleClick={event => {
                event.stopPropagation()
                setBuckets(current => ({ ...current, [field]: current[field].filter(id => id !== employee.employeeId) }))
                setRecipientSearch(current => ({ ...current, [field]: employee.employeeName }))
              }}
            >
              <Avatar size={20}>{initials(employee.employeeName)}</Avatar>
              <b>{employee.employeeName}</b>
              <small>{channel === 'Email' ? employee.workEmail : employee.mobile}</small>
            </span>
          </Tag>
        }}
        suffixIcon={null}
      />
      {field === 'to' && <Space size={2} className="mail-recipient-actions">
        <Button type="text" size="small" onClick={() => setShowCc(current => !current)}>Cc</Button>
        <Button type="text" size="small" onClick={() => setShowBcc(current => !current)}>Bcc</Button>
        <Tooltip title="Open employee directory"><Button aria-label="Open employee directory" type="text" size="small" icon={<TeamOutlined />} onClick={() => { setDirectorySelected(selectedIds); setDirectoryOpen(true) }} /></Tooltip>
      </Space>}
    </div>
  }

  const switchComposeChannel = async (next: string | number) => {
    const nextChannel = next as CommunicationChannel
    if (nextChannel === channel) return
    if (attachments.length && !window.confirm('Changing channel will remove the current attachment draft. Continue?')) return
    setChannel(nextChannel); setTemplateId(null); setSubject(''); setBody(''); setAttachments([]); setDraftId(null)
    await createDraft(nextChannel)
  }

  const applyTemplate = (value?: number) => {
    const template = templates.find(item => item.id === value)
    setTemplateId(value || null)
    if (template) { setSubject(template.subjectTemplate || ''); setBody(template.bodyTemplate || '') }
  }

  const uploadFile = async (file: File) => {
    if (channel === 'Sms') return notify('SMS does not support file attachments.', 'warning')
    if (!attachmentFieldId || !draftId) return notify('Attachment storage is still preparing. Try again in a moment.', 'warning')
    setAttachmentUploading(true)
    const response = await uploadEntityAttachment(attachmentFieldId, 'EMPLOYEE_COMMUNICATION_DRAFT', draftId, file, {}, () => undefined)
    setAttachmentUploading(false)
    if (!response.ok || !response.data) return notify(response.error || 'Attachment upload failed.', 'error')
    setAttachments(current => [...current, response.data])
  }

  const removeAttachment = async (file: EntityAttachment) => {
    const response = await deleteEntityAttachment(file.publicId)
    if (!response.ok) return notify(response.error || 'Attachment could not be removed.', 'error')
    setAttachments(current => current.filter(item => item.publicId !== file.publicId))
  }

  const requestPayload = (): EmployeeCommunicationSelection => ({
    clientId,
    draftId,
    channel,
    templateId,
    subject: subject.trim(),
    body: body.trim(),
    selectionMode: 'SelectedEmployees',
    employeeIds: selectedIds,
    toEmployeeIds: buckets.to,
    ccEmployeeIds: buckets.cc,
    bccEmployeeIds: buckets.bcc,
    excludedEmployeeIds: [],
    search: '',
    workLocationIds: [],
    departments: [],
    designations: [],
  })

  const previewMessage = async () => {
    if (!selectedIds.length) return notify('Add at least one employee recipient.', 'warning')
    if (!stripHtml(body)) return notify('Write a message first.', 'warning')
    if (channel === 'Email' && !subject.trim()) return notify('Email subject is required.', 'warning')
    setPreviewLoading(true)
    const response = await previewEmployeeCommunication(requestPayload())
    setPreviewLoading(false)
    if (!response.ok || !response.data) return notify(response.error || 'Preview could not be prepared.', 'error')
    setPreview(response.data)
  }

  const sendNewMessage = async () => {
    if (!preview?.canSend) return
    setSending(true)
    const response = await sendEmployeeCommunication({ ...requestPayload(), idempotencyKey: idempotencyKey() })
    setSending(false)
    if (!response.ok || !response.data) return notify(response.error || 'Message could not be queued.', 'error')
    notify(channelLabel(channel) + ' queued for ' + response.data.totalEligible + ' employee(s).', 'success')
    resetCompose(); await Promise.all([loadCampaigns(), loadConversations()])
  }

  const sendReplyFromDrawer = async () => {
    if (!replyConversationId || !stripHtml(body)) return
    setSending(true)
    const response = await replyToEmployeeConversation(replyConversationId, { body, templateId, draftId, idempotencyKey: idempotencyKey() })
    setSending(false)
    if (!response.ok || !response.data) return notify(response.error || 'Reply could not be sent.', 'error')
    setConversationDetail(response.data); notify('Reply queued.', 'success'); resetCompose(); await loadConversations()
  }

  const sendQuickReply = async () => {
    if (!conversationDetail || !quickReply.trim()) return
    setQuickReplying(true)
    const response = await replyToEmployeeConversation(conversationDetail.id, { body: quickReply.trim(), idempotencyKey: idempotencyKey() })
    setQuickReplying(false)
    if (!response.ok || !response.data) return notify(response.error || 'Reply could not be sent.', 'error')
    setConversationDetail(response.data); setQuickReply(''); await loadConversations()
  }

  const openCampaign = async (campaign: EmployeeCommunicationCampaign) => {
    setCampaignDetail(campaign); setCampaignDetailLoading(true)
    const response = await getEmployeeCommunicationCampaign(campaign.id)
    setCampaignDetailLoading(false)
    if (response.ok) setCampaignDetail(response.data)
  }

  const retryCampaign = async () => {
    if (!campaignDetail) return
    setRetrying(true)
    const response = await retryFailedEmployeeCommunication(campaignDetail.id)
    setRetrying(false)
    if (response.ok) setCampaignDetail(response.data)
  }

  const folderRows = [
    { key: 'all' as MailFolder, icon: <InboxOutlined />, label: 'All conversations', count: conversations.length },
    { key: 'unread' as MailFolder, icon: <Badge dot={unreadTotal > 0}><MailOutlined /></Badge>, label: 'Unread', count: unreadTotal },
    { key: 'Email' as MailFolder, icon: <MailOutlined />, label: 'Email', count: null },
    { key: 'Sms' as MailFolder, icon: <MessageOutlined />, label: 'SMS', count: null },
    { key: 'WhatsApp' as MailFolder, icon: <WhatsAppOutlined />, label: 'WhatsApp', count: null },
  ]

  return <section className="employee-communication-page" data-testid="employee-communication-page">
    <header className="communication-commandbar">
      <div>
        <span className="communication-kicker">Employee engagement desk</span>
        <h2>Employee Communication</h2>
      </div>
      <Space wrap>
        <Select
          data-testid="communication-client-selector"
          className="communication-client-select"
          value={clientId || undefined}
          disabled={Boolean(fixedClientId)}
          onChange={value => { setClientId(Number(value)); setSelectedConversationId(null) }}
          options={clients.filter(item => item.isActive).map(item => ({ value: item.id, label: item.name }))}
          placeholder="Select client"
        />
        <Button data-testid="communication-history-tab" icon={<HistoryOutlined />} onClick={() => { setHistoryOpen(true); void loadCampaigns() }}>Campaigns</Button>
        <Button data-testid="communication-compose-button" type="primary" size="large" icon={<PlusOutlined />} disabled={!canSend || !clientId} onClick={() => void openNewMessage()}>New message</Button>
      </Space>
    </header>

    <div className="communication-mail-shell" data-testid="communication-inbox">
      <aside className="communication-folder-pane">
        <Button type="primary" block icon={<PlusOutlined />} disabled={!canSend} onClick={() => void openNewMessage()}>Compose</Button>
        <nav>
          {folderRows.map(item => <button
            type="button"
            data-testid={item.key === 'all' ? 'communication-conversations-tab' : undefined}
            key={item.key}
            className={folder === item.key ? 'active' : ''}
            onClick={() => setFolder(item.key)}
          ><span>{item.icon}{item.label}</span>{item.count !== null && <b>{item.count}</b>}</button>)}
        </nav>
        <div className="communication-folder-foot"><span>Private delivery</span><p>Bulk recipients receive independent copies; addresses are never exposed.</p></div>
      </aside>

      <aside className="communication-thread-list" data-testid="communication-thread-list">
        <div className="communication-list-toolbar">
          <Input allowClear prefix={<SearchOutlined />} value={search} onChange={event => setSearch(event.target.value)} placeholder="Search people or messages" />
          <Segmented size="small" value={statusFilter} onChange={value => setStatusFilter(String(value))} options={[{ label: 'Open', value: 'Open' }, { label: 'All', value: '' }]} />
        </div>
        <div className="communication-list-summary"><strong>{folder === 'all' ? 'Inbox' : folder === 'unread' ? 'Unread' : channelLabel(folder)}</strong><Button type="text" icon={<ReloadOutlined />} loading={conversationLoading} onClick={() => void loadConversations()} /></div>
        {conversationLoading ? <Skeleton className="communication-list-skeleton" active paragraph={{ rows: 8 }} /> : <List
          dataSource={conversations}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No conversations here yet." /> }}
          renderItem={item => <List.Item>
            <button
              type="button"
              data-testid={'communication-thread-' + item.id}
              className={[selectedConversationId === item.id ? 'active' : '', item.unreadCount > 0 ? 'unread' : ''].filter(Boolean).join(' ')}
              onClick={() => { setSelectedConversationId(item.id); setQuickReply('') }}
            >
              <Avatar style={{ background: channelColor(item.channel) }}>{initials(item.employeeName)}</Avatar>
              <div><header><strong>{item.employeeName}</strong><time>{shortDate(item.lastMessageAtUtc)}</time></header><p>{item.lastMessagePreview || 'No preview'}</p><footer><span>{channelIcon(item.channel)} {channelLabel(item.channel)}</span><small>{item.employeeCode}</small></footer></div>
              {item.unreadCount > 0 && <Badge count={item.unreadCount} />}
            </button>
          </List.Item>}
        />}
      </aside>

      <main className="communication-conversation-panel" data-testid="communication-conversation-panel">
        {!selectedConversationId ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Choose a conversation or start a new message." /> :
          conversationDetailLoading ? <Skeleton active avatar paragraph={{ rows: 10 }} /> :
          conversationDetail ? <ConversationView
            detail={conversationDetail}
            senderName={session?.user.displayName || 'HR team'}
            canSend={canSend}
            quickReply={quickReply}
            setQuickReply={setQuickReply}
            quickReplying={quickReplying}
            sendQuickReply={sendQuickReply}
            openReply={() => void openReplyComposer(conversationDetail)}
          /> : <Empty description="Conversation unavailable." />}
      </main>
    </div>

    <Drawer
      rootClassName="communication-compose-drawer-root"
      className="communication-compose-drawer"
      width="min(1120px, 96vw)"
      open={composeOpen}
      destroyOnClose={false}
      maskClosable={false}
      onClose={() => !sending && resetCompose()}
      title={<div className="compose-drawer-title"><span>{composeMode === 'reply' ? 'Reply to employee' : 'New employee message'}</span><small>{channelLabel(channel)} · secured individual delivery</small></div>}
      extra={<Space><Tag color={composePreparing ? 'processing' : draftId ? 'success' : 'default'}>{composePreparing ? 'Preparing storage' : draftId ? 'Draft secured' : 'No draft'}</Tag><Button type="text" icon={<MoreOutlined />} /></Space>}
      footer={<div className="compose-drawer-footer"><span>{selectedIds.length} recipient{selectedIds.length === 1 ? '' : 's'} · {attachments.length} attachment{attachments.length === 1 ? '' : 's'}</span><Space><Button onClick={resetCompose}>Discard</Button>{composeMode === 'reply'
        ? <Button type="primary" icon={<SendOutlined />} loading={sending} disabled={!stripHtml(body)} onClick={() => void sendReplyFromDrawer()}>Send reply</Button>
        : <Button data-testid="communication-preview-button" type="primary" icon={<EyeOutlined />} loading={previewLoading} disabled={!selectedIds.length || !stripHtml(body) || (channel === 'Email' && !subject.trim())} onClick={() => void previewMessage()}>Preview & send</Button>}</Space></div>}
    >
      <div className="compose-mail-surface">
        <div className="compose-channel-strip" data-testid="communication-channel-control">
          <Segmented block value={channel} onChange={value => void switchComposeChannel(value)} options={[
            { value: 'Email', label: <span><MailOutlined /> Email</span> },
            { value: 'Sms', label: <span><MessageOutlined /> SMS</span> },
            { value: 'WhatsApp', label: <span><WhatsAppOutlined /> WhatsApp</span> },
          ]} />
        </div>
        <div className="compose-envelope">
          <div className="mail-from-row"><span>From</span><Avatar size={26}>HR</Avatar><b>{session?.user.displayName || 'HR team'}</b><small>via {channelLabel(channel)}</small></div>
          {recipientField('to', 'To')}
          {showCc && recipientField('cc', 'Cc')}
          {showBcc && recipientField('bcc', 'Bcc')}
          {channel === 'Email' && <div className="mail-subject-row"><span>Subject</span><Input bordered={false} value={subject} onChange={event => setSubject(event.target.value)} placeholder="Add a clear subject" /></div>}
        </div>
        <div className="compose-template-row">
          <Select allowClear value={templateId || undefined} onChange={applyTemplate} placeholder="Use a saved template" options={templates.map(item => ({ value: item.id, label: item.name }))} />
          <span>Personalize with {templates.find(item => item.id === templateId)?.variables?.map(item => '{{' + item.variableKey + '}}').join(', ') || '{{employeeName}}'}</span>
        </div>

        {channel === 'Email' ? <CommunicationRichEditor value={body} onChange={setBody} onFilesPasted={files => files.forEach(file => void uploadFile(file))} placeholder="Write a long email, paste tables or screenshots, and format it exactly as employees should receive it." /> :
          channel === 'Sms' ? <div className="compose-phone-preview sms"><div className="phone-preview-head"><MessageOutlined /> SMS preview</div><div className="phone-preview-screen"><div className="sms-preview-bubble">{body || 'Your text message will appear here.'}</div></div><Input.TextArea data-testid="communication-message-body" value={body} maxLength={1000} showCount autoSize={{ minRows: 5, maxRows: 10 }} onChange={event => setBody(event.target.value)} placeholder="Write a concise text message…" /></div> :
          <div className="compose-phone-preview whatsapp"><div className="phone-preview-head"><WhatsAppOutlined /> WhatsApp preview</div><div className="whatsapp-preview-screen"><div className="whatsapp-preview-bubble">{body || 'Your WhatsApp message will appear here.'}<small>{new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })} ✓✓</small></div></div><Input.TextArea data-testid="communication-message-body" value={body} maxLength={10000} autoSize={{ minRows: 5, maxRows: 10 }} onChange={event => setBody(event.target.value)} placeholder="Write a WhatsApp message…" /></div>}

        {channel !== 'Sms' && <div className="compose-attachment-zone">
          <Upload.Dragger
            multiple
            showUploadList={false}
            disabled={!draftId || attachmentUploading}
            beforeUpload={file => { void uploadFile(file); return Upload.LIST_IGNORE }}
          >
            <p className="ant-upload-drag-icon"><PaperClipOutlined /></p>
            <p className="ant-upload-text">{attachmentUploading ? 'Securing attachment…' : 'Drop files here, paste screenshots, or click to browse'}</p>
            <p className="ant-upload-hint">PDF, DOCX, JPG and PNG · stored through the active secured attachment server</p>
          </Upload.Dragger>
          {!!attachments.length && <div className="compose-file-list">{attachments.map(file => <article key={file.publicId}><FileOutlined /><div><b>{file.originalFileName}</b><span>{formatBytes(file.fileSizeBytes)} · {file.storageServerName}</span></div><Button aria-label={'Preview ' + file.originalFileName} type="text" icon={<EyeOutlined />} onClick={() => void openAttachmentWithTicket(file.publicId, 'Preview')} /><Button aria-label={'Remove ' + file.originalFileName} danger type="text" icon={<DeleteOutlined />} onClick={() => void removeAttachment(file)} /></article>)}</div>}
        </div>}
      </div>
    </Drawer>

    <Modal
      open={directoryOpen}
      width={940}
      title="Employee directory"
      onCancel={() => setDirectoryOpen(false)}
      onOk={() => {
        setBuckets(current => ({ ...current, to: Array.from(new Set([...current.to, ...directorySelected])) }))
        setDirectoryOpen(false)
      }}
      okText={'Add ' + directorySelected.length + ' to recipients'}
    >
      <Input className="directory-search" allowClear prefix={<SearchOutlined />} value={directorySearch} onChange={event => setDirectorySearch(event.target.value)} placeholder="Search employee, department, designation, email or mobile" />
      <Table
        data-testid="communication-recipient-table"
        rowKey="employeeId"
        size="small"
        loading={recipientLoading}
        dataSource={visibleDirectory}
        pagination={{ pageSize: 8 }}
        rowSelection={{ selectedRowKeys: directorySelected, onChange: (keys: Key[]) => setDirectorySelected(keys.map(Number)) }}
        columns={[
          { title: 'Employee', render: (_, row) => <Space><Avatar>{initials(row.employeeName)}</Avatar><span><b>{row.employeeName}</b><br /><small>{row.employeeCode}</small></span></Space> },
          { title: 'Organization', render: (_, row) => <span>{row.designation || '-'}<br /><small>{row.department || '-'}</small></span> },
          { title: 'Email', dataIndex: 'workEmail', render: value => value || <Tag color="warning">Missing</Tag> },
          { title: 'Mobile', dataIndex: 'mobile', render: value => value || <Tag color="warning">Missing</Tag> },
        ] as ColumnsType<EmployeeCommunicationRecipient>}
      />
    </Modal>

    <Modal
      className="communication-preview-modal"
      width={900}
      open={Boolean(preview)}
      title="Final delivery check"
      onCancel={() => !sending && setPreview(null)}
      footer={<Space><Button onClick={() => setPreview(null)}>Back to edit</Button><Button data-testid="communication-send-button" type="primary" icon={<SendOutlined />} loading={sending} disabled={!preview?.canSend} onClick={() => void sendNewMessage()}>Send {preview?.eligibleCount || 0} private copies</Button></Space>}
    >
      {preview && <div className="communication-preview-content">
        <div className="preview-metrics"><article><b>{preview.selectedCount}</b><span>Selected</span></article><article><b>{preview.eligibleCount}</b><span>Ready</span></article><article><b>{preview.excludedCount}</b><span>Excluded</span></article><article><b>{attachments.length}</b><span>Attachments</span></article></div>
        {preview.warnings.length > 0 && <Alert showIcon type="warning" message="Review before sending" description={preview.warnings.join(' ')} />}
        <div className={'preview-message ' + channel.toLowerCase()}><header>{channelIcon(channel)} {channelLabel(channel)} preview</header>{subject && <h3>{preview.sampleSubject || subject}</h3>}<div dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(preview.sampleBody || body) }} /></div>
        <Table rowKey="employeeId" size="small" pagination={{ pageSize: 5 }} dataSource={preview.recipients} columns={[
          { title: 'Group', dataIndex: 'recipientType', width: 80, render: value => <Tag>{value}</Tag> },
          { title: 'Employee', render: (_, row) => <span><b>{row.employeeName}</b><br /><small>{row.employeeCode}</small></span> },
          { title: 'Destination', dataIndex: 'destination' },
          { title: 'Status', render: (_, row) => row.isEligible ? <Tag color="success" icon={<CheckCircleFilled />}>Ready</Tag> : <Tag color="warning">Excluded</Tag> },
        ]} />
      </div>}
    </Modal>

    <Drawer className="communication-history-drawer" width="min(1120px, 96vw)" open={historyOpen} onClose={() => setHistoryOpen(false)} title="Campaign history" extra={<Button icon={<ReloadOutlined />} loading={campaignLoading} onClick={() => void loadCampaigns()}>Refresh</Button>}>
      <DataTable rows={campaigns} getRowId={row => row.id} columns={campaignColumns} emptyText="No campaigns found." actions={row => <Button icon={<EyeOutlined />} onClick={() => void openCampaign(row)}>Open</Button>} />
    </Drawer>

    <Drawer className="communication-campaign-drawer" width="min(1040px, 96vw)" open={Boolean(campaignDetail)} onClose={() => setCampaignDetail(null)} title={campaignDetail ? 'Campaign #' + campaignDetail.id : 'Campaign'}>
      {campaignDetailLoading || !campaignDetail ? <Skeleton active /> : <CampaignDetail campaign={campaignDetail} retrying={retrying} retry={retryCampaign} />}
    </Drawer>
  </section>
}

function ConversationView({ detail, senderName, canSend, quickReply, setQuickReply, quickReplying, sendQuickReply, openReply }: {
  detail: EmployeeConversationDetail
  senderName: string
  canSend: boolean
  quickReply: string
  setQuickReply: (value: string) => void
  quickReplying: boolean
  sendQuickReply: () => Promise<void>
  openReply: () => void
}) {
  return <div className={'channel-conversation channel-' + detail.channel.toLowerCase()}>
    <header className="conversation-contact-head">
      <Space><Avatar size={42} style={{ background: channelColor(detail.channel) }}>{initials(detail.employeeName)}</Avatar><div><h3>{detail.employeeName}</h3><span>{detail.employeeCode} · {detail.destination}</span></div></Space>
      <Space><Tag color={detail.channel === 'WhatsApp' ? 'green' : detail.channel === 'Sms' ? 'purple' : 'blue'}>{channelIcon(detail.channel)} {channelLabel(detail.channel)}</Tag><Button icon={<ReloadOutlined />} /></Space>
    </header>
    {detail.channel === 'Email'
      ? <EmailConversation detail={detail} senderName={senderName} />
      : <PhoneConversation detail={detail} senderName={senderName} />}
    {detail.channel === 'Email' ? <div className="email-reply-dock"><Button type="primary" icon={<MailOutlined />} disabled={!canSend} onClick={openReply}>Reply with rich email</Button><Button icon={<PaperClipOutlined />} disabled={!canSend} onClick={openReply}>Reply with attachment</Button></div>
      : <div className={'phone-reply-dock ' + (detail.channel === 'WhatsApp' ? 'whatsapp' : 'sms')}>
          <Button type="text" shape="circle" icon={<PaperClipOutlined />} disabled={!canSend || detail.channel === 'Sms'} onClick={openReply} />
          <Input.TextArea data-testid="communication-reply-input" value={quickReply} onChange={event => setQuickReply(event.target.value)} autoSize={{ minRows: 1, maxRows: 4 }} placeholder={detail.channel === 'WhatsApp' ? 'Type a WhatsApp message' : 'Type a text message'} onPressEnter={event => { if (!event.shiftKey) { event.preventDefault(); void sendQuickReply() } }} />
          <Button data-testid="communication-reply-send" type="primary" shape="circle" icon={<SendOutlined />} loading={quickReplying} disabled={!canSend || !quickReply.trim()} onClick={() => void sendQuickReply()} />
        </div>}
  </div>
}

function EmailConversation({ detail, senderName }: { detail: EmployeeConversationDetail; senderName: string }) {
  return <div className="email-thread">
    {detail.messages.length ? detail.messages.map(message => <article className={'email-thread-message ' + message.direction.toLowerCase()} key={message.id}>
      <header><Avatar>{initials(message.direction === 'Inbound' ? detail.employeeName : senderName)}</Avatar><div><h4>{message.direction === 'Inbound' ? detail.employeeName : senderName}</h4><span>{message.subject || 'Employee communication'}</span></div><time>{dateTime(message.receivedAtUtc || message.sentAtUtc || message.createdAtUtc)}</time></header>
      <div className="email-address-lines"><p><b>From</b>{message.direction === 'Inbound' ? detail.destination : senderName}</p><p><b>To</b>{message.direction === 'Inbound' ? senderName : detail.destination}</p></div>
      <div className="email-message-body" dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(message.body) }} />
      <MessageAttachments message={message} />
      <footer><Tag color={message.status === 'Failed' ? 'error' : 'success'}>{message.status}</Tag>{message.errorMessage && <span>{message.errorMessage}</span>}</footer>
    </article>) : <Empty description="No email messages yet." />}
  </div>
}

function PhoneConversation({ detail, senderName }: { detail: EmployeeConversationDetail; senderName: string }) {
  const whatsApp = detail.channel === 'WhatsApp'
  return <div className={'phone-thread ' + (whatsApp ? 'whatsapp-thread' : 'sms-thread')}>
    <div className="phone-day-pill">Today</div>
    {detail.messages.map(message => <article className={'phone-message ' + message.direction.toLowerCase()} key={message.id}>
      <span>{message.direction === 'Inbound' ? detail.employeeName : senderName}</span>
      <p>{stripHtml(message.body)}</p>
      <MessageAttachments message={message} compact />
      <footer>{shortDate(message.receivedAtUtc || message.sentAtUtc || message.createdAtUtc)} {message.direction === 'Outbound' && (whatsApp ? '✓✓' : message.status)}</footer>
    </article>)}
  </div>
}

function MessageAttachments({ message, compact = false }: { message: EmployeeConversationMessage; compact?: boolean }) {
  if (!message.attachments?.length) return null
  return <div className={'message-attachment-list ' + (compact ? 'compact' : '')}>{message.attachments.map(file => <button type="button" key={file.id} onClick={() => void openAttachmentWithTicket(file.publicId, 'Preview')}><FileOutlined /><span><b>{file.fileName}</b><small>{formatBytes(file.fileSizeBytes)}</small></span><DownloadOutlined /></button>)}</div>
}

const campaignColumns: Column<EmployeeCommunicationCampaign>[] = [
  { key: 'id', label: 'Campaign', render: row => <span><b>#{row.id}</b><br /><small>{dateTime(row.createdAtUtc)}</small></span> },
  { key: 'channel', label: 'Channel', render: row => <Tag>{channelIcon(row.channel)} {channelLabel(row.channel)}</Tag> },
  { key: 'message', label: 'Message', render: row => <span><b>{row.subjectSnapshot || row.templateName || 'Direct message'}</b><br /><small>{stripHtml(row.bodySnapshot).slice(0, 90)}</small></span> },
  { key: 'audience', label: 'Audience', render: row => row.totalEligible + ' / ' + row.totalSelected },
  { key: 'status', label: 'Status', render: row => <Tag color={campaignStatusColor[row.status]}>{row.status}</Tag> },
]

function CampaignDetail({ campaign, retrying, retry }: { campaign: EmployeeCommunicationCampaign; retrying: boolean; retry: () => Promise<void> }) {
  const percent = campaign.totalEligible ? Math.round(((campaign.totalDelivered || campaign.totalSent || 0) / campaign.totalEligible) * 100) : 0
  const columns: ColumnsType<EmployeeCommunicationCampaignRecipient> = [
    { title: 'Group', dataIndex: 'recipientType', width: 80, render: value => <Tag>{value || 'To'}</Tag> },
    { title: 'Employee', render: (_, row) => <span><b>{row.employeeName}</b><br /><small>{row.employeeCode}</small></span> },
    { title: 'Destination', dataIndex: 'destination' },
    { title: 'Status', dataIndex: 'status', render: value => <Tag color={recipientStatusColor[value as CommunicationRecipientStatus]}>{value}</Tag> },
    { title: 'Updated', render: (_, row) => dateTime(row.readAtUtc || row.deliveredAtUtc || row.sentAtUtc || row.queuedAtUtc) },
  ]
  return <div className="campaign-detail">
    <header><Progress type="circle" percent={percent} size={96} /><div><h2>{campaign.subjectSnapshot || campaign.templateName || channelLabel(campaign.channel) + ' campaign'}</h2><p>{stripHtml(campaign.bodySnapshot)}</p><Space><Tag>{channelLabel(campaign.channel)}</Tag><Tag color={campaignStatusColor[campaign.status]}>{campaign.status}</Tag><span><ClockCircleOutlined /> {dateTime(campaign.createdAtUtc)}</span></Space></div></header>
    <div className="campaign-metrics"><article><b>{campaign.totalSelected}</b><span>Selected</span></article><article><b>{campaign.totalSent}</b><span>Sent</span></article><article><b>{campaign.totalDelivered}</b><span>Delivered</span></article><article><b>{campaign.totalFailed}</b><span>Failed</span></article></div>
    {campaign.totalFailed > 0 && <Button danger icon={<ReloadOutlined />} loading={retrying} onClick={() => void retry()}>Retry failed messages</Button>}
    <Table rowKey="id" columns={columns} dataSource={campaign.recipients || []} pagination={{ pageSize: 10 }} />
  </div>
}
