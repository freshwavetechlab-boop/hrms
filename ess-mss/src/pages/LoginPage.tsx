import { useState } from 'react'
import type { FormEvent } from 'react'
import type { User } from '../types'
import { login, setToken } from '../services/essApi'

export function LoginPage({ onLogin }: { onLogin: (user: User) => void }) {
  const [email, setEmail] = useState(''), [password, setPassword] = useState(''), [error, setError] = useState(''), [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => { event.preventDefault(); setBusy(true); setError(''); try { const data = await login(email, password); setToken(data.token); onLogin(data.user) } catch (e) { setError(e instanceof Error ? e.message : 'Unable to sign in.') } finally { setBusy(false) } }
  return <main className="ess-login"><section className="login-intro"><div className="product-mark">F</div><span className="eyebrow">Employee & Manager Workspace</span><h1>Frevo One HR</h1><p>Leave, attendance, pay, and approvals in one clean workspace.</p></section><section className="login-panel"><div><span className="eyebrow">Secure sign in</span><h2>Welcome back</h2><p>Use your organization login credentials.</p></div><form onSubmit={submit}><label><span>Work email</span><input type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="username" placeholder="name@company.com" required /></label><label><span>Password</span><input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" placeholder="Enter password" required /></label>{error && <p className="form-error">{error}</p>}<button disabled={busy}>{busy ? 'Signing in...' : 'Sign in'}</button></form></section></main>
}
