import { createContext, useContext, useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { getCurrentUser, login as authenticate, logout as endSession } from '../services/authService'
import type { AuthUser } from '../types/payroll'

type AuthSession = { user: AuthUser; logout: () => Promise<void> }
const AuthSessionContext = createContext<AuthSession | null>(null)
export const useAuthSession = () => useContext(AuthSessionContext)

export default function AuthGate({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null), [email, setEmail] = useState(import.meta.env.DEV ? 'admin@paymint.local' : ''), [password, setPassword] = useState(import.meta.env.DEV ? 'Admin@12345' : '')
  const [loading, setLoading] = useState(true), [error, setError] = useState(''), [accountOpen, setAccountOpen] = useState(false)
  const accountRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const expire = () => { sessionStorage.removeItem('payroll.auth.token'); localStorage.removeItem('payroll.auth.token'); localStorage.removeItem('payroll.auth.user'); setUser(null) }
    window.addEventListener('payroll:unauthorized', expire)
    void getCurrentUser().then(data => { if (data) setUser(data); else expire() }).finally(() => setLoading(false))
    return () => window.removeEventListener('payroll:unauthorized', expire)
  }, [])
  useEffect(() => {
    const close = (event: MouseEvent) => { if (!accountRef.current?.contains(event.target as Node)) setAccountOpen(false) }
    if (accountOpen) document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [accountOpen])

  const login = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    const data = await authenticate(email, password)
    if (!data) { setError('Invalid email or password.'); return }
    sessionStorage.setItem('payroll.auth.token', data.token)
    localStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.user')
    setUser(data.user)
  }

  const logout = async () => {
    await endSession()
    sessionStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.user')
    setUser(null)
  }

  if (loading) return <main className="auth-shell"><section className="auth-card"><h1>Frevo One HR</h1><p>Restoring secure workspace...</p></section></main>
  if (!user) return <main className="auth-shell"><section className="auth-card"><span className="eyebrow purple">Secure Workspace</span><h1>Sign in to Frevo One HR</h1><form onSubmit={login}><label>Email<input value={email} onChange={event => setEmail(event.target.value)} autoComplete="username" /></label><label>Password<input type="password" value={password} onChange={event => setPassword(event.target.value)} autoComplete="current-password" /></label>{error && <strong className="auth-error">{error}</strong>}<button>Sign in</button></form>{import.meta.env.DEV }</section></main>
  return <AuthSessionContext.Provider value={{ user, logout }}><div className="auth-session" ref={accountRef}><button className="auth-avatar" type="button" aria-label="Open account menu" aria-expanded={accountOpen} onClick={() => setAccountOpen(open => !open)}><span>{user.displayName.split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase()}</span></button>{accountOpen && <div className="auth-dropdown"><strong>{user.displayName}</strong><small>{user.email}</small><span>{user.roles.join(', ')}</span><button type="button" onClick={() => void logout()}>Logout</button></div>}</div>{children}</AuthSessionContext.Provider>
}
