import { useState } from 'react'
import type { FormEvent } from 'react'
import type { User } from '../types'
import { changePassword } from '../services/essApi'
import { showToast } from '../utils/ui'

export function ChangePasswordPage({ forced, onChanged, onCancel }: { forced: boolean; onChanged: (user: User) => void; onCancel?: () => void }) {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    if (newPassword.length < 8) { setError('New password must be at least 8 characters.'); return }
    if (newPassword !== confirmPassword) { setError('New password and confirmation do not match.'); return }
    setBusy(true)
    try {
      const user = await changePassword(currentPassword, newPassword)
      showToast('Password changed.', 'success')
      onChanged(user)
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : 'Unable to change password.')
    } finally {
      setBusy(false)
    }
  }

  return <main className="password-change-shell">
    <form className="password-change-card" onSubmit={submit}>
      <span className="eyebrow">{forced ? 'First login security' : 'Account security'}</span>
      <h2>Change password</h2>
      <p>{forced ? 'Please set a new password before opening the ESS portal.' : 'Update your ESS login password.'}</p>
      <label><span>Current password</span><input type="password" value={currentPassword} onChange={event => setCurrentPassword(event.target.value)} autoComplete="current-password" required /></label>
      <label><span>New password</span><input type="password" value={newPassword} onChange={event => setNewPassword(event.target.value)} autoComplete="new-password" required /></label>
      <label><span>Confirm new password</span><input type="password" value={confirmPassword} onChange={event => setConfirmPassword(event.target.value)} autoComplete="new-password" required /></label>
      {error && <p className="form-error">{error}</p>}
      <div>
        {!forced && <button type="button" className="secondary" onClick={onCancel}>Cancel</button>}
        <button disabled={busy}>{busy ? 'Saving...' : 'Change password'}</button>
      </div>
    </form>
  </main>
}
