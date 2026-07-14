import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { OrganizationBrand, User } from '../types'
import { login, organizationBrand, setToken } from '../services/essApi'

export function LoginPage({ onLogin }: { onLogin: (user: User) => void }) {
  const [email, setEmail] = useState(''), [password, setPassword] = useState(''), [error, setError] = useState(''), [busy, setBusy] = useState(false), [showPassword, setShowPassword] = useState(false)
  const [organization, setOrganization] = useState<OrganizationBrand | null>(null)
  useEffect(() => { void organizationBrand().then(setOrganization).catch(() => undefined) }, [])
  const submit = async (event: FormEvent) => { event.preventDefault(); setBusy(true); setError(''); try { const data = await login(email, password); setToken(data.token); onLogin(data.user) } catch (e) { setError(e instanceof Error ? e.message : 'Unable to sign in.') } finally { setBusy(false) } }
  return <main className="ess-login">
    <section className="login-panel">
      <div className="login-card">
        <div className="login-card-head">
          <img src="/assets/FrevoOneLoginLogo.png" alt="Frevo One HR" />
          <span className="eyebrow">Secure sign in</span>
          <h2>Employee login</h2>
          <p>Use your organization login credentials to continue.</p>
        </div>
        <form onSubmit={submit}>
          <label><span>Employee code / Login ID</span><input type="text" value={email} onChange={e => setEmail(e.target.value)} autoComplete="username" placeholder="Enter employee code" required /></label>
          <label><span>Password</span><div className="ess-password-field"><input type={showPassword ? 'text' : 'password'} value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" placeholder="Enter password" required /><button type="button" onClick={() => setShowPassword(value => !value)}>{showPassword ? 'Hide' : 'Show'}</button></div></label>
          {error && <p className="form-error">{error}</p>}
          <button disabled={busy}>{busy ? 'Signing in...' : 'Sign in'}</button>
        </form>
        <div className="login-card-foot">
          <span>Protected employee access</span>
          <small>Contact HR/payroll if your login is not active.</small>
        </div>
      </div>
    </section>
    <section className="login-intro" aria-label="Frevo One employee portal">
      <div className="login-org-card">
        <img src={organization?.logoDataUrl || '/assets/organization-logo.png'} alt={organization?.name || 'Organization logo'} />
      </div>
      <div className="login-hero-copy">
        <span className="eyebrow">Employee Self Service</span>
        <h1>Welcome to your employee workspace</h1>
      </div>
    </section>
  </main>
}
