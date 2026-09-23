import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Checkbox, DatePicker, Form, Input, InputNumber, Select, Space, Table, Tag, message } from 'antd'
import dayjs, { type Dayjs } from 'dayjs'
import type { RecruitmentCandidateApplication, WorkflowApprover } from '../types/payroll'
import { getInterviewSchedulingContext, saveInterviewBatchItem, sendInterviewInviteBatchItem } from '../services/recruitmentTalentService'
import { getRecruitmentApplicationTransitions, transitionRecruitmentApplicationSilently } from '../services/recruitmentOrchestrationService'
import RecruitmentEditorDrawer from './RecruitmentEditorDrawer'

type Props = {
  open: boolean
  applications: RecruitmentCandidateApplication[]
  panelUsers: WorkflowApprover[]
  onClose: () => void
  onSaved: () => void | Promise<void>
}

type BatchRow = { application: RecruitmentCandidateApplication; start: Dayjs; destination: string; panelUserIds?: number[]; decisionApproverUserId?: number; durationMinutes?: number; roundCode?: string; interviewType?: string }

const nextStart = () => dayjs().add(1, 'day').hour(10).minute(0).second(0).millisecond(0)

export default function RecruitmentBatchInterviewEditor({ open, applications, panelUsers, onClose, onSaved }: Props) {
  const [rows, setRows] = useState<BatchRow[]>([])
  const [panelUserIds, setPanelUserIds] = useState<number[]>([])
  const [decisionApproverUserId, setDecisionApproverUserId] = useState<number>()
  const [mode, setMode] = useState('Virtual')
  const [timeZoneId, setTimeZoneId] = useState('Asia/Kolkata')
  const [durationMinutes, setDurationMinutes] = useState(60)
  const [gapMinutes, setGapMinutes] = useState(15)
  const [defaultDestination, setDefaultDestination] = useState('')
  const [sendInvites, setSendInvites] = useState(true)
  const [saving, setSaving] = useState(false)
  const [failures, setFailures] = useState<string[]>([])
  const clientId = applications[0]?.clientId
  const eligiblePanelUsers = useMemo(() => panelUsers.filter(user => user.clientId == null || user.clientId === clientId), [clientId, panelUsers])

  const rebuildSlots = (source = applications) => {
    const base = rows[0]?.start || nextStart()
    setRows(source.map((application, index) => ({
      ...rows.find(row => row.application.id === application.id),
      application,
      start: base.add(index * (durationMinutes + gapMinutes), 'minute'),
      destination: rows.find(row => row.application.id === application.id)?.destination || defaultDestination,
    })))
  }

  useEffect(() => {
    if (!open) return
    setFailures([])
    const base = nextStart()
    setRows(applications.map((application, index) => ({ application, start: base.add(index * 75, 'minute'), destination: '' })))
  }, [applications, open])

  const moveToInterview = async (application: RecruitmentCandidateApplication) => {
    if (/interview/i.test(application.currentStage || '')) return
    const transitions = await getRecruitmentApplicationTransitions(application.id)
    const transition = transitions.find(row => /interview/i.test(`${row.toStageCode} ${row.outcomeCode} ${row.actionLabel}`) && !/reject/i.test(row.outcomeCode))
    if (!transition) throw new Error('candidate is not ready for an Interview pipeline transition')
    const moved = await transitionRecruitmentApplicationSilently(application.id, transition.id, 'Selected for batch interview scheduling.')
    if (!moved.ok) throw new Error(moved.error || 'candidate could not move to Interview stage')
  }

  const submit = async () => {
    if (!rows.length || rows.some(row => !(row.panelUserIds ?? panelUserIds).length || !(row.decisionApproverUserId ?? decisionApproverUserId))) return
    if (sendInvites && rows.some(row => !row.destination.trim())) return void message.warning('Add a meeting link or location for every candidate before sending invites.')
    setSaving(true)
    setFailures([])
    let scheduled = 0
    let invited = 0
    const nextFailures: string[] = []
    for (const row of rows) {
      try {
        await moveToInterview(row.application)
        const context = await getInterviewSchedulingContext(row.application.id)
        if (!context) throw new Error('Interview scheduling context is unavailable')
        if (row.application.interviewSettingsMissing && (!row.roundCode?.trim() || !row.interviewType?.trim())) throw new Error('enter the missing round name and interview type in this row')
        const rowPanel = row.panelUserIds ?? panelUserIds
        if (rowPanel.length < Math.max(1, context.minimumPanelCount || 1)) throw new Error(`requires at least ${context.minimumPanelCount} panel member(s)`)
        const duration = Math.max(row.durationMinutes ?? durationMinutes, context.defaultDurationMinutes || 1)
        const saved = await saveInterviewBatchItem({
          id: 0,
          applicationId: row.application.id,
          roundCode: row.roundCode || context.roundCode || context.pipelineStageName || 'Interview',
          interviewType: row.interviewType || context.interviewType || 'Panel',
          scheduledStart: row.start.format('YYYY-MM-DDTHH:mm:ss'),
          scheduledEnd: row.start.add(duration, 'minute').format('YYYY-MM-DDTHH:mm:ss'),
          mode,
          locationOrLink: row.destination.trim(),
          status: 'Scheduled',
          result: 'Pending',
          overallFeedback: '',
          overallScore: 0,
          panelUserIds: rowPanel,
          decisionApproverUserId: row.decisionApproverUserId ?? decisionApproverUserId,
          timeZoneId,
        })
        if (!saved.ok || !saved.data) throw new Error(saved.error || 'interview could not be saved')
        scheduled += 1
        if (sendInvites) {
          const invite = await sendInterviewInviteBatchItem(saved.data.id)
          if (!invite.ok) throw new Error(`scheduled, but invite failed: ${invite.error}`)
          invited += 1
        }
      } catch (error) {
        nextFailures.push(`${row.application.candidateName}: ${error instanceof Error ? error.message : 'failed'}`)
      }
    }
    setSaving(false)
    setFailures(nextFailures)
    await onSaved()
    if (scheduled) message.success(`${scheduled} interview${scheduled === 1 ? '' : 's'} scheduled${sendInvites ? `; ${invited} invite${invited === 1 ? '' : 's'} sent` : ''}.`)
    if (!nextFailures.length) onClose()
  }

  return <RecruitmentEditorDrawer
    open={open}
    width="min(1180px, 97vw)"
    eyebrow="Batch interview scheduling"
    title={`Schedule ${rows.length} candidate${rows.length === 1 ? '' : 's'}`}
    description="Use one panel and sequential slots; every candidate remains individually editable before scheduling."
    onClose={onClose}
    onSubmit={() => void submit()}
    submitText={sendInvites ? 'Schedule & send invites' : 'Schedule interviews'}
    submitLoading={saving}
    submitDisabled={!rows.length || rows.some(row => !(row.panelUserIds ?? panelUserIds).length || !(row.decisionApproverUserId ?? decisionApproverUserId)) || (sendInvites && rows.some(row => !row.destination.trim()))}
    destroyOnClose
  >
    <Form layout="vertical">
      <div className="interview-editor-grid">
        <Form.Item label="Panel members" required className="interview-editor-span"><Select mode="multiple" showSearch optionFilterProp="label" value={panelUserIds} onChange={values => setPanelUserIds(values.map(Number))} options={eligiblePanelUsers.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` }))} placeholder="Select common interview panel" /></Form.Item>
        <Form.Item label="Final decision approver" required><Select allowClear showSearch optionFilterProp="label" value={decisionApproverUserId} onChange={setDecisionApproverUserId} options={eligiblePanelUsers.map(user => ({ value: user.id, label: user.displayName }))} placeholder="Select common decision approver" /></Form.Item>
        <Form.Item label="Mode"><Select value={mode} onChange={setMode} options={['Virtual', 'Face-to-Face', 'Telephonic'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item label="Time zone"><Select value={timeZoneId} onChange={setTimeZoneId} options={[{ value: 'Asia/Kolkata', label: 'Asia/Kolkata (IST)' }, { value: 'UTC', label: 'UTC' }]} /></Form.Item>
        <Form.Item label="Duration (minutes)"><InputNumber min={15} max={480} value={durationMinutes} onChange={value => setDurationMinutes(Number(value || 60))} style={{ width: '100%' }} /></Form.Item>
        <Form.Item label="Gap between candidates"><InputNumber min={0} max={240} value={gapMinutes} onChange={value => setGapMinutes(Number(value || 0))} style={{ width: '100%' }} /></Form.Item>
        <Form.Item label={mode === 'Virtual' ? 'Default meeting link' : 'Default location / contact'} className="interview-editor-span"><Space.Compact block><Input value={defaultDestination} onChange={event => setDefaultDestination(event.target.value)} placeholder="Apply a common value, then override any candidate below" /><Button onClick={() => setRows(current => current.map(row => ({ ...row, destination: defaultDestination })))}>Apply to all</Button><Button onClick={() => rebuildSlots()}>Rebuild slots</Button></Space.Compact></Form.Item>
        <Form.Item className="interview-editor-span"><Checkbox checked={sendInvites} onChange={event => setSendInvites(event.target.checked)}>Email each candidate and the selected panel after scheduling</Checkbox></Form.Item>
      </div>
    </Form>
    {!!failures.length && <Alert type="warning" showIcon message={`${failures.length} candidate${failures.length === 1 ? '' : 's'} need attention`} description={<ul>{failures.map(value => <li key={value}>{value}</li>)}</ul>} />}
    <Table<BatchRow>
      rowKey={row => row.application.id}
      pagination={false}
      size="small"
      dataSource={rows}
      scroll={{ x: 1400 }}
      columns={[
        { title: 'Candidate', render: (_, row) => <div><b>{row.application.candidateName}</b><br /><small>{row.application.applicationCode} · ATS {row.application.atsScore ?? 'pending'}</small></div> },
        { title: 'Job', render: (_, row) => <div>{row.application.positionTitle}<br /><Tag>{row.application.currentStage}</Tag></div> },
        { title: 'Panel (required)', width: 220, render: (_, row) => <Select mode="multiple" value={row.panelUserIds ?? panelUserIds} options={eligiblePanelUsers.map(user => ({ value: user.id, label: user.displayName }))} onChange={panelUserIds => setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, panelUserIds } : item))} style={{ width: '100%' }} /> },
        { title: 'Decision approver (required)', width: 190, render: (_, row) => <Select value={row.decisionApproverUserId ?? decisionApproverUserId} options={eligiblePanelUsers.map(user => ({ value: user.id, label: user.displayName }))} onChange={decisionApproverUserId => setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, decisionApproverUserId } : item))} style={{ width: '100%' }} /> },
        { title: 'Duration (minutes)', render: (_, row) => <InputNumber min={15} max={480} value={row.durationMinutes ?? durationMinutes} onChange={value => setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, durationMinutes: value || undefined } : item))} /> },
        { title: 'Round / type', width: 180, render: (_, row) => row.application.interviewSettingsMissing ? <Space direction="vertical"><Tag color="orange">Complete round settings</Tag><Input placeholder="Round name (required)" value={row.roundCode} onChange={event => setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, roundCode: event.target.value } : item))} /><Input placeholder="Interview type (required)" value={row.interviewType} onChange={event => setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, interviewType: event.target.value } : item))} /></Space> : <Tag>Job round configured</Tag> },
        { title: 'Start', width: 230, render: (_, row) => <DatePicker showTime format="DD MMM YYYY, HH:mm" value={row.start} onChange={value => value && setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, start: value } : item))} style={{ width: '100%' }} /> },
        { title: mode === 'Virtual' ? 'Meeting link' : 'Location / contact', render: (_, row) => <Input value={row.destination} onChange={event => setRows(current => current.map(item => item.application.id === row.application.id ? { ...item, destination: event.target.value } : item))} /> },
      ]}
    />
  </RecruitmentEditorDrawer>
}
