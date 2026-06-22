import { useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import AppIcon from './components/AppIcon'
import type { IconName } from './components/AppIcon'
import SecurityPanel from './components/SecurityPanel'
import { leaveAttendanceMenus, reportingMenus, securityMenus, settingsMenus, workflowMenus } from './data/payrollDefaults'
import EmployeePage from './pages/EmployeePage'
import LeaveAttendancePage from './pages/LeaveAttendancePage'
import type { LeaveAttendanceMenu } from './pages/LeaveAttendancePage'
import PayHistoryPage from './pages/PayHistoryPage'
import PayrollAttendancePage from './pages/PayrollAttendancePage'
import PayrollPage from './pages/PayrollPage'
import ReportingPage, { reportItems } from './pages/ReportingPage'
import type { ReportDefinition, ReportingMenu } from './pages/ReportingPage'
import WorkflowPage from './pages/WorkflowPage'
import type { WorkflowMenu } from './pages/WorkflowPage'
import WorkflowTasks from './components/WorkflowTasks'
import SettingsPage from './pages/SettingsPage'
import './OrganizationSetup.css'
import './ModuleDrawer.css'

type ModuleCode = 'Settings' | 'Employees' | 'Payroll' | 'Security' | 'LeaveAttendance' | 'Reports' | 'Workflows'
type SettingsTab = (typeof settingsMenus)[number]
type SecurityTab = (typeof securityMenus)[number]
type PayrollTab = 'Attendance Review' | 'Pay Run' | 'Adjustments'

const modules: { code: ModuleCode | 'Reports'; label: string; icon: IconName; description: string; disabled?: boolean }[] = [
  { code: 'Payroll', label: 'Payroll', icon: 'payruns', description: 'Run payroll, compare variances and review history.' },
  { code: 'LeaveAttendance', label: 'Leave & Attendance', icon: 'calendar', description: 'Configure leave, holidays, attendance and opening balances.' },
  { code: 'Employees', label: 'Employees', icon: 'employees', description: 'Manage employee master, salary and statutory profile.' },
  { code: 'Security', label: 'Security', icon: 'security', description: 'Control users, roles, permissions and audit evidence.' },
  { code: 'Workflows', label: 'Workflows', icon: 'reports', description: 'Configure reusable approval workflows and tasks.' },
  { code: 'Settings', label: 'Settings', icon: 'settings', description: 'Configure organization, clients and payroll setup.' },
  { code: 'Reports', label: 'Reports', icon: 'reports', description: 'Client-scoped reporting and analytics.' }
]

export default function SettingsApp() {
  const routeLocation = useLocation()
  const isPayHistory = routeLocation.pathname === '/pay-runs/history'
  const savedTab = localStorage.getItem('payroll.tab') as SettingsTab | null
  const savedModule = localStorage.getItem('payroll.module')
  const savedSecurityTab = localStorage.getItem('payroll.securityTab') as SecurityTab | null
  const savedLeaveAttendanceTab = localStorage.getItem('payroll.leaveAttendanceTab') as LeaveAttendanceMenu | null
  const savedReportingTab = localStorage.getItem('payroll.reportingTab') as ReportingMenu | null
  const savedWorkflowTab = localStorage.getItem('payroll.workflowTab') as WorkflowMenu | null
  const initialModule: ModuleCode = isPayHistory || savedModule === 'Payroll' || savedModule === 'Pay Runs' ? 'Payroll' : savedModule === 'Employees' ? 'Employees' : savedModule === 'Security' ? 'Security' : savedModule === 'LeaveAttendance' ? 'LeaveAttendance' : savedModule === 'Reports' ? 'Reports' : savedModule === 'Workflows' ? 'Workflows' : 'Settings'
  const [tab, setActiveTab] = useState<SettingsTab>(savedTab && settingsMenus.includes(savedTab) ? savedTab : 'Organization')
  const [navOpen, setNavOpen] = useState(true), [appDrawerOpen, setAppDrawerOpen] = useState(false), [showMyTasks, setShowMyTasks] = useState(false)
  const [securityTab, setSecurityTab] = useState<SecurityTab>(savedSecurityTab && securityMenus.includes(savedSecurityTab) ? savedSecurityTab : 'Users')
  const [payrollTab, setPayrollTab] = useState<PayrollTab>((localStorage.getItem('payroll.payrollTab') as PayrollTab | null) === 'Pay Run' ? 'Pay Run' : (localStorage.getItem('payroll.payrollTab') as PayrollTab | null) === 'Adjustments' ? 'Adjustments' : 'Attendance Review')
  const [leaveAttendanceTab, setLeaveAttendanceTab] = useState<LeaveAttendanceMenu>(savedLeaveAttendanceTab && leaveAttendanceMenus.includes(savedLeaveAttendanceTab) ? savedLeaveAttendanceTab : 'Preferences')
  const [reportingTab, setReportingTab] = useState<ReportingMenu>(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')
  const [workflowTab, setWorkflowTab] = useState<WorkflowMenu>(savedWorkflowTab && workflowMenus.includes(savedWorkflowTab) ? savedWorkflowTab : 'Workflow Setup')
  const [reportingReport, setReportingReport] = useState<ReportDefinition>(() => reportItems(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')[0])
  const [mainModule, setMainModule] = useState<ModuleCode>(initialModule), [settingsMessage, setSettingsMessage] = useState('Settings ready.')
  const activeModule = modules.find(module => module.code === mainModule)!
  const pageTitle = showMyTasks ? 'My Tasks' : mainModule === 'Settings' ? tab : mainModule === 'Employees' ? 'Employee Master' : mainModule === 'Security' ? securityTab : mainModule === 'LeaveAttendance' ? leaveAttendanceTab : mainModule === 'Reports' ? reportingTab : mainModule === 'Workflows' ? workflowTab : isPayHistory ? 'Pay History' : mainModule === 'Payroll' ? payrollTab : 'Pay Run'
  const pageDescription = mainModule === 'Payroll' ? isPayHistory ? 'All client-wise draft, approved and paid payroll runs.' : payrollTab === 'Attendance Review' ? 'Review client attendance, resolve discrepancies, then run payroll.' : payrollTab === 'Adjustments' ? 'Maintain one-time variable payments, recoveries and off-cycle payouts.' : 'Draft, review and approve monthly payroll.' : mainModule === 'Security' ? 'Manage identities, roles, permissions and audit evidence.' : mainModule === 'Employees' ? 'Maintain client-linked employee records, salary profiles and payment details.' : mainModule === 'LeaveAttendance' ? 'Setup leave types, holidays, attendance preferences and opening balances.' : settingsMessage

  const setTab = (nextTab: SettingsTab) => { localStorage.setItem('payroll.tab', nextTab); setActiveTab(nextTab) }
  const setModule = (nextModule: ModuleCode) => { setShowMyTasks(false); localStorage.setItem('payroll.module', nextModule); setMainModule(nextModule) }
  const setPayrollModuleTab = (nextTab: PayrollTab) => { localStorage.setItem('payroll.payrollTab', nextTab); setPayrollTab(nextTab); setModule('Payroll') }
  const setSecurityModuleTab = (nextTab: SecurityTab) => { localStorage.setItem('payroll.securityTab', nextTab); setSecurityTab(nextTab); setModule('Security') }
  const setLeaveAttendanceModuleTab = (nextTab: LeaveAttendanceMenu) => { localStorage.setItem('payroll.leaveAttendanceTab', nextTab); setLeaveAttendanceTab(nextTab); setModule('LeaveAttendance') }
  const setReportingModuleTab = (nextTab: ReportingMenu) => { localStorage.setItem('payroll.reportingTab', nextTab); setReportingTab(nextTab); setReportingReport(reportItems(nextTab)[0]); setModule('Reports') }
  const setWorkflowModuleTab = (nextTab: WorkflowMenu) => { localStorage.setItem('payroll.workflowTab', nextTab); setWorkflowTab(nextTab); setModule('Workflows') }
  const renderContextMenu = () => {
    const tasks = <button className={showMyTasks ? 'active' : ''} type="button" onClick={() => setShowMyTasks(true)}>My Tasks<small>Approvals</small></button>
    if (mainModule === 'Settings') return <>{tasks}{settingsMenus.map(item => <button className={tab === item ? 'active' : ''} type="button" onClick={() => setTab(item)} key={item}>{item}<small>{item === 'Salary Templates' ? 'Client-wise' : ''}</small></button>)}</>
    if (mainModule === 'Payroll') return <>{tasks}<Link className={!isPayHistory && payrollTab === 'Attendance Review' ? 'active' : ''} to="/" onClick={() => setPayrollModuleTab('Attendance Review')}>Attendance Review<small>Pre-payroll</small></Link><Link className={!isPayHistory && payrollTab === 'Pay Run' ? 'active' : ''} to="/" onClick={() => setPayrollModuleTab('Pay Run')}>Pay Run<small>Regular / off-cycle</small></Link><Link className={!isPayHistory && payrollTab === 'Adjustments' ? 'active' : ''} to="/" onClick={() => setPayrollModuleTab('Adjustments')}>Adjustments<small>Variable pay</small></Link><Link className={isPayHistory ? 'active' : ''} to="/pay-runs/history" onClick={() => setModule('Payroll')}>Pay History</Link></>
    if (mainModule === 'Security') return <>{tasks}{securityMenus.map(item => <button className={securityTab === item ? 'active' : ''} type="button" onClick={() => setSecurityModuleTab(item)} key={item}>{item}</button>)}</>
    if (mainModule === 'LeaveAttendance') return <>{tasks}{leaveAttendanceMenus.map(item => <button className={leaveAttendanceTab === item ? 'active' : ''} type="button" onClick={() => setLeaveAttendanceModuleTab(item)} key={item}>{item}</button>)}</>
    if (mainModule === 'Reports') return <>{tasks}{reportingMenus.map(item => <div className="report-nav-group" key={item}><button className={reportingTab === item ? 'active' : ''} type="button" onClick={() => setReportingModuleTab(item)}>{item}</button>{reportingTab === item && <div className="report-nav-submenu">{reportItems(item).map(report => <button className={reportingReport.name === report.name ? 'active' : ''} type="button" onClick={() => setReportingReport(report)} key={report.name}>{report.name}</button>)}</div>}</div>)}</>
    if (mainModule === 'Workflows') return workflowMenus.map(item => <button className={workflowTab === item ? 'active' : ''} type="button" onClick={() => setWorkflowModuleTab(item)} key={item}>{item}</button>)
    return <button className="active" type="button">Employee Master<small>Core HR</small></button>
  }
  const renderPage = () => {
    if (showMyTasks) return <WorkflowTasks />
    if (mainModule === 'Security') return <SecurityPanel initialTab={securityTab} />
    if (mainModule === 'LeaveAttendance') return <LeaveAttendancePage activeMenu={leaveAttendanceTab} onSelectMenu={setLeaveAttendanceModuleTab} />
    if (mainModule === 'Payroll') return isPayHistory ? <PayHistoryPage /> : payrollTab === 'Attendance Review' ? <PayrollAttendancePage /> : <PayrollPage key={payrollTab} mode={payrollTab === 'Adjustments' ? 'adjustments' : 'payrun'} />
    if (mainModule === 'Employees') return <EmployeePage />
    if (mainModule === 'Reports') return <ReportingPage activeMenu={reportingTab} activeReport={reportingReport} />
    if (mainModule === 'Workflows') return <WorkflowPage activeMenu={workflowTab} />
    return <SettingsPage tab={tab} onMessage={setSettingsMessage} />
  }

  return <div className={`payroll-app compact module-shell ${navOpen ? '' : 'nav-collapsed'} ${appDrawerOpen ? 'drawer-open' : ''}`}><aside className="app-sidebar"><div className="side-head"><a className="brand"><i>P</i><span>paymint</span></a><button className="sidebar-toggle" type="button" title={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-label={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} onClick={() => setNavOpen(open => !open)}><AppIcon name={navOpen ? 'collapse' : 'expand'} /></button></div><div className="sidebar-context"><span className="eyebrow purple">Current Module</span><strong>{activeModule.label}</strong><small>{activeModule.description}</small></div><nav><div className="submenu context-menu">{renderContextMenu()}</div></nav></aside><main className="compact-main"><header className="topbar"><div><span className="eyebrow purple">{mainModule} / {pageTitle}</span><h2>{pageTitle}</h2><p>{pageDescription}</p></div><div className="topbar-actions"><button className="app-launcher" type="button" title="Open app modules" aria-label="Open app modules" onClick={() => setAppDrawerOpen(true)}><AppIcon name="apps" /></button></div><b>NK</b></header>{renderPage()}</main>{appDrawerOpen && <div className="drawer-scrim" onClick={() => setAppDrawerOpen(false)} />}<aside className="module-drawer" aria-hidden={!appDrawerOpen}><header><div><span className="eyebrow purple">App Launcher</span><h3>Choose module</h3></div><button type="button" aria-label="Close app modules" onClick={() => setAppDrawerOpen(false)}>×</button></header>{modules.map(module => <button className={mainModule === module.code ? 'active' : ''} type="button" disabled={module.disabled} onClick={() => { if (!module.disabled) { setModule(module.code as ModuleCode); setAppDrawerOpen(false) } }} key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></button>)}</aside></div>
}
