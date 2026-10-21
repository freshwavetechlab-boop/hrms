import type { ExcelPayslipEmailStatus } from '../services/excelPayslipService'

export function canQueuePayslipEmail(status: ExcelPayslipEmailStatus | undefined, mode: 'Individual' | 'Combined', email: string) {
  if (!status) return false
  if (mode === 'Individual' && !status.canSend) return false
  const history = status.recipientDeliveries || []
  return (history.length > 0 || status.canSend) && !history.some(item => item.mode === 'Unknown' || item.status === 'Unknown'
    || item.email.toLowerCase() === email.trim().toLowerCase())
}

export function payslipSentCopyLabel(status: string | undefined) {
  return status === 'Saved' ? 'Saved in Sent folder' : status === 'Failed' ? 'Sent copy failed'
    : status === 'NotConfigured' ? 'Sent copy unavailable: IMAP off' : 'Sent copy not verified'
}
