import { useEffect, useMemo, useState } from 'react'
import { EyeOutlined } from '@ant-design/icons'
import { Button } from 'antd'
import DataTable from './components/DataTable'
import { deletePayRun, getPayRun, getPayRuns, recordPayRunPayments } from './services/payrollService'
import type { PayRun } from './types/payroll'
import { money } from './utils/salary'
import './PayHistory.css'

export default function PayHistory() {
  const [runs, setRuns] = useState<PayRun[]>([])
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
      <div className="pay-run-intro pay-history-intro"><div><span className="eyebrow purple">Run archive</span><p>Select a run to review payment status and record disbursement.</p></div></div>
      <section className="pay-history-table-wrap card">
        <DataTable
          rows={runs}
          title="Pay run history"
          getRowId={run => run.id}
          emptyText="No payroll runs found."
          exportFileName="pay-run-history"
          hideInactiveClear
          actionsWidth={112}
          actions={run => <Button size="small" type="link" icon={<EyeOutlined />} onClick={() => void open(run)}>Open</Button>}
          columns={[
            { key: 'clientName', label: 'Client', width: 210, render: run => <>{run.clientName}<small>{run.runCode || `Run #${run.id}`}</small></> },
            { key: 'payPeriod', label: 'Pay period', width: 130 },
            { key: 'runName', label: 'Run', width: 220, value: run => run.runName || run.runType || 'Payroll run', render: run => <>{run.runName || run.runType || 'Payroll run'}<small>{run.reason || run.runType}</small></> },
            { key: 'status', label: 'Status', width: 145, render: run => <b className={`pay-history-status ${run.status.toLowerCase().replace(/\s+/g, '-')}`}>{run.status}</b> },
            { key: 'employeeCount', label: 'Employees', width: 120, render: run => run.employeeCount.toLocaleString('en-IN') },
            { key: 'payrollCost', label: 'Payroll cost', width: 160, value: run => Number(run.payrollCost || 0), render: run => money(run.payrollCost) },
            { key: 'netPay', label: 'Net pay', width: 160, value: run => Number(run.netPay || 0), render: run => <strong>{money(run.netPay)}</strong> },
          ]}
        />
      </section>
    </section>
  )
}
