import { useEffect, useMemo, useState } from 'react'
import DataTable from './components/DataTable'
import { getPayRun, getPayRuns, recordPayRunPayments } from './services/payrollService'
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

  const filtered = runs.filter(run => `${run.clientName} ${run.payPeriod} ${run.status}`.toLowerCase().includes(query.toLowerCase()))
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

  return (
    <section className="pay-runs">
      <div className="pay-run-intro"><div><span className="eyebrow purple">Payroll</span><h3>Pay history</h3><p>Select a run to review payment status and record disbursement.</p></div><input placeholder="Filter client, period, status..." value={query} onChange={event => setQuery(event.target.value)} /></div>
      <section className="history-grid">{filtered.map(run => <button type="button" className={`run-item ${selected?.id === run.id ? 'active' : ''}`} onClick={() => void open(run)} key={run.id}><strong>{run.clientName}</strong><span>{run.payPeriod} - {run.employeeCount} employees - {run.status}</span><b>{money(run.netPay)}</b></button>)}</section>
      {!filtered.length && <p className="empty">No payroll runs found.</p>}
      {selected && <section className="payment-workspace">
        <header><div><span className="eyebrow purple">{selected.clientName} - {selected.payPeriod}</span><h3>Payment recording</h3><p>{selected.status} - {unpaid.length} employee{unpaid.length === 1 ? '' : 's'} unpaid</p></div><button type="button" className="secondary" onClick={() => setSelected(null)}>Close</button></header>
        {message && <p className="form-warning">{message}</p>}
        {canRecord ? <section className="payment-panel"><div><b>Record payment</b><span>All unpaid employees are selected by default. Deselect anyone whose payment will be made later.</span></div><label><span>Payment date</span><input type="date" value={paymentDate} onChange={event => setPaymentDate(event.target.value)} /></label><strong>{money(paymentTotal)}</strong><button type="button" disabled={busy || !selectedIds.length || paymentTotal <= 0} onClick={() => void record()}>{busy ? 'Recording...' : `Mark ${selectedIds.length} as paid`}</button>{paymentTotal <= 0 && <p className="payment-warning">No positive net payment is selected. Review the payroll calculation before recording payment.</p>}</section> : <p className="payment-warning">Payments can be recorded only after the run is approved.</p>}
        <DataTable rows={selected.employees.filter(employee => !employee.isSkipped)} getRowId={row => row.employeeId} exportFileName={`payment-status-${selected.payPeriod}`} columns={[
          { key: 'select', label: '', filterable: false, sortable: false, render: employee => canRecord && employee.paymentStatus !== 'Paid' ? <input type="checkbox" checked={selectedIds.includes(employee.employeeId)} onChange={() => toggle(employee.employeeId)} /> : '' },
          { key: 'employee', label: 'Employee', value: employee => employee.employeeName, render: employee => <>{employee.employeeName}<small>{employee.employeeCode}</small></> },
          { key: 'department', label: 'Department' },
          { key: 'netPay', label: 'Net pay', value: employee => money(employee.netPay) },
          { key: 'paymentStatus', label: 'Payment status' },
          { key: 'paymentDateText', label: 'Payment date', value: employee => employee.paymentDate ? new Date(employee.paymentDate).toLocaleDateString('en-IN') : '-' }
        ]} />
      </section>}
    </section>
  )
}
