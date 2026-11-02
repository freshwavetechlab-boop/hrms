import { createContext, useContext, useEffect, useState } from 'react'
import type { FormEvent, ReactNode, SyntheticEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { EyeInvisibleOutlined, EyeOutlined } from '@ant-design/icons'
import { changePassword, getCurrentUser, login as authenticate, logout as endSession } from '../services/authService'
import type { AuthUser } from '../types/payroll'
import { recruitmentLoginPath } from '../utils/recruitmentDashboardAccess'
import { useToast } from './ToastProvider'

const productLogo = '/assets/FrevoOneLogo.png'
const loginLogo = '/assets/FrevoOneLoginLogo.png'

type AuthSession = { user: AuthUser; logout: () => Promise<void> }
const AuthSessionContext = createContext<AuthSession | null>(null)
export const useAuthSession = () => useContext(AuthSessionContext)

export default function AuthGate({ children }: { children: ReactNode }) {
  const navigate = useNavigate()
  const toast = useToast()
  const [user, setUser] = useState<AuthUser | null>(null), [email, setEmail] = useState(import.meta.env.DEV ? 'admin@paymint.local' : ''), [password, setPassword] = useState(import.meta.env.DEV ? 'Admin@12345' : '')
  const [loading, setLoading] = useState(true), [error, setError] = useState(''), [showPassword, setShowPassword] = useState(false), [signingIn, setSigningIn] = useState(false)

  useEffect(() => {
    const expire = () => { sessionStorage.removeItem('payroll.auth.token'); localStorage.removeItem('payroll.auth.token'); localStorage.removeItem('payroll.auth.user'); setUser(null) }
    window.addEventListener('payroll:unauthorized', expire)
    void getCurrentUser().then(data => { if (data) setUser(data); else expire() }).finally(() => setLoading(false))
    return () => window.removeEventListener('payroll:unauthorized', expire)
  }, [])
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
    setUser(response.data.user)
    toast('Signed in successfully.', 'success')
    navigate(recruitmentLoginPath(response.data.user.permissions), { replace: true })
  }

  const logout = async () => {
    await endSession()
    sessionStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.user')
    setUser(null)
  }

  const useProductLogo = (event: SyntheticEvent<HTMLImageElement>) => { event.currentTarget.onerror = null; event.currentTarget.src = productLogo }
  if (loading) return <main className="auth-shell"><section className="auth-card"><img className="auth-product-logo" src={loginLogo} onError={useProductLogo} alt="Frevo One HR" /></section></main>
  if (!user) return <main className="auth-shell"><section className="auth-card auth-login-card"><div className="auth-brand-panel"><img className="auth-product-logo" src={loginLogo} onError={useProductLogo} alt="Frevo One HR" /></div><form onSubmit={login}><label><span>Email</span><input value={email} onChange={event => setEmail(event.target.value)} autoComplete="username" placeholder="Enter email or login ID" disabled={signingIn} /></label><label><span>Password</span><div className="auth-password-field"><input type={showPassword ? 'text' : 'password'} value={password} onChange={event => setPassword(event.target.value)} autoComplete="current-password" placeholder="Enter password" disabled={signingIn} /><button type="button" aria-label={showPassword ? 'Hide password' : 'Show password'} title={showPassword ? 'Hide password' : 'Show password'} onClick={() => setShowPassword(current => !current)} disabled={signingIn}>{showPassword ? <EyeInvisibleOutlined /> : <EyeOutlined />}</button></div></label>{error && <strong className="auth-error">{error}</strong>}<button className="auth-submit" type="submit" disabled={signingIn}>{signingIn ? 'Signing in...' : 'Sign in'}</button></form></section></main>
  if (user.mustChangePassword) return <RequiredPasswordChange user={user} onChanged={updated => { setUser(updated); setPassword('') }} onLogout={logout} />
  return <AuthSessionContext.Provider value={{ user, logout }}>{children}</AuthSessionContext.Provider>
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
