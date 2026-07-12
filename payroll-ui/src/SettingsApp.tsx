import { useEffect, useRef, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { AppstoreOutlined, BellOutlined, LogoutOutlined, MenuFoldOutlined, MenuUnfoldOutlined, SearchOutlined, UserOutlined } from '@ant-design/icons'
import { Avatar, Badge, Breadcrumb, Button, Dropdown, Input, Space, Tooltip } from 'antd'
import type { MenuProps } from 'antd'
import AppIcon from './components/AppIcon'
import type { IconName } from './components/AppIcon'
import SecurityPanel from './components/SecurityPanel'
import { leaveAttendanceMenus, org0, reportingMenus, securityMenus, settingsMenus, workflowMenus } from './data/payrollDefaults'
import DashboardPage from './pages/DashboardPage'
import EmployeePage, { type EmployeePageView } from './pages/EmployeePage'
import LeaveAttendancePage from './pages/LeaveAttendancePage'
import type { LeaveAttendanceMenu } from './pages/LeaveAttendancePage'
import PayHistoryPage from './pages/PayHistoryPage'
import PayrollAttendancePage from './pages/PayrollAttendancePage'
import PayrollPage from './pages/PayrollPage'
import ReportingPage, { reportItems } from './pages/ReportingPage'
import type { ReportDefinition, ReportingMenu } from './pages/ReportingPage'
import WorkflowPage from './pages/WorkflowPage'
import type { WorkflowMenu } from './pages/WorkflowPage'
import EmployeeTaxProfileManager from './components/EmployeeTaxProfileManager'
import WorkflowTasks from './components/WorkflowTasks'
import { useAuthSession } from './components/AuthGate'
import SettingsPage from './pages/SettingsPage'
import { getOrganization } from './services/settingsService'
import type { Org } from './types/payroll'
import './OrganizationSetup.css'
import './ModuleDrawer.css'

type ModuleCode = 'Dashboard' | 'Settings' | 'Employees' | 'Payroll' | 'LeaveAttendance' | 'Security' | 'Reports' | 'Workflows'
type SettingsTab = (typeof settingsMenus)[number]
type SecurityTab = (typeof securityMenus)[number]
type PayrollTab = 'Regular Run' | 'Off-cycle Run' | 'Adjustments' | 'Employee Tax Profile'
type EmployeeTab = 'Employee Master' | 'Org Structure'
type SettingsSection = 'General' | 'LeaveAttendance'
const allPayrollSetupMenus: SettingsTab[] = ['Tax Engine', 'Statutory Setup', 'Salary Components', 'Salary Templates', 'Payslip Templates']
const compactSidebarQuery = '(max-width: 640px)'
const productLogo = '/assets/FrevoOneLogo.png'
const productMark = '/favicon.svg'

const modules: { code: ModuleCode | 'Reports'; label: string; icon: IconName; description: string; disabled?: boolean }[] = [
  { code: 'Dashboard', label: 'Dashboard', icon: 'reports', description: 'Client-wise payroll, attendance and approval overview.' },
  { code: 'Payroll', label: 'Payroll', icon: 'payruns', description: 'Run payroll, compare variances and review history.' },
  { code: 'LeaveAttendance', label: 'Leave & Attendance', icon: 'calendar', description: 'Review attendance before payroll processing.' },
  { code: 'Employees', label: 'Employees', icon: 'employees', description: 'Manage employee master, salary and statutory profile.' },
  { code: 'Security', label: 'Security', icon: 'security', description: 'Control users, roles, permissions and audit evidence.' },
  { code: 'Workflows', label: 'Workflows', icon: 'reports', description: 'Configure reusable approval workflows and tasks.' },
  { code: 'Settings', label: 'Settings', icon: 'settings', description: 'Configure organization, clients and payroll setup.' },
  { code: 'Reports', label: 'Reports', icon: 'reports', description: 'Client-scoped reporting and analytics.' }
]

const navAttrs = (label: string) => ({ title: label, 'data-initial': (label.match(/[A-Za-z0-9]/)?.[0] ?? '*').toUpperCase() })
const menuIcon = (label: string): IconName => {
  const value = label.toLowerCase()
  if (value.includes('task')) return 'tasks'
  if (value.includes('dashboard')) return 'dashboard'
  if (value.includes('adjust')) return 'adjustments'
  if (value.includes('run') || value.includes('pay run')) return 'run'
  if (value.includes('history')) return 'history'
  if (value.includes('tax') || value.includes('statutory') || value.includes('tds')) return 'tax'
  if (value.includes('employee')) return 'employees'
  if (value.includes('org')) return 'org'
  if (value.includes('attendance')) return 'attendance'
  if (value.includes('leave') || value.includes('holiday')) return value.includes('holiday') ? 'holiday' : 'calendar'
  if (value.includes('client')) return 'building'
  if (value.includes('location')) return 'location'
  if (value.includes('dropdown')) return 'dropdown'
  if (value.includes('salary template') || value.includes('payslip template')) return 'template'
  if (value.includes('component')) return 'component'
  if (value.includes('workflow')) return 'workflow'
  if (value.includes('notification')) return 'notification'
  if (value.includes('scheduled') || value.includes('job')) return 'job'
  if (value.includes('billing')) return 'billing'
  if (value.includes('payroll') || value.includes('pay')) return 'payruns'
  if (value.includes('report') || value.includes('mis') || value.includes('dashboard')) return 'reports'
  if (value.includes('security')) return 'security'
  if (value.includes('user')) return 'user'
  if (value.includes('role')) return 'role'
  if (value.includes('audit')) return 'shield'
  return 'document'
}
const menuLabel = (label: string) => <span className="menu-label"><AppIcon name={menuIcon(label)} /><span>{label}</span></span>
const menuContent = (label: string, detail?: string) => <>{menuLabel(label)}{detail && <small>{detail}</small>}</>

const slug = (value: string) => value.toLowerCase().replace(/&/g, 'and').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
const fromSlug = <T extends string>(items: readonly T[], value: string | undefined, fallback: T) => items.find(item => slug(item) === value) ?? fallback
const modulePaths: Record<ModuleCode, string> = {
  Dashboard: '/dashboard',
  Payroll: '/payroll/regular',
  LeaveAttendance: '/attendance',
  Employees: '/employees/master',
  Security: '/security/users',
  Workflows: '/workflows/workflow-setup',
  Settings: '/settings/organization',
  Reports: '/reports/payroll-reports'
}

export default function SettingsApp() {
  const session = useAuthSession()
  const sidebarRef = useRef<HTMLElement | null>(null)
  const canManageStatutory = Boolean(session?.user.permissions.includes('tax.statutory.manage'))
  const routeLocation = useLocation()
  const navigate = useNavigate()
  const isPayHistory = routeLocation.pathname === '/pay-runs/history'
  const savedTab = localStorage.getItem('payroll.tab') as SettingsTab | null
  const savedSecurityTab = localStorage.getItem('payroll.securityTab') as SecurityTab | null
  const savedLeaveAttendanceTab = localStorage.getItem('payroll.leaveAttendanceTab') as LeaveAttendanceMenu | null
  const savedReportingTab = localStorage.getItem('payroll.reportingTab') as ReportingMenu | null
  const savedWorkflowTab = localStorage.getItem('payroll.workflowTab') as WorkflowMenu | null
  const payrollSetupMenus = allPayrollSetupMenus.filter(item => item !== 'Statutory Setup' || canManageStatutory)
  const routeParts = routeLocation.pathname.split('/').filter(Boolean)
  const routeModule = (() : ModuleCode => {
    if (routeParts[0] === 'pay-runs' || routeParts[0] === 'payroll') return 'Payroll'
    if (routeParts[0] === 'attendance') return 'LeaveAttendance'
    if (routeParts[0] === 'employees') return 'Employees'
    if (routeParts[0] === 'security') return 'Security'
    if (routeParts[0] === 'reports') return 'Reports'
    if (routeParts[0] === 'workflows') return 'Workflows'
    if (routeParts[0] === 'settings') return 'Settings'
    return 'Dashboard'
  })()
  const routeIsTasks = routeParts[0] === 'tasks'
  const initialModule: ModuleCode = routeIsTasks ? 'Dashboard' : routeModule
  const [tab, setActiveTab] = useState<SettingsTab>(savedTab && settingsMenus.includes(savedTab) && (savedTab !== 'Statutory Setup' || canManageStatutory) ? savedTab : 'Organization')
  const [navOpen, setNavOpen] = useState(() => typeof window === 'undefined' || !window.matchMedia(compactSidebarQuery).matches)
  const [mobileShell, setMobileShell] = useState(() => typeof window !== 'undefined' && window.matchMedia(compactSidebarQuery).matches)
  const [appDrawerOpen, setAppDrawerOpen] = useState(false), [showMyTasks, setShowMyTasks] = useState(false)
  const [collapsedFlyout, setCollapsedFlyout] = useState<string | null>(null)
  const [payrollSetupOpen, setPayrollSetupOpen] = useState(() => allPayrollSetupMenus.includes(savedTab ?? 'Organization'))
  const [leaveAttendanceOpen, setLeaveAttendanceOpen] = useState(false)
  const [settingsSection, setSettingsSection] = useState<SettingsSection>('General')
  const [securityTab, setSecurityTab] = useState<SecurityTab>(savedSecurityTab && securityMenus.includes(savedSecurityTab) ? savedSecurityTab : 'Users')
  const [employeeTab, setEmployeeTab] = useState<EmployeeTab>('Employee Master')
  const [payrollTab, setPayrollTab] = useState<PayrollTab>((localStorage.getItem('payroll.payrollTab') as PayrollTab | null) ?? 'Regular Run')
  const [leaveAttendanceTab, setLeaveAttendanceTab] = useState<LeaveAttendanceMenu>(savedLeaveAttendanceTab && leaveAttendanceMenus.includes(savedLeaveAttendanceTab) ? savedLeaveAttendanceTab : 'Attendance Policies')
  const [reportingTab, setReportingTab] = useState<ReportingMenu>(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')
  const [workflowTab, setWorkflowTab] = useState<WorkflowMenu>(savedWorkflowTab && workflowMenus.includes(savedWorkflowTab) ? savedWorkflowTab : 'Workflow Setup')
  const [reportingReport, setReportingReport] = useState<ReportDefinition>(() => reportItems(savedReportingTab && reportingMenus.includes(savedReportingTab) ? savedReportingTab : 'Payroll Reports')[0])
  const [mainModule, setMainModule] = useState<ModuleCode>(initialModule)
  const [shellOrg, setShellOrg] = useState<Org>(org0)
  const activeModule = modules.find(module => module.code === mainModule)!
  const pageTitle = showMyTasks ? 'My Tasks' : mainModule === 'Dashboard' ? 'Dashboard' : mainModule === 'Settings' ? settingsSection === 'LeaveAttendance' ? leaveAttendanceTab : tab : mainModule === 'LeaveAttendance' ? 'Attendance Review' : mainModule === 'Employees' ? employeeTab : mainModule === 'Security' ? securityTab : mainModule === 'Reports' ? reportingTab : mainModule === 'Workflows' ? workflowTab : isPayHistory ? 'Pay History' : mainModule === 'Payroll' ? payrollTab : 'Pay Run'
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

  useEffect(() => {
    void getOrganization(org0).then(organization => setShellOrg({ ...org0, ...organization }))
    const syncOrganization = (event: Event) => {
      const detail = (event as CustomEvent<Org>).detail
      if (detail) setShellOrg({ ...org0, ...detail })
    }
    window.addEventListener('organization-updated', syncOrganization)
    return () => window.removeEventListener('organization-updated', syncOrganization)
  }, [])

  useEffect(() => {
    const clearCollapsedSidebarFocus = (event: PointerEvent) => {
      if (navOpen) return
      const sidebar = sidebarRef.current
      if (!sidebar || sidebar.contains(event.target as Node)) return
      setCollapsedFlyout(null)
      const activeElement = document.activeElement
      if (activeElement instanceof HTMLElement && sidebar.contains(activeElement)) activeElement.blur()
    }

    document.addEventListener('pointerdown', clearCollapsedSidebarFocus, true)
    return () => document.removeEventListener('pointerdown', clearCollapsedSidebarFocus, true)
  }, [navOpen])

  useEffect(() => {
    const parts = routeLocation.pathname.split('/').filter(Boolean)
    const tabSlug = parts.at(-1)

    if (parts[0] === 'tasks') {
      setShowMyTasks(true)
      setMainModule('Dashboard')
      return
    }

    setShowMyTasks(false)
    if (parts[0] === 'pay-runs') {
      setMainModule('Payroll')
      return
    }
    if (parts[0] === 'payroll') {
      const nextTab: PayrollTab = parts[1] === 'adjustments' ? 'Adjustments' : parts[1] === 'off-cycle' ? 'Off-cycle Run' : parts[1] === 'tax-profile' ? 'Employee Tax Profile' : 'Regular Run'
      setPayrollTab(nextTab)
      localStorage.setItem('payroll.payrollTab', nextTab)
      setMainModule('Payroll')
      return
    }
    if (parts[0] === 'attendance') {
      setMainModule('LeaveAttendance')
      return
    }
    if (parts[0] === 'employees') {
      setEmployeeTab(parts[1] === 'org-structure' ? 'Org Structure' : 'Employee Master')
      setMainModule('Employees')
      return
    }
    if (parts[0] === 'security') {
      const nextTab = fromSlug(securityMenus, tabSlug, 'Users')
      setSecurityTab(nextTab)
      localStorage.setItem('payroll.securityTab', nextTab)
      setMainModule('Security')
      return
    }
    if (parts[0] === 'reports') {
      const nextTab = fromSlug(reportingMenus, tabSlug, 'Payroll Reports')
      setReportingTab(nextTab)
      setReportingReport(reportItems(nextTab)[0])
      localStorage.setItem('payroll.reportingTab', nextTab)
      setMainModule('Reports')
      return
    }
    if (parts[0] === 'workflows') {
      const nextTab = fromSlug(workflowMenus, tabSlug, 'Workflow Setup')
      setWorkflowTab(nextTab)
      localStorage.setItem('payroll.workflowTab', nextTab)
      setMainModule('Workflows')
      return
    }
    if (parts[0] === 'settings') {
      if (parts[1] === 'leave-attendance') {
        const nextTab = fromSlug(leaveAttendanceMenus, parts[2], 'Attendance Policies')
        setSettingsSection('LeaveAttendance')
        setLeaveAttendanceOpen(true)
        setLeaveAttendanceTab(nextTab)
        localStorage.setItem('payroll.leaveAttendanceTab', nextTab)
      } else {
        const nextTab = fromSlug(settingsMenus, tabSlug, 'Organization')
        const allowedTab = nextTab === 'Statutory Setup' && !canManageStatutory ? 'Organization' : nextTab
        setSettingsSection('General')
        setActiveTab(allowedTab)
        setPayrollSetupOpen(allPayrollSetupMenus.includes(allowedTab))
        localStorage.setItem('payroll.tab', allowedTab)
      }
      setMainModule('Settings')
      return
    }
    setMainModule('Dashboard')
  }, [canManageStatutory, routeLocation.pathname])

  const navigateFromMenu = (action: () => void) => {
    action()
    setCollapsedFlyout(null)
    if (mobileShell) setNavOpen(false)
  }

  const toggleNavGroup = (key: string, action: () => void) => {
    if (!navOpen) {
      action()
      setCollapsedFlyout(current => current === key ? null : key)
      return
    }
    action()
  }

  const openTasks = () => { setShowMyTasks(true); navigate('/tasks') }
  const setTab = (nextTab: SettingsTab) => { setShowMyTasks(false); setSettingsSection('General'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.tab', nextTab); setActiveTab(nextTab); navigate(`/settings/${slug(nextTab)}`) }
  const setModule = (nextModule: ModuleCode) => { setShowMyTasks(false); localStorage.setItem('payroll.module', nextModule); setMainModule(nextModule); navigate(modulePaths[nextModule]) }
  const setPayrollModuleTab = (nextTab: PayrollTab) => { localStorage.setItem('payroll.payrollTab', nextTab); setPayrollTab(nextTab); setShowMyTasks(false); setMainModule('Payroll'); navigate(nextTab === 'Adjustments' ? '/payroll/adjustments' : nextTab === 'Off-cycle Run' ? '/payroll/off-cycle' : nextTab === 'Employee Tax Profile' ? '/payroll/tax-profile' : '/payroll/regular') }
  const setEmployeeModuleTab = (nextTab: EmployeeTab) => { setEmployeeTab(nextTab); setShowMyTasks(false); setMainModule('Employees'); navigate(nextTab === 'Org Structure' ? '/employees/org-structure' : '/employees/master') }
  const setPayHistory = () => { setShowMyTasks(false); localStorage.setItem('payroll.module', 'Payroll'); setMainModule('Payroll'); navigate('/pay-runs/history') }
  const setSecurityModuleTab = (nextTab: SecurityTab) => { localStorage.setItem('payroll.securityTab', nextTab); setSecurityTab(nextTab); setShowMyTasks(false); setMainModule('Security'); navigate(`/security/${slug(nextTab)}`) }
  const setLeaveAttendanceSettingsTab = (nextTab: LeaveAttendanceMenu) => { setShowMyTasks(false); setSettingsSection('LeaveAttendance'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.leaveAttendanceTab', nextTab); setLeaveAttendanceTab(nextTab); setMainModule('Settings'); navigate(`/settings/leave-attendance/${slug(nextTab)}`) }
  const setReportingModuleTab = (nextTab: ReportingMenu) => { localStorage.setItem('payroll.reportingTab', nextTab); setReportingTab(nextTab); setReportingReport(reportItems(nextTab)[0]); setShowMyTasks(false); setMainModule('Reports'); navigate(`/reports/${slug(nextTab)}`) }
  const setWorkflowModuleTab = (nextTab: WorkflowMenu) => { localStorage.setItem('payroll.workflowTab', nextTab); setWorkflowTab(nextTab); setShowMyTasks(false); setMainModule('Workflows'); navigate(`/workflows/${slug(nextTab)}`) }
  const renderContextMenu = () => {
    const tasks = <button {...navAttrs('My Tasks')} className={showMyTasks ? 'active' : ''} type="button" onClick={() => navigateFromMenu(openTasks)}>{menuContent('My Tasks', 'Approvals')}</button>
    if (mainModule === 'Dashboard') return <>
      <button {...navAttrs('Dashboard')} className={!showMyTasks ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setModule('Dashboard'))}>{menuContent('Dashboard', 'Overview')}</button>
      {tasks}
    </>
    if (mainModule === 'Settings') {
      const generalSettings = settingsMenus.filter(item => !payrollSetupMenus.includes(item))
      const payrollSetupActive = payrollSetupMenus.includes(tab)
      return <>
        {tasks}
        {generalSettings.map(item => <button {...navAttrs(item)} className={settingsSection === 'General' && tab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setTab(item))} key={item}>{menuContent(item)}</button>)}
        <div className={`settings-nav-group ${payrollSetupOpen ? 'expanded' : ''} ${collapsedFlyout === 'settings-payroll' ? 'flyout-open' : ''}`}>
          <button {...navAttrs('Payroll Setup')} className={settingsSection === 'General' && payrollSetupActive ? 'active' : ''} type="button" aria-expanded={payrollSetupOpen} onClick={() => toggleNavGroup('settings-payroll', () => setPayrollSetupOpen(open => navOpen ? !open : true))}>{menuLabel('Payroll Setup')}<small>{payrollSetupOpen ? '-' : '+'}</small></button>
          {payrollSetupOpen && <div className="settings-nav-submenu">{payrollSetupMenus.map(item => <button {...navAttrs(item)} className={settingsSection === 'General' && tab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setTab(item))} key={item}>{menuContent(item, item === 'Salary Templates' ? 'Client-wise' : undefined)}</button>)}</div>}
        </div>
        <div className={`settings-nav-group ${leaveAttendanceOpen ? 'expanded' : ''} ${collapsedFlyout === 'settings-leave-attendance' ? 'flyout-open' : ''}`}>
          <button {...navAttrs('Leave & Attendance')} className={settingsSection === 'LeaveAttendance' ? 'active' : ''} type="button" aria-expanded={leaveAttendanceOpen} onClick={() => toggleNavGroup('settings-leave-attendance', () => setLeaveAttendanceOpen(open => navOpen ? !open : true))}>{menuLabel('Leave & Attendance')}<small>{leaveAttendanceOpen ? '-' : '+'}</small></button>
          {leaveAttendanceOpen && <div className="settings-nav-submenu">{leaveAttendanceMenus.map(item => <button {...navAttrs(item)} className={settingsSection === 'LeaveAttendance' && leaveAttendanceTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setLeaveAttendanceSettingsTab(item))} key={item}>{menuContent(item)}</button>)}</div>}
        </div>
      </>
    }
    if (mainModule === 'Payroll') return <>
      {tasks}
      <Link {...navAttrs('Adjustments')} className={!isPayHistory && payrollTab === 'Adjustments' ? 'active' : ''} to="/payroll/adjustments" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Adjustments'))}>{menuContent('Adjustments', 'Variable pay')}</Link>
      <div className={`settings-nav-group expanded ${collapsedFlyout === 'payroll-run' ? 'flyout-open' : ''}`}><button {...navAttrs('Pay Run')} className={!isPayHistory && ['Regular Run', 'Off-cycle Run'].includes(payrollTab) ? 'active' : ''} type="button" onClick={() => toggleNavGroup('payroll-run', () => undefined)}>{menuLabel('Pay Run')}</button><div className="settings-nav-submenu"><Link {...navAttrs('Regular Run')} className={!isPayHistory && payrollTab === 'Regular Run' ? 'active' : ''} to="/payroll/regular" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Regular Run'))}>{menuContent('Regular Run')}</Link><Link {...navAttrs('Off-cycle Run')} className={!isPayHistory && payrollTab === 'Off-cycle Run' ? 'active' : ''} to="/payroll/off-cycle" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Off-cycle Run'))}>{menuContent('Off-cycle Run')}</Link></div></div>
      <Link {...navAttrs('Employee Tax Profile')} className={!isPayHistory && payrollTab === 'Employee Tax Profile' ? 'active' : ''} to="/payroll/tax-profile" onClick={() => navigateFromMenu(() => setPayrollModuleTab('Employee Tax Profile'))}>{menuContent('Employee Tax Profile', 'TDS profile')}</Link>
      <Link {...navAttrs('Pay History')} className={isPayHistory ? 'active' : ''} to="/pay-runs/history" onClick={() => navigateFromMenu(setPayHistory)}>{menuContent('Pay History')}</Link>
    </>
    if (mainModule === 'LeaveAttendance') return <>{tasks}<Link {...navAttrs('Attendance Review')} className="active" to="/attendance" onClick={() => navigateFromMenu(() => setModule('LeaveAttendance'))}>{menuContent('Attendance Review', 'Pre-payroll')}</Link></>
    if (mainModule === 'Employees') return <>{tasks}<Link {...navAttrs('Employee Master')} className={employeeTab === 'Employee Master' ? 'active' : ''} to="/employees/master" onClick={() => navigateFromMenu(() => setEmployeeModuleTab('Employee Master'))}>{menuContent('Employee Master', 'Core HR')}</Link><Link {...navAttrs('Org Structure')} className={employeeTab === 'Org Structure' ? 'active' : ''} to="/employees/org-structure" onClick={() => navigateFromMenu(() => setEmployeeModuleTab('Org Structure'))}>{menuContent('Org Structure', 'Hierarchy')}</Link></>
    if (mainModule === 'Security') return <>{tasks}{securityMenus.map(item => <button {...navAttrs(item)} className={securityTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setSecurityModuleTab(item))} key={item}>{menuContent(item)}</button>)}</>
    if (mainModule === 'Reports') return <>{tasks}{reportingMenus.map(item => {
      const expanded = reportingTab === item
      return <div className={`report-nav-group ${expanded ? 'expanded' : ''} ${collapsedFlyout === `reports-${slug(item)}` ? 'flyout-open' : ''}`} key={item}>
        <button {...navAttrs(item)} className={expanded ? 'active' : ''} type="button" aria-expanded={expanded} onClick={() => toggleNavGroup(`reports-${slug(item)}`, () => setReportingModuleTab(item))}>{menuLabel(item)}<small>{expanded ? '-' : '+'}</small></button>
        {expanded && <div className="report-nav-submenu">{reportItems(item).map(report => <button {...navAttrs(report.name)} className={reportingReport.name === report.name ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setReportingReport(report))} key={report.name}>{menuContent(report.name)}</button>)}</div>}
      </div>
    })}</>
    if (mainModule === 'Workflows') return workflowMenus.map(item => <button {...navAttrs(item)} className={workflowTab === item ? 'active' : ''} type="button" onClick={() => navigateFromMenu(() => setWorkflowModuleTab(item))} key={item}>{menuContent(item)}</button>)
    return <button {...navAttrs('Employee Master')} className="active" type="button" onClick={() => navigateFromMenu(() => setModule('Employees'))}>{menuContent('Employee Master', 'Core HR')}</button>
  }
  const renderPage = () => {
    if (showMyTasks) return <WorkflowTasks />
    if (mainModule === 'Dashboard') return <DashboardPage />
    if (mainModule === 'Security') return <SecurityPanel initialTab={securityTab} />
    if (mainModule === 'LeaveAttendance') return <PayrollAttendancePage />
    if (mainModule === 'Payroll') return isPayHistory ? <PayHistoryPage /> : payrollTab === 'Employee Tax Profile' ? <EmployeeTaxProfileManager /> : <PayrollPage key={payrollTab} mode={payrollTab === 'Adjustments' ? 'adjustments' : 'payrun'} runType={payrollTab === 'Off-cycle Run' ? 'Off-cycle Run' : 'Regular Run'} />
    if (mainModule === 'Employees') return <EmployeePage view={(employeeTab === 'Org Structure' ? 'org' : 'master') as EmployeePageView} />
    if (mainModule === 'Reports') return <ReportingPage activeMenu={reportingTab} activeReport={reportingReport} />
    if (mainModule === 'Workflows') return <WorkflowPage activeMenu={workflowTab} />
    return settingsSection === 'LeaveAttendance' ? <LeaveAttendancePage activeMenu={leaveAttendanceTab} onSelectMenu={setLeaveAttendanceSettingsTab} /> : <SettingsPage tab={tab} onMessage={() => undefined} />
  }

  const shellClassName = ['payroll-app compact module-shell', navOpen ? '' : 'nav-collapsed', appDrawerOpen ? 'drawer-open' : '', mobileShell && navOpen ? 'mobile-nav-open' : ''].filter(Boolean).join(' ')

  return <div className={shellClassName}>
    {mobileShell && navOpen && <button className="sidebar-scrim" type="button" aria-label="Close sidebar" onClick={() => setNavOpen(false)} />}
    <aside className="app-sidebar" ref={sidebarRef}>
      <div className="side-head">
        <a className="brand has-logo product-brand" aria-label="Frevo One HR"><img className="brand-logo product-brand-logo" src={productLogo} alt="Frevo One HR" /><img className="brand-logo product-brand-mark" src={productMark} alt="Frevo One HR" /></a>
        <Button className="sidebar-toggle" type="text" shape="circle" title={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-label={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-expanded={navOpen} icon={navOpen ? <MenuFoldOutlined /> : <MenuUnfoldOutlined />} onClick={() => setNavOpen(open => !open)} />
      </div>
      <div className="sidebar-context"><i><AppIcon name={activeModule.icon} /></i><div><span>Module</span><strong>{activeModule.label}</strong></div></div>
      <nav><div className="submenu context-menu">{renderContextMenu()}</div></nav>
    </aside>
    <main className="compact-main">
      <header className="topbar app-topbar">
        <div className="topbar-title">
          <div className="topbar-title-line">
            {shellOrg.logoDataUrl && <img className="topbar-org-logo" src={shellOrg.logoDataUrl} alt="Organization logo" />}
            <div>
              <Breadcrumb className="topbar-breadcrumb" items={[{ title: activeModule.label }, { title: pageTitle }]} />
              <h2 title={pageTitle}>{pageTitle}</h2>
            </div>
          </div>
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
      <footer className="shell-bottom-brand">
        {shellOrg.logoDataUrl ? <img src={shellOrg.logoDataUrl} alt="Organization logo" /> : <b>{(shellOrg.name || 'O').slice(0, 1).toUpperCase()}</b>}
        <span>{shellOrg.name || 'Organization'}</span>
      </footer>
    </main>
    {appDrawerOpen && <div className="drawer-scrim" onClick={() => setAppDrawerOpen(false)} />}
    <aside className="module-drawer" aria-hidden={!appDrawerOpen}>
      <header><div><span className="eyebrow purple">App Launcher</span><h3>Choose module</h3></div><button type="button" aria-label="Close app modules" onClick={() => setAppDrawerOpen(false)}><AppIcon name="close" /></button></header>
      {modules.map(module => <button className={mainModule === module.code ? 'active' : ''} type="button" disabled={module.disabled} onClick={() => { if (!module.disabled) { setModule(module.code as ModuleCode); setAppDrawerOpen(false) } }} key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></button>)}
    </aside>
  </div>
}

