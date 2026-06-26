import { createContext, useContext, useEffect, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { getCurrentUser, login as authenticate, logout as endSession } from '../services/authService'
import type { AuthUser } from '../types/payroll'

type AuthSession = { user: AuthUser; logout: () => Promise<void> }
const AuthSessionContext = createContext<AuthSession | null>(null)
export const useAuthSession = () => useContext(AuthSessionContext)

export default function AuthGate({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null), [email, setEmail] = useState(import.meta.env.DEV ? 'admin@paymint.local' : ''), [password, setPassword] = useState(import.meta.env.DEV ? 'Admin@12345' : '')
  const [loading, setLoading] = useState(true), [error, setError] = useState(''), [accountOpen, setAccountOpen] = useState(false)

  useEffect(() => {
    const expire = () => setUser(null)
    window.addEventListener('payroll:unauthorized', expire)
    void getCurrentUser().then(data => { if (data) setUser(data); else expire() }).finally(() => setLoading(false))
    return () => window.removeEventListener('payroll:unauthorized', expire)
  }, [])

  const login = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    const data = await authenticate(email, password)
    if (!data) { setError('Invalid email or password.'); return }
    setUser(data.user)
  }

  const logout = async () => {
    await endSession()
    setUser(null)
  }

  if (loading) return <main className="auth-shell"><section className="auth-card"><h1>Frevo One HR</h1><p>Restoring secure workspace...</p></section></main>
  if (!user) return <main className="auth-shell"><section className="auth-card"><span className="eyebrow purple">Secure Workspace</span><h1>Sign in to Frevo One HR</h1><form onSubmit={login}><label>Email<input value={email} onChange={event => setEmail(event.target.value)} autoComplete="username" /></label><label>Password<input type="password" value={password} onChange={event => setPassword(event.target.value)} autoComplete="current-password" /></label>{error && <strong className="auth-error">{error}</strong>}<button>Sign in</button></form>{import.meta.env.DEV }</section></main>
  return <AuthSessionContext.Provider value={{ user, logout }}><div className="auth-session"><button className="auth-avatar" type="button" aria-label="Open account menu" aria-expanded={accountOpen} onClick={() => setAccountOpen(open => !open)}>{user.displayName.split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase()}</button>{accountOpen && <div className="auth-dropdown"><strong>{user.displayName}</strong><small>{user.email}</small><span>{user.roles.join(', ')}</span><button type="button" onClick={() => void logout()}>Logout</button></div>}</div>{children}</AuthSessionContext.Provider>
}
