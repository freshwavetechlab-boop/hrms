import { useEffect, useState } from 'react'
import { getClients } from '../services/payrollService'
import { runReport, type ReportResult } from '../services/reportingService'
import type { Client } from '../types/payroll'
import type { reportingMenus } from '../data/payrollDefaults'
import PayslipRegister from '../components/PayslipRegister'
import DataTable from '../components/DataTable'

export type ReportingMenu = (typeof reportingMenus)[number]
export type ReportDefinition = { name: string; code?: string }
const catalogue: Record<ReportingMenu, ReportDefinition[]> = {
  'Payroll Reports': [{ name: 'Salary Register', code: 'salary-register' }, { name: 'Payslip Register', code: 'payslip-register' }, { name: 'Payroll Summary', code: 'payroll-summary' }, { name: 'Department Payroll Cost', code: 'headcount' }, { name: 'Location Payroll Cost', code: 'location-cost' }, { name: 'Employee Wise Salary', code: 'salary-register' }, { name: 'Net Pay Report', code: 'net-pay-estimate' }, { name: 'Bank Transfer Report' }],
  'Employee Reports': [{ name: 'Employee Master Report', code: 'employee-master' }, { name: 'Employee Directory', code: 'employee-master' }, { name: 'Active Employees', code: 'employee-master' }, { name: 'New Joiners', code: 'new-joiners' }, { name: 'Employee Tenure Report', code: 'tenure' }, { name: 'Employee Demographics' }],
  'Attendance Reports': [{ name: 'Daily Attendance', code: 'daily-attendance' }, { name: 'Monthly Attendance', code: 'monthly-attendance' }, { name: 'Late Coming Report', code: 'attendance-exception' }, { name: 'Attendance Exception Report', code: 'attendance-exception' }, { name: 'Attendance Trend Analysis', code: 'attendance-trend' }],
  'Leave Reports': [{ name: 'Leave Balance Report', code: 'leave-balance' }, { name: 'Leave Accrual Report', code: 'leave-accrual' }, { name: 'Leave Utilization Report', code: 'leave-utilization' }, { name: 'Leave Without Pay Report', code: 'lwp-balance' }, { name: 'Leave Approval Status', code: 'leave-approval-status' }],
  'Recruitment Reports': [{ name: 'Open Requisitions' }, { name: 'Recruitment Funnel' }, { name: 'Candidate Pipeline' }, { name: 'Time To Hire' }, { name: 'Cost Per Hire' }],
  'Onboarding Reports': [{ name: 'Joining Tracker' }, { name: 'Documentation Status' }, { name: 'Induction Completion' }, { name: 'Pending Onboarding Tasks' }],
  'Separation Reports': [{ name: 'Resignation Report' }, { name: 'Attrition Report' }, { name: 'Full & Final Tracker' }, { name: 'Notice Period Tracker' }],
  'Compliance Reports': [{ name: 'PF Report', code: 'pf-register' }, { name: 'PF ECR Report' }, { name: 'ESI Report', code: 'esi-register' }, { name: 'PT Report' }, { name: 'Statutory Deduction Summary', code: 'net-pay-estimate' }],
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
}
export const reportItems = (menu: ReportingMenu) => catalogue[menu]
export default function ReportingPage({ activeMenu, activeReport }: { activeMenu: ReportingMenu; activeReport: ReportDefinition }) {
  if (activeReport.code === 'payslip-register') return <PayslipRegister />
  const [clients, setClients] = useState<Client[]>([]), [clientId, setClientId] = useState(0), [result, setResult] = useState<ReportResult>({ title: '', columns: [], rows: [] })
  const [month, setMonth] = useState(new Date().toISOString().slice(0, 7)), [fromDate, setFromDate] = useState(`${new Date().toISOString().slice(0, 7)}-01`), [toDate, setToDate] = useState(new Date().toISOString().slice(0, 10))
  const periodCodes = ['daily-attendance', 'attendance-trend', 'leave-utilization', 'leave-approval-status']
  const monthCodes = ['monthly-attendance', 'attendance-exception']
  const showPeriod = !!activeReport.code && periodCodes.includes(activeReport.code)
  const showMonth = !!activeReport.code && monthCodes.includes(activeReport.code)
  useEffect(() => { void getClients().then(rows => { const active = rows.filter(x => x.isActive); setClients(active); setClientId(current => current || active[0]?.id || 0) }) }, [])
  useEffect(() => { setResult({ title: '', columns: [], rows: [] }); if (clientId && activeReport.code) void runReport(activeReport.code, clientId, { month: showMonth ? month : undefined, fromDate: showPeriod ? fromDate : undefined, toDate: showPeriod ? toDate : undefined }).then(setResult) }, [clientId, activeReport, month, fromDate, toDate])
  return <section className="reporting-page"><div className="card report-workspace"><header><div><span className="eyebrow purple">{activeMenu}</span><h3>{activeReport.name}</h3><p>{activeReport.code ? 'Client-scoped live report. Refine filters and export the current view.' : 'Report page is configured. It will become live when its source module is available.'}</p></div></header>{activeReport.code && <div className="report-filters"><label className="report-client"><span>Client</span><select value={clientId} onChange={e => setClientId(Number(e.target.value))}>{clients.map(c => <option value={c.id} key={c.id}>{c.name}</option>)}</select></label>{showMonth && <label className="report-client"><span>Month</span><input type="month" value={month} onChange={e => setMonth(e.target.value)} /></label>}{showPeriod && <label className="report-client"><span>From date</span><input type="date" value={fromDate} onChange={e => setFromDate(e.target.value)} /></label>}{showPeriod && <label className="report-client"><span>To date</span><input type="date" value={toDate} onChange={e => setToDate(e.target.value)} /></label>}</div>}</div>{activeReport.code ? <div className="card report-result"><DataTable rows={result.rows} getRowId={(_, index) => index} exportFileName={activeReport.code} emptyText="No records for this client." columns={result.columns.map(column => ({ key: column, label: column }))} /></div> : <div className="card report-empty"><p>Data source pending</p></div>}</section>
}
