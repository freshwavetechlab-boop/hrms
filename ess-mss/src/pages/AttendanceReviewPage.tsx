import { useState } from 'react'
import MssAttendanceManager from '../components/MssAttendanceManager'
import type { User } from '../types'

export function AttendanceReviewPage({ user }: { user: User }) {
  const clientId = user.clientId ?? 0
  const [notice, setNotice] = useState<{ message: string; type: string } | null>(null)

  if (!clientId) return <section className="mss-attendance-review"><div className="attendance-policy-empty"><h3>Manager account mapping required</h3><p>Assign this MSS manager to a client before granting attendance management.</p></div></section>

  return <section className="mss-attendance-review payroll-attendance-page">
    {notice && <div className={`mss-attendance-notice ${notice.type}`} role="status">{notice.message}<button type="button" onClick={() => setNotice(null)}>Dismiss</button></div>}
    <MssAttendanceManager clientId={clientId} onMessage={(message, type = 'success') => setNotice({ message, type })} />
  </section>
}
