import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Badge, Button, Empty, List, Popover, Progress, Space, Spin, Tag, Typography } from 'antd'
import { BellOutlined, ReloadOutlined } from '@ant-design/icons'
import { getJsonResult } from '../services/apiClient'
import { useAuthSession } from './AuthGate'
import type { RecruitmentInterview } from '../types/payroll'
import { getAttendanceGaps } from '../services/attendanceIntegrationService'
import { dismissExcelPayslipMailJob, getExcelPayslipMailJobs, type ExcelPayslipMailJob } from '../services/excelPayslipService'
import { getRecruitmentMomPings } from '../services/recruitmentCaseService'
import { isUnrestrictedClientAdmin } from '../utils/clientAccess'
import { dismissOfferMailJob, getOfferMailJobs, validOfferMailJobs, type OfferMailJob } from '../services/recruitmentOfferMailService'
import type { BulkMailJob } from './BulkMailProgress'

type ActionItem = { key: string; title: string; detail: string; route: string; mailJob?: ExcelPayslipMailJob; offerJob?: OfferMailJob }
type PendingTask = { id: number; stageName: string; resourceType: string; resourceId: string }

// Add future action providers here using authorized, recipient-scoped APIs.
// Tasks disappear when completed. Mail jobs retain their outcome until the sender dismisses them.
export default function ActionNotifications() {
  const session = useAuthSession()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [items, setItems] = useState<ActionItem[]>([])
  const [jobFeed, setJobFeed] = useState<{ scope: string; items: ExcelPayslipMailJob[] }>({ scope: '', items: [] })
  const [offerFeed, setOfferFeed] = useState<{ scope: string; items: OfferMailJob[] }>({ scope: '', items: [] })
  const [dismissing, setDismissing] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const generation = useRef(0)
  const canSeeMailJobs = isUnrestrictedClientAdmin(session?.user) && Boolean(session?.user.permissions.some(permission => ['payroll.run', 'payroll.approve', 'payroll.payments'].includes(permission)))
  const canSeeOfferJobs = Boolean(session?.user.permissions.some(permission => ['recruitment.offer.issue', 'recruitment.manage', 'settings.manage'].includes(permission)))
  const jobScope = `${session?.user.id}:${session?.user.clientId || ''}:${canSeeMailJobs}`
  const currentJobScope = useRef(jobScope)
  currentJobScope.current = jobScope
  const offerScope = `${jobScope}:${canSeeOfferJobs}:${session?.user.clientAccessMode}:${session?.user.assignedClientIds?.join(',')}:${session?.user.recruitmentScopeMode}:${session?.user.recruitmentLocationIds?.join(',')}`
  const currentOfferScope = useRef(offerScope)
  currentOfferScope.current = offerScope
  const refresh = useCallback(async () => {
    const attempt = ++generation.current
    if (!session) { setItems([]); setJobFeed({ scope: jobScope, items: [] }); setOfferFeed({ scope: offerScope, items: [] }); setLoading(false); return }
    setLoading(true)
    const canConfigure = session.user.permissions.some(p => ['settings.manage', 'client.settings.manage', 'attendance.manage', 'workflow.manage', 'employees.manage'].includes(p))
    const [tasks, interviews, configuration, mailJobs, momPings, offerJobs] = await Promise.all([
      getJsonResult<PendingTask[]>('/api/workflows/tasks/pending', [], { loader: false, toast: false }),
      getJsonResult<RecruitmentInterview[]>('/api/recruitment/interviews', [], { loader: false, toast: false }),
      canConfigure ? getAttendanceGaps(session.user.clientId || undefined) : Promise.resolve(null),
      canSeeMailJobs ? getExcelPayslipMailJobs() : Promise.resolve(null),
      getRecruitmentMomPings(),
      canSeeOfferJobs ? getOfferMailJobs() : Promise.resolve(null),
    ])
    if (attempt !== generation.current) return
    const next: ActionItem[] = (tasks.data || []).map(task => ({ key: `approval:${task.id}`, title: task.stageName || 'Approval required', detail: `${task.resourceType} · ${task.resourceId}`, route: '/tasks' }))
    for (const ping of momPings.data || []) next.push({ key: `mom-ping:${ping.id}`, title: ping.title, detail: ping.detail, route: ping.route })
    const gaps = (configuration?.data || []).filter(gap => (!session.user.clientId || gap.clientId === session.user.clientId) && gap.requiredPermissions?.some(p => session.user.permissions.includes(p)))
    if (gaps.length) {
      const clients = new Set(gaps.map(gap => gap.clientId)).size
      next.push({ key: 'attendance:setup', title: 'Attendance setup needs attention', detail: clients === 1 ? `${gaps[0].clientName} · ${gaps.length} missing settings` : `${clients} clients · ${gaps.length} missing settings`, route: gaps[0].route })
    }
    for (const interview of interviews?.data || []) {
      if (interview.isCurrentUserFeedbackPending)
        next.push({ key: `feedback:${interview.id}`, title: 'Your panel feedback is pending', detail: `${interview.candidateName} · ${interview.positionTitle}`, route: `/recruitment/interviews?feedbackInterviewId=${interview.id}` })
      else if (interview.canRecordDecision)
        next.push({ key: `decision:${interview.id}`, title: 'Panel feedback complete — record decision', detail: `${interview.candidateName} · ${interview.positionTitle}`, route: `/recruitment/interviews?decisionInterviewId=${interview.id}` })
    }
    setItems(next)
    const receivedJobs = mailJobs?.data?.items
    const jobsValid = Boolean(mailJobs?.ok && validMailJobs(receivedJobs))
    if (jobsValid) setJobFeed({ scope: jobScope, items: receivedJobs! })
    else if (!canSeeMailJobs) setJobFeed({ scope: jobScope, items: [] })
    const offersValid = Boolean(offerJobs?.ok && validOfferMailJobs(offerJobs.data.items))
    if (offersValid) setOfferFeed({ scope: offerScope, items: offerJobs!.data.items })
    else if (!canSeeOfferJobs || offerJobs?.status === 403) setOfferFeed({ scope: offerScope, items: [] })
    setError(!tasks.ok || !momPings.ok || (interviews && !interviews.ok) || (configuration && !configuration.ok) || (mailJobs && !jobsValid) || (offerJobs && !offersValid) ? 'Some notifications could not be refreshed. Any displayed mail progress is the last known status; retry to check the latest.' : '')
    setLoading(false)
  }, [session?.user.id, session?.user.clientId, session?.user.permissions.join('|'), offerScope])

  const notifications: ActionItem[] = [...items, ...(canSeeMailJobs && jobFeed.scope === jobScope ? jobFeed.items : [])
    .filter(job => !job.dismissedAtUtc && (!session?.user.clientId || job.clientId === session.user.clientId))
    .map(job => ({ key: `excel-mail:${job.id}`, title: job.status === 'Completed' ? 'Excel payslip email complete' : job.status === 'Needs attention' ? 'Excel payslip email needs attention' : 'Excel payslip email in progress', detail: `${job.clientName} · ${job.month}`, route: `/reports/payroll-reports?report=excel-payslips&clientId=${job.clientId}&batchId=${encodeURIComponent(job.batchId)}&mailJobId=${encodeURIComponent(job.id)}`, mailJob: job })),
    ...(canSeeOfferJobs && offerFeed.scope === offerScope ? offerFeed.items : []).filter(job => !job.dismissedAtUtc).map(job => ({ key: `offer-mail:${job.id}`, title: job.status === 'Completed' ? 'Offer email complete' : job.status === 'Needs attention' ? 'Offer email needs attention' : 'Offer email in progress', detail: `${job.clientName} · ${job.total} offer letters`, route: `/recruitment/offers-and-pre-onboarding?clientId=${job.clientId}&mailJobId=${encodeURIComponent(job.id)}`, offerJob: job }))]

  const dismissJob = async (job: ExcelPayslipMailJob) => {
    if (!canSeeMailJobs || !job.isComplete || dismissing) return
    const requestedScope = jobScope
    setDismissing(job.id)
    const result = await dismissExcelPayslipMailJob(job.clientId, job.batchId, job.id)
    setDismissing(current => current === job.id ? '' : current)
    if (currentJobScope.current !== requestedScope) return
    if (result.ok && result.data?.ok === true) {
      setJobFeed(current => current.scope === requestedScope ? { ...current, items: current.items.filter(item => item.id !== job.id) } : current)
      window.dispatchEvent(new Event('hrms:actions-changed'))
    } else setError(result.error || 'Unable to dismiss this mail notification. Retry after refreshing its status.')
  }
  const dismissOfferJob = async (job: OfferMailJob) => {
    if (!canSeeOfferJobs || !job.isComplete || dismissing) return
    const requestedScope = offerScope
    setDismissing(job.id)
    const result = await dismissOfferMailJob(job.clientId, job.id)
    setDismissing('')
    if (currentOfferScope.current !== requestedScope) return
    if (result.ok && result.data.ok) { setOfferFeed(current => ({ ...current, items: current.items.filter(item => item.id !== job.id) })); window.dispatchEvent(new Event('hrms:actions-changed')) }
    else setError(result.error || 'Unable to dismiss the offer email notification.')
  }

  useEffect(() => {
    void refresh()
    const tick = () => { if (!document.hidden) void refresh() }
    const timer = window.setInterval(tick, 30000)
    window.addEventListener('focus', tick)
    window.addEventListener('hrms:actions-changed', tick)
    return () => { generation.current++; window.clearInterval(timer); window.removeEventListener('focus', tick); window.removeEventListener('hrms:actions-changed', tick) }
  }, [refresh])

  return <Popover trigger="click" placement="bottomRight" open={open} onOpenChange={value => { setOpen(value); if (value) void refresh() }} title={<Space><span>Notifications</span><Button size="small" type="text" aria-label="Refresh pending actions" icon={<ReloadOutlined />} onClick={() => void refresh()} /></Space>} content={
    <div style={{ width: 'min(400px, 80vw)', maxHeight: '65vh', overflowY: 'auto' }}>
      {error && <Alert type="warning" showIcon message={error} />}
      {loading && !notifications.length ? <Spin /> : <List dataSource={notifications} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No notifications" /> }} renderItem={item => <List.Item style={{ display: 'block' }}>
        <Button type="text" style={{ height: 'auto', width: '100%', whiteSpace: 'normal', textAlign: 'left', display: 'block', overflowWrap: 'anywhere' }} onClick={() => { setOpen(false); navigate(item.route) }}>
          <Typography.Text strong>{item.title}</Typography.Text><br /><Typography.Text type="secondary">{item.detail}</Typography.Text>
          {item.mailJob && <MailJobProgress job={item.mailJob} />}
          {item.offerJob && <MailJobProgress job={item.offerJob} documentLabel="letters" />}
        </Button>
        {item.mailJob?.isComplete && <Button type="link" size="small" loading={dismissing === item.mailJob.id} disabled={Boolean(dismissing)} aria-label={`Dismiss ${item.mailJob.month} Excel payslip email notification`} onClick={() => void dismissJob(item.mailJob!)}>Dismiss</Button>}
        {item.offerJob?.isComplete && <Button type="link" size="small" loading={dismissing === item.offerJob.id} disabled={Boolean(dismissing)} aria-label="Dismiss offer email notification" onClick={() => void dismissOfferJob(item.offerJob!)}>Dismiss</Button>}
      </List.Item>} />}
    </div>
  }><Badge count={notifications.length} overflowCount={99}><Button className="topbar-icon-btn" icon={<BellOutlined />} aria-label="Notifications" /></Badge></Popover>
}

function validMailJobs(value: unknown): value is ExcelPayslipMailJob[] {
  return Array.isArray(value) && value.every(job => job && typeof job === 'object'
    && ['id', 'batchId'].every(key => typeof job[key] === 'string' && job[key].trim())
    && Number.isSafeInteger(job.clientId) && job.clientId > 0 && typeof job.clientName === 'string'
    && typeof job.month === 'string' && /^\d{4}-(0[1-9]|1[0-2])$/.test(job.month)
    && ['Queued', 'Processing', 'Completed', 'Needs attention'].includes(job.status)
    && ['total', 'completed', 'sent', 'queued', 'failed', 'missingEmail', 'skipped', 'unknown'].every(key => Number.isSafeInteger(job[key]) && job[key] >= 0 && job[key] <= job.total)
    && Number.isSafeInteger(job.percentComplete) && job.percentComplete >= 0 && job.percentComplete <= 100
    && typeof job.isComplete === 'boolean'
    && (job.dismissedAtUtc === null || (typeof job.dismissedAtUtc === 'string' && Number.isFinite(Date.parse(job.dismissedAtUtc)))))
    && new Set(value.map(job => job.id)).size === value.length
}

function MailJobProgress({ job, documentLabel = 'slips' }: { job: BulkMailJob; documentLabel?: string }) {
  const detail = [`Emails SMTP accepted ${job.acceptedEmails ?? '?'} / ${job.totalEmails ?? '?'}`, `Sent copies saved ${job.sentCopySavedEmails ?? '?'}`, `${job.total} ${documentLabel}`, ...(job.mode === 'Combined' ? ['Coordinator copy'] : []), `${documentLabel === 'slips' ? 'Slips' : 'Letters'} queued ${job.queued}`, ...(job.failed ? [`Failed ${job.failed}`] : []), ...(job.missingEmail ? [`Missing email ${job.missingEmail}`] : []), ...(job.notReleased ? [`Not released ${job.notReleased}`] : []), ...(job.sentCopyUnverifiedEmails ? [`Sent copy unverified ${job.sentCopyUnverifiedEmails}`] : []), ...(job.unknown ? [`Unknown ${job.unknown}`] : [])].join(' · ')
  return <div style={{ marginTop: 6 }}>
    <Tag color={job.status === 'Needs attention' ? 'orange' : job.isComplete ? 'green' : 'blue'}>{job.status}</Tag>
    <Typography.Text type="secondary">{job.completed} / {job.total} processed · {job.percentComplete}%</Typography.Text>
    <Progress percent={job.percentComplete} size="small" showInfo={false} status={job.status === 'Needs attention' ? 'exception' : job.isComplete ? 'success' : 'active'} aria-label={`${job.percentComplete}% processed`} />
    <Typography.Text type="secondary">{detail}</Typography.Text>
    <div><Typography.Text type="secondary">View batch progress</Typography.Text></div>
  </div>
}
