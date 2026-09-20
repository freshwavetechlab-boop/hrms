import { useEffect, useState } from 'react'
import { Alert, Button, Card, Segmented, Space, Tag } from 'antd'
import { useNavigate } from 'react-router-dom'
import DataTable from './DataTable'
import { getJsonResult } from '../services/apiClient'
import { internalInterviewRoot } from '../services/internalInterviewService'

type Row = { interviewId: number; candidateName: string; positionTitle: string; roundCode: string; scheduledStartUtc: string; status: string; mode: string; mediaState: string; mediaError: string }
export default function InternalInterviewDashboard() {
  const [rows, setRows] = useState<Row[]>([])
  const [error, setError] = useState('')
  const [filter, setFilter] = useState('Upcoming')
  const navigate = useNavigate()
  useEffect(() => {
    let active = true, running = false
    const refresh = async () => { if (running) return; running = true; try { const result = await getJsonResult(`${internalInterviewRoot}/dashboard`, [] as Row[], { loader: false, toast: false }); if (active) { if (result.ok) { setRows(result.data); setError('') } else setError(result.error) } } finally { running = false } }
    void refresh(); const timer = window.setInterval(() => void refresh(), 10000)
    return () => { active = false; window.clearInterval(timer) }
  }, [])
  const matches = (row: Row, category: string) => category === 'Upcoming' ? ['Scheduled', 'Waiting'].includes(row.status) : category === 'Live' ? row.status === 'Live' : ['Completed', 'Cancelled', 'No Show'].includes(row.status)
  return <Card title="Internal interview sessions" size="small" extra={<Tag>Human decisions remain in Interview Tracker</Tag>}>
    {error && <Alert type="warning" showIcon message={error} />}
    <Space wrap><Segmented value={filter} onChange={value => { if (typeof value === 'string') setFilter(value) }} options={['Upcoming', 'Live', 'Completed'].map(value => ({ value, label: `${value} (${rows.filter(row => matches(row, value)).length})` }))} /></Space>
    <DataTable rows={rows.filter(row => matches(row, filter))} getRowId={row => row.interviewId} exportFileName="internal-interview-sessions" pageSizeOptions={[5, 10, 20]} columns={[
      { label: 'Candidate / position', key: 'candidateName', wrap: true, render: row => <><b>{row.candidateName}</b><div>{row.positionTitle} · {row.roundCode}</div></> },
      { label: 'Scheduled', key: 'scheduledStartUtc', render: row => new Date(/Z$/.test(row.scheduledStartUtc) ? row.scheduledStartUtc : row.scheduledStartUtc + 'Z').toLocaleString() },
      { label: 'Mode', key: 'mode' }, { label: 'Session status', key: 'status', render: row => <><Tag color={row.status === 'Live' ? 'green' : 'blue'}>{row.status}</Tag>{row.mediaError && <div>Media needs attention</div>}</> },
    ]} actions={row => <Button onClick={() => navigate(`/recruitment/interview-session/${row.interviewId}`)}>Open session</Button>} />
  </Card>
}
