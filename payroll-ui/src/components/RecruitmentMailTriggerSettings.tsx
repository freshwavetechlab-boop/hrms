import { useEffect, useMemo, useState } from 'react'
import { DeleteOutlined, EditOutlined, MailOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, Form, Input, Popconfirm, Space, Switch, Tag } from 'antd'
import DataTable from './DataTable'
import { deleteRecruitmentMailTrigger, getRecruitmentMailTriggers, saveRecruitmentMailTrigger } from '../services/notificationService'
import type { RecruitmentMailTrigger } from '../types/payroll'

const recipientLabels: Array<[keyof RecruitmentMailTrigger, string]> = [
  ['sendToCandidate', 'Candidate'],
  ['sendToPanel', 'Panel'],
  ['sendToRequester', 'Requester'],
  ['sendToRecruiter', 'Recruiter'],
  ['sendToApprovers', 'Approvers']
]

const tokens = ['{{candidateName}}', '{{positionTitle}}', '{{offerNumber}}', '{{currency}}', '{{offeredCtc}}', '{{proposedJoiningDate}}', '{{workOrderNumber}}', '{{momVersion}}', '{{candidateActionUrl}}', '{{status}}']

export default function RecruitmentMailTriggerSettings() {
  const [rows, setRows] = useState<RecruitmentMailTrigger[]>([])
  const [editing, setEditing] = useState<RecruitmentMailTrigger | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)

  const load = async () => {
    setLoading(true)
    try { setRows(await getRecruitmentMailTriggers()) } finally { setLoading(false) }
  }

  useEffect(() => { void load() }, [])
  const selectedRecipients = useMemo(() => editing ? recipientLabels.filter(([key]) => Boolean(editing[key])).length : 0, [editing])

  const save = async () => {
    if (!editing) return
    setSaving(true)
    try {
      const response = await saveRecruitmentMailTrigger(editing)
      if (response.ok) { setEditing(null); await load() }
    } finally { setSaving(false) }
  }

  const remove = async (id: number) => {
    const response = await deleteRecruitmentMailTrigger(id)
    if (response.ok) await load()
  }

  return <div data-testid="recruitment-mail-triggers">
    <div className="component-table-head">
      <div><b>Recruitment lifecycle mail triggers</b><span>One auditable matrix for MoM, negotiation and offer emails. Deleted triggers stop future mail.</span></div>
      <Tag icon={<MailOutlined />} color="blue">{rows.filter(row => row.isEnabled).length} active</Tag>
    </div>
    {rows.length
      ? <DataTable rows={rows} exportFileName="recruitment-mail-triggers" columns={[
        { key: 'triggerName', label: 'Trigger', render: row => <div className="communication-table-primary"><b>{row.triggerName}</b><span>{row.triggerCode}</span></div> },
        { key: 'clientName', label: 'Client' },
        { key: 'subjectTemplate', label: 'Subject / template', render: row => <div className="communication-table-primary"><b>{row.subjectTemplate}</b><span>{row.description}</span></div> },
        { key: 'recipients', label: 'Recipients', render: row => <Space size={[4, 4]} wrap>{recipientLabels.filter(([key]) => Boolean(row[key])).map(([, label]) => <Tag key={label}>{label}</Tag>)}</Space> },
        { key: 'isEnabled', label: 'Status', render: row => <Tag color={row.isEnabled ? 'green' : 'default'}>{row.isEnabled ? 'Enabled' : 'Disabled'}</Tag> },
        { key: 'updatedAtUtc', label: 'Updated', value: row => row.updatedAtUtc ? new Date(`${row.updatedAtUtc}Z`).toLocaleString('en-IN') : '-' }
      ]} actions={row => <Space>
        <Button size="small" icon={<EditOutlined />} onClick={() => setEditing({ ...row })}>Edit</Button>
        <Popconfirm title="Delete this mail trigger?" description="Future emails for this event will stop." okText="Delete" okButtonProps={{ danger: true }} onConfirm={() => void remove(row.id)}>
          <Button size="small" danger icon={<DeleteOutlined />}>Delete</Button>
        </Popconfirm>
      </Space>} />
      : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={loading ? 'Loading mail triggers…' : 'No recruitment mail triggers configured'} />}

    <Drawer title={editing ? `Edit · ${editing.triggerName}` : 'Edit mail trigger'} open={Boolean(editing)} width="min(860px, 96vw)" destroyOnClose onClose={() => setEditing(null)} footer={<Space><Button onClick={() => setEditing(null)}>Cancel</Button><Button type="primary" loading={saving} disabled={!editing?.triggerName.trim() || !editing?.subjectTemplate.trim() || selectedRecipients === 0} onClick={() => void save()}>Save trigger</Button></Space>}>
      {editing && <Form layout="vertical" requiredMark={false}>
        <Alert type="info" showIcon message={editing.triggerCode} description="Changes apply to future deliveries. Already queued or sent mail is not changed." />
        <Form.Item label="Trigger name"><Input value={editing.triggerName} onChange={event => setEditing({ ...editing, triggerName: event.target.value })} /></Form.Item>
        <Form.Item label="Description"><Input value={editing.description} onChange={event => setEditing({ ...editing, description: event.target.value })} /></Form.Item>
        <Form.Item label="Enabled"><Switch checked={editing.isEnabled} onChange={value => setEditing({ ...editing, isEnabled: value })} /></Form.Item>
        <Form.Item label="Recipients"><Space wrap>{recipientLabels.map(([key, label]) => <span key={String(key)}><Switch size="small" checked={Boolean(editing[key])} onChange={value => setEditing({ ...editing, [key]: value })} /> {label}</span>)}</Space></Form.Item>
        <Form.Item label="Subject template"><Input value={editing.subjectTemplate} onChange={event => setEditing({ ...editing, subjectTemplate: event.target.value })} /></Form.Item>
        {editing.sendToCandidate && <Form.Item label="Candidate email body (HTML)"><Input.TextArea rows={7} value={editing.candidateBodyTemplate} onChange={event => setEditing({ ...editing, candidateBodyTemplate: event.target.value })} /></Form.Item>}
        {(editing.sendToPanel || editing.sendToRequester || editing.sendToRecruiter || editing.sendToApprovers) && <Form.Item label="Internal email body (HTML)"><Input.TextArea rows={7} value={editing.internalBodyTemplate} onChange={event => setEditing({ ...editing, internalBodyTemplate: event.target.value })} /></Form.Item>}
        <Form.Item label="Available placeholders"><Space wrap>{tokens.map(token => <Tag key={token}>{token}</Tag>)}</Space></Form.Item>
      </Form>}
    </Drawer>
  </div>
}
