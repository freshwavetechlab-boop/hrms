import { useEffect, useState } from 'react'
import { Alert, Button, Collapse, Modal, Select, Spin, Tag } from 'antd'
import type { Client, Component, Structure } from '../types/payroll'
import { simulateSalary, type SimulationResult, type SourceSalaryScenario } from '../services/payrollCalculationService'
import EmployeePayrollSimulation from './EmployeePayrollSimulation'
import SalarySimulationWorksheet from './SalarySimulationWorksheet'
import { simulationUsesCtc } from '../utils/salarySimulationWorksheet'
import './PayrollSimulation.css'
import './SalaryFormControls.css'

export type PayrollSimulationProps = { template: Structure; components: Component[]; clients: Client[]; fixedClientId?: number; sourceScenario?: SourceSalaryScenario; preferEmployeeSalary?: boolean; onClose: () => void }
export default function PayrollSimulation(props: PayrollSimulationProps) {
  const [employeeMode, setEmployeeMode] = useState(!!props.preferEmployeeSalary && !props.sourceScenario)
  return employeeMode && !props.sourceScenario
    ? <EmployeePayrollSimulation {...props} onTemplateMode={() => setEmployeeMode(false)} />
    : <TemplatePayrollSimulation {...props} onEmployeeMode={props.sourceScenario ? undefined : () => setEmployeeMode(true)} />
}
function TemplatePayrollSimulation({ template, components, clients, fixedClientId, sourceScenario, onClose, onEmployeeMode }: PayrollSimulationProps & { onEmployeeMode?: () => void }) {
  const ids = String(template.clientId || '').split(/[;,|]/).map(value => Number(value.split(':')[0])).filter(value => value > 0)
  const [clientId, setClientId] = useState<number | undefined>(fixedClientId || (ids.length === 1 ? ids[0] : undefined))
  const [month, setMonth] = useState(() => { if (sourceScenario?.payPeriod) return sourceScenario.payPeriod; const date = new Date(); return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}` })
  const initialEdits = () => ({ PAYROLL_DAYS: String(sourceScenario?.payrollDays ?? 30), PAYABLE_DAYS: String(sourceScenario?.payableDays ?? 30),
    ...(sourceScenario ? { PRESENT_DAYS: String(sourceScenario.presentDays), ANNUAL_CTC: String(sourceScenario.annualCtc) } : template.annualCtc.trim() ? { ANNUAL_CTC: template.annualCtc } : {}),
    ...Object.fromEntries(Object.entries(sourceScenario?.amountOverrides ?? {}).map(([id, value]) => [`COMPONENT:${id}`, String(value)])) })
  const [edits, setEdits] = useState<Record<string, string>>(initialEdits), [rules, setRules] = useState<Record<string, string>>({})
  const [retry, setRetry] = useState(0), [loading, setLoading] = useState(false)
  const [state, setState] = useState<{ key: string; scope: string; result: SimulationResult | null; error: string }>({ key: '', scope: '', result: null, error: '' })
  const numeric = (key: string) => !edits[key]?.trim() ? null : Number(edits[key])
  const days = numeric('PAYROLL_DAYS'), payable = numeric('PAYABLE_DAYS'), present = numeric('PRESENT_DAYS') ?? payable, annual = numeric('ANNUAL_CTC')
  const overrides = Object.fromEntries(Object.entries(edits).filter(([key, value]) => key.startsWith('COMPONENT:') && value.trim() !== '').map(([key, value]) => [key.slice(10), Number(value)]))
  const valid = !!clientId && days !== null && payable !== null && Number.isInteger(days) && days >= 1 && days <= 31
    && [present, payable, annual, ...Object.values(overrides)].every(value => value === null || Number.isFinite(value) && value >= 0)
    && present !== null && present <= days && payable <= (sourceScenario?.reviewedExtraDutyUnits ? 999.99 : days)
  const draft = { ...template, lines: template.lines.map(line => Object.hasOwn(rules, String(line.componentId)) ? { ...line, value: '', calculationType: 'Formula', formula: rules[String(line.componentId)] } : line) }
  const snapshot = sourceScenario ? { ...sourceScenario.snapshot, lines: sourceScenario.snapshot.lines.map(line => Object.hasOwn(rules, line.componentId) ? { ...line, value: '', calculationType: 'Formula', formula: rules[line.componentId] } : line) } : undefined
  const scope = `${clientId}:${month}`
  const key = JSON.stringify({ clientId, month, edits, draft, components, snapshot, retry })
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
  const previewLines = template.lines.map(line => { const component = components.find(item => String(item.id) === String(line.componentId)); return `${line.formula || line.value || component?.formula || component?.value || ''} ${line.baseComponent || component?.baseComponent || ''}` }).join(' ')
  const usesCtc = simulationUsesCtc(result) || /\b(?:CTC|GROSS|MONTHLY_CTC|ANNUAL_CTC)\b/i.test(previewLines)
  return <Modal open title={`Payroll worksheet · ${template.name}`} width={1400} zIndex={1600} className="payroll-simulation salary-form-controls salary-worksheet-modal" onCancel={onClose} footer={<Button onClick={onClose}>Close</Button>}>
    <div className="simulation-run"><Tag color="purple">Template draft · what-if only</Tag>{onEmployeeMode && <Button onClick={event => { event.currentTarget.blur(); onEmployeeMode() }}>Use employee & saved attendance</Button>}<Button onClick={() => { setEdits(initialEdits()); setRules({}); setRetry(value => value + 1) }}>Reset worksheet</Button></div>
    <div className="payroll-simulation-controls">
      {!fixedClientId && <label className="simulation-field">Client<Select aria-label="Simulation client" value={clientId} options={clients.filter(client => !ids.length || ids.includes(client.id)).map(client => ({ value: client.id, label: client.name }))} onChange={setClientId} /></label>}
      <label className="simulation-field">Pay month<input aria-label="Template simulation month" type="month" value={month} onChange={event => setMonth(event.target.value)} /></label>
      <div className="simulation-field"><span>Template</span><b>{template.name}</b></div>
    </div>
    <p className="salary-worksheet-context-note">Select a highlighted input cell and change its value in the formula bar. All dependent values recalculate with the native engine. Template mode tests draft rules; employee policies and saved attendance are available in employee mode.</p>
    {!valid && <Alert type="error" message="Check worksheet inputs" description="Payroll days must be 1–31. Enter valid non-negative days and amounts; blank present days in this hypothetical template scenario follow payable days." />}
    {loading && <p role="status"><Spin size="small" /> Recalculating worksheet with payroll engine…</p>}
    {responseCurrent && state.error && <Alert type="error" showIcon message={state.error} />}
    {result && <>
      {result.issues.filter(issue => issue.severity !== 'Info').map((issue, index) => <Alert key={index} type={issue.severity === 'Error' ? 'error' : 'warning'} showIcon message={`${issue.component}: ${issue.message}`} description={issue.action} />)}
      <SalarySimulationWorksheet result={result} template={draft} components={components} employeeName="Template scenario" month={month} clientName={clients.find(client => client.id === clientId)?.name || ''} edits={edits} onEdit={(field, value) => setEdits(current => ({ ...current, [field]: value }))} onRuleEdit={changeRule} stale={!current} />
      <Collapse><Collapse.Panel key="notes" header={`Calculation notes (${result.issues.filter(issue => issue.severity === 'Info').length})`}>{result.issues.filter(issue => issue.severity === 'Info').map((issue, index) => <p key={index}><b>{issue.component}:</b> {issue.message} {issue.action}</p>)}</Collapse.Panel><Collapse.Panel key="context" header="Scenario inputs & native calculation coverage"><p>{result.coverage}</p><div className="payroll-simulation-controls">
        {(['PAYROLL_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS', ...(usesCtc ? ['ANNUAL_CTC'] : [])]).map(field => <label className="simulation-field" key={field}>{field.replace(/_/g, ' ')}<input aria-label={`Template scenario ${field}`} inputMode="decimal" value={edits[field] ?? ''} placeholder={field === 'PRESENT_DAYS' ? 'Same as payable days (scenario)' : 'Required input'} onChange={event => setEdits(current => ({ ...current, [field]: event.target.value }))} /></label>)}
      </div>{Object.entries(rules).length > 0 && <Alert type="info" message="Formula changes are temporary and apply only to this worksheet. Save template changes separately in the designer after review." />}</Collapse.Panel></Collapse>
    </>}
    {!result && <section className="salary-worksheet-empty"><h3>Worksheet inputs</h3><div className="payroll-simulation-controls">{(['PAYROLL_DAYS', 'PRESENT_DAYS', 'PAYABLE_DAYS', ...(usesCtc ? ['ANNUAL_CTC'] : [])]).map(field => <label className="simulation-field" key={field}>{field.replace(/_/g, ' ')}<input aria-label={`Template scenario ${field}`} inputMode="decimal" value={edits[field] ?? ''} placeholder={field === 'PRESENT_DAYS' ? 'Same as payable days (scenario)' : 'Required input'} onChange={event => setEdits(current => ({ ...current, [field]: event.target.value }))} /></label>)}</div></section>}
  </Modal>
}
