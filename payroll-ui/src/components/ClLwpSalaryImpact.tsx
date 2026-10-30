import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, InputNumber, Select, Space } from 'antd'
import type { ClLwpScenario, simulateClLwp } from '../utils/clLwpSimulation'
import { compareClLwpSalary, getSalarySnapshots, type SalarySnapshot } from '../services/clLwpSalaryImpactService'

type Impact = Awaited<ReturnType<typeof compareClLwpSalary>>
export default function ClLwpSalaryImpact({ scenario, days }: { scenario: ClLwpScenario; days: ReturnType<typeof simulateClLwp> }) {
  const [snapshots, setSnapshots] = useState<SalarySnapshot[]>([]), [selectedId, setSelectedId] = useState(''), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [sourcePresent, setSourcePresent] = useState<number | null>(null), [scenarioPresent, setScenarioPresent] = useState<number | null>(null), [annualCtc, setAnnualCtc] = useState<number | null>(null)
  const [overrides, setOverrides] = useState<Record<string, number | null>>({}), [reviewed, setReviewed] = useState(false), [impact, setImpact] = useState<Impact | null>(null), [resultKey, setResultKey] = useState('')
  const ticket = useRef(0)
  useEffect(() => () => { ticket.current++ }, [])
  const selected = snapshots.find(s => s.id === selectedId)
  const key = JSON.stringify({ scenario, selected, sourcePresent, scenarioPresent, annualCtc, overrides, reviewed })
  const currentKey = useRef(key)
  useEffect(() => { currentKey.current = key }, [key])
  const stale = !!impact && resultKey !== key
  const load = async () => {
    const id = ++ticket.current; setBusy(true); setError('')
    const response = await getSalarySnapshots(scenario.clientId)
    if (id !== ticket.current) return
    setBusy(false); setReviewed(false)
    if (!response.ok) { setError(response.error || 'Salary snapshots unavailable.'); return }
    setSnapshots(response.data); setSelectedId(''); setImpact(null)
  }
  const run = async () => {
    if (!selected || busy || !reviewed || days.errors.length) return
    const id = ++ticket.current, capturedKey = key; setBusy(true); setError('')
    try {
      if (Object.values(overrides).some(v => v == null)) throw Error('Complete or remove each temporary amount override.')
      const result = await compareClLwpSalary({ clientId: scenario.clientId, selected, annualCtc, amountOverrides: overrides as Record<string, number>, source: { payrollDays: scenario.sourceDivisor, presentDays: sourcePresent, payableDays: scenario.sourcePayable }, scenario: { payrollDays: days.divisor, presentDays: scenario.basis === 'Worked' ? scenario.attendanceDays : scenarioPresent, payableDays: days.payable }, isCurrent: () => ticket.current === id && currentKey.current === capturedKey })
      if (ticket.current === id && currentKey.current === capturedKey) { setImpact(result); setResultKey(capturedKey) }
    } catch (e) { if (ticket.current === id) setError(e instanceof Error ? e.message : 'Salary impact unavailable.') }
    finally { if (ticket.current === id) setBusy(false) }
  }
  return <section aria-label="CL LWP salary amount impact">
    <h3>Salary amount impact — main payroll engine</h3>
    <Alert showIcon type="info" message="Template snapshot arithmetic, not Employee payroll certification" description="Choose a saved client template and explicitly review its salary inputs. Source and scenario use the same snapshot in the actual server calculator. Employee overrides, approved adjustments, dated PF/PT/TDS are outside this Template comparison. No policy, balance, attendance or payroll is saved." />
    <Space direction="vertical" style={{ width: '100%' }}>
      <Button disabled={busy} onClick={() => void load()}>Load saved client salary snapshots</Button>
      {snapshots.length > 0 && <Select aria-label="CL LWP salary template snapshot" disabled={busy} style={{ width: '100%' }} placeholder="Choose exact template ID/version" value={selectedId || undefined} options={snapshots.map(s => ({ value: s.id, label: `Template ${s.id} · ${s.snapshotHash.slice(0, 12)}` }))} onChange={id => { setSelectedId(id); setReviewed(false); setOverrides({}); setAnnualCtc(null) }} />}
      {selected && <>
        <p>Snapshot {selected.snapshotHash}. Amount inputs are proposed simulation values; no employee amount is inferred.</p>
        <div className="cl-lwp-simulator-grid">
          <label>Source actual present days<InputNumber aria-label="Salary impact source actual present days" min={0} max={31} step={0.5} value={sourcePresent} onChange={setSourcePresent} /></label>
          {scenario.basis !== 'Worked' && <label>Scenario actual present days (payable days do not prove worked days)<InputNumber aria-label="Salary impact scenario actual present days" min={0} max={31} step={0.5} value={scenarioPresent} onChange={setScenarioPresent} /></label>}
          <label>Explicit annual CTC (if used)<InputNumber aria-label="Salary impact annual CTC" min={0} max={1000000000} value={annualCtc} onChange={setAnnualCtc} /></label>
        </div>
        <details><summary>Review component rules and optional temporary monthly amounts</summary>{selected.snapshot.components.map(c => <div key={c.id}><strong>{c.code}</strong> {c.category} / {c.calculationType}: {c.formula || c.value || 'Missing amount'}<InputNumber aria-label={`Salary impact override ${c.code}`} min={0} max={1000000000} value={overrides[c.id] ?? null} placeholder="No override" onChange={value => setOverrides(current => { const next = { ...current }; if (value == null) delete next[c.id]; else next[c.id] = value; return next })} /></div>)}</details>
        <Checkbox disabled={busy} checked={reviewed} onChange={event => setReviewed(event.target.checked)}>I reviewed this template snapshot, temporary salary inputs and source/scenario day meanings. This does not approve an actual policy.</Checkbox>
        <Button type="primary" loading={busy} disabled={busy || !reviewed || !!days.errors.length} onClick={() => void run()}>Compare salary amounts using main engine</Button>
      </>}
      {error && <Alert type="error" showIcon message={error} />}
      {stale && <Alert type="warning" message="Inputs changed. Previous results are stale; re-simulate before review." />}
      {impact && !stale && <>
        {[impact.source, impact.scenario].map((r, i) => <div key={i}><p>{i ? 'Scenario' : 'Source'}: {r.coverage}</p>{r.issues.map((issue, j) => <Alert key={j} type={issue.severity === 'Error' ? 'error' : 'info'} message={`${issue.component}: ${issue.message}`} description={issue.action} />)}</div>)}
        {impact.difference && <div style={{ overflow: 'auto', maxHeight: 360 }}><table><thead><tr><th>Component / total</th><th>Source</th><th>Scenario</th><th>Delta</th><th>Reason</th></tr></thead><tbody>{impact.difference.lines.map(l => <tr key={`${l.code}:${l.category}`}><td>{l.code} / {l.category}</td><td>{l.source.toFixed(2)}</td><td>{l.scenario.toFixed(2)}</td><td>{l.delta.toFixed(2)}</td><td>{l.reason}<details><summary>Resolved inputs</summary><pre>{JSON.stringify({ source: l.sourceInputs, scenario: l.scenarioInputs }, null, 2)}</pre></details></td></tr>)}{impact.difference.totals.map(t => <tr key={t.key}><th>{t.key}</th><td>{t.source.toFixed(2)}</td><td>{t.scenario.toFixed(2)}</td><td>{t.delta.toFixed(2)}</td><td>Difference of main-engine totals</td></tr>)}</tbody></table></div>}
      </>}
    </Space>
  </section>
}
