import { useEffect, useMemo, useState } from 'react'
import { Select } from 'antd'
import { getClients, getEmployees, getPayRuns } from '../services/payrollService'
import { runReport, type ReportResult } from '../services/reportingService'
import { getEmployeeManagerUsers, getSetup, getWorkLocations } from '../services/settingsService'
import { setup0 } from '../data/payrollDefaults'
import type { Client, Component, Employee, PayRun, Structure, WorkflowApprover, WorkLocation } from '../types/payroll'
import type { reportingMenus } from '../data/payrollDefaults'
import PayslipRegister from '../components/PayslipRegister'
import DataTable, { type Column } from '../components/DataTable'
import SearchSelect from '../components/SearchSelect'
import { downloadXlsx } from '../utils/xlsx'

export type ReportingMenu = (typeof reportingMenus)[number]
export type ReportDefinition = { name: string; code?: string }
type ReportRow = Record<string, string | number | boolean | null>
type EmployeeExportGroup = 'table' | 'all' | '0001' | '0002' | '0006' | '0008' | '0009'
const standardEmployeeInfotypes: Exclude<EmployeeExportGroup, 'table' | 'all'>[] = ['0001', '0002', '0006', '0008', '0009']
const employeeExportOptions = [
  { value: 'all', label: 'All infotypes' },
  { value: 'table', label: 'Table view' },
  { value: '0001', label: '0001 — Organization, department, designation, grade, location, manager, email' },
  { value: '0002', label: '0002 — Personal, mobile, PAN, Aadhaar, UAN, ESIC' },
  { value: '0006', label: '0006 — Addresses' },
  { value: '0008', label: '0008 — Salary template, Annual CTC' },
  { value: '0009', label: '0009 — Bank details' }
] satisfies { value: EmployeeExportGroup; label: string }[]
const catalogue = {
  'Payroll Reports': [{ name: 'Salary Register', code: 'salary-register' }, { name: 'Payslip Register', code: 'payslip-register' }, { name: 'Payroll Summary', code: 'payroll-summary' }, { name: 'Component Ledger', code: 'component-ledger' }, { name: 'Monthly Advice Report', code: 'monthly-advice-report' }, { name: 'Department Payroll Cost', code: 'headcount' }, { name: 'Location Payroll Cost', code: 'location-cost' }, { name: 'Employee Wise Salary', code: 'salary-register' }, { name: 'Net Pay Report', code: 'net-pay-estimate' }, { name: 'Bank Transfer Report', code: 'bank-transfer-report' }],
  'Client Billing Report': [{ name: 'Payrun Billing Basis', code: 'client-billing-report' }, { name: 'Payroll Cost Report', code: 'payroll-cost-report' }],
  'Employee Reports': [
    { name: 'Employee Master Report', code: 'employee-master' },
    { name: 'Employee Directory', code: 'employee-master' },
    { name: 'Active Employees', code: 'employee-master' },
    // Hidden for now; enable later when needed:
    // { name: 'New Joiners', code: 'new-joiners' },
    { name: 'Employee Tenure Report', code: 'tenure' },
    // { name: 'Employee Demographics' }
  ],
  'Attendance Reports': [{ name: 'Daily Attendance', code: 'daily-attendance' }, { name: 'Monthly Attendance', code: 'monthly-attendance' }, { name: 'Late Coming Report', code: 'attendance-exception' }, { name: 'Attendance Exception Report', code: 'attendance-exception' }, { name: 'Attendance Trend Analysis', code: 'attendance-trend' }],
  'Leave Reports': [{ name: 'Leave Balance Report', code: 'leave-balance' }, { name: 'Leave Accrual Report', code: 'leave-accrual' }, { name: 'Leave Utilization Report', code: 'leave-utilization' }, { name: 'Leave Without Pay Report', code: 'lwp-balance' }, { name: 'Leave Approval Status', code: 'leave-approval-status' }],
  'Recruitment Reports': [{ name: 'Open Requisitions' }, { name: 'Recruitment Funnel' }, { name: 'Candidate Pipeline' }, { name: 'Time To Hire' }, { name: 'Cost Per Hire' }],
  'Onboarding Reports': [{ name: 'Joining Tracker' }, { name: 'Documentation Status' }, { name: 'Induction Completion' }, { name: 'Pending Onboarding Tasks' }],
  'Separation Reports': [{ name: 'Resignation Report' }, { name: 'Attrition Report' }, { name: 'Full & Final Tracker' }, { name: 'Notice Period Tracker' }],
  'Compliance Reports': [{ name: 'PF Report', code: 'pf-register' }, { name: 'PF ECR Report', code: 'pf-ecr-report' }, { name: 'ESI Report', code: 'esi-register' }, { name: 'PT Report', code: 'pt-register' }, { name: 'Statutory Deduction Summary', code: 'statutory-summary' }],
  'Tax Reports': [{ name: 'TDS Register', code: 'tds-register' }, { name: 'Employee Tax Projection' }, { name: 'Form 16 Register' }, { name: 'Tax Liability Report', code: 'tds-register' }],
  'Loan & Advance Reports': [{ name: 'Loan Register' }, { name: 'Loan Outstanding Report' }, { name: 'EMI Recovery Report' }, { name: 'Salary Advance Report' }],
  'Cost Center Reports': [{ name: 'Cost Center Salary Cost' }, { name: 'Cost Center Headcount' }, { name: 'Cost Center Variance' }, { name: 'Cost Allocation Report' }],
  'Department Reports': [{ name: 'Department Headcount', code: 'headcount' }, { name: 'Department Payroll Cost', code: 'headcount' }, { name: 'Department Attrition' }, { name: 'Department Attendance' }],
  'Location Reports': [{ name: 'Location Headcount' }, { name: 'Location Payroll Cost', code: 'location-cost' }, { name: 'Location Attendance' }, { name: 'Location Compliance Dashboard' }],
  'Contractor Reports': [{ name: 'Contractor Headcount' }, { name: 'Contractor Attendance' }, { name: 'Contractor Wage Cost' }, { name: 'Contractor Billing Report' }],
  'Audit Reports': [{ name: 'User Activity Report' }, { name: 'Login History' }, { name: 'Data Change Log' }, { name: 'Payroll Process Audit' }],
  'MIS Reports': [{ name: 'Monthly HR MIS' }, { name: 'Monthly Payroll MIS' }, { name: 'Workforce Summary' }, { name: 'Executive Summary' }],
  'Executive Dashboards': [{ name: 'CEO Dashboard' }, { name: 'CFO Dashboard' }, { name: 'CHRO Dashboard' }],
  'Scheduled Reports': [{ name: 'Daily Reports' }, { name: 'Weekly Reports' }, { name: 'Monthly Reports' }, { name: 'Delivery Configuration' }],
  'Report Builder': [{ name: 'Ad-hoc Report Builder' }, { name: 'Saved Report Layouts' }, { name: 'Shared Reports' }]
} satisfies Record<string, ReportDefinition[]>
export const reportItems = (menu: ReportingMenu) => catalogue[menu] ?? []
export default function ReportingPage({ activeMenu, activeReport }: { activeMenu: ReportingMenu; activeReport: ReportDefinition }) {
  if (activeReport.code === 'payslip-register') return <PayslipRegister />
  const [clients, setClients] = useState<Client[]>([]), [clientId, setClientId] = useState(0), [result, setResult] = useState<ReportResult>({ title: '', columns: [], rows: [] })
  const [payRuns, setPayRuns] = useState<PayRun[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [components, setComponents] = useState<Component[]>([])
  const [locations, setLocations] = useState<WorkLocation[]>([]), [managerUsers, setManagerUsers] = useState<WorkflowApprover[]>([]), [salaryStructures, setSalaryStructures] = useState<Structure[]>([])
  const [employeeExportGroups, setEmployeeExportGroups] = useState<EmployeeExportGroup[]>(['table'])
  const [employeeDataReady, setEmployeeDataReady] = useState(false), [employeeLookupReady, setEmployeeLookupReady] = useState(false)
  const [payRunId, setPayRunId] = useState(0), [employeeId, setEmployeeId] = useState(0), [componentCode, setComponentCode] = useState('')
  const [month, setMonth] = useState(new Date().toISOString().slice(0, 7)), [fromDate, setFromDate] = useState(`${new Date().toISOString().slice(0, 7)}-01`), [toDate, setToDate] = useState(new Date().toISOString().slice(0, 10))
  const periodCodes = ['daily-attendance', 'attendance-trend', 'leave-utilization', 'leave-approval-status']
  const monthCodes = ['monthly-attendance', 'attendance-exception', 'salary-register', 'component-ledger', 'pf-register', 'pf-ecr-report', 'esi-register', 'pt-register', 'tds-register', 'statutory-summary', 'client-billing-report', 'payroll-cost-report', 'monthly-advice-report', 'bank-transfer-report']
  const payRunCodes = ['salary-register', 'component-ledger', 'pf-register', 'pf-ecr-report', 'esi-register', 'pt-register', 'tds-register', 'statutory-summary', 'client-billing-report', 'payroll-cost-report', 'monthly-advice-report', 'bank-transfer-report']
  const employeeCodes = ['salary-register', 'component-ledger', 'pf-register', 'pf-ecr-report', 'esi-register', 'pt-register', 'tds-register', 'statutory-summary']
  const showPeriod = !!activeReport.code && periodCodes.includes(activeReport.code)
  const showMonth = !!activeReport.code && monthCodes.includes(activeReport.code)
  const showPayRun = !!activeReport.code && payRunCodes.includes(activeReport.code)
  const showEmployee = !!activeReport.code && employeeCodes.includes(activeReport.code)
  const showComponent = activeReport.code === 'component-ledger'
  const isEmployeeMaster = activeReport.code === 'employee-master' && activeReport.name === 'Employee Master Report'
  const employeeExportReady = employeeDataReady && employeeLookupReady
  const clientPayRuns = useMemo(() => payRuns.filter(run => run.clientId === clientId).sort((a, b) => b.id - a.id), [clientId, payRuns])
  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [clientId, employees])
  const componentOptions = useMemo(() => [
    { value: '', label: 'All components' },
    { value: 'Earning', label: 'All earnings' },
    { value: 'Deduction', label: 'All deductions' },
    { value: 'Benefit', label: 'All benefits' },
    { value: 'Reimbursement', label: 'All reimbursements' },
    ...components.filter(component => component.active).map(component => ({ value: component.code, label: `${component.code} - ${component.name}` }))
  ], [components])
  useEffect(() => {
    void Promise.all([getClients(), getPayRuns(), getEmployees(), getSetup(setup0)]).then(([clientRows, runRows, employeeRows, setup]) => {
      const active = clientRows.filter(x => x.isActive)
      setClients(active)
      setClientId(current => current || active[0]?.id || 0)
      setPayRuns(runRows)
      setEmployees(employeeRows)
      setComponents(setup.salaryComponents ?? [])
      setSalaryStructures(setup.salaryStructures ?? [])
      setEmployeeDataReady(true)
    })
  }, [])
  useEffect(() => {
    if (!isEmployeeMaster) { setEmployeeLookupReady(false); return }
    setEmployeeLookupReady(false)
    void Promise.all([getWorkLocations(), getEmployeeManagerUsers()]).then(([locationRows, managerRows]) => {
      setLocations(locationRows)
      setManagerUsers(managerRows)
      setEmployeeLookupReady(true)
    })
  }, [isEmployeeMaster])
  useEffect(() => {
    setEmployeeId(0)
    setComponentCode('')
    if (!showPayRun) {
      setPayRunId(0)
      return
    }
    const latestRun = clientPayRuns[0]
    setPayRunId(latestRun?.id ?? 0)
    if (latestRun?.payPeriod) setMonth(latestRun.payPeriod)
  }, [activeReport.code, clientId, showPayRun, clientPayRuns])
  useEffect(() => {
    setResult({ title: '', columns: [], rows: [] })
    if (clientId && activeReport.code) void runReport(activeReport.code, clientId, {
      month: showMonth ? month : undefined,
      fromDate: showPeriod ? fromDate : undefined,
      toDate: showPeriod ? toDate : undefined,
      payRunId: showPayRun ? payRunId : undefined,
      employeeId: showEmployee ? employeeId : undefined,
      componentCode: showComponent ? componentCode : undefined
    }).then(setResult)
  }, [clientId, activeReport, month, fromDate, toDate, payRunId, employeeId, componentCode, showMonth, showPeriod, showPayRun, showEmployee, showComponent])
  const reportColumns: Column<ReportRow>[] = result.columns.map(column => ({ key: column, label: column }))
  const changeEmployeeExportGroups = (values: EmployeeExportGroup[]) => {
    const added = values.find(value => !employeeExportGroups.includes(value))
    let next = values
    if (added === 'all') next = ['all']
    else if (added && standardEmployeeInfotypes.includes(added as Exclude<EmployeeExportGroup, 'table' | 'all'>)) next = values.filter(value => value !== 'all')
    setEmployeeExportGroups(next.length ? next : ['table'])
  }
  const exportEmployeeMaster = (visibleRows: ReportRow[]) => {
    const client = clients.find(row => row.id === clientId)
    const clientEmployeeByCode = new Map(employees
      .filter(row => row.clientId === clientId)
      .map(row => [row.employeeCode.trim().toLowerCase(), row]))
    const exportRecords = visibleRows.map(reportRow => ({
      reportRow,
      employee: clientEmployeeByCode.get(String(reportRow['Employee Code'] ?? '').trim().toLowerCase())
    }))
    const selected = employeeExportGroups.includes('all')
      ? standardEmployeeInfotypes
      : employeeExportGroups
    const locationById = new Map(locations.map(row => [row.id, row]))
    const managerUserById = new Map(managerUsers.map(row => [row.id, row]))
    const employeeById = new Map(employees.map(row => [row.id, row]))
    const salaryStructureById = new Map(salaryStructures.map(row => [String(row.id), row]))
    type ExportColumn = {
      key: string
      label: string
      read: (employee: Employee | undefined, reportRow: ReportRow) => unknown
    }
    const columns: ExportColumn[] = []
    const columnKeys = new Set<string>()
    const addColumn = (column: ExportColumn) => {
      if (columnKeys.has(column.key)) return
      columnKeys.add(column.key)
      columns.push(column)
    }
    const tableColumnKeys: Record<string, string> = {
      'Employee Code': 'employeeCode',
      Employee: 'employeeName',
      Department: 'department',
      Designation: 'designation',
      Location: 'workLocation',
      'Joining Date': 'joiningDate',
      Active: 'active'
    }

    // Identity columns stay first so every combined row remains traceable.
    addColumn({ key: 'employeeCode', label: 'Employee Code', read: (employee, row) => employee?.employeeCode ?? row['Employee Code'] })
    addColumn({ key: 'employeeName', label: 'Employee', read: (employee, row) => employee ? employeeName(employee) : row.Employee })

    if (selected.includes('table')) result.columns.forEach(label => addColumn({
      key: tableColumnKeys[label] ?? `table:${label.trim().toLowerCase()}`,
      label,
      read: (_, row) => row[label]
    }))
    if (selected.includes('0001')) {
      addColumn({ key: 'client', label: 'Client', read: () => client?.name ?? '' })
      addColumn({ key: 'department', label: 'Department', read: employee => employee?.department ?? '' })
      addColumn({ key: 'designation', label: 'Designation', read: employee => employee?.designation ?? '' })
      addColumn({ key: 'grade', label: 'Grade', read: employee => employee?.grade ?? '' })
      addColumn({ key: 'workLocation', label: 'Work Location', read: employee => employee ? locationById.get(employee.workLocationId)?.name ?? '' : '' })
      addColumn({
        key: 'reportingManager',
        label: 'Reporting Manager',
        read: employee => {
          if (!employee) return ''
          const managerUser = employee.reportingManagerUserId ? managerUserById.get(employee.reportingManagerUserId) : undefined
          const managerEmployee = employeeById.get(employee.reportingManagerId)
          return managerUser?.displayName || (managerEmployee ? employeeName(managerEmployee) : '')
        }
      })
      addColumn({
        key: 'managerEmail',
        label: 'Manager Email',
        read: employee => {
          if (!employee) return ''
          const managerUser = employee.reportingManagerUserId ? managerUserById.get(employee.reportingManagerUserId) : undefined
          const managerEmployee = employeeById.get(employee.reportingManagerId)
          return managerUser?.email || managerEmployee?.workEmail || ''
        }
      })
      addColumn({ key: 'workEmail', label: 'Work Email', read: employee => employee?.workEmail ?? '' })
      addColumn({ key: 'portalAccess', label: 'Portal Access', read: employee => employee ? yesNo(employee.portalAccess) : '' })
    }
    if (selected.includes('0002')) {
      addColumn({ key: 'firstName', label: 'First Name', read: employee => employee?.firstName ?? '' })
      addColumn({ key: 'lastName', label: 'Last Name', read: employee => employee?.lastName ?? '' })
      addColumn({ key: 'gender', label: 'Gender', read: employee => employee?.gender ?? '' })
      addColumn({ key: 'dateOfBirth', label: 'Date Of Birth', read: employee => employee?.personalDetails?.dateOfBirth ?? '' })
      addColumn({ key: 'mobile', label: 'Mobile', read: employee => employee?.personalDetails?.mobile ?? '' })
      addColumn({ key: 'panNumber', label: 'PAN', read: employee => employee?.personalDetails?.panNumber ?? '' })
      addColumn({ key: 'aadhaarNumber', label: 'Aadhaar', read: employee => employee?.personalDetails?.aadhaarNumber ?? '' })
      addColumn({ key: 'uanNumber', label: 'UAN Number', read: employee => employee?.personalDetails?.uanNumber ?? '' })
      addColumn({ key: 'esicNumber', label: 'ESIC Number', read: employee => employee?.personalDetails?.esicNumber ?? '' })
    }
    if (selected.includes('0006')) {
      addColumn({ key: 'address', label: 'Address', read: employee => employee?.personalDetails?.address ?? '' })
      addColumn({ key: 'correspondenceAddress', label: 'Correspondence Address', read: employee => employee?.personalDetails?.correspondenceAddress ?? '' })
      addColumn({ key: 'permanentAddress', label: 'Permanent Address', read: employee => employee?.personalDetails?.permanentAddress ?? '' })
    }
    if (selected.includes('0008')) {
      addColumn({ key: 'salaryTemplate', label: 'Salary Template', read: employee => employee ? salaryStructureById.get(String(employee.salaryStructureId))?.name ?? '' : '' })
      addColumn({ key: 'annualCtc', label: 'Annual CTC', read: employee => employee?.annualCtc ?? '' })
    }
    if (selected.includes('0009')) {
      addColumn({ key: 'bankName', label: 'Bank Name', read: employee => employee?.paymentDetails?.bankName ?? '' })
      addColumn({ key: 'bankAccountNo', label: 'Bank Account No', read: employee => employee?.paymentDetails?.bankAccountNo ?? '' })
      addColumn({ key: 'ifscCode', label: 'IFSC', read: employee => employee?.paymentDetails?.ifscCode ?? '' })
      addColumn({ key: 'paymentMode', label: 'Payment Mode', read: employee => employee?.paymentDetails?.paymentMode ?? '' })
    }

    downloadXlsx(`employee-master-${filePart(client?.code || client?.name || String(clientId))}.xlsx`, [{
      name: 'Employee Master',
      rows: [
        columns.map(column => column.label),
        ...exportRecords.map(({ employee, reportRow }) => columns.map(column => excelText(column.read(employee, reportRow))))
      ]
    }])
  }
  const employeeExportSelector = isEmployeeMaster ? <Select<EmployeeExportGroup[]>
    data-testid="employee-master-export-sections"
    className="employee-master-export-sections"
    mode="multiple"
    maxTagCount="responsive"
    maxTagTextLength={20}
    loading={!employeeExportReady}
    disabled={!employeeExportReady}
    value={employeeExportGroups}
    onChange={changeEmployeeExportGroups}
    options={employeeExportOptions}
    optionFilterProp="label"
    placeholder="Choose export data"
    aria-label="Employee infotypes to export"
  /> : undefined

  return <section className={`reporting-page${isEmployeeMaster ? ' employee-master-report' : ''}`}>
    <div className="card report-workspace">
      <header><div><span className="eyebrow purple">{activeMenu}</span><h3>{activeReport.name}</h3><p>{activeReport.code ? 'Client-scoped live report. Refine filters and export the current view.' : 'Report page is configured. It will become live when its source module is available.'}</p></div></header>
      {activeReport.code && <div className="report-filters">
        <label className="report-client"><span>Client</span><SearchSelect value={clientId} onChange={value => setClientId(Number(value))} options={clients.map(c => ({ value: c.id, label: c.name }))} /></label>
        {showMonth && <label className="report-client"><span>{['salary-register', 'component-ledger', 'monthly-advice-report', 'bank-transfer-report'].includes(activeReport.code) ? 'Pay Period' : 'Month'}</span><input type="month" value={month} onChange={e => setMonth(e.target.value)} /></label>}
        {showPayRun && <label className="report-client"><span>Payrun</span><SearchSelect value={payRunId} onChange={value => setPayRunId(Number(value))} options={[{ value: 0, label: 'Use selected month' }, ...clientPayRuns.map(run => ({ value: run.id, label: `${run.payPeriod} - ${run.runName || run.runType} - ${run.status}` }))]} /></label>}
        {showEmployee && <label className="report-client"><span>Employee</span><SearchSelect value={employeeId} onChange={value => setEmployeeId(Number(value))} options={[{ value: 0, label: 'All employees' }, ...clientEmployees.map(employee => ({ value: employee.id, label: `${employee.employeeCode} - ${employee.firstName} ${employee.lastName}` }))]} /></label>}
        {showComponent && <label className="report-client"><span>Component</span><SearchSelect value={componentCode} onChange={value => setComponentCode(String(value))} options={componentOptions} /></label>}
        {showPeriod && <label className="report-client"><span>From date</span><input type="date" value={fromDate} onChange={e => setFromDate(e.target.value)} /></label>}
        {showPeriod && <label className="report-client"><span>To date</span><input type="date" value={toDate} onChange={e => setToDate(e.target.value)} /></label>}
      </div>}
    </div>
    {activeReport.code ? <div className="card report-result"><DataTable<ReportRow>
      rows={result.rows}
      getRowId={(_, index) => index}
      exportFileName={activeReport.code}
      emptyText="No records for this client."
      columns={reportColumns}
      exportToolbar={employeeExportSelector}
      onExcelExport={isEmployeeMaster ? exportEmployeeMaster : undefined}
      exportDisabled={isEmployeeMaster && !employeeExportReady}
    /></div> : <div className="card report-empty"><p>Data source pending</p></div>}
  </section>
}

function employeeName(employee: Employee) { return `${employee.firstName} ${employee.lastName}`.trim() }
function excelText(value: unknown) { return value === null || value === undefined ? '' : String(value) }
function yesNo(value: boolean) { return value ? 'Yes' : 'No' }
function filePart(value: string) { return value.trim().replace(/[^a-z0-9_-]+/gi, '-').replace(/^-+|-+$/g, '').toLowerCase() || 'client' }
