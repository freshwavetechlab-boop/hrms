import { useEffect, useState } from 'react'
import { Alert, Button, Col, Divider, Drawer, Form, Input, InputNumber, Row, Space, Switch, Tag } from 'antd'
import { ApiOutlined, SyncOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import { getIncomingMail, getIncomingMailDetail, runImapAction, reconcileMailDelivery } from '../services/notificationService'
import type { NotificationIncomingMail, NotificationSmtpSetting } from '../types/payroll'

export function NotificationImapFields({ smtp, setSmtp }: { smtp: NotificationSmtpSetting; setSmtp: (value: NotificationSmtpSetting) => void }) {
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState('')
  const test = async () => {
    setTesting(true); setResult('')
    try { const response = await runImapAction('test'); if (response.ok) setResult(response.data.message) }
    finally { setTesting(false) }
  }
  return <>
    <Divider orientation="left">Incoming mail / IMAP</Divider>
    <Row gutter={[14, 4]}>
      <Col xs={24} md={6}><Form.Item label="Enable incoming mail"><Switch checked={smtp.imapEnabled ?? false} onChange={imapEnabled => setSmtp({ ...smtp, imapEnabled })} /></Form.Item></Col>
      <Col xs={24} md={8}><Form.Item label="IMAP host"><Input value={smtp.imapHost ?? 'imap.hostinger.com'} onChange={event => setSmtp({ ...smtp, imapHost: event.target.value })} /></Form.Item></Col>
      <Col xs={12} md={4}><Form.Item label="IMAP port"><InputNumber min={1} max={65535} value={smtp.imapPort ?? 993} onChange={value => setSmtp({ ...smtp, imapPort: value ?? 993 })} /></Form.Item></Col>
      <Col xs={12} md={6}><Form.Item label="Mailbox folder"><Input value={smtp.imapFolder ?? 'INBOX'} onChange={event => setSmtp({ ...smtp, imapFolder: event.target.value })} /></Form.Item></Col>
    </Row>
    <p>Uses the SMTP username and password above over a secure TLS connection. Save email settings before testing. New mail syncs every minute while background workers are enabled; each Message-ID is processed once. Mailbox read flags remain unchanged.</p>
    <Space wrap><Tag color={smtp.imapEnabled ? 'green' : 'default'}>{smtp.imapEnabled ? 'Incoming mail enabled' : 'Incoming mail disabled'}</Tag><Button icon={<ApiOutlined />} loading={testing} onClick={() => void test()}>Test saved IMAP connection</Button></Space>
    {result && <Alert style={{ marginTop: 12 }} type="success" showIcon message={result} />}
    {smtp.imapLastError && <Alert style={{ marginTop: 12 }} type="error" showIcon message="Last incoming mail sync failed" description={smtp.imapLastError} />}
  </>
}

export default function NotificationIncomingMailPanel({ smtp, onSynced }: { smtp: NotificationSmtpSetting; onSynced: () => void }) {
  const [rows, setRows] = useState<NotificationIncomingMail[]>([])
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState('')
  const [selected, setSelected] = useState<NotificationIncomingMail | null>(null)
  const [reportSince, setReportSince] = useState(() => new Date(Date.now() - 7 * 86400000).toISOString().slice(0, 10))
  const load = async () => setRows(await getIncomingMail())
  useEffect(() => { void load() }, [])
  const sync = async () => {
    setBusy(true); setResult('')
    try {
      const response = await runImapAction('sync')
      if (response.ok) { setResult(`${response.data.count} new message(s) imported. Previously processed Message-IDs skipped.`); onSynced(); await load() }
    } finally { setBusy(false) }
  }
  const reconcile = async () => {
    setBusy(true); setResult('')
    try {
      const response = await reconcileMailDelivery(reportSince)
      if (response.ok) { setResult(`${response.data.updated} delivery report(s) matched; ${response.data.unmatched} need review; ${response.data.skipped} already processed.`); await load() }
    } finally { setBusy(false) }
  }
  return <>
    <div className="component-table-head"><div><b>Incoming mail</b><span>{smtp.imapLastSyncAt ? `Last sync: ${new Date(smtp.imapLastSyncAt + (smtp.imapLastSyncAt.endsWith('Z') ? '' : 'Z')).toLocaleString('en-IN')}` : 'No sync yet'}. Replies are matched using In-Reply-To / References and the original recipient.</span></div><Space wrap><Button onClick={() => void load()}>Refresh</Button><Button type="primary" icon={<SyncOutlined />} disabled={!smtp.imapEnabled} loading={busy} onClick={() => void sync()}>Sync incoming mail</Button></Space></div>
    {!smtp.imapEnabled && <Alert type="info" message="Enable incoming mail in Email / SMTP configuration and save email settings." />}
    <Space wrap style={{ marginBottom: 12 }}><Input aria-label="Delivery reports from date" type="date" value={reportSince} onChange={event => setReportSince(event.target.value)} style={{ width: 165 }} /><Button loading={busy} disabled={!smtp.imapEnabled || !reportSince} onClick={() => void reconcile()}>Check payslip delivery reports</Button></Space>
    {result && <Alert type="success" message={result} />}
    {smtp.imapLastError && <Alert type="error" message={smtp.imapLastError} />}
    <DataTable rows={rows} exportFileName="incoming-mail" columns={[
      { key: 'receivedAt', label: 'Received', value: row => new Date(row.receivedAt + (row.receivedAt.endsWith('Z') ? '' : 'Z')).toLocaleString('en-IN') },
      { key: 'sender', label: 'From' }, { key: 'subject', label: 'Subject' },
      { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Delivery failed' ? 'red' : row.status === 'Delivery needs review' ? 'gold' : row.queueId ? 'green' : 'blue'}>{row.status}</Tag> },
      { key: 'queueId', label: 'Original email', value: row => row.queueId ? `#${row.queueId}` : 'Unmatched' },
      { key: 'messageId', label: 'Message-ID' }
    ]} actions={row => <Button size="small" onClick={() => void getIncomingMailDetail(row.id).then(setSelected)}>View</Button>} />
    {selected && <Drawer title={selected.subject || 'Incoming mail'} width="min(680px, 100vw)" open onClose={() => setSelected(null)}>
      <p><b>From:</b> {selected?.sender}</p><p><b>Message-ID:</b> {selected?.messageId || 'Not provided by sender'}</p>
      <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'inherit' }}>{selected?.bodyText || 'No preview available. Open the original message in your mailbox for attachments or a large body.'}</pre>
    </Drawer>}
  </>
}
