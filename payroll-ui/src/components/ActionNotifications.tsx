import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Badge, Button, Empty, List, Popover, Space, Spin, Typography } from 'antd'
import { BellOutlined, ReloadOutlined } from '@ant-design/icons'
import { getJsonResult } from '../services/apiClient'
import { useAuthSession } from './AuthGate'
import type { RecruitmentInterview } from '../types/payroll'

type ActionItem = { key: string; title: string; detail: string; route: string }
type PendingTask = { id: number; stageName: string; resourceType: string; resourceId: string }

// Add future action providers here using authorized, recipient-scoped APIs.
// Entries disappear when the source task is completed; these are not fake unread messages.
export default function ActionNotifications() {
  const session = useAuthSession()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [items, setItems] = useState<ActionItem[]>([])
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const generation = useRef(0)
  const refresh = useCallback(async () => {
    const attempt = ++generation.current
    if (!session) { setItems([]); return }
    setLoading(true)
    const [tasks, interviews] = await Promise.all([
      getJsonResult<PendingTask[]>('/api/workflows/tasks/pending', [], { loader: false, toast: false }),
      getJsonResult<RecruitmentInterview[]>('/api/recruitment/interviews', [], { loader: false, toast: false }),
    ])
    if (attempt !== generation.current) return
    const next: ActionItem[] = (tasks.data || []).map(task => ({ key: `approval:${task.id}`, title: task.stageName || 'Approval required', detail: `${task.resourceType} · ${task.resourceId}`, route: '/tasks' }))
    for (const interview of interviews?.data || []) {
      if (interview.isCurrentUserFeedbackPending)
        next.push({ key: `feedback:${interview.id}`, title: 'Your panel feedback is pending', detail: `${interview.candidateName} · ${interview.positionTitle}`, route: `/recruitment/interviews?feedbackInterviewId=${interview.id}` })
      else if (interview.canRecordDecision)
        next.push({ key: `decision:${interview.id}`, title: 'Panel feedback complete — record decision', detail: `${interview.candidateName} · ${interview.positionTitle}`, route: `/recruitment/interviews?decisionInterviewId=${interview.id}` })
    }
    setItems(next)
    setError(!tasks.ok || (interviews && !interviews.ok) ? 'Some pending actions could not be refreshed. Retry to check the latest status.' : '')
    setLoading(false)
  }, [session?.user.id, session?.user.clientId, session?.user.permissions.join('|')])

  useEffect(() => {
    void refresh()
    const tick = () => { if (!document.hidden) void refresh() }
    const timer = window.setInterval(tick, 30000)
    window.addEventListener('focus', tick)
    window.addEventListener('hrms:actions-changed', tick)
    return () => { generation.current++; window.clearInterval(timer); window.removeEventListener('focus', tick); window.removeEventListener('hrms:actions-changed', tick) }
  }, [refresh])

  return <Popover trigger="click" placement="bottomRight" open={open} onOpenChange={value => { setOpen(value); if (value) void refresh() }} title={<Space><span>Pending actions</span><Button size="small" type="text" aria-label="Refresh pending actions" icon={<ReloadOutlined />} onClick={() => void refresh()} /></Space>} content={
    <div style={{ width: 'min(400px, 80vw)', maxHeight: '65vh', overflowY: 'auto' }}>
      {error && <Alert type="warning" showIcon message={error} />}
      {loading && !items.length ? <Spin /> : <List dataSource={items} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No pending actions" /> }} renderItem={item => <List.Item><Button type="text" style={{ height: 'auto', width: '100%', whiteSpace: 'normal', textAlign: 'left' }} onClick={() => { setOpen(false); navigate(item.route) }}><Typography.Text strong>{item.title}</Typography.Text><br /><Typography.Text type="secondary">{item.detail}</Typography.Text></Button></List.Item>} />}
    </div>
  }><Badge count={items.length} overflowCount={99}><Button className="topbar-icon-btn" icon={<BellOutlined />} aria-label="Notifications" /></Badge></Popover>
}
