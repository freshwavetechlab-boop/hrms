import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Collapse, Modal, Select, Spin, Tag } from 'antd'
import { simulateSalary, simulationEmployees, type SimulationEmployee, type SimulationResult } from '../services/payrollCalculationService'
import { getPayRuns } from '../services/payrollService'
import { employeeSimulationInput } from '../utils/employeeSimulationInput'
import { calendarDaysForSimulation, employeePresentScenario } from '../utils/employeePresentSimulation'
import { money } from '../utils/salary'
import SalarySimulationWorksheet, { type SalaryWorksheetEmployeeRow } from './SalarySimulationWorksheet'
import type { PayrollSimulationProps } from './PayrollSimulation'

type RowState = { baseline: SimulationResult | null; result: SimulationResult | null; loading: boolean; current: boolean; error: string }
const EMPTY_ROW: RowState = { baseline: null, result: null, loading: true, current: false, error: '' }
let activeRequests = 0
const requestQueue: (() => void)[] = []
function limited<T>(run: () => Promise<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    const start = () => { activeRequests++; void run().then(resolve, reject).finally(() => { activeRequests--; requestQueue.shift()?.() }) }
    if (activeRequests < 3) start(); else requestQueue.push(start)
  })
}

function EmployeeRowCalculation({ clientId, employeeId, payPeriod, template, components, edits, onState }: {
  clientId: number; employeeId: number; payPeriod: string; template: PayrollSimulationProps['template']; components: PayrollSimulationProps['components']; edits: Record<string, string>; onState: (state: RowState) => void;
}) {
  const callback = useRef(onState); callback.current = onState
  const [baseline, setBaseline] = useState<SimulationResult | null>(null)
  const lastResult = useRef<SimulationResult | null>(null)
  const context = JSON.stringify({ clientId, employeeId, payPeriod, template, components })
  useEffect(() => {
    if (!calendarDaysForSimulation(payPeriod)) { callback.current({ baseline: null, result: null, loading: false, current: false, error: 'Select a valid payroll month.' }); return }
    let active = true
    callback.current(EMPTY_ROW)
    void limited(async () => active ? simulateSalary({ mode: 'Employee', clientId, employeeId, template, components, payPeriod, payrollDays: null, presentDays: null, payableDays: null, annualCtc: null, amountOverrides: {} }) : null).then(response => {
      if (!active || !response) return
      if (response.ok && response.data) { lastResult.current = response.data; setBaseline(response.data); callback.current({ baseline: response.data, result: response.data, loading: false, current: true, error: '' }) }
      else callback.current({ baseline: null, result: null, loading: false, current: false, error: response.error || 'Could not load this employee’s effective salary.' })
    }).catch(error => { if (active) callback.current({ baseline: null, result: null, loading: false, current: false, error: String(error) }) })
    return () => { active = false }
  }, [context]) // eslint-disable-line react-hooks/exhaustive-deps
  const editsKey = JSON.stringify(edits)
  useEffect(() => {
    if (!baseline) return
    if (!Object.values(edits).some(value => value.trim() !== '')) { lastResult.current = baseline; callback.current({ baseline, result: baseline, loading: false, current: true, error: '' }); return }
    const scenario = employeePresentScenario(baseline, payPeriod, edits)
    const input = employeeSimulationInput({ clientId, employeeId, payPeriod, payrollDays: scenario.payrollDays === null ? '' : String(scenario.payrollDays), presentDays: scenario.presentDays === null ? '' : String(scenario.presentDays), payableDays: scenario.payableDays === null ? '' : String(scenario.payableDays), annualCtc: scenario.annualCtc === null ? '' : String(scenario.annualCtc) })
    const amountOverrides = Object.fromEntries(Object.entries(edits).filter(([key, value]) => key.startsWith('COMPONENT:') && value.trim() !== '').map(([key, value]) => [key.slice(10), Number(value)]))
    if (!input || Object.values(amountOverrides).some(value => !Number.isFinite(value) || value < 0)) {
      callback.current({ baseline, result: lastResult.current, loading: false, current: false, error: 'Enter valid days and amounts. This what-if input must respect the saved payroll divisor; source extra-duty and split-period allocations require their reviewed native inputs.' }); return
    }
    let active = true
    callback.current({ baseline, result: lastResult.current, loading: true, current: false, error: '' })
    const timer = window.setTimeout(() => {
      void limited(async () => active ? simulateSalary({ ...input, amountOverrides, template, components }) : null).then(response => {
        if (!active || !response) return
        if (response.ok && response.data) lastResult.current = response.data
        callback.current({ baseline, result: lastResult.current, loading: false, current: !!response.ok && !!response.data, error: response.ok && response.data ? '' : response.error || 'Simulation returned no result.' })
      }).catch(error => { if (active) callback.current({ baseline, result: lastResult.current, loading: false, current: false, error: String(error) }) })
    }, 350)
    return () => { active = false; window.clearTimeout(timer) }
  }, [baseline, editsKey, context]) // eslint-disable-line react-hooks/exhaustive-deps
  return null
}

function pendingResult(employee: SimulationEmployee, clientId: number, payPeriod: string): SimulationResult {
  return { mode: 'Employee', clientId, employeeId: employee.id, employeeCode: employee.code, employeeName: employee.name, payPeriod, employee: employee.name, templateId: '', complete: false, coverage: '', issues: [], inputs: [{ name: 'PRESENT_DAYS', value: null, source: 'Enter a temporary present-days value', unit: 'Days' }], lines: [], gross: null, deductions: null, benefits: null, net: null }
}

export default function EmployeePayrollSimulation({ template, components, clients, fixedClientId, onClose, onTemplateMode }: PayrollSimulationProps & { onTemplateMode: () => void }) {
  const ids = String(template.clientId || '').split(/[;,|]/).map(value => Number(value.split(':')[0])).filter(value => value > 0)
  const [clientId, setClientId] = useState<number | undefined>(fixedClientId || (ids.length === 1 ? ids[0] : undefined))
  const [selectedEmployees, setSelectedEmployees] = useState<SimulationEmployee[]>([]), [search, setSearch] = useState(''), [employees, setEmployees] = useState<SimulationEmployee[]>([]), [searchError, setSearchError] = useState(''), [searching, setSearching] = useState(false)
  const [payPeriod, setPayPeriod] = useState(() => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` })
  const periodChosen = useRef(false)
  const [editsByEmployee, setEditsByEmployee] = useState<Record<number, Record<string, string>>>({}), [states, setStates] = useState<Record<number, RowState>>({})
  const [retry, setRetry] = useState(0), [activeEmployeeId, setActiveEmployeeId] = useState<number>(), [advanced, setAdvanced] = useState(false)
  useEffect(() => {
    if (!clientId) return
    let active = true
    void getPayRuns().then(runs => {
      const latest = runs.filter(run => run.clientId === clientId && run.runType !== 'Off Cycle' && run.employeeCount > 0 && /^\d{4}-(0[1-9]|1[0-2])$/.test(run.payPeriod)).sort((a, b) => b.payPeriod.localeCompare(a.payPeriod))[0]
      if (active && latest && !periodChosen.current) { setPayPeriod(latest.payPeriod); setStates({}); setEditsByEmployee({}) }
    })
    return () => { active = false }
  }, [clientId])
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
    }, 250)
    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [clientId, search])
  const choices = [...new Map([...selectedEmployees, ...employees].map(employee => [employee.id, employee])).values()]
  const activeEmployee = selectedEmployees.find(employee => employee.id === activeEmployeeId) ?? selectedEmployees[0]
  const activeState = activeEmployee ? states[activeEmployee.id] : undefined
  const result = activeState?.result
  const edit = (id: number, field: string, value: string) => { periodChosen.current = true; setActiveEmployeeId(id); setEditsByEmployee(current => ({ ...current, [id]: { ...current[id], [field]: value } })) }
  const isMissingAttendance = (row?: RowState) => !!row?.result && row.result.issues.some(issue => issue.component === 'Attendance' && /days are missing/i.test(issue.message))
  const loading = selectedEmployees.filter(employee => !states[employee.id] || states[employee.id].loading).length
  const complete = selectedEmployees.filter(employee => states[employee.id]?.current && states[employee.id]?.result?.complete).length
  const rows: SalaryWorksheetEmployeeRow[] = selectedEmployees.map(employee => ({ key: String(employee.id), result: states[employee.id]?.result ?? pendingResult(employee, clientId!, payPeriod), employeeCode: employee.code, employeeName: employee.name, edits: editsByEmployee[employee.id] ?? {}, onEdit: (field, value) => edit(employee.id, field, value), stale: !states[employee.id]?.current, status: states[employee.id]?.error || (states[employee.id]?.loading ? 'Calculating' : states[employee.id]?.result?.complete ? 'Ready' : 'Needs input') }))
  const activeIssues = result?.issues.filter(issue => issue.severity !== 'Info' && !(issue.component === 'Attendance' && /days are missing/i.test(issue.message))) ?? []
  return <Modal open title={`Payroll worksheet · ${template.name}`} width={1400} zIndex={1600} className="payroll-simulation salary-form-controls salary-worksheet-modal" onCancel={onClose} footer={<Button onClick={onClose}>Close</Button>}>
    <div className="salary-present-mode"><div><Tag color="purple">Employee salary simulation</Tag><p>Select employees, choose a month and edit their yellow <b>Present days</b> cells.</p></div><Button onClick={event => { event.currentTarget.blur(); onTemplateMode() }}>Try template draft instead</Button></div>
    <div className="payroll-simulation-controls salary-present-controls">
      {!fixedClientId && <label className="simulation-field">Client<Select aria-label="Employee simulation client" value={clientId} options={clients.filter(client => !ids.length || ids.includes(client.id)).map(client => ({ value: client.id, label: client.name }))} onChange={value => { setClientId(value); setSelectedEmployees([]); setEmployees([]); setSearch(''); setEditsByEmployee({}); setStates({}); periodChosen.current = false }} /></label>}
      <label className="simulation-field salary-present-employees">Employees<Select mode="multiple" aria-label="Simulation employees" showSearch filterOption={false} disabled={!clientId} value={selectedEmployees.map(employee => employee.id)} loading={searching} maxTagCount="responsive" placeholder="Select employees by name or code" options={choices.map(employee => ({ value: employee.id, label: `${employee.code} · ${employee.name}` }))} onSearch={setSearch} onChange={selected => {
        const bounded = selected.slice(0, 25)
        setSelectedEmployees(bounded.map(id => choices.find(employee => employee.id === id)!).filter(Boolean))
        setActiveEmployeeId(current => current && bounded.includes(current) ? current : bounded[0])
        setEditsByEmployee(current => Object.fromEntries(Object.entries(current).filter(([id]) => bounded.includes(Number(id)))))
      }} popupMatchSelectWidth={false} dropdownStyle={{ zIndex: 1700 }} /></label>
      <label className="simulation-field">Pay month<input aria-label="Employee simulation month" type="month" value={payPeriod} onChange={event => { periodChosen.current = true; setPayPeriod(event.target.value); setEditsByEmployee({}); setStates({}) }} /><small>{calendarDaysForSimulation(payPeriod) ?? '—'} calendar days · saved payroll divisor takes priority</small></label>
      <div className="simulation-run"><Button disabled={!selectedEmployees.length} onClick={() => { setEditsByEmployee({}); setStates({}); setRetry(value => value + 1) }}>Reset to saved inputs</Button></div>
    </div>
    {searchError && <Alert type="error" message={searchError} />}
    {!selectedEmployees.length ? <div className="salary-present-empty"><h3>Compare employee salaries in one worksheet</h3><p>Select up to 25 employees. Saved salary, location, payroll days and attendance load automatically.</p></div> : <>
      <div className="salary-present-status"><Tag color="green">{complete} ready</Tag>{loading > 0 && <span role="status"><Spin size="small" /> Calculating {loading}…</span>}<span>{selectedEmployees.length - complete - loading > 0 ? `${selectedEmployees.length - complete - loading} need input or review` : `${selectedEmployees.length} selected`}</span><Button size="small" type="text" onClick={() => setAdvanced(value => !value)}>{advanced ? 'Present days only' : 'Advanced inputs'}</Button></div>
      {clientId && selectedEmployees.map(employee => <EmployeeRowCalculation key={`${clientId}:${payPeriod}:${employee.id}:${template.id}:${retry}`} clientId={clientId} employeeId={employee.id} payPeriod={payPeriod} template={template} components={components} edits={editsByEmployee[employee.id] ?? {}} onState={row => setStates(current => ({ ...current, [employee.id]: row }))} />)}
      <SalarySimulationWorksheet result={rows[0].result} month={payPeriod} clientName={clients.find(client => client.id === clientId)?.name || ''} edits={rows[0].edits} onEdit={rows[0].onEdit} rows={rows} presentOnly={!advanced} onRowSelect={key => setActiveEmployeeId(Number(key))} />
      <div className="salary-present-explanation">Present-day edits are temporary. Known saved paid-credit / extra-duty differences are retained; a payable-only source keeps its separately reviewed salary units. Without saved attendance, payable days follow your present-days entry as a calendar-month what-if; no leave or weekly-off entitlement is invented.</div>
      {activeEmployee && <section className="salary-present-selected" aria-label="Selected employee calculation details"><div><strong>{activeEmployee.code} · {activeEmployee.name}</strong><span>{activeState?.loading ? 'Recalculating' : activeState?.current && result?.complete ? 'Ready' : 'Needs input or review'}</span></div>
        {activeState?.error && <Alert type="error" showIcon message={activeState.error} />}
        {isMissingAttendance(activeState) && <Alert type="info" showIcon message="Enter Present days in this employee’s yellow cell to calculate a temporary salary scenario." description="No attendance is saved for this month. Recording attendance and running payroll remain separate actions." />}
        {activeIssues.map((issue, index) => <Alert key={index} type={issue.severity === 'Error' ? 'error' : 'warning'} showIcon message={`${issue.component}: ${issue.message}`} description={issue.action} />)}
        {result?.complete && activeState?.current && <div className="simulation-totals">{([['Gross earned', result.gross], ['Deductions', result.deductions], ['Employer benefits', result.benefits], ['Net payable', result.net]] as const).map(([label, value]) => <div key={label}><span>{label}</span><strong>{value == null ? 'Not supplied' : money(value)}</strong></div>)}</div>}
        {result && <Collapse><Collapse.Panel key="notes" header={`Calculation notes (${result.issues.filter(issue => issue.severity === 'Info').length})`}>{result.issues.filter(issue => issue.severity === 'Info').map((issue, index) => <p key={index}><b>{issue.component}:</b> {issue.message} {issue.action}</p>)}</Collapse.Panel><Collapse.Panel key="inputs" header="Resolved inputs and attendance source"><p>{result.attendanceEvidence?.explanation || result.coverage}</p><div className="simulation-inputs">{result.inputs.map(value => <div key={value.name}><strong>{value.name}</strong><span>{value.value ?? 'Not supplied'} {value.unit}</span><small>{value.source}</small></div>)}</div></Collapse.Panel></Collapse>}
      </section>}
    </>}
  </Modal>
}
