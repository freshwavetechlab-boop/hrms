import { useEffect, useMemo, useState } from 'react'
import { getMonthlyAttendance } from '../services/leaveAttendanceService'
import { cancelPayrollAdjustment, createPayRun, exportPayRunUrl, getClients, getEmployees, getPayRun, getPayRunDiagnostics, getPayrollAdjustments, getPayRuns, runPayRunAction, savePayrollAdjustment, updatePayRunEmployee } from '../services/payrollService'
import { getSetup } from '../services/settingsService'
import type { Client, Component, Employee, EmployeeMonthlyAttendance, PayRun, PayRunDiagnostics, PayrollAdjustment, RunEmployee, Setup } from '../types/payroll'
import { setup0 } from '../data/payrollDefaults'
import { money } from '../utils/salary'
import PageTabs from './PageTabs'
import DataTable, { type Column } from './DataTable'
import FileDropZone from './FileDropZone'
import SearchSelect from './SearchSelect'
import { componentToAdjustmentType, prepareAdjustmentImports, type AdjustmentImportMode } from '../features/payroll/adjustmentImport'

const currentPeriod = new Date().toISOString().slice(0, 7)
type PayrollTab = 'Regular Run' | 'Adjustments' | 'Off-cycle Run'
type PayrollStage = 'Setup' | 'Employees' | 'Review'
type AdjustmentStage = 'Entry' | 'Import'
const stageItems: readonly PayrollStage[] = ['Employees', 'Setup', 'Review']
const adjustmentStages: readonly AdjustmentStage[] = ['Entry', 'Import']

const adjustment0: PayrollAdjustment = { id: 0, clientId: 0, employeeId: 0, employeeName: '', employeeCode: '', componentId: 0, componentCode: '', componentName: '', adjustmentType: 'Earning', amount: 0, payPeriod: currentPeriod, payRunType: 'Regular', reasonCode: 'Overtime', notes: '', taxable: true, status: 'Approved', payRunId: null }

export default function PayRunsPanel({ mode = 'payrun', initialRunType = 'Regular Run' }: { mode?: 'payrun' | 'adjustments'; initialRunType?: Exclude<PayrollTab, 'Adjustments'> }) {
  const [clients, setClients] = useState<Client[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [runs, setRuns] = useState<PayRun[]>([])
  const [setup, setSetup] = useState<Setup>(setup0), [selected, setSelected] = useState<PayRun | null>(null)
  const [diagnostics, setDiagnostics] = useState<PayRunDiagnostics | null>(null)
  const [clientId, setClientId] = useState(0), [period, setPeriod] = useState(currentPeriod), [workingDays, setWorkingDays] = useState(30)
  const [includedIds, setIncludedIds] = useState<number[]>([]), [offcycleEmployeeIds, setOffcycleEmployeeIds] = useState<number[]>([]), [offcycleAdjustmentIds, setOffcycleAdjustmentIds] = useState<number[]>([])
  const [adjustments, setAdjustments] = useState<PayrollAdjustment[]>([]), [adjustment, setAdjustment] = useState<PayrollAdjustment>(adjustment0)
  const [tab] = useState<PayrollTab>(mode === 'adjustments' ? 'Adjustments' : initialRunType), [busy, setBusy] = useState(false), [message, setMessage] = useState('Select client, verify employees, then run payroll.')
  const [stage, setStage] = useState<PayrollStage>('Employees')
  const [adjustmentStage, setAdjustmentStage] = useState<AdjustmentStage>('Entry')
  const [attendanceRows, setAttendanceRows] = useState<EmployeeMonthlyAttendance[]>([]), [offcycleName, setOffcycleName] = useState('Off-cycle payment'), [offcycleReason, setOffcycleReason] = useState('Missed employee / reimbursement payment')

  const clientEmployees = useMemo(() => employees.filter(employee => employee.clientId === clientId && employee.isActive), [clientId, employees])
  const variableComponents = useMemo(() => setup.salaryComponents.filter(isAdjustmentComponent), [setup.salaryComponents])
  const pendingAdjustments = adjustments.filter(item => item.status === 'Approved' && item.clientId === clientId && item.payPeriod === period)
  const regularAdjustments = pendingAdjustments.filter(item => item.payRunType !== 'Off Cycle')
  const offcycleAdjustments = pendingAdjustments.filter(item => item.payRunType === 'Off Cycle')
  const includedCount = includedIds.length
  const estimatedMonthlyCost = clientEmployees.filter(employee => includedIds.includes(employee.id)).reduce((sum, employee) => sum + Number(employee.annualCtc || 0) / 12, 0)
  const offcycleEmployeeCost = clientEmployees.filter(employee => offcycleEmployeeIds.includes(employee.id)).reduce((sum, employee) => sum + Number(employee.annualCtc || 0) / 12, 0)
  const offcycleAdjustmentCost = offcycleAdjustments.filter(item => offcycleAdjustmentIds.includes(item.id)).reduce((sum, item) => sum + (isDeductionAdjustment(item) ? -Number(item.amount || 0) : Number(item.amount || 0)), 0)
  const selectedEstimate = tab === 'Off-cycle Run' ? offcycleEmployeeCost + offcycleAdjustmentCost : estimatedMonthlyCost
  const estimateLabel = tab === 'Off-cycle Run' ? 'Off-cycle estimate' : 'Estimated monthly cost'
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
    setSetup({ ...setup0, ...setupRow, salaryComponents: setupRow.salaryComponents ?? [] })
    setAdjustments(adjustmentRows)
  }

  const open = async (id: number) => {
    const [payRun, diagnosticRows] = await Promise.all([getPayRun(id), getPayRunDiagnostics(id)])
    if (payRun) setSelected(payRun)
    setDiagnostics(diagnosticRows)
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
      setDiagnostics(await getPayRunDiagnostics(response.data.id))
      setStage('Review')
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

  const importAdjustments = async (file: File, mode: AdjustmentImportMode) => {
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
    if (response.ok && response.data) { setSelected(response.data); setDiagnostics(await getPayRunDiagnostics(response.data.id)); setMessage(success); await load() }
    setBusy(false)
  }

  const chooseComponent = (id: number) => {
    const component = variableComponents.find(item => item.id === id)
    if (component) setAdjustment({ ...adjustment, componentId: component.id, componentCode: component.code, componentName: component.name, adjustmentType: componentToAdjustmentType(component), taxable: component.taxable })
  }

  const reviewPanel = selected ? <PayRunReview selected={selected} diagnostics={diagnostics} busy={busy} updateEmployee={updateEmployee} action={action} materialVarianceCount={materialVarianceCount} /> : <section className="card report-empty"><p>No draft selected yet.</p></section>

  return <section className="pay-runs payroll-cockpit">
    <div className="payroll-status-strip"><span>{message}</span><span className={`status-chip ${attendanceReady ? 'paid' : 'pending-approval'}`}>{attendanceReady ? 'Attendance ready' : `${attendanceIssues || includedCount} attendance issue${(attendanceIssues || includedCount) === 1 ? '' : 's'}`}</span></div>
    {mode !== 'adjustments' && <PageTabs items={stageItems} value={stage} onChange={setStage} label="Payroll flow" className="payrun-flow-tabs" />}
    {mode === 'adjustments' && <PageTabs items={adjustmentStages} value={adjustmentStage} onChange={setAdjustmentStage} label="Adjustment flow" className="payrun-flow-tabs" />}
    <section className="payroll-command-grid">
      {(mode === 'adjustments' || stage === 'Setup') && <div className="card payroll-control-panel"><header><i className="blue">R</i><div><h3>Run context</h3><p>Client, period and statutory working days are shared across payroll operations.</p></div></header><div className="pay-run-form enterprise"><label>Client<SearchSelect value={clientId} onChange={value => changeClient(Number(value))} options={clients.filter(client => client.isActive).map(client => ({ value: client.id, label: client.name }))} /></label><label>Pay period<input type="month" value={period} onChange={event => setPeriod(event.target.value)} /></label><label>Working days<input type="number" min="1" max="31" value={workingDays} onChange={event => setWorkingDays(Number(event.target.value))} /></label>{tab === 'Regular Run' && <button onClick={() => void createRegular()} disabled={busy || !clientId || includedCount === 0 || !attendanceReady}>{busy ? 'Preparing...' : 'Run regular draft'}</button>}{tab === 'Off-cycle Run' && <button onClick={() => void createOffcycle()} disabled={busy || !clientId || (offcycleEmployeeIds.length === 0 && offcycleAdjustmentIds.length === 0)}>{busy ? 'Preparing...' : 'Create off-cycle draft'}</button>}</div>{tab === 'Off-cycle Run' && <div className="offcycle-fields"><label>Run name<input value={offcycleName} onChange={event => setOffcycleName(event.target.value)} /></label><label>Reason<input value={offcycleReason} onChange={event => setOffcycleReason(event.target.value)} /></label></div>}{!attendanceReady && tab === 'Regular Run' && <p className="payment-warning">Attendance review must be clean before regular payroll can run. Off-cycle can be used for missed/reimbursement-only payments.</p>}<div className="payroll-kpis"><div><span>{tab === 'Off-cycle Run' ? 'Selected employees' : 'Regular employees'}</span><strong>{tab === 'Off-cycle Run' ? `${offcycleEmployeeIds.length}/${clientEmployees.length}` : `${includedCount}/${clientEmployees.length}`}</strong></div><div><span>{tab === 'Off-cycle Run' ? 'Selected adjustments' : 'Approved adjustments'}</span><strong>{tab === 'Off-cycle Run' ? `${offcycleAdjustmentIds.length}/${offcycleAdjustments.length}` : pendingAdjustments.length}</strong></div><div><span>{estimateLabel}</span><strong>{money(selectedEstimate)}</strong></div><div><span>Current draft net</span><strong>{money(selected?.netPay)}</strong></div></div></div>}
      {tab === 'Regular Run' && stage === 'Employees' && <RosterCard employees={clientEmployees} includedIds={includedIds} setIncludedIds={setIncludedIds} adjustments={regularAdjustments} />}
      {tab === 'Adjustments' && adjustmentStage === 'Entry' && <AdjustmentCard adjustment={adjustment} setAdjustment={setAdjustment} employees={clientEmployees} components={variableComponents} adjustments={adjustments.filter(item => item.clientId === clientId && item.payPeriod === period)} chooseComponent={chooseComponent} saveAdjustment={saveAdjustment} edit={row => setAdjustment({ ...row, amount: Number(row.amount) })} cancel={async id => { await cancelPayrollAdjustment(id); await load() }} />}
      {tab === 'Adjustments' && adjustmentStage === 'Import' && <AdjustmentImportPanel importScheduled={file => void importAdjustments(file, 'scheduled')} importArrears={file => void importAdjustments(file, 'arrears')} />}
      {tab === 'Off-cycle Run' && stage === 'Employees' && <OffcycleCard employees={clientEmployees} selectedIds={offcycleEmployeeIds} setSelectedIds={setOffcycleEmployeeIds} adjustments={offcycleAdjustments} selectedAdjustmentIds={offcycleAdjustmentIds} setSelectedAdjustmentIds={setOffcycleAdjustmentIds} name={offcycleName} setName={setOffcycleName} reason={offcycleReason} setReason={setOffcycleReason} />}
      {mode !== 'adjustments' && stage === 'Review' && reviewPanel}
    </section>
  </section>
}

function RosterCard(p: { employees: Employee[]; includedIds: number[]; setIncludedIds: (fn: number[] | ((ids: number[]) => number[])) => void; adjustments: PayrollAdjustment[] }) {
  const selected = new Set(p.includedIds)
  const columns = employeeSelectionColumns(p.includedIds, p.setIncludedIds, 'Include')
  return <div className="card payroll-roster employee-selection-table"><header><i className="blue">E</i><div><h3>Employee roster</h3><p>Deselect employees before payroll is calculated. Approved regular adjustments are auto-included.</p></div></header><div className="roster-actions"><button type="button" onClick={() => p.setIncludedIds(p.employees.map(employee => employee.id))}>Select all</button><button type="button" onClick={() => p.setIncludedIds([])}>Clear</button><span>{selected.size} selected</span></div><DataTable rows={p.employees} title="Employees" getRowId={row => row.id} emptyText="No employees found for this client." exportFileName="regular-payroll-employees" columns={columns} pageSizeOptions={[25, 50, 100, 250]} /><AdjustmentMini rows={p.adjustments} title="Approved regular adjustments" /></div>
}

function AdjustmentCard(p: { adjustment: PayrollAdjustment; setAdjustment: (row: PayrollAdjustment) => void; employees: Employee[]; components: Component[]; adjustments: PayrollAdjustment[]; chooseComponent: (id: number) => void; saveAdjustment: () => void; edit: (row: PayrollAdjustment) => void; cancel: (id: number) => Promise<void> }) {
  return <div className="card payroll-adjustments"><header><i className="blue">A</i><div><h3>{p.adjustment.id ? 'Edit adjustment entry' : 'Variable earning / adjustment entry'}</h3><p>Use approved one-time entries for overtime, arrears, bonus, reimbursements and recoveries.</p></div></header><div className="adjustment-form"><label>Employee<SearchSelect value={p.adjustment.employeeId} onChange={value => p.setAdjustment({ ...p.adjustment, employeeId: Number(value) })} options={[{ value: 0, label: 'Select employee' }, ...p.employees.map(employee => ({ value: employee.id, label: `${employee.firstName} ${employee.lastName} / ${employee.employeeCode}` }))]} /></label><label>Component<SearchSelect value={p.adjustment.componentId} onChange={value => p.chooseComponent(Number(value))} options={[{ value: 0, label: 'Select variable component' }, ...p.components.map(component => ({ value: component.id, label: `${component.name} / ${component.category}` }))]} /></label><label>Pay Run Type<SearchSelect value={p.adjustment.payRunType} onChange={value => p.setAdjustment({ ...p.adjustment, payRunType: value })} options={['Regular', 'Off Cycle'].map(value => ({ value, label: value }))} /></label><label>Adjustment Type<SearchSelect value={p.adjustment.adjustmentType} onChange={value => p.setAdjustment({ ...p.adjustment, adjustmentType: value })} options={['Earning', 'Reimbursement', 'Deduction'].map(value => ({ value, label: value }))} /></label><label>Amount<input type="number" min="0" value={p.adjustment.amount || ''} onChange={event => p.setAdjustment({ ...p.adjustment, amount: Number(event.target.value) })} /></label><label>Reason<SearchSelect value={p.adjustment.reasonCode} onChange={value => p.setAdjustment({ ...p.adjustment, reasonCode: value })} options={['Overtime', 'Arrears', 'Bonus/Incentive', 'Missed Salary', 'Reimbursement', 'Recovery/Correction'].map(value => ({ value, label: value }))} /></label><label className="wide">Notes<input value={p.adjustment.notes} onChange={event => p.setAdjustment({ ...p.adjustment, notes: event.target.value })} placeholder="Business justification, approval reference or reimbursement details" /></label><button type="button" onClick={p.saveAdjustment}>{p.adjustment.id ? 'Update adjustment' : 'Save approved adjustment'}</button></div><AdjustmentMini rows={p.adjustments} title="Adjustment register" edit={p.edit} cancel={p.cancel} /></div>
}

function AdjustmentImportPanel(p: { importScheduled: (file: File) => void; importArrears: (file: File) => void }) {
  return <div className="card adjustment-import-panel"><header><i className="blue">I</i><div><h3>Bulk adjustment imports</h3><p>Import rows become approved adjustment entries and are picked by regular or off-cycle payroll based on Pay Run Type.</p></div></header><div className="adjustment-import-grid"><article><h3>Scheduled earnings import</h3><p>Use for planned bonuses, incentives, overtime or one-time reimbursements.</p><small>CSV columns: employeeCode, componentCode, amount, payPeriod, payRunType, reason, notes</small><FileDropZone accept=".csv,text/csv" title="Drop scheduled CSV here" hint="Browse or drag CSV for scheduled earnings." onFile={p.importScheduled} /></article><article><h3>Automatic arrears from revision</h3><p>Use salary-revision differences to generate arrears without manual amount calculation.</p><small>CSV columns: employeeCode, componentCode, oldMonthly, newMonthly, months, payPeriod, payRunType, notes</small><FileDropZone accept=".csv,text/csv" title="Drop arrears CSV here" hint="Browse or drag CSV for arrears import." onFile={p.importArrears} /></article></div></div>
}

function OffcycleCard(p: { employees: Employee[]; selectedIds: number[]; setSelectedIds: (fn: number[] | ((ids: number[]) => number[])) => void; adjustments: PayrollAdjustment[]; selectedAdjustmentIds: number[]; setSelectedAdjustmentIds: (fn: number[] | ((ids: number[]) => number[])) => void; name: string; setName: (value: string) => void; reason: string; setReason: (value: string) => void }) {
  const selected = new Set(p.selectedIds)
  const columns = employeeSelectionColumns(p.selectedIds, p.setSelectedIds, 'Pay')
  return <div className="card payroll-roster employee-selection-table"><header><i className="blue">E</i><div><h3>Off-cycle employees</h3><p>Select employees and approved off-cycle adjustments to include.</p></div></header><div className="roster-actions"><button type="button" onClick={() => p.setSelectedIds(p.employees.map(employee => employee.id))}>Select all</button><button type="button" onClick={() => p.setSelectedIds([])}>Clear</button><span>{selected.size} selected</span></div><DataTable rows={p.employees} title="Employees to pay" getRowId={row => row.id} emptyText="No employees found for this client." exportFileName="off-cycle-employees" columns={columns} pageSizeOptions={[25, 50, 100, 250]} /><h3>Approved off-cycle adjustments</h3><div className="adjustment-select-list">{p.adjustments.map(row => <label key={row.id}><input type="checkbox" checked={p.selectedAdjustmentIds.includes(row.id)} onChange={event => p.setSelectedAdjustmentIds(ids => event.target.checked ? [...ids, row.id] : ids.filter(id => id !== row.id))} /><span><strong>{row.employeeName} / {row.componentName}</strong><small>{row.adjustmentType} / {row.reasonCode} / {money(row.amount)}</small></span></label>)}{!p.adjustments.length && <p className="empty">No approved off-cycle adjustments for this period.</p>}</div></div>
}

function employeeSelectionColumns(selectedIds: number[], setSelectedIds: (fn: number[] | ((ids: number[]) => number[])) => void, selectLabel: string): Column<Employee>[] {
  const selected = new Set(selectedIds)
  const toggle = (employeeId: number, checked: boolean) => setSelectedIds(ids => checked ? ids.includes(employeeId) ? ids : [...ids, employeeId] : ids.filter(id => id !== employeeId))
  return [
    { key: 'selected', label: selectLabel, sortable: false, filterable: false, width: '76px', render: employee => <input type="checkbox" checked={selected.has(employee.id)} onChange={event => toggle(employee.id, event.target.checked)} />, exportValue: employee => selected.has(employee.id) ? 'Yes' : 'No' },
    { key: 'employeeName', label: 'Employee', value: employee => `${employee.firstName} ${employee.lastName}`.trim(), render: employee => <>{employee.firstName} {employee.lastName}<small>{employee.employeeCode}</small></> },
    { key: 'employeeCode', label: 'Code' },
    { key: 'department', label: 'Department', value: employee => employee.department || 'No department' },
    { key: 'monthlyCost', label: 'Monthly cost', value: employee => Math.round(Number(employee.annualCtc || 0) / 12), render: employee => money(Number(employee.annualCtc || 0) / 12) }
  ]
}

function AdjustmentMini(p: { rows: PayrollAdjustment[]; title: string; edit?: (row: PayrollAdjustment) => void; cancel?: (id: number) => Promise<void> }) {
  const columns: Column<PayrollAdjustment>[] = [
    { key: 'employeeName', label: 'Employee', render: row => <>{row.employeeName}<small>{row.employeeCode}</small></>, exportValue: row => `${row.employeeName} ${row.employeeCode}`.trim() },
    { key: 'adjustmentType', label: 'Type', render: row => <>{row.adjustmentType}<small>{row.payRunType}</small></>, exportValue: row => `${row.adjustmentType} / ${row.payRunType}` },
    { key: 'componentName', label: 'Component', render: row => <>{row.componentName}<small>{row.reasonCode}</small></>, exportValue: row => `${row.componentName} / ${row.reasonCode}` },
    { key: 'amount', label: 'Amount', value: row => Number(row.amount || 0), render: row => money(row.amount) },
    { key: 'status', label: 'Status' }
  ]
  return <div className="adjustment-mini"><DataTable rows={p.rows} title={p.title} getRowId={row => row.id} emptyText="No adjustments found." exportFileName={p.title.toLowerCase().replace(/\s+/g, '-')} columns={columns} pageSizeOptions={[5, 10, 25]} actions={row => <span className="adjustment-actions">{p.edit && row.status !== 'Applied' && <button type="button" onClick={() => p.edit?.(row)}>Edit</button>}{p.cancel && row.status !== 'Applied' && <button type="button" className="danger" onClick={() => void p.cancel?.(row.id)}>Cancel</button>}</span>} /></div>
}

function PayRunReview(p: { selected: PayRun; diagnostics: PayRunDiagnostics | null; busy: boolean; materialVarianceCount: number; updateEmployee: (employee: RunEmployee, change: Partial<RunEmployee>) => Promise<void>; action: (path: string, success: string) => Promise<void> }) {
  const selected = p.selected
  const includedEmployees = selected.employees.filter(employee => !employee.isSkipped)
  const presentDays = includedEmployees.reduce((sum, employee) => sum + Number(employee.presentDays || 0), 0)
  const payableDays = includedEmployees.reduce((sum, employee) => sum + Number(employee.payableDays || 0), 0)
  const workingDays = selected.runType === 'Off Cycle' ? 0 : selected.totalWorkingDays * includedEmployees.length
  const lopDays = Math.max(0, workingDays - payableDays)
  const columns: Column<RunEmployee>[] = [
    { key: 'isSkipped', label: 'Include', sortable: false, filterable: false, width: '72px', render: employee => <input type="checkbox" checked={!employee.isSkipped} disabled={selected.status !== 'Draft'} onChange={event => void p.updateEmployee(employee, { isSkipped: !event.target.checked })} />, exportValue: row => row.isSkipped ? 'No' : 'Yes' },
    { key: 'employeeName', label: 'Employee', render: employee => <>{employee.employeeName}<small>{employee.employeeCode}</small></>, exportValue: row => `${row.employeeName} ${row.employeeCode}`.trim() },
    { key: 'presentDays', label: 'Present', render: employee => <input type="number" value={employee.presentDays} disabled={selected.status !== 'Draft' || employee.isSkipped || selected.runType === 'Off Cycle'} onChange={event => void p.updateEmployee(employee, { presentDays: Number(event.target.value) })} /> },
    { key: 'payableDays', label: 'Payable', value: row => row.payableDays ?? 0 },
    { key: 'leaveBreakdown', label: 'Leave', sortable: false, filterable: false, render: employee => <LeaveChips employee={employee} totalWorkingDays={selected.totalWorkingDays} offcycle={selected.runType === 'Off Cycle'} />, exportValue: row => leaveText(row, selected.totalWorkingDays, selected.runType === 'Off Cycle') },
    { key: 'oneTimeEarnings', label: 'One-time earnings', render: employee => <input type="number" value={employee.oneTimeEarnings} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void p.updateEmployee(employee, { oneTimeEarnings: Number(event.target.value) })} />, exportValue: row => row.oneTimeEarnings },
    { key: 'manualTds', label: 'Manual TDS', render: employee => <input type="number" value={employee.manualTds || 0} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void p.updateEmployee(employee, { manualTds: Number(event.target.value) })} />, exportValue: row => row.manualTds || 0 },
    { key: 'oneTimeDeductions', label: 'Recovery', render: employee => <input type="number" value={employee.oneTimeDeductions} disabled={selected.status !== 'Draft' || employee.isSkipped} onChange={event => void p.updateEmployee(employee, { oneTimeDeductions: Number(event.target.value) })} />, exportValue: row => row.oneTimeDeductions },
    { key: 'grossPay', label: 'Gross', value: row => row.grossPay, render: row => money(row.grossPay) },
    { key: 'deductions', label: 'Deductions', value: row => row.statutoryDeductions + row.oneTimeDeductions, render: row => money(row.statutoryDeductions + row.oneTimeDeductions) },
    { key: 'netPay', label: 'Net', value: row => row.netPay, render: row => <strong>{money(row.netPay)}</strong> }
  ]
  return <section className="card pay-run-details enterprise-review"><header><i className="blue">R</i><div><h3>{selected.clientName} / {selected.payPeriod}</h3><p>{selected.runType || 'Regular'} / {selected.runName || 'Regular payroll'} / {selected.status} / {selected.employeeCount} included employees</p></div><span className={`status-chip ${selected.status.toLowerCase().replace(/\s+/g, '-')}`}>{selected.status}</span></header><div className="pay-run-summary"><div><span>Payroll cost</span><strong>{money(selected.payrollCost)}</strong></div><div><span>Net payable</span><strong>{money(selected.netPay)}</strong></div><div><span>Payable days</span><strong>{payableDays}</strong></div><div><span>LOP days</span><strong>{lopDays}</strong></div><div><span>Present days</span><strong>{presentDays}</strong></div><div><span>Variance alerts</span><strong>{p.materialVarianceCount}</strong></div></div>{selected.runType === 'Off Cycle' && <p className="payment-warning">Off-cycle runs do not use monthly attendance payable days. Regular payroll pulls payable days from Attendance Review.</p>}<div className="pay-run-table"><DataTable rows={selected.employees} title="Employee payroll review" getRowId={row => row.employeeId} rowClassName={row => row.isSkipped ? 'skipped' : ''} emptyText="No employees in this run." exportFileName={`pay-run-${selected.payPeriod}`} columns={columns} pageSizeOptions={[10, 25, 50, 100]} /></div><DiagnosticsPanel diagnostics={p.diagnostics} payPeriod={selected.payPeriod} /><div className="pay-run-actions"><button type="button" className="lock-action" disabled={p.busy || selected.status !== 'Draft'} onClick={() => void p.action('submit', 'Payroll locked and sent for approval.')}>Lock and send for approval</button><button type="button" className="approve-action" disabled={p.busy || selected.status !== 'Pending Approval'} onClick={() => void p.action('approve', 'Payroll approved.')}>Approve payroll</button><button type="button" className="secondary recall-action" disabled={p.busy || !['Approved', 'Pending Approval'].includes(selected.status)} onClick={() => void p.action('recall', 'Payroll recalled to draft.')}>Recall</button><a className="secondary export-action" href={exportPayRunUrl(selected.id)}>Export</a></div></section>
}

function DiagnosticsPanel({ diagnostics, payPeriod }: { diagnostics: PayRunDiagnostics | null; payPeriod: string }) {
  if (!diagnostics) return <section className="payroll-diagnostics"><p className="empty">Diagnostics are not available for this run yet.</p></section>
  const failedSteps = diagnostics.stepLogs.filter(row => row.status !== 'Success')
  const failedRecon = diagnostics.reconciliationResults.filter(row => row.status !== 'Passed')
  const validationRows = diagnostics.validationIssues.slice(0, 50)
  const traceRows = diagnostics.calculationTraces.slice(0, 100)
  return <section className="payroll-diagnostics"><div className="pay-run-summary diagnostics-summary"><div><span>Pipeline steps</span><strong>{diagnostics.stepLogs.length}</strong></div><div><span>Failed steps</span><strong>{failedSteps.length}</strong></div><div><span>Validation issues</span><strong>{diagnostics.validationIssues.length}</strong></div><div><span>Recon failures</span><strong>{failedRecon.length}</strong></div><div><span>Trace rows</span><strong>{diagnostics.calculationTraces.length}</strong></div></div><DataTable rows={diagnostics.reconciliationResults} title="Reconciliation controls" getRowId={row => row.id} emptyText="No reconciliation checks found." exportFileName={`reconciliation-${payPeriod}`} pageSizeOptions={[5, 10, 25]} columns={[
    { key: 'status', label: 'Status', render: row => <span className={`status-chip ${row.status.toLowerCase()}`}>{row.status}</span>, exportValue: row => row.status },
    { key: 'checkName', label: 'Check' },
    { key: 'expectedAmount', label: 'Expected', value: row => row.expectedAmount, render: row => money(row.expectedAmount) },
    { key: 'actualAmount', label: 'Actual', value: row => row.actualAmount, render: row => money(row.actualAmount) },
    { key: 'differenceAmount', label: 'Difference', value: row => row.differenceAmount, render: row => money(row.differenceAmount) }
  ]} /><DataTable rows={validationRows} title="Validation and exception log" getRowId={row => row.id} emptyText="No validation issues found." exportFileName={`validation-${payPeriod}`} pageSizeOptions={[5, 10, 25]} columns={[
    { key: 'severity', label: 'Severity' },
    { key: 'employeeCode', label: 'Employee' },
    { key: 'stepName', label: 'Step' },
    { key: 'message', label: 'Message' },
    { key: 'isBlocking', label: 'Blocking', render: row => row.isBlocking ? 'Yes' : 'No', exportValue: row => row.isBlocking ? 'Yes' : 'No' }
  ]} /><DataTable rows={traceRows} title="Calculation trace sample" getRowId={row => row.id} emptyText="No calculation trace rows found." exportFileName={`calculation-trace-${payPeriod}`} pageSizeOptions={[10, 25, 50]} columns={[
    { key: 'employeeCode', label: 'Employee' },
    { key: 'componentCode', label: 'Component', render: row => <>{row.componentCode}<small>{row.componentName}</small></>, exportValue: row => `${row.componentCode} ${row.componentName}` },
    { key: 'formulaUsed', label: 'Formula' },
    { key: 'baseAmount', label: 'Base', value: row => row.baseAmount, render: row => money(row.baseAmount) },
    { key: 'factor', label: 'Factor' },
    { key: 'calculatedAmount', label: 'Amount', value: row => row.calculatedAmount, render: row => money(row.calculatedAmount) }
  ]} /></section>
}

function attendanceIssue(row: EmployeeMonthlyAttendance) {
  const working = Number(row.workingDays || 0), present = Number(row.presentDays || 0), payable = Number(row.payableDays || 0), lop = Number(row.lopDays || 0)
  return working <= 0 || present > working || payable > working || Math.abs((payable + lop) - working) > 0.01
}

function isAdjustmentComponent(component: Component) {
  return component.active && ['Earning', 'Deduction', 'Reimbursement', 'Benefit', 'Correction'].includes(component.category)
}

function LeaveChips(p: { employee: RunEmployee; totalWorkingDays: number; offcycle: boolean }) {
  const items = leaveItems(p.employee, p.totalWorkingDays, p.offcycle)
  return <span className="leave-chip-list">{items.length ? items.map(item => <span className={`leave-chip ${item.type.toLowerCase()}`} title={item.name} key={item.code}>{item.code} {item.days}d</span>) : <small>No leave</small>}</span>
}

function leaveItems(employee: RunEmployee, totalWorkingDays: number, offcycle: boolean) {
  const rows = employee.leaveBreakdown ?? []
  if (rows.length || offcycle || employee.isSkipped) return rows
  const lop = Math.max(0, totalWorkingDays - Number(employee.payableDays || 0))
  return lop ? [{ code: 'LOP', name: 'Loss of Pay', type: 'Unpaid', days: lop, payableDays: 0 }] : []
}

function leaveText(employee: RunEmployee, totalWorkingDays: number, offcycle: boolean) {
  return leaveItems(employee, totalWorkingDays, offcycle).map(item => `${item.code} ${item.days}d`).join(', ')
}

function isDeductionAdjustment(adjustment: PayrollAdjustment) {
  return ['Deduction', 'Recovery'].includes(adjustment.adjustmentType)
}
