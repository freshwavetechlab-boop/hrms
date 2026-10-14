import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Col, Collapse, Empty, Row, Select, Space, Spin, Tabs, Tag } from 'antd'
import { Link } from 'react-router-dom'
import { CheckCircleOutlined, ExclamationCircleOutlined, ReloadOutlined } from '@ant-design/icons'
import { setup0 } from '../data/payrollDefaults'
import { getJsonResult } from '../services/apiClient'
import { buildEmployeeCoverage } from '../utils/clientConfigCoverage'
import ClientEmployeeCoverage from './ClientEmployeeCoverage'
import type { AttendanceGroup, AttendanceSettings, AttendanceShift, Client, ClientBillingAdvancedSetup, ClientBillingConfiguration, ClientBillingModule, Drop, Employee, Holiday, LeaveType, NotificationSetup, RecruitmentAdminSetup, ScheduledJob, Setup, TravelExpenseSetup, WorkLocation } from '../types/payroll'
import './ClientConfigCheck.css'

type ConfigState = {
  failures: Record<string, string>
  checkedAt: string
  checkedOn: string
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
  status: 'Configured' | 'Attention' | 'Blocked' | 'Not enabled' | 'Unavailable'
  summary: string
  details: string[]
  missing: Finding[]
  route: string
}
type Finding = { action: string; impact: string; payroll: 'Blocks payroll' | 'Review before payroll' | 'Does not block payroll'; route?: string }
const gap = (action: string, impact: string, payroll: Finding['payroll'] = 'Does not block payroll', route?: string): Finding => ({ action, impact, payroll, route })

const todayYear = new Date().getFullYear()
const emptyNotification: NotificationSetup = { smtp: { id: 1, isEnabled: false, deliveryPaused: false, host: '', port: 587, userName: '', password: '', enableSsl: true, fromEmail: '', fromName: '' }, templates: [], rules: [], queue: [], logs: [] }
const number = (value: number) => value.toLocaleString('en-IN')
const yesNo = (value: boolean) => value ? 'Yes' : 'No'
const active = <T extends { isActive?: boolean }>(rows: T[]) => rows.filter(row => row.isActive !== false)
const activeConfig = <T extends { active?: boolean }>(rows: T[]) => rows.filter(row => row.active !== false)
const refId = (value: string | number | null | undefined) => Number(String(value ?? '').split(':')[0].trim() || 0)
const matchesClient = (value: string | number, clientId: number) => String(value).split(/[;,|]/).some(part => refId(part) === clientId)
const unique = (rows: string[]) => Array.from(new Set(rows.filter(Boolean)))
const sources: Record<string, string[]> = {
  client: ['employees', 'locations'], masters: ['drops', 'employees'], employees: ['employees'],
  payroll: ['setup', 'employees'], statutory: ['setup'], attendance: ['attendance', 'groups', 'attendanceSettings', 'employees'],
  leave: ['leave', 'holidays'], travel: ['travel'], billing: ['billing', 'billingRows', 'billingAdvanced'],
  recruitment: ['recruitment'], notifications: ['notifications', 'jobs']
}
const checkGroups = [
  { key: 'organization', label: 'Organization', checks: ['client', 'masters', 'employees'] },
  { key: 'attendance', label: 'Attendance & leave', checks: ['attendance', 'leave'] },
  { key: 'payroll', label: 'Payroll & compliance', checks: ['payroll', 'statutory'] },
  { key: 'modules', label: 'Other modules', checks: ['travel', 'billing', 'recruitment', 'notifications'] }
]
const statusColor = (status: CheckItem['status']) => status === 'Configured' ? 'success' : status === 'Blocked' ? 'error' : status === 'Attention' ? 'warning' : 'default'
const statusIcon = (status: CheckItem['status']) => status === 'Configured' ? <CheckCircleOutlined /> : <ExclamationCircleOutlined />
const item = (key: string, title: string, route: string, details: string[], candidates: (Finding | string)[], disabled = false): CheckItem => {
  const missing = candidates.filter((row): row is Finding => typeof row !== 'string')
  return {
    key, title, route, details, missing,
    status: disabled ? 'Not enabled' : missing.some(row => row.payroll === 'Blocks payroll') ? 'Blocked' : missing.length ? 'Attention' : 'Configured',
    summary: disabled ? 'Optional module; payroll can run without it' : missing.length ? `${missing.length} action(s) remaining` : 'Setup found; review policy details'
  }
}

export default function ClientConfigCheck() {
  const [clientId, setClientId] = useState(0)
  const [loading, setLoading] = useState(false)
  const [clients, setClients] = useState<Client[]>([])
  const [clientError, setClientError] = useState('')
  const [state, setState] = useState<ConfigState | null>(null)
  const [activeTab, setActiveTab] = useState('summary')
  const [expandedCheck, setExpandedCheck] = useState('')
  const tabsRef = useRef<HTMLDivElement>(null)
  const openTab = (key: string, check = '') => {
    setActiveTab(key)
    setExpandedCheck(check)
    tabsRef.current?.scrollIntoView({ block: 'start' })
  }

  const load = async () => {
    if (!clientId || loading) return
    setLoading(true)
    setState(null)
    setActiveTab('summary')
    setExpandedCheck('')
    const failures: Record<string, string> = {}
    const read = async <T,>(key: string, path: string, fallback: T): Promise<T> => {
      const result = await getJsonResult<T>(path, fallback, { toast: false })
      if (!result.ok) failures[key] = result.error || 'Unable to read configuration.'
      return result.data
    }
    try {
      const [setup, employees, locations, drops, billingModule, billingRows, billingAdvanced, travel, recruitment, notifications, jobs, shifts, attendanceGroups, attendanceSettings, leaveTypes, holidays] = await Promise.all([
        read('setup', '/api/setup', setup0),
        read<Employee[]>('employees', '/api/employees', []),
        read<WorkLocation[]>('locations', '/api/work-locations', []),
        read<Drop[]>('drops', '/api/dropdowns', []),
        read<ClientBillingModule>('billing', '/api/client-billing/module', { isEnabled: false, advancedCostingEnabled: false }),
        read<ClientBillingConfiguration[]>('billingRows', '/api/client-billing/configurations', []),
        read<ClientBillingAdvancedSetup>('billingAdvanced', '/api/client-billing/advanced', { headers: [], lines: [] }),
        read<TravelExpenseSetup>('travel', '/api/travel-expense/setup', { clientSettings: [], policies: [], assignments: [], rules: [], categories: [], audit: [] }),
        read<RecruitmentAdminSetup>('recruitment', '/api/recruitment-admin', { settings: [], masters: [], consultants: [], vendors: [], assignmentRules: [], slaRules: [], documentChecklist: [], approvalMappings: [], templates: [] }),
        read('notifications', '/api/notifications/setup', emptyNotification),
        read<ScheduledJob[]>('jobs', '/api/scheduled-jobs', []),
        read<AttendanceShift[]>('attendance', `/api/leave-attendance/shifts?clientId=${clientId}`, []),
        read<AttendanceGroup[]>('groups', `/api/leave-attendance/groups?clientId=${clientId}`, []),
        read<AttendanceSettings | null>('attendanceSettings', `/api/leave-attendance/attendance-settings?clientId=${clientId}`, null),
        read<LeaveType[]>('leave', `/api/leave-attendance/leave-types?clientId=${clientId}`, []),
        read<Holiday[]>('holidays', `/api/leave-attendance/holidays?clientId=${clientId}&year=${todayYear}`, [])
      ])
      const now = new Date()
      const checkedOn = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
      setState({ failures, checkedAt: now.toLocaleString(), checkedOn, setup, clients, employees, locations, drops, shifts, attendanceGroups, attendanceSettings, leaveTypes, holidays, billingModule, billingRows, billingAdvanced, travel, recruitment, notifications, jobs })
    } finally { setLoading(false) }
  }

  useEffect(() => {
    let cancelled = false
    void getJsonResult<Client[]>('/api/clients?lookup=true', []).then(result => {
      if (cancelled) return
      if (result.ok) setClients(result.data.filter(row => row.isActive))
      else setClientError(result.error || 'Unable to load clients. Reload the page to retry.')
    })
    return () => { cancelled = true }
  }, [])

  const coverage = useMemo(() => state ? buildEmployeeCoverage({ ...state, clientId, asOf: state.checkedOn }) : [], [state, clientId])
  const checks = useMemo(() => {
    if (!state || !clientId) return []
    const client = state.clients.find(row => row.id === clientId)
    const employees = state.employees.filter(row => row.clientId === clientId && row.isActive)
    const locations = active(state.locations).filter(row => row.clientId === clientId)
    const structures = activeConfig(state.setup.salaryStructures).filter(row => matchesClient(row.clientId, clientId))
    const payslips = activeConfig(state.setup.payslipTemplates).filter(row => matchesClient(row.clientId, clientId))
    const components = activeConfig(state.setup.salaryComponents)
    const assignedStructureIds = new Set(employees.map(row => refId(row.salaryStructureId)).filter(Boolean))
    const unassignedSalary = employees.filter(row => !String(row.salaryStructureId || '').trim()).length
    const withoutPayBasis = employees.filter(row => row.annualCtc <= 0 && !Object.keys(row.salaryComponents || {}).length && !String(row.salaryStructureId || '').trim())
    const invalidIdentity = employees.filter(row => !row.employeeCode?.trim() || !row.firstName?.trim())
    const invalidTemplates = employees.filter(row => String(row.salaryStructureId || '').trim() && !structures.some(template => String(template.id) === String(row.salaryStructureId)))
    const missingLocation = employees.filter(row => !row.workLocationId).length
    const attendanceGroups = active(state.attendanceGroups)
    const coveredIds = new Set(attendanceGroups.flatMap(row => row.employeeIds || []))
    const uncoveredEmployees = employees.filter(row => !coveredIds.has(row.id))
    const sample = (rows: Employee[]) => rows.slice(0, 8).map(row => row.employeeCode || `ID ${row.id}`).join(', ') + (rows.length > 8 ? ` and ${rows.length - 8} more` : '')
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
          !client?.code ? gap('Set the client code.', 'Client identification in imports and reporting may be incomplete.') : '',
          locations.length === 0 ? gap('Add and assign work locations.', 'State-based deductions and location reports need location data. Salary processing can continue, but review statutory results.', 'Review before payroll', '/settings/work-locations') : ''
        ].filter(Boolean)
      ),
      item('masters', 'Dropdown & Organization Masters', '/settings/dropdown-masters',
        [
          `${clientDrops.length} usable dropdown value${clientDrops.length === 1 ? '' : 's'} for global/client scope.`,
          `Departments: ${unique(employees.map(row => row.department)).length || 0}; Designations: ${unique(employees.map(row => row.designation)).length || 0}.`,
          `Work week options: ${clientDrops.filter(row => row.type === 'Work Week').length}.`
        ],
        [
          clientDrops.filter(row => row.type === 'Department').length === 0 && unique(employees.map(row => row.department)).length === 0 ? gap('Add departments and map employees.', 'Department reporting and GL allocation will be incomplete; this is a payroll warning.') : '',
          clientDrops.filter(row => row.type === 'Work Week').length === 0 ? gap('Review weekly-off choices in Dropdown Masters.', 'Attendance policy needs a valid weekly-off pattern. Existing valid policies and recorded attendance can still be used.', 'Review before payroll') : ''
        ].filter(Boolean)
      ),
      item('employees', 'Employees & Reporting', '/employees/master',
        [
          `${number(employees.length)} active employees.`,
          `${number(employees.filter(row => row.portalAccess).length)} employees have portal access enabled.`,
          `${number(employees.filter(row => row.reportingManagerUserId).length)} employees have workflow reporting user mapped.`,
          `${number(missingLocation)} employees have no work location.`
        ],
        [
          employees.length === 0 ? gap('Create or activate employees for this client.', 'Payroll cannot run when no eligible active employees exist.', 'Blocks payroll') : '',
          invalidIdentity.length > 0 ? gap(`Complete employee code and first name for ${invalidIdentity.length} employee(s): ${sample(invalidIdentity)}.`, 'The payroll validator rejects included employees with either field blank.', 'Blocks payroll') : '',
          missingLocation > 0 ? gap(`Assign work locations to ${missingLocation} employee(s).`, 'Payroll can calculate, but state-based statutory calculations and location reports may be incomplete.', 'Review before payroll') : '',
          employees.some(row => !row.reportingManagerUserId) ? gap('Review missing workflow approver mappings.', 'Workflows using a reporting user may have no approver. Employee hierarchy or specific-user workflows can resolve differently.') : ''
        ].filter(Boolean)
      ),
      item('payroll', 'Payroll Structure', '/settings/salary-templates',
        [
          `${components.length} active salary component${components.length === 1 ? '' : 's'} in the shared master.`,
          `${structures.length} active salary template${structures.length === 1 ? '' : 's'} for this client.`,
          `${assignedStructureIds.size} template${assignedStructureIds.size === 1 ? '' : 's'} currently assigned to employees.`,
          `${payslips.length} active payslip template${payslips.length === 1 ? '' : 's'}.`,
          ...structures.map(template => `${template.name}: ${template.lines.length} component(s). ${template.lines.map(line => {
            const component = state.setup.salaryComponents.find(row => String(row.id) === String(line.componentId))
            const calculation = line.calculationType || component?.calculationType || 'calculation not set'
            const value = line.formula || line.value || component?.formula || component?.value || 'value not set'
            const proRata = line.proRataOverride === 'true' || (line.proRataOverride !== 'false' && component?.proRata)
            return `${component?.name || line.componentId}: ${calculation}, ${value}, pro-rata ${yesNo(Boolean(proRata))}`
          }).join('; ')}. Verify attendance pro-rata and employee-specific amounts in Salary Simulation.`)
        ],
        [
          state.setup.salaryComponents.length === 0 ? gap('Create salary components.', 'The payroll engine cannot run without component masters.', 'Blocks payroll', '/settings/salary-components') : '',
          state.setup.salaryComponents.length > 0 && components.length === 0 ? gap('Activate the required salary components.', 'Inactive components cannot provide the intended earnings and deductions. Review the resulting pay before processing.', 'Review before payroll', '/settings/salary-components') : '',
          structures.length === 0 ? gap('Create a salary template if using template-based pay.', 'A template is not mandatory when employees already have annual CTC or individual salary components. Confirm those values and simulate pay.', 'Review before payroll') : '',
          payslips.length === 0 ? gap('Configure a payslip template.', 'This affects payslip presentation, not gross/net salary calculation.', 'Does not block payroll', '/settings/payslip-templates') : '',
          withoutPayBasis.length > 0 ? gap(`Set a salary basis for ${withoutPayBasis.length} employee(s): ${sample(withoutPayBasis)}.`, 'Each included employee needs annual CTC, individual salary components, or a salary template. All three are missing for these employees.', 'Blocks payroll', '/employees/master') : '',
          invalidTemplates.length > 0 ? gap(`Review unavailable/inactive template assignments for ${invalidTemplates.length} employee(s): ${sample(invalidTemplates)}.`, 'The assigned template is not an active template for this client. Confirm the intended components and simulate before running.', 'Review before payroll') : '',
          unassignedSalary > withoutPayBasis.length ? gap(`Review ${unassignedSalary - withoutPayBasis.length} employee(s) using CTC or individual components without a template.`, 'These employees pass the salary-basis presence check. Confirm the actual earnings, deductions and benefit amounts in simulation.', 'Review before payroll') : ''
        ].filter(Boolean)
      ),
      item('statutory', 'Compliance & Tax', '/settings/statutory-setup',
        [
          'Organization-level statutory settings below are shared defaults; verify employee eligibility and template overrides for this client.',
          `EPF enabled: ${yesNo(Boolean(state.setup.statutory.epf))}${state.setup.statutory.epfNumber ? ` (${state.setup.statutory.epfNumber})` : ''}.`,
          `ESI enabled: ${yesNo(Boolean(state.setup.statutory.esi))}${state.setup.statutory.esiNumber ? ` (${state.setup.statutory.esiNumber})` : ''}.`,
          `PT enabled: ${yesNo(Boolean(state.setup.statutory.pt))}; LWF enabled: ${yesNo(Boolean(state.setup.statutory.lwf))}.`,
          `${state.setup.tax.clientSettings.filter(row => refId(row.clientId) === clientId && row.active).length} active client tax setting(s).`
        ],
        [
          state.setup.statutory.epf && !state.setup.statutory.epfNumber ? gap('Enter the EPF registration number.', 'EPF reporting needs this registration. Its absence does not block the engine calculating salary.') : '',
          state.setup.statutory.esi && !state.setup.statutory.esiNumber ? gap('Enter the ESI registration number.', 'ESI reporting needs this registration. Its absence does not block salary calculation.') : '',
          state.setup.statutory.pt && !state.setup.statutory.ptStateSlabs?.length ? gap('Review Professional Tax slabs.', 'PT is enabled but state slabs are absent. Confirm any applicable fallback and deduction amounts before finalizing payroll.', 'Review before payroll') : '',
          !state.setup.tax.slabs?.length ? gap('Review tax slabs and client tax settings.', 'Automatic income-tax calculation may be incomplete where applicable. Confirm tax applicability and configured deductions.', 'Review before payroll', '/settings/tax-engine') : ''
        ].filter(Boolean)
      ),
      item('attendance', 'Shift & Attendance Policy', '/settings/leave-attendance/attendance-policies',
        [
          `${active(state.shifts).length} active shift${active(state.shifts).length === 1 ? '' : 's'} configured.`,
          ...active(state.shifts).map(row => `${row.shiftName}: ${row.shiftType}, ${row.startTime || 'flexible start'} to ${row.endTime || 'flexible end'}${row.isOvernight ? ' next day (attendance belongs to shift start day)' : ''}; full day ${row.minimumFullDayHours} hrs, half day ${row.minimumHalfDayHours} hrs, grace ${row.graceMinutes} min, break ${row.breakMinutes} min; effective ${row.effectiveFrom.slice(0, 10)} to ${row.effectiveTo?.slice(0, 10) || 'open-ended'}.`),
          `${attendanceGroups.length} attendance polic${attendanceGroups.length === 1 ? 'y' : 'ies'} configured.`,
          state.attendanceSettings ? `Attendance engine: ${state.attendanceSettings.checkInTime} to ${state.attendanceSettings.checkOutTime}, full day ${state.attendanceSettings.minimumHoursForFullDay} hrs, half day ${state.attendanceSettings.minimumHoursForHalfDay} hrs.` : 'Attendance engine settings not found.',
          ...attendanceGroups.map(row => `${row.name}: ${row.employeeCount} employees, weekly off ${row.workWeek}, attendance cycle day ${row.attendanceCycleStartDay}-${row.attendanceCycleEndDay}, payroll report day ${row.payrollReportGenerationDay}${row.shiftName ? `, shift ${row.shiftName}` : ''}. This controls the attendance period and employees available in policy-based payroll.`)
        ],
        [
          active(state.shifts).length === 0 ? gap('Create shifts if using punch-based attendance.', 'Shift timing drives punch interpretation and present/half-day rules. Reviewed monthly attendance can support payroll without a named shift.', 'Review before payroll', '/settings/leave-attendance/shift-master') : '',
          attendanceGroups.length === 0 ? gap('Create an attendance policy and assign employees.', 'Policy-based payroll needs a policy with mapped employees. Payroll without a policy filter still requires valid attendance for each included employee.', 'Review before payroll') : '',
          uncoveredEmployees.length > 0 ? gap(`Assign ${uncoveredEmployees.length} uncovered employee(s): ${sample(uncoveredEmployees)}.`, 'They will not be included when running payroll for a policy they are not mapped to. Verify inclusion and attendance before processing.', 'Review before payroll') : '',
          !state.attendanceSettings?.id ? gap('Save client attendance engine settings.', 'The displayed timing may be system defaults. Confirm working-hour thresholds and grace rules before generating attendance.', 'Review before payroll', '/settings/leave-attendance/attendance') : ''
        ].filter(Boolean)
      ),
      item('leave', 'Leave & Holiday', '/settings/leave-attendance/leave-types',
        [
          `${leaveTypes.length} active leave type${leaveTypes.length === 1 ? '' : 's'}.`,
          `${state.holidays.length} holiday${state.holidays.length === 1 ? '' : 's'} for ${todayYear}.`,
          ...leaveTypes.map(row => `${row.name}: ${row.entitlement} ${row.entitlementPeriod.toLowerCase()}, ${row.type}, ${row.applicabilityMode}. Controls leave entitlement and eligibility; approved leave must be reflected in payable days.`)
        ],
        [
          leaveTypes.length === 0 ? gap('Configure leave types if employees need leave.', 'Leave balances and applications will be unavailable. Payroll can use reviewed payable days; verify paid/unpaid leave treatment.', 'Review before payroll') : '',
          state.holidays.length === 0 ? gap(`Add the ${todayYear} holiday calendar.`, 'Automatic attendance cannot identify holidays from an empty calendar. Verify holiday pay in attendance totals before payroll.', 'Review before payroll', '/settings/leave-attendance/holiday') : ''
        ].filter(Boolean)
      ),
      item('travel', 'Travel & Expense', '/settings/travel-and-expense-policies',
        [
          `Module enabled for client: ${yesNo(Boolean(travelSetting?.isEnabled))}.`,
          `Travel desk: ${yesNo(Boolean(travelSetting?.isTravelDeskEnabled))}; trip/accommodation/local tabs: ${yesNo(Boolean(travelSetting?.showTripDetails))}/${yesNo(Boolean(travelSetting?.showAccommodationDetails))}/${yesNo(Boolean(travelSetting?.showLocalTravelDetails))}.`,
          `${travelPolicies.length} policy header(s), ${travelAssignments.length} assignment(s), ${travelRules.length} rule(s), ${travelCategories.length} claim category rows.`,
          ...travelRules.map(row => `${row.policyName} - ${row.ruleType}: ${row.ruleName}, ${row.isAllowed ? 'allowed' : 'blocked'}, limit ${row.limitAmount ?? 'not set'}, exception ${row.exceptionHandling}.`)
        ],
        [
          travelSetting?.isEnabled && travelPolicies.length === 0 ? gap('Create a travel policy.', 'Travel eligibility and expense limits cannot be determined from a missing policy. Regular salary can still run.') : '',
          travelSetting?.isEnabled && travelAssignments.length === 0 ? gap('Assign travel policies to eligible employees.', 'Employees may have no applicable travel policy. Check assignments before approving travel or expense claims.') : '',
          travelSetting?.isEnabled && travelRules.length === 0 ? gap('Configure travel limits and eligibility rules.', 'A policy header alone does not define allowed travel or expense limits.') : '',
          travelSetting?.isEnabled && travelCategories.length === 0 ? gap('Enable claim headers and categories.', 'Employees cannot enter the required claim categories. Reconcile approved reimbursements separately before payroll.') : ''
        ].filter(Boolean), !travelSetting?.isEnabled
      ),
      item('billing', 'Client Billing', '/settings/client-billing-configuration',
        [
          `Billing module enabled: ${yesNo(state.billingModule.isEnabled)}; advanced costing: ${yesNo(state.billingModule.advancedCostingEnabled)}.`,
          `${billingHeaders.length} contract/rule header(s), ${billingLines.length} billable line(s), ${billingRows.length} legacy billing row(s).`,
          ...billingHeaders.map(header => `${header.ruleName}: ${header.workLocationName || 'All locations'}, GST ${header.gstRatePercent}%, ${billingLines.filter(line => line.headerId === header.id).length} billable line(s).`),
          ...billingLines.map(line => `${line.ruleName} - ${line.lineType}/${line.matchValue}: ${line.billingTreatment}, ${line.rateType} ${line.rateValue}, GST ${yesNo(line.taxApplicable)}.`)
        ],
        [
          state.billingModule.isEnabled && state.billingModule.advancedCostingEnabled && billingHeaders.length === 0 ? gap('Create client billing contracts.', 'Advanced billing needs contracts to determine billable salary, service charges and GST. Payroll itself can run.') : '',
          state.billingModule.isEnabled && state.billingModule.advancedCostingEnabled && billingLines.length === 0 ? gap('Add billable contract lines.', 'Advanced invoices cannot include the required charges without billing lines. Gross/net employee pay is unaffected.') : '',
          state.billingModule.isEnabled && !state.billingModule.advancedCostingEnabled && billingRows.length === 0 ? gap('Configure billing rates or enable contract-based costing.', 'No billing configuration was found for the active billing mode. This affects invoices, not salary calculation.') : ''
        ].filter(Boolean), !state.billingModule.isEnabled
      ),
      item('recruitment', 'Recruitment', '/recruitment/dashboard',
        [
          `Recruitment enabled: ${yesNo(Boolean(recruitmentSetting?.recruitmentEnabled))}.`,
          `Employee RFR: ${yesNo(Boolean(recruitmentSetting?.allowEmployeeRfrCreation))}; vendor/consultant/internal/referral: ${yesNo(Boolean(recruitmentSetting?.enableVendorHiring))}/${yesNo(Boolean(recruitmentSetting?.enableConsultantHiring))}/${yesNo(Boolean(recruitmentSetting?.enableInternalHiring))}/${yesNo(Boolean(recruitmentSetting?.enableReferralHiring))}.`,
          `${recruitmentMasters.length} master values, ${recruitmentRules.length} assignment rules, ${state.recruitment.slaRules.filter(row => row.clientId === clientId && row.isActive).length} SLA rules.`,
        ],
        [
          recruitmentSetting?.recruitmentEnabled && recruitmentMasters.length === 0 ? gap('Review centralized recruitment dropdown values.', 'Recruitment form choices may be unavailable. Check Dropdown Masters before adding values; existing employee payroll is unaffected.', 'Does not block payroll', '/settings/dropdown-masters') : '',
          recruitmentSetting?.recruitmentEnabled && recruitmentRules.length === 0 ? gap('Configure recruiter assignment rules if automatic routing is needed.', 'Recruitment requests may need manual assignment. Existing employee payroll is unaffected.') : ''
        ].filter(Boolean), !recruitmentSetting?.recruitmentEnabled
      ),
      item('notifications', 'Notifications & Jobs', '/security/apps/notifications',
        [
          `SMTP enabled: ${yesNo(Boolean(state.notifications.smtp.isEnabled))}; paused: ${yesNo(Boolean(state.notifications.smtp.deliveryPaused))}.`,
          `${state.notifications.templates.filter(row => row.isActive).length} active template(s), ${notificationRules.length} enabled rule(s) for global/client scope.`,
          `${state.jobs.filter(row => row.isEnabled).length} scheduled job(s) enabled.`
        ],
        [
          !state.notifications.smtp.isEnabled ? gap('Enable SMTP when email delivery is required.', 'Queued welcome emails, payslips and approval alerts will not be delivered. Payroll calculation remains available.') : '',
          state.notifications.smtp.deliveryPaused ? gap('Resume email delivery when ready.', 'Email delivery is paused. Salary calculation can continue while messages remain queued.') : '',
          state.notifications.templates.filter(row => row.isActive).length === 0 ? gap('Create notification templates.', 'Required emails cannot be composed from an inactive or missing template.') : '',
          notificationRules.length === 0 ? gap('Enable rules for required notification events.', 'Automatic event emails may not be queued. This does not prevent salary calculation.') : ''
        ].filter(Boolean)
      )
    ].map(row => {
      const errors = (sources[row.key] || []).filter(key => state.failures[key])
      if (errors.length) return { ...row, status: 'Unavailable' as const, summary: 'Could not verify this section', missing: [], details: errors.map(key => `${key}: ${state.failures[key]}. Check access/connectivity, then run the check again.`) }
      const coverageKeys: Record<string, string[]> = { client: ['location-coverage'], employees: ['manager-coverage', 'bank-coverage', 'portal-coverage'], payroll: ['salary-coverage'], attendance: ['attendance-coverage', 'shift-coverage'], leave: ['leave-coverage', 'holiday-coverage'], travel: ['travel-coverage'], notifications: ['email-coverage'] }
      const gaps = coverage.filter(check => coverageKeys[row.key]?.includes(check.key) && !check.unavailable && !check.disabled).flatMap(check => {
        const uncovered = check.results.filter(employee => employee.status === 'Uncovered').length
        const review = check.results.filter(employee => employee.status === 'Review').length
        return uncovered || review ? [gap(`${check.title}: ${uncovered} uncovered; ${review} need review.`, 'Open the employee counts in Employee coverage for the complete list, reasons and assignments.', ['payroll', 'attendance', 'leave'].includes(row.key) ? 'Review before payroll' : 'Does not block payroll', '#employee-coverage')] : []
      })
      return gaps.length ? { ...row, missing: [...row.missing, ...gaps], status: row.status === 'Configured' ? 'Attention' as const : row.status, summary: 'Employee coverage gaps need attention' } : row
    })
  }, [state, clientId, coverage])

  const blockers = checks.flatMap(row => row.missing.filter(finding => finding.payroll === 'Blocks payroll').map(finding => ({ ...finding, route: finding.route || row.route })))
  const reviewCount = checks.flatMap(row => row.missing).filter(row => row.payroll === 'Review before payroll').length
  const unavailable = checks.filter(row => row.status === 'Unavailable').length
  const selectedClient = clients.find(row => row.id === clientId)

  const renderCheck = (row: CheckItem) => <section id={`config-${row.key}`} className="config-check-section">
    <div className="config-section-heading"><span>{row.summary}</span><Link to={row.route}>Open setup</Link></div>
    <Row gutter={[14, 14]}>
      <Col xs={24} lg={row.missing.length ? 14 : 24}>
        <h4>{row.status === 'Unavailable' ? 'Unable to verify' : 'Configuration and meaning'}</h4>
        <ul>{row.details.map((detail, index) => <li key={`${row.key}-detail-${index}`}>{detail}</li>)}</ul>
      </Col>
      {row.missing.length > 0 && <Col xs={24} lg={10}>
        <h4>Impact and next action</h4>
        <ul className="config-findings">{row.missing.map((finding, index) => <li key={`${row.key}-missing-${index}`}>
          <Tag color={finding.payroll === 'Blocks payroll' ? 'error' : finding.payroll === 'Review before payroll' ? 'warning' : 'blue'}>{finding.payroll}</Tag>
          <strong>{finding.action}</strong><p>{finding.impact}</p>{finding.route?.startsWith('#') ? <Button type="link" className="config-coverage-link" onClick={() => openTab('coverage')}>View employee coverage</Button> : <Link to={finding.route || row.route}>Review configuration</Link>}
        </li>)}</ul>
      </Col>}
    </Row>
  </section>

  return <section className="client-config-check">
    <div className="config-check-toolbar">
      <div>
        <strong>{selectedClient ? selectedClient.name : 'Client configuration'}</strong>
        {state && <small>Checked {state.checkedAt}</small>}
      </div>
      <Space wrap>
        <Select aria-label="Client" disabled={loading} className="config-check-client" showSearch optionFilterProp="label" value={clientId || undefined} placeholder="Select client" onChange={value => { setClientId(Number(value)); setState(null) }} options={clients.map(client => ({ value: client.id, label: `${client.name} (${client.code || 'No code'})` }))} />
        <Button type="primary" disabled={!clientId} icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>Run check</Button>
      </Space>
    </div>
    {clientError && <Alert showIcon type="error" message="Clients unavailable" description={clientError} />}

    {loading ? <div role="status"><Spin /> Checking configuration...</div> : !state ? <Empty description={clientId ? 'Configuration not checked. Click Run check.' : 'Select a client to run configuration check.'} /> : <>
      <Alert showIcon type={blockers.length ? 'error' : 'warning'}
        message={blockers.length ? `${blockers.length} configuration blocker(s) for payroll` : unavailable ? 'Configuration check incomplete' : 'No configuration blockers detected; pay-period validation still required'}
        description="This checks saved configuration for all active client employees. A payroll run validates its selected employees and pay period separately. Configured does not certify calculation accuracy or payroll readiness." />
      <div className="config-check-totals"><span><strong>{blockers.length}</strong> payroll blockers</span><span><strong>{reviewCount}</strong> payroll reviews</span><span><strong>{unavailable}</strong> unverified sections</span></div>
      <div ref={tabsRef} className="config-check-tabs">
      <Tabs activeKey={activeTab} onChange={key => openTab(key)} destroyInactiveTabPane items={[
        { key: 'summary', label: 'Summary', children: <>
          {blockers.length > 0 && <ul className="config-blocker-list">{blockers.map((finding, index) => <li key={index}><Link to={finding.route}>{finding.action}</Link><p>{finding.impact}</p></li>)}</ul>}
          <div className="config-summary-grid">
            {checks.map(row => <button type="button" onClick={() => openTab(checkGroups.find(group => group.checks.includes(row.key))!.key, row.key)} key={row.key}><Tag color={statusColor(row.status)}>{row.status}</Tag><strong>{row.title}</strong><span>{row.summary}</span></button>)}
          </div>
          <Collapse ghost className="config-minimum-accordion">
          <Collapse.Panel key="minimum" header="Minimum requirements for regular payroll">
          <section className="config-payroll-minimum" aria-label="Minimum requirements for payroll">
        <ol>
          <li><Link to="/employees/master">Eligible active employees</Link> with employee code and first name.</li>
          <li><Link to="/settings/salary-components">Salary component masters</Link> and a pay basis per employee: annual CTC, individual salary components, or a salary template.</li>
          <li><Link to="/attendance">Valid attendance for the pay period</Link>. Complete payable days, resolve weekly-off exceptions and ensure the attendance cycle is at most 31 days. A policy-filtered run needs mapped employees. <Tag>Not checked for a month</Tag></li>
          <li><Link to="/settings/salary-templates">Valid formulas and salary simulation</Link>. Resolve missing tokens, invalid dependencies and calculation errors, then verify gross, deductions, benefits and net pay. <Tag>Simulation required</Tag></li>
          <li>Check the pay-run employee selection and existing runs so employees are not already covered for the same client and month. <Tag>Run-time check</Tag></li>
        </ol>
        <p>Missing PAN, UAN, bank/IFSC, payslip layout, email delivery, recruitment or client billing setup does not by itself block salary calculation. Bank payment and reporting still need their respective details. Applicable statutory deductions and payable days must be reviewed before finalizing.</p>
      </section>
          </Collapse.Panel>
          </Collapse>
        </> },
        { key: 'coverage', label: 'Employee coverage', children: <ClientEmployeeCoverage checks={coverage} asOf={state.checkedOn} employeeDataAvailable={!state.failures.employees}
          total={state.employees.filter(row => row.clientId === clientId).length}
          active={state.employees.filter(row => row.clientId === clientId && row.isActive).length} /> },
        ...checkGroups.map(group => {
          const rows = checks.filter(row => group.checks.includes(row.key))
          const attention = rows.filter(row => ['Blocked', 'Attention', 'Unavailable'].includes(row.status)).length
          return { key: group.key, label: <span>{group.label}{attention > 0 && <span className="config-tab-count" aria-label={`${attention} sections need attention`}>{attention}</span>}</span>, children:
            <Collapse ghost accordion activeKey={expandedCheck} onChange={key => setExpandedCheck(Array.isArray(key) ? String(key[0] || '') : String(key))}>
              {rows.map(row => <Collapse.Panel key={row.key} header={<span className="config-accordion-heading"><strong>{row.title}</strong><Tag color={statusColor(row.status)} icon={statusIcon(row.status)}>{row.status}</Tag></span>}>
                {renderCheck(row)}
              </Collapse.Panel>)}
            </Collapse> }
        })
      ]} />
      </div>
    </>}
  </section>
}
