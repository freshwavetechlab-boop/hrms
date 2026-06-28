import { useEffect, useMemo, useState } from 'react'
import { getClients, getPayRun, getPayRuns } from '../services/payrollService'
import type { Client, PayRun, RunEmployee } from '../types/payroll'
import { money } from '../utils/salary'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import './PayslipRegister.css'

export default function PayslipRegister() {
  const [clients, setClients] = useState<Client[]>([])
  const [runs, setRuns] = useState<PayRun[]>([])
  const [clientId, setClientId] = useState(0)
  const [runId, setRunId] = useState(0)
  const [run, setRun] = useState<PayRun | null>(null)
  const [selected, setSelected] = useState<RunEmployee | null>(null)

  useEffect(() => {
    void getClients().then(rows => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(active[0]?.id ?? 0)
    })
    void getPayRuns().then(setRuns)
  }, [])

  const clientRuns = useMemo(() => runs.filter(item => item.clientId === clientId && ['Approved', 'Partially Paid', 'Paid'].includes(item.status)), [runs, clientId])

  useEffect(() => {
    const available = clientRuns.some(item => item.id === runId) ? runId : clientRuns[0]?.id ?? 0
    setRunId(available)
    setRun(null)
    setSelected(null)
    if (available) void getPayRun(available).then(setRun)
  }, [clientRuns, runId])

  const download = () => {
    if (!run || !selected) return
    const deductions = selected.statutoryDeductions + selected.oneTimeDeductions
    const body = `<!doctype html><html><head><meta charset="utf-8"><title>Payslip ${run.payPeriod}</title><style>body{font-family:Arial;color:#172136;max-width:760px;margin:36px auto}header{border-bottom:2px solid #6546e8;padding-bottom:14px}h1{margin:0;font-size:24px}small{color:#64748b}.grid{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin:22px 0}.box{padding:12px;border:1px solid #e5e8f0;border-radius:8px}.label{display:block;color:#64748b;font-size:12px}.amount{font-size:19px;font-weight:bold}footer{margin-top:24px;color:#64748b;font-size:12px}</style></head><body><header><h1>${run.clientName} - Payslip</h1><small>${run.payPeriod} - ${selected.employeeName} (${selected.employeeCode})</small></header><section class="grid"><div class="box"><span class="label">Gross pay</span><span class="amount">${money(selected.grossPay)}</span></div><div class="box"><span class="label">Deductions</span><span class="amount">${money(deductions)}</span></div><div class="box"><span class="label">Net pay</span><span class="amount">${money(selected.netPay)}</span></div><div class="box"><span class="label">Payment status</span><span class="amount">${selected.paymentStatus}</span></div></section><footer>This is a system-generated payslip record.</footer></body></html>`
    const link = document.createElement('a')
    link.href = URL.createObjectURL(new Blob([body], { type: 'text/html' }))
    link.download = `payslip-${selected.employeeCode}-${run.payPeriod}.html`
    link.click()
    URL.revokeObjectURL(link.href)
  }

  return (
    <section className="payslip-register">
      <section className="card report-workspace">
        <header><div><span className="eyebrow purple">Payroll reports</span><h3>Payslip Register</h3><p>Published payroll results by employee. Preview or download an individual payslip record.</p></div></header>
        <div className="payslip-filters">
          <label><span>Client</span><SearchSelect value={clientId} onChange={value => setClientId(Number(value))} options={clients.map(client => ({ value: client.id, label: client.name }))} /></label>
          <label><span>Pay period</span><SearchSelect value={runId} onChange={value => setRunId(Number(value))} options={selectOptions(clientRuns.map(item => ({ value: item.id, label: `${item.payPeriod} - ${item.status}` })), 'Select approved pay run', 0)} /></label>
        </div>
      </section>
      {run && <section className="card payslip-list"><DataTable rows={run.employees.filter(employee => !employee.isSkipped)} getRowId={row => row.employeeId} exportFileName={`payslip-register-${run.payPeriod}`} columns={[
        { key: 'employee', label: 'Employee', value: row => row.employeeName, render: row => <>{row.employeeName}<small>{row.employeeCode}</small></> },
        { key: 'department', label: 'Department' },
        { key: 'grossPay', label: 'Gross', value: row => money(row.grossPay) },
        { key: 'deductions', label: 'Deductions', value: row => money(row.statutoryDeductions + row.oneTimeDeductions) },
        { key: 'netPay', label: 'Net pay', value: row => money(row.netPay) },
        { key: 'paymentStatus', label: 'Payment' }
      ]} actions={row => <button type="button" onClick={() => setSelected(row)}>Preview</button>} /></section>}
      {!run && <section className="card report-empty"><p>No approved pay run is available for this client.</p></section>}
      {selected && run && <div className="payslip-modal-backdrop" onClick={() => setSelected(null)}><section className="payslip-preview-panel" role="dialog" aria-modal="true" aria-label="Payslip preview" onClick={event => event.stopPropagation()}><header><div><span className="eyebrow purple">{run.payPeriod}</span><h3>{selected.employeeName}</h3><p>{selected.employeeCode} - {selected.department}</p></div><button type="button" className="payslip-close" onClick={() => setSelected(null)}>x</button></header><div className="payslip-totals"><span>Gross <b>{money(selected.grossPay)}</b></span><span>Deductions <b>{money(selected.statutoryDeductions + selected.oneTimeDeductions)}</b></span><span>Net pay <b>{money(selected.netPay)}</b></span></div><footer><small>Payment status: <b>{selected.paymentStatus}</b></small><button type="button" onClick={download}>Download payslip</button></footer></section></div>}
    </section>
  )
}
