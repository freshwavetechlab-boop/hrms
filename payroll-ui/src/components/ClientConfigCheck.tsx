import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Card, Col, Empty, Progress, Row, Select, Space, Spin, Tag } from 'antd'
import { Link } from 'react-router-dom'
import { CheckCircleOutlined, ExclamationCircleOutlined, ReloadOutlined } from '@ant-design/icons'
import { setup0 } from '../data/payrollDefaults'
import { getAttendanceGroups, getAttendanceSettings, getAttendanceShifts, getHolidays, getLeaveTypes } from '../services/leaveAttendanceService'
import { getClients, getEmployees } from '../services/payrollService'
import { getClientBillingAdvanced, getClientBillingConfigurations, getClientBillingModule, getDropdowns, getRecruitmentAdminSetup, getScheduledJobs, getSetup, getTravelExpenseSetup, getWorkLocations } from '../services/settingsService'
import { getNotificationSetup } from '../services/notificationService'
import type { AttendanceGroup, AttendanceSettings, AttendanceShift, Client, ClientBillingAdvancedSetup, ClientBillingConfiguration, ClientBillingModule, Drop, Employee, Holiday, LeaveType, NotificationSetup, RecruitmentAdminSetup, ScheduledJob, Setup, TravelExpenseSetup, WorkLocation } from '../types/payroll'
import './ClientConfigCheck.css'

type ConfigState = {
  setup: Setup
  clients: Client[]
  employees: Employee[]
  locations: WorkLocation[]
  drops: Drop[]
  shifts: AttendanceShift[]
  attendanceGroups: AttendanceGroup[]
  attendanceSettings: AttendanceSettings | null
  leaveTypes: LeaveType[]
  holidays: Holiday[]
  billingModule: ClientBillingModule
  billingRows: ClientBillingConfiguration[]
  billingAdvanced: ClientBillingAdvancedSetup
  travel: TravelExpenseSetup
  recruitment: RecruitmentAdminSetup
  notifications: NotificationSetup
  jobs: ScheduledJob[]
}

type CheckItem = {
  key: string
  title: string
  status: 'Complete' | 'Attention' | 'Missing'
  summary: string
  details: string[]
  missing: string[]
  route: string
}

const todayYear = new Date().getFullYear()
const emptyNotification: NotificationSetup = { smtp: { id: 1, isEnabled: false, deliveryPaused: false, host: '', port: 587, userName: '', password: '', enableSsl: true, fromEmail: '', fromName: '' }, templates: [], rules: [], queue: [], logs: [] }
const number = (value: number) => value.toLocaleString('en-IN')
const yesNo = (value: boolean) => value ? 'Yes' : 'No'
const active = <T extends { isActive?: boolean }>(rows: T[]) => rows.filter(row => row.isActive !== false)
const refId = (value: string | number | null | undefined) => Number(value || 0)
const unique = (rows: string[]) => Array.from(new Set(rows.filter(Boolean)))
const statusColor = (status: CheckItem['status']) => status === 'Complete' ? 'success' : status === 'Attention' ? 'warning' : 'error'
const statusIcon = (status: CheckItem['status']) => status === 'Complete' ? <CheckCircleOutlined /> : <ExclamationCircleOutlined />
const item = (key: string, title: string, route: string, details: string[], missing: string[], attention = false): CheckItem => ({
  key,
  title,
  route,
  details,
  missing,
  status: missing.length ? 'Missing' : attention ? 'Attention' : 'Complete',
  summary: missing.length ? `${missing.length} item${missing.length > 1 ? 's' : ''} remaining` : attention ? 'Configured with warnings' : 'Configured'
})

export default function ClientConfigCheck() {
  const [clientId, setClientId] = useState(0)
  const [loading, setLoading] = useState(true)
  const [state, setState] = useState<ConfigState | null>(null)

  const load = async (preferredClientId = clientId) => {
    setLoading(true)
    const [setup, clients, employees, locations, drops, billingModule, billingRows, billingAdvanced, travel, recruitment, notifications, jobs] = await Promise.all([
      getSetup(setup0),
      getClients(),
      getEmployees(),
      getWorkLocations(),
      getDropdowns(),
      getClientBillingModule(),
      getClientBillingConfigurations(),
      getClientBillingAdvanced(),
      getTravelExpenseSetup(),
      getRecruitmentAdminSetup(),
      getNotificationSetup().catch(() => emptyNotification),
      getScheduledJobs().catch(() => [] as ScheduledJob[])
    ])
    const activeClients = clients.filter(row => row.isActive)
    const nextClientId = preferredClientId || activeClients[0]?.id || 0
    const [shifts, attendanceGroups, attendanceSettings, leaveTypes, holidays] = nextClientId
      ? await Promise.all([getAttendanceShifts(nextClientId), getAttendanceGroups(nextClientId), getAttendanceSettings(nextClientId).catch(() => null), getLeaveTypes(nextClientId), getHolidays(nextClientId, todayYear)])
      : [[], [], null, [], []] as const
    setClientId(nextClientId)
    setState({ setup, clients: activeClients, employees, locations, drops, shifts, attendanceGroups, attendanceSettings, leaveTypes, holidays, billingModule, billingRows, billingAdvanced, travel, recruitment, notifications, jobs })
    setLoading(false)
  }

  useEffect(() => { void load(0) }, [])
  useEffect(() => {
    if (!state || !clientId) return
    setLoading(true)
    void Promise.all([getAttendanceShifts(clientId), getAttendanceGroups(clientId), getAttendanceSettings(clientId).catch(() => null), getLeaveTypes(clientId), getHolidays(clientId, todayYear)])
      .then(([shifts, attendanceGroups, attendanceSettings, leaveTypes, holidays]) => setState(current => current ? { ...current, shifts, attendanceGroups, attendanceSettings, leaveTypes, holidays } : current))
      .finally(() => setLoading(false))
  }, [clientId])

  const checks = useMemo(() => {
    if (!state || !clientId) return []
    const client = state.clients.find(row => row.id === clientId)
    const employees = state.employees.filter(row => row.clientId === clientId && row.isActive)
    const locations = active(state.locations).filter(row => row.clientId === clientId)
    const structures = active(state.setup.salaryStructures).filter(row => refId(row.clientId) === clientId)
    const payslips = active(state.setup.payslipTemplates).filter(row => refId(row.clientId) === clientId)
    const components = active(state.setup.salaryComponents)
    const assignedStructureIds = new Set(employees.map(row => refId(row.salaryStructureId)).filter(Boolean))
    const unassignedSalary = employees.filter(row => !refId(row.salaryStructureId)).length
    const missingLocation = employees.filter(row => !row.workLocationId).length
    const attendanceGroups = active(state.attendanceGroups)
    const leaveTypes = active(state.leaveTypes)
    const travelSetting = state.travel.clientSettings.find(row => row.clientId === clientId)
    const travelPolicies = active(state.travel.policies).filter(row => row.companyId === clientId)
    const travelAssignments = active(state.travel.assignments).filter(row => row.companyId === clientId)
    const travelRules = active(state.travel.rules).filter(row => travelPolicies.some(policy => policy.id === row.policyId))
    const travelCategories = active(state.travel.categories).filter(row => row.clientId === clientId)
    const billingHeaders = active(state.billingAdvanced.headers).filter(row => row.clientId === clientId)
    const billingLines = active(state.billingAdvanced.lines).filter(row => billingHeaders.some(header => header.id === row.headerId))
    const billingRows = active(state.billingRows).filter(row => row.clientId === clientId)
    const recruitmentSetting = state.recruitment.settings.find(row => row.clientId === clientId)
    const recruitmentMasters = active(state.recruitment.masters).filter(row => row.clientId === clientId)
    const recruitmentRules = active(state.recruitment.assignmentRules).filter(row => row.clientId === clientId)
    const notificationRules = state.notifications.rules.filter(row => row.isEnabled && (!row.clientId || row.clientId === clientId))
    const clientDrops = active(state.drops).filter(row => !row.clientId || row.clientId === clientId)

    return [
      item('client', 'Client Master', '/settings/clients',
        [
          client ? `Client: ${client.name} (${client.code || 'no code'})` : 'Client is not selected.',
          `${locations.length} active work location${locations.length === 1 ? '' : 's'}.`,
          `${employees.length} active employee${employees.length === 1 ? '' : 's'}.`
        ],
        [
          !client?.code ? 'Client code is missing.' : '',
          locations.length === 0 ? 'Add at least one work location.' : '',
          employees.length === 0 ? 'Upload or create employees.' : ''
        ].filter(Boolean)
      ),
      item('masters', 'Dropdown & Organization Masters', '/settings/dropdown-masters',
        [
          `${clientDrops.length} usable dropdown value${clientDrops.length === 1 ? '' : 's'} for global/client scope.`,
          `Departments: ${unique(employees.map(row => row.department)).length || 0}; Designations: ${unique(employees.map(row => row.designation)).length || 0}.`,
          `Work week options: ${clientDrops.filter(row => row.type === 'Work Week').length}.`
        ],
        [
          clientDrops.filter(row => row.type === 'Department').length === 0 && unique(employees.map(row => row.department)).length === 0 ? 'Department master/employee department data is missing.' : '',
          clientDrops.filter(row => row.type === 'Work Week').length === 0 ? 'Work Week dropdown is missing; attendance policy selection may be limited.' : ''
        ].filter(Boolean),
        clientDrops.length < 5
      ),
      item('employees', 'Employees & Reporting', '/employees/master',
        [
          `${number(employees.length)} active employees.`,
          `${number(employees.filter(row => row.portalAccess).length)} employees have portal access enabled.`,
          `${number(employees.filter(row => row.reportingManagerUserId).length)} employees have workflow reporting user mapped.`,
          `${number(missingLocation)} employees have no work location.`
        ],
        [
          employees.length === 0 ? 'No active employees for this client.' : '',
          missingLocation > 0 ? `${missingLocation} employee(s) need work location mapping.` : ''
        ].filter(Boolean),
        employees.some(row => !row.reportingManagerUserId)
      ),
      item('payroll', 'Payroll Structure', '/settings/salary-templates',
        [
          `${components.length} active salary component${components.length === 1 ? '' : 's'}.`,
          `${structures.length} active salary template${structures.length === 1 ? '' : 's'} for this client.`,
          `${assignedStructureIds.size} template${assignedStructureIds.size === 1 ? '' : 's'} currently assigned to employees.`,
          `${payslips.length} active payslip template${payslips.length === 1 ? '' : 's'}.`
        ],
        [
          components.length === 0 ? 'Create salary components.' : '',
          structures.length === 0 ? 'Create at least one salary template for this client.' : '',
          payslips.length === 0 ? 'Create a payslip template.' : '',
          unassignedSalary > 0 ? `${unassignedSalary} employee(s) do not have salary template assigned.` : ''
        ].filter(Boolean)
      ),
      item('statutory', 'Compliance & Tax', '/settings/statutory-setup',
        [
          `EPF enabled: ${yesNo(Boolean(state.setup.statutory.epf))}${state.setup.statutory.epfNumber ? ` (${state.setup.statutory.epfNumber})` : ''}.`,
          `ESI enabled: ${yesNo(Boolean(state.setup.statutory.esi))}${state.setup.statutory.esiNumber ? ` (${state.setup.statutory.esiNumber})` : ''}.`,
          `PT enabled: ${yesNo(Boolean(state.setup.statutory.pt))}; LWF enabled: ${yesNo(Boolean(state.setup.statutory.lwf))}.`,
          `${state.setup.tax.clientSettings.filter(row => refId(row.clientId) === clientId && row.active).length} active client tax setting(s).`
        ],
        [
          state.setup.statutory.epf && !state.setup.statutory.epfNumber ? 'EPF is enabled but EPF number is missing.' : '',
          state.setup.statutory.esi && !state.setup.statutory.esiNumber ? 'ESI is enabled but ESI number is missing.' : '',
          state.setup.statutory.pt && !state.setup.statutory.ptStateSlabs?.length ? 'PT is enabled but PT slab setup is missing.' : ''
        ].filter(Boolean),
        !state.setup.tax.slabs?.length
      ),
      item('attendance', 'Shift & Attendance Policy', '/settings/leave-attendance/attendance-policies',
        [
          `${active(state.shifts).length} active shift${active(state.shifts).length === 1 ? '' : 's'} configured.`,
          `${attendanceGroups.length} attendance polic${attendanceGroups.length === 1 ? 'y' : 'ies'} configured.`,
          state.attendanceSettings ? `Attendance engine: ${state.attendanceSettings.checkInTime} to ${state.attendanceSettings.checkOutTime}, full day ${state.attendanceSettings.minimumHoursForFullDay} hrs, half day ${state.attendanceSettings.minimumHoursForHalfDay} hrs.` : 'Attendance engine settings not found.',
          ...attendanceGroups.slice(0, 5).map(row => `${row.name}: ${row.employeeCount} employees, ${row.workWeek}, cycle ${row.attendanceCycleStartDay}-${row.attendanceCycleEndDay}, report day ${row.payrollReportGenerationDay}${row.shiftName ? `, shift ${row.shiftName}` : ''}.`)
        ],
        [
          active(state.shifts).length === 0 ? 'Create at least one shift if attendance depends on shift timing.' : '',
          attendanceGroups.length === 0 ? 'Create attendance policy and assign employees.' : '',
          attendanceGroups.reduce((sum, row) => sum + (row.employeeCount || row.employeeIds?.length || 0), 0) < employees.length ? 'Some employees may not be covered by attendance policies.' : ''
        ].filter(Boolean)
      ),
      item('leave', 'Leave & Holiday', '/settings/leave-attendance/leave-types',
        [
          `${leaveTypes.length} active leave type${leaveTypes.length === 1 ? '' : 's'}.`,
          `${state.holidays.length} holiday${state.holidays.length === 1 ? '' : 's'} for ${todayYear}.`,
          ...leaveTypes.slice(0, 6).map(row => `${row.name}: ${row.entitlement} ${row.entitlementPeriod.toLowerCase()}, ${row.type}, ${row.applicabilityMode}.`)
        ],
        [
          leaveTypes.length === 0 ? 'Configure leave types.' : '',
          state.holidays.length === 0 ? `Upload/create ${todayYear} holiday calendar.` : ''
        ].filter(Boolean)
      ),
      item('travel', 'Travel & Expense', '/settings/travel-and-expense-policies',
        [
          `Module enabled for client: ${yesNo(Boolean(travelSetting?.isEnabled))}.`,
          `Travel desk: ${yesNo(Boolean(travelSetting?.isTravelDeskEnabled))}; trip/accommodation/local tabs: ${yesNo(Boolean(travelSetting?.showTripDetails))}/${yesNo(Boolean(travelSetting?.showAccommodationDetails))}/${yesNo(Boolean(travelSetting?.showLocalTravelDetails))}.`,
          `${travelPolicies.length} policy header(s), ${travelAssignments.length} assignment(s), ${travelRules.length} rule(s), ${travelCategories.length} claim category rows.`,
          ...travelRules.slice(0, 5).map(row => `${row.policyName} - ${row.ruleType}: ${row.ruleName}, ${row.isAllowed ? 'allowed' : 'blocked'}, limit ${row.limitAmount ?? 'not set'}, exception ${row.exceptionHandling}.`)
        ],
        [
          !travelSetting?.isEnabled ? 'Enable Travel & Expense for this client if employee travel/claims are required.' : '',
          travelSetting?.isEnabled && travelPolicies.length === 0 ? 'Create travel policy header.' : '',
          travelSetting?.isEnabled && travelCategories.length === 0 ? 'Enable claim headers/categories for this client.' : ''
        ].filter(Boolean)
      ),
      item('billing', 'Client Billing', '/settings/client-billing-configuration',
        [
          `Billing module enabled: ${yesNo(state.billingModule.isEnabled)}; advanced costing: ${yesNo(state.billingModule.advancedCostingEnabled)}.`,
          `${billingHeaders.length} contract/rule header(s), ${billingLines.length} billable line(s), ${billingRows.length} legacy billing row(s).`,
          ...billingHeaders.slice(0, 4).map(header => `${header.ruleName}: ${header.workLocationName || 'All locations'}, GST ${header.gstRatePercent}%, ${billingLines.filter(line => line.headerId === header.id).length} billable line(s).`),
          ...billingLines.slice(0, 6).map(line => `${line.ruleName} - ${line.lineType}/${line.matchValue}: ${line.billingTreatment}, ${line.rateType} ${line.rateValue}, GST ${yesNo(line.taxApplicable)}.`)
        ],
        [
          state.billingModule.isEnabled && !state.billingModule.advancedCostingEnabled ? 'Enable advanced costing if billing is contract-line based.' : '',
          state.billingModule.isEnabled && billingHeaders.length === 0 ? 'Create client billing contract/rule header.' : '',
          state.billingModule.isEnabled && billingLines.length === 0 ? 'Add billable contract lines.' : ''
        ].filter(Boolean)
      ),
      item('recruitment', 'Recruitment', '/recruitment/dashboard',
        [
          `Recruitment enabled: ${yesNo(Boolean(recruitmentSetting?.recruitmentEnabled))}.`,
          `Employee RFR: ${yesNo(Boolean(recruitmentSetting?.allowEmployeeRfrCreation))}; vendor/consultant/internal/referral: ${yesNo(Boolean(recruitmentSetting?.enableVendorHiring))}/${yesNo(Boolean(recruitmentSetting?.enableConsultantHiring))}/${yesNo(Boolean(recruitmentSetting?.enableInternalHiring))}/${yesNo(Boolean(recruitmentSetting?.enableReferralHiring))}.`,
          `${recruitmentMasters.length} master values, ${recruitmentRules.length} assignment rules, ${state.recruitment.slaRules.filter(row => row.clientId === clientId && row.isActive).length} SLA rules.`,
        ],
        [
          !recruitmentSetting?.recruitmentEnabled ? 'Recruitment is disabled for this client.' : '',
          recruitmentSetting?.recruitmentEnabled && recruitmentMasters.length === 0 ? 'Add recruitment dropdown/master values.' : '',
          recruitmentSetting?.recruitmentEnabled && recruitmentRules.length === 0 ? 'Create recruiter assignment rules.' : ''
        ].filter(Boolean)
      ),
      item('notifications', 'Notifications & Jobs', '/settings/notifications',
        [
          `SMTP enabled: ${yesNo(Boolean(state.notifications.smtp.isEnabled))}; paused: ${yesNo(Boolean(state.notifications.smtp.deliveryPaused))}.`,
          `${state.notifications.templates.filter(row => row.isActive).length} active template(s), ${notificationRules.length} enabled rule(s) for global/client scope.`,
          `${state.jobs.filter(row => row.isEnabled).length} scheduled job(s) enabled.`
        ],
        [
          !state.notifications.smtp.isEnabled ? 'SMTP is disabled; queued emails will not be delivered.' : '',
          state.notifications.templates.filter(row => row.isActive).length === 0 ? 'Create notification templates.' : '',
          notificationRules.length === 0 ? 'Create enabled notification rules for required events.' : ''
        ].filter(Boolean),
        state.notifications.smtp.deliveryPaused
      )
    ]
  }, [state, clientId])

  const complete = checks.filter(row => row.status === 'Complete').length
  const score = checks.length ? Math.round((complete / checks.length) * 100) : 0
  const selectedClient = state?.clients.find(row => row.id === clientId)

  return <section className="client-config-check">
    <Card className="config-check-hero">
      <div>
        <span className="eyebrow purple">Client configuration readiness</span>
        <h2>{selectedClient ? selectedClient.name : 'Select client'}</h2>
        <p>Run a full client-wise setup check before payroll, attendance, billing, ESS, claims or recruitment go live.</p>
      </div>
      <Space wrap>
        <Select className="config-check-client" showSearch optionFilterProp="label" value={clientId || undefined} placeholder="Select client" onChange={value => setClientId(Number(value))} options={(state?.clients ?? []).map(client => ({ value: client.id, label: `${client.name} (${client.code || 'No code'})` }))} />
        <Button icon={<ReloadOutlined />} onClick={() => void load(clientId)} loading={loading}>Run check</Button>
      </Space>
    </Card>

    {loading && !state ? <Card><Spin /> Loading configuration...</Card> : !clientId ? <Empty description="Select a client to run configuration check." /> : <>
      <Row gutter={[12, 12]}>
        <Col xs={24} md={7}><Card className="config-score-card"><Progress type="circle" percent={score} status={score === 100 ? 'success' : 'normal'} /><strong>{complete} of {checks.length} sections complete</strong><span>{checks.reduce((sum, row) => sum + row.missing.length, 0)} open action(s)</span></Card></Col>
        <Col xs={24} md={17}>
          <div className="config-summary-grid">
            {checks.map(row => <article key={row.key}><Tag color={statusColor(row.status)} icon={statusIcon(row.status)}>{row.status}</Tag><strong>{row.title}</strong><span>{row.summary}</span></article>)}
          </div>
        </Col>
      </Row>

      <Alert className="config-check-note" showIcon type="info" message="How to use this page" description="Read each section as a go-live checklist. Complete means the minimum setup exists. Attention means setup exists but should be reviewed. Missing means configuration is not ready for that area." />

      <div className="config-check-list">
        {checks.map(row => <Card key={row.key} className={`config-check-card ${row.status.toLowerCase()}`} title={<Space><Tag color={statusColor(row.status)} icon={statusIcon(row.status)}>{row.status}</Tag><span>{row.title}</span></Space>} extra={<Link to={row.route}>Open setup</Link>}>
          <Row gutter={[14, 14]}>
            <Col xs={24} lg={row.missing.length ? 14 : 24}>
              <h4>Configured</h4>
              <ul>{row.details.map((detail, index) => <li key={`${row.key}-detail-${index}`}>{detail}</li>)}</ul>
            </Col>
            {row.missing.length > 0 && <Col xs={24} lg={10}>
              <h4>Remaining</h4>
              <ul className="missing">{row.missing.map((detail, index) => <li key={`${row.key}-missing-${index}`}>{detail}</li>)}</ul>
            </Col>}
          </Row>
        </Card>)}
      </div>
    </>}
  </section>
}
