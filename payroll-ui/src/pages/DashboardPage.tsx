import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertOutlined, CalendarOutlined, CheckCircleOutlined, ClockCircleOutlined, TeamOutlined, WalletOutlined } from '@ant-design/icons'
import type { DashboardChartPoint, DashboardPayrollTrendPoint, DashboardSnapshot } from '../types/payroll'
import { getDashboard } from '../services/dashboardService'

export type DashboardView = 'overview' | 'workforce' | 'payroll' | 'attendance' | 'approvals'

const money = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 0, style: 'currency', currency: 'INR' })
const count = new Intl.NumberFormat('en-IN')
const dashboardTitles: Record<DashboardView, string> = {
  overview: 'HRMS Dashboard',
  workforce: 'Workforce Dashboard',
  payroll: 'Payroll Dashboard',
  attendance: 'Attendance Dashboard',
  approvals: 'Approvals Dashboard'
}

function formatMonth(value: string) {
  if (!value) return 'Current month'
  const [year, month] = value.split('-').map(Number)
  return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric' }).format(new Date(year, month - 1, 1))
}

function formatDate(value: string) {
  if (!value) return ''
  return new Intl.DateTimeFormat('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }).format(new Date(value))
}

function shortMonth(value: string) {
  if (!value) return ''
  const [year, month] = value.split('-').map(Number)
  return new Intl.DateTimeFormat('en-IN', { month: 'short' }).format(new Date(year, month - 1, 1))
}

function EmptyChart() {
  return <div className="dashboard-empty-chart">No chart data available.</div>
}

function BarChart({ data, valueFormat = value => count.format(value) }: { data: DashboardChartPoint[]; valueFormat?: (value: number) => string }) {
  const rows = data.filter(item => Number(item.value) > 0)
  const max = Math.max(...rows.map(item => Number(item.value)), 0)
  if (!rows.length) return <EmptyChart />
  return <div className="dashboard-bar-chart">
    {rows.map(item => {
      const value = Number(item.value)
      return <div className="dashboard-bar-row" key={item.label}>
        <span title={item.label}>{item.label}</span>
        <b>{valueFormat(value)}</b>
        <i><em style={{ width: `${max ? Math.max((value / max) * 100, 4) : 0}%` }} /></i>
      </div>
    })}
  </div>
}

function DonutChart({ data }: { data: DashboardChartPoint[] }) {
  const palette = ['#6546e8', '#22c55e', '#f97316', '#ef4444', '#0ea5e9', '#a855f7']
  const rows = data.filter(item => Number(item.value) > 0)
  const total = rows.reduce((sum, item) => sum + Number(item.value), 0)
  if (!rows.length || total <= 0) return <EmptyChart />
  let cursor = 0
  const gradient = rows.map((item, index) => {
    const start = cursor
    cursor += (Number(item.value) / total) * 100
    return `${palette[index % palette.length]} ${start}% ${cursor}%`
  }).join(', ')
  return <div className="dashboard-donut-wrap">
    <div className="dashboard-donut" style={{ background: `conic-gradient(${gradient})` }}><strong>{count.format(total)}</strong><small>Total</small></div>
    <div className="dashboard-donut-legend">
      {rows.map((item, index) => <span key={item.label}><i style={{ background: palette[index % palette.length] }} />{item.label}<b>{count.format(Number(item.value))}</b></span>)}
    </div>
  </div>
}

function PayrollTrendChart({ data }: { data: DashboardPayrollTrendPoint[] }) {
  const rows = data.filter(item => Number(item.netPay) > 0 || Number(item.payrollCost) > 0)
  if (!rows.length) return <EmptyChart />
  const max = Math.max(...rows.map(item => Math.max(Number(item.netPay), Number(item.payrollCost))), 1)
  const width = 520
  const height = 190
  const points = rows.map((item, index) => {
    const x = rows.length === 1 ? width / 2 : 28 + (index * (width - 56)) / (rows.length - 1)
    const y = height - 34 - (Number(item.netPay) / max) * (height - 72)
    return { x, y, item }
  })
  const path = points.map((point, index) => `${index ? 'L' : 'M'}${point.x},${point.y}`).join(' ')
  return <div className="dashboard-line-chart">
    <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Payroll net pay trend">
      <path className="dashboard-line-grid" d={`M28 ${height - 34}H${width - 28}M28 36H${width - 28}`} />
      <path className="dashboard-line-fill" d={`${path} L${points.at(-1)?.x ?? 28},${height - 34} L${points[0]?.x ?? 28},${height - 34} Z`} />
      <path className="dashboard-line-stroke" d={path} />
      {points.map(point => <g key={point.item.month}>
        <circle cx={point.x} cy={point.y} r="4.5" />
        <text x={point.x} y={height - 12} textAnchor="middle">{shortMonth(point.item.month)}</text>
      </g>)}
    </svg>
    <div className="dashboard-trend-caption">
      <span>Latest net pay</span><strong>{money.format(rows.at(-1)?.netPay ?? 0)}</strong>
      <span>Peak net pay</span><strong>{money.format(Math.max(...rows.map(item => item.netPay)))}</strong>
    </div>
  </div>
}

function costBreakupToChart(dashboard: DashboardSnapshot | null): DashboardChartPoint[] {
  const item = dashboard?.payrollCostBreakup
  if (!item) return []
  return [
    { label: 'Gross earnings', value: item.grossEarnings },
    { label: 'Statutory deductions', value: item.statutoryDeductions },
    { label: 'Other deductions', value: item.otherDeductions },
    { label: 'Net pay', value: item.netPay }
  ]
}

export default function DashboardPage({ view = 'overview' }: { view?: DashboardView }) {
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
  const canSee = (section: string) => sections.includes(section) && (view === 'overview' || view === section)
  const attendanceReady = useMemo(() => {
    if (!canSee('attendance') || !metrics?.activeEmployees) return 0
    return Math.round((metrics.attendanceRecorded / metrics.activeEmployees) * 100)
  }, [metrics, sections, view])

  const clientName = clientId === 0 ? 'All clients' : dashboard?.clients.find(client => client.id === clientId)?.name ?? 'Selected client'
  const recentTotals = ['Approved', 'Processing', 'Pending Approval'].map(status => dashboard?.payRunStatuses.find(item => item.status === status) ?? { status, count: 0, netPay: 0 })

  return <section className="dashboard-page">
    <header className="dashboard-header">
      <div>
        <span className="eyebrow purple">{dashboardTitles[view]}</span>
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

    <div className="dashboard-chart-grid">
      {canSee('workforce') && <article className="card dashboard-card dashboard-chart-card">
        <header><i><TeamOutlined /></i><div><h3>Workforce Distribution</h3><p>Headcount concentration by department and location.</p></div></header>
        <div className="dashboard-two-chart">
          <section><h4>By department</h4><BarChart data={dashboard?.departmentHeadcount ?? []} /></section>
          <section><h4>By location</h4><BarChart data={dashboard?.locationHeadcount ?? []} /></section>
        </div>
      </article>}

      {canSee('payroll') && <article className="card dashboard-card dashboard-chart-card">
        <header><i><WalletOutlined /></i><div><h3>Payroll Cost Trend</h3><p>Six latest payroll periods by net pay.</p></div></header>
        <PayrollTrendChart data={dashboard?.payrollTrend ?? []} />
      </article>}

      {canSee('attendance') && <article className="card dashboard-card dashboard-chart-card">
        <header><i><CalendarOutlined /></i><div><h3>Attendance Control View</h3><p>Current month recording readiness and LOP exposure.</p></div></header>
        <div className="dashboard-two-chart compact">
          <section><h4>Readiness</h4><DonutChart data={dashboard?.attendanceMix ?? []} /></section>
          <section><h4>Payability</h4><BarChart data={dashboard?.attendancePayability ?? []} valueFormat={value => count.format(value)} /></section>
        </div>
      </article>}

      {canSee('approvals') && <article className="card dashboard-card dashboard-chart-card">
        <header><i><ClockCircleOutlined /></i><div><h3>Approval Workload</h3><p>Pending queue by stage and recent action mix.</p></div></header>
        <div className="dashboard-two-chart">
          <section><h4>Pending by stage</h4><BarChart data={dashboard?.approvalStageBreakup ?? []} /></section>
          <section><h4>Actioned outcomes</h4><DonutChart data={dashboard?.approvalActionMix ?? []} /></section>
        </div>
      </article>}
    </div>

    {view !== 'overview' && <div className="dashboard-deep-grid">
      {view === 'workforce' && canSee('workforce') && <>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><TeamOutlined /></i><div><h3>ESS Adoption</h3><p>Portal access enablement across active employees.</p></div></header>
          <DonutChart data={dashboard?.essAdoption ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><TeamOutlined /></i><div><h3>Gender Mix</h3><p>Employee master distribution by gender field.</p></div></header>
          <DonutChart data={dashboard?.genderHeadcount ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><TeamOutlined /></i><div><h3>Designation Concentration</h3><p>Largest employee groups by designation.</p></div></header>
          <BarChart data={dashboard?.designationHeadcount ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><TeamOutlined /></i><div><h3>Grade Distribution</h3><p>Grade-wise workforce segmentation.</p></div></header>
          <BarChart data={dashboard?.gradeHeadcount ?? []} />
        </article>
      </>}

      {view === 'payroll' && canSee('payroll') && <>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><WalletOutlined /></i><div><h3>Run Status Mix</h3><p>Current month payrun control status.</p></div></header>
          <DonutChart data={(dashboard?.payRunStatuses ?? []).map(item => ({ label: item.status, value: item.count }))} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><WalletOutlined /></i><div><h3>Employee Payment Status</h3><p>Payment progress inside current month payruns.</p></div></header>
          <DonutChart data={dashboard?.payrollPaymentStatus ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><WalletOutlined /></i><div><h3>Payroll Amount Composition</h3><p>Earnings, deductions and net pay comparison.</p></div></header>
          <BarChart data={costBreakupToChart(dashboard)} valueFormat={value => money.format(value)} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><WalletOutlined /></i><div><h3>Run Type Mix</h3><p>Regular, off-cycle and other payroll runs.</p></div></header>
          <DonutChart data={dashboard?.payrollRunType ?? []} />
        </article>
      </>}

      {view === 'attendance' && canSee('attendance') && <>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><CalendarOutlined /></i><div><h3>Daily Attendance Status</h3><p>Daily attendance event mix for the selected month.</p></div></header>
          <DonutChart data={dashboard?.attendanceDailyStatus ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><CalendarOutlined /></i><div><h3>Attendance Source</h3><p>How monthly attendance records were populated.</p></div></header>
          <BarChart data={dashboard?.attendanceSourceType ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><CalendarOutlined /></i><div><h3>Readiness Exceptions</h3><p>Missing attendance and blocking value issues.</p></div></header>
          <BarChart data={[{ label: 'Missing employees', value: metrics?.attendanceMissing ?? 0 }, { label: 'Check value issues', value: metrics?.attendanceIssues ?? 0 }]} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><CalendarOutlined /></i><div><h3>Payable Exposure</h3><p>Present and LOP day totals from attendance review.</p></div></header>
          <BarChart data={dashboard?.attendancePayability ?? []} />
        </article>
      </>}

      {view === 'approvals' && canSee('approvals') && <>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><ClockCircleOutlined /></i><div><h3>Pending by Resource</h3><p>Request type split of your pending approval queue.</p></div></header>
          <DonutChart data={dashboard?.approvalResourceBreakup ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><ClockCircleOutlined /></i><div><h3>Approval Aging</h3><p>How long tasks have waited for action.</p></div></header>
          <BarChart data={dashboard?.approvalAging ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><ClockCircleOutlined /></i><div><h3>Stage Load</h3><p>Current pending tasks grouped by approval stage.</p></div></header>
          <BarChart data={dashboard?.approvalStageBreakup ?? []} />
        </article>
        <article className="card dashboard-card dashboard-chart-card">
          <header><i><ClockCircleOutlined /></i><div><h3>Action History Mix</h3><p>Your recently completed task outcomes.</p></div></header>
          <DonutChart data={dashboard?.approvalActionMix ?? []} />
        </article>
      </>}
    </div>}

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
      <div className="dashboard-status-list dashboard-recent-totals">
        {recentTotals.map(item => <div key={item.status}><span>{item.status} Pay Runs</span><strong>{count.format(item.count)}</strong><small>{money.format(item.netPay)}</small></div>)}
      </div>
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
