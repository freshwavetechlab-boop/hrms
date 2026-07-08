import { useEffect, useMemo, useState } from 'react'
import DataTable from './components/DataTable'
import { deletePayRun, getPayRun, getPayRuns, recordPayRunPayments } from './services/payrollService'
import type { PayRun } from './types/payroll'
import { money } from './utils/salary'
import './PayHistory.css'

export default function PayHistory() {
  const [runs, setRuns] = useState<PayRun[]>([])
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<PayRun | null>(null)
  const [selectedIds, setSelectedIds] = useState<number[]>([])
  const [paymentDate, setPaymentDate] = useState(new Date().toISOString().slice(0, 10))
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const load = () => void getPayRuns().then(setRuns)

  useEffect(load, [])

  const open = async (run: PayRun) => {
    const detail = await getPayRun(run.id)
    if (!detail) return
    setSelected(detail)
    setSelectedIds(detail.employees.filter(employee => !employee.isSkipped && employee.paymentStatus !== 'Paid').map(employee => employee.employeeId))
    setMessage('')
  }

  const unpaid = selected?.employees.filter(employee => !employee.isSkipped && employee.paymentStatus !== 'Paid') ?? []
  const paymentTotal = useMemo(() => unpaid.filter(employee => selectedIds.includes(employee.employeeId)).reduce((sum, employee) => sum + employee.netPay, 0), [unpaid, selectedIds])
  const canRecord = selected?.status === 'Approved' || selected?.status === 'Partially Paid'
  const toggle = (id: number) => setSelectedIds(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id])

  const record = async () => {
    if (!selected || !selectedIds.length || paymentTotal <= 0) return
    setBusy(true)
    const response = await recordPayRunPayments(selected.id, { employeeIds: selectedIds, paymentDate })
    setBusy(false)
    if (!response.ok || !response.data) {
      setMessage('Unable to record payment. Check your payment permission and selected employees.')
      return
    }
    setSelected(response.data)
    setSelectedIds(response.data.employees.filter(employee => !employee.isSkipped && employee.paymentStatus !== 'Paid').map(employee => employee.employeeId))
    setMessage('Payment recorded successfully.')
    load()
  }

  const hardDelete = async () => {
    if (!selected || !window.confirm(`Hard delete payroll run for ${selected.clientName} - ${selected.payPeriod}?`)) return
    setBusy(true)
    const response = await deletePayRun(selected.id)
    setBusy(false)
    if (!response.ok) {
      setMessage(response.error || 'Unable to hard delete payroll run.')
      return
    }
    setSelected(null)
    setSelectedIds([])
    setMessage('Payroll run hard deleted.')
    load()
  }

  const searchText = query.trim().toLowerCase()
  const matches = (run: PayRun) => [
    run.clientName,
    run.payPeriod,
    run.status,
    run.runCode,
    run.runType,
    run.runName,
    run.reason,
    run.employeeCount,
    run.payrollCost,
    run.netPay
  ].join(' ').toLowerCase().includes(searchText)
  const visibleRuns = searchText ? runs.filter(matches) : runs

  if (selected) return (
    <section className="pay-runs pay-history-detail-page">
      <div className="pay-history-detail-head">
        <button type="button" className="secondary" onClick={() => setSelected(null)}>Back to history</button>
        <div><span className="eyebrow purple">Payroll / Pay history</span><h3>{selected.clientName} - {selected.payPeriod}</h3><p>{selected.runName || selected.runType} / {selected.status} / {selected.employeeCount.toLocaleString('en-IN')} employees</p></div>
        <span className="adjustment-actions"><button type="button" className="danger" disabled={busy} onClick={() => void hardDelete()}>Hard delete</button></span>
      </div>
      <section className="pay-history-metrics">
        <article><span>Payroll cost</span><b>{money(selected.payrollCost)}</b></article>
        <article><span>Net payable</span><b>{money(selected.netPay)}</b></article>
        <article><span>Unpaid employees</span><b>{unpaid.length.toLocaleString('en-IN')}</b></article>
        <article><span>Status</span><b className={`pay-history-status ${selected.status.toLowerCase().replace(/\s+/g, '-')}`}>{selected.status}</b></article>
      </section>
      {message && <p className="form-warning">{message}</p>}
      {canRecord ? <section className="payment-panel"><div><b>Record payment</b><span>All unpaid employees are selected by default. Deselect anyone whose payment will be made later.</span></div><label><span>Payment date</span><input type="date" value={paymentDate} onChange={event => setPaymentDate(event.target.value)} /></label><strong>{money(paymentTotal)}</strong><button type="button" disabled={busy || !selectedIds.length || paymentTotal <= 0} onClick={() => void record()}>{busy ? 'Recording...' : `Mark ${selectedIds.length} as paid`}</button>{paymentTotal <= 0 && <p className="payment-warning">No positive net payment is selected. Review the payroll calculation before recording payment.</p>}</section> : <p className="payment-warning">Payments can be recorded only after the run is approved.</p>}
      <section className="payment-workspace full-page">
        <header><div><span className="eyebrow purple">Employee payment status</span><h3>Run details</h3><p>{selected.employees.filter(employee => !employee.isSkipped).length.toLocaleString('en-IN')} employee records</p></div></header>
        <DataTable rows={selected.employees.filter(employee => !employee.isSkipped)} getRowId={row => row.employeeId} exportFileName={`payment-status-${selected.payPeriod}`} columns={[
          { key: 'select', label: '', filterable: false, sortable: false, render: employee => canRecord && employee.paymentStatus !== 'Paid' ? <input type="checkbox" checked={selectedIds.includes(employee.employeeId)} onChange={() => toggle(employee.employeeId)} /> : '' },
          { key: 'employee', label: 'Employee', value: employee => employee.employeeName, render: employee => <>{employee.employeeName}<small>{employee.employeeCode}</small></> },
          { key: 'department', label: 'Department' },
          { key: 'netPay', label: 'Net pay', value: employee => money(employee.netPay) },
          { key: 'paymentStatus', label: 'Payment status' },
          { key: 'paymentDateText', label: 'Payment date', value: employee => employee.paymentDate ? new Date(employee.paymentDate).toLocaleDateString('en-IN') : '-' }
        ]} />
      </section>
    </section>
  )

  return (
    <section className="pay-runs">
      <div className="pay-run-intro pay-history-intro"><div><span className="eyebrow purple">Payroll</span><h3>Pay history</h3><p>Select a run to review payment status and record disbursement.</p></div><label className="pay-history-search"><span>Search pay runs</span><input placeholder="Client, period, run, status, amount..." value={query} onChange={event => setQuery(event.target.value)} /></label></div>
      <section className="pay-history-table-wrap">
        <div className="pay-history-table-caption"><strong>Pay run history</strong><span>{visibleRuns.length} of {runs.length} runs</span>{query && <button type="button" onClick={() => setQuery('')}>Clear</button>}</div>
        <table className="pay-history-table">
          <thead><tr><th>Client</th><th>Pay period</th><th>Run</th><th>Status</th><th>Employees</th><th>Payroll cost</th><th>Net pay</th></tr></thead>
          <tbody>
            {visibleRuns.map(run => <tr key={run.id} onClick={() => void open(run)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') void open(run) }} tabIndex={0} role="button">
              <td><strong>{run.clientName}</strong><small>{run.runCode || `Run #${run.id}`}</small></td>
              <td>{run.payPeriod}</td>
              <td><span>{run.runName || run.runType || 'Payroll run'}</span><small>{run.reason || run.runType}</small></td>
              <td><b className={`pay-history-status ${run.status.toLowerCase().replace(/\s+/g, '-')}`}>{run.status}</b></td>
              <td>{run.employeeCount.toLocaleString('en-IN')}</td>
              <td>{money(run.payrollCost)}</td>
              <td><strong>{money(run.netPay)}</strong></td>
            </tr>)}
          </tbody>
        </table>
        {!visibleRuns.length && <p className="empty">No payroll runs found.</p>}
      </section>
    </section>
  )
}
