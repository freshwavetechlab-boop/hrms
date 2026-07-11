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
import { TravelPage } from './pages/TravelPage'

const viewStorageKey = 'ess.current.view'
const views: View[] = ['Dashboard', 'My Profile', 'Leave', 'Travel', 'Attendance', 'Pay', 'Tax', 'My Tasks', 'Team', 'Approvals']
const viewRoutes: Record<View, string> = {
  Dashboard: 'home',
  'My Profile': 'profile',
  Leave: 'leave',
  Travel: 'travel',
  Attendance: 'attendance',
  Pay: 'pay',
  Tax: 'tax',
  'My Tasks': 'my-tasks',
  Team: 'team',
  Approvals: 'approvals',
}
const routeViews = Object.fromEntries(Object.entries(viewRoutes).map(([view, route]) => [route, view])) as Record<string, View>

const routeView = () => {
  const route = (window.location.hash.replace(/^#\/?/, '') || window.location.pathname.replace(/^\/+/, '') || '').toLowerCase()
  return routeViews[route]
}
const savedView = () => {
  const route = routeView()
  if (route && views.includes(route)) return route
  const value = localStorage.getItem(viewStorageKey) as View | null
  return value && views.includes(value) ? value : 'Dashboard'
}

const setViewUrl = (view: View, replace = false) => {
  const next = `#/${viewRoutes[view]}`
  if (window.location.hash === next) return
  if (replace) window.history.replaceState(null, '', next)
  else window.history.pushState(null, '', next)
}

export default function App() {
  const [user, setUser] = useState<User | null>(null)
  const [view, setView] = useState<View>(savedView)

  useEffect(() => { if (!getToken()) return; void me().then(setUser).catch(() => undefined) }, [])
  useEffect(() => {
    const syncFromUrl = () => {
      const next = routeView()
      if (next && views.includes(next)) setView(next)
    }
    window.addEventListener('hashchange', syncFromUrl)
    window.addEventListener('popstate', syncFromUrl)
    return () => {
      window.removeEventListener('hashchange', syncFromUrl)
      window.removeEventListener('popstate', syncFromUrl)
    }
  }, [])
  useEffect(() => {
    localStorage.setItem(viewStorageKey, view)
    setViewUrl(view, !window.location.hash)
  }, [view])

  if (!user) return <LoginPage onLogin={setUser} />

  const manager = !user.roles.includes('employee') || user.roles.some(role => ['hr_manager', 'payroll_approver', 'super_admin'].includes(role))
  const logout = () => { clearToken(); setUser(null); setView('Dashboard'); setViewUrl('Dashboard', true) }

  return <WorkspaceShell user={user} view={view} manager={manager} onNavigate={setView} onLogout={logout}><Page view={view} manager={manager} user={user} setView={setView} /></WorkspaceShell>
}

function Page({ view, manager, user, setView }: { view: View; manager: boolean; user: User; setView: (view: View) => void }) {
  if (view === 'Dashboard') return <DashboardPage user={user} manager={manager} setView={setView} />
  if (view === 'My Profile') return <ProfilePage user={user} />
  if (view === 'My Tasks') return <TasksPage user={user} />
  if (view === 'Leave') return <LeavePage user={user} />
  if (view === 'Travel') return <TravelPage user={user} />
  if (view === 'Pay') return <PayPage user={user} />
  if (view === 'Tax') return <TaxPage user={user} />
  return <PlaceholderPage view={view} manager={manager} />
}
