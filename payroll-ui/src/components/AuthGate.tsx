import { createContext, useContext, useEffect, useState } from 'react'
import type { FormEvent, ReactNode, SyntheticEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { EyeInvisibleOutlined, EyeOutlined } from '@ant-design/icons'
import { changePassword, getCurrentUser, login as authenticate, logout as endSession } from '../services/authService'
import type { AuthUser } from '../types/payroll'
import { recruitmentLoginPath } from '../utils/recruitmentDashboardAccess'
import { useToast } from './ToastProvider'
import { assignedClients, isSelectedClientUser, projectClientAccess } from '../utils/clientAccess'
import { configureClientRequestScope, setActiveClientRequestScope } from '../services/clientRequestScope'

const productLogo = '/assets/FrevoOneLogo.png'
const loginLogo = '/assets/FrevoOneLoginLogo.png'

type AuthSession = { user: AuthUser; originalUser: AuthUser; activeClientId: number; selectClient: (clientId: number) => boolean; logout: () => Promise<void> }
const AuthSessionContext = createContext<AuthSession | null>(null)
export const useAuthSession = () => useContext(AuthSessionContext)

export default function AuthGate({ children }: { children: ReactNode }) {
  const navigate = useNavigate()
  const location = useLocation()
  const toast = useToast()
  const [user, setUser] = useState<AuthUser | null>(null), [email, setEmail] = useState(import.meta.env.DEV ? 'admin@paymint.local' : ''), [password, setPassword] = useState(import.meta.env.DEV ? 'Admin@12345' : '')
  const [loading, setLoading] = useState(true), [error, setError] = useState(''), [showPassword, setShowPassword] = useState(false), [signingIn, setSigningIn] = useState(false)
  const [activeClientId, setActiveClientId] = useState(0)
  const acceptUser = (data: AuthUser) => {
    setActiveClientId(configureClientRequestScope(data, Number(new URLSearchParams(window.location.search).get('clientId'))))
    setUser(current => JSON.stringify(current) === JSON.stringify(data) ? current : data)
  }

  useEffect(() => {
    const expire = () => { configureClientRequestScope(null); sessionStorage.removeItem('payroll.auth.token'); localStorage.removeItem('payroll.auth.token'); localStorage.removeItem('payroll.auth.user'); setUser(null); setActiveClientId(0) }
    window.addEventListener('payroll:unauthorized', expire)
    let active = true
    let refreshing = false
    const refresh = () => {
      if (refreshing) return
      refreshing = true
      void getCurrentUser().then(data => { if (!active) return; if (data) acceptUser(data); else expire() }).catch(() => undefined).finally(() => { refreshing = false; if (active) setLoading(false) })
    }
    refresh()
    window.addEventListener('focus', refresh)
    window.addEventListener('payroll:client-access-refresh', refresh)
    return () => { active = false; window.removeEventListener('payroll:unauthorized', expire); window.removeEventListener('focus', refresh); window.removeEventListener('payroll:client-access-refresh', refresh) }
  }, [])
  useEffect(() => {
    if (!user || !isSelectedClientUser(user)) return
    const requested = Number(new URLSearchParams(location.search).get('clientId'))
    if (requested > 0 && requested !== activeClientId && setActiveClientRequestScope(requested)) setActiveClientId(requested)
  }, [user, location.search, activeClientId])
  const login = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    if (!email.trim() || !password.trim()) {
      const message = 'Email and password are required.'
      setError(message)
      toast(message, 'warning')
      return
    }
    setSigningIn(true)
    const response = await authenticate(email, password)
    setSigningIn(false)
    if (!response.ok || !response.data) {
      const message = loginErrorMessage(response.status, response.error)
      setError(message)
      toast(message, 'error')
      return
    }
    sessionStorage.setItem('payroll.auth.token', response.data.token)
    localStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.user')
    acceptUser(response.data.user)
    toast('Signed in successfully.', 'success')
    navigate(recruitmentLoginPath(response.data.user.permissions), { replace: true })
  }

  const logout = async () => {
    await endSession()
    sessionStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.user')
    configureClientRequestScope(null)
    setActiveClientId(0)
    setUser(null)
  }

  const useProductLogo = (event: SyntheticEvent<HTMLImageElement>) => { event.currentTarget.onerror = null; event.currentTarget.src = productLogo }
  if (loading) return <main className="auth-shell"><section className="auth-card"><img className="auth-product-logo" src={loginLogo} onError={useProductLogo} alt="Frevo One HR" /></section></main>
  if (!user) return <main className="auth-shell"><section className="auth-card auth-login-card"><div className="auth-brand-panel"><img className="auth-product-logo" src={loginLogo} onError={useProductLogo} alt="Frevo One HR" /></div><form onSubmit={login}><label><span>Email</span><input value={email} onChange={event => setEmail(event.target.value)} autoComplete="username" placeholder="Enter email or login ID" disabled={signingIn} /></label><label><span>Password</span><div className="auth-password-field"><input type={showPassword ? 'text' : 'password'} value={password} onChange={event => setPassword(event.target.value)} autoComplete="current-password" placeholder="Enter password" disabled={signingIn} /><button type="button" aria-label={showPassword ? 'Hide password' : 'Show password'} title={showPassword ? 'Hide password' : 'Show password'} onClick={() => setShowPassword(current => !current)} disabled={signingIn}>{showPassword ? <EyeInvisibleOutlined /> : <EyeOutlined />}</button></div></label>{error && <strong className="auth-error">{error}</strong>}<button className="auth-submit" type="submit" disabled={signingIn}>{signingIn ? 'Signing in...' : 'Sign in'}</button></form></section></main>
  if (user.mustChangePassword) return <RequiredPasswordChange user={user} onChanged={updated => { acceptUser(updated); setPassword('') }} onLogout={logout} />
  if (isSelectedClientUser(user) && !assignedClients(user).length) return <main className="auth-shell"><section className="auth-card"><h2>No active assigned clients</h2><p>Ask an administrator to update your client access.</p><button type="button" onClick={() => void logout()}>Sign out</button></section></main>
  const selectClient = (clientId: number) => {
    if (!isSelectedClientUser(user) || !setActiveClientRequestScope(clientId)) return false
    const currentQuery = new URLSearchParams(window.location.search), nextQuery = new URLSearchParams()
    // Record IDs from another client must not survive switching. Retain only
    // workspace layout/report choices, and remount modules to discard old data.
    for (const key of ['report', 'pool', 'section', 'month']) if (currentQuery.has(key)) nextQuery.set(key, currentQuery.get(key)!)
    nextQuery.set('clientId', String(clientId))
    navigate(`${window.location.pathname}?${nextQuery.toString()}`, { replace: true })
    setActiveClientId(clientId)
    return true
  }
  const projectedUser = projectClientAccess(user, activeClientId)
  return <AuthSessionContext.Provider key={`${user.id}:${isSelectedClientUser(user) ? activeClientId + ':' + assignedClients(user).join(',') : 'legacy'}`} value={{ user: projectedUser, originalUser: user, activeClientId, selectClient, logout }}>{children}</AuthSessionContext.Provider>
}

function RequiredPasswordChange({ user, onChanged, onLogout }: { user: AuthUser; onChanged: (user: AuthUser) => void; onLogout: () => Promise<void> }) {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (newPassword.trim().length < 8) { setError('New password must be at least 8 characters.'); return }
    if (newPassword !== confirmation) { setError('New passwords do not match.'); return }
    if (newPassword.trim() === currentPassword) { setError('Choose a password different from your temporary password.'); return }
    setSaving(true)
    setError('')
    try {
      const response = await changePassword(currentPassword, newPassword)
      if (!response.ok || !response.data) { setError(response.error || 'Unable to change password.'); return }
      onChanged(response.data)
    } finally { setSaving(false) }
  }
  return <main className="auth-shell"><section className="auth-card auth-login-card">
    <div className="auth-brand-panel"><img className="auth-product-logo" src={productLogo} alt="Frevo One HR" /></div>
    <form onSubmit={event => void submit(event)}>
      <h2>Change temporary password</h2><p>{user.displayName}, choose your own password to continue.</p>
      <label><span>Temporary password</span><input type="password" autoComplete="current-password" required value={currentPassword} onChange={event => setCurrentPassword(event.target.value)} disabled={saving} /></label>
      <label><span>New password</span><input type="password" autoComplete="new-password" minLength={8} required value={newPassword} onChange={event => setNewPassword(event.target.value)} disabled={saving} /></label>
      <label><span>Confirm new password</span><input type="password" autoComplete="new-password" minLength={8} required value={confirmation} onChange={event => setConfirmation(event.target.value)} disabled={saving} /></label>
      {error && <strong className="auth-error" role="alert">{error}</strong>}
      <button className="auth-submit" type="submit" disabled={saving}>{saving ? 'Saving...' : 'Change password & continue'}</button>
      <button type="button" disabled={saving} onClick={() => void onLogout()}>Sign out</button>
    </form>
  </section></main>
}

function loginErrorMessage(status: number, error: string) {
  if (status === 400) return error || 'Email and password are required.'
  if (status === 401) return 'Invalid email or password.'
  if (status === 0) {
    if (/timed out|abort/i.test(error)) return 'Login request timed out. API or database did not respond.'
    return 'Unable to reach the API. Check that the server and database are running.'
  }
  if (status >= 500) return error || 'Server error while signing in. Check API/database logs.'
  return error || `Login failed with status ${status}.`
}
