import { useEffect, useState } from 'react'
import { Alert, Button, Select, Switch, Table, Tag } from 'antd'
import { getEngineActivity, type EngineActivity, type EngineActivityPage } from '../services/engineMonitoringService'
import { engineHistoryRange, localDateInput } from './engineHistoryRange'
import { activityDuration, activityOutcome } from './engineActivityLogFormat'
import './EngineActivityLog.css'

const timestamp = (value: string | null) => value ? new Date(value).toLocaleString() : '—'
const statuses = ['Running', 'Completed', 'Failed', 'Rejected', 'Retry', 'Cancelled', 'NeedsReview', 'Interrupted']
export default function EngineActivityLog({ engines }: { engines: Array<{ code: string; name: string }> }) {
  const [engine, setEngine] = useState('')
  const [status, setStatus] = useState('')
  const [from, setFrom] = useState(localDateInput(new Date()))
  const [until, setUntil] = useState(localDateInput(new Date()))
  const [auto, setAuto] = useState(true)
  const [cursor, setCursor] = useState<{ before: string; beforeId: string } | null>(null)
  const [reload, setReload] = useState(0)
  const [data, setData] = useState<EngineActivityPage | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  useEffect(() => { setCursor(null) }, [engine, status, from, until])
  useEffect(() => {
    let active = true
    let timer = 0
    setData(null)
    const load = async (initial: boolean) => {
      if (initial) setLoading(true)
      try {
        const range = engineHistoryRange('custom', from, until)
        const result = await getEngineActivity({ ...range, engine, status, ...cursor })
        if (!active) return
        if (result.ok) { setData(result.data); setError('') } else { setData(null); setError(result.error) }
      } catch (e) { if (active) { setData(null); setError(e instanceof Error ? e.message : 'Activity unavailable.') } }
      finally {
        if (active) { setLoading(false); if (auto && !cursor) timer = window.setTimeout(tick, 5000) }
      }
    }
    const tick = () => { if (!active) return; if (document.hidden) timer = window.setTimeout(tick, 5000); else void load(false) }
    void load(true)
    return () => { active = false; window.clearTimeout(timer) }
  }, [engine, status, from, until, cursor, reload, auto])
  const older = () => {
    const last = data?.items.at(-1)
    if (last) setCursor({ before: last.startedAtUtc, beforeId: last.id })
  }
  return <section className="engine-chart-card engine-activity-log" id="engine-activity-log">
    <header><div><h3>Engine activity log</h3><p>Individual task timings across all monitored engines. Processing includes database/provider waits; HTTP entries measure the request, not any queued work that follows.</p></div><Tag>Super Admin only</Tag></header>
    <div className="engine-chart-controls">
      <Select aria-label="Activity engine" value={engine} onChange={setEngine} style={{ minWidth: 200 }} options={[{ value: '', label: 'All engines' }, ...engines.map(e => ({ value: e.code, label: e.name }))]} />
      <Select aria-label="Activity outcome" value={status} onChange={setStatus} style={{ minWidth: 150 }} options={[{ value: '', label: 'All outcomes' }, ...statuses.map(s => ({ value: s, label: activityOutcome(s) }))]} />
      <label>From <input aria-label="Activity from date" type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
      <label>Through <input aria-label="Activity through date" type="date" value={until} onChange={e => setUntil(e.target.value)} /></label>
      <label><Switch size="small" checked={auto} onChange={setAuto} /> Auto-refresh (5s)</label>
      <Button loading={loading} onClick={() => setReload(v => v + 1)}>Refresh logs</Button>
    </div>
    {error && <Alert showIcon type="warning" message="Activity logs unavailable" description={error} />}
    {data && (!data.recordingEnabled || data.warning || data.droppedRecords > 0) && <Alert showIcon type="warning" message={!data.recordingEnabled ? 'Detailed recording is disabled' : 'Log coverage is incomplete'} description={data.warning || (data.droppedRecords > 0 ? `${data.droppedRecords} records exceeded retry-buffer limits and could not be saved.` : 'Existing saved logs remain available; new tasks are not being recorded.')} />}
    <Table<EngineActivity> className="engine-activity-table" rowKey="id" size="small" loading={loading} dataSource={data?.items ?? []}
      scroll={{ x: 1040 }} pagination={{ pageSize: 10, showSizeChanger: false, hideOnSinglePage: true }}
      locale={{ emptyText: error ? 'Logs could not be loaded.' : 'No task logs for these filters. Detailed recording starts with this update; older timings are not invented.' }}
      columns={[
        { title: 'Engine', width: 135, render: (_, row) => engines.find(e => e.code === row.engineCode)?.name || row.engineCode },
        { title: 'Task / candidate / job', width: 290, render: (_, row) => <div><strong>{row.candidateName || row.operation}</strong>{row.candidateName && <div>{row.operation}</div>}{row.positionTitle && <div>{row.positionTitle}</div>}<small>{row.applicationCode || (row.applicationId ? `Application #${row.applicationId}` : '')}{row.referenceId && ` · ${row.referenceType} #${row.referenceId}`}</small><small>Log {row.id.slice(0, 12)}{row.attempt ? ` · Attempt ${row.attempt}` : ''}</small></div> },
        { title: 'Started / finished', width: 185, render: (_, row) => <div>{timestamp(row.startedAtUtc)}<small>End: {timestamp(row.completedAtUtc)}</small></div> },
        { title: 'Outcome', width: 145, render: (_, row) => <div><Tag color={row.status === 'Completed' ? 'green' : row.status === 'Running' ? 'blue' : 'orange'}>{activityOutcome(row.status)}</Tag>{row.httpStatus && <small>HTTP {row.httpStatus}</small>}{row.failureCode && <small>{row.failureCode}</small>}</div> },
        { title: 'Processing time', width: 145, render: (_, row) => <div><strong>{row.status === 'Running' ? `${activityDuration(Math.max(0, Date.now() - Date.parse(row.startedAtUtc)))} elapsed` : activityDuration(row.durationMs)}</strong>{row.queueWaitMs !== null && <small>Queue wait: {activityDuration(row.queueWaitMs)}</small>}</div> },
        { title: 'AI phase', width: 140, render: (_, row) => <div>{activityDuration(row.aiDurationMs)}{row.aiStatus && <small>{row.aiStatus}</small>}</div> },
      ]} />
    <div className="engine-chart-controls"><Button disabled={!cursor} onClick={() => setCursor(null)}>Latest records</Button><Button disabled={!data?.hasMore || loading} onClick={older}>Older records</Button><span>{cursor ? 'Older records: auto-refresh paused. ' : ''}{data?.deployment ? `${data.deployment} · ` : ''}{data?.retentionDays ?? 7}-day detailed retention; aggregates remain 30 days. AI phase is included in processing, not extra time. Unrecorded timing is not zero.</span></div>
  </section>
}
