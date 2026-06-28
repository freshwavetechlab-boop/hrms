import { useEffect, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { AppstoreOutlined, BellOutlined, LogoutOutlined, MenuFoldOutlined, MenuUnfoldOutlined, SearchOutlined, UserOutlined } from '@ant-design/icons'
import { Avatar, Badge, Breadcrumb, Button, Dropdown, Input, Space, Tooltip } from 'antd'
import type { MenuProps } from 'antd'
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
const compactSidebarQuery = '(max-width: 640px)'

const modules: { code: ModuleCode | 'Reports'; label: string; icon: IconName; description: string; disabled?: boolean }[] = [
  { code: 'Payroll', label: 'Payroll', icon: 'payruns', description: 'Run payroll, compare variances and review history.' },
  { code: 'LeaveAttendance', label: 'Leave & Attendance', icon: 'calendar', description: 'Review attendance before payroll processing.' },
  { code: 'Employees', label: 'Employees', icon: 'employees', description: 'Manage employee master, salary and statutory profile.' },
  { code: 'Security', label: 'Security', icon: 'security', description: 'Control users, roles, permissions and audit evidence.' },
  { code: 'Workflows', label: 'Workflows', icon: 'reports', description: 'Configure reusable approval workflows and tasks.' },
  { code: 'Settings', label: 'Settings', icon: 'settings', description: 'Configure organization, clients and payroll setup.' },
  { code: 'Reports', label: 'Reports', icon: 'reports', description: 'Client-scoped reporting and analytics.' }
]

const navAttrs = (label: string) => ({ title: label, 'data-initial': (label.match(/[A-Za-z0-9]/)?.[0] ?? '•').toUpperCase() })

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
  const [navOpen, setNavOpen] = useState(() => typeof window === 'undefined' || !window.matchMedia(compactSidebarQuery).matches)
  const [mobileShell, setMobileShell] = useState(() => typeof window !== 'undefined' && window.matchMedia(compactSidebarQuery).matches)
  const [appDrawerOpen, setAppDrawerOpen] = useState(false), [showMyTasks, setShowMyTasks] = useState(false)
  const [payrollSetupOpen, setPayrollSetupOpen] = useState(() => allPayrollSetupMenus.includes(savedTab ?? 'Organization'))
  const [leaveAttendanceOpen, setLeaveAttendanceOpen] = useState(false)
  const [settingsSection, setSettingsSection] = useState<SettingsSection>('General')
  const [securityTab, setSecurityTab] = useState<SecurityTab>(savedSecurityTab && securityMenus.includes(savedSecurityTab) ? savedSecurityTab : 'Users')
  const [payrollTab, setPayrollTab] = useState<PayrollTab>((localStorage.getItem('payroll.payrollTab') as PayrollTab | null) ?? 'Regular Run')
  const [leaveAttendanceTab, setLeaveAttendanceTab] = useState<LeaveAttendanceMenu>(savedLeaveAttendanceTab && leaveAttendanceMenus.includes(savedLeaveAttendanceTab) ? savedLeaveAttendanceTab : 'Preferences')
  const [reportingTab, setReportingTab] = useState<ReportingMenu>(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')
  const [workflowTab, setWorkflowTab] = useState<WorkflowMenu>(savedWorkflowTab && workflowMenus.includes(savedWorkflowTab) ? savedWorkflowTab : 'Workflow Setup')
  const [reportingReport, setReportingReport] = useState<ReportDefinition>(() => reportItems(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')[0])
  const [mainModule, setMainModule] = useState<ModuleCode>(initialModule)
  const activeModule = modules.find(module => module.code === mainModule)!
  const pageTitle = showMyTasks ? 'My Tasks' : mainModule === 'Settings' ? settingsSection === 'LeaveAttendance' ? leaveAttendanceTab : tab : mainModule === 'LeaveAttendance' ? 'Attendance Review' : mainModule === 'Employees' ? 'Employee Master' : mainModule === 'Security' ? securityTab : mainModule === 'Reports' ? reportingTab : mainModule === 'Workflows' ? workflowTab : isPayHistory ? 'Pay History' : mainModule === 'Payroll' ? payrollTab : 'Pay Run'
  const currentUser = session?.user
  const userInitials = (currentUser?.displayName || 'User').split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase()
  const accountMenu: MenuProps = {
    items: [
      { key: 'profile', label: <div className="account-menu-card"><strong>{currentUser?.displayName}</strong><small>{currentUser?.email}</small><span>{currentUser?.roles.join(', ')}</span></div> },
      { type: 'divider' },
      { key: 'logout', danger: true, icon: <LogoutOutlined />, label: 'Logout' }
    ],
    onClick: ({ key }) => { if (key === 'logout') void session?.logout() }
  }

  useEffect(() => {
    const query = window.matchMedia(compactSidebarQuery)
    const syncShell = () => {
      setMobileShell(query.matches)
      setNavOpen(!query.matches)
    }
    syncShell()
    query.addEventListener('change', syncShell)
    return () => query.removeEventListener('change', syncShell)
  }, [])

  useEffect(() => {
    document.documentElement.classList.add('portal-shell-mounted')
    document.body.classList.add('portal-shell-mounted')
    return () => {
      document.documentElement.classList.remove('portal-shell-mounted')
      document.body.classList.remove('portal-shell-mounted')
    }
  }, [])

  const navigateFromMenu = (action: () => void) => {
    action()
    if (mobileShell) setNavOpen(false)
  }

  const setTab = (nextTab: SettingsTab) => { setShowMyTasks(false); setSettingsSection('General'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.tab', nextTab); setActiveTab(nextTab) }
  const setModule = (nextModule: ModuleCode) => { setShowMyTasks(false); localStorage.setItem('payroll.module', nextModule); setMainModule(nextModule) }
  const setPayrollModuleTab = (nextTab: PayrollTab) => { localStorage.setItem('payroll.payrollTab', nextTab); setPayrollTab(nextTab); setModule('Payroll') }
  const setSecurityModuleTab = (nextTab: SecurityTab) => { localStorage.setItem('payroll.securityTab', nextTab); setSecurityTab(nextTab); setModule('Security') }
  const setLeaveAttendanceSettingsTab = (nextTab: LeaveAttendanceMenu) => { setShowMyTasks(false); setSettingsSection('LeaveAttendance'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.leaveAttendanceTab', nextTab); setLeaveAttendanceTab(nextTab); setMainModule('Settings') }
  const setReportingModuleTab = (nextTab: ReportingMenu) => { localStorage.setItem('payroll.reportingTab', nextTab); setReportingTab(nextTab); setReportingReport(reportItems(nextTab)[0]); setModule('Reports') }
  const setWorkflowModuleTab = (nextTab: WorkflowMenu) => { localStorage.setItem('payroll.workflowTab', nextTab); setWorkflowTab(nextTab); setModule('Workflows') }
  const renderContextMenu = () => {
    const tasks = <button {...navAttrs('My Tasks')} className={showMyTasks ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setShowMyTasks(true))}>My Tasks<small>Approvals</small></button>
    if (mainModule === 'Settings') {
      const generalSettings = settingsMenus.filter(item => !payrollSetupMenus.includes(item))
      const payrollSetupActive = payrollSetupMenus.includes(tab)
      return <>
        {tasks}
        {generalSettings.map(item => <button {...navAttrs(item)} className={settingsSection === 'General' && tab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setTab(item))} key={item}>{item}</button>)}
        <div className={`settings-nav-group ${payrollSetupOpen ? 'expanded' : ''}`}>
          <button {...navAttrs('Payroll Setup')} className={settingsSection === 'General' && payrollSetupActive ? 'active' : ''} type="button" aria-expanded={payrollSetupOpen} onClick={() => setPayrollSetupOpen(open => !open)}><span>Payroll Setup</span><small>{payrollSetupOpen ? '-' : '+'}</small></button>
          {payrollSetupOpen && <div className="settings-nav-submenu">{payrollSetupMenus.map(item => <button {...navAttrs(item)} className={settingsSection === 'General' && tab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setTab(item))} key={item}><span>{item}</span>{item === 'Salary Templates' && <small>Client-wise</small>}</button>)}</div>}
        </div>
        <div className={`settings-nav-group ${leaveAttendanceOpen ? 'expanded' : ''}`}>
          <button {...navAttrs('Leave & Attendance')} className={settingsSection === 'LeaveAttendance' ? 'active' : ''} type="button" aria-expanded={leaveAttendanceOpen} onClick={() => setLeaveAttendanceOpen(open => !open)}><span>Leave & Attendance</span><small>{leaveAttendanceOpen ? '-' : '+'}</small></button>
          {leaveAttendanceOpen && <div className="settings-nav-submenu">{leaveAttendanceMenus.map(item => <button {...navAttrs(item)} className={settingsSection === 'LeaveAttendance' && leaveAttendanceTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setLeaveAttendanceSettingsTab(item))} key={item}>{item}</button>)}</div>}
        </div>
      </>
    }
    if (mainModule === 'Payroll') return <>
      {tasks}
      <Link {...navAttrs('Adjustments')} className={!isPayHistory && payrollTab === 'Adjustments' ? 'active' : ''} to="/" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Adjustments'))}>Adjustments<small>Variable pay</small></Link>
      <div className="settings-nav-group expanded"><button {...navAttrs('Pay Run')} className={!isPayHistory && payrollTab !== 'Adjustments' ? 'active' : ''} type="button"><span>Pay Run</span></button><div className="settings-nav-submenu"><Link {...navAttrs('Regular Run')} className={!isPayHistory && payrollTab === 'Regular Run' ? 'active' : ''} to="/" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Regular Run'))}>Regular Run</Link><Link {...navAttrs('Off-cycle Run')} className={!isPayHistory && payrollTab === 'Off-cycle Run' ? 'active' : ''} to="/" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Off-cycle Run'))}>Off-cycle Run</Link></div></div>
      <Link {...navAttrs('Pay History')} className={isPayHistory ? 'active' : ''} to="/pay-runs/history" onClick={() => navigateFromMenu(() => setModule('Payroll'))}>Pay History</Link>
    </>
    if (mainModule === 'LeaveAttendance') return <>{tasks}<Link {...navAttrs('Attendance Review')} className="active" to="/" onClick={() => navigateFromMenu(() => setModule('LeaveAttendance'))}>Attendance Review<small>Pre-payroll</small></Link></>
    if (mainModule === 'Security') return <>{tasks}{securityMenus.map(item => <button {...navAttrs(item)} className={securityTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setSecurityModuleTab(item))} key={item}>{item}</button>)}</>
    if (mainModule === 'Reports') return <>{tasks}{reportingMenus.map(item => <div className="report-nav-group" key={item}><button {...navAttrs(item)} className={reportingTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setReportingModuleTab(item))}>{item}</button>{reportingTab === item && <div className="report-nav-submenu">{reportItems(item).map(report => <button {...navAttrs(report.name)} className={reportingReport.name === report.name ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setReportingReport(report))} key={report.name}>{report.name}</button>)}</div>}</div>)}</>
    if (mainModule === 'Workflows') return workflowMenus.map(item => <button {...navAttrs(item)} className={workflowTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setWorkflowModuleTab(item))} key={item}>{item}</button>)
    return <button {...navAttrs('Employee Master')} className="active" type="button" onClick={() => navigateFromMenu(() => setModule('Employees'))}>Employee Master<small>Core HR</small></button>
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

  const shellClassName = ['payroll-app compact module-shell', navOpen ? '' : 'nav-collapsed', appDrawerOpen ? 'drawer-open' : '', mobileShell && navOpen ? 'mobile-nav-open' : ''].filter(Boolean).join(' ')

  return <div className={shellClassName}>
    {mobileShell && navOpen && <button className="sidebar-scrim" type="button" aria-label="Close sidebar" onClick={() => setNavOpen(false)} />}
    <aside className="app-sidebar">
      <div className="side-head">
        <a className="brand"><i>F</i><span>Frevo One HR</span></a>
        <Button className="sidebar-toggle" type="text" shape="circle" title={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-label={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-expanded={navOpen} icon={navOpen ? <MenuFoldOutlined /> : <MenuUnfoldOutlined />} onClick={() => setNavOpen(open => !open)} />
      </div>
      <div className="sidebar-context"><i><AppIcon name={activeModule.icon} /></i><div><span>Module</span><strong>{activeModule.label}</strong></div></div>
      <nav><div className="submenu context-menu">{renderContextMenu()}</div></nav>
    </aside>
    <main className="compact-main">
      <header className="topbar app-topbar">
        <div className="topbar-title">
          <Breadcrumb className="topbar-breadcrumb" items={[{ title: activeModule.label }, { title: pageTitle }]} />
          <h2 title={pageTitle}>{pageTitle}</h2>
        </div>
        <Space className="topbar-tools" size={10}>
          <Input className="global-search-antd" prefix={<SearchOutlined />} placeholder="Search..." allowClear suffix={<span className="search-shortcut">Ctrl K</span>} />
          <Tooltip title="Notifications"><Badge dot><Button className="topbar-icon-btn" type="default" icon={<BellOutlined />} aria-label="Notifications" /></Badge></Tooltip>
          <Tooltip title="Open app modules"><Button className="topbar-icon-btn" type="default" icon={<AppstoreOutlined />} aria-label="Open app modules" onClick={() => setAppDrawerOpen(true)} /></Tooltip>
          <Dropdown menu={accountMenu} trigger={['click']} placement="bottomRight">
            <button className="account-trigger" type="button" aria-label="Open account menu">
              <Avatar size={36} icon={!userInitials ? <UserOutlined /> : undefined}>{userInitials}</Avatar>
            </button>
          </Dropdown>
        </Space>
      </header>
      <div className="module-content">{renderPage()}</div>
    </main>
    {appDrawerOpen && <div className="drawer-scrim" onClick={() => setAppDrawerOpen(false)} />}
    <aside className="module-drawer" aria-hidden={!appDrawerOpen}>
      <header><div><span className="eyebrow purple">App Launcher</span><h3>Choose module</h3></div><button type="button" aria-label="Close app modules" onClick={() => setAppDrawerOpen(false)}><AppIcon name="close" /></button></header>
      {modules.map(module => <button className={mainModule === module.code ? 'active' : ''} type="button" disabled={module.disabled} onClick={() => { if (!module.disabled) { setModule(module.code as ModuleCode); setAppDrawerOpen(false) } }} key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></button>)}
    </aside>
  </div>
}

