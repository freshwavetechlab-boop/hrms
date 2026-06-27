import { useEffect, useState } from 'react'
import './App.css'
import './Dashboard.css'
import './Pay.css'
import './AccountMenu.css'
import { WorkspaceShell } from './components/WorkspaceShell'
import { clearToken, getToken, me } from './services/essApi'
import type { User, View } from './types'
import { DashboardPage } from './pages/DashboardPage'
import { LeavePage } from './pages/LeavePage'
import { LoginPage } from './pages/LoginPage'
import { PayPage } from './pages/PayPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { ProfilePage } from './pages/ProfilePage'
import { TaxPage } from './pages/TaxPage'
import { TasksPage } from './pages/TasksPage'

const viewStorageKey = 'ess.current.view'
const views: View[] = ['Dashboard', 'My Profile', 'Leave', 'Attendance', 'Pay', 'Tax', 'My Tasks', 'Team', 'Approvals']
const savedView = () => {
  const value = localStorage.getItem(viewStorageKey) as View | null
  return value && views.includes(value) ? value : 'Dashboard'
}

export default function App() {
  const [user, setUser] = useState<User | null>(null)
  const [view, setView] = useState<View>(savedView)

  useEffect(() => { if (!getToken()) return; void me().then(setUser).catch(() => undefined) }, [])
  useEffect(() => { localStorage.setItem(viewStorageKey, view) }, [view])

  if (!user) return <LoginPage onLogin={setUser} />

  const manager = !user.roles.includes('employee') || user.roles.some(role => ['hr_manager', 'payroll_approver', 'super_admin'].includes(role))
  const logout = () => { clearToken(); setUser(null) }

  return <WorkspaceShell user={user} view={view} manager={manager} onNavigate={setView} onLogout={logout}><Page view={view} manager={manager} user={user} setView={setView} /></WorkspaceShell>
}

function Page({ view, manager, user, setView }: { view: View; manager: boolean; user: User; setView: (view: View) => void }) {
  if (view === 'Dashboard') return <DashboardPage user={user} manager={manager} setView={setView} />
  if (view === 'My Profile') return <ProfilePage user={user} />
  if (view === 'My Tasks') return <TasksPage user={user} />
  if (view === 'Leave') return <LeavePage user={user} />
  if (view === 'Pay') return <PayPage user={user} />
  if (view === 'Tax') return <TaxPage user={user} />
  return <PlaceholderPage view={view} manager={manager} />
}
