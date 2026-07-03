import { createContext, useContext, useEffect, useState } from 'react'
import type { FormEvent, ReactNode, SyntheticEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { EyeInvisibleOutlined, EyeOutlined } from '@ant-design/icons'
import { getCurrentUser, login as authenticate, logout as endSession } from '../services/authService'
import type { AuthUser } from '../types/payroll'
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
    navigate('/dashboard', { replace: true })
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
  return <AuthSessionContext.Provider value={{ user, logout }}>{children}</AuthSessionContext.Provider>
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
