import { useRef, useState } from 'react'
import { Alert, Button, Input, Modal, Space, Tag } from 'antd'
import { applyEmptySalarySetup, previewEmptySalarySetup, type EmptySalarySetupPreview } from '../services/emptySalarySetupService'

function closedMonth() {
  const now = new Date(), month = new Date(now.getFullYear(), now.getMonth() - 1, 1)
  return `${month.getFullYear()}-${String(month.getMonth() + 1).padStart(2, '0')}`
}
export default function EmptySalarySetupCleanup({ employeeId, clientId, employeeCode, onApplied }: {
  employeeId: number; clientId: number; employeeCode: string; onApplied: () => void
}) {
  const [open, setOpen] = useState(false), [month, setMonth] = useState(closedMonth)
  const [preview, setPreview] = useState<EmptySalarySetupPreview | null>(null)
  const [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [reason, setReason] = useState('Delete empty initial setup entries so the saved month-start salary remains effective.')
  const requestId = useRef('')
  const review = async () => {
    setBusy(true); setPreview(null); setError('')
    try {
      const result = await previewEmptySalarySetup(employeeId, clientId, month)
      if (!result.ok || !result.data) throw Error(result.error || 'Cleanup preview is unavailable. The reviewed API build must be running.')
      if (result.data.employeeId !== employeeId || result.data.clientId !== clientId || result.data.payPeriod !== month) throw Error('Cleanup scope changed. Refresh this employee.')
      requestId.current = crypto.randomUUID(); setPreview(result.data)
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Cleanup review failed.') }
    finally { setBusy(false) }
  }
  const apply = async () => {
    if (!preview?.canApply || !requestId.current || busy) return
    setBusy(true); setError('')
    try {
      const result = await applyEmptySalarySetup(preview, requestId.current, reason)
      if (!result.ok || !result.data) throw Error(result.error || 'Cleanup save could not be confirmed. Retry with this same review reference.')
      if (result.data.employeeId !== employeeId || result.data.clientId !== clientId || result.data.payPeriod !== month || result.data.requestId !== requestId.current.replaceAll('-', '')) throw Error('Cleanup response did not match this review. Refresh before continuing.')
      setOpen(false); onApplied()
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Cleanup save failed.') }
    finally { setBusy(false) }
  }
  return <>
    <Button size="small" onClick={() => { setOpen(true); setPreview(null); setError('') }}>Review empty salary setup</Button>
    {open && <Modal open centered title={`Empty salary setup · ${employeeCode}`} bodyStyle={{ maxHeight: '65dvh', overflowY: 'auto', minWidth: 0 }} onCancel={() => { if (!busy) setOpen(false) }} maskClosable={!busy} closable={!busy} footer={<Space wrap><Button disabled={busy} onClick={() => setOpen(false)}>Close</Button><Button loading={busy} disabled={!month} onClick={() => void review()}>Review month</Button><Button type="primary" loading={busy} disabled={!preview?.canApply || reason.trim().length < 12} onClick={() => void apply()}>Apply reviewed cleanup</Button></Space>}>
      <p>Deletes only the listed empty setup entries. The correct month-start salary remains effective. No backup or salary copy is created. Employee details, other months and saved payroll stay unchanged.</p>
      <label className="simulation-field">Payroll month<Input aria-label="Empty salary cleanup month" type="month" value={month} disabled={busy} onChange={event => { setMonth(event.target.value); setPreview(null) }} /></label>
      <label className="simulation-field">Reason<Input aria-label="Empty salary cleanup reason" value={reason} maxLength={500} disabled={busy} onChange={event => setReason(event.target.value)} /></label>
      {preview && <><p>Saved template: {preview.templateId || 'Unavailable'}</p><Space wrap>{preview.emptyEffectiveDates.map(date => <Tag key={date}>{date}</Tag>)}</Space>{preview.canApply && <Alert type="warning" message="The listed empty entries will be permanently deleted." />}{!preview.emptyEffectiveDates.length && <Alert type="info" message="No empty active setup entries need cleanup in this month." />}{preview.blockers.map(message => <Alert key={message} type="warning" message={message} />)}</>}
      {error && <Alert type="error" message={error} />}
    </Modal>}
  </>
}
