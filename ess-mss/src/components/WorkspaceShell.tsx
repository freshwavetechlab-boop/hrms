import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import type { OrganizationBrand, ProfileData, User, View } from '../types'
import { essApi, organizationBrand } from '../services/essApi'
import { initials } from '../utils/ui'

type Props = {
  user: User
  view: View
  manager: boolean
  employeeSelf: boolean
  onNavigate: (view: View) => void
  onLogout: () => void
  onChangePassword: () => void
  children: ReactNode
}

type IconName = 'home' | 'dashboard' | 'profile' | 'leave' | 'plus' | 'list' | 'travel' | 'expense' | 'recruitment' | 'work' | 'attendance' | 'pay' | 'tax' | 'tasks' | 'manager' | 'team' | 'approval' | 'collapse' | 'expand'
type NavItem = { icon: IconName; label: string; view: View; action?: string }

export function WorkspaceShell({ user, view, manager, employeeSelf, onNavigate, onLogout, onChangePassword, children }: Props) {
  const [accountOpen, setAccountOpen] = useState(false)
  const accountMenuRef = useRef<HTMLDivElement>(null)
  const [activeAction, setActiveAction] = useState<string | null>(null)
  const [openGroup, setOpenGroup] = useState('home')
  const [pageTitle, setPageTitle] = useState<{ title: string; section?: string } | null>(null)
  const [navCollapsed, setNavCollapsed] = useState(() => typeof window !== 'undefined' ? window.innerWidth <= 760 : false)
  const [recruitmentEnabled, setRecruitmentEnabled] = useState<boolean | null>(null)
  const [travelExpenseEnabled, setTravelExpenseEnabled] = useState<boolean | null>(null)
  const [organization, setOrganization] = useState<OrganizationBrand | null>(null)
  const [profile, setProfile] = useState<ProfileData | null>(null)
  const hasRecruitmentAccess = canAccessRecruitment(user)
  const groups = useMemo(
    () => [
      { key: 'home', icon: 'home' as IconName, label: 'Home', items: [{ icon: 'dashboard' as IconName, label: 'Dashboard', view: 'Dashboard' as View }, ...(employeeSelf ? [{ icon: 'profile' as IconName, label: 'My profile', view: 'My Profile' as View }] : [])] },
      ...(employeeSelf ? [{ key: 'time', icon: 'attendance' as IconName, label: 'Time & leave', items: [{ icon: 'attendance' as IconName, label: 'Attendance calendar', view: 'Attendance' as View }, { icon: 'plus' as IconName, label: 'Apply leave', view: 'Leave' as View, action: 'ess:leave:new' }, { icon: 'list' as IconName, label: 'Leave history', view: 'Leave' as View, action: 'ess:leave:list' }] }] : []),
      ...(employeeSelf ? [{ key: 'payroll', icon: 'pay' as IconName, label: 'Pay & tax', items: [{ icon: 'pay' as IconName, label: 'Payslips', view: 'Pay' as View }, { icon: 'tax' as IconName, label: 'Tax declarations', view: 'Tax' as View }] }] : []),
      ...(employeeSelf && travelExpenseEnabled === true ? [{ key: 'travel', icon: 'travel' as IconName, label: 'Travel & expense', items: [{ icon: 'plus' as IconName, label: 'Create travel request', view: 'Travel' as View, action: 'ess:travel:new' }, { icon: 'list' as IconName, label: 'Travel requests', view: 'Travel' as View, action: 'ess:travel:list' }, { icon: 'expense' as IconName, label: 'Other expense claim', view: 'Expense' as View, action: 'ess:expense:new' }, { icon: 'list' as IconName, label: 'Expense claims', view: 'Expense' as View, action: 'ess:expense:list' }] }] : []),
      ...(employeeSelf && recruitmentEnabled === true ? [{ key: 'recruitment', icon: 'recruitment' as IconName, label: 'Recruitment', items: [{ icon: 'plus' as IconName, label: 'Create requisition', view: 'Recruitment' as View, action: 'ess:recruitment:new' }, { icon: 'list' as IconName, label: 'My requisitions', view: 'Recruitment' as View, action: 'ess:recruitment:list' }] }] : []),
      { key: 'tasks', icon: 'tasks' as IconName, label: 'Approvals', items: [{ icon: 'tasks' as IconName, label: 'My approval tasks', view: 'My Tasks' as View }] },
      ...(manager ? [{ key: 'manager', icon: 'manager' as IconName, label: 'Manager workspace', items: [{ icon: 'team' as IconName, label: 'Team overview', view: 'Team' as View }, { icon: 'approval' as IconName, label: 'Team approvals', view: 'Approvals' as View }] }] : []),
    ],
    [employeeSelf, manager, recruitmentEnabled, travelExpenseEnabled],
  )

  useEffect(() => {
    if (!employeeSelf) { setTravelExpenseEnabled(false); return }
    void essApi.features().then(features => setTravelExpenseEnabled(features.travelExpenseEnabled)).catch(() => setTravelExpenseEnabled(false))
  }, [employeeSelf, user.email])
  useEffect(() => {
    let active = true
    const request = employeeSelf ? essApi.profile() : Promise.resolve(null)
    void request.then(nextProfile => {
      if (active) setProfile(nextProfile)
    }).catch(() => {
      if (active) setProfile(null)
    })
    return () => { active = false }
  }, [employeeSelf, user.email])
  useEffect(() => {
    if (!employeeSelf || !hasRecruitmentAccess) { setRecruitmentEnabled(false); return }
    void essApi.recruitmentOptions().then(options => setRecruitmentEnabled(options.moduleEnabled && hasRecruitmentAccess)).catch(() => setRecruitmentEnabled(false))
  }, [employeeSelf, hasRecruitmentAccess, user.email])
  useEffect(() => { void organizationBrand().then(setOrganization).catch(() => undefined) }, [])
  useEffect(() => { if (recruitmentEnabled === false && view === 'Recruitment') onNavigate('Dashboard') }, [onNavigate, recruitmentEnabled, view])
  useEffect(() => { if (travelExpenseEnabled === false && (view === 'Travel' || view === 'Expense')) onNavigate('Dashboard') }, [onNavigate, travelExpenseEnabled, view])
  useEffect(() => { if (!employeeSelf && isEmployeeOnlyView(view)) onNavigate('Dashboard') }, [employeeSelf, onNavigate, view])

  useEffect(() => {
    const active = groups.find(group => group.items.some(item => item.view === view))
    if (active) setOpenGroup(active.key)
    setPageTitle(null)
  }, [groups, view])

  useEffect(() => {
    const updateTitle = (event: Event) => {
      const detail = (event as CustomEvent<{ title?: string; section?: string }>).detail
      setPageTitle(detail?.title ? { title: detail.title, section: detail.section } : null)
    }
    window.addEventListener('ess:page-title', updateTitle as EventListener)
    return () => window.removeEventListener('ess:page-title', updateTitle as EventListener)
  }, [])

  useEffect(() => {
    if (!accountOpen) return
    const closeOnOutsidePress = (event: PointerEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) setAccountOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setAccountOpen(false)
    }
    document.addEventListener('pointerdown', closeOnOutsidePress)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePress)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [accountOpen])

  useEffect(() => {
    if (!activeAction) return
    if (activeAction.includes(':leave:') && view !== 'Leave') setActiveAction(null)
    if (activeAction.includes(':travel:') && view !== 'Travel') setActiveAction(null)
    if (activeAction.includes(':expense:') && view !== 'Expense') setActiveAction(null)
    if (activeAction.includes(':recruitment:') && view !== 'Recruitment') setActiveAction(null)
  }, [activeAction, view])

  const navigate = (item: NavItem) => {
    setActiveAction(item.action ?? null)
    onNavigate(item.view)
    if (typeof window !== 'undefined' && window.innerWidth <= 760) setNavCollapsed(true)
    const action = item.action
    if (action) window.setTimeout(() => window.dispatchEvent(new CustomEvent(action)), 0)
  }
  const isActiveItem = (item: NavItem) => {
    if (view !== item.view) return false
    if (activeAction) return item.action === activeAction
    if (view === 'Leave') return item.action === 'ess:leave:list'
    if (view === 'Travel') return item.action === 'ess:travel:list'
    if (view === 'Expense') return item.action === 'ess:expense:list'
    if (view === 'Recruitment') return item.action === 'ess:recruitment:list'
    return !item.action
  }
  const activeGroup = groups.find(group => group.items.some(item => item.view === view))
  const activeItem = activeAction
    ? activeGroup?.items.find(item => item.action === activeAction)
    : activeGroup?.items.find(item => isActiveItem(item)) ?? activeGroup?.items.find(item => item.view === view)
  const title = pageTitle?.title || activeItem?.label || view
  const section = pageTitle?.section || activeGroup?.label || (manager && !employeeSelf ? 'Manager workspace' : 'Employee workspace')

  return <div className={`ess-shell ${navCollapsed ? 'ess-nav-collapsed' : ''}`}>
    <aside className="ess-sidebar">
      <div className="ess-side-head">
        <div className="ess-brand ess-product-brand"><img className="ess-product-logo" src="/assets/FrevoOneLogo.png" alt="Frevo One HR" /><img className="ess-product-mark" src="/favicon.svg" alt="Frevo One HR" /></div>
        <button type="button" className="ess-sidebar-toggle" title={navCollapsed ? 'Expand sidebar' : 'Collapse sidebar'} aria-label={navCollapsed ? 'Expand sidebar' : 'Collapse sidebar'} onClick={() => setNavCollapsed(value => !value)}>
          <MenuIcon name={navCollapsed ? 'expand' : 'collapse'} />
        </button>
      </div>
      <nav className="ess-nav">
        {groups.map(group => {
          const active = group.items.some(item => item.view === view)
          const open = openGroup === group.key
          return <section className={active ? 'active' : ''} key={group.key}>
            <button type="button" className="ess-nav-group" onClick={() => setOpenGroup(open ? '' : group.key)}>
              <span><b><MenuIcon name={group.icon} /></b>{group.label}</span><i>{open ? '-' : '+'}</i>
            </button>
            {!navCollapsed && open && <div className="ess-nav-items">{group.items.map(item => <button type="button" className={isActiveItem(item) ? 'active' : ''} onClick={() => navigate(item)} key={`${group.key}-${item.label}`}>
              <span><em><MenuIcon name={item.icon} /></em>{item.label}</span>{item.view === 'Approvals' && <b>0</b>}
            </button>)}</div>}
          </section>
        })}
      </nav>
      <div className="sidebar-help"><b>Need help?</b><span>Contact your HR or payroll team for policy and access questions.</span></div>
    </aside>
    <main className="ess-main">
      <header className="ess-topbar">
        <div className="ess-titlebar">
          {organization?.logoDataUrl && <img className="ess-topbar-org-logo" src={organization.logoDataUrl} alt={organization.name || 'Organization logo'} />}
          <div><span className="eyebrow">{manager && !employeeSelf ? 'Manager workspace' : 'Employee workspace'} / {section}</span><h2>{title}</h2></div>
        </div>
        <div className={`account-menu ${accountOpen ? 'open' : ''}`} ref={accountMenuRef}>
          <button className="user-menu" type="button" title="Account menu" aria-label={`Open account menu for ${user.displayName}`} onClick={() => setAccountOpen(open => !open)} aria-haspopup="menu" aria-expanded={accountOpen}><span>{initials(user.displayName)}</span><div><b>{user.displayName}</b><small title={profile?.attendanceOffice || undefined}>{profile?.attendanceOffice || (manager ? 'Manager access' : 'Employee access')}</small></div><i>v</i></button>
          {accountOpen && <div className="account-dropdown" role="menu">{profile?.attendanceOffice && <div className="account-office"><span>Attendance office</span><b>{profile.attendanceOffice}</b></div>}{employeeSelf && <button role="menuitem" type="button" onClick={() => { setActiveAction(null); onNavigate('My Profile'); setAccountOpen(false) }}>My profile</button>}<button role="menuitem" type="button" onClick={() => { setAccountOpen(false); onChangePassword() }}>Change password</button><button role="menuitem" type="button" onClick={onLogout}>Logout</button></div>}
        </div>
      </header>
      {children}
    </main>
  </div>
}

function isEmployeeOnlyView(view: View) {
  return ['My Profile', 'Leave', 'Travel', 'Expense', 'Recruitment', 'Attendance', 'Pay', 'Tax'].includes(view)
}

function canAccessRecruitment(user: User) {
  return user.permissions.some(permission => ['recruitment.rfr.view', 'recruitment.rfr.create', 'recruitment.manage'].includes(permission.toLowerCase()))
}

function MenuIcon({ name }: { name: IconName }) {
  const common = { width: 14, height: 14, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, 'aria-hidden': true }
  if (name === 'collapse') return <svg viewBox="64 64 896 896" width="1em" height="1em" fill="currentColor" aria-hidden="true"><path d="M408 442h480c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8H408c-4.4 0-8 3.6-8 8v56c0 4.4 3.6 8 8 8zm-8 204c0 4.4 3.6 8 8 8h480c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8H408c-4.4 0-8 3.6-8 8v56zm504-486H120c-4.4 0-8 3.6-8 8v56c0 4.4 3.6 8 8 8h784c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8zm0 632H120c-4.4 0-8 3.6-8 8v56c0 4.4 3.6 8 8 8h784c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8zM142.4 642.1L298.7 519c4.9-3.8 4.9-11.2 0-15.1L142.4 380.9c-6.4-5-15.7-.5-15.7 7.6v245.9c0 8.2 9.3 12.7 15.7 7.7z" /></svg>
  if (name === 'expand') return <svg viewBox="64 64 896 896" width="1em" height="1em" fill="currentColor" aria-hidden="true"><path d="M408 442h480c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8H408c-4.4 0-8 3.6-8 8v56c0 4.4 3.6 8 8 8zm-8 204c0 4.4 3.6 8 8 8h480c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8H408c-4.4 0-8 3.6-8 8v56zm504-486H120c-4.4 0-8 3.6-8 8v56c0 4.4 3.6 8 8 8h784c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8zm0 632H120c-4.4 0-8 3.6-8 8v56c0 4.4 3.6 8 8 8h784c4.4 0 8-3.6 8-8v-56c0-4.4-3.6-8-8-8zM325.6 381.9L169.3 505c-4.9 3.8-4.9 11.2 0 15.1l156.3 123c6.4 5 15.7.5 15.7-7.6V389.6c0-8.2-9.3-12.7-15.7-7.7z" /></svg>
  if (name === 'home') return <svg {...common}><path d="M3 11l9-8 9 8" /><path d="M5 10v10h14V10" /><path d="M10 20v-6h4v6" /></svg>
  if (name === 'dashboard') return <svg {...common}><rect x="3" y="3" width="7" height="8" rx="1" /><rect x="14" y="3" width="7" height="5" rx="1" /><rect x="14" y="12" width="7" height="9" rx="1" /><rect x="3" y="15" width="7" height="6" rx="1" /></svg>
  if (name === 'profile') return <svg {...common}><circle cx="12" cy="8" r="4" /><path d="M4 21c1.7-4 14.3-4 16 0" /></svg>
  if (name === 'leave') return <svg {...common}><path d="M8 3v4" /><path d="M16 3v4" /><rect x="4" y="5" width="16" height="16" rx="2" /><path d="M4 10h16" /><path d="M9 15l2 2 4-5" /></svg>
  if (name === 'plus') return <svg {...common}><path d="M12 5v14" /><path d="M5 12h14" /></svg>
  if (name === 'list') return <svg {...common}><path d="M8 6h13" /><path d="M8 12h13" /><path d="M8 18h13" /><path d="M3 6h.01" /><path d="M3 12h.01" /><path d="M3 18h.01" /></svg>
  if (name === 'travel') return <svg {...common}><path d="M10 21l2-6" /><path d="M14 21l-2-6" /><path d="M3 9l18-5-5 18-4-7-7-4z" /></svg>
  if (name === 'expense') return <svg {...common}><path d="M7 3h10l3 3v15H7z" /><path d="M17 3v4h4" /><path d="M10 12h7" /><path d="M10 16h5" /><path d="M4 7v14" /></svg>
  if (name === 'recruitment') return <svg {...common}><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M8 2v4" /><path d="M16 2v4" /><path d="M8 11h8" /><path d="M8 15h5" /></svg>
  if (name === 'work') return <svg {...common}><rect x="3" y="7" width="18" height="13" rx="2" /><path d="M9 7V5a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2" /><path d="M3 13h18" /></svg>
  if (name === 'attendance') return <svg {...common}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>
  if (name === 'pay') return <svg {...common}><rect x="3" y="6" width="18" height="12" rx="2" /><path d="M7 10h4" /><path d="M7 14h2" /><circle cx="16" cy="12" r="2" /></svg>
  if (name === 'tax') return <svg {...common}><path d="M7 7h10" /><path d="M7 12h10" /><path d="M7 17h6" /><rect x="4" y="3" width="16" height="18" rx="2" /></svg>
  if (name === 'tasks') return <svg {...common}><path d="M9 6h11" /><path d="M9 12h11" /><path d="M9 18h11" /><path d="M4 6l1 1 2-2" /><path d="M4 12l1 1 2-2" /><path d="M4 18l1 1 2-2" /></svg>
  if (name === 'manager') return <svg {...common}><circle cx="9" cy="8" r="3" /><path d="M3 21c.8-3.2 11.2-3.2 12 0" /><path d="M17 8h4" /><path d="M19 6v4" /></svg>
  if (name === 'team') return <svg {...common}><circle cx="8" cy="8" r="3" /><circle cx="17" cy="9" r="2.5" /><path d="M2 21c.8-3.5 11.2-3.5 12 0" /><path d="M14 20c.7-2.3 6.3-2.3 7 0" /></svg>
  return <svg {...common}><path d="M9 12l2 2 4-5" /><path d="M21 12a9 9 0 1 1-3-6.7" /></svg>
}
