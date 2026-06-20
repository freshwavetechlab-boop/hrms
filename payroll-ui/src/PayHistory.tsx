import { useEffect, useState } from 'react'
import { getPayRuns } from './services/payrollService'
import type { PayRun } from './types/payroll'
import { money } from './utils/salary'

export default function PayHistory() {
  const [runs, setRuns] = useState<PayRun[]>([]), [query, setQuery] = useState('')
  useEffect(() => { void getPayRuns().then(setRuns) }, [])
  const filtered = runs.filter(run => `${run.clientName} ${run.payPeriod} ${run.status}`.toLowerCase().includes(query.toLowerCase()))
  return <section className="pay-runs"><div className="pay-run-intro"><div><span className="eyebrow purple">Payroll</span><h3>Pay history</h3><p>All client-wise draft, approved and paid payroll runs.</p></div><input placeholder="Filter client, period, status..." value={query} onChange={event => setQuery(event.target.value)} /></div><section className="history-grid">{filtered.map(run => <article className="run-item" key={run.id}><strong>{run.clientName}</strong><span>{run.payPeriod} / {run.employeeCount} employees / {run.status}</span><b>{money(run.netPay)}</b></article>)}</section>{!filtered.length && <p className="empty">No payroll runs found.</p>}</section>
}
