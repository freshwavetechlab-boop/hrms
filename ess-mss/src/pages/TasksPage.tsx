import { useEffect, useState } from 'react'
import type { Task, User } from '../types'
import { essApi } from '../services/essApi'

export function TasksPage({ user }: { user: User }) {
  const [rows, setRows] = useState<Task[]>([])
  useEffect(() => { void essApi.tasks().then(setRows) }, [user.email])
  return <section className="leave-workspace"><div className="feature-heading"><span className="eyebrow">My tasks</span><h3>Approvals assigned to you</h3><p>Complete approvals here. Your action is recorded in the shared workflow history.</p></div><div className="task-list">{rows.map(task => <article key={task.id}><div><b>{task.resourceType}</b><span>{task.resourceId} / {task.stageName}</span></div><small>{new Date(task.createdAt).toLocaleString('en-IN')}</small></article>)}{!rows.length && <div className="empty-work"><b>No approval tasks are assigned to you.</b><span>Tasks from Leave, Payroll, and other modules will appear here when assigned.</span></div>}</div></section>
}
