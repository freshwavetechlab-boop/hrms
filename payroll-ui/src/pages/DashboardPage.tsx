import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertOutlined, CalendarOutlined, CheckCircleOutlined, ClockCircleOutlined, TeamOutlined, WalletOutlined } from '@ant-design/icons'
import type { DashboardSnapshot } from '../types/payroll'
import { getDashboard } from '../services/dashboardService'

const money = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 0, style: 'currency', currency: 'INR' })
const count = new Intl.NumberFormat('en-IN')

function formatMonth(value: string) {
  if (!value) return 'Current month'
  const [year, month] = value.split('-').map(Number)
  return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric' }).format(new Date(year, month - 1, 1))
}

function formatDate(value: string) {
  if (!value) return ''
  return new Intl.DateTimeFormat('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }).format(new Date(value))
}

export default function DashboardPage() {
  const [clientId, setClientId] = useState(0)
  const [dashboard, setDashboard] = useState<DashboardSnapshot | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let active = true
    setLoading(true)
    void getDashboard(clientId).then(data => {
      if (!active) return
      setDashboard(data)
      if (data.selectedClientId !== clientId) setClientId(data.selectedClientId)
    }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [clientId])

  const metrics = dashboard?.metrics
  const sections = dashboard?.sections ?? []
  const canSee = (section: string) => sections.includes(section)
  const attendanceReady = useMemo(() => {
    if (!canSee('attendance') || !metrics?.activeEmployees) return 0
    return Math.round((metrics.attendanceRecorded / metrics.activeEmployees) * 100)
  }, [metrics, sections])

  const clientName = clientId === 0 ? 'All clients' : dashboard?.clients.find(client => client.id === clientId)?.name ?? 'Selected client'

  return <section className="dashboard-page">
    <header className="dashboard-header">
      <div>
        <span className="eyebrow purple">HRMS Dashboard</span>
        <h3>{clientName}</h3>
        <p>{formatMonth(dashboard?.month ?? '')} role-based workspace summary.</p>
      </div>
      <label>
        <span>Client</span>
        <select value={clientId} onChange={event => setClientId(Number(event.target.value))} disabled={loading}>
          <option value={0}>All clients</option>
          {(dashboard?.clients ?? []).map(client => <option value={client.id} key={client.id}>{client.name}</option>)}
        </select>
      </label>
    </header>

    <div className="dashboard-kpis">
      {canSee('workforce') && <article><TeamOutlined /><span>Active employees</span><strong>{count.format(metrics?.activeEmployees ?? 0)}</strong><small>{count.format(metrics?.portalUsers ?? 0)} ESS enabled</small></article>}
      {canSee('payroll') && <article><WalletOutlined /><span>Net payroll</span><strong>{money.format(metrics?.currentMonthNetPay ?? 0)}</strong><small>{count.format(metrics?.currentMonthPayRuns ?? 0)} run(s) this month</small></article>}
      {canSee('attendance') && <article><CalendarOutlined /><span>Attendance ready</span><strong>{attendanceReady}%</strong><small>{count.format(metrics?.attendanceMissing ?? 0)} missing, {count.format(metrics?.attendanceIssues ?? 0)} issue(s)</small></article>}
      {canSee('approvals') && <article><ClockCircleOutlined /><span>Pending approvals</span><strong>{count.format(metrics?.pendingTasks ?? 0)}</strong><small>{count.format(metrics?.pendingLeaveRequests ?? 0)} leave request(s)</small></article>}
    </div>

    {!loading && sections.length === 0 && <article className="card dashboard-card">
      <header><i><AlertOutlined /></i><div><h3>No Dashboard Sections Assigned</h3><p>Ask a security administrator to assign Dashboard permissions to your role.</p></div></header>
    </article>}

    <div className="dashboard-grid">
      {canSee('payroll') && <article className="card dashboard-card">
        <header><i><CheckCircleOutlined /></i><div><h3>Payroll Status</h3><p>Current month run health by status.</p></div></header>
        <div className="dashboard-status-list">
          {(dashboard?.payRunStatuses.length ? dashboard.payRunStatuses : [{ status: 'No runs yet', count: 0, netPay: 0 }]).map(item => <div key={item.status}>
            <span>{item.status}</span>
            <strong>{count.format(item.count)}</strong>
            <small>{money.format(item.netPay)}</small>
          </div>)}
        </div>
      </article>}

      {(canSee('approvals') || canSee('attendance') || canSee('payroll')) && <article className="card dashboard-card">
        <header><i><AlertOutlined /></i><div><h3>Action Queue</h3><p>Items that can block HR and payroll closure.</p></div></header>
        <div className="dashboard-action-list">
          {canSee('approvals') && <Link to="/tasks"><strong>{count.format(metrics?.pendingTasks ?? 0)}</strong><span>My workflow tasks</span></Link>}
          {canSee('attendance') && <Link to="/attendance"><strong>{count.format(metrics?.attendanceIssues ?? 0)}</strong><span>Attendance exceptions</span></Link>}
          {canSee('payroll') && <Link to="/payroll/regular"><strong>{count.format(metrics?.payrollExceptions ?? 0)}</strong><span>Blocking payroll validations</span></Link>}
        </div>
      </article>}
    </div>

    {canSee('payroll') && <article className="card dashboard-card dashboard-recent">
      <header><i><WalletOutlined /></i><div><h3>Recent Pay Runs</h3><p>Latest payroll activity for the selected client view.</p></div></header>
      <div className="dashboard-table">
        <table>
          <thead><tr><th>Client</th><th>Period</th><th>Run</th><th>Status</th><th>Employees</th><th>Net Pay</th><th>Updated</th></tr></thead>
          <tbody>
            {(dashboard?.recentPayRuns ?? []).map(run => <tr key={run.id}>
              <td>{run.clientName || '-'}</td>
              <td>{run.payPeriod}</td>
              <td>{run.runName || run.runType}</td>
              <td><span className={`status-chip ${run.status.toLowerCase().replace(/\s+/g, '-')}`}>{run.status}</span></td>
              <td>{count.format(run.employeeCount)}</td>
              <td>{money.format(run.netPay)}</td>
              <td>{formatDate(run.updatedAt)}</td>
            </tr>)}
            {!dashboard?.recentPayRuns.length && <tr><td colSpan={7}>No payroll activity found for this view.</td></tr>}
          </tbody>
        </table>
      </div>
    </article>}
  </section>
}
