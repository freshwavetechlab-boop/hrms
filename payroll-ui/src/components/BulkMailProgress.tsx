import { Alert, Button, Progress, Select, Space, Tag } from 'antd'
import { CloseOutlined, DownOutlined, ReloadOutlined, UpOutlined } from '@ant-design/icons'
import './BulkMailProgress.css'

export type BulkMailJob = {
  id: string; createdAtUtc: string; status: string; isComplete: boolean; percentComplete: number; message: string
  total: number; completed: number; queued: number; failed: number; missingEmail: number; skipped: number; unknown: number
  totalEmails?: number; acceptedEmails?: number; sentCopySavedEmails?: number; sentCopyUnverifiedEmails?: number; deliveredEmails?: number
  mode?: string; recipient?: string; notReleased?: number
}
export type BulkMailProgressProps = {
  jobs: BulkMailJob[]; selectedId: string; onSelect: (id: string) => void; loading: boolean; error: string; onRefresh: () => void
  expanded?: boolean; onToggle?: () => void; onHide?: () => void; documentLabel?: string; documentPlural?: string
}

export default function BulkMailProgress({ jobs, selectedId, onSelect, loading, error, onRefresh, expanded = true, onToggle, onHide, documentLabel = 'slips', documentPlural = 'payslips' }: BulkMailProgressProps) {
  const job = jobs.find(item => item.id === selectedId) || jobs[0]
  if (!job && !error) return null
  const issues = job?.status === 'Needs attention'
  const accepted = job?.acceptedEmails
  const label = documentLabel[0].toUpperCase() + documentLabel.slice(1)
  return <div className={`bulk-mail-progress excel-payslip-mail-progress${expanded ? '' : ' is-compact'}`} aria-label="Background email progress">
    {error && <Alert type="warning" showIcon message={error} />}
    <div className="excel-payslip-summary"><Space wrap><strong title={job?.message}>Email progress</strong>{job && <Tag color={issues ? 'orange' : job.isComplete ? 'green' : 'blue'}>{job.isComplete ? issues ? 'Needs attention' : 'Processing finished' : job.status}</Tag>}{job && !expanded && <span className="excel-payslip-muted excel-payslip-progress-counts">{accepted == null ? 'Email count not verified' : `${accepted} / ${job.totalEmails} emails SMTP accepted`} · {job.total} {documentLabel}{job.mode === 'Combined' ? ' · Coordinator copy' : ''}</span>}</Space><Space wrap size={4}>
      {expanded && jobs.length > 1 && <Select aria-label="Email job history" value={job?.id} onChange={onSelect} options={jobs.map(item => ({ value: item.id, label: `${new Date(item.createdAtUtc).toLocaleString()} · ${item.total} ${documentPlural}` }))} style={{ width: 245, maxWidth: '100%' }} />}
      <Button size="small" icon={<ReloadOutlined />} aria-label="Refresh email progress" title="Refresh email progress" loading={loading} onClick={onRefresh}>{expanded && 'Refresh progress'}</Button>
      {onToggle && <Button size="small" aria-label={expanded ? 'Collapse email progress' : 'Expand email progress'} title={expanded ? 'Collapse email progress' : 'Expand email progress'} icon={expanded ? <UpOutlined /> : <DownOutlined />} onClick={onToggle} />}
      {onHide && <Button size="small" aria-label="Hide email progress" title="Hide email progress" icon={<CloseOutlined />} onClick={onHide} />}
    </Space></div>
    {job && expanded && <><Progress percent={job.percentComplete} status={job.isComplete ? issues ? 'exception' : 'success' : 'active'} size="small" showInfo={false} />
      <Space wrap size={[6, 4]}><Tag color="blue">Emails SMTP accepted: {accepted ?? 'Unverified'} / {job.totalEmails ?? '?'}</Tag><Tag color="green">Saved in Sent folder: {job.sentCopySavedEmails ?? 'Unverified'}</Tag>{!!job.sentCopyUnverifiedEmails && <Tag color="orange">Sent copy not verified: {job.sentCopyUnverifiedEmails}</Tag>}<Tag>Recipient delivery confirmed: {job.deliveredEmails ?? 0}</Tag><Tag color="blue">{label} queued: {job.queued}</Tag><Tag color={job.failed ? 'red' : undefined}>{label} failed: {job.failed}</Tag><Tag color={job.missingEmail ? 'orange' : undefined}>Email missing / invalid: {job.missingEmail}</Tag>{!!job.notReleased && <Tag color="orange">Not released / updated: {job.notReleased}</Tag>}{job.skipped > 0 && <Tag>Previously handled: {job.skipped}</Tag>}{job.unknown > 0 && <Tag color="orange">Delivery not verified: {job.unknown}</Tag>}<span className="excel-payslip-muted">{job.completed} of {job.total} {documentLabel} processed</span></Space>
      {job.mode === 'Combined' && <Alert type="info" showIcon message={`Coordinator copy${job.recipient ? ` to ${job.recipient}` : ''}: ${job.total} ${documentLabel} in ${job.totalEmails ?? '?'} email(s). This does not mark employee emails as sent.`} />}
      {issues && <div className="excel-payslip-muted">{job.message || 'Review the recipients needing attention.'}</div>}
    </>}
  </div>
}
