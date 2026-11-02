import { useEffect, useState } from 'react'
import { Alert, Button, Collapse, Input, Modal, Select, Spin, Tag } from 'antd'
import { simulateSalary, simulationEmployees, type SimulationEmployee, type SimulationResult } from '../services/payrollCalculationService'
import { employeeSimulationInput } from '../utils/employeeSimulationInput'
import type { PayrollSimulationProps } from './PayrollSimulation'

const amount = (value: number | null) => value === null ? '-' : value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
export default function EmployeePayrollSimulation({ template, components, clients, fixedClientId, onClose, onTemplateMode }: PayrollSimulationProps & { onTemplateMode: () => void }) {
  const ids = String(template.clientId || '').split(/[;,|]/).map(value => Number(value.split(':')[0])).filter(value => value > 0)
  const [clientId, setClientId] = useState<number | undefined>(fixedClientId || (ids.length === 1 ? ids[0] : undefined))
  const [employeeId, setEmployeeId] = useState<number>(), [search, setSearch] = useState(''), [employees, setEmployees] = useState<SimulationEmployee[]>([]), [searchError, setSearchError] = useState(''), [searching, setSearching] = useState(false)
  const [payPeriod, setPayPeriod] = useState(() => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` })
  const [payrollDays, setPayrollDays] = useState(''), [presentDays, setPresentDays] = useState(''), [payableDays, setPayableDays] = useState(''), [annualCtc, setAnnualCtc] = useState('')
  const [retry, setRetry] = useState(0), [state, setState] = useState<{ key: string; result: SimulationResult | null; error: string }>({ key: '', result: null, error: '' })
  const input = employeeSimulationInput({ clientId, employeeId, payPeriod, payrollDays, presentDays, payableDays, annualCtc })
  const key = JSON.stringify({ input, template, components, retry })
  const valid = input !== null, current = valid && state.key === key, result = current ? state.result : null
  useEffect(() => {
    if (!clientId) return
    let cancelled = false
    const timer = window.setTimeout(() => {
      setSearching(true); setSearchError('')
      void simulationEmployees(clientId, search).then(response => {
        if (cancelled) return
        setSearching(false)
        if (response.ok && Array.isArray(response.data)) setEmployees(response.data)
        else { setEmployees([]); setSearchError(response.error || 'Employee search is unavailable.') }
      })
    }, 350)
    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [clientId, search])
  useEffect(() => {
    const snapshot = JSON.parse(key) as { input: typeof input; template: typeof template; components: typeof components }
    if (!snapshot.input) return
    let cancelled = false
    const timer = window.setTimeout(() => {
      void simulateSalary({ ...snapshot.input!, template: snapshot.template, components: snapshot.components }).then(response => {
        if (!cancelled) setState({ key, result: response.ok ? response.data : null, error: response.ok && response.data ? '' : response.error || 'Simulation returned no result.' })
      })
    }, 450)
    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [key])
  return <Modal open title={`Salary simulation: ${template.name}`} width={1200} zIndex={1600} className="payroll-simulation salary-form-controls" onCancel={onClose} footer={<Button onClick={onClose}>Close</Button>}>
    <div className="simulation-run"><Tag>Employee Master salary</Tag><Button onClick={event => { event.currentTarget.blur(); onTemplateMode() }}>Use template inputs</Button></div>
    <p>Select an employee and payroll month. Blank inputs use their saved salary and attendance. Changes recalculate automatically without saving them.</p>
    <div className="payroll-simulation-controls">
      <label className="simulation-field">Client<Select aria-label="Employee simulation client" disabled={!!fixedClientId} value={clientId} options={clients.filter(client => !ids.length || ids.includes(client.id)).map(client => ({ value: client.id, label: client.name }))} onChange={value => { setClientId(value); setEmployeeId(undefined); setEmployees([]); setSearch('') }} /></label>
      <label className="simulation-field">Employee<Select aria-label="Simulation employee" showSearch filterOption={false} disabled={!clientId} value={employeeId} loading={searching} placeholder="Search employee name or code" options={employees.map(employee => ({ value: employee.id, label: `${employee.code} - ${employee.name}` }))} onSearch={setSearch} onChange={setEmployeeId} popupMatchSelectWidth={false} dropdownStyle={{ zIndex: 1700 }} /></label>
      <label className="simulation-field">Payroll month<input aria-label="Employee simulation month" type="month" value={payPeriod} onChange={event => setPayPeriod(event.target.value)} /></label>
      {([['Payroll days', payrollDays, setPayrollDays], ['Payable days', payableDays, setPayableDays], ['Present days', presentDays, setPresentDays], ['Annual CTC', annualCtc, setAnnualCtc]] as const).map(([label, value, set]) => <label className="simulation-field" key={label}>{label}<Input aria-label={`Employee simulation ${label}`} inputMode="decimal" value={value} placeholder="Use saved input" onChange={event => set(event.target.value)} /></label>)}
    </div>
    {searchError && <Alert type="error" message={searchError} />}
    {!employeeId ? <Alert type="info" message="Select an employee assigned to this saved template for the selected month." /> : !valid ? <Alert type="error" message="Use a valid payroll month and non-negative numbers. Payroll days must be 1–31; present/payable days cannot exceed payroll days." /> : !current ? <p role="status"><Spin size="small" /> Calculating with payroll engine…</p> : null}
    {current && state.error && <Alert type="error" showIcon message={state.error} />}
    {result && <section aria-label="Employee payroll simulation result">
      <Alert type={result.complete ? 'success' : 'error'} message={result.complete ? `Calculated: ${result.employee}` : 'Simulation incomplete'} description={result.coverage} />
      {result.issues.map((issue, index) => <Alert key={index} type={issue.severity === 'Error' ? 'error' : issue.severity === 'Warning' ? 'warning' : 'info'} message={`${issue.component}: ${issue.message}`} description={issue.action} />)}
      {result.complete && <div className="simulation-totals">{([['Gross earned', result.gross], ['Deductions', result.deductions], ['Employer benefits', result.benefits], ['Net payable', result.net]] as const).map(([label, value]) => <div key={label}><span>{label}</span><strong>Rs {amount(value)}</strong></div>)}</div>}
      <Collapse><Collapse.Panel key="inputs" header="Resolved salary and attendance inputs"><div className="simulation-inputs">{result.inputs.map(value => <div key={value.name}><strong>{value.name}</strong><span>{value.value ?? 'Missing'} {value.unit}</span><small>{value.source}</small></div>)}</div></Collapse.Panel></Collapse>
      {!!result.lines.length && <div className="simulation-table-scroll" tabIndex={0} role="region" aria-label="Employee component-wise salary calculation"><table className="simulation-table"><thead><tr><th>Component</th><th>Type</th><th>Formula / value</th><th>Monthly</th><th>Earned</th><th>Pro-rata</th></tr></thead><tbody>{result.lines.map((line, index) => <tr key={`${line.code}-${index}`}><td><strong>{line.code}</strong><small>{line.name}</small></td><td>{line.category}</td><td><code>{line.formula || line.source}</code><small>{line.substituted}</small></td><td className="simulation-number">Rs {amount(line.monthly)}</td><td className="simulation-number">Rs {amount(line.earned)}</td><td>{line.proRata ? 'Yes' : 'No'}</td></tr>)}</tbody></table></div>}
    </section>}
    <div className="simulation-run"><Button disabled={!valid || !current} onClick={() => setRetry(value => value + 1)}>Recalculate from saved data</Button></div>
  </Modal>
}
