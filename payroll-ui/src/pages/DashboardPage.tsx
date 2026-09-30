import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { CalendarOutlined, CheckCircleOutlined, ClockCircleOutlined, ReloadOutlined, TeamOutlined, WalletOutlined } from '@ant-design/icons'
import { Button, Empty, Space, Tag } from 'antd'
import type { DashboardChartPoint, DashboardSnapshot } from '../types/payroll'
import { getDashboard } from '../services/dashboardService'
import { workforcePath, type WorkforceField } from '../utils/employeeWorkforce'
import { useSessionPreference } from '../hooks/useRecruitmentPreferences'
import { useAuthSession } from '../components/AuthGate'
import SearchSelect from '../components/SearchSelect'
import DataTable from '../components/DataTable'
import { PageHeaderPortal } from '../components/layout/AppPageHeader'
import {
  DashboardActionQueue, DashboardChartCard, DashboardChartGrid, DashboardEngine,
  DashboardKpiGrid, DashboardSectionCard,
  type DashboardChartSpec, type DashboardKpi, type DashboardQueueItem,
} from '../components/dashboard/DashboardEngine'
import './DashboardPage.css'

export type DashboardView = 'overview' | 'workforce' | 'payroll' | 'attendance' | 'approvals'

const money = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 0, style: 'currency', currency: 'INR' })
const count = new Intl.NumberFormat('en-IN')
function formatMonth(value: string) {
  if (!value) return 'Current month'
  const [year, month] = value.split('-').map(Number)
  return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric' }).format(new Date(year, month - 1, 1))
}
function formatDate(value: string) {
  return value ? new Intl.DateTimeFormat('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }).format(new Date(value)) : ''
}

export default function DashboardPage({ view = 'overview' }: { view?: DashboardView }) {
  const session = useAuthSession()
  const navigate = useNavigate()
  const scopedClientId = Number(session?.user.clientId || 0)
  const clientScoped = scopedClientId > 0 && !session?.user.permissions.includes('security.manage')
  const [preferredClient, setClientId] = useSessionPreference('dashboard.ui:' + session?.user.id + ':' + scopedClientId + ':client', scopedClientId)
  const clientId = clientScoped ? scopedClientId : preferredClient
  const [dashboard, setDashboard] = useState<DashboardSnapshot | null>(null)
  const [loading, setLoading] = useState(true)
  const [revision, setRevision] = useState(0)

  useEffect(() => {
    let active = true
    setLoading(true)
    void getDashboard(clientId).then(data => {
      if (!active) return
      setDashboard(data)
      if (!clientScoped && data.selectedClientId !== clientId) setClientId(data.selectedClientId)
    }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [clientId, clientScoped, revision])

  const canOpenEmployees = !loading && ['employees.view', 'employees.manage'].some(permission => session?.user.permissions.includes(permission))
  const openWorkforce = (filters: Partial<Record<WorkforceField, string>> = {}) => navigate(workforcePath(clientId, filters))
  const workforceAction = (filters: Partial<Record<WorkforceField, string>> = {}) => canOpenEmployees ? () => openWorkforce(filters) : undefined
  const metrics = dashboard?.metrics
  const canSee = (section: string) => dashboard?.sections.includes(section) && (view === 'overview' || view === section)
  const attendanceReady = metrics?.activeEmployees ? Math.round(metrics.attendanceRecorded / metrics.activeEmployees * 100) : 0
  const selectedClient = dashboard?.clients.find(client => client.id === clientId)
  const clientName = clientId === 0 ? 'All clients' : selectedClient?.name ?? 'Selected client'
  const isRru = selectedClient?.code?.toUpperCase() === 'RRU' || selectedClient?.name?.toLowerCase().includes('rashtriya raksha university')
  const period = formatMonth(dashboard?.month ?? '')
  const statuses = dashboard?.payRunStatuses ?? []
  const kpis: DashboardKpi[] = []
  const charts: DashboardChartSpec[] = []
  const actions: DashboardQueueItem[] = []
  const chart = (id: string, title: string, subtitle: string, data: DashboardChartPoint[] = [], kind: DashboardChartSpec['kind'] = 'horizontalBar', field?: WorkforceField, currency = false) => {
    charts.push({ id: 'hrms-' + id, title, subtitle, labels: data.map(point => point.label), series: [{ label: title, values: data.map(point => point.value) }], kind,
      compatibleKinds: ['horizontalBar', 'bar', 'doughnut'], valueFormat: currency ? value => money.format(value) : undefined,
      onPointClick: field && canOpenEmployees ? label => openWorkforce({ [field]: label }) : undefined })
  }
  const action = (id: string, title: string, value: number, path: string, icon: DashboardQueueItem['icon']) => {
    actions.push({ id, title: count.format(value) + ' ' + title, meta: clientName, owner: period, age: '', priority: value > 0 ? 'High' : 'Normal', icon, actionLabel: 'Open', onAction: () => navigate(path) })
  }

  if (canSee('workforce')) {
    kpis.push({ key: 'employees', label: 'Active employees', value: count.format(metrics?.activeEmployees ?? 0), helper: count.format(metrics?.portalUsers ?? 0) + ' ESS enabled', icon: <TeamOutlined />, onClick: workforceAction() })
    chart('department', 'Workforce by department', 'Active headcount concentration by department.', dashboard?.departmentHeadcount, 'horizontalBar', 'department')
    chart('location', 'Workforce by location', 'Active headcount across mapped work locations.', dashboard?.locationHeadcount, 'horizontalBar', 'location')
    if (view === 'workforce') {
      chart('ess', 'ESS Adoption', 'Portal access enablement across active employees.', dashboard?.essAdoption, 'doughnut', 'portalAccess')
      chart('gender', 'Gender Mix', 'Employee master distribution by gender.', dashboard?.genderHeadcount, 'doughnut', 'gender')
      chart('designation', 'Designation Concentration', 'Largest employee groups by designation.', dashboard?.designationHeadcount, 'horizontalBar', 'designation')
      chart('grade', 'Grade Distribution', 'Grade-wise workforce segmentation.', dashboard?.gradeHeadcount, 'horizontalBar', 'grade')
    }
  }
  if (canSee('payroll')) {
    kpis.push({ key: 'payroll', label: 'Net payroll', value: money.format(metrics?.currentMonthNetPay ?? 0), helper: count.format(metrics?.currentMonthPayRuns ?? 0) + ' run(s) this month', icon: <WalletOutlined />, tone: 'success' })
    const trend = dashboard?.payrollTrend ?? []
    charts.push({ id: 'hrms-payroll-trend', title: 'Payroll Cost Trend', subtitle: 'Latest net pay ' + money.format(trend.at(-1)?.netPay ?? 0) + ' / Peak ' + money.format(Math.max(0, ...trend.map(row => row.netPay))),
      labels: trend.map(row => formatMonth(row.month)), series: [{ label: 'Net pay', values: trend.map(row => row.netPay) }, { label: 'Payroll cost', values: trend.map(row => row.payrollCost), color: '#20a46b' }], kind: 'area', compatibleKinds: ['area', 'line', 'bar'], valueFormat: value => money.format(value) })
    action('payroll', 'blocking payroll validations', metrics?.payrollExceptions ?? 0, '/payroll/regular', <WalletOutlined />)
    if (view === 'payroll') {
      chart('run-status', 'Run Status Mix', 'Current month payrun control status.', statuses.map(item => ({ label: item.status, value: item.count })), 'doughnut')
      chart('payment-status', 'Employee Payment Status', 'Payment progress inside current month payruns.', dashboard?.payrollPaymentStatus, 'doughnut')
      const cost = dashboard?.payrollCostBreakup
      chart('amounts', 'Payroll Amount Composition', 'Earnings, deductions and net pay comparison.', cost ? [
        { label: 'Gross earnings', value: cost.grossEarnings }, { label: 'Statutory deductions', value: cost.statutoryDeductions },
        { label: 'Other deductions', value: cost.otherDeductions }, { label: 'Net pay', value: cost.netPay },
      ] : [], 'horizontalBar', undefined, true)
      chart('run-type', 'Run Type Mix', 'Regular, off-cycle and other payroll runs.', dashboard?.payrollRunType, 'doughnut')
    }
  }
  if (canSee('attendance')) {
    kpis.push({ key: 'attendance', label: 'Attendance ready', value: attendanceReady + '%', helper: count.format(metrics?.attendanceMissing ?? 0) + ' missing, ' + count.format(metrics?.attendanceIssues ?? 0) + ' issue(s)', icon: <CalendarOutlined />, tone: 'warning' })
    chart('attendance-ready', 'Attendance Readiness', 'Current month attendance recording readiness.', dashboard?.attendanceMix, 'doughnut')
    chart('payability', 'Payable Exposure', 'Present and LOP day totals from attendance review.', dashboard?.attendancePayability)
    action('attendance', 'attendance exceptions', metrics?.attendanceIssues ?? 0, '/attendance', <CalendarOutlined />)
    if (view === 'attendance') {
      chart('daily-attendance', 'Daily Attendance Status', 'Daily attendance event mix for the reporting month.', dashboard?.attendanceDailyStatus, 'doughnut')
      chart('attendance-source', 'Attendance Source', 'How monthly attendance records were populated.', dashboard?.attendanceSourceType)
      chart('attendance-exceptions', 'Readiness Exceptions', 'Missing attendance and blocking value issues.', [{ label: 'Missing employees', value: metrics?.attendanceMissing ?? 0 }, { label: 'Check value issues', value: metrics?.attendanceIssues ?? 0 }])
    }
  }
  if (canSee('approvals')) {
    kpis.push({ key: 'approvals', label: 'Pending approvals', value: count.format(metrics?.pendingTasks ?? 0), helper: count.format(metrics?.pendingLeaveRequests ?? 0) + ' leave request(s)', icon: <ClockCircleOutlined />, tone: 'danger' })
    chart('approval-stages', 'Approval Stage Load', 'Current pending tasks grouped by approval stage.', dashboard?.approvalStageBreakup)
    chart('approval-actions', 'Action History Mix', 'Your recently completed task outcomes.', dashboard?.approvalActionMix, 'doughnut')
    action('approvals', 'workflow tasks', metrics?.pendingTasks ?? 0, '/tasks', <ClockCircleOutlined />)
    if (view === 'approvals') {
      chart('approval-resource', 'Pending by Resource', 'Request type split of your pending approval queue.', dashboard?.approvalResourceBreakup, 'doughnut')
      chart('approval-aging', 'Approval Aging', 'How long tasks have waited for action.', dashboard?.approvalAging)
    }
  }

  return <section className={'hrms-dashboard dashboard-view-' + view}>
    <PageHeaderPortal slot="dashboard-client-controls">{!clientScoped && <SearchSelect testId="dashboard-client" value={clientId} onChange={value => setClientId(Number(value))} disabled={loading} options={[{ value: 0, label: 'All clients' }, ...(dashboard?.clients ?? []).map(client => ({ value: client.id, label: client.name }))]} />}</PageHeaderPortal>
    <PageHeaderPortal slot="dashboard-page-controls"><Space wrap><Tag icon={<CalendarOutlined />}>{period}</Tag><Button aria-label="Refresh dashboard" icon={<ReloadOutlined spin={loading} />} disabled={loading} onClick={() => setRevision(value => value + 1)}>Refresh</Button></Space></PageHeaderPortal>
    <DashboardEngine loading={loading}>
      {kpis.length > 0 ? <DashboardKpiGrid items={kpis} label="HR and payroll metrics" /> : <Empty description="No dashboard sections are assigned for this view." />}
      {canSee('workforce') && <DashboardSectionCard title="Employee type & category" subtitle="Active employees by classification. Select a count to open the matching employee list.">
        <div className="dashboard-classifications">{([{ key: 'employmentType', label: 'Employee type', data: dashboard?.employmentTypeHeadcount }, { key: 'skillCategory', label: 'Employee category', data: dashboard?.skillCategoryHeadcount }] as const).map(group => <section key={group.key}>
          <h4>{group.label}</h4><div className="employee-classification-counts">{(group.data || []).map(point => <button key={point.label} type="button" disabled={!canOpenEmployees} onClick={() => openWorkforce({ [group.key]: point.label })}><span>{point.label}</span><strong>{count.format(point.value)}</strong></button>)}</div>
          {!group.data?.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No active employees in this group." />}
        </section>)}</div>
      </DashboardSectionCard>}
      {canSee('workforce') && isRru && dashboard?.selectedClientId === clientId && <DashboardSectionCard title="RRU Workforce Overview" subtitle="Campus-wise staffing, gender and skill-category distribution.">
        <DashboardKpiGrid label="RRU workforce metrics" items={[
          { key: 'total', label: 'Total staff', value: count.format(metrics?.activeEmployees ?? 0), helper: 'Active employee records', icon: <TeamOutlined />, onClick: workforceAction() },
          ...(['Male', 'Female'] as const).map(gender => ({ key: gender, label: gender, value: count.format(dashboard.genderHeadcount.find(point => point.label.toLowerCase() === gender.toLowerCase())?.value ?? 0), helper: 'Across mapped campuses', icon: <TeamOutlined />, onClick: workforceAction({ gender }) })),
          { key: 'appointed', label: 'Appointed total', value: count.format(metrics?.activeEmployees ?? 0), helper: 'Current active appointments', icon: <CheckCircleOutlined />, onClick: workforceAction() },
        ]} />
        <DataTable rows={dashboard.campusGenderHeadcount} getRowId={row => row.campus} exportFileName="campus-workforce" emptyText="No active campus workforce found." columns={[
          { key: 'campus', label: 'Campus', width: 220 },
          ...(['total', 'male', 'female', 'other'] as const).map(key => ({ key, label: ({ total: 'Total staff', male: 'Male', female: 'Female', other: 'Other / unmapped' })[key], value: (row: DashboardSnapshot['campusGenderHeadcount'][number]) => row[key], render: (row: DashboardSnapshot['campusGenderHeadcount'][number]) => <Button type="link" disabled={!canOpenEmployees} onClick={() => openWorkforce({ location: row.campus, ...(key !== 'total' ? { campusGender: key } : {}) })}>{count.format(row[key])}</Button> })),
        ]} />
      </DashboardSectionCard>}
      {charts.length > 0 && <DashboardChartGrid label="HR and payroll analytics">{charts.map(spec => <DashboardChartCard key={spec.id} spec={spec} />)}</DashboardChartGrid>}
      {actions.length > 0 && <DashboardActionQueue title="Action Queue" subtitle="Items that can block HR and payroll closure." items={actions} emptyText="No pending actions." />}
      {canSee('payroll') && <>
        <DashboardSectionCard title="Payroll Status" subtitle="Current month run health by status.">
          <DataTable rows={statuses} getRowId={row => row.status} exportFileName="payroll-status" emptyText="No runs yet." columns={[
            { key: 'status', label: 'Status' }, { key: 'count', label: 'Pay runs', render: row => count.format(row.count) },
            { key: 'netPay', label: 'Net pay (INR)', render: row => money.format(row.netPay) },
          ]} />
        </DashboardSectionCard>
        <DashboardSectionCard title="Recent Pay Runs" subtitle="Latest payroll activity for the selected client view.">
          <div className="dashboard-insight-strip" aria-label="Payroll status totals">{['Approved', 'Processing', 'Pending Approval'].map(status => {
            const item = statuses.find(row => row.status === status)
            return <article key={status}><WalletOutlined /><div><b>{count.format(item?.count ?? 0)}</b><span>{status} pay runs</span><span>{money.format(item?.netPay ?? 0)}</span></div></article>
          })}</div>
          <DataTable rows={dashboard?.recentPayRuns ?? []} getRowId={row => row.id} exportFileName="recent-pay-runs" emptyText="No payroll activity found for this view." columns={[
            { key: 'clientName', label: 'Client', width: 220 }, { key: 'payPeriod', label: 'Period' },
            { key: 'run', label: 'Run', value: row => row.runName || row.runType },
            { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Approved' ? 'green' : row.status === 'Processing' ? 'blue' : 'orange'}>{row.status}</Tag> },
            { key: 'employeeCount', label: 'Employees', render: row => count.format(row.employeeCount) },
            { key: 'netPay', label: 'Net pay (INR)', render: row => money.format(row.netPay) },
            { key: 'updatedAt', label: 'Updated', render: row => formatDate(row.updatedAt) },
          ]} />
        </DashboardSectionCard>
      </>}
    </DashboardEngine>
  </section>
}
