import { useRef, useState, type ChangeEvent } from 'react'
import { Alert, Button, Checkbox, Modal } from 'antd'
import type { PayRun } from '../types/payroll'
import { postJson } from '../services/apiClient'
import { prepareDraftEmployerCosts, type DraftEmployerCost } from '../utils/draftEmployerCosts'

export default function DraftEmployerCosts({ run, onChanged }: { run: PayRun; onChanged: () => Promise<void> }) {
  const input = useRef<HTMLInputElement>(null)
  const [rows, setRows] = useState<DraftEmployerCost[] | null>(null), [confirmed, setConfirmed] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('')
  const read = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]; event.target.value = ''; if (!file) return
    setConfirmed(false); setError(''); setMessage('')
    try { setRows(prepareDraftEmployerCosts(await file.text(), run.employees)) }
    catch (failure) { setRows(null); setError(failure instanceof Error ? failure.message : 'Review the employer cost CSV.') }
  }
  const apply = async () => {
    if (!rows || !confirmed || busy) return
    setBusy(true); setError('')
    try {
      const result = await postJson(`/api/pay-runs/${run.id}/employer-costs`, { clientId: run.clientId, payPeriod: run.payPeriod, expectedNetPay: run.netPay, expectedPayrollCost: run.payrollCost, confirmed: true, rows }, null as { inserted: number; unchanged: number; employerCostAdded: number; netPay: number } | null, { toast: false, timeoutMs: 180000 })
      if (!result.ok || !result.data || !Number.isInteger(result.data.inserted) || !Number.isInteger(result.data.unchanged) || result.data.inserted + result.data.unchanged !== rows.length || result.data.netPay !== run.netPay) throw Error(result.error || 'Employer costs could not be confirmed. Refresh this draft before retrying.')
      setMessage(`${result.data.inserted} employer-cost entries added; ${result.data.unchanged} already present. Employee net pay is unchanged.`)
      setRows(null); await onChanged()
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Employer cost import failed.') }
    finally { setBusy(false) }
  }
  return <section aria-label="Draft employer cost import">
    <input ref={input} aria-label="Draft employer costs CSV" hidden type="file" accept=".csv,text/csv" disabled={busy} onChange={read} />
    <Button disabled={busy} onClick={event => { event.currentTarget.blur(); input.current?.click() }}>Import period employer costs</Button>
    {error && <Alert type="error" showIcon message={error} />}{message && <p role="status">{message}</p>}
    <Modal open={rows !== null} title={`Employer costs · ${run.clientName} · ${run.payPeriod} · Draft ${run.id}`} onCancel={() => { if (!busy) setRows(null) }} closable={!busy} maskClosable={!busy} footer={<Button type="primary" loading={busy} disabled={!confirmed} onClick={() => void apply()}>Apply employer costs to this draft</Button>}>
      <Alert type="info" showIcon message="Employer contribution entries only" description="These period costs stay outside employee earnings, deductions and net pay. Existing source references are checked to prevent duplicate additions. Salary Master values stay unchanged." />
      <p>{rows?.length || 0} entries · {new Set(rows?.map(row => row.employeeId)).size} employees · ₹{rows?.reduce((sum, row) => sum + row.amount, 0).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</p>
      <Checkbox checked={confirmed} onChange={event => setConfirmed(event.target.checked)}>I reviewed these source employer costs for this exact client, month and draft.</Checkbox>
    </Modal>
  </section>
}
