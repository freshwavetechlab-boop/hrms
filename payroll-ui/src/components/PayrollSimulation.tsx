import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Collapse, Input, Modal, Select, Tag } from 'antd'
import { CalculatorOutlined, DeleteOutlined } from '@ant-design/icons'
import type { Client, Component, Structure } from '../types/payroll'
import { calculateSalaryDetails, calculateSalaryTotals } from '../utils/salary'
import { simulateSalary, type SimulationResult } from '../services/payrollCalculationService'
import SalaryFieldHelp from './SalaryFieldHelp'
import './PayrollSimulation.css'
import './SalaryFormControls.css'

const amount = (n: number | null) => n === null || !Number.isFinite(n) ? '-' : n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const numeric = (s: string) => s.trim() === '' ? null : Number(s)
export default function PayrollSimulation({ template, components, clients, fixedClientId, onClose }: { template: Structure; components: Component[]; clients: Client[]; fixedClientId?: number; onClose: () => void }) {
  const ids = String(template.clientId || '').split(/[;,|]/).map(s => Number(s.split(':')[0])).filter(n => n > 0)
  const [clientId, setClientId] = useState<number | undefined>(fixedClientId || (ids.length === 1 ? ids[0] : undefined))
  const [days, setDays] = useState('30'), [present, setPresent] = useState(''), [payable, setPayable] = useState('30'), [ctc, setCtc] = useState(String(template.annualCtc || ''))
  const [overrides, setOverrides] = useState<Record<string, string>>({}), [overrideId, setOverrideId] = useState<string>()
  const [result, setResult] = useState<SimulationResult | null>(null), [resultKey, setResultKey] = useState(''), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const requestId = useRef(0)
  useEffect(() => () => { requestId.current++ }, [])
  const key = JSON.stringify({ clientId, days, present, payable, ctc, overrides, template, components })
  const stale = !!result && resultKey !== key
  const invalid = [days, present, payable, ctc, ...Object.values(overrides)].some(s => s.trim() !== '' && (!Number.isFinite(Number(s)) || Number(s) < 0)) || (days.trim() !== '' && (!Number.isInteger(Number(days)) || Number(days) < 1 || Number(days) > 31)) || Object.values(overrides).some(s => s.trim() === '')
  const available = components.filter(c => template.lines.some(l => String(l.componentId) === String(c.id)))
  const effectivePresent = !present.trim() ? payable : present
  const previewRows = calculateSalaryDetails(Number(ctc || 0), components, template, overrides, { payrollDays: Number(days), payableDays: Number(payable), presentDays: Number(effectivePresent) })
  const previewIssues = previewRows.filter(r => r.error).map(r => `${r.component.code}: ${r.error}`)
  if (invalid || !days.trim() || !payable.trim()) previewIssues.push('Enter valid payroll and payable days. Zero payable days is allowed.')
  if (template.lines.some(l => !available.some(c => c.active && String(c.id) === String(l.componentId)))) previewIssues.push('The template includes a missing or inactive component.')
  const previewTotals = calculateSalaryTotals(previewRows.map(r => ({ ...r, monthly: r.earned ?? r.monthly })))
  const previewValid = previewIssues.length === 0 && previewRows.length > 0
  const totals = (gross: number | null, deductions: number | null, benefits: number | null, net: number | null) => <div className="simulation-totals">{([['Gross earned', gross], ['Deductions', deductions], ['Employer benefits', benefits], ['Net payable', net]] as const).map(([label, value]) => <div key={label}><span>{label}</span><strong>Rs {amount(value)}</strong></div>)}</div>
  const table = (rows: { code: string; name: string; category: string; formula: string; monthly: number; earned: number; proRata: boolean; error?: string }[]) => <div className="simulation-table-scroll" tabIndex={0} role="region" aria-label="Component-wise salary calculation"><table className="simulation-table"><thead><tr><th>Component</th><th>Type</th><th>Formula / value</th><th>Monthly</th><th>Earned</th><th>Pro-rata</th></tr></thead><tbody>{rows.map((r, i) => <tr key={`${r.code}-${i}`}><td><strong>{r.code}</strong><small>{r.name}</small></td><td>{r.category}</td><td><code>{r.formula || '-'}</code>{r.error && <small className="simulation-row-error">{r.error}</small>}</td><td className="simulation-number">{r.error ? '-' : `Rs ${amount(r.monthly)}`}</td><td className="simulation-number">{r.error ? '-' : `Rs ${amount(r.earned)}`}</td><td>{r.proRata ? 'Yes' : 'No'}</td></tr>)}</tbody></table>{!rows.length && <p>No components in this template.</p>}</div>
  const run = async () => {
    if (!clientId || invalid || !previewValid) return
    const id = ++requestId.current, snapshotKey = key; setBusy(true); setError(''); setResult(null)
    const response = await simulateSalary({ mode: 'Template', clientId, template, components, payPeriod: '', payrollDays: numeric(days), presentDays: numeric(effectivePresent), payableDays: numeric(payable), annualCtc: numeric(ctc), amountOverrides: Object.fromEntries(Object.entries(overrides).map(([id, v]) => [id, Number(v)])) })
    if (id !== requestId.current) return
    setBusy(false); if (response.ok && response.data) { setResult(response.data); setResultKey(snapshotKey) } else setError(response.error || 'Simulation returned no result.')
  }
  return <Modal open title={`Salary simulation: ${template.name}`} width={1200} zIndex={1600} className="payroll-simulation salary-form-controls" onCancel={onClose} footer={<Button onClick={onClose}>Close</Button>}>
    <div className="payroll-simulation-controls">
      {([['Payroll days', days, setDays], ['Payable days', payable, setPayable]] as const).map(([label, value, set]) => <label className="simulation-field" key={label}><span>{label}<SalaryFieldHelp label={`Simulation ${label}`} text="Template values recalculate when you change days. Zero payable days means no payable attendance." example="Wages 15,414 x 14 payable days / 30 payroll days = 7,193.20." /></span><Input aria-label={`Simulation ${label}`} inputMode="decimal" value={value} placeholder="Required input" onChange={e => set(e.target.value)} /></label>)}
    </div>
    <section className="simulation-preview" aria-label="Template calculation preview">
      {previewIssues.length > 0 && <Alert type="error" showIcon message="Check simulation inputs" description={previewIssues.join(' ')} />}
      {totals(previewValid ? previewTotals.gross : null, previewValid ? previewTotals.deductions : null, previewValid ? previewTotals.employerCost : null, previewValid ? previewTotals.net : null)}
      {table(previewRows.map(r => ({ code: r.component.code, name: r.component.name, category: r.component.category, formula: r.dailyRate !== undefined ? `${r.dailyRate} x ${r.units} payable days` : r.source || r.calculationType || '', monthly: r.monthly, earned: r.earned ?? r.monthly, proRata: !!r.proRata, error: r.error || (!previewValid ? 'Check simulation inputs' : undefined) })))}
    </section>
    <Collapse className="simulation-options"><Collapse.Panel key="options" header="Additional inputs and payroll verification">
      <div className="payroll-simulation-controls">
        <label className="simulation-field">Verification client<Select aria-label="Simulation client" disabled={!!fixedClientId} value={clientId} dropdownStyle={{ zIndex: 1700 }} options={clients.filter(c => !ids.length || ids.includes(c.id)).map(c => ({ value: c.id, label: c.name }))} onChange={setClientId} /></label>
        {([['Present days', present, setPresent], ['Annual CTC', ctc, setCtc]] as const).map(([label, value, set]) => <label className="simulation-field" key={label}><span>{label}<SalaryFieldHelp label={`Simulation ${label}`} text={label === 'Present days' ? 'For formulas using PRESENT_DAYS. Blank follows payable days in template simulation.' : 'Annual salary target used by CTC formulas. This temporary input does not change the template.'} example={label === 'Present days' ? '20 present days plus 2 paid leave days can mean 22 payable days.' : '300000 annually supplies a monthly target of 25000.'} /></span><Input aria-label={`Simulation ${label}`} inputMode="decimal" value={value} placeholder={label === 'Present days' ? 'Same as payable days' : 'Annual target'} onChange={e => set(e.target.value)} /></label>)}
      </div>
    <Collapse><Collapse.Panel key="amounts" header="Temporary component amounts"><div className="simulation-overrides"><Select aria-label="Override component" dropdownStyle={{ zIndex: 1700 }} value={overrideId} placeholder="Select component" options={available.map(c => ({ value: String(c.id), label: `${c.code} (${c.calculationType === 'Daily Rate' ? 'per day' : 'full-period amount'})` }))} onChange={setOverrideId} /><Button disabled={!overrideId || Object.hasOwn(overrides, overrideId)} onClick={() => { if (overrideId) setOverrides({ ...overrides, [overrideId]: '' }) }}>Add override</Button></div>{Object.entries(overrides).map(([id, v]) => <div className="simulation-overrides" key={id}><label className="simulation-field">{available.find(c => String(c.id) === id)?.code}<Input aria-label={`Override ${id}`} inputMode="decimal" value={v} onChange={e => setOverrides({ ...overrides, [id]: e.target.value })} /></label><Button aria-label={`Remove override ${id}`} icon={<DeleteOutlined />} onClick={() => setOverrides(Object.fromEntries(Object.entries(overrides).filter(([key]) => key !== id)))} /></div>)}</Collapse.Panel></Collapse>
    <div className="simulation-run"><Button aria-label="Run salary simulation" icon={<CalculatorOutlined />} loading={busy} disabled={!clientId || invalid || !previewValid} onClick={() => void run()}>Verify with payroll engine</Button></div>
    </Collapse.Panel></Collapse>
    {invalid && <Alert type="error" message="Enter valid non-negative numbers. An added override cannot be blank." />}
    {error && <Alert type="error" showIcon message={error} />}
    {stale && <Alert type="warning" showIcon message="Inputs changed. Run simulation again; the result below is from previous inputs." />}
    {result && <div className={stale ? 'simulation-stale' : ''}>
      <Alert type={result.complete ? 'info' : 'error'} message={result.complete ? 'Calculated by payroll engine' : 'Simulation incomplete'} description={result.coverage} />
      {result.issues.map((issue, i) => <Alert key={i} type={issue.severity === 'Error' ? 'error' : issue.severity === 'Warning' ? 'warning' : 'info'} showIcon message={`${issue.component}: ${issue.message}`} description={issue.action} />)}
      {result.complete && totals(result.gross, result.deductions, result.benefits, result.net)}
      {result.lines.length > 0 && table(result.lines.map(line => ({ ...line, name: components.find(c => c.code === line.code)?.name || line.name })))}
      <Collapse><Collapse.Panel key="inputs" header={`Resolved inputs${result.employee ? `: ${result.employee}` : ''}`}><div className="simulation-inputs">{result.inputs.map(input => <div key={input.name}><strong>{input.name}</strong><span>{input.value === null ? 'Missing' : input.value} {input.unit}</span><small>{input.source}</small></div>)}</div></Collapse.Panel></Collapse>
      {result.lines.length > 0 && <Collapse>{result.lines.map((line, i) => <Collapse.Panel key={i} header={<div className="simulation-line-title"><strong>{line.code}</strong><Tag>{line.category}</Tag><span>Rs {amount(line.earned)}</span></div>}><dl className="simulation-trace"><dt>Source</dt><dd>{line.source}</dd><dt>Configured formula / value</dt><dd><code>{line.formula || 'Resolved policy / transaction amount'}</code></dd><dt>Substituted calculation</dt><dd><code>{line.substituted}</code></dd><dt>Before component rounding</dt><dd>{line.raw}</dd><dt>Full-period / resolved amount</dt><dd>{amount(line.monthly)}</dd><dt>Attendance adjustment</dt><dd>{line.proRata ? 'Amount x payable days / payroll days' : 'No second adjustment'}</dd><dt>Earned result</dt><dd>{amount(line.earned)}</dd><dt>Rounding</dt><dd>{line.rounding}</dd></dl>{Object.entries(line.inputs).map(([name, v]) => <Tag key={name}>{name} = {v}</Tag>)}</Collapse.Panel>)}</Collapse>}
    </div>}
  </Modal>
}
