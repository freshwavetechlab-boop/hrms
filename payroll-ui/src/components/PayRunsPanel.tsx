import { type ChangeEvent, useEffect, useMemo, useState } from 'react'
import { getMonthlyAttendance } from '../services/leaveAttendanceService'
import { cancelPayrollAdjustment, createPayRun, exportPayRunUrl, getClients, getEmployees, getPayRun, getPayrollAdjustments, getPayRuns, runPayRunAction, savePayrollAdjustment, updatePayRunEmployee } from '../services/payrollService'
import { getSetup } from '../services/settingsService'
import type { Client, Component, Employee, EmployeeMonthlyAttendance, PayRun, PayrollAdjustment, RunEmployee, Setup } from '../types/payroll'
import { setup0 } from '../data/payrollDefaults'
import { money } from '../utils/salary'
import PageTabs from './PageTabs'
import { componentToAdjustmentType, prepareAdjustmentImports, type AdjustmentImportMode } from '../features/payroll/adjustmentImport'

const currentPeriod = new Date().toISOString().slice(0, 7)
type PayrollTab = 'Regular Run' | 'Adjustments' | 'Off-cycle Run'

const adjustment0: PayrollAdjustment = { id: 0, clientId: 0, employeeId: 0, employeeName: '', employeeCode: '', componentId: 0, componentCode: '', componentName: '', adjustmentType: 'Earning', amount: 0, payPeriod: currentPeriod, payRunType: 'Regular', reasonCode: 'Overtime', notes: '', taxable: true, status: 'Approved', payRunId: null }

export default function PayRunsPanel({ mode = 'payrun' }: { mode?: 'payrun' | 'adjustments' }) {
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [runs, setRuns] = useState<PayRun[]>([])
  const [setup, setSetup] = useState<Setup>(setup0), [selected, setSelected] = useState<PayRun | null>(null)
  const [clientId, setClientId] = useState(0), [period, setPeriod] = useState(currentPeriod), [workingDays, setWorkingDays] = useState(30)
  const [includedIds, setIncludedIds] = useState<number[]>([]), [offcycleEmployeeIds, setOffcycleEmployeeIds] = useState<number[]>([]), [offcycleAdjustmentIds, setOffcycleAdjustmentIds] = useState<number[]>([])
  const [adjustments, setAdjustments] = useState<PayrollAdjustment[]>([]), [adjustment, setAdjustment] = useState<PayrollAdjustment>(adjustment0)
  const [tab, setTab] = useState<PayrollTab>(mode === 'adjustments' ? 'Adjustments' : 'Regular Run'), [busy, setBusy] = useState(false), [message, setMessage] = useState('Select client, verify employees, then run payroll.')
  const [attendanceRows, setAttendanceRows] = useState<EmployeeMonthlyAttendance[]>([]), [offcycleName, setOffcycleName] = useState('Off-cycle payment'), [offcycleReason, setOffcycleReason] = useState('Missed employee / reimbursement payment')

  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [clientId, employees])
  const variableComponents = useMemo(() => setup.salaryComponents.filter(isAdjustmentComponent), [setup.salaryComponents])
  const pendingAdjustments = adjustments.filter(item => item.status === 'Approved' && item.clientId === clientId && item.payPeriod === period)
  const regularAdjustments = pendingAdjustments.filter(item => item.payRunType !== 'Off Cycle')
  const offcycleAdjustments = pendingAdjustments.filter(item => item.payRunType === 'Off Cycle')
  const includedCount = includedIds.length
  const estimatedMonthlyCost = clientEmployees.filter(employee => includedIds.includes(employee.id)).reduce((sum, employee) => sum + Number(employee.annualCtc || 0) / 12, 0)
  const varianceEmployees = selected?.employees.filter(employee => !employee.isSkipped && Math.abs(employee.netPayVariance || 0) > 0) ?? []
  const materialVarianceCount = varianceEmployees.filter(employee => Math.abs(employee.variancePercent || 0) >= 10 || Math.abs(employee.netPayVariance || 0) >= 5000).length
  const attendanceIssues = attendanceRows.filter(row => includedIds.includes(row.employeeId) && attendanceIssue(row)).length
  const attendanceReady = includedCount > 0 && attendanceRows.length > 0 && attendanceIssues === 0

  const load = async () => {
    const [clientRows, employeeRows, runRows, setupRow, adjustmentRows] = await Promise.all([getClients(), getEmployees(), getPayRuns(), getSetup(setup0), getPayrollAdjustments()])
    const nextClientId = clientId || clientRows[0]?.id || 0
    setClients(clientRows)
    if (!clientId && nextClientId) setClientId(nextClientId)
    setEmployees(employeeRows)
    setIncludedIds(employeeRows.filter(employee => employee.clientId === nextClientId && employee.isActive).map(employee => employee.id))
    setOffcycleEmployeeIds([])
    setRuns(runRows)
    setSetup({ ...setup0, ...setupRow, salaryComponents: setupRow.salaryComponents?.length ? setupRow.salaryComponents : setup0.salaryComponents })
    setAdjustments(adjustmentRows)
  }

  const open = async (id: number) => {
    const payRun = await getPayRun(id)
    if (payRun) setSelected(payRun)
  }

  useEffect(() => { void Promise.resolve().then(load) }, [])
  useEffect(() => { if (clientId && period) void getMonthlyAttendance(clientId, period).then(setAttendanceRows) }, [clientId, period])

  const changeClient = (id: number) => {
    setClientId(id)
    setIncludedIds(employees.filter(employee => employee.clientId === id && employee.isActive).map(employee => employee.id))
    setOffcycleEmployeeIds([])
    setOffcycleAdjustmentIds([])
  }

  const createRegular = async () => {
    if (!attendanceReady) return setMessage('Attendance is not ready. Open Payroll > Attendance Review and resolve missing/check-value rows first.')
    setBusy(true)
    const excludedEmployeeIds = clientEmployees.filter(employee => !includedIds.includes(employee.id)).map(employee => employee.id)
    const response = await createPayRun({ clientId, payPeriod: period, payDate: `${period}-28`, totalWorkingDays: workingDays, runType: 'Regular', runName: 'Regular payroll', excludedEmployeeIds, adjustmentIds: regularAdjustments.map(item => item.id) })
    await afterCreate(response, 'Draft payroll prepared with approved one-time adjustments.')
  }

  const createOffcycle = async () => {
    setBusy(true)
    const response = await createPayRun({ clientId, payPeriod: period, payDate: `${period}-28`, totalWorkingDays: workingDays, runType: 'Off Cycle', runName: offcycleName, reason: offcycleReason, includedEmployeeIds: offcycleEmployeeIds, adjustmentIds: offcycleAdjustmentIds })
    await afterCreate(response, 'Off-cycle draft prepared for selected employees/payments.')
  }

  const afterCreate = async (response: { ok: boolean; data: PayRun | null }, success: string) => {
    if (response.ok && response.data) {
      setSelected(response.data)
      setMessage(success)
      await load()
    } else {
      const existing = runs.find(run => run.clientId === clientId && run.payPeriod === period && (run.runType || 'Regular') === 'Regular')
      if (existing) { await open(existing.id); setMessage('Existing regular draft opened for this client and period.') }
      else setMessage('Payroll could not be created. Check client, period, selected employees and adjustments.')
    }
    setBusy(false)
  }

  const saveAdjustment = async () => {
    if (!adjustment.employeeId || !adjustment.amount || !adjustment.componentName.trim()) return setMessage('Select employee, component and amount for adjustment.')
    const component = variableComponents.find(item => item.id === adjustment.componentId)
    const selectedEmployee = employees.find(item => item.id === adjustment.employeeId)
    const response = await savePayrollAdjustment({ ...adjustment, clientId, payPeriod: period, employeeName: selectedEmployee ? `${selectedEmployee.firstName} ${selectedEmployee.lastName}`.trim() : '', employeeCode: selectedEmployee?.employeeCode || '', componentCode: component?.code || adjustment.componentCode, componentName: component?.name || adjustment.componentName, adjustmentType: adjustment.adjustmentType, amount: Number(adjustment.amount) })
    if (response.ok) {
      setAdjustment({ ...adjustment0, clientId, payPeriod: period })
      setMessage('Adjustment saved. It will be picked in regular/off-cycle payroll based on Pay Run Type.')
      await load()
    }
  }

  const importAdjustments = async (event: ChangeEvent<HTMLInputElement>, mode: AdjustmentImportMode) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    if (!clientId) return setMessage('Select client before importing adjustments.')
    if (file.size > 1024 * 1024) return setMessage('Import file is too large. Keep CSV under 1 MB.')
    const prepared = prepareAdjustmentImports({ text: await file.text(), mode, employees: clientEmployees, components: variableComponents, clientId, period, seed: adjustment0 })
    if (!prepared.rows.length && !prepared.skipped) return setMessage('CSV has no import rows.')
    let imported = 0, skipped = prepared.skipped
    for (const row of prepared.rows) {
      const response = await savePayrollAdjustment(row)
      response.ok ? imported += 1 : skipped += 1
    }
    setMessage(`${mode === 'arrears' ? 'Arrears' : 'Scheduled earning'} import completed. Imported ${imported}, skipped ${skipped}.${prepared.errors[0] ? ` ${prepared.errors[0]}` : ''}`)
    await load()
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
    if (response.ok && response.data) { setSelected(response.data); setMessage(success); await load() }
    setBusy(false)
  }

  const chooseComponent = (id: number) => {
    const component = variableComponents.find(item => item.id === id)
    if (component) setAdjustment({ ...adjustment, componentId: component.id, componentCode: component.code, componentName: component.name, adjustmentType: componentToAdjustmentType(component), taxable: component.taxable })
  }

  const visibleTabs: readonly PayrollTab[] = mode === 'adjustments' ? ['Adjustments', 'Off-cycle Run'] : ['Regular Run', 'Off-cycle Run']

  return <section className="pay-runs payroll-cockpit">
    <div className="pay-run-intro"><div><span className="eyebrow purple">Payroll Command Center</span><h3>Run payroll with review controls</h3><p>{message}</p></div><span className={`status-chip ${attendanceReady ? 'paid' : 'pending-approval'}`}>{attendanceReady ? 'Attendance ready' : `${attendanceIssues || includedCount} attendance issue${(attendanceIssues || includedCount) === 1 ? '' : 's'}`}</span></div>
    {mode !== 'adjustments' && <PageTabs items={visibleTabs} value={tab} onChange={setTab} label="Payroll operations" />}
    <section className="payroll-command-grid">
      <div className="card payroll-control-panel"><header><i className="blue">1</i><div><h3>Run context</h3><p>Client, period and statutory working days are shared across payroll operations.</p></div></header><div className="pay-run-form enterprise"><label>Client<select value={clientId} onChange={event => changeClient(Number(event.target.value))}>{clients.filter(client => client.isActive).map(client => <option value={client.id} key={client.id}>{client.name}</option>)}</select></label><label>Pay period<input type="month" value={period} onChange={event => setPeriod(event.target.value)} /></label><label>Working days<input type="number" min="1" max="31" value={workingDays} onChange={event => setWorkingDays(Number(event.target.value))} /></label>{tab === 'Regular Run' && <button onClick={() => void createRegular()} disabled={busy || !clientId || includedCount === 0 || !attendanceReady}>{busy ? 'Preparing...' : 'Run regular draft'}</button>}{tab === 'Off-cycle Run' && <button onClick={() => void createOffcycle()} disabled={busy || !clientId || (offcycleEmployeeIds.length === 0 && offcycleAdjustmentIds.length === 0)}>{busy ? 'Preparing...' : 'Create off-cycle draft'}</button>}</div>{!attendanceReady && tab === 'Regular Run' && <p className="payment-warning">Attendance review must be clean before regular payroll can run. Off-cycle can be used for missed/reimbursement-only payments.</p>}<div className="payroll-kpis"><div><span>Regular employees</span><strong>{includedCount}/{clientEmployees.length}</strong></div><div><span>Approved adjustments</span><strong>{pendingAdjustments.length}</strong></div><div><span>Estimated monthly cost</span><strong>{money(estimatedMonthlyCost)}</strong></div><div><span>Current draft net</span><strong>{money(selected?.netPay)}</strong></div></div></div>
      {tab === 'Regular Run' && <RosterCard employees={clientEmployees} includedIds={includedIds} setIncludedIds={setIncludedIds} adjustments={regularAdjustments} />}
      {tab === 'Adjustments' && <><AdjustmentCard adjustment={adjustment} setAdjustment={setAdjustment} employees={clientEmployees} components={variableComponents} adjustments={adjustments.filter(item => item.clientId === clientId && item.payPeriod === period)} chooseComponent={chooseComponent} saveAdjustment={saveAdjustment} edit={row => setAdjustment({ ...row, amount: Number(row.amount) })} cancel={async id => { await cancelPayrollAdjustment(id); await load() }} /><AdjustmentImportPanel importScheduled={event => void importAdjustments(event, 'scheduled')} importArrears={event => void importAdjustments(event, 'arrears')} /></>}
      {tab === 'Off-cycle Run' && <OffcycleCard employees={clientEmployees} selectedIds={offcycleEmployeeIds} setSelectedIds={setOffcycleEmployeeIds} adjustments={offcycleAdjustments} selectedAdjustmentIds={offcycleAdjustmentIds} setSelectedAdjustmentIds={setOffcycleAdjustmentIds} name={offcycleName} setName={setOffcycleName} reason={offcycleReason} setReason={setOffcycleReason} />}
    </section>
    {selected && <PayRunReview selected={selected} busy={busy} updateEmployee={updateEmployee} action={action} materialVarianceCount={materialVarianceCount} />}
  </section>
}

function RosterCard(p: { employees: Employee[]; includedIds: number[]; setIncludedIds: (fn: number[] | ((ids: number[]) => number[])) => void; adjustments: PayrollAdjustment[] }) {
  return <div className="card payroll-roster"><header><i className="blue">2</i><div><h3>Employee roster</h3><p>Deselect employees before payroll is calculated. Approved regular adjustments are auto-included.</p></div></header><div className="roster-actions"><button type="button" onClick={() => p.setIncludedIds(p.employees.map(employee => employee.id))}>Select all</button><button type="button" onClick={() => p.setIncludedIds([])}>Clear</button></div><div className="roster-list">{p.employees.map(employee => <label className={p.includedIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={p.includedIds.includes(employee.id)} onChange={event => p.setIncludedIds(ids => event.target.checked ? [...ids, employee.id] : ids.filter(id => id !== employee.id))} /><span><strong>{employee.firstName} {employee.lastName}</strong><small>{employee.employeeCode} / {employee.department || 'No department'} / {money(Number(employee.annualCtc || 0) / 12)}</small></span></label>)}</div><AdjustmentMini rows={p.adjustments} title="Approved regular adjustments" /></div>
}

function AdjustmentCard(p: { adjustment: PayrollAdjustment; setAdjustment: (row: PayrollAdjustment) => void; employees: Employee[]; components: Component[]; adjustments: PayrollAdjustment[]; chooseComponent: (id: number) => void; saveAdjustment: () => void; edit: (row: PayrollAdjustment) => void; cancel: (id: number) => Promise<void> }) {
  return <div className="card payroll-adjustments"><header><i className="blue">2</i><div><h3>{p.adjustment.id ? 'Edit adjustment entry' : 'Variable earning / adjustment entry'}</h3><p>Use approved one-time entries for overtime, arrears, bonus, reimbursements and recoveries.</p></div></header><div className="adjustment-form"><label>Employee<select value={p.adjustment.employeeId} onChange={event => p.setAdjustment({ ...p.adjustment, employeeId: Number(event.target.value) })}><option value="0">Select employee</option>{p.employees.map(employee => <option value={employee.id} key={employee.id}>{employee.firstName} {employee.lastName} / {employee.employeeCode}</option>)}</select></label><label>Component<select value={p.adjustment.componentId} onChange={event => p.chooseComponent(Number(event.target.value))}><option value="0">Select variable component</option>{p.components.map(component => <option value={component.id} key={component.id}>{component.name} / {component.category}</option>)}</select></label><label>Pay Run Type<select value={p.adjustment.payRunType} onChange={event => p.setAdjustment({ ...p.adjustment, payRunType: event.target.value })}><option>Regular</option><option>Off Cycle</option></select></label><label>Adjustment Type<select value={p.adjustment.adjustmentType} onChange={event => p.setAdjustment({ ...p.adjustment, adjustmentType: event.target.value })}><option>Earning</option><option>Reimbursement</option><option>Deduction</option></select></label><label>Amount<input type="number" min="0" value={p.adjustment.amount || ''} onChange={event => p.setAdjustment({ ...p.adjustment, amount: Number(event.target.value) })} /></label><label>Reason<select value={p.adjustment.reasonCode} onChange={event => p.setAdjustment({ ...p.adjustment, reasonCode: event.target.value })}><option>Overtime</option><option>Arrears</option><option>Bonus/Incentive</option><option>Missed Salary</option><option>Reimbursement</option><option>Recovery/Correction</option></select></label><label className="wide">Notes<input value={p.adjustment.notes} onChange={event => p.setAdjustment({ ...p.adjustment, notes: event.target.value })} placeholder="Business justification, approval reference or reimbursement details" /></label><button type="button" onClick={p.saveAdjustment}>{p.adjustment.id ? 'Update adjustment' : 'Save approved adjustment'}</button></div><AdjustmentMini rows={p.adjustments} title="Adjustment register" edit={p.edit} cancel={p.cancel} /></div>
}

function AdjustmentImportPanel(p: { importScheduled: (event: ChangeEvent<HTMLInputElement>) => void; importArrears: (event: ChangeEvent<HTMLInputElement>) => void }) {
  return <div className="card adjustment-import-panel"><header><i className="blue">3</i><div><h3>Bulk adjustment imports</h3><p>Import rows become approved adjustment entries and are picked by regular or off-cycle payroll based on Pay Run Type.</p></div></header><div className="adjustment-import-grid"><article><h3>Scheduled earnings import</h3><p>Use for planned bonuses, incentives, overtime or one-time reimbursements.</p><small>CSV columns: employeeCode, componentCode, amount, payPeriod, payRunType, reason, notes</small><input type="file" accept=".csv,text/csv" onChange={p.importScheduled} /></article><article><h3>Automatic arrears from revision</h3><p>Use salary-revision differences to generate arrears without manual amount calculation.</p><small>CSV columns: employeeCode, componentCode, oldMonthly, newMonthly, months, payPeriod, payRunType, notes</small><input type="file" accept=".csv,text/csv" onChange={p.importArrears} /></article></div></div>
}

function OffcycleCard(p: { employees: Employee[]; selectedIds: number[]; setSelectedIds: (fn: number[] | ((ids: number[]) => number[])) => void; adjustments: PayrollAdjustment[]; selectedAdjustmentIds: number[]; setSelectedAdjustmentIds: (fn: number[] | ((ids: number[]) => number[])) => void; name: string; setName: (value: string) => void; reason: string; setReason: (value: string) => void }) {
  return <div className="card payroll-roster"><header><i className="blue">2</i><div><h3>Off-cycle run setup</h3><p>Use for missed employees, reimbursement-only payments or approved exceptional payouts outside regular payroll.</p></div></header><div className="offcycle-fields"><label>Run name<input value={p.name} onChange={event => p.setName(event.target.value)} /></label><label>Reason<input value={p.reason} onChange={event => p.setReason(event.target.value)} /></label></div><h3>Employees to pay</h3><div className="roster-list compact-roster">{p.employees.map(employee => <label className={p.selectedIds.includes(employee.id) ? 'selected' : ''} key={employee.id}><input type="checkbox" checked={p.selectedIds.includes(employee.id)} onChange={event => p.setSelectedIds(ids => event.target.checked ? [...ids, employee.id] : ids.filter(id => id !== employee.id))} /><span><strong>{employee.firstName} {employee.lastName}</strong><small>{employee.employeeCode} / {employee.department || 'No department'}</small></span></label>)}</div><h3>Approved off-cycle adjustments</h3><div className="adjustment-select-list">{p.adjustments.map(row => <label key={row.id}><input type="checkbox" checked={p.selectedAdjustmentIds.includes(row.id)} onChange={event => p.setSelectedAdjustmentIds(ids => event.target.checked ? [...ids, row.id] : ids.filter(id => id !== row.id))} /><span><strong>{row.employeeName} / {row.componentName}</strong><small>{row.adjustmentType} / {row.reasonCode} / {money(row.amount)}</small></span></label>)}{!p.adjustments.length && <p className="empty">No approved off-cycle adjustments for this period.</p>}</div></div>
}

function AdjustmentMini(p: { rows: PayrollAdjustment[]; title: string; edit?: (row: PayrollAdjustment) => void; cancel?: (id: number) => Promise<void> }) {
  return <div className="adjustment-mini"><h3>{p.title}</h3><table><thead><tr><th>Employee</th><th>Type</th><th>Component</th><th>Amount</th><th>Status</th><th>Actions</th></tr></thead><tbody>{p.rows.map(row => <tr key={row.id}><td>{row.employeeName}<small>{row.employeeCode}</small></td><td>{row.adjustmentType}<small>{row.payRunType}</small></td><td>{row.componentName}<small>{row.reasonCode}</small></td><td>{money(row.amount)}</td><td>{row.status}</td><td><span className="adjustment-actions">{p.edit && row.status !== 'Applied' && <button type="button" onClick={() => p.edit?.(row)}>Edit</button>}{p.cancel && row.status !== 'Applied' && <button type="button" className="danger" onClick={() => void p.cancel?.(row.id)}>Cancel</button>}</span></td></tr>)}</tbody></table>{!p.rows.length && <p className="empty">No adjustments found.</p>}</div>
}

function PayRunReview(p: { selected: PayRun; busy: boolean; materialVarianceCount: number; updateEmployee: (employee: RunEmployee, change: Partial<RunEmployee>) => Promise<void>; action: (path: string, success: string) => Promise<void> }) {
  const selected = p.selected
  return <section className="card pay-run-details enterprise-review"><header><i className="blue">3</i><div><h3>{selected.clientName} / {selected.payPeriod}</h3><p>{selected.runType || 'Regular'} / {selected.runName || 'Regular payroll'} / {selected.status} / {selected.employeeCount} included employees</p></div><span className={`status-chip ${selected.status.toLowerCase().replace(/\s+/g, '-')}`}>{selected.status}</span></header><div className="pay-run-summary"><div><span>Payroll cost</span><strong>{money(selected.payrollCost)}</strong></div><div><span>Net payable</span><strong>{money(selected.netPay)}</strong></div><div><span>Variance alerts</span><strong>{p.materialVarianceCount}</strong></div></div><div className="pay-run-table"><table><thead><tr><th>Include</th><th>Employee</th><th>Present</th><th>One-time earnings</th><th>Manual TDS</th><th>Recovery</th><th>Gross</th><th>Deductions</th><th>Net</th></tr></thead><tbody>{selected.employees.map(employee => <tr key={employee.id}><td><input type="checkbox" checked={!employee.isSkipped} disabled={selected.status !== 'Draft'} onChange={event => void p.updateEmployee(employee, { isSkipped: !event.target.checked })} /></td><td>{employee.employeeName}<small>{employee.employeeCode}</small></td><td><input type="number" value={employee.presentDays} disabled={selected.status !== 'Draft' || employee.isSkipped || selected.runType === 'Off Cycle'} onChange={event => void p.updateEmployee(employee, { presentDays: Number(event.target.value) })} /></td><td><input type="number" value={employee.oneTimeEarnings} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void p.updateEmployee(employee, { oneTimeEarnings: Number(event.target.value) })} /></td><td><input type="number" value={employee.manualTds || 0} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void p.updateEmployee(employee, { manualTds: Number(event.target.value) })} /></td><td><input type="number" value={employee.oneTimeDeductions} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void p.updateEmployee(employee, { oneTimeDeductions: Number(event.target.value) })} /></td><td>{money(employee.grossPay)}</td><td>{money(employee.statutoryDeductions + employee.oneTimeDeductions)}</td><td><strong>{money(employee.netPay)}</strong></td></tr>)}</tbody></table></div><div className="pay-run-actions"><button type="button" disabled={p.busy || selected.status !== 'Draft'} onClick={() => void p.action('submit', 'Payroll locked and sent for approval.')}>Lock and send for approval</button><button type="button" disabled={p.busy || selected.status !== 'Pending Approval'} onClick={() => void p.action('approve', 'Payroll approved.')}>Approve payroll</button><button type="button" className="secondary" disabled={p.busy || !['Approved', 'Pending Approval'].includes(selected.status)} onClick={() => void p.action('recall', 'Payroll recalled to draft.')}>Recall</button><a className="secondary" href={exportPayRunUrl(selected.id)}>Export</a></div></section>
}

function attendanceIssue(row: EmployeeMonthlyAttendance) {
  const working = Number(row.workingDays || 0), present = Number(row.presentDays || 0), payable = Number(row.payableDays || 0), lop = Number(row.lopDays || 0)
  return working <= 0 || present > working || payable > working || Math.abs((payable + lop) - working) > 0.01
}

function isAdjustmentComponent(component: Component) {
  return component.active && ['Earning', 'Deduction', 'Reimbursement', 'Benefit', 'Correction'].includes(component.category)
}
