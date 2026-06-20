import { useEffect, useMemo, useState } from 'react'
import { createPayRun, exportPayRunUrl, getClients, getEmployees, getPayRun, getPayRuns, runPayRunAction, updatePayRunEmployee } from '../services/payrollService'
import type { Client, Employee, PayRun, RunEmployee } from '../types/payroll'
import { money, percent } from '../utils/salary'

const currentPeriod = new Date().toISOString().slice(0, 7)

export default function PayRunsPanel() {
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [runs, setRuns] = useState<PayRun[]>([])
  const [selected, setSelected] = useState<PayRun | null>(null), [clientId, setClientId] = useState(0), [period, setPeriod] = useState(currentPeriod), [workingDays, setWorkingDays] = useState(30)
  const [includedIds, setIncludedIds] = useState<number[]>([]), [busy, setBusy] = useState(false), [message, setMessage] = useState('Select client, verify employees, then run payroll.')

  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [clientId, employees])
  const includedCount = includedIds.length
  const estimatedMonthlyCost = clientEmployees.filter(employee => includedIds.includes(employee.id)).reduce((sum, employee) => sum + Number(employee.annualCtc || 0) / 12, 0)
  const varianceEmployees = selected?.employees.filter(employee => !employee.isSkipped && Math.abs(employee.netPayVariance || 0) > 0) ?? []
  const materialVarianceCount = varianceEmployees.filter(employee => Math.abs(employee.variancePercent || 0) >= 10 || Math.abs(employee.netPayVariance || 0) >= 5000).length

  const load = async () => {
    const [clientRows, employeeRows, runRows] = await Promise.all([getClients(), getEmployees(), getPayRuns()])
    setClients(clientRows)
    if (!clientId && clientRows[0]) setClientId(clientRows[0].id)
    setEmployees(employeeRows)
    setRuns(runRows)
  }

  const open = async (id: number) => {
    const payRun = await getPayRun(id)
    if (payRun) setSelected(payRun)
  }

  useEffect(() => { void load() }, [])
  useEffect(() => { setIncludedIds(clientEmployees.map(employee => employee.id)) }, [clientEmployees])

  const create = async () => {
    setBusy(true)
    const excludedEmployeeIds = clientEmployees.filter(employee => !includedIds.includes(employee.id)).map(employee => employee.id)
    const response = await createPayRun({ clientId, payPeriod: period, payDate: `${period}-28`, totalWorkingDays: workingDays, excludedEmployeeIds })
    if (response.ok && response.data) {
      setSelected(response.data)
      setMessage('Draft payroll prepared. Review variances before locking.')
      await load()
    } else {
      const existing = runs.find(run => run.clientId === clientId && run.payPeriod === period)
      if (existing) {
        await open(existing.id)
        setMessage('Existing draft opened for this client and period.')
      } else setMessage('Payroll could not be created. Check client, period and roster.')
    }
    setBusy(false)
  }

  const updateEmployee = async (employee: RunEmployee, change: Partial<RunEmployee>) => {
    if (!selected || selected.status !== 'Draft') return
    await updatePayRunEmployee(selected.id, { ...employee, ...change })
    await open(selected.id)
    await load()
  }

  const action = async (path: string, success: string) => {
    if (!selected) return
    setBusy(true)
    const response = await runPayRunAction(selected.id, path)
    if (response.ok && response.data) {
      setSelected(response.data)
      setMessage(success)
      await load()
    }
    setBusy(false)
  }

  return <section className="pay-runs payroll-cockpit">
    <div className="pay-run-intro"><div><span className="eyebrow purple">Payroll Command Center</span><h3>Run payroll with review controls</h3><p>{message}</p></div><span className={`status-chip ${selected?.status?.toLowerCase().replace(/\s+/g, '-') || ''}`}>{selected?.status || 'Setup'}</span></div>
    <section className="payroll-steps">
      {['Select client', 'Verify employees', 'Prepare draft', 'Review variance', 'Lock approval'].map((step, index) => <div className={index <= (selected ? selected.status === 'Draft' ? 3 : 4 : includedCount ? 1 : 0) ? 'active' : ''} key={step}><b>{index + 1}</b><span>{step}</span></div>)}
    </section>
    <section className="payroll-command-grid">
      <div className="card payroll-control-panel"><header><i className="blue">1</i><div><h3>Run setup</h3><p>Choose client, period and statutory working days.</p></div></header><div className="pay-run-form enterprise"><label>Client<select value={clientId} onChange={event => setClientId(Number(event.target.value))}>{clients.filter(client => client.isActive).map(client => <option value={client.id} key={client.id}>{client.name}</option>)}</select></label><label>Pay period<input type="month" value={period} onChange={event => setPeriod(event.target.value)} /></label><label>Working days<input type="number" min="1" max="31" value={workingDays} onChange={event => setWorkingDays(Number(event.target.value))} /></label><button onClick={() => void create()} disabled={busy || !clientId || includedCount === 0}>{busy ? 'Preparing...' : 'Run payroll draft'}</button></div><div className="payroll-kpis"><div><span>Employees selected</span><strong>{includedCount}/{clientEmployees.length}</strong></div><div><span>Estimated monthly cost</span><strong>{money(estimatedMonthlyCost)}</strong></div><div><span>Current draft net</span><strong>{money(selected?.netPay)}</strong></div></div></div>
      <div className="card payroll-roster"><header><i className="blue">2</i><div><h3>Employee roster</h3><p>Deselect employees before payroll is calculated.</p></div></header><div className="roster-actions"><button type="button" onClick={() => setIncludedIds(clientEmployees.map(employee => employee.id))}>Select all</button><button type="button" onClick={() => setIncludedIds([])}>Clear</button></div><div className="roster-list">{clientEmployees.map(employee => <label className={includedIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={includedIds.includes(employee.id)} onChange={event => setIncludedIds(ids => event.target.checked ? [...ids, employee.id] : ids.filter(id => id !== employee.id))} /><span><strong>{employee.firstName} {employee.lastName}</strong><small>{employee.employeeCode} / {employee.department || 'No department'} / {money(Number(employee.annualCtc || 0) / 12)}</small></span></label>)}</div>{!clientEmployees.length && <p className="empty">No active employees found for this client.</p>}</div>
    </section>
    {selected && <section className="card pay-run-details enterprise-review"><header><i className="blue">3</i><div><h3>{selected.clientName} / {selected.payPeriod}</h3><p>{selected.status} / {selected.totalWorkingDays} working days / {selected.employeeCount} included employees</p></div><span className={`status-chip ${selected.status.toLowerCase().replace(/\s+/g, '-')}`}>{selected.status}</span></header><div className="pay-run-summary"><div><span>Payroll cost</span><strong>{money(selected.payrollCost)}</strong></div><div><span>Net payable</span><strong>{money(selected.netPay)}</strong></div><div><span>Variance alerts</span><strong>{materialVarianceCount}</strong></div></div><div className="variance-strip">{varianceEmployees.slice(0, 4).map(employee => <article className={Math.abs(employee.variancePercent || 0) >= 10 ? 'risk' : ''} key={employee.id}><strong>{employee.employeeName}</strong><span>{money(employee.previousNetPay)} to {money(employee.netPay)}</span><b>{percent(employee.variancePercent)}</b></article>)}{!varianceEmployees.length && <article><strong>No variance detected</strong><span>Previous payroll comparison is clean.</span><b>0%</b></article>}</div><div className="pay-run-table"><table><thead><tr><th>Include</th><th>Employee</th><th>Present</th><th>Gross</th><th>Deductions</th><th>Prev net</th><th>Net</th><th>Variance</th><th>Review note</th></tr></thead><tbody>{selected.employees.map(employee => <tr className={employee.isSkipped ? 'skipped' : Math.abs(employee.variancePercent || 0) >= 10 ? 'variance' : ''} key={employee.id}><td><input type="checkbox" checked={!employee.isSkipped} disabled={selected.status !== 'Draft'} onChange={event => void updateEmployee(employee, { isSkipped: !event.target.checked })} /></td><td>{employee.employeeName}<small>{employee.employeeCode} / {employee.department || 'No department'}</small></td><td><input type="number" value={employee.presentDays} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void updateEmployee(employee, { presentDays: Number(event.target.value) })} /></td><td>{money(employee.grossPay)}</td><td>{money(employee.statutoryDeductions + employee.oneTimeDeductions)}</td><td>{money(employee.previousNetPay)}</td><td><strong>{money(employee.netPay)}</strong></td><td><span className={Math.abs(employee.variancePercent || 0) >= 10 ? 'variance-badge risk' : 'variance-badge'}>{percent(employee.variancePercent)}</span></td><td>{employee.varianceReason || 'Ready'}</td></tr>)}</tbody></table></div><div className="pay-run-actions"><button type="button" disabled={busy || selected.status !== 'Draft'} onClick={() => void action('submit', 'Payroll locked and sent for approval.')}>Lock and send for approval</button><button type="button" disabled={busy || selected.status !== 'Pending Approval'} onClick={() => void action('approve', 'Payroll approved.')}>Approve payroll</button><button type="button" className="secondary" disabled={busy || !['Approved', 'Pending Approval'].includes(selected.status)} onClick={() => void action('recall', 'Payroll recalled to draft.')}>Recall</button><a className="secondary" href={exportPayRunUrl(selected.id)}>Export</a></div></section>}
  </section>
}
