import { clientChoice, clientSelectProps } from './ClientOption'
import { useEffect, useState } from 'react'
import { Alert, Button, Collapse, Modal, Select, Spin, Tag } from 'antd'
import type { Client, Component, Structure } from '../types/payroll'
import { simulateSalary, type SimulationResult, type SourceSalaryScenario } from '../services/payrollCalculationService'
import EmployeePayrollSimulation from './EmployeePayrollSimulation'
import SalarySimulationWorksheet from './SalarySimulationWorksheet'
import { simulationUsesCtc } from '../utils/salarySimulationWorksheet'
import { calendarDaysForSimulation } from '../utils/employeePresentSimulation'
import { roundPayroll } from '../utils/payrollFormula'
import SalarySimulationPayslipPreview from './SalarySimulationPayslipPreview'
import type { SimulationPayslipDesign } from '../utils/simulationConfiguredPayslip'
import './PayrollSimulation.css'
import './SalaryFormControls.css'

export type PayrollSimulationProps = { template: Structure; components: Component[]; clients: Client[]; fixedClientId?: number; clientEmployeeCounts?: Record<number, number>; sourceScenario?: SourceSalaryScenario; preferEmployeeSalary?: boolean; payslipDesign?: SimulationPayslipDesign; onClose: () => void }
export default function PayrollSimulation(props: PayrollSimulationProps) {
  const [employeeMode, setEmployeeMode] = useState(!!props.preferEmployeeSalary && !props.sourceScenario)
  return employeeMode && !props.sourceScenario
    ? <EmployeePayrollSimulation {...props} onTemplateMode={() => setEmployeeMode(false)} />
    : <TemplatePayrollSimulation {...props} onEmployeeMode={props.sourceScenario ? undefined : () => setEmployeeMode(true)} />
}
const convertedCtc = (value: string, factor: number) => {
  const number = value.trim() ? Number(value) : NaN
  return Number.isFinite(number) && number >= 0 && Number.isFinite(number * factor) ? String(roundPayroll(number * factor)) : ''
}
function TemplatePayrollSimulation({ template, components, clients, fixedClientId, sourceScenario, payslipDesign, onClose, onEmployeeMode }: PayrollSimulationProps & { onEmployeeMode?: () => void }) {
  const ids = String(template.clientId || '').split(/[;,|]/).map(value => Number(value.split(':')[0])).filter(value => value > 0)
  const [clientId, setClientId] = useState<number | undefined>(fixedClientId || (ids.length === 1 ? ids[0] : undefined))
  const [initialMonth] = useState(() => {
    const sourcePeriod = sourceScenario && (sourceScenario.payPeriod || template.excelSource?.payPeriod)
    if (sourcePeriod) return sourcePeriod
    const date = new Date()
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`
  })
  const [month, setMonth] = useState(initialMonth)
  const initialEdits = (): Record<string, string> => {
    const annualCtc = sourceScenario ? String(sourceScenario.annualCtc) : template.annualCtc.trim()
    return { ANNUAL_CTC: annualCtc, MONTHLY_CTC: convertedCtc(annualCtc, 1 / 12), SCENARIO_EMPLOYEE_CODE: 'EMPLOYEE-001',
    ...Object.fromEntries(Object.entries(sourceScenario?.amountOverrides ?? {}).map(([id, value]) => [`COMPONENT:${id}`, String(value)])) }
  }
  const [edits, setEdits] = useState<Record<string, string>>(() => initialEdits()), [rules, setRules] = useState<Record<string, string>>({})
  const editInput = (field: string, value: string) => {
    if (field === 'ANNUAL_CTC' || field === 'MONTHLY_CTC') {
      const amount = value.replaceAll(',', '')
      setEdits(current => ({ ...current, [field]: amount,
        ...(field === 'ANNUAL_CTC' ? { MONTHLY_CTC: convertedCtc(amount, 1 / 12) } : { ANNUAL_CTC: convertedCtc(amount, 12) }) }))
      return
    }
    setEdits(current => ({ ...current, [field]: value }))
  }
  const changeMonth = (period: string) => {
    setMonth(period)
    setEdits(current => ({ ...current, PAYROLL_DAYS: '' }))
  }
  const [retry, setRetry] = useState(0), [loading, setLoading] = useState(false)
  const [state, setState] = useState<{ key: string; scope: string; result: SimulationResult | null; error: string }>({ key: '', scope: '', result: null, error: '' })
  const calendarDays = calendarDaysForSimulation(month)
  const monthScenario = month === initialMonth ? sourceScenario : undefined
  const savedDays = monthScenario?.payrollDays ?? (template.excelSource?.payPeriod === month ? template.excelSource.payrollDays : undefined)
  const defaultDays = savedDays !== undefined && Number.isInteger(savedDays) && savedDays >= 1 && savedDays <= 31 ? savedDays : calendarDays
  const days = edits.PAYROLL_DAYS?.trim() ? Number(edits.PAYROLL_DAYS) : defaultDays
  const presentText = edits.PRESENT_DAYS ?? String(monthScenario?.presentDays ?? days ?? '')
  const attendanceEdits: Record<string, string> = { ...edits, PRESENT_DAYS: presentText, PAYABLE_DAYS: edits.PAYABLE_DAYS ?? String(monthScenario?.payableDays ?? presentText) }
  const numeric = (key: string) => attendanceEdits[key]?.trim() ? Number(attendanceEdits[key]) : null
  const payable = numeric('PAYABLE_DAYS'), present = numeric('PRESENT_DAYS') ?? payable, annual = numeric('ANNUAL_CTC'), monthlyCtc = numeric('MONTHLY_CTC')
  const overrides = Object.fromEntries(Object.entries(edits).filter(([key, value]) => key.startsWith('COMPONENT:') && value.trim() !== '').map(([key, value]) => [key.slice(10), Number(value)]))
  const valid = !!clientId && calendarDays !== null && days !== null && payable !== null && Number.isInteger(days) && days >= 1 && days <= 31
    && [present, payable, annual, monthlyCtc, ...Object.values(overrides)].every(value => value === null || Number.isFinite(value) && value >= 0)
    && (annual === null || annual <= 1000000000)
    && present !== null && present <= days && payable <= (sourceScenario?.reviewedExtraDutyUnits ? 999.99 : days)
  const draft = { ...template, lines: template.lines.map(line => Object.hasOwn(rules, String(line.componentId)) ? { ...line, value: '', calculationType: 'Formula', formula: rules[String(line.componentId)] } : line) }
  const snapshot = sourceScenario ? { ...sourceScenario.snapshot, lines: sourceScenario.snapshot.lines.map(line => Object.hasOwn(rules, line.componentId) ? { ...line, value: '', calculationType: 'Formula', formula: rules[line.componentId] } : line) } : undefined
  const scope = `${clientId}:${month}`
  const key = JSON.stringify({ clientId, month, payrollDays: days, edits, draft, components, snapshot, retry })
  const responseCurrent = state.key === key, current = responseCurrent && !state.error, result = state.scope === scope ? state.result : null
  useEffect(() => {
    if (!valid || !clientId) { setLoading(false); return }
    let active = true
    setLoading(true)
    const timer = window.setTimeout(() => {
      void simulateSalary({ mode: 'Template', clientId, template: draft, components,
        ...(snapshot ? { snapshot, reviewedExtraDutyUnits: sourceScenario?.reviewedExtraDutyUnits } : {}), payPeriod: month,
        payrollDays: days, presentDays: present, payableDays: payable, annualCtc: annual, amountOverrides: overrides }).then(response => {
        if (!active) return
        setLoading(false)
        setState(previous => ({ key, scope, result: response.ok && response.data ? response.data : previous.scope === scope ? previous.result : null, error: response.ok && response.data ? '' : response.error || 'Simulation returned no result.' }))
      })
    }, 400)
    return () => { active = false; window.clearTimeout(timer) }
    // The exact serialized snapshot is the request identity; stale responses are discarded.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, valid])
  const changeRule = (componentId: string, formula: string) => { setRules(current => ({ ...current, [componentId]: formula.trim().replace(/^=/, '') })); setEdits(current => Object.fromEntries(Object.entries(current).filter(([key]) => key !== `COMPONENT:${componentId}`))) }
  const previewLines = template.lines.map(line => {
    const component = components.find(item => String(item.id) === String(line.componentId))
    const method = line.calculationType?.trim() || component?.calculationType || ''
    const expression = [line.formula, line.value, component?.formula, component?.value].find(value => value?.trim())?.trim() || ''
    const base = line.baseComponent?.trim() || component?.baseComponent?.trim() || (/^(?:Residual \/ Balancing|Balancing Amount)$/i.test(method) ? 'GROSS' : '')
    return `${expression} ${base} ${/^Percentage of CTC$/i.test(method) ? 'CTC' : ''}`
  }).join(' ')
  const usesCtc = simulationUsesCtc(result) || /\b(?:CTC|GROSS|MONTHLY_CTC|ANNUAL_CTC)\b/i.test(previewLines)
  const worksheetResult: SimulationResult = result ?? { mode: 'Template', clientId, payPeriod: month, templateId: String(draft.id || ''), employee: 'Template scenario',
    complete: false, coverage: '', issues: [], inputs: [
      { name: 'PAYROLL_DAYS', value: days, source: 'Temporary scenario input', unit: 'Days' },
      { name: 'PRESENT_DAYS', value: present, source: 'Temporary scenario input', unit: 'Days' },
      { name: 'PAYABLE_DAYS', value: payable, source: 'Temporary scenario input', unit: 'Days' },
      { name: 'ANNUAL_CTC', value: annual, source: 'Temporary scenario input', unit: 'Amount' }],
    lines: [], gross: null, deductions: null, benefits: null, net: null }
  return <Modal open title={`Payroll worksheet · ${template.name}`} width={1400} zIndex={1600} className="payroll-simulation salary-form-controls salary-worksheet-modal" onCancel={onClose} footer={<><SalarySimulationPayslipPreview mode="Template" design={payslipDesign} result={result} template={draft} components={components} clientId={clientId} clientName={clients.find(client => client.id === clientId)?.name || ''} month={month} current={current && valid} loading={loading} employeeCode={edits.SCENARIO_EMPLOYEE_CODE || 'EMPLOYEE-001'} employeeName={edits.SCENARIO_EMPLOYEE_NAME || 'Template scenario'} /><Button onClick={onClose}>Close</Button></>}>
    <div className="simulation-run"><Tag color="purple">Template draft · what-if only</Tag>{onEmployeeMode && <Button onClick={event => { event.currentTarget.blur(); onEmployeeMode() }}>Use employee & saved attendance</Button>}<Button onClick={() => { setEdits(initialEdits()); setRules({}); setRetry(value => value + 1) }}>Reset worksheet</Button></div>
    <div className="payroll-simulation-controls">
      {!fixedClientId && <label className="simulation-field">Client<Select aria-label="Simulation client" value={clientId} {...clientSelectProps(clients.filter(client => !ids.length || ids.includes(client.id)).map(clientChoice))} onChange={setClientId} /></label>}
      <label className="simulation-field">Pay month<input aria-label="Template simulation month" type="month" value={month} onChange={event => changeMonth(event.target.value)} /><small>{calendarDays ?? '—'} calendar days · clear the divisor to use the default for this month</small></label>
      <div className="simulation-field"><span>Template</span><b>{template.name}</b></div>
    </div>
    <p className="salary-worksheet-context-note">Enter monthly amounts / daily rates and attendance directly in the highlighted inputs. Each calculated component uses this salary template’s displayed formula; totals recalculate with the payroll engine. Nothing is saved.</p>
    {!sourceScenario && <p className="salary-worksheet-context-note">Payable days follow Present days until you edit Payable days separately.</p>}
    {!valid && <Alert type="error" message="Check worksheet inputs" description="Select a month, use a whole payroll divisor from 1 to 31, and check the attendance days and amounts against it." />}
    {loading && <p role="status"><Spin size="small" /> Recalculating worksheet with payroll engine…</p>}
    {responseCurrent && state.error && <Alert type="error" showIcon message={state.error} />}
    {usesCtc && <div className="salary-worksheet-ctc-pair"><label className="simulation-field">Annual CTC<input aria-label="Template scenario annual CTC" inputMode="decimal" value={edits.ANNUAL_CTC ?? ''} placeholder="Enter annual CTC" onChange={event => editInput('ANNUAL_CTC', event.target.value)} /></label><label className="simulation-field">Monthly CTC<input aria-label="Template scenario monthly CTC" inputMode="decimal" value={edits.MONTHLY_CTC ?? ''} placeholder="Enter monthly CTC" onChange={event => editInput('MONTHLY_CTC', event.target.value)} /></label><small>Edit either amount. Annual = monthly × 12; monthly = annual ÷ 12. Net pay is calculated separately.</small></div>}
    <SalarySimulationWorksheet result={worksheetResult} template={draft} components={components} defaultPayrollDays={defaultDays} employeeCode={edits.SCENARIO_EMPLOYEE_CODE || 'EMPLOYEE-001'} employeeName={edits.SCENARIO_EMPLOYEE_NAME || 'Template scenario'} month={month} clientName={clients.find(client => client.id === clientId)?.name || ''} edits={attendanceEdits} onEdit={editInput} onRuleEdit={changeRule} stale={!current} />
    {Object.keys(rules).length > 0 && <Alert type="info" message="Formula changes are temporary and apply only to this worksheet. Save template changes separately in the designer after review." />}
    {result && <>
      {result.issues.filter(issue => issue.severity !== 'Info').map((issue, index) => <Alert key={index} type={issue.severity === 'Error' ? 'error' : 'warning'} showIcon message={`${issue.component}: ${issue.message}`} description={issue.action} />)}
      {result.issues.some(issue => issue.severity === 'Info') && <Collapse><Collapse.Panel key="notes" header={`Calculation notes (${result.issues.filter(issue => issue.severity === 'Info').length})`}>{result.issues.filter(issue => issue.severity === 'Info').map((issue, index) => <p key={index}><b>{issue.component}:</b> {issue.message} {issue.action}</p>)}</Collapse.Panel></Collapse>}
    </>}
  </Modal>
}
