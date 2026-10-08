import { Alert, Button, Progress, Select, Space, Tag } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import type { ExcelPayslipMailJob } from '../services/excelPayslipService'

export default function ExcelPayslipMailProgress({ jobs, selectedId, onSelect, loading, error, onRefresh }: {
  jobs: ExcelPayslipMailJob[]; selectedId: string; onSelect: (id: string) => void; loading: boolean; error: string; onRefresh: () => void
}) {
  const job = jobs.find(item => item.id === selectedId) || jobs[0]
  if (!job && !error) return null
  const issues = job?.status === 'Needs attention'
  return <div className="excel-payslip-mail-progress" aria-label="Background email progress">
    {error && <Alert type="warning" showIcon message={error} />}
    <div className="excel-payslip-summary"><Space wrap><strong title={job?.message}>Email progress</strong>{job && <Tag color={issues ? 'orange' : job.isComplete ? 'green' : 'blue'}>{job.status}</Tag>}</Space><Space wrap>
      {jobs.length > 1 && <Select aria-label="Email job history" value={job?.id} onChange={onSelect} options={jobs.map(item => ({ value: item.id, label: `${new Date(item.createdAtUtc).toLocaleString()} · ${item.total} payslips` }))} style={{ width: 245, maxWidth: '100%' }} />}
      <Button size="small" icon={<ReloadOutlined />} loading={loading} onClick={onRefresh}>Refresh progress</Button>
    </Space></div>
    {job && <><Progress percent={job.percentComplete} status={job.isComplete ? issues ? 'exception' : 'success' : 'active'} size="small" showInfo={false} />
      <Space wrap size={[6, 4]}><Tag color="green">Sent {job.sent}</Tag><Tag color="blue">Queued {job.queued}</Tag><Tag color={job.failed ? 'red' : undefined}>Failed {job.failed}</Tag><Tag color={job.missingEmail ? 'orange' : undefined}>Email missing / invalid {job.missingEmail}</Tag>{job.skipped > 0 && <Tag>Already handled {job.skipped}</Tag>}{job.unknown > 0 && <Tag color="orange">Needs review {job.unknown}</Tag>}<span className="excel-payslip-muted">{job.completed} of {job.total} processed</span></Space>
      {issues && <div className="excel-payslip-muted">{job.message || 'Review the recipients needing attention.'}</div>}
    </>}
  </div>
}
