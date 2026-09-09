import { Fragment, useEffect, useRef, useState, type MouseEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { AppstoreOutlined, BellOutlined, DownOutlined, LogoutOutlined, MenuFoldOutlined, MenuUnfoldOutlined, SearchOutlined, UserOutlined } from '@ant-design/icons'
import { Avatar, Badge, Button, Dropdown, Input, Menu, Space, Tooltip } from 'antd'
import type { MenuProps } from 'antd'
import AppIcon from './components/AppIcon'
import type { IconName } from './components/AppIcon'
import AppPageHeader from './components/layout/AppPageHeader'
import AttachmentSettings from './components/AttachmentSettings'
import EssSettings from './components/EssSettings'
import SecurityPanel from './components/SecurityPanel'
import { appSettingsMenus, leaveAttendanceMenus, org0, reportingMenus, securityMenus, settingsMenus, workflowMenus } from './data/payrollDefaults'
import DashboardPage, { type DashboardView } from './pages/DashboardPage'
import EmployeePage, { type EmployeePageView } from './pages/EmployeePage'
import EmployeeCommunicationPage from './pages/EmployeeCommunicationPage'
import LeaveAttendancePage from './pages/LeaveAttendancePage'
import type { LeaveAttendanceMenu } from './pages/LeaveAttendancePage'
import PayHistoryPage from './pages/PayHistoryPage'
import PayrollAttendancePage from './pages/PayrollAttendancePage'
import PayrollPage from './pages/PayrollPage'
import ReportingPage, { reportItems } from './pages/ReportingPage'
import type { ReportDefinition, ReportingMenu } from './pages/ReportingPage'
import RecruitmentPage, { recruitmentViews, type RecruitmentPageView } from './pages/RecruitmentPage'
import MyProfilePage from './pages/MyProfilePage'
import TravelAdvancesPage from './pages/TravelAdvancesPage'
import WorkflowPage from './pages/WorkflowPage'
import type { WorkflowMenu } from './pages/WorkflowPage'
import EmployeeTaxProfileManager from './components/EmployeeTaxProfileManager'
import WorkflowTasks from './components/WorkflowTasks'
import MailAutomationProvider from './components/MailAutomationProvider'
import { useAuthSession } from './components/AuthGate'
import SettingsPage from './pages/SettingsPage'
import { getOrganization } from './services/settingsService'
import type { Org } from './types/payroll'
import './OrganizationSetup.css'
import './ModuleDrawer.css'
import './SecurityDrawerCompact.css'
import './RecruitmentExperience.css'
import './AppShell.css'

type ModuleCode = 'Dashboard' | 'Settings' | 'Employees' | 'Payroll' | 'LeaveAttendance' | 'TalentAcquisition' | 'Security' | 'Reports' | 'Workflows'
type SettingsTab = (typeof settingsMenus)[number]
type SecurityTab = (typeof securityMenus)[number]
type AppSettingsTab = (typeof appSettingsMenus)[number]
type PayrollTab = 'Regular Run' | 'Off-cycle Run' | 'Adjustments' | 'Employee Tax Profile' | 'Travel Advances'
type EmployeeTab = 'Employee Master' | 'Employee Communication' | 'Org Structure'
type SettingsSection = 'General' | 'LeaveAttendance'
const allPayrollSetupMenus: SettingsTab[] = ['Tax Engine', 'Statutory Setup', 'Salary Components', 'Salary Templates', 'Payslip Templates']
const compactSidebarQuery = '(max-width: 640px)'
const productLogo = '/assets/FrevoOneLogo.png'
const productMark = '/favicon.svg'
const dashboardViews: DashboardView[] = ['overview', 'workforce', 'payroll', 'attendance', 'approvals']
const fallbackDashboardAccess = [{ code: 'overview', name: 'Overview Dashboard', description: 'Combined HR, payroll, attendance and approval summary.', route: '/dashboard', sortOrder: 10 }]
type RecruitmentNavigationLeaf = { view: RecruitmentPageView; label: string; icon: IconName }
type RecruitmentNavigationItem = RecruitmentNavigationLeaf | { key: string; label: string; icon: IconName; children: RecruitmentNavigationLeaf[] }
const recruitmentNavigation: Array<{
  key: string
  label: string
  icon: IconName
  children: RecruitmentNavigationItem[]
}> = [
  {
    key: 'recruitment-workspaces',
    label: 'Talent Acquisition',
    icon: 'talent',
    children: [
      { view: 'Dashboard', label: 'Overview', icon: 'dashboard' },
      { view: 'Hiring Pipeline', label: 'Pipeline', icon: 'pipeline' },
      { view: 'Requisitions', label: 'Hiring Requests', icon: 'request' },
      { view: 'Job Descriptions', label: 'Jobs', icon: 'job' },
      { view: 'ATS Screening', label: 'ATS & resume intake', icon: 'resume' },
      {
        key: 'candidates', label: 'Candidates', icon: 'candidate', children: [
          { view: 'Applications', label: 'Applications', icon: 'application' },
          { view: 'Talent Pool', label: 'Talent profiles', icon: 'user' },
        ],
      },
      { view: 'Interviews', label: 'Selection & Onboarding', icon: 'onboarding' },
    ],
  },
]
const recruitmentNavigationLeaves = recruitmentNavigation.flatMap(group => group.children.flatMap(item => 'view' in item ? [item] : item.children))
const recruitmentNavigationView = (view: RecruitmentPageView): RecruitmentPageView => {
  if (view === 'Open Positions') return 'Requisitions'
  if (view === 'Job Postings') return 'Job Descriptions'
  if (view === 'Work Orders & SLA') return 'Hiring Pipeline'
  if (view === 'Offers & Pre-Onboarding') return 'Interviews'
  return view
}
const recruitmentNavigationLabel = (view: RecruitmentPageView) => {
  const canonicalView = recruitmentNavigationView(view)
  return recruitmentNavigationLeaves.find(item => item.view === canonicalView)?.label ?? view
}

const recruitmentDescriptions: Record<string, string> = {
  Overview: 'Hiring demand, approvals, open roles and joining targets in one concise view.',
  'Hiring Requests': 'Raise hiring demand and follow every request through approval to an open vacancy.',
  Jobs: 'Prepare governed role profiles and publish approved openings through the right channels.',
  Candidates: 'Manage reusable talent profiles, applications and explainable ATS screening.',
  Applications: 'Review applications received through published job links and position-linked manual resume uploads.',
  'Talent profiles': 'Maintain reusable candidate profiles, resumes, experience and consent details.',
  'ATS & resume intake': 'Upload resumes and review explainable ATS screening results.',
  Pipeline: 'Follow client demand and candidates through each stage of the hiring journey.',
  'Selection & Onboarding': 'Coordinate interviews, offers, documents and joining readiness in one workspace.',
}

const surfaceDescriptions: Record<string, string> = {
  Organization: 'Maintain company identity, registered offices and statutory organization details.',
  Clients: 'Manage client entities, payroll scope and operational ownership.',
  'Work Locations': 'Maintain client locations used across employee, attendance and payroll processes.',
  'Dropdown Masters': 'Govern reusable master values used by forms and business processes.',
  Attachments: 'Configure secure document categories, storage behavior and verification requirements.',
  'Tax Engine': 'Configure income-tax rules, slabs and calculation behavior for payroll.',
  'Statutory Setup': 'Maintain provident fund, insurance and statutory payroll configuration.',
  'Client Billing Configuration': 'Define payroll billing rules, rates and client charge behavior.',
  'Travel & Expense Policies': 'Configure eligibility, limits and approval rules for employee claims.',
  'Salary Components': 'Maintain earnings, deductions, benefits and calculation behavior.',
  'Salary Templates': 'Build reusable salary structures from approved payroll components.',
  'Payslip Templates': 'Configure the presentation and sections used in employee payslips.',
  Notifications: 'Manage delivery channels, templates, rules and message audit visibility.',
  'Scheduled Jobs': 'Review recurring background jobs, schedules and recent execution status.',
  'Attendance Policies': 'Define attendance rules, shifts and payability behavior by client.',
  'Leave Types': 'Maintain leave categories, eligibility and accrual rules.',
  Holiday: 'Maintain client calendars and location-specific holidays.',
  Attendance: 'Configure attendance processing and source behavior.',
  'Geo-Fencing': 'Define permitted attendance locations and geographic boundaries.',
  'Import Balance': 'Validate and import employee opening leave balances.',
  'Employee Master': 'Maintain searchable employee records, statutory details and salary information.',
  'Employee Communication': 'Manage private employee conversations and targeted communication campaigns.',
  'Org Structure': 'Review reporting lines, organizational units and client hierarchy.',
  'Attendance Review': 'Review attendance readiness, exceptions and payability before payroll processing.',
  Users: 'Manage user identities, access scope and account status.',
  Roles: 'Define role-based capabilities and business access boundaries.',
  Audit: 'Review immutable security and administrative activity evidence.',
  'ESS Settings': 'Configure employee self-service access and supported capabilities.',
  'Storage Servers': 'Configure governed storage providers for HRMS documents.',
  'Workflow Setup': 'Define approval activities, approvers and the actions that start them.',
  'API Catalog': 'Review supported request paths when connecting screen actions to workflows.',
  'Department Head Assignments': 'Map each department to the user responsible for its approvals.',
  'My Tasks': 'Review assigned approvals and complete the work currently waiting for you.',
  'Workflow History': 'Review workflow requests, current outcomes and their complete approval trail.',
  'Regular Run': 'Prepare, validate and process the regular payroll cycle.',
  'Off-cycle Run': 'Process controlled payroll outside the regular cycle.',
  Adjustments: 'Import and review controlled payroll adjustments before final processing.',
  'Employee Tax Profile': 'Review declarations and employee-level income-tax configuration.',
  'Travel Advances': 'Manage employee travel advances, settlement and payroll recovery.',
  'Pay History': 'Review completed and in-progress payroll runs with their payment trail.',
  'My Profile': 'Review your account details, employment documents and personal preferences.',
}

const modules: { code: ModuleCode | 'Reports'; label: string; icon: IconName; description: string; disabled?: boolean }[] = [
  { code: 'Dashboard', label: 'Dashboard', icon: 'dashboard', description: 'Client-wise payroll, attendance and approval overview.' },
  { code: 'Payroll', label: 'Payroll', icon: 'payruns', description: 'Run payroll, compare variances and review history.' },
  { code: 'LeaveAttendance', label: 'Leave & Attendance', icon: 'calendar', description: 'Review attendance before payroll processing.' },
  { code: 'Employees', label: 'Employees', icon: 'employees', description: 'Manage employee master, salary and statutory profile.' },
  { code: 'TalentAcquisition', label: 'Talent Acquisition', icon: 'job', description: 'Monitor requisitions and open positions.' },
  { code: 'Security', label: 'Security', icon: 'security', description: 'Control users, roles, permissions and audit evidence.' },
  { code: 'Workflows', label: 'Workflows', icon: 'workflow', description: 'Configure reusable approval workflows and tasks.' },
  { code: 'Settings', label: 'Settings', icon: 'settings', description: 'Configure organization, clients and payroll setup.' },
  { code: 'Reports', label: 'Reports', icon: 'reports', description: 'Client-scoped reporting and analytics.' }
]

const menuInitial = (label: string) => (label.match(/[A-Za-z0-9]+/g) ?? [])
  .filter(word => !['and', 'of', 'the'].includes(word.toLowerCase()))
  .slice(0, 2)
  .map(word => word[0].toUpperCase())
  .join('') || '*'
const navAttrs = (label: string) => ({ title: label, 'data-initial': menuInitial(label) })
const menuIcon = (label: string): IconName => {
  const value = label.toLowerCase()
  if (value.includes('task')) return 'tasks'
  if (value.includes('dashboard')) return 'dashboard'
  if (value.includes('adjust')) return 'adjustments'
  if (value.includes('run') || value.includes('pay run')) return 'run'
  if (value.includes('history')) return 'history'
  if (value.includes('tax') || value.includes('statutory') || value.includes('tds')) return 'tax'
  if (value.includes('employee')) return 'employees'
  if (value.includes('recruit') || value.includes('talent') || value.includes('position')) return 'employees'
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
  if (value.includes('advance')) return 'money'
  if (value.includes('payroll') || value.includes('pay')) return 'payruns'
  if (value.includes('report') || value.includes('mis') || value.includes('dashboard')) return 'reports'
  if (value.includes('security')) return 'security'
  if (value.includes('user')) return 'user'
  if (value.includes('role')) return 'role'
  if (value.includes('audit')) return 'shield'
  if (value.includes('setting')) return 'settings'
  return 'document'
}
const menuLabel = (label: string, icon: IconName | null = menuIcon(label)) => <span className="menu-label" data-initial={menuInitial(label)}>{icon && <AppIcon name={icon} />}<span>{label}</span></span>
const menuContent = (label: string, detail?: string, icon: IconName | null = menuIcon(label)) => <>{menuLabel(label, icon)}{detail && <small>{detail}</small>}</>
const payrollSetupIcons: Partial<Record<SettingsTab, IconName>> = {
  'Tax Engine': 'tax',
  'Statutory Setup': 'shield',
  'Salary Components': 'component',
  'Salary Templates': 'template',
  'Payslip Templates': 'payslip',
}
const leaveAttendanceIcons: Record<LeaveAttendanceMenu, IconName> = {
  'Attendance Policies': 'settings',
  'Leave Types': 'calendar',
  Holiday: 'holiday',
  Attendance: 'attendance',
  'Geo-Fencing': 'location',
  'Import Balance': 'document',
}
const appSettingsIcons: Record<AppSettingsTab, IconName> = {
  'ESS Settings': 'apps',
  'Storage Servers': 'server',
}
const generalSettingsIcons: Partial<Record<SettingsTab, IconName>> = {
  Organization: 'org',
  Clients: 'building',
  'Work Locations': 'location',
  'Dropdown Masters': 'dropdown',
  Attachments: 'paperclip',
  'Client Billing Configuration': 'billing',
  'Travel & Expense Policies': 'money',
  Notifications: 'notification',
  'Scheduled Jobs': 'history',
}
const employeeMenuIcons: Record<EmployeeTab, IconName> = {
  'Employee Master': 'employees',
  'Employee Communication': 'notification',
  'Org Structure': 'org',
}
const securityMenuIcons: Record<SecurityTab, IconName> = { Users: 'user', Roles: 'role', Audit: 'shield' }
const workflowMenuIcons: Record<WorkflowMenu, IconName> = {
  'Workflow Setup': 'workflow',
  'API Catalog': 'api',
  'Department Head Assignments': 'org',
  'My Tasks': 'tasks',
  'Workflow History': 'history',
}

const slug = (value: string) => value.toLowerCase().replace(/&/g, 'and').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
const fromSlug = <T extends string>(items: readonly T[], value: string | undefined, fallback: T) => items.find(item => slug(item) === value) ?? fallback
const menuClickIsNewTabIntent = (event: MouseEvent<HTMLAnchorElement>) => event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey
const modulePaths: Record<ModuleCode, string> = {
  Dashboard: '/dashboard',
  Payroll: '/payroll/regular',
  LeaveAttendance: '/attendance',
  Employees: '/employees/master',
  TalentAcquisition: '/recruitment/dashboard',
  Security: '/security/users',
  Workflows: '/workflows/workflow-setup',
  Settings: '/settings/organization',
  Reports: '/reports/payroll-reports'
}

export default function SettingsApp() {
  const session = useAuthSession()
  const sidebarRef = useRef<HTMLElement | null>(null)
  const canManageStatutory = Boolean(session?.user.permissions.includes('tax.statutory.manage'))
  const canViewEmployeeCommunication = Boolean(session?.user.permissions.includes('employee.communication.view'))
  const routeLocation = useLocation()
  const navigate = useNavigate()
  const isPayHistory = routeLocation.pathname === '/pay-runs/history'
  const isProfile = routeLocation.pathname === '/profile'
  const savedTab = localStorage.getItem('payroll.tab') as SettingsTab | null
  const savedSecurityTab = localStorage.getItem('payroll.securityTab') as SecurityTab | null
  const savedLeaveAttendanceTab = localStorage.getItem('payroll.leaveAttendanceTab') as LeaveAttendanceMenu | null
  const savedReportingTab = localStorage.getItem('payroll.reportingTab') as ReportingMenu | null
  const savedWorkflowTab = localStorage.getItem('payroll.workflowTab') as WorkflowMenu | null
  const payrollSetupMenus = allPayrollSetupMenus.filter(item => item !== 'Statutory Setup' || canManageStatutory)
  const routeParts = routeLocation.pathname.split('/').filter(Boolean)
  const dashboardView = routeParts[0] === 'dashboard' && dashboardViews.includes(routeParts[1] as DashboardView) ? routeParts[1] as DashboardView : 'overview'
  const recruitmentView = routeParts[0] === 'recruitment' ? fromSlug(recruitmentViews, routeParts[1], 'Dashboard') : 'Dashboard'
  const securityAppSettingsTab: AppSettingsTab | null = routeParts[0] === 'security' && routeParts[1] === 'app-settings'
    ? fromSlug(appSettingsMenus, routeParts[2], 'ESS Settings')
    : null
  const routeModule = (() : ModuleCode => {
    if (routeParts[0] === 'pay-runs' || routeParts[0] === 'payroll') return 'Payroll'
    if (routeParts[0] === 'attendance') return 'LeaveAttendance'
    if (routeParts[0] === 'employees') return 'Employees'
    if (routeParts[0] === 'recruitment') return 'TalentAcquisition'
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
  const [securityAppSettingsOpen, setSecurityAppSettingsOpen] = useState(() => securityAppSettingsTab !== null)
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
  const currentUser = session?.user
  const dashboardAccess = [...(currentUser?.dashboardAccess?.length ? currentUser.dashboardAccess : fallbackDashboardAccess)].sort((left, right) => left.sortOrder - right.sortOrder)
  const activeDashboard = dashboardAccess.find(item => item.code === dashboardView) ?? dashboardAccess[0]
  const dashboardMenu: MenuProps = {
    items: dashboardAccess.map(item => ({ key: item.code, label: <div className="dashboard-switch-item"><strong>{item.name}</strong><small>{item.description}</small></div> })),
    selectedKeys: [activeDashboard.code],
    onClick: ({ key }) => {
      const item = dashboardAccess.find(option => option.code === key)
      if (!item) return
      setShowMyTasks(false)
      setMainModule('Dashboard')
      navigate(item.route || '/dashboard')
    }
  }
  const pageTitle = isProfile ? 'My Profile' : showMyTasks ? 'My Tasks' : mainModule === 'Dashboard' ? activeDashboard.name : mainModule === 'Settings' ? settingsSection === 'LeaveAttendance' ? leaveAttendanceTab : tab : mainModule === 'LeaveAttendance' ? 'Attendance Review' : mainModule === 'Employees' ? employeeTab : mainModule === 'TalentAcquisition' ? recruitmentNavigationLabel(recruitmentView) : mainModule === 'Security' ? securityAppSettingsTab ?? securityTab : mainModule === 'Reports' ? reportingReport.name : mainModule === 'Workflows' ? workflowTab : isPayHistory ? 'Pay History' : mainModule === 'Payroll' ? payrollTab : 'Pay Run'
  const breadcrumbItems = isProfile
    ? [{ title: 'Account' }, { title: pageTitle }]
    : mainModule === 'Reports'
      ? [{ title: activeModule.label }, { title: reportingTab }, { title: pageTitle }]
      : mainModule === 'Security' && securityAppSettingsTab
      ? [{ title: activeModule.label }, { title: 'App Settings' }, { title: pageTitle }]
    : [{ title: activeModule.label }, { title: pageTitle }]
  const pageDescription = isProfile
    ? surfaceDescriptions['My Profile']
    : showMyTasks
      ? surfaceDescriptions['My Tasks']
      : mainModule === 'Dashboard'
        ? activeDashboard.description
        : mainModule === 'Reports'
          ? reportingReport.code ? `Refine client and period filters, review ${reportingReport.name.toLowerCase()} data and export the current view.` : 'This report is configured and will become available when its source module is enabled.'
          : mainModule === 'TalentAcquisition'
            ? recruitmentDescriptions[pageTitle] ?? activeModule.description
            : surfaceDescriptions[pageTitle] ?? activeModule.description
  const pageIconName: IconName = isProfile ? 'user' : showMyTasks ? 'tasks' : activeModule.icon
  const userInitials = (currentUser?.displayName || 'User').split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase()
  const accountMenu: MenuProps = {
    items: [
      { key: 'account-info', disabled: true, label: <div className="account-menu-card"><strong>{currentUser?.displayName}</strong><small>{currentUser?.email}</small><span>{currentUser?.roles.join(', ')}</span></div> },
      { key: 'profile', icon: <UserOutlined />, label: 'My Profile & Documents' },
      { type: 'divider' },
      { key: 'logout', danger: true, icon: <LogoutOutlined />, label: 'Logout' }
    ],
    onClick: ({ key }) => {
      if (key === 'profile') navigate('/profile')
      if (key === 'logout') void session?.logout()
    }
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
      const nextTab: PayrollTab = parts[1] === 'adjustments' ? 'Adjustments' : parts[1] === 'off-cycle' ? 'Off-cycle Run' : parts[1] === 'tax-profile' ? 'Employee Tax Profile' : parts[1] === 'travel-advances' ? 'Travel Advances' : 'Regular Run'
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
      setEmployeeTab(parts[1] === 'org-structure' ? 'Org Structure' : parts[1] === 'communications' ? 'Employee Communication' : 'Employee Master')
      setMainModule('Employees')
      return
    }
    if (parts[0] === 'recruitment') {
      setMainModule('TalentAcquisition')
      return
    }
    if (parts[0] === 'security') {
      if (parts[1] === 'app-settings') {
        setSecurityAppSettingsOpen(true)
        setMainModule('Security')
        return
      }
      const nextTab = fromSlug(securityMenus, tabSlug, 'Users')
      setSecurityTab(nextTab)
      localStorage.setItem('payroll.securityTab', nextTab)
      setMainModule('Security')
      return
    }
    if (parts[0] === 'reports') {
      const nextTab = fromSlug(reportingMenus, tabSlug, 'Payroll Reports')
      const reports = reportItems(nextTab)
      const reportSlug = new URLSearchParams(routeLocation.search).get('report') || ''
      const nextReport = reports.find(report => slug(report.name) === reportSlug) ?? reports[0]
      setReportingTab(nextTab)
      setReportingReport(nextReport)
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
      if (parts[1] === 'recruitment-administration') {
        const legacySection = parts[2] || ''
        const legacyParams = new URLSearchParams(routeLocation.search)
        if (!legacySection || legacySection === 'ats') {
          legacyParams.set('manage', '1')
          legacyParams.delete('tool')
          navigate(`/recruitment/ats-screening?${legacyParams.toString()}`, { replace: true })
          return
        }
        if (legacySection === 'forms' || legacySection === 'templates') {
          legacyParams.set('manage', '1')
          legacyParams.set('tool', legacySection)
          navigate(`/recruitment/hiring-pipeline?${legacyParams.toString()}`, { replace: true })
          return
        }
        if (legacySection === 'sla') {
          navigate(`/recruitment/work-orders-and-sla${routeLocation.search}`, { replace: true })
          return
        }
        if (legacySection === 'checklist') {
          navigate(`/recruitment/open-positions${routeLocation.search}`, { replace: true })
          return
        }
        if (legacySection === 'approvals') {
          navigate('/workflows/workflow-setup', { replace: true })
          return
        }
        if (legacySection === 'pipelines') {
          legacyParams.set('manage', '1')
          legacyParams.delete('tool')
          navigate(`/recruitment/hiring-pipeline?${legacyParams.toString()}`, { replace: true })
          return
        }
        if (['settings', 'consultants', 'vendors', 'assignment'].includes(legacySection)) {
          navigate(`/recruitment/requisitions${routeLocation.search}`, { replace: true })
          return
        }
      }
      if (parts[1] === 'ess-settings') {
        navigate('/security/app-settings/ess-settings', { replace: true })
        return
      }
      if (parts[1] === 'leave-attendance') {
        const nextTab = fromSlug(leaveAttendanceMenus, parts[2], 'Attendance Policies')
        setSettingsSection('LeaveAttendance')
        setPayrollSetupOpen(false)
        setLeaveAttendanceOpen(true)
        setLeaveAttendanceTab(nextTab)
        localStorage.setItem('payroll.leaveAttendanceTab', nextTab)
      } else {
        const nextTab = fromSlug(settingsMenus, tabSlug, 'Organization')
        const allowedTab = nextTab === 'Statutory Setup' && !canManageStatutory ? 'Organization' : nextTab
        setSettingsSection('General')
        setActiveTab(allowedTab)
        setLeaveAttendanceOpen(false)
        setPayrollSetupOpen(allPayrollSetupMenus.includes(allowedTab))
        localStorage.setItem('payroll.tab', allowedTab)
      }
      setMainModule('Settings')
      return
    }
    setMainModule('Dashboard')
  }, [canManageStatutory, navigate, routeLocation.pathname, routeLocation.search])

  useEffect(() => {
    if (!navOpen) return
    const frame = window.requestAnimationFrame(() => {
      const nav = sidebarRef.current?.querySelector<HTMLElement>('nav')
      const activeDestination = nav?.querySelector<HTMLElement>('a.active, .ant-menu-item-selected')
      if (!nav || !activeDestination) return
      const navRect = nav.getBoundingClientRect()
      const activeRect = activeDestination.getBoundingClientRect()
      if (activeRect.top < navRect.top + 8) nav.scrollTop -= navRect.top + 8 - activeRect.top
      if (activeRect.bottom > navRect.bottom - 8) nav.scrollTop += activeRect.bottom - navRect.bottom + 8
    })
    return () => window.cancelAnimationFrame(frame)
  }, [leaveAttendanceOpen, navOpen, payrollSetupOpen, reportingTab, routeLocation.pathname, routeLocation.search, securityAppSettingsOpen])

  const navigateFromMenu = (action: () => void) => {
    action()
    setCollapsedFlyout(null)
    if (mobileShell) setNavOpen(false)
  }

  const menuLink = (to: string, label: string, active: boolean, action: () => void, detail?: string, icon?: IconName | null) =>
    <Link {...navAttrs(label)} className={active ? 'active' : ''} to={to} onClick={event => {
      if (menuClickIsNewTabIntent(event)) return
      navigateFromMenu(action)
    }}>{menuContent(label, detail, icon === undefined ? menuIcon(label) : icon)}</Link>

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
  const setPayrollModuleTab = (nextTab: PayrollTab) => { localStorage.setItem('payroll.payrollTab', nextTab); setPayrollTab(nextTab); setShowMyTasks(false); setMainModule('Payroll'); navigate(nextTab === 'Adjustments' ? '/payroll/adjustments' : nextTab === 'Off-cycle Run' ? '/payroll/off-cycle' : nextTab === 'Employee Tax Profile' ? '/payroll/tax-profile' : nextTab === 'Travel Advances' ? '/payroll/travel-advances' : '/payroll/regular') }
  const setEmployeeModuleTab = (nextTab: EmployeeTab) => { setEmployeeTab(nextTab); setShowMyTasks(false); setMainModule('Employees'); navigate(nextTab === 'Org Structure' ? '/employees/org-structure' : nextTab === 'Employee Communication' ? '/employees/communications' : '/employees/master') }
  const setRecruitmentModuleView = (nextView: RecruitmentPageView) => { setShowMyTasks(false); setMainModule('TalentAcquisition'); localStorage.setItem('payroll.module', 'TalentAcquisition'); navigate(`/recruitment/${slug(nextView)}`) }
  const setPayHistory = () => { setShowMyTasks(false); localStorage.setItem('payroll.module', 'Payroll'); setMainModule('Payroll'); navigate('/pay-runs/history') }
  const setSecurityModuleTab = (nextTab: SecurityTab) => { localStorage.setItem('payroll.securityTab', nextTab); setSecurityTab(nextTab); setShowMyTasks(false); setMainModule('Security'); navigate(`/security/${slug(nextTab)}`) }
  const setSecurityAppSettingsTab = (nextTab: AppSettingsTab) => { setSecurityAppSettingsOpen(true); setShowMyTasks(false); setMainModule('Security'); navigate(`/security/app-settings/${slug(nextTab)}`) }
  const setLeaveAttendanceSettingsTab = (nextTab: LeaveAttendanceMenu) => { setShowMyTasks(false); setSettingsSection('LeaveAttendance'); localStorage.setItem('payroll.module', 'Settings'); localStorage.setItem('payroll.leaveAttendanceTab', nextTab); setLeaveAttendanceTab(nextTab); setMainModule('Settings'); navigate(`/settings/leave-attendance/${slug(nextTab)}`) }
  const setReportingModuleTab = (nextTab: ReportingMenu) => { localStorage.setItem('payroll.reportingTab', nextTab); setReportingTab(nextTab); setReportingReport(reportItems(nextTab)[0]); setShowMyTasks(false); setMainModule('Reports'); navigate(`/reports/${slug(nextTab)}`) }
  const setReportingReportTab = (nextTab: ReportingMenu, report: ReportDefinition) => { localStorage.setItem('payroll.reportingTab', nextTab); setReportingTab(nextTab); setReportingReport(report); setShowMyTasks(false); setMainModule('Reports'); navigate(`/reports/${slug(nextTab)}?report=${slug(report.name)}`) }
  const setWorkflowModuleTab = (nextTab: WorkflowMenu) => { localStorage.setItem('payroll.workflowTab', nextTab); setWorkflowTab(nextTab); setShowMyTasks(false); setMainModule('Workflows'); navigate(`/workflows/${slug(nextTab)}`) }
  const renderContextMenu = () => {
    const tasks = menuLink('/tasks', 'My Tasks', showMyTasks, openTasks, 'Approvals')
    if (mainModule === 'Dashboard') return <>
      {menuLink('/dashboard', 'Dashboard', !showMyTasks, () => setModule('Dashboard'), 'Overview')}
      {tasks}
    </>
    if (mainModule === 'Settings') {
      const generalSettings = settingsMenus.filter(item => !payrollSetupMenus.includes(item))
      const payrollSetupActive = payrollSetupMenus.includes(tab)
      return <>
        {tasks}
        {generalSettings.map(item => <Fragment key={item}>{menuLink(`/settings/${slug(item)}`, item, settingsSection === 'General' && tab === item, () => setTab(item), undefined, generalSettingsIcons[item])}</Fragment>)}
        <div className={`settings-nav-group flyout-align-end ${payrollSetupOpen ? 'expanded' : ''} ${collapsedFlyout === 'settings-payroll' ? 'flyout-open' : ''}`}>
          <button {...navAttrs('Payroll Setup')} className={settingsSection === 'General' && payrollSetupActive ? 'active' : ''} type="button" aria-expanded={payrollSetupOpen} onClick={() => toggleNavGroup('settings-payroll', () => { setLeaveAttendanceOpen(false); setPayrollSetupOpen(open => navOpen ? !open : true) })}>{menuLabel('Payroll Setup', null)}<small>{payrollSetupOpen ? '-' : '+'}</small></button>
          {payrollSetupOpen && <div className="settings-nav-submenu">{payrollSetupMenus.map(item => <Fragment key={item}>{menuLink(`/settings/${slug(item)}`, item, settingsSection === 'General' && tab === item, () => setTab(item), item === 'Salary Templates' ? 'Client-wise' : undefined, payrollSetupIcons[item])}</Fragment>)}</div>}
        </div>
        <div className={`settings-nav-group flyout-align-end ${leaveAttendanceOpen ? 'expanded' : ''} ${collapsedFlyout === 'settings-leave-attendance' ? 'flyout-open' : ''}`}>
          <button {...navAttrs('Leave & Attendance')} className={settingsSection === 'LeaveAttendance' ? 'active' : ''} type="button" aria-expanded={leaveAttendanceOpen} onClick={() => toggleNavGroup('settings-leave-attendance', () => { setPayrollSetupOpen(false); setLeaveAttendanceOpen(open => navOpen ? !open : true) })}>{menuLabel('Leave & Attendance', null)}<small>{leaveAttendanceOpen ? '-' : '+'}</small></button>
          {leaveAttendanceOpen && <div className="settings-nav-submenu">{leaveAttendanceMenus.map(item => <Fragment key={item}>{menuLink(`/settings/leave-attendance/${slug(item)}`, item, settingsSection === 'LeaveAttendance' && leaveAttendanceTab === item, () => setLeaveAttendanceSettingsTab(item), undefined, leaveAttendanceIcons[item])}</Fragment>)}</div>}
        </div>
      </>
    }
    if (mainModule === 'Payroll') return <>
      {tasks}
      {menuLink('/payroll/adjustments', 'Adjustments', !isPayHistory && payrollTab === 'Adjustments', () => setPayrollModuleTab('Adjustments'), 'Variable pay')}
      <div className={`settings-nav-group expanded ${collapsedFlyout === 'payroll-run' ? 'flyout-open' : ''}`}><button {...navAttrs('Pay Run')} className={!isPayHistory && ['Regular Run', 'Off-cycle Run'].includes(payrollTab) ? 'active' : ''} type="button" onClick={() => toggleNavGroup('payroll-run', () => undefined)}>{menuLabel('Pay Run', null)}</button><div className="settings-nav-submenu">{menuLink('/payroll/regular', 'Regular Run', !isPayHistory && payrollTab === 'Regular Run', () => setPayrollModuleTab('Regular Run'), undefined, 'run')}{menuLink('/payroll/off-cycle', 'Off-cycle Run', !isPayHistory && payrollTab === 'Off-cycle Run', () => setPayrollModuleTab('Off-cycle Run'), undefined, 'offcycle')}</div></div>
      {menuLink('/payroll/tax-profile', 'Employee Tax Profile', !isPayHistory && payrollTab === 'Employee Tax Profile', () => setPayrollModuleTab('Employee Tax Profile'), 'TDS profile')}
      {menuLink('/payroll/travel-advances', 'Travel Advances', !isPayHistory && payrollTab === 'Travel Advances', () => setPayrollModuleTab('Travel Advances'), 'Pay & settle')}
      {menuLink('/pay-runs/history', 'Pay History', isPayHistory, setPayHistory)}
    </>
    if (mainModule === 'LeaveAttendance') return <>{tasks}{menuLink('/attendance', 'Attendance Review', true, () => setModule('LeaveAttendance'), 'Pre-payroll')}</>
    if (mainModule === 'Employees') return <>{tasks}{menuLink('/employees/master', 'Employee Master', employeeTab === 'Employee Master', () => setEmployeeModuleTab('Employee Master'), 'Core HR', employeeMenuIcons['Employee Master'])}{canViewEmployeeCommunication && menuLink('/employees/communications', 'Employee Communication', employeeTab === 'Employee Communication', () => setEmployeeModuleTab('Employee Communication'), 'Engage', employeeMenuIcons['Employee Communication'])}{menuLink('/employees/org-structure', 'Org Structure', employeeTab === 'Org Structure', () => setEmployeeModuleTab('Org Structure'), 'Hierarchy', employeeMenuIcons['Org Structure'])}</>
    if (mainModule === 'TalentAcquisition') return <>
      {tasks}
      <Menu
        className="recruitment-nav-menu"
        mode="inline"
        inlineIndent={14}
        selectedKeys={[slug(recruitmentNavigationView(recruitmentView))]}
        defaultOpenKeys={[...recruitmentNavigation.map(group => `recruitment-${group.key}`), 'recruitment-candidates']}
        items={recruitmentNavigation.map(group => ({
          key: `recruitment-${group.key}`,
          label: menuLabel(group.label, group.icon),
          children: group.children.map(item => 'view' in item
            ? { key: slug(item.view), label: menuLabel(item.label, item.icon) }
            : {
                key: `recruitment-${item.key}`,
                label: menuLabel(item.label, item.icon),
                children: item.children.map(child => ({ key: slug(child.view), label: menuLabel(child.label, child.icon) })),
              }),
        }))}
        onClick={({ key }) => {
          const next = recruitmentViews.find(item => slug(item) === key)
          if (next) setRecruitmentModuleView(next)
        }}
      />
    </>
    if (mainModule === 'Security') return <>
      {tasks}
      {securityMenus.map(item => <Fragment key={item}>{menuLink(`/security/${slug(item)}`, item, !securityAppSettingsTab && securityTab === item, () => setSecurityModuleTab(item), undefined, securityMenuIcons[item])}</Fragment>)}
      <div className={`settings-nav-group flyout-align-end ${securityAppSettingsOpen ? 'expanded' : ''} ${collapsedFlyout === 'security-app-settings' ? 'flyout-open' : ''}`}>
        <button {...navAttrs('App Settings')} className={securityAppSettingsTab ? 'active' : ''} type="button" aria-expanded={securityAppSettingsOpen} onClick={() => toggleNavGroup('security-app-settings', () => setSecurityAppSettingsOpen(open => navOpen ? !open : true))}>{menuLabel('App Settings', null)}<small>{securityAppSettingsOpen ? '-' : '+'}</small></button>
        {securityAppSettingsOpen && <div className="settings-nav-submenu">{appSettingsMenus.map(item => <Fragment key={item}>{menuLink(`/security/app-settings/${slug(item)}`, item, securityAppSettingsTab === item, () => setSecurityAppSettingsTab(item), undefined, appSettingsIcons[item])}</Fragment>)}</div>}
      </div>
    </>
    if (mainModule === 'Reports') return <>{tasks}{reportingMenus.map(item => {
      const expanded = reportingTab === item
      return <div className={`report-nav-group ${['Leave Reports', 'Tax Reports'].includes(item) ? 'flyout-align-end' : ''} ${expanded ? 'expanded' : ''} ${collapsedFlyout === `reports-${slug(item)}` ? 'flyout-open' : ''}`} key={item}>
        <button {...navAttrs(item)} className={expanded ? 'active' : ''} type="button" aria-expanded={expanded} onClick={() => toggleNavGroup(`reports-${slug(item)}`, () => setReportingModuleTab(item))}>{menuLabel(item, null)}<small>{expanded ? '-' : '+'}</small></button>
        {expanded && <div className="report-nav-submenu">{reportItems(item).map(report => <Fragment key={report.name}>{menuLink(`/reports/${slug(item)}?report=${slug(report.name)}`, report.name, reportingReport.name === report.name, () => setReportingReportTab(item, report), undefined, null)}</Fragment>)}</div>}
      </div>
    })}</>
    if (mainModule === 'Workflows') return workflowMenus.map(item => <Fragment key={item}>{menuLink(`/workflows/${slug(item)}`, item, workflowTab === item, () => setWorkflowModuleTab(item), undefined, workflowMenuIcons[item])}</Fragment>)
    return menuLink('/employees/master', 'Employee Master', true, () => setModule('Employees'), 'Core HR')
  }
  const renderPage = () => {
    if (isProfile && currentUser) return <MyProfilePage user={currentUser} />
    if (showMyTasks) return <WorkflowTasks />
    if (mainModule === 'Dashboard') return <DashboardPage view={dashboardView} />
    if (mainModule === 'Security') return securityAppSettingsTab === 'ESS Settings' ? <EssSettings /> : securityAppSettingsTab === 'Storage Servers' ? <AttachmentSettings mode="storage" /> : <SecurityPanel initialTab={securityTab} />
    if (mainModule === 'LeaveAttendance') return <PayrollAttendancePage />
    if (mainModule === 'Payroll') return isPayHistory ? <PayHistoryPage /> : payrollTab === 'Employee Tax Profile' ? <EmployeeTaxProfileManager /> : payrollTab === 'Travel Advances' ? <TravelAdvancesPage /> : <PayrollPage key={payrollTab} mode={payrollTab === 'Adjustments' ? 'adjustments' : 'payrun'} runType={payrollTab === 'Off-cycle Run' ? 'Off-cycle Run' : 'Regular Run'} />
    if (mainModule === 'Employees') return employeeTab === 'Employee Communication' ? <EmployeeCommunicationPage /> : <EmployeePage view={(employeeTab === 'Org Structure' ? 'org' : 'master') as EmployeePageView} />
    if (mainModule === 'TalentAcquisition') return <RecruitmentPage view={recruitmentView} />
    if (mainModule === 'Reports') return <ReportingPage activeMenu={reportingTab} activeReport={reportingReport} />
    if (mainModule === 'Workflows') return <WorkflowPage activeMenu={workflowTab} />
    return settingsSection === 'LeaveAttendance' ? <LeaveAttendancePage activeMenu={leaveAttendanceTab} onSelectMenu={setLeaveAttendanceSettingsTab} /> : <SettingsPage tab={tab} onMessage={() => undefined} />
  }

  const shellClassName = ['hrms-shell', navOpen ? '' : 'rail-collapsed', appDrawerOpen ? 'drawer-open' : '', mobileShell && navOpen ? 'mobile-nav-open' : ''].filter(Boolean).join(' ')

  return <div className={shellClassName}>
    <MailAutomationProvider enabled={Boolean(currentUser?.permissions.includes('settings.manage'))} />
    {mobileShell && navOpen && <button className="hrms-sidebar-scrim" type="button" aria-label="Close sidebar" onClick={() => setNavOpen(false)} />}
    <aside className="hrms-sidebar" ref={sidebarRef}>
      <div className="hrms-sidebar-head">
        <a className="hrms-brand" aria-label="Frevo One HR"><img className="product-brand-logo" src={productLogo} alt="Frevo One HR" /><img className="product-brand-mark" src={productMark} alt="Frevo One HR" /></a>
        <Button className="hrms-sidebar-toggle" type="text" title={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-label={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} aria-expanded={navOpen} icon={navOpen ? <MenuFoldOutlined /> : <MenuUnfoldOutlined />} onClick={() => setNavOpen(open => !open)} />
      </div>
      <div className="hrms-sidebar-context"><i><AppIcon name={activeModule.icon} /></i><div><span>Module</span><strong>{activeModule.label}</strong></div></div>
      <nav aria-label={`${activeModule.label} navigation`}><div className="submenu context-menu">{renderContextMenu()}</div></nav>
    </aside>
    <main className="hrms-main">
      <header className="hrms-topbar">
        <div className="hrms-topbar-left">
          <Button className="hrms-mobile-nav-trigger" type="text" aria-label="Open navigation" icon={<MenuUnfoldOutlined />} onClick={() => setNavOpen(true)} />
          <Input className="global-search-antd" prefix={<SearchOutlined />} placeholder="Search modules, employees and actions" allowClear suffix={<span className="search-shortcut">Ctrl K</span>} />
        </div>
        <Space className="hrms-topbar-tools" size={8}>
          {dashboardAccess.length > 1 && <Dropdown menu={dashboardMenu} trigger={['click']} placement="bottomRight">
            <button className="dashboard-switcher" type="button" aria-label="Switch dashboard">
              <AppIcon name="dashboard" />
              <span>{activeDashboard.name}</span>
              <DownOutlined />
            </button>
          </Dropdown>}
          <Tooltip title="Notifications"><Badge dot><Button className="topbar-icon-btn" type="default" icon={<BellOutlined />} aria-label="Notifications" /></Badge></Tooltip>
          <Tooltip title="Open app modules"><Button className="topbar-icon-btn" type="default" icon={<AppstoreOutlined />} aria-label="Open app modules" onClick={() => setAppDrawerOpen(true)} /></Tooltip>
          <Dropdown menu={accountMenu} trigger={['click']} placement="bottomRight">
            <button className="account-trigger" type="button" aria-label="Open account menu">
              <Avatar size={36} icon={!userInitials ? <UserOutlined /> : undefined}>{userInitials}</Avatar>
            </button>
          </Dropdown>
        </Space>
      </header>
      <div className="hrms-content">
        <AppPageHeader title={pageTitle} description={pageDescription} icon={<AppIcon name={pageIconName} />} breadcrumbs={breadcrumbItems} />
        <div className="hrms-page-body">{renderPage()}</div>
      </div>
      <footer className="hrms-footer">
        {shellOrg.logoDataUrl ? <img src={shellOrg.logoDataUrl} alt="Organization logo" /> : <b>{(shellOrg.name || 'O').slice(0, 1).toUpperCase()}</b>}
        <span>{shellOrg.name || 'Organization'}</span>
      </footer>
    </main>
    {appDrawerOpen && <div className="drawer-scrim" onClick={() => setAppDrawerOpen(false)} />}
    <aside className="module-drawer" aria-hidden={!appDrawerOpen}>
      <header><div><span className="eyebrow purple">App Launcher</span><h3>Choose module</h3></div><button type="button" aria-label="Close app modules" onClick={() => setAppDrawerOpen(false)}><AppIcon name="close" /></button></header>
      {modules.map(module => module.disabled ? <button className={mainModule === module.code ? 'active' : ''} type="button" disabled key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></button> : <Link className={mainModule === module.code ? 'active' : ''} to={modulePaths[module.code as ModuleCode]} onClick={event => {
        if (menuClickIsNewTabIntent(event)) return
        setModule(module.code as ModuleCode)
        setAppDrawerOpen(false)
      }} key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></Link>)}
    </aside>
  </div>
}

