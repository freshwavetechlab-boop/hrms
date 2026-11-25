import { clientChoice, clientSelectProps } from './ClientOption'
import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Collapse, Modal, Select, Spin, Tag } from 'antd'
import { getSimulationClientEmployees, simulateSalary, type SimulationEmployee, type SimulationMode, type SimulationResult } from '../services/payrollCalculationService'
import { getPayRuns } from '../services/payrollService'
import { employeeSimulationInput } from '../utils/employeeSimulationInput'
import { calendarDaysForSimulation, employeePresentScenario } from '../utils/employeePresentSimulation'
import { roundPayroll } from '../utils/payrollFormula'
import { simulationUsesCtc } from '../utils/salarySimulationWorksheet'
import SalarySimulationWorksheet, { type SalaryWorksheetEmployeeRow } from './SalarySimulationWorksheet'
import type { PayrollSimulationProps } from './PayrollSimulation'
import SalarySimulationPayslipPreview from './SalarySimulationPayslipPreview'

type EmployeeMode = Extract<SimulationMode, 'Employee' | 'EmployeeTemplate'>
type RowState = { baseline: SimulationResult | null; result: SimulationResult | null; loading: boolean; current: boolean; error: string; requestKey?: string }
const EMPTY_ROW: RowState = { baseline: null, result: null, loading: true, current: false, error: '' }
const contextKey = (mode: EmployeeMode, clientId: number | undefined, employeeId: number, payPeriod: string, template: PayrollSimulationProps['template'], components: PayrollSimulationProps['components']) => JSON.stringify({ mode, clientId, employeeId, payPeriod, template, components })
const owns = (values: Record<string, string>, key: string) => Object.prototype.hasOwnProperty.call(values, key)
function convertedCtc(value: string, factor: number): string {
  if (!value.trim()) return ''
  const amount = Number(value)
  return Number.isFinite(amount) && amount >= 0 && Number.isFinite(amount * factor) ? String(roundPayroll(amount * factor)) : ''
}
let activeRequests = 0
const requestQueue: (() => void)[] = []
function limited<T>(run: () => Promise<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    const start = () => { activeRequests++; void run().then(resolve, reject).finally(() => { activeRequests--; requestQueue.shift()?.() }) }
    if (activeRequests < 3) start(); else requestQueue.push(start)
  })
}

function EmployeeRowCalculation({ mode, clientId, employeeId, payPeriod, template, components, edits, onState }: {
  mode: EmployeeMode; clientId: number; employeeId: number; payPeriod: string; template: PayrollSimulationProps['template']; components: PayrollSimulationProps['components']; edits: Record<string, string>; onState: (state: RowState) => void;
}) {
  const callback = useRef(onState); callback.current = onState
  const [baseline, setBaseline] = useState<SimulationResult | null>(null)
  const baselineContext = useRef('')
  const lastResult = useRef<SimulationResult | null>(null)
  const context = contextKey(mode, clientId, employeeId, payPeriod, template, components)
  const editsKey = JSON.stringify(edits), requestKey = `${context}:${editsKey}`
  const report = (state: RowState) => callback.current({ ...state, requestKey })
  const matches = (data: SimulationResult) => data.mode === mode && data.clientId === clientId && data.employeeId === employeeId && data.payPeriod === payPeriod && (!data.complete || !template.id || String(data.templateId).split(';').map(id => id.trim()).includes(String(template.id)))
  useEffect(() => {
    baselineContext.current = ''
    if (!calendarDaysForSimulation(payPeriod)) { report({ baseline: null, result: null, loading: false, current: false, error: 'Select a valid payroll month.' }); return }
    if (mode === 'Employee' && !template.id) { report({ baseline: null, result: null, loading: false, current: false, error: 'Choose a saved template for saved employee payroll, or use Current template what-if.' }); return }
    let active = true
    report(EMPTY_ROW)
    void limited(async () => active ? simulateSalary({ mode, clientId, employeeId, template, components, payPeriod, payrollDays: null, presentDays: null, payableDays: null, annualCtc: null, amountOverrides: {} }) : null).then(response => {
      if (!active || !response) return
      if (response.ok && response.data && matches(response.data)) { baselineContext.current = context; lastResult.current = response.data; setBaseline(response.data); report({ baseline: response.data, result: response.data, loading: true, current: false, error: '' }) }
      else report({ baseline: null, result: null, loading: false, current: false, error: response.error || 'Could not load this employee’s effective salary.' })
    }).catch(error => { if (active) report({ baseline: null, result: null, loading: false, current: false, error: String(error) }) })
    return () => { active = false }
  }, [context]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!baseline || baselineContext.current !== context) return
    if (!Object.keys(edits).length) { lastResult.current = baseline; report({ baseline, result: baseline, loading: false, current: true, error: '' }); return }
    if (mode === 'EmployeeTemplate' && ['PRESENT_DAYS', 'PAYABLE_DAYS'].some(key => owns(edits, key) && !edits[key].trim())) {
      report({ baseline, result: lastResult.current, loading: false, current: false, error: 'Enter the cleared attendance days, or reset to saved inputs.' }); return
    }
    const scenario = employeePresentScenario(baseline, payPeriod, edits)
    const payrollDays = mode === 'EmployeeTemplate' ? baseline.inputs.find(value => value.name === 'PAYROLL_DAYS')?.value ?? calendarDaysForSimulation(payPeriod) : scenario.payrollDays
    const input = employeeSimulationInput({ clientId, employeeId, payPeriod, payrollDays: payrollDays === null ? '' : String(payrollDays), presentDays: scenario.presentDays === null ? '' : String(scenario.presentDays), payableDays: scenario.payableDays === null ? '' : String(scenario.payableDays), annualCtc: scenario.annualCtc === null ? '' : String(scenario.annualCtc) })
    const amountOverrides = Object.fromEntries(Object.entries(edits).filter(([key, value]) => key.startsWith('COMPONENT:') && value.trim() !== '').map(([key, value]) => [key.slice(10), Number(value)]))
    const clearedComponentIds = Object.keys(edits).filter(key => key.startsWith('COMPONENT:') && !edits[key].trim()).map(key => key.slice(10))
    const invalidMonthlyCtc = !!edits.MONTHLY_CTC?.trim() && (!Number.isFinite(Number(edits.MONTHLY_CTC)) || Number(edits.MONTHLY_CTC) < 0)
    if (!input || invalidMonthlyCtc || Object.values(amountOverrides).some(value => !Number.isFinite(value) || value < 0)) {
      report({ baseline, result: lastResult.current, loading: false, current: false, error: 'Enter valid days and amounts. This what-if input must respect the saved payroll divisor; source extra-duty and split-period allocations require their reviewed native inputs.' }); return
    }
    let active = true
    report({ baseline, result: lastResult.current, loading: true, current: false, error: '' })
    const timer = window.setTimeout(() => {
      void limited(async () => active ? simulateSalary({ ...input, mode, amountOverrides, template, components, ...(mode === 'EmployeeTemplate' ? { clearedComponentIds, clearAnnualCtc: owns(edits, 'ANNUAL_CTC') && !edits.ANNUAL_CTC.trim() } : {}) }) : null).then(response => {
        if (!active || !response) return
        const valid = response.ok && response.data !== null && matches(response.data)
        if (valid) lastResult.current = response.data
        report({ baseline, result: lastResult.current, loading: false, current: valid, error: valid ? '' : response.error || 'Simulation returned no matching result.' })
      }).catch(error => { if (active) report({ baseline, result: lastResult.current, loading: false, current: false, error: String(error) }) })
    }, 350)
    return () => { active = false; window.clearTimeout(timer) }
  }, [baseline, editsKey, context]) // eslint-disable-line react-hooks/exhaustive-deps
  return null
}

function pendingResult(employee: SimulationEmployee, clientId: number, payPeriod: string, mode: EmployeeMode, template: PayrollSimulationProps['template']): SimulationResult {
  return { mode, clientId, employeeId: employee.id, employeeCode: employee.code, employeeName: employee.name, payPeriod, employee: employee.name, templateId: String(template.id || ''), complete: false, coverage: '', issues: [], inputs: [{ name: 'PRESENT_DAYS', value: null, source: 'Enter a temporary present-days value', unit: 'Days' }], ...(mode === 'EmployeeTemplate' ? { componentInputs: Object.fromEntries(template.lines.map(line => [String(line.componentId), null])) } : {}), lines: [], gross: null, deductions: null, benefits: null, net: null }
}

export default function EmployeePayrollSimulation({ template, components, clients, fixedClientId, payslipDesign, onClose, onTemplateMode }: PayrollSimulationProps & { onTemplateMode: () => void }) {
  const ids = String(template.clientId || '').split(/[;,|]/).map(value => Number(value.split(':')[0])).filter(value => value > 0)
  const [clientId, setClientId] = useState<number | undefined>(fixedClientId || (ids.length === 1 ? ids[0] : undefined))
  const [mode, setMode] = useState<EmployeeMode>('EmployeeTemplate')
  const [selectedEmployees, setSelectedEmployees] = useState<SimulationEmployee[]>([]), [search, setSearch] = useState(''), [searching, setSearching] = useState(false)
  const [roster, setRoster] = useState<{ clientId?: number; search: string; items: SimulationEmployee[]; total: number | null; error: string }>({ search: '', items: [], total: null, error: '' })
  const [payPeriod, setPayPeriod] = useState(() => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` })
  const periodChosen = useRef(false)
  const previousClient = useRef(clientId)
  const [editsByEmployee, setEditsByEmployee] = useState<Record<number, Record<string, string>>>({}), [states, setStates] = useState<Record<number, RowState>>({})
  const [retry, setRetry] = useState(0), [activeEmployeeId, setActiveEmployeeId] = useState<number>(), [advanced, setAdvanced] = useState(false)
  const [searchRetry, setSearchRetry] = useState(0)
  useEffect(() => {
    if (fixedClientId && fixedClientId !== clientId) setClientId(fixedClientId)
  }, [fixedClientId, clientId])
  useEffect(() => {
    if (previousClient.current === clientId) return
    previousClient.current = clientId
    setSelectedEmployees([]); setSearch(''); setStates({}); setEditsByEmployee({}); setActiveEmployeeId(undefined)
    setRoster({ search: '', items: [], total: null, error: '' }); periodChosen.current = false
  }, [clientId])
  useEffect(() => {
    if (mode === 'Employee' && !template.id) { setMode('EmployeeTemplate'); setStates({}); setEditsByEmployee({}) }
  }, [mode, template.id])
  useEffect(() => {
    if (!clientId || mode !== 'Employee') return
    let active = true
    void getPayRuns().then(runs => {
      const latest = runs.filter(run => run.clientId === clientId && run.runType !== 'Off Cycle' && run.employeeCount > 0 && /^\d{4}-(0[1-9]|1[0-2])$/.test(run.payPeriod)).sort((a, b) => b.payPeriod.localeCompare(a.payPeriod))[0]
      if (active && latest && !periodChosen.current) { setPayPeriod(latest.payPeriod); setStates({}); setEditsByEmployee({}) }
    })
    return () => { active = false }
  }, [clientId, mode])
  useEffect(() => {
    if (!clientId) { setSearching(false); return }
    let cancelled = false
    setSearching(true)
    const timer = window.setTimeout(() => {
      void getSimulationClientEmployees(clientId, search).then(response => {
        if (cancelled) return
        setSearching(false)
        if (response.ok && response.data && Array.isArray(response.data.items) && Number.isFinite(response.data.total)) setRoster({ clientId, search, items: response.data.items, total: response.data.total, error: '' })
        else setRoster({ clientId, search, items: [], total: null, error: response.error || 'Employee search is unavailable.' })
      }).catch(error => { if (!cancelled) { setRoster({ clientId, search, items: [], total: null, error: error instanceof Error ? error.message : 'Employee search is unavailable.' }); setSearching(false) } })
    }, 250)
    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [clientId, search, searchRetry])
  const loadedRoster = roster.clientId === clientId && roster.search === search
  const employeeSearchPending = !!clientId && (searching || !loadedRoster)
  const searchError = loadedRoster ? roster.error : ''
  const choices = [...new Map([...(previousClient.current === clientId ? selectedEmployees : []), ...(loadedRoster ? roster.items : [])].map(employee => [employee.id, employee])).values()]
  const rowState = (employeeId: number) => states[employeeId]?.requestKey?.startsWith(`${contextKey(mode, clientId, employeeId, payPeriod, template, components)}:`) ? states[employeeId] : undefined
  const activeEmployee = selectedEmployees.find(employee => employee.id === activeEmployeeId) ?? selectedEmployees[0]
  const activeState = activeEmployee ? rowState(activeEmployee.id) : undefined
  const result = activeState?.result
  const employeeIsCurrent = (employeeId: number) => !!rowState(employeeId)?.current && rowState(employeeId)?.requestKey === `${contextKey(mode, clientId, employeeId, payPeriod, template, components)}:${JSON.stringify(editsByEmployee[employeeId] ?? {})}`
  const edit = (id: number, field: string, value: string) => { if (field === 'PAYROLL_DAYS') return; periodChosen.current = true; setActiveEmployeeId(id); setStates(current => current[id] ? { ...current, [id]: { ...current[id], current: false } } : current); setEditsByEmployee(current => ({ ...current, [id]: { ...current[id], [field]: value } })) }
  const editCtc = (field: 'ANNUAL_CTC' | 'MONTHLY_CTC', value: string) => {
    if (!activeEmployee) return
    const clean = value.replace(/,/g, '')
    edit(activeEmployee.id, field, clean)
    edit(activeEmployee.id, field === 'ANNUAL_CTC' ? 'MONTHLY_CTC' : 'ANNUAL_CTC', convertedCtc(clean, field === 'ANNUAL_CTC' ? 1 / 12 : 12))
  }
  const loading = selectedEmployees.filter(employee => !rowState(employee.id) || rowState(employee.id)?.loading).length
  const complete = selectedEmployees.filter(employee => employeeIsCurrent(employee.id) && rowState(employee.id)?.result?.complete).length
  const rows: SalaryWorksheetEmployeeRow[] = selectedEmployees.map(employee => {
    const state = rowState(employee.id)
    return { key: String(employee.id), result: state?.result ?? pendingResult(employee, clientId!, payPeriod, mode, template), employeeCode: employee.code, employeeName: employee.name, edits: editsByEmployee[employee.id] ?? {}, onEdit: (field, value) => edit(employee.id, field, value), stale: !employeeIsCurrent(employee.id), status: state?.error || (state?.loading ? 'Calculating' : employeeIsCurrent(employee.id) && state?.result?.complete ? 'Ready' : 'Needs input') }
  })
  const activeIssues = result?.issues.filter(issue => issue.severity !== 'Info') ?? []
  const emptyMaster = loadedRoster && !employeeSearchPending && !searchError && roster.total === 0
  const noSearchMatch = loadedRoster && !employeeSearchPending && !searchError && (roster.total ?? 0) > 0 && roster.items.length === 0
  const emptyEmployeeText = searchError ? 'Employee list unavailable. Use Retry below.' : employeeSearchPending ? 'Loading Employee Master…' : emptyMaster ? 'No Employee Master records for this client.' : 'No matching employees. Try another name or code.'
  const activeEdits = activeEmployee ? editsByEmployee[activeEmployee.id] ?? {} : {}
  const savedAnnualCtc = activeState?.baseline?.inputs.find(value => value.name === 'ANNUAL_CTC')?.value ?? null
  const annualCtc = owns(activeEdits, 'ANNUAL_CTC') ? activeEdits.ANNUAL_CTC : savedAnnualCtc === null ? '' : String(savedAnnualCtc)
  const monthlyCtc = owns(activeEdits, 'MONTHLY_CTC') ? activeEdits.MONTHLY_CTC : convertedCtc(annualCtc, 1 / 12)
  const usesCtc = template.lines.some(line => {
    const component = components.find(candidate => String(candidate.id) === String(line.componentId))
    const formula = [line.formula, line.value, component?.formula, component?.value].find(value => value?.trim()) || ''
    const method = line.calculationType?.trim() || component?.calculationType || ''
    const base = line.baseComponent?.trim() || component?.baseComponent?.trim() || (/^(?:Residual \/ Balancing|Balancing Amount)$/i.test(method) ? 'GROSS' : '')
    return /\b(?:GROSS|ANNUAL_CTC|MONTHLY_CTC|CTC)\b/i.test(`${formula} ${base}`) || /^Percentage of CTC$/i.test(method)
  }) || simulationUsesCtc(result ?? null)
  const changeMode = (value: EmployeeMode) => { setMode(value); setStates({}); setEditsByEmployee({}); setAdvanced(false) }
  return <Modal open title={`Payroll worksheet · ${template.name}`} width={1400} zIndex={1600} className="payroll-simulation salary-form-controls salary-worksheet-modal" onCancel={onClose} footer={<><SalarySimulationPayslipPreview mode={mode} design={payslipDesign} result={result} template={template} components={components} clientId={clientId} clientName={clients.find(client => client.id === clientId)?.name || ''} month={payPeriod} employeeId={activeEmployee?.id} employeeCode={activeEmployee?.code} employeeName={activeEmployee?.name} current={previousClient.current === clientId && !!activeEmployee && employeeIsCurrent(activeEmployee.id) && !activeState?.error} loading={!!activeEmployee && (!activeState || activeState.loading)} /><Button onClick={onClose}>Close</Button></>}>
    <div className="salary-present-mode"><div><Tag color="purple">{mode === 'EmployeeTemplate' ? 'Current template · employee what-if' : 'Saved employee payroll simulation'}</Tag><p>{mode === 'EmployeeTemplate' ? 'Select any client employee. Saved inputs load when available; enter missing amounts in the yellow cells. Nothing is assigned or saved.' : 'Check effective saved salary, attendance and payroll assignments for this month.'}</p></div><Button onClick={event => { event.currentTarget.blur(); onTemplateMode() }}>Try template draft instead</Button></div>
    <div className="payroll-simulation-controls salary-present-controls">
      {!fixedClientId && <label className="simulation-field">Client<Select aria-label="Employee simulation client" value={clientId} {...clientSelectProps(clients.filter(client => !ids.length || ids.includes(client.id)).map(clientChoice))} onChange={setClientId} /></label>}
      <label className="simulation-field salary-present-employees">Employees<Select mode="multiple" aria-label="Simulation employees" showSearch filterOption={false} disabled={!clientId} value={previousClient.current === clientId ? selectedEmployees.map(employee => employee.id) : []} loading={employeeSearchPending} maxTagCount="responsive" placeholder="Search this client’s Employee Master" notFoundContent={emptyEmployeeText} options={choices.map(employee => ({ value: employee.id, label: `${employee.code} · ${employee.name}${employee.isActive === false ? ' (Inactive)' : ''}` }))} onSearch={setSearch} onChange={selected => {
        const bounded = selected.slice(0, 25)
        setSelectedEmployees(bounded.map(id => choices.find(employee => employee.id === id)!).filter(Boolean))
        setActiveEmployeeId(current => current && bounded.includes(current) ? current : bounded[0])
        setEditsByEmployee(current => Object.fromEntries(Object.entries(current).filter(([id]) => bounded.includes(Number(id)))))
      }} popupMatchSelectWidth={false} dropdownStyle={{ zIndex: 1700 }} /></label>
      <label className="simulation-field">Pay month<input aria-label="Employee simulation month" type="month" value={payPeriod} onChange={event => { periodChosen.current = true; setPayPeriod(event.target.value); setEditsByEmployee({}); setStates({}) }} /><small>{calendarDaysForSimulation(payPeriod) ?? '—'} calendar days · saved payroll divisor takes priority</small></label>
      <label className="simulation-field">Calculation basis<Select aria-label="Employee calculation basis" value={mode} onChange={changeMode} options={[{ value: 'EmployeeTemplate', label: 'Current template what-if' }, { value: 'Employee', label: 'Saved employee payroll', disabled: !template.id }]} popupMatchSelectWidth={false} dropdownStyle={{ zIndex: 1700 }} /></label>
    </div>
    {loadedRoster && roster.total !== null && !searchError && <p className="salary-worksheet-context-note">{roster.total} Employee Master records for this client.{mode === 'EmployeeTemplate' ? <> Test <b>{template.name}</b> without a salary assignment.</> : ' Saved payroll assignments are checked when you select an employee.'}</p>}
    {searchError && <Alert type="error" showIcon message="Employee list could not be loaded" description={searchError} action={<Button onClick={() => setSearchRetry(value => value + 1)}>Retry</Button>} />}
    {emptyMaster && <Alert type="info" showIcon message="No Employee Master records for this client" description="Add employees first, or use Try template draft instead to test the salary template without an employee." action={<Button href={`/employees/master?clientId=${clientId}`} target="_blank" rel="noopener noreferrer">Open Employee Master</Button>} />}
    {noSearchMatch && <Alert type="info" showIcon message="No matching employees" description="Try another employee name or code. The client has Employee Master records; salary assignments are not required for a current-template what-if." />}
    {!selectedEmployees.length ? <div className="salary-present-empty"><h3>Compare employee salaries in one worksheet</h3><p>Select up to 25 employees. Available salary inputs and attendance load automatically; missing inputs can be entered temporarily.</p></div> : <>
      <div className="salary-present-status"><Tag color="green">{complete} ready</Tag>{loading > 0 && <span role="status"><Spin size="small" /> Calculating {loading}…</span>}<span>{selectedEmployees.length - complete - loading > 0 ? `${selectedEmployees.length - complete - loading} need input or review` : `${selectedEmployees.length} selected`}</span><Button size="small" disabled={loading > 0} onClick={() => { setEditsByEmployee({}); setStates({}); setRetry(value => value + 1) }}>Reset to saved inputs</Button>{mode === 'Employee' && <Button size="small" type="text" onClick={() => setAdvanced(value => !value)}>{advanced ? 'Attendance only' : 'Advanced inputs'}</Button>}</div>
      {clientId && selectedEmployees.map(employee => <EmployeeRowCalculation key={`${mode}:${clientId}:${payPeriod}:${employee.id}:${template.id}:${retry}`} mode={mode} clientId={clientId} employeeId={employee.id} payPeriod={payPeriod} template={template} components={components} edits={editsByEmployee[employee.id] ?? {}} onState={row => setStates(current => ({ ...current, [employee.id]: row }))} />)}
      {activeEmployee && (usesCtc || savedAnnualCtc !== null || owns(activeEdits, 'ANNUAL_CTC')) && <div className="payroll-simulation-controls"><label className="simulation-field">Annual CTC · {activeEmployee.code}<input aria-label="Selected employee annual CTC" inputMode="decimal" value={annualCtc} readOnly={mode === 'Employee' && !advanced} onChange={event => editCtc('ANNUAL_CTC', event.target.value)} /></label><label className="simulation-field">Monthly CTC<input aria-label="Selected employee monthly CTC" inputMode="decimal" value={monthlyCtc} readOnly={mode === 'Employee' && !advanced} onChange={event => editCtc('MONTHLY_CTC', event.target.value)} /><small>Annual ÷ 12 · temporary input for {activeEmployee.name}</small></label></div>}
      <SalarySimulationWorksheet result={rows[0].result} template={template} components={components} month={payPeriod} clientName={clients.find(client => client.id === clientId)?.name || ''} edits={rows[0].edits} onEdit={rows[0].onEdit} rows={rows} presentOnly={mode === 'Employee' && !advanced} onRowSelect={key => setActiveEmployeeId(Number(key))} />
      <div className="salary-present-explanation">{mode === 'EmployeeTemplate' ? 'Only this simulation uses the selected template and your edited amounts. Employee salary assignments, attendance and payroll are not saved.' : 'These inputs are temporary. Saved payroll assignments remain unchanged.'} Known saved paid-credit / extra-duty differences are retained; a payable-only source keeps its reviewed salary units.</div>
      {activeEmployee && <section className="salary-present-selected" aria-label="Selected employee calculation details"><div><strong>{activeEmployee.code} · {activeEmployee.name}</strong><span>{activeState?.loading ? 'Recalculating' : employeeIsCurrent(activeEmployee.id) && result?.complete ? 'Ready' : 'Needs input or review'}</span></div>
        {activeState?.error && <Alert type="error" showIcon message={activeState.error} />}
        {activeIssues.map((issue, index) => <Alert key={index} type={issue.severity === 'Error' ? 'error' : 'warning'} showIcon message={`${issue.component}: ${issue.message}`} description={issue.action} />)}
        {result && result.issues.some(issue => issue.severity === 'Info') && <Collapse><Collapse.Panel key="notes" header={`Calculation notes (${result.issues.filter(issue => issue.severity === 'Info').length})`}>{result.issues.filter(issue => issue.severity === 'Info').map((issue, index) => <p key={index}><b>{issue.component}:</b> {issue.message} {issue.action}</p>)}</Collapse.Panel></Collapse>}
      </section>}
    </>}
  </Modal>
}
