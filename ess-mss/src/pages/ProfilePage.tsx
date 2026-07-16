import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { LoadState, ProfileData, SaveProfileData, User } from '../types'
import { essApi } from '../services/essApi'
import { initials, showToast } from '../utils/ui'

export function ProfilePage({ user }: { user: User }) {
  const [profile, setProfile] = useState<ProfileData | null>(null)
  const [form, setForm] = useState<SaveProfileData | null>(null)
  const [state, setState] = useState<LoadState>('loading')
  const [saving, setSaving] = useState(false)
  useEffect(() => { void load() }, [user.email])
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { section: 'Home', title: 'My profile' } }))
  }, [])

  const load = async () => {
    setState('loading')
    try {
      const data = await essApi.profile()
      setProfile(data)
      setForm(data ? toForm(data) : null)
      setState(data ? 'ready' : 'error')
    } catch {
      setState('error')
    }
  }

  const set = <K extends keyof SaveProfileData>(key: K, value: SaveProfileData[K]) => setForm(current => current ? { ...current, [key]: value } : current)
  const save = async (event: FormEvent) => {
    event.preventDefault()
    if (!form) return
    setSaving(true)
    try {
      const saved = await essApi.saveProfile(form)
      setProfile(saved)
      setForm(toForm(saved))
      showToast('Profile updated.', 'success')
    } catch (error) {
      showToast(error instanceof Error ? error.message : 'Unable to update profile.', 'error')
    } finally {
      setSaving(false)
    }
  }

  if (state === 'loading') return <section className="feature-page"><div className="empty-work"><span>Loading your profile...</span></div></section>
  if (state === 'error' || !profile || !form) return <section className="feature-page"><div className="empty-work"><b>Profile is unavailable.</b><span>Your account may not yet be linked to an active employee record. Contact HR for assistance.</span></div></section>
  const fields = [['Employee code', profile.employeeCode], ['Department', profile.department || 'Not assigned'], ['Designation', profile.designation || 'Not assigned'], ['Work location', profile.workLocation || 'Not assigned'], ['Attendance office', profile.attendanceOffice || 'Not configured'], ['Joining date', profile.dateOfJoining ? new Date(profile.dateOfJoining).toLocaleDateString('en-IN') : 'Not available'], ['Reporting manager', profile.reportingManager.trim() || 'Not assigned']]
  return <form className="profile-page profile-edit-page" onSubmit={save}>
    <div className="profile-head">
      <span>{initials(`${profile.firstName} ${profile.lastName}`)}</span>
      <div><h3>{`${profile.firstName} ${profile.lastName}`.trim()}</h3><p>{profile.employeeCode} / {profile.designation || 'Employee'}</p></div>
      <small className={profile.canEdit ? 'profile-edit-badge on' : 'profile-edit-badge'}>{profile.canEdit ? 'Self update enabled' : 'Self update disabled'}</small>
    </div>
    <div className="profile-grid readonly">{fields.map(([label, value]) => <div key={label}><span>{label}</span><b>{value}</b></div>)}</div>
    {profile.canEdit && <>
      <section className="profile-form-section"><h4>Basic and contact details</h4><div className="travel-form-grid profile-form-grid">
        <label><span>First name</span><input value={form.firstName} onChange={event => set('firstName', event.target.value)} /></label>
        <label><span>Last name</span><input value={form.lastName} onChange={event => set('lastName', event.target.value)} /></label>
        <label><span>Work email</span><input type="email" value={form.workEmail} onChange={event => set('workEmail', event.target.value)} /></label>
        <label><span>Date of birth</span><input type="date" value={dateInput(form.dateOfBirth)} onChange={event => set('dateOfBirth', event.target.value)} /></label>
        <label><span>Mobile</span><input value={form.mobile} onChange={event => set('mobile', event.target.value)} /></label>
        <label><span>PAN</span><input value={form.panNumber} onChange={event => set('panNumber', event.target.value)} /></label>
        <label><span>Aadhaar</span><input value={form.aadhaarNumber} onChange={event => set('aadhaarNumber', event.target.value)} /></label>
      </div></section>
      <section className="profile-form-section"><h4>Address</h4><div className="travel-form-grid profile-form-grid">
        <label className="wide"><span>Current address</span><textarea value={form.address} onChange={event => set('address', event.target.value)} /></label>
        <label className="wide"><span>Correspondence address</span><textarea value={form.correspondenceAddress} onChange={event => set('correspondenceAddress', event.target.value)} /></label>
        <label className="wide"><span>Permanent address</span><textarea value={form.permanentAddress} onChange={event => set('permanentAddress', event.target.value)} /></label>
        <label><span>City</span><input value={form.city} onChange={event => set('city', event.target.value)} /></label>
        <label><span>District</span><input value={form.district} onChange={event => set('district', event.target.value)} /></label>
        <label><span>State</span><input value={form.state} onChange={event => set('state', event.target.value)} /></label>
      </div></section>
      <section className="profile-form-section"><h4>Bank details</h4><div className="travel-form-grid profile-form-grid">
        <label><span>Bank name</span><input value={form.bankName} onChange={event => set('bankName', event.target.value)} /></label>
        <label><span>Account number</span><input value={form.bankAccountNo} onChange={event => set('bankAccountNo', event.target.value)} /></label>
        <label><span>IFSC</span><input value={form.ifscCode} onChange={event => set('ifscCode', event.target.value)} /></label>
        <label><span>Payment mode</span><select value={form.paymentMode} onChange={event => set('paymentMode', event.target.value)}><option value="">Select</option><option>Bank Transfer</option><option>Cheque</option><option>Cash</option></select></label>
      </div></section>
      <div className="profile-actions"><button type="button" className="secondary" onClick={() => setForm(toForm(profile))}>Reset</button><button disabled={saving}>{saving ? 'Saving...' : 'Save profile'}</button></div>
    </>}
  </form>
}

function toForm(profile: ProfileData): SaveProfileData {
  return {
    firstName: profile.firstName || '',
    lastName: profile.lastName || '',
    workEmail: profile.workEmail || '',
    dateOfBirth: dateInput(profile.dateOfBirth),
    mobile: profile.mobile || '',
    panNumber: profile.panNumber || '',
    aadhaarNumber: profile.aadhaarNumber || '',
    address: profile.address || '',
    correspondenceAddress: profile.correspondenceAddress || '',
    permanentAddress: profile.permanentAddress || '',
    city: profile.city || '',
    district: profile.district || '',
    state: profile.state || '',
    bankName: profile.bankName || '',
    bankAccountNo: profile.bankAccountNo || '',
    ifscCode: profile.ifscCode || '',
    paymentMode: profile.paymentMode || '',
  }
}

function dateInput(value: string) {
  return value ? String(value).slice(0, 10) : ''
}
