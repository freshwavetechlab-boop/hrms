import { useRef, useState } from 'react'
import { Alert, Button, Checkbox, Drawer } from 'antd'
import { getSelectedPayRunProcessingPreview, processSelectedPayRun, selectedAdjustmentPreviewError, type SelectedPayRunProcessingPreview } from '../services/selectedPayRunService'
import type { PayRun } from '../types/payroll'
import DataTable from './DataTable'

type Props = { run: PayRun; permitted: boolean; onChanged: () => Promise<void> }

export default function SelectedPayRunProcessing(props: Props) {
  const { run } = props
  // A changed run/client/month invalidates all local acknowledgments and pending previews.
  return <SelectedRun key={`${run.id}:${run.clientId}:${run.payPeriod}:${run.status}`} {...props} />
}

function SelectedRun({ run, permitted, onChanged }: Props) {
  const [open, setOpen] = useState(false)
  const [preview, setPreview] = useState<SelectedPayRunProcessingPreview | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const pending = useRef(false)

  const refresh = async () => {
    if (pending.current) return
    pending.current = true; setBusy(true); setConfirmed(false); setPreview(null); setMessage('')
    try {
      const result = await getSelectedPayRunProcessingPreview(run.id, run.clientId, run.payPeriod)
      if (!result.ok || !result.data) { setMessage(result.error || 'Unable to verify run scope. No processing was started.'); return }
      const value = result.data
      if (value.payRunId !== run.id || value.clientId !== run.clientId || value.payPeriod !== run.payPeriod) {
        setMessage('Preview scope differs from the selected run. Close and reopen the correct run.'); return
      }
      const adjustmentError = selectedAdjustmentPreviewError(value)
      if (adjustmentError) { setMessage(adjustmentError); return }
      setPreview(value)
    } finally { pending.current = false; setBusy(false) }
  }
  const process = async () => {
    if (pending.current || !confirmed || !preview?.canProcess || !preview.fingerprint) return
    pending.current = true; setBusy(true); setConfirmed(false); setMessage('')
    // Consume the local preview before sending. Any outcome requires a fresh review.
    setPreview(null)
    try {
      const result = await processSelectedPayRun(run.id, {
        clientId: run.clientId, payPeriod: run.payPeriod, expectedFingerprint: preview.fingerprint, confirmed: true,
      })
      if (!result.ok || !result.data) {
        setMessage(`${result.error || 'Processing outcome unavailable.'} Refresh run state before any explicit retry; existing attempts are retained.`)
        return
      }
      setMessage(`Run ${result.data.id}: ${result.data.status}. Review its component results and diagnostics. Nothing was submitted, paid or sent.`)
      await onChanged()
    } finally { pending.current = false; setBusy(false) }
  }
  if (!permitted || run.runType !== 'Regular') return null
  return <>
    <Button onClick={() => { setOpen(true); void refresh() }}>Review selected draft processing</Button>
    <Drawer title={<div>Process selected payroll draft<div style={{ fontSize: 12, fontWeight: 400, marginTop: 4, overflowWrap: 'anywhere' }}>{run.clientName || `Client ${run.clientId}`} · {run.payPeriod} · Run {run.id} · {run.runCode}</div></div>} open={open} width="min(640px, 100vw)" onClose={() => { if (!pending.current) { setOpen(false); setPreview(null); setConfirmed(false); setMessage('') } }}
      closable={!busy} maskClosable={!busy} keyboard={!busy}
      footer={<div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
        <Button disabled={busy} onClick={() => { setOpen(false); setPreview(null); setConfirmed(false); setMessage('') }}>Cancel</Button>
        <Button disabled={busy} onClick={() => void refresh()}>Refresh run state</Button>
        <Button type="primary" loading={busy} disabled={busy || !confirmed || !preview?.canProcess} onClick={() => void process()}>Process only this run</Button>
      </div>}>
      <Alert type="warning" showIcon message="Draft calculation is a database write" description="This action saves this exact run's calculation or failure diagnostics. It does not submit, approve, pay, publish or send payslips. Existing payroll history is retained." />
      {message && <p role="status">{message}</p>}
      {busy && <p role="status">Checking or processing the selected run…</p>}
      {preview && <>
        <p>Current state: <b>{preview.status}</b> · Explicit employee selection: <b>{preview.employeeCount}</b></p>
        <DataTable title="Reviewed employee selection" rows={preview.employees} getRowId={employee => employee.employeeId}
          columns={[{ key: 'code', label: 'Employee code', value: employee => employee.employeeCode }, { key: 'name', label: 'Employee name', value: employee => employee.employeeName }]}
          pageSizeOptions={[10, 25, 50]} emptyText="No eligible employees in this selected client and period." />
        {!!preview.adjustments?.length && <DataTable title="Reviewed source adjustments" rows={preview.adjustments} getRowId={row => row.adjustmentId} pageSizeOptions={[10, 25, 50]} columns={[
          { key: 'adjustmentId', label: 'Adjustment ID' },
          { key: 'employeeId', label: 'Employee', value: row => preview.employees.find(employee => employee.employeeId === row.employeeId)?.employeeCode || row.employeeId },
          { key: 'componentCode', label: 'Component' }, { key: 'category', label: 'Category' },
          { key: 'amount', label: 'Amount', render: row => `Rs ${row.amount.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` },
          { key: 'taxable', label: 'Taxable', value: row => row.taxable ? 'Yes' : 'No' },
          { key: 'sourceBatchId', label: 'Saved Excel batch' }, { key: 'sourceImportId', label: 'Source Apply receipt' }, { key: 'sourceRow', label: 'Source row' },
          { key: 'sourceHash', label: 'Source fingerprint', render: row => <abbr title={row.sourceHash}>{row.sourceHash.slice(0, 12)}</abbr> },
        ]} />}
        {preview.effects.map(effect => <p key={effect}>{effect}</p>)}
        {preview.blockers.length > 0 && <Alert type="error" showIcon message="Resolve these blockers before processing" description={<ul>{preview.blockers.map(reason => <li key={reason}>{reason}</li>)}</ul>} />}
        {preview.canProcess && <Checkbox checked={confirmed} onChange={event => setConfirmed(event.target.checked)}>{preview.adjustments?.length ? 'I reviewed this client, period, run, employee scope and listed source adjustments, and confirm saving only this draft calculation.' : 'I reviewed this client, period, run and explicit employee scope, and confirm saving only this draft calculation.'}</Checkbox>}
      </>}
    </Drawer>
  </>
}
