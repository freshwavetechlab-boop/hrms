import { useEffect, useState } from 'react'
import type { LoadState, ProfileData, User } from '../types'
import { essApi } from '../services/essApi'
import { initials } from '../utils/ui'

export function ProfilePage({ user }: { user: User }) {
  const [profile, setProfile] = useState<ProfileData | null>(null), [state, setState] = useState<LoadState>('loading')
  useEffect(() => { void essApi.profile().then(data => { setProfile(data); setState(data ? 'ready' : 'error') }).catch(() => setState('error')) }, [user.email])
  if (state === 'loading') return <section className="feature-page"><div className="empty-work"><span>Loading your profile...</span></div></section>
  if (state === 'error' || !profile) return <section className="feature-page"><div className="empty-work"><b>Profile is unavailable.</b><span>Your account may not yet be linked to an active employee record. Contact HR for assistance.</span></div></section>
  const fields = [['Employee code', profile.employeeCode], ['Work email', profile.workEmail], ['Department', profile.department || 'Not assigned'], ['Designation', profile.designation || 'Not assigned'], ['Work location', profile.workLocation || 'Not assigned'], ['Joining date', profile.dateOfJoining ? new Date(profile.dateOfJoining).toLocaleDateString('en-IN') : 'Not available'], ['Reporting manager', profile.reportingManager.trim() || 'Not assigned']]
  return <section className="profile-page"><div className="profile-head"><span>{initials(`${profile.firstName} ${profile.lastName}`)}</span><div><span className="eyebrow">My profile</span><h3>{`${profile.firstName} ${profile.lastName}`.trim()}</h3><p>Employment details managed by your organization.</p></div></div><div className="profile-grid">{fields.map(([label, value]) => <div key={label}><span>{label}</span><b>{value}</b></div>)}</div><div className="empty-work"><b>Need to update your details?</b><span>Personal and payment detail changes will be submitted through a controlled HR verification workflow in the next release.</span></div></section>
}
