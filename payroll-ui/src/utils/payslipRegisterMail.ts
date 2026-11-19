import type { PayslipMailJobRequest, PayslipMailJobSubmission, PayslipMailOverview } from '../services/payslipService'

export function validRegisterMail(value: PayslipMailOverview | null, clientId: number, runId: number): value is PayslipMailOverview {
  return !!value && value.clientId === clientId && value.payRunId === runId && Array.isArray(value.items) && Array.isArray(value.jobs)
    && new Set(value.items.map(item => item.employeeId)).size === value.items.length
    && value.items.every(item => Number.isSafeInteger(item.employeeId) && typeof item.canSend === 'boolean' && typeof item.canResend === 'boolean'
      && typeof item.email === 'string' && typeof item.status === 'string')
    && value.jobs.every(job => job.clientId === clientId && job.payRunId === runId && typeof job.id === 'string'
      && typeof job.isComplete === 'boolean' && Number.isFinite(job.percentComplete) && job.percentComplete >= 0 && job.percentComplete <= 100)
}

export function validRegisterMailSubmission(value: PayslipMailJobSubmission | null, clientId: number, runId: number, request: PayslipMailJobRequest): value is PayslipMailJobSubmission {
  return !!value && value.job?.id === request.requestId && value.job.clientId === clientId && value.job.payRunId === runId
    && Array.isArray(value.items) && value.items.length === request.employeeIds.length
    && new Set(value.items.map(item => item.employeeId)).size === request.employeeIds.length
    && value.items.every(item => request.employeeIds.includes(item.employeeId) && ['Queued', 'Already queued', 'Excluded'].includes(item.status))
}

export function registerMailFilter(status: string, filter: string) {
  return filter === 'All' || (filter === 'Sent' ? ['Sent', 'Delivered'].includes(status)
    : filter === 'Needs attention' ? ['Failed', 'Bounced', 'Deferred', 'Unknown'].includes(status) : status === filter)
}
