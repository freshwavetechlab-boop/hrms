import { useEffect, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import AppIcon from './components/AppIcon'
import type { IconName } from './components/AppIcon'
import SecurityPanel from './components/SecurityPanel'
import { leaveAttendanceMenus, securityMenus, settingsMenus } from './data/payrollDefaults'
import EmployeePage from './pages/EmployeePage'
import LeaveAttendancePage from './pages/LeaveAttendancePage'
import type { LeaveAttendanceMenu } from './pages/LeaveAttendancePage'
import PayHistoryPage from './pages/PayHistoryPage'
import PayrollPage from './pages/PayrollPage'
import SettingsPage from './pages/SettingsPage'
import './OrganizationSetup.css'

const moduleCodes = ['Settings', 'Employees', 'Payroll', 'Security', 'LeaveAttendance'] as const
type ModuleCode = (typeof moduleCodes)[number]
type SettingsTab = (typeof settingsMenus)[number]
type SecurityTab = (typeof securityMenus)[number]

const modules: { code: ModuleCode | 'Reports'; label: string; icon: IconName; description: string; disabled?: boolean }[] = [
  { code: 'Payroll', label: 'Payroll', icon: 'payruns', description: 'Run payroll, compare variances and review history.' },
  { code: 'LeaveAttendance', label: 'Leave & Attendance', icon: 'calendar', description: 'Configure leave, holidays, attendance and opening balances.' },
  { code: 'Employees', label: 'Employees', icon: 'employees', description: 'Manage employee master, salary and statutory profile.' },
  { code: 'Security', label: 'Security', icon: 'security', description: 'Control users, roles, permissions and audit evidence.' },
  { code: 'Settings', label: 'Settings', icon: 'settings', description: 'Configure organization, clients and payroll setup.' },
  { code: 'Reports', label: 'Reports', icon: 'reports', description: 'Analytics and statutory reports coming soon.', disabled: true }
]

export default function SettingsApp() {
  const routeLocation = useLocation()
  const isPayHistory = routeLocation.pathname === '/pay-runs/history'
  const savedTab = localStorage.getItem('payroll.tab') as SettingsTab | null
  const savedModule = localStorage.getItem('payroll.module')
  const savedSecurityTab = localStorage.getItem('payroll.securityTab') as SecurityTab | null
  const savedLeaveAttendanceTab = localStorage.getItem('payroll.leaveAttendanceTab') as LeaveAttendanceMenu | null
  const initialModule: ModuleCode = isPayHistory || savedModule === 'Payroll' || savedModule === 'Pay Runs' ? 'Payroll' : savedModule === 'Employees' ? 'Employees' : savedModule === 'Security' ? 'Security' : savedModule === 'LeaveAttendance' ? 'LeaveAttendance' : 'Settings'
  const [tab, setActiveTab] = useState<SettingsTab>(savedTab && settingsMenus.includes(savedTab) ? savedTab : 'Organization')
  const [navOpen, setNavOpen] = useState(true), [appDrawerOpen, setAppDrawerOpen] = useState(false)
  const [securityTab, setSecurityTab] = useState<SecurityTab>(savedSecurityTab && securityMenus.includes(savedSecurityTab) ? savedSecurityTab : 'Users')
  const [leaveAttendanceTab, setLeaveAttendanceTab] = useState<LeaveAttendanceMenu>(savedLeaveAttendanceTab && leaveAttendanceMenus.includes(savedLeaveAttendanceTab) ? savedLeaveAttendanceTab : 'Preferences')
  const [mainModule, setMainModule] = useState<ModuleCode>(initialModule), [settingsMessage, setSettingsMessage] = useState('Settings ready.')
  const activeModule = modules.find(module => module.code === mainModule)!
  const pageTitle = mainModule === 'Settings' ? tab : mainModule === 'Employees' ? 'Employee Master' : mainModule === 'Security' ? securityTab : mainModule === 'LeaveAttendance' ? leaveAttendanceTab : isPayHistory ? 'Pay History' : 'Pay Run'
  const pageDescription = mainModule === 'Payroll' ? isPayHistory ? 'All client-wise draft, approved and paid payroll runs.' : 'Draft, review and approve monthly payroll.' : mainModule === 'Security' ? 'Manage identities, roles, permissions and audit evidence.' : mainModule === 'Employees' ? 'Maintain client-linked employee records, salary profiles and payment details.' : mainModule === 'LeaveAttendance' ? 'Setup leave types, holidays, attendance preferences and opening balances.' : settingsMessage

  useEffect(() => { if (isPayHistory && mainModule !== 'Payroll') setModule('Payroll') }, [isPayHistory])
  const setTab = (nextTab: SettingsTab) => { localStorage.setItem('payroll.tab', nextTab); setActiveTab(nextTab) }
  const setModule = (nextModule: ModuleCode) => { localStorage.setItem('payroll.module', nextModule); setMainModule(nextModule) }
  const setSecurityModuleTab = (nextTab: SecurityTab) => { localStorage.setItem('payroll.securityTab', nextTab); setSecurityTab(nextTab); setModule('Security') }
  const setLeaveAttendanceModuleTab = (nextTab: LeaveAttendanceMenu) => { localStorage.setItem('payroll.leaveAttendanceTab', nextTab); setLeaveAttendanceTab(nextTab); setModule('LeaveAttendance') }
  const renderContextMenu = () => {
    if (mainModule === 'Settings') return settingsMenus.map(item => <button className={tab === item ? 'active' : ''} type="button" onClick={() => setTab(item)} key={item}>{item}<small>{item === 'Salary Templates' ? 'Client-wise' : ''}</small></button>)
    if (mainModule === 'Payroll') return <><Link className={isPayHistory ? '' : 'active'} to="/" onClick={() => setModule('Payroll')}>Pay Run</Link><Link className={isPayHistory ? 'active' : ''} to="/pay-runs/history" onClick={() => setModule('Payroll')}>Pay History</Link></>
    if (mainModule === 'Security') return securityMenus.map(item => <button className={securityTab === item ? 'active' : ''} type="button" onClick={() => setSecurityModuleTab(item)} key={item}>{item}</button>)
    if (mainModule === 'LeaveAttendance') return leaveAttendanceMenus.map(item => <button className={leaveAttendanceTab === item ? 'active' : ''} type="button" onClick={() => setLeaveAttendanceModuleTab(item)} key={item}>{item}</button>)
    return <button className="active" type="button">Employee Master<small>Core HR</small></button>
  }
  const renderPage = () => {
    if (mainModule === 'Security') return <SecurityPanel initialTab={securityTab} />
    if (mainModule === 'LeaveAttendance') return <LeaveAttendancePage activeMenu={leaveAttendanceTab} onSelectMenu={setLeaveAttendanceModuleTab} />
    if (mainModule === 'Payroll') return isPayHistory ? <PayHistoryPage /> : <PayrollPage />
    if (mainModule === 'Employees') return <EmployeePage />
    return <SettingsPage tab={tab} onMessage={setSettingsMessage} />
  }

  return <div className={`payroll-app compact module-shell ${navOpen ? '' : 'nav-collapsed'} ${appDrawerOpen ? 'drawer-open' : ''}`}><aside className="app-sidebar"><div className="side-head"><a className="brand"><i>P</i><span>paymint</span></a><button type="button" aria-label={navOpen ? 'Collapse sidebar' : 'Expand sidebar'} onClick={() => setNavOpen(open => !open)}><AppIcon name={navOpen ? 'collapse' : 'expand'} /></button></div><div className="sidebar-context"><span className="eyebrow purple">Current Module</span><strong>{activeModule.label}</strong><small>{activeModule.description}</small></div><nav><div className="submenu context-menu">{renderContextMenu()}</div></nav></aside><main className="compact-main"><header className="topbar"><div><span className="eyebrow purple">{mainModule} / {pageTitle}</span><h2>{pageTitle}</h2><p>{pageDescription}</p></div><button className="app-launcher" type="button" aria-label="Open app modules" onClick={() => setAppDrawerOpen(true)}><AppIcon name="expand" /></button><b>NK</b></header>{renderPage()}</main>{appDrawerOpen && <div className="drawer-scrim" onClick={() => setAppDrawerOpen(false)} />}<aside className="module-drawer" aria-hidden={!appDrawerOpen}><header><div><span className="eyebrow purple">App Launcher</span><h3>Choose module</h3></div><button type="button" aria-label="Close app modules" onClick={() => setAppDrawerOpen(false)}>×</button></header>{modules.map(module => <button className={mainModule === module.code ? 'active' : ''} type="button" disabled={module.disabled} onClick={() => { if (!module.disabled) { setModule(module.code as ModuleCode); setAppDrawerOpen(false) } }} key={module.code}><AppIcon name={module.icon} /><strong>{module.label}</strong><small>{module.description}</small></button>)}</aside></div>
}
