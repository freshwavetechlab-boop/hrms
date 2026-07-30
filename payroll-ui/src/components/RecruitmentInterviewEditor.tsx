import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card, DatePicker, Descriptions, Form, Input, InputNumber, Modal, Progress, Select, Space, Statistic, Table, Tag, Typography } from 'antd'
import dayjs, { type Dayjs } from 'dayjs'
import { getInterviewFeedback, getInterviewSchedulingContext, saveInterview, saveInterviewFeedback } from '../services/recruitmentTalentService'
import type { RecruitmentCandidateApplication, RecruitmentInterview, RecruitmentInterviewFeedback, RecruitmentInterviewSchedulingContext, SaveRecruitmentInterviewFeedbackCompetencyScore, WorkflowApprover } from '../types/payroll'
import SearchSelect, { selectOptions } from './SearchSelect'
import './RecruitmentInterviewEditor.css'

type CommonProps = {
  open: boolean
  applications: RecruitmentCandidateApplication[]
  panelUsers: WorkflowApprover[]
  onClose: () => void
  onSaved: () => void | Promise<void>
}

type ScheduleProps = CommonProps & {
  mode: 'schedule'
  interview?: RecruitmentInterview | null
  initialApplicationId?: number
}

type FeedbackProps = CommonProps & {
  mode: 'feedback'
  interview: RecruitmentInterview
}

export type RecruitmentInterviewEditorProps = ScheduleProps | FeedbackProps

type ScheduleDraft = {
  id: number
  applicationId: number
  roundCode: string
  interviewType: string
  mode: string
  range: [Dayjs, Dayjs]
  panelUserIds: number[]
  locationOrLink: string
  status: string
  result: string
  overallFeedback: string
  overallScore: number
  timeZoneId: string
}

type CompetencyDraft = Record<number, { score?: number; comments: string }>

const parsePanelIds = (interview: RecruitmentInterview) => {
  if (interview.panelUserIds?.length) return interview.panelUserIds.map(Number).filter(Number.isFinite)
  try {
    return (JSON.parse(interview.panelUserIdsJson || '[]') as unknown[]).map(Number).filter(Number.isFinite)
  } catch {
    return []
  }
}

const contextFromInterview = (interview: RecruitmentInterview): RecruitmentInterviewSchedulingContext => ({
  applicationId: interview.applicationId,
  isPipelineManaged: interview.isPipelineManaged,
  pipelineStageInstanceId: interview.pipelineStageInstanceId,
  roundConfigurationId: interview.roundConfigurationId,
  pipelineStageName: interview.pipelineStageName,
  roundCode: interview.roundCode,
  interviewType: interview.interviewType,
  defaultDurationMinutes: interview.defaultDurationMinutes || 60,
  minimumPanelCount: interview.minimumPanelCount || 1,
  minimumPassingScore: interview.minimumPassingScore || 60,
  scoreInputMode: interview.scoreInputMode || 'PercentageWeighted',
  panelAggregationMethod: interview.panelAggregationMethod || 'Average',
  feedbackRequired: interview.feedbackRequired,
  calendarEnabled: interview.calendarEnabled,
  allowReschedule: interview.allowReschedule,
  timeZoneId: interview.timeZoneId || 'Asia/Kolkata',
  nextAttemptNumber: interview.attemptNumber || 1,
  competencies: interview.competencies || []
})

const initialSchedule = (interview?: RecruitmentInterview | null, applicationId = 0): ScheduleDraft => {
  if (interview) {
    const start = dayjs(interview.scheduledStart)
    const end = dayjs(interview.scheduledEnd)
    return {
      id: interview.id,
      applicationId: interview.applicationId,
      roundCode: interview.roundCode,
      interviewType: interview.interviewType,
      mode: interview.mode,
      range: [start.isValid() ? start : dayjs(), end.isValid() ? end : dayjs().add(60, 'minute')],
      panelUserIds: parsePanelIds(interview),
      locationOrLink: interview.locationOrLink || '',
      status: interview.status,
      result: interview.result,
      overallFeedback: interview.overallFeedback || '',
      overallScore: Number(interview.overallScore || 0),
      timeZoneId: interview.timeZoneId || 'Asia/Kolkata'
    }
  }
  const start = dayjs().add(1, 'day').minute(0).second(0).millisecond(0)
  return { id: 0, applicationId, roundCode: 'Round 1', interviewType: 'Technical', mode: 'Virtual', range: [start, start.add(60, 'minute')], panelUserIds: [], locationOrLink: '', status: 'Scheduled', result: 'Pending', overallFeedback: '', overallScore: 0, timeZoneId: 'Asia/Kolkata' }
}

export default function RecruitmentInterviewEditor(props: RecruitmentInterviewEditorProps) {
  return props.mode === 'schedule' ? <ScheduleEditor {...props} /> : <FeedbackEditor {...props} />
}

function ScheduleEditor({ open, interview, initialApplicationId = 0, applications, panelUsers, onClose, onSaved }: ScheduleProps) {
  const [draft, setDraft] = useState<ScheduleDraft>(() => initialSchedule(interview, initialApplicationId))
  const [context, setContext] = useState<RecruitmentInterviewSchedulingContext | null>(interview ? contextFromInterview(interview) : null)
  const [contextLoading, setContextLoading] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!open) return
    setDraft(initialSchedule(interview, initialApplicationId))
    setContext(interview ? contextFromInterview(interview) : null)
  }, [open, interview, initialApplicationId])

  useEffect(() => {
    if (!open || interview?.id || !draft.applicationId) return
    let active = true
    setContextLoading(true)
    void getInterviewSchedulingContext(draft.applicationId).then(next => {
      if (!active) return
      setContext(next)
      if (next) {
        setDraft(current => {
          const start = current.range[0]
          return {
            ...current,
            roundCode: next.roundCode || next.pipelineStageName || current.roundCode,
            interviewType: next.interviewType || current.interviewType,
            timeZoneId: next.timeZoneId || 'Asia/Kolkata',
            range: [start, start.add(Math.max(1, next.defaultDurationMinutes || 60), 'minute')]
          }
        })
      }
    }).finally(() => { if (active) setContextLoading(false) })
    return () => { active = false }
  }, [open, interview?.id, draft.applicationId])

  const selectedApplication = applications.find(row => row.id === draft.applicationId)
  const eligiblePanelUsers = useMemo(() => panelUsers.filter(user => !selectedApplication || user.clientId == null || user.clientId === selectedApplication.clientId), [panelUsers, selectedApplication])
  const minimumPanelCount = context?.minimumPanelCount || 1
  const rangeValid = draft.range[0]?.isValid() && draft.range[1]?.isValid() && draft.range[1].isAfter(draft.range[0])
  const cannotReschedule = Boolean(interview?.id && context?.isPipelineManaged && !context.allowReschedule)
  const completionValid = draft.status !== 'Completed' || draft.result !== 'Pending'
  const canSave = draft.applicationId > 0 && rangeValid && completionValid && draft.panelUserIds.length >= minimumPanelCount && !contextLoading && (Boolean(interview?.id) || context !== null)

  const submit = async () => {
    if (!canSave) return
    setSaving(true)
    const response = await saveInterview({
      id: draft.id,
      applicationId: draft.applicationId,
      roundCode: draft.roundCode,
      interviewType: draft.interviewType,
      scheduledStart: draft.range[0].format('YYYY-MM-DDTHH:mm:ss'),
      scheduledEnd: draft.range[1].format('YYYY-MM-DDTHH:mm:ss'),
      mode: draft.mode,
      locationOrLink: draft.locationOrLink,
      status: draft.status,
      result: draft.result,
      overallFeedback: draft.overallFeedback,
      overallScore: draft.overallScore,
      panelUserIds: draft.panelUserIds.map(Number).filter(Number.isFinite),
      timeZoneId: draft.timeZoneId
    })
    setSaving(false)
    if (!response.ok) return
    await onSaved()
    onClose()
  }

  return <Modal
    open={open}
    width={900}
    title={<div><Typography.Text type="secondary">Recruitment interview</Typography.Text><Typography.Title level={4}>{interview?.id ? 'Update interview' : 'Schedule interview'}</Typography.Title></div>}
    onCancel={onClose}
    onOk={() => void submit()}
    okText={interview?.id ? 'Save changes' : 'Schedule interview'}
    confirmLoading={saving}
    okButtonProps={{ disabled: !canSave }}
    destroyOnClose
  >
    <div className="interview-editor">
      <Form layout="vertical" className="interview-editor-grid">
        <Form.Item label="Application" required>
          <SearchSelect disabled={Boolean(interview?.id)} value={draft.applicationId} onChange={value => { setContext(null); setDraft({ ...draft, applicationId: Number(value), panelUserIds: [] }) }} options={selectOptions(applications.map(row => ({ value: row.id, label: `${row.applicationCode} - ${row.candidateName} / ${row.positionTitle}` })), 'Select application', 0)} />
        </Form.Item>
        <Form.Item label="Time zone"><Select value={draft.timeZoneId} onChange={timeZoneId => setDraft({ ...draft, timeZoneId })} options={[{ value: 'Asia/Kolkata', label: 'Asia/Kolkata (IST)' }, { value: 'UTC', label: 'UTC' }]} /></Form.Item>
        <Form.Item label="Round" required><Input disabled={Boolean(context?.isPipelineManaged)} value={draft.roundCode} onChange={event => setDraft({ ...draft, roundCode: event.target.value })} /></Form.Item>
        <Form.Item label="Interview type" required><Select disabled={Boolean(context?.isPipelineManaged)} value={draft.interviewType} onChange={interviewType => setDraft({ ...draft, interviewType })} options={['Technical', 'HR', 'Managerial', 'Client', 'Panel'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item className="interview-editor-span" label="Date and time" required extra={cannotReschedule ? 'Rescheduling is disabled in this pipeline round.' : `${context?.defaultDurationMinutes || 60} minute minimum duration.`}>
          <DatePicker.RangePicker disabled={cannotReschedule} showTime={{ format: 'HH:mm' }} format="DD MMM YYYY, HH:mm" value={draft.range} onChange={values => values?.[0] && values?.[1] && setDraft({ ...draft, range: [values[0], values[1]] })} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item label="Mode"><Select value={draft.mode} onChange={mode => setDraft({ ...draft, mode })} options={['Virtual', 'Face-to-Face', 'Telephonic'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item label={draft.mode === 'Virtual' ? 'Meeting link' : 'Location / contact'}><Input value={draft.locationOrLink} onChange={event => setDraft({ ...draft, locationOrLink: event.target.value })} /></Form.Item>
        <Form.Item className="interview-editor-span" label="Panel members" required extra={`At least ${minimumPanelCount} panel member(s) are required.`}>
          <Select mode="multiple" value={draft.panelUserIds} onChange={values => setDraft({ ...draft, panelUserIds: values.map(Number) })} options={eligiblePanelUsers.map(user => ({ value: user.id, label: `${user.displayName} - ${user.email}` }))} showSearch optionFilterProp="label" placeholder="Select interview panel" />
        </Form.Item>
        <Form.Item label="Status"><Select value={draft.status} onChange={status => setDraft({ ...draft, status })} options={['Scheduled', 'Rescheduled', 'Completed', 'Cancelled', 'No Show'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item label="Result"><Select value={draft.result} onChange={result => setDraft({ ...draft, result })} options={['Pending', 'Selected', 'Rejected', 'On Hold', 'No Show', 'Reschedule'].map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item className="interview-editor-span" label="HR summary"><Input.TextArea rows={3} value={draft.overallFeedback} onChange={event => setDraft({ ...draft, overallFeedback: event.target.value })} /></Form.Item>
      </Form>

      {draft.applicationId > 0 && !contextLoading && !context && !interview?.id && <Alert type="warning" showIcon message="Interview cannot be scheduled from the application's current stage" description="Move the application to a configured Interview pipeline stage and ensure that stage has an interview-round configuration." />}
      {!completionValid && <Alert type="warning" showIcon message="Select a final result before marking this interview completed." />}
      {context && <Card size="small" className="interview-round-card" title={<Space><span>{context.pipelineStageName || context.roundCode}</span>{context.isPipelineManaged && <Tag color="purple">Pipeline managed</Tag>}</Space>}>
        <Descriptions size="small" column={{ xs: 1, sm: 2, md: 4 }}>
          <Descriptions.Item label="Attempt">{context.nextAttemptNumber}</Descriptions.Item>
          <Descriptions.Item label="Passing score">{context.minimumPassingScore}%</Descriptions.Item>
          <Descriptions.Item label="Feedback">{context.feedbackRequired ? 'Required' : 'Optional'}</Descriptions.Item>
          <Descriptions.Item label="Calendar">{context.calendarEnabled ? 'Enabled' : 'Manual scheduling'}</Descriptions.Item>
        </Descriptions>
        {!!context.competencies.length && <div className="interview-competency-tags">{context.competencies.map(row => <Tag key={row.id}>{row.competencyName} · {row.weightPercent}%</Tag>)}</div>}
      </Card>}
    </div>
  </Modal>
}

function FeedbackEditor({ open, interview, panelUsers, onClose, onSaved }: FeedbackProps) {
  const [rows, setRows] = useState<RecruitmentInterviewFeedback[]>([])
  const [panelUserId, setPanelUserId] = useState(0)
  const [recommendation, setRecommendation] = useState('Hire')
  const [overallScore, setOverallScore] = useState(0)
  const [comments, setComments] = useState('')
  const [competencyDraft, setCompetencyDraft] = useState<CompetencyDraft>({})
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const panelIds = useMemo(() => parsePanelIds(interview), [interview])
  const competencies = interview.competencies || []

  const resetForm = () => {
    setPanelUserId(0)
    setRecommendation('Hire')
    setOverallScore(0)
    setComments('')
    setCompetencyDraft(Object.fromEntries(competencies.map(row => [row.id, { score: undefined, comments: '' }])))
  }

  useEffect(() => {
    if (!open) return
    let active = true
    resetForm()
    setLoading(true)
    void getInterviewFeedback(interview.id).then(next => { if (active) setRows(next) }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  // Reset whenever a different interview is opened. Competencies are part of the interview snapshot.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, interview.id])

  const selectedScores = competencies.map(row => ({ competency: row, draft: competencyDraft[row.id] })).filter(row => row.draft?.score != null)
  const selectedWeight = selectedScores.reduce((sum, row) => sum + Number(row.competency.weightPercent || 0), 0)
  const pointsMode = interview.scoreInputMode === 'Points'
  const totalPoints = selectedScores.reduce((sum, row) => sum + Number(row.draft?.score || 0), 0)
  const weightedScore = competencies.length
    ? (selectedWeight > 0 ? pointsMode ? totalPoints * 100 / selectedWeight : selectedScores.reduce((sum, row) => sum + Number(row.draft?.score || 0) * Number(row.competency.weightPercent || 0), 0) / selectedWeight : 0)
    : overallScore
  const allRequiredScoresPresent = !interview.feedbackRequired || !competencies.length || competencies.every(row => competencyDraft[row.id]?.score != null)
  const hasConfiguredScore = !competencies.length || selectedScores.length > 0
  const feedbackBlocked = ['Cancelled', 'No Show'].includes(interview.status)
  const canSubmit = !feedbackBlocked && panelUserId > 0 && allRequiredScoresPresent && hasConfiguredScore

  const editFeedback = (row: RecruitmentInterviewFeedback) => {
    const scoreLookup = new Map((row.competencyScores || []).map(score => [score.interviewStageCompetencyId, score]))
    setPanelUserId(row.panelUserId)
    setRecommendation(row.recommendation || 'Hire')
    setOverallScore(Number(row.overallScore || 0))
    setComments(row.comments || '')
    setCompetencyDraft(Object.fromEntries(competencies.map(competency => {
      const score = scoreLookup.get(competency.id)
      return [competency.id, { score: score ? Number(score.score) : undefined, comments: score?.comments || '' }]
    })))
  }

  const submit = async () => {
    if (!canSubmit) return
    setSaving(true)
    const competencyScores: SaveRecruitmentInterviewFeedbackCompetencyScore[] = selectedScores.map(row => ({ interviewStageCompetencyId: row.competency.id, score: Number(row.draft?.score || 0), comments: row.draft?.comments || '' }))
    const response = await saveInterviewFeedback(interview.id, { panelUserId, overallScore: Number(weightedScore.toFixed(2)), recommendation, competencyScoresJson: '{}', comments, competencyScores })
    setSaving(false)
    if (!response.ok) return
    setRows(await getInterviewFeedback(interview.id))
    resetForm()
    await onSaved()
  }

  return <Modal
    open={open}
    width={980}
    title={<div><Typography.Text type="secondary">Panel feedback</Typography.Text><Typography.Title level={4}>{interview.candidateName} · {interview.roundCode}</Typography.Title></div>}
    onCancel={onClose}
    footer={<Space><Button onClick={onClose}>Close</Button><Button type="primary" loading={saving} disabled={!canSubmit} onClick={() => void submit()}>Save feedback</Button></Space>}
    destroyOnClose
  >
    <div className="interview-editor feedback-editor">
      <Descriptions size="small" bordered column={{ xs: 1, sm: 2, md: 4 }}>
        <Descriptions.Item label="Position">{interview.positionTitle}</Descriptions.Item>
        <Descriptions.Item label="Schedule">{dayjs(interview.scheduledStart).format('DD MMM YYYY, HH:mm')}</Descriptions.Item>
        <Descriptions.Item label="Passing score">{interview.minimumPassingScore || 0}%</Descriptions.Item>
        <Descriptions.Item label="Feedback">{interview.feedbackRequired ? <Tag color="red">Required</Tag> : <Tag>Optional</Tag>}</Descriptions.Item>
      </Descriptions>

      <Card size="small" title="Submitted panel feedback" loading={loading}>
        <Table<RecruitmentInterviewFeedback> rowKey="id" size="small" pagination={false} dataSource={rows} columns={[
          { title: 'Panel member', dataIndex: 'panelUserName' },
          { title: 'Score', dataIndex: 'overallScore', render: value => `${Number(value || 0).toFixed(2)}%` },
          { title: 'Recommendation', dataIndex: 'recommendation' },
          { title: 'Submitted', dataIndex: 'submittedAt', render: value => dayjs(String(value)).format('DD MMM YYYY, HH:mm') },
          { title: '', key: 'action', width: 80, render: (_, row) => <Button size="small" onClick={() => editFeedback(row)}>Edit</Button> }
        ]} locale={{ emptyText: 'No panel feedback submitted yet.' }} />
      </Card>

      {!panelIds.length && <Alert type="warning" showIcon message="No panel members are assigned to this interview." />}
      {feedbackBlocked && <Alert type="error" showIcon message={`Feedback cannot be submitted for an interview marked ${interview.status}.`} />}
      <Form layout="vertical" className="interview-editor-grid">
        <Form.Item label="Panel member" required>
          <SearchSelect value={panelUserId} onChange={value => { const id = Number(value); const existing = rows.find(row => row.panelUserId === id); existing ? editFeedback(existing) : resetFormWithPanel(id, competencies, setPanelUserId, setRecommendation, setOverallScore, setComments, setCompetencyDraft) }} options={selectOptions(panelIds.map(id => ({ value: id, label: panelUsers.find(user => user.id === id)?.displayName || `User #${id}` })), 'Select panel member', 0)} />
        </Form.Item>
        <Form.Item label="Recommendation" required><Select value={recommendation} onChange={setRecommendation} options={['Strong Hire', 'Hire', 'On Hold', 'No Hire', 'Strong No Hire'].map(value => ({ value, label: value }))} /></Form.Item>

        {!!competencies.length && <div className="interview-editor-span competency-score-grid">
          <div className="competency-score-heading"><div><Typography.Title level={5}>Configured competencies</Typography.Title><Typography.Text type="secondary">{pointsMode ? 'Enter actual points up to each configured maximum.' : 'Enter percentages; weights are applied automatically.'}</Typography.Text></div><Statistic title={pointsMode ? 'Total points' : 'Weighted score'} value={Number((pointsMode ? totalPoints : weightedScore).toFixed(2))} suffix={`/ ${pointsMode ? competencies.reduce((sum, row) => sum + Number(row.weightPercent || 0), 0) : 100}`} /></div>
          {competencies.map(competency => {
            const value = competencyDraft[competency.id]?.score
            const maximum = pointsMode ? Number(competency.weightPercent) : 100
            return <Card size="small" key={competency.id} title={competency.competencyName} extra={<Space><Tag color="blue">{pointsMode ? `${competency.weightPercent} max points` : `${competency.weightPercent}% weight`}</Tag><Tag color={value != null && value >= competency.minimumScore ? 'green' : 'orange'}>Minimum {competency.minimumScore}</Tag></Space>}>
              <div className="competency-score-row">
                <InputNumber min={0} max={maximum} precision={2} value={value} onChange={score => setCompetencyDraft(current => ({ ...current, [competency.id]: { ...current[competency.id], score: score == null ? undefined : Number(score) } }))} placeholder={`0 - ${maximum}`} />
                <Progress percent={maximum > 0 ? Number(value || 0) * 100 / maximum : 0} showInfo={false} status={value != null && value < competency.minimumScore ? 'exception' : 'normal'} />
                <Input placeholder="Competency-specific observation" value={competencyDraft[competency.id]?.comments || ''} onChange={event => setCompetencyDraft(current => ({ ...current, [competency.id]: { ...current[competency.id], comments: event.target.value } }))} />
              </div>
            </Card>
          })}
        </div>}

        {!competencies.length && <Form.Item label="Overall score" required><InputNumber min={0} max={100} precision={2} value={overallScore} onChange={value => setOverallScore(Number(value || 0))} /></Form.Item>}
        <Form.Item className="interview-editor-span" label="Overall comments"><Input.TextArea rows={4} value={comments} onChange={event => setComments(event.target.value)} placeholder="Capture evidence, strengths, concerns and hiring rationale." /></Form.Item>
      </Form>
    </div>
  </Modal>
}

function resetFormWithPanel(
  panelUserId: number,
  competencies: RecruitmentInterview['competencies'],
  setPanelUserId: (value: number) => void,
  setRecommendation: (value: string) => void,
  setOverallScore: (value: number) => void,
  setComments: (value: string) => void,
  setCompetencyDraft: (value: CompetencyDraft) => void
) {
  setPanelUserId(panelUserId)
  setRecommendation('Hire')
  setOverallScore(0)
  setComments('')
  setCompetencyDraft(Object.fromEntries(competencies.map(row => [row.id, { score: undefined, comments: '' }])))
}
