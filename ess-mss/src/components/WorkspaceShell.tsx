import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import type { User, View } from '../types'
import { initials, viewLabel } from '../utils/ui'

type Props = {
  user: User
  view: View
  manager: boolean
  onNavigate: (view: View) => void
  onLogout: () => void
  children: ReactNode
}

export function WorkspaceShell({ user, view, manager, onNavigate, onLogout, children }: Props) {
  const [accountOpen, setAccountOpen] = useState(false)
  const nav = useMemo(
    () => (manager ? ['Dashboard', 'My Tasks', 'Leave', 'Attendance', 'Pay', 'Tax', 'Team', 'Approvals'] : ['Dashboard', 'My Tasks', 'Leave', 'Attendance', 'Pay', 'Tax']) as View[],
    [manager],
  )

  return <div className="ess-shell"><aside className="ess-sidebar"><div className="ess-brand"><i>F</i><strong>Frevo One HR</strong><small>ESS / MSS</small></div><nav>{nav.map(item => <button className={view === item ? 'active' : ''} onClick={() => onNavigate(item)} key={item}><span>{viewLabel(item)}</span>{item === 'Approvals' && <b>0</b>}</button>)}</nav><div className="sidebar-help"><b>Need help?</b><span>Contact your HR or payroll team for policy and access questions.</span></div></aside><main className="ess-main"><header className="ess-topbar"><div><span className="eyebrow">{manager ? 'Manager workspace' : 'Employee workspace'} / {view}</span><h2>{view}</h2></div><div className="account-menu"><button className="user-menu" type="button" onClick={() => setAccountOpen(open => !open)} aria-expanded={accountOpen}><span>{initials(user.displayName)}</span><div><b>{user.displayName}</b><small>{manager ? 'Manager access' : 'Employee access'}</small></div><i>⌄</i></button>{accountOpen && <div className="account-dropdown"><button type="button" onClick={() => { onNavigate('My Profile'); setAccountOpen(false) }}>My profile</button><button type="button" onClick={onLogout}>Logout</button></div>}</div></header>{children}</main></div>
}
