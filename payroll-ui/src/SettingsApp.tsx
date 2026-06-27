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
import { useAuthSession } from './components/AuthGate'
import SettingsPage from './pages/SettingsPage'
import './OrganizationSetup.css'
import './ModuleDrawer.css'

type ModuleCode = 'Settings' | 'Employees' | 'Payroll' | 'LeaveAttendance' | 'Security' | 'Reports' | 'Workflows'
type SettingsTab = (typeof settingsMenus)[number]
type SecurityTab = (typeof securityMenus)[number]
type PayrollTab = 'Regular Run' | 'Off-cycle Run' | 'Adjustments'
type SettingsSection = 'General' | 'LeaveAttendance'
const allPayrollSetupMenus: SettingsTab[] = ['Pay Schedule', 'Tax Engine', 'Statutory Setup', 'Salary Components', 'Salary Templates', 'Payslip Templates']

const modules: { code: ModuleCode | 'Reports'; label: string; icon: IconName; description: string; disabled?: boolean }[] = [
  { code: 'Payroll', label: 'Payroll', icon: 'payruns', description: 'Run payroll, compare variances and review history.' },
  { code: 'LeaveAttendance', label: 'Leave & Attendance', icon: 'calendar', description: 'Review attendance before payroll processing.' },
  { code: 'Employees', label: 'Employees', icon: 'employees', description: 'Manage employee master, salary and statutory profile.' },
  { code: 'Security', label: 'Security', icon: 'security', description: 'Control users, roles, permissions and audit evidence.' },
  { code: 'Workflows', label: 'Workflows', icon: 'reports', description: 'Configure reusable approval workflows and tasks.' },
  { code: 'Settings', label: 'Settings', icon: 'settings', description: 'Configure organization, clients and payroll setup.' },
  { code: 'Reports', label: 'Reports', icon: 'reports', description: 'Client-scoped reporting and analytics.' }
]

export default function SettingsApp() {
  const session = useAuthSession()
  const canManageStatutory = Boolean(session?.user.permissions.includes('tax.statutory.manage'))
  const routeLocation = useLocation()
  const isPayHistory = routeLocation.pathname === '/pay-runs/history'
  const savedTab = localStorage.getItem('payroll.tab') as SettingsTab | null
  const savedModule = localStorage.getItem('payroll.module')
  const savedSecurityTab = localStorage.getItem('payroll.securityTab') as SecurityTab | null
  const savedLeaveAttendanceTab = localStorage.getItem('payroll.leaveAttendanceTab') as LeaveAttendanceMenu | null
  const savedReportingTab = localStorage.getItem('payroll.reportingTab') as ReportingMenu | null
  const savedWorkflowTab = localStorage.getItem('payroll.workflowTab') as WorkflowMenu | null
  const payrollSetupMenus = allPayrollSetupMenus.filter(item => item !== 'Statutory Setup' || canManageStatutory)
  const initialModule: ModuleCode = isPayHistory || savedModule === 'Payroll' || savedModule === 'Pay Runs' ? 'Payroll' : savedModule === 'LeaveAttendance' ? 'LeaveAttendance' : savedModule === 'Employees' ? 'Employees' : savedModule === 'Security' ? 'Security' : savedModule === 'Reports' ? 'Reports' : savedModule === 'Workflows' ? 'Workflows' : 'Settings'
  const [tab, setActiveTab] = useState<SettingsTab>(savedTab && settingsMenus.includes(savedTab) && (savedTab !== 'Statutory Setup' || canManageStatutory) ? savedTab : 'Organization')
  const [navOpen, setNavOpen] = useState(true), [appDrawerOpen, setAppDrawerOpen] = useState(false), [showMyTasks, setShowMyTasks] = useState(false)
  const [payrollSetupOpen, setPayrollSetupOpen] = useState(() => allPayrollSetupMenus.includes(savedTab ?? 'Organization'))
  const [leaveAttendanceOpen, setLeaveAttendanceOpen] = useState(false)
  const [settingsSection, setSettingsSection] = useState<SettingsSection>('General')
  const [securityTab, setSecurityTab] = useState<SecurityTab>(savedSecurityTab && securityMenus.includes(savedSecurityTab) ? savedSecurityTab : 'Users')
  const [payrollTab, setPayrollTab] = useState<PayrollTab>((localStorage.getItem('payroll.payrollTab') as PayrollTab | null) ?? 'Regular Run')
  const [leaveAttendanceTab, setLeaveAttendanceTab] = useState<LeaveAttendanceMenu>(savedLeaveAttendanceTab && leaveAttendanceMenus.includes(savedLeaveAttendanceTab) ? savedLeaveAttendanceTab : 'Preferences')
  const [reportingTab, setReportingTab] = useState<ReportingMenu>(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')
  const [workflowTab, setWorkflowTab] = useState<WorkflowMenu>(savedWorkflowTab && workflowMenus.includes(savedWorkflowTab) ? savedWorkflowTab : 'Workflow Setup')
  const [reportingReport, setReportingReport] = useState<ReportDefinition>(() => reportItems(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')[0])
  const [mainModule, setMainModule] = useState<ModuleCode>(initialModule), [settingsMessage, setSettingsMessage] = useState('Settings ready.')
  const activeModule = modules.find(module => module.code === mainModule)!
  const pageTitle = showMyTasks ? 'My Tasks' : mainModule === 'Settings' ? settingsSection === 'LeaveAttendance' ? leaveAttendanceTab : tab : mainModule === 'LeaveAttendance' ? 'Attendance Review' : mainModule === 'Employees' ? 'Employee Master' : mainModule === 'Security' ? securityTab : mainModule === 'Reports' ? reportingTab : mainModule === 'Workflows' ? workflowTab : isPayHistory ? 'Pay History' : mainModule === 'Payroll' ? payrollTab : 'Pay Run'

  const setTab = (nextTab: SettingsTab) => { setShowMyTasks(false); setSettingsSection('General'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.tab', nextTab); setActiveTab(nextTab) }
  const setModule = (nextModule: ModuleCode) => { setShowMyTasks(false); localStorage.setItem('payroll.module', nextModule); setMainModule(nextModule) }
  const setPayrollModuleTab = (nextTab: PayrollTab) => { localStorage.setItem('payroll.payrollTab', nextTab); setPayrollTab(nextTab); setModule('Payroll') }
  const setSecurityModuleTab = (nextTab: SecurityTab) => { localStorage.setItem('payroll.securityTab', nextTab); setSecurityTab(nextTab); setModule('Security') }
  const setLeaveAttendanceSettingsTab = (nextTab: LeaveAttendanceMenu) => { setShowMyTasks(false); setSettingsSection('LeaveAttendance'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.leaveAttendanceTab', nextTab); setLeaveAttendanceTab(nextTab); setMainModule('Settings') }
  const setReportingModuleTab = (nextTab: ReportingMenu) => { localStorage.setItem('payroll.reportingTab', nextTab); setReportingTab(nextTab); setReportingReport(reportItems(nextTab)[0]); setModule('Reports') }
  const setWorkflowModuleTab = (nextTab: WorkflowMenu) => { localStorage.setItem('payroll.workflowTab', nextTab); setWorkflowTab(nextTab); setModule('Workflows') }
  const renderContextMenu = () => {
    const tasks = <button className={showMyTasks ? 'active' : ''} type="button" onClick={() => setShowMyTasks(true)}>My Tasks<small>Approvals</small></button>
    if (mainModule === 'Settings') {
      const generalSettings = settingsMenus.filter(item => !payrollSetupMenus.includes(item))
      const payrollSetupActive = payrollSetupMenus.includes(tab)
      return <>{tasks}{generalSettings.map(item => <button className={settingsSection === 'General' && tab === item ? 'active' : ''} type="button" onClick={() => setTab(item)} key={item}>{item}</button>)}<div className={`settings-nav-group ${payrollSetupOpen ? 'expanded' : ''}`}><button className={settingsSection === 'General' && payrollSetupActive ? 'active' : ''} type="button" aria-expanded={payrollSetupOpen} onClick={() => setPayrollSetupOpen(open => !open)}><span>Payroll Setup</span><small>{payrollSetupOpen ? '−' : '+'}</small></button>{payrollSetupOpen && <div className="settings-nav-submenu">{payrollSetupMenus.map(item => <button className={settingsSection === 'General' && tab === item ? 'active' : ''} type="button" onClick={() => setTab(item)} key={item}><span>{item}</span>{item === 'Salary Templates' && <small>Client-wise</small>}</button>)}</div>}</div><div className={`settings-nav-group ${leaveAttendanceOpen ? 'expanded' : ''}`}><button className={settingsSection === 'LeaveAttendance' ? 'active' : ''} type="button" aria-expanded={leaveAttendanceOpen} onClick={() => setLeaveAttendanceOpen(open => !open)}><span>Leave & Attendance</span><small>{leaveAttendanceOpen ? '−' : '+'}</small></button>{leaveAttendanceOpen && <div className="settings-nav-submenu">{leaveAttendanceMenus.map(item => <button className={settingsSection === 'LeaveAttendance' && leaveAttendanceTab === item ? 'active' : ''} type="button" onClick={() => setLeaveAttendanceSettingsTab(item)} key={item}>{item}</button>)}</div>}</div></>
    }
    if (mainModule === 'Payroll') return <>{tasks}<Link className={!isPayHistory && payrollTab === 'Adjustments' ? 'active' : ''} to="/" onClick={() => setPayrollModuleTab('Adjustments')}>Adjustments<small>Variable pay</small></Link><div className="settings-nav-group expanded"><button className={!isPayHistory && payrollTab !== 'Adjustments' ? 'active' : ''} type="button"><span>Pay Run</span></button><div className="settings-nav-submenu"><Link className={!isPayHistory && payrollTab === 'Regular Run' ? 'active' : ''} to="/" onClick={() => setPayrollModuleTab('Regular Run')}>Regular Run</Link><Link className={!isPayHistory && payrollTab === 'Off-cycle Run' ? 'active' : ''} to="/" onClick={() => setPayrollModuleTab('Off-cycle Run')}>Off-cycle Run</Link></div></div><Link className={isPayHistory ? 'active' : ''} to="/pay-runs/history" onClick={() => setModule('Payroll')}>Pay History</Link></>
    if (mainModule === 'LeaveAttendance') return <>{tasks}<Link className="active" to="/" onClick={() => setModule('LeaveAttendance')}>Attendance Review<small>Pre-payroll</small></Link></>
    if (mainModule === 'Security') return <>{tasks}{securityMenus.map(item => <button className={securityTab === item ? 'active' : ''} type="button" onClick={() => setSecurityModuleTab(item)} key={item}>{item}</button>)}</>
    if (mainModule === 'Reports') return <>{tasks}{reportingMenus.map(item => <div className="report-nav-group" key={item}><button className={reportingTab === item ? 'active' : ''} type="button" onClick={() => setReportingModuleTab(item)}>{item}</button>{reportingTab === item && <div className="report-nav-submenu">{reportItems(item).map(report => <button className={reportingReport.name === report.name ? 'active' : ''} type="button" onClick={() => setReportingReport(report)} key={report.name}>{report.name}</button>)}</div>}</div>)}</>
    if (mainModule === 'Workflows') return workflowMenus.map(item => <button className={workflowTab === item ? 'active' : ''} type="button" onClick={() => setWorkflowModuleTab(item)} key={item}>{item}</button>)
    return <button className="active" type="button">Employee Master<small>Core HR</small></button>
  }
  const renderPage = () => {
    if (showMyTasks) return <WorkflowTasks />
    if (mainModule === 'Security') return <SecurityPanel initialTab={securityTab} />
    if (mainModule === 'LeaveAttendance') return <PayrollAttendancePage />
    if (mainModule === 'Payroll') return isPayHistory ? <PayHistoryPage /> : <PayrollPage key={payrollTab} mode={payrollTab === 'Adjustments' ? 'adjustments' : 'payrun'} runType={payrollTab === 'Off-cycle Run' ? 'Off-cycle Run' : 'Regular Run'} />
    if (mainModule === 'Employees') return <EmployeePage />
    if (mainModule === 'Reports') return <ReportingPage activeMenu={reportingTab} activeReport={reportingReport} />
    if (mainModule === 'Workflows') return <WorkflowPage activeMenu={workflowTab} />
    return settingsSection === 'LeaveAttendance' ? <LeaveAttendancePage activeMenu={leaveAttendanceTab} onSelectMenu={setLeaveAttendanceSettingsTab} /> : <SettingsPage tab={tab} onMessage={() => undefined} />
  }

  return <div className={`payroll-app compact module-shell ${navOpen ? '' : 'nav-collapsed'} ${appDrawerOpen ? 'drawer-open' : ''}`}><aside className="app-sidebar"><div className="side-head"><a className="brand"><i>F</i><span>Frevo One HR</span></a><button className="sidebar-toggle" type="button" title={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-label={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} onClick={() => setNavOpen(open => !open)}><AppIcon name={navOpen ? 'collapse' : 'expand'} /></button></div><div className="sidebar-context"><i><AppIcon name={activeModule.icon} /></i><div><span>Module</span><strong>{activeModule.label}</strong></div></div><nav><div className="submenu context-menu">{renderContextMenu()}</div></nav></aside><main className="compact-main"><header className="topbar app-topbar"><div className="topbar-title"><span>{mainModule}</span><h2>{pageTitle}</h2></div><div className="topbar-tools"><label className="global-search"><input placeholder="Search..." /><span>⌘K</span></label><button type="button" className="notification-btn" aria-label="Notifications">🔔</button><button className="app-launcher" type="button" title="Open app modules" aria-label="Open app modules" onClick={() => setAppDrawerOpen(true)}><AppIcon name="apps" /></button></div></header>{renderPage()}</main>{appDrawerOpen && <div className="drawer-scrim" onClick={() => setAppDrawerOpen(false)} />}<aside className="module-drawer" aria-hidden={!appDrawerOpen}><header><div><span className="eyebrow purple">App Launcher</span><h3>Choose module</h3></div><button type="button" aria-label="Close app modules" onClick={() => setAppDrawerOpen(false)}>×</button></header>{modules.map(module => <button className={mainModule === module.code ? 'active' : ''} type="button" disabled={module.disabled} onClick={() => { if (!module.disabled) { setModule(module.code as ModuleCode); setAppDrawerOpen(false) } }} key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></button>)}</aside></div>
}

