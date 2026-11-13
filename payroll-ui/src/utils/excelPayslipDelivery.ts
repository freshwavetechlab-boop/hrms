import type { ExcelPayslipEmailStatus } from '../services/excelPayslipService'

export function canQueuePayslipEmail(status: ExcelPayslipEmailStatus | undefined, mode: 'Individual' | 'Combined', email: string) {
  if (!status) return false
  if (mode === 'Individual' && !status.canSend) return false
  const history = status.recipientDeliveries || []
  return (history.length > 0 || status.canSend) && !history.some(item => item.mode === 'Unknown' || item.status === 'Unknown'
    || item.email.toLowerCase() === email.trim().toLowerCase())
}

export function correctedPayslipResendBlockReason(status: ExcelPayslipEmailStatus | undefined) {
  if (!status) return 'Email status has not been checked.'
  const history = status.recipientDeliveries || []
  if (status.status === 'Queued' || history.some(item => item.mode !== 'Combined' && item.status === 'Queued')) return 'An employee email is already queued or processing.'
  if (status.status === 'Unknown' || history.some(item => item.mode === 'Unknown' || item.status === 'Unknown')) return 'Previous delivery is uncertain. Resolve it before sending again.'
  if (status.status === 'Deferred' || history.some(item => item.mode !== 'Combined' && item.status === 'Deferred')) return 'Previous delivery is delayed. Wait for its final status.'
  return ''
}

// This separate, explicitly acknowledged path may include completed deliveries.
// Ordinary sends continue to use canQueuePayslipEmail and skip sent recipients.
export function canQueueCorrectedPayslipEmail(status: ExcelPayslipEmailStatus | undefined) {
  return Boolean(status) && !correctedPayslipResendBlockReason(status)
}

export function payslipSentCopyLabel(status: string | undefined) {
  return status === 'Saved' ? 'Saved in Sent folder' : status === 'Failed' ? 'Sent copy failed'
    : status === 'NotConfigured' ? 'Sent copy unavailable: IMAP off' : 'Sent copy not verified'
}
