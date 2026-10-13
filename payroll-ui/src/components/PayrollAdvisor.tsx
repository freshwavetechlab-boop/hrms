import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Collapse, Modal, Select, Spin, Tag } from 'antd'
import { BulbOutlined, HistoryOutlined, ReloadOutlined } from '@ant-design/icons'
import type { Client, Component, Structure } from '../types/payroll'
import { getPayrollReview, getPayrollReviewHistory, queuePayrollReview, recordPayrollReviewDecision, reviewSnapshot, snapshotKey, type ReviewFinding, type ReviewJob } from '../services/payrollAdvisorService'
import SalaryFieldHelp from './SalaryFieldHelp'
import './PayrollAdvisor.css'

export default function PayrollAdvisor({ components, component, template, clients = [], fixedClientId, onDisableProration }: { components: Component[]; component?: Component; template?: Structure; clients?: Client[]; fixedClientId?: number; onDisableProration: (code: string) => void }) {
  const [open, setOpen] = useState(false), [consent, setConsent] = useState(false), [job, setJob] = useState<ReviewJob | null>(null)
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [history, setHistory] = useState<ReviewJob[]>([]), [decisions, setDecisions] = useState<string[]>([])
  const [pollVersion, setPollVersion] = useState(0)
  const [pendingFinding, setPendingFinding] = useState<ReviewFinding | null>(null)
  const scopeIds = template ? String(template.clientId || '').split(/[;,|]/).map(v => Number(v.split(':')[0])).filter(Boolean) : []
  const [chosenClient, setChosenClient] = useState<number>()
  const clientId = fixedClientId || (scopeIds.includes(chosenClient || 0) ? chosenClient! : scopeIds[0]) || 0
  const snapshot = reviewSnapshot(components, component, template, clientId)
  const key = snapshotKey(snapshot), currentKey = useRef(key)
  currentKey.current = key
  const stale = !!job && (snapshotKey(job.snapshot) !== key || Date.now() - new Date(job.createdAt + (job.createdAt.endsWith('Z') ? '' : 'Z')).getTime() > 86400000)
  const running = !!job && ['Queued', 'Processing'].includes(job.status)
  const ready = !!job && !running && !stale
  const canReview = !!component?.code.trim() || (!!template?.lines.length && clientId > 0)

  useEffect(() => {
    if (!open || !job || !running) return
    let stopped = false
    let timer: ReturnType<typeof setTimeout>
    const poll = async () => {
      const result = await getPayrollReview(job.id)
      if (stopped) return
      if (result.ok && result.data) { setJob(result.data); setError(''); if (['Queued', 'Processing'].includes(result.data.status)) timer = setTimeout(poll, 2500) }
      else setError(result.error || 'Unable to read review status. Use Refresh status.')
    }
    timer = setTimeout(poll, 1500)
    return () => { stopped = true; clearTimeout(timer) }
  }, [open, job?.id, running, pollVersion])

  const run = async () => {
    setBusy(true); setError('')
    const response = await queuePayrollReview(snapshot, consent)
    setBusy(false)
    if (response.ok && response.data) { setJob(response.data); setDecisions([]) } else setError(response.error || 'Unable to start review.')
  }
  const refresh = async () => { if (!job) return; setBusy(true); const r = await getPayrollReview(job.id); setBusy(false); if (r.ok && r.data) { setJob(r.data); setError(''); setPollVersion(v => v + 1) } else setError(r.error) }
  const decide = async (finding: ReviewFinding, apply: boolean) => {
    if (!job || !ready) return
    const requestedKey = key
    setBusy(true); setError('')
    const response = await recordPayrollReviewDecision(job, snapshot, finding.id, apply ? 'ApplyToDraft' : 'KeepExisting')
    setBusy(false)
    if (!response.ok) { setError(response.error); return }
    if (currentKey.current !== requestedKey) { setError('Configuration changed while recording the decision. Review again before applying.'); return }
    setDecisions(current => [...current, finding.id])
    if (apply) onDisableProration(finding.code)
  }
  const amount = (v: number) => v.toLocaleString('en-IN', { maximumFractionDigits: 2, minimumFractionDigits: 2 })
  return <>
    <Button aria-label="AI review" icon={<BulbOutlined />} onClick={() => setOpen(true)}>AI review</Button>
    <Modal open={open} title="Payroll configuration review" width={980} zIndex={1500} className="payroll-advisor-modal" onCancel={() => setOpen(false)} footer={<Button onClick={() => setOpen(false)}>Close</Button>}>
      <div className="payroll-advisor-content">
        <Alert type="info" showIcon message="Suggestions only, not statutory approval" description="Verified statutory sources are not attached. Review eligibility, jurisdiction, effective dates and exceptions with your payroll reviewer. Nothing here saves or activates live configuration." />
        <div className="payroll-advisor-toolbar">
          {!component && <div className="payroll-advisor-client"><span>Client<SalaryFieldHelp label="Review client" text="Review the selected client's draft. The review does not certify client-specific statutory applicability." example="For a shared template, select the client being reviewed. A review for another client is not reused." /></span><Select aria-label="Review client" value={clientId || undefined} disabled={!!fixedClientId} options={scopeIds.map(id => ({ value:id, label:clients.find(c => c.id === id)?.name || `Client ${id}` }))} onChange={setChosenClient} placeholder="Assign a client to the template" /></div>}
          <Button icon={<HistoryOutlined />} loading={busy} onClick={async () => { setBusy(true); const r = await getPayrollReviewHistory(clientId); setBusy(false); if (r.ok) { setHistory(r.data); setError(r.data.length ? '' : 'No previous reviews for this scope.') } else setError(r.error) }}>My review history</Button>
          {job && <Button icon={<ReloadOutlined />} disabled={busy} onClick={() => void refresh()}>Refresh status</Button>}
        </div>
        <Checkbox checked={consent} onChange={e => setConsent(e.target.checked)}>Send these configuration fields and sample calculations to the saved AI provider. No employee records, PAN, Aadhaar or bank details are included.</Checkbox>
        <div className="payroll-advisor-run"><Button aria-label={job ? 'Run new review' : 'Run review'} type="primary" icon={<BulbOutlined />} disabled={!consent || !canReview || running} loading={busy} onClick={() => void run()}>{job ? 'Run new review' : 'Run review'}</Button>{running && <Spin size="small" />}{job && <Tag color={running ? 'processing' : job.status === 'Failed' ? 'error' : 'warning'}>{job.status === 'NeedsReview' ? 'Review required' : job.status}</Tag>}</div>
        {error && <Alert type="warning" showIcon message={error} />}
        {stale && <Alert type="warning" showIcon message="Draft changed. This review is out of date." description="Run a new review before applying any further suggestion." />}
        {job?.error && <Alert type="error" showIcon message={job.error} description="Engine checks remain available. AI failure never means approval." />}
        {history.length > 0 && <Collapse><Collapse.Panel key="history" header="Previous reviews">{history.map(item => <div className="payroll-advisor-history" key={item.id}><span>{new Date(item.createdAt + (item.createdAt.endsWith('Z') ? '' : 'Z')).toLocaleString()} / {item.snapshot.mode} / {item.status}</span><Button size="small" onClick={() => { setJob(item); setDecisions([]); setHistory([]) }}>View</Button></div>)}</Collapse.Panel></Collapse>}
        {job?.report && <>
          {component && <Alert type="info" message="Component-only review. Use AI review on its salary template for dependency checks and sample calculations." />}
          <div className="payroll-advisor-findings">{job.report.findings.map(f => <article key={f.id}>
            <div className="payroll-advisor-finding-head"><strong>{f.code}: {f.title}</strong><Tag color={f.severity === 'Error' ? 'error' : 'warning'}>{f.source} / {f.severity}</Tag></div>
            <p>{f.explanation}</p><p><b>Example: </b>{f.example}</p><p><b>Next step: </b>{f.suggestion}</p>
            <div className="payroll-advisor-actions">{f.canDisableProration && <Button size="small" disabled={!ready || busy || decisions.includes(f.id)} onClick={() => setPendingFinding(f)}>Apply to draft</Button>}<Button size="small" disabled={!ready || busy || decisions.includes(f.id)} onClick={() => void decide(f,false)}>{decisions.includes(f.id) ? 'Decision recorded' : 'Keep existing'}</Button></div>
          </article>)}</div>
          {!job.report.findings.length && !running && <Alert type="info" message="No findings returned. This is not statutory approval." />}
          {!!job.report.scenarios.length && <Collapse><Collapse.Panel key="scenarios" header="Calculation scenarios (payroll engine)">{job.report.scenarios.map(s => <section key={s.label} className="payroll-advisor-scenario"><h4>{s.label}: {s.payableDays} / {s.payrollDays} days</h4>{s.error ? <Alert type="error" message={s.error} /> : <div className="payroll-advisor-table"><table><thead><tr><th>Component</th><th>Category</th><th>Full-period equivalent</th><th>Earned amount</th></tr></thead><tbody>{s.amounts.map(a => <tr key={a.code}><td>{a.code}</td><td>{a.category}</td><td>{amount(a.fullPeriod)}</td><td>{amount(a.earned)}</td></tr>)}</tbody></table></div>}</section>)}</Collapse.Panel></Collapse>}
          <small>{job.report.coverage}</small>{job.report.model && <small>Provider: {job.report.provider} / Model: {job.report.model}</small>}
        </>}
      </div>
    </Modal>
    <Modal open={!!pendingFinding} title="Remove the second attendance adjustment?" zIndex={1600} okText="Apply to draft" confirmLoading={busy} okButtonProps={{ disabled: !ready }} onCancel={() => setPendingFinding(null)} onOk={async () => { if (pendingFinding) await decide(pendingFinding,true); setPendingFinding(null) }}>Only this unsaved draft changes. Re-simulate and use the normal Save action after review.</Modal>
  </>
}
