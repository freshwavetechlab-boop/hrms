import { useState } from 'react'
import { Alert, Button, Drawer, InputNumber } from 'antd'
import type { PayRun, RunEmployee } from '../types/payroll'
import { getPayRun, updatePayRunEmployee } from '../services/payrollService'

export default function DraftPayrollAdjustments({ run, employee, disabled, onChanged }: { run: PayRun; employee: RunEmployee; disabled: boolean; onChanged: () => Promise<void> }) {
  const [open, setOpen] = useState(false), [earnings, setEarnings] = useState(employee.oneTimeEarnings), [deductions, setDeductions] = useState(employee.oneTimeDeductions)
  const [busy, setBusy] = useState(false), [error, setError] = useState('')
  const valid = [earnings, deductions].every(value => Number.isFinite(value) && value >= 0)
  const save = async () => {
    if (busy || !valid) return
    setBusy(true); setError('')
    try {
      const current = await getPayRun(run.id), row = current?.employees.find(value => value.employeeId === employee.employeeId)
      if (current?.status !== 'Draft' || current.clientId !== run.clientId || current.payPeriod !== run.payPeriod || !row
        || ['presentDays', 'payableDays', 'oneTimeEarnings', 'oneTimeDeductions', 'manualTds', 'detailsJson', 'isSkipped'].some(key => row[key as keyof RunEmployee] !== employee[key as keyof RunEmployee])) throw Error('The saved draft changed. Close and refresh before editing its totals.')
      const response = await updatePayRunEmployee(run.id, { ...employee, oneTimeEarnings: earnings, oneTimeDeductions: deductions })
      if (!response.ok) throw Error(response.error || 'Draft update could not be confirmed. Refresh before retrying.')
      await onChanged(); setOpen(false)
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Draft update failed.') }
    finally { setBusy(false) }
  }
  return <>
    <Button size="small" disabled={disabled || employee.presentDays == null || employee.presentDays !== employee.payableDays || employee.isSkipped} onClick={() => { setEarnings(employee.oneTimeEarnings); setDeductions(employee.oneTimeDeductions); setError(''); setOpen(true) }}>Edit totals</Button>
    {open && <Drawer open title={`Draft adjustments · ${employee.employeeCode}`} width={540} onClose={() => { if (!busy) setOpen(false) }} maskClosable={!busy} keyboard={!busy} closable={!busy} footer={<Button type="primary" loading={busy} disabled={!valid} onClick={() => void save()}>Recalculate this draft employee</Button>}>
      <p>{run.clientName} · {run.payPeriod} · {employee.employeeName}</p>
      <Alert type="info" showIcon message="Review one-time totals" description="Totals include linked approved adjustments. Only the additional difference becomes a manual draft amount. The payroll engine recalculates this employee; employee salary masters stay unchanged. Dated policy snapshots remain protected by the server." />
      <label className="simulation-field">One-time earnings total<InputNumber aria-label="Draft one-time earnings total" min={0} precision={2} value={earnings} disabled={busy} onChange={value => setEarnings(value ?? Number.NaN)} /></label>
      <label className="simulation-field">One-time deductions total<InputNumber aria-label="Draft one-time deductions total" min={0} precision={2} value={deductions} disabled={busy} onChange={value => setDeductions(value ?? Number.NaN)} /></label>
      <p>Recorded present / payable days: {employee.presentDays} / {employee.payableDays}. Existing TDS and linked adjustments are retained.</p>
      {error && <Alert type="error" showIcon message={error} />}
    </Drawer>}
  </>
}
