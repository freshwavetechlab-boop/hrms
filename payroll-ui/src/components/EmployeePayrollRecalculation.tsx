import { useRef, useState } from 'react'
import { Alert, Button, Checkbox, Drawer, Input } from 'antd'
import { Link } from 'react-router-dom'
import { getJsonResult, postJson } from '../services/apiClient'
import type { PayRun, RunEmployee } from '../types/payroll'
import { recalculationPreviewError, type EmployeeRecalculationPreview } from '../utils/employeePayrollRecalculation'
import { money } from '../utils/salary'
import { presentDayText } from '../utils/payrollAttendanceDisplay'
import DataTable from './DataTable'

export default function EmployeePayrollRecalculation({ run, employee, disabled, onChanged }: { run: PayRun; employee: RunEmployee; disabled: boolean; onChanged: () => Promise<void> }) {
  const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [preview, setPreview] = useState<EmployeeRecalculationPreview | null>(null), [confirmed, setConfirmed] = useState(false), [reason, setReason] = useState('')
  const pending = useRef(false), sequence = useRef(0)
  const path = `/api/pay-runs/${run.id}/employees/${employee.employeeId}`
  const refresh = async () => {
    if (pending.current) return
    const request = ++sequence.current
    pending.current = true; setBusy(true); setError(''); setPreview(null); setConfirmed(false)
    try {
      const response = await getJsonResult<EmployeeRecalculationPreview | null>(`${path}/recalculation-preview`, null, { toast: false, loader: false, timeoutMs: 120000 })
      if (request !== sequence.current) return
      if (!response.ok || !response.data) throw Error(response.error || 'Calculation preview is unavailable. Check that the updated API is running.')
      const failure = recalculationPreviewError(response.data, run, employee)
      if (failure) throw Error(failure)
      setPreview(response.data)
    } catch (failure) { if (request === sequence.current) setError(failure instanceof Error ? failure.message : 'Unable to preview this employee.') }
    finally { if (request === sequence.current) { pending.current = false; setBusy(false) } }
  }
  const save = async () => {
    if (pending.current || !preview || !confirmed || !reason.trim() || recalculationPreviewError(preview, run, employee)) return
    const fingerprint = preview.expectedFingerprint
    pending.current = true; setBusy(true); setPreview(null); setConfirmed(false); setError('')
    try {
      const response = await postJson(`${path}/recalculate`, { expectedFingerprint: fingerprint, confirmed: true, reason: reason.trim() }, null,
        { toast: false, timeoutMs: 180000, timeoutMessage: 'Save outcome is unconfirmed. Refresh this payroll before retrying; no automatic retry was made.' })
      if (!response.ok) throw Error(response.error || 'Recalculation could not be confirmed. Refresh this payroll before retrying.')
      await onChanged(); setOpen(false)
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to save this employee. Refresh before retrying.') }
    finally { pending.current = false; setBusy(false) }
  }
  return <>
    <Button size="small" disabled={disabled || run.status !== 'Draft' || employee.isSkipped} onClick={() => { setOpen(true); setReason(''); void refresh() }}>Recalculate</Button>
    {open && <Drawer open title={`Recalculate · ${employee.employeeCode}`} width="min(700px, 100vw)" closable={!busy} maskClosable={!busy} keyboard={!busy} onClose={() => { if (!pending.current) { sequence.current++; setOpen(false); setPreview(null); setConfirmed(false) } }} footer={<div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}><Button disabled={busy} onClick={() => void refresh()}>Refresh preview</Button><Button type="primary" loading={busy} disabled={busy || !preview || !confirmed || !reason.trim()} onClick={() => void save()}>Update only {employee.employeeCode}</Button></div>}>
      <p>{run.clientName} · {run.payPeriod} · {employee.employeeName}</p>
      <Alert type="info" showIcon message="One employee. Same payroll run." description="The native engine reads saved salary and attendance inputs for this employee. Review the changes before saving. Other employees and existing mail history stay unchanged." />
      <p><Link to={`/attendance?clientId=${run.clientId}&period=${run.payPeriod}&employeeIds=${employee.employeeId}`} target="_blank" rel="noopener noreferrer">Review attendance inputs ↗</Link> · <Link to="/employees/master" target="_blank" rel="noopener noreferrer">Open Employee Master ↗</Link></p>
      {busy && <p role="status">Checking this employee’s calculation…</p>}
      {error && <Alert type="error" showIcon message={error} />}
      {preview?.before && preview.after && <>
        <DataTable title="Before & after" rows={[
          { field: 'Gross pay', before: money(preview.before.gross), after: money(preview.after.gross) },
          { field: 'Deductions', before: money(preview.before.deductions), after: money(preview.after.deductions) },
          { field: 'Net pay', before: money(preview.before.net), after: money(preview.after.net) },
          { field: 'Actual present', before: presentDayText(preview.before.presentDays), after: presentDayText(preview.after.presentDays) },
          { field: 'Payable days', before: String(preview.before.payableDays), after: String(preview.after.payableDays) },
        ]} getRowId={row => row.field} exportDisabled columns={[{ key: 'field', label: 'Calculation' }, { key: 'before', label: 'Saved' }, { key: 'after', label: 'Recalculated' }]} pageSizeOptions={[5]} />
        <DataTable title="Component changes" rows={preview.changes} getRowId={row => `${row.category}:${row.code}`} exportDisabled emptyText="No component amounts changed." columns={[{ key: 'name', label: 'Component' }, { key: 'before', label: 'Saved', render: row => money(row.before) }, { key: 'after', label: 'Recalculated', render: row => money(row.after) }]} pageSizeOptions={[10, 25]} />
        <label className="simulation-field">Reason for correction<Input.TextArea maxLength={500} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <Checkbox checked={confirmed} onChange={event => setConfirmed(event.target.checked)}>I reviewed this employee’s changes and confirm updating only this employee in run #{run.id}.</Checkbox>
      </>}
    </Drawer>}
  </>
}
