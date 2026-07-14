import { useEffect } from 'react'
import type { View } from '../types'

export function PlaceholderPage({ view, manager }: { view: View; manager: boolean }) {
  const copy: Record<Exclude<View, 'Dashboard' | 'Recruitment'>, string> = { 'My Tasks': '', 'My Profile': 'Keep your personal, contact, and payment information accurate. Changes that require verification will be routed to HR.', Leave: '', Travel: 'Create and track travel requests based on your applicable company travel policy.', Expense: 'Create and track reimbursement claims against travel or standalone expenses.', Attendance: manager ? 'Review team attendance exceptions and take action on regularization requests.' : 'Review daily attendance, correct exceptions, and submit regularization when permitted.', Pay: 'Access published payslips, compensation details, and tax documents when released by payroll.', Tax: 'Select tax regime and submit declarations when payroll opens the configured windows.', Team: 'View direct reports, their core details, availability, and upcoming leave.', Approvals: 'Review requests assigned to you. Decisions will be recorded in the audit trail.' }
  useEffect(() => {
    window.dispatchEvent(new CustomEvent('ess:page-title', { detail: { title: view } }))
  }, [view])
  return <section className="feature-page"><div className="empty-work"><b>This feature is ready for activation.</b><span>{copy[view as Exclude<View, 'Dashboard' | 'Recruitment'>] || 'The navigation, access boundary, user guidance, and workspace layout are in place.'}</span></div></section>
}

