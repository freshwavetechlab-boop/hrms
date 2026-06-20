import { useEffect, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { getCurrentUser, login as authenticate, logout as endSession } from '../services/authService'
import type { AuthUser } from '../types/payroll'

export default function AuthGate({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null), [email, setEmail] = useState('admin@paymint.local'), [password, setPassword] = useState('Admin@12345')
  const [loading, setLoading] = useState(true), [error, setError] = useState('')

  useEffect(() => {
    const token = localStorage.getItem('payroll.auth.token')
    if (!token) { setLoading(false); return }
    void getCurrentUser().then(data => { if (data) setUser(data); else localStorage.removeItem('payroll.auth.token') }).finally(() => setLoading(false))
  }, [])

  const login = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    const data = await authenticate(email, password)
    if (!data) { setError('Invalid email or password.'); return }
    localStorage.setItem('payroll.auth.token', data.token)
    localStorage.setItem('payroll.auth.user', JSON.stringify(data.user))
    setUser(data.user)
  }

  const logout = async () => {
    await endSession()
    localStorage.removeItem('payroll.auth.token')
    localStorage.removeItem('payroll.auth.user')
    setUser(null)
  }

  if (loading) return <main className="auth-shell"><section className="auth-card"><h1>Paymint</h1><p>Restoring secure workspace...</p></section></main>
  if (!user) return <main className="auth-shell"><section className="auth-card"><span className="eyebrow purple">Secure Workspace</span><h1>Sign in to Paymint</h1><p>Enterprise payroll requires authenticated, auditable access before any setup, employee, or payroll operation.</p><form onSubmit={login}><label>Email<input value={email} onChange={event => setEmail(event.target.value)} autoComplete="username" /></label><label>Password<input type="password" value={password} onChange={event => setPassword(event.target.value)} autoComplete="current-password" /></label>{error && <strong className="auth-error">{error}</strong>}<button>Sign in</button></form><small>Bootstrap admin: admin@paymint.local / Admin@12345</small></section></main>
  return <><div className="auth-session"><span>{user.displayName}</span><small>{user.roles.join(', ')}</small><button type="button" onClick={() => void logout()}>Logout</button></div>{children}</>
}
