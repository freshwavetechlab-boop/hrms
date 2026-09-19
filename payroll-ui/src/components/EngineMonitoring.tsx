import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Progress, Segmented, Tag, Tooltip } from 'antd'
import { Line } from 'react-chartjs-2'
import { CategoryScale, Chart as ChartJS, Filler, Legend, LinearScale, LineElement, PointElement, Tooltip as ChartTooltip } from 'chart.js'
import { emptyEngineMonitoringSnapshot, engineMonitoringStreamUrl, getEngineMonitoring, type EngineMonitoringSnapshot, type EngineRuntimeMetric } from '../services/engineMonitoringService'
import './EngineMonitoring.css'
import { engineActivityData, type EngineChartMetric } from './engineActivityChart'
import EngineHistoryPanel from './EngineHistoryPanel'
import EngineActivityLog from './EngineActivityLog'
import type { HistoryPeriod } from './engineHistoryRange'

ChartJS.register(CategoryScale, Filler, Legend, LinearScale, LineElement, PointElement, ChartTooltip)

const stateColor = (state: EngineRuntimeMetric['state']) => state === 'Attention' ? 'error' : state === 'Active' ? 'processing' : state === 'Observed' ? 'success' : 'default'
const duration = (value: number) => value >= 1000 ? `${(value / 1000).toFixed(1)}s` : `${Math.round(value)}ms`
const uptime = (seconds: number) => `${Math.floor(seconds / 86400)}d ${Math.floor(seconds % 86400 / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`

export default function EngineMonitoring() {
  const [snapshot, setSnapshot] = useState<EngineMonitoringSnapshot>(emptyEngineMonitoringSnapshot())
  const [error, setError] = useState('')
  const [refreshing, setRefreshing] = useState(false)
  const [connected, setConnected] = useState(false)
  const [chartMetric, setChartMetric] = useState<EngineChartMetric>('requests')
  const [historyPeriod, setHistoryPeriod] = useState<HistoryPeriod>('live')
  const apiTarget = new URL(engineMonitoringStreamUrl(), window.location.href).origin

  const refresh = async (manual = false) => {
    if (manual) setRefreshing(true)
    const result = await getEngineMonitoring()
    if (result.ok) { setSnapshot(result.data); setError('') } else setError(result.status === 403 ? 'Only a Super Admin can view engine monitoring.' : result.error)
    if (manual) setRefreshing(false)
  }

  useEffect(() => {
    let active = true
    let retryTimer = 0
    let stream: EventSource | null = null
    const connect = async () => {
      const result = await getEngineMonitoring()
      if (!active) return
      if (!result.ok) {
        setError(result.status === 403 ? 'Only a Super Admin can view engine monitoring.' : result.error)
        retryTimer = window.setTimeout(connect, 5000)
        return
      }
      setSnapshot(result.data)
      setError('')
      stream = new EventSource(engineMonitoringStreamUrl(), { withCredentials: true })
      stream.addEventListener('snapshot', event => {
        if (!active) return
        try { setConnected(true); setSnapshot(JSON.parse((event as MessageEvent<string>).data) as EngineMonitoringSnapshot); setError('') }
        catch { /* Ignore a single malformed snapshot and keep the stream alive. */ }
      })
      stream.onerror = () => {
        stream?.close()
        setConnected(false)
        if (active) retryTimer = window.setTimeout(connect, 3000)
      }
    }
    void connect()
    return () => { active = false; stream?.close(); window.clearTimeout(retryTimer) }
  }, [])

  const chartData = useMemo(() => engineActivityData(snapshot.engines, chartMetric), [snapshot.engines, chartMetric])

  const active = snapshot.engines.reduce((sum, engine) => sum + engine.activeRequests, 0)
  const recent = snapshot.engines.reduce((sum, engine) => sum + engine.requestsLastFiveMinutes, 0)
  const alerts = snapshot.engines.filter(engine => engine.state === 'Attention').length

  return <section className="engine-monitoring">
    <div className="engine-monitoring-toolbar">
      <div><span className="eyebrow purple">Read-only control plane</span><h2>Engine Control Center</h2><p>Live API workload and process health without changing engine behaviour.</p></div>
      <div className="engine-monitoring-actions"><Tag color="purple">Super Admin only</Tag><Tag color={connected ? "blue" : "orange"}>{connected ? "Live · 1s" : "Reconnecting"}</Tag><Button loading={refreshing} onClick={() => void refresh(true)}>Refresh</Button></div>
    </div>

    {error && <Alert type="error" showIcon message="Monitoring unavailable" description={error} />}
    <div className="engine-source-note" data-testid="engine-api-target"><Tag color="blue">Monitoring API: {apiTarget}</Tag><span>Parser activity is local to this API process; another localhost port or deployment has separate counters. ATS queue activity is shared only within the same database. These metrics measure operations, not extraction or matching accuracy.</span></div>
    <Alert type="info" showIcon message="Safe monitoring phase" description="Enable/disable and user access controls are intentionally locked until monitoring is validated. Existing engines continue unchanged." />

    <div className="engine-process-grid">
      <div className="engine-gauge"><Progress type="dashboard" percent={Number(snapshot.process.cpuPercent)} strokeColor="#635bff" size={120} format={value => `${value ?? 0}%`} /><span>API CPU</span></div>
      <div><span>Memory</span><strong>{snapshot.process.workingSetMb.toFixed(1)} MB</strong><small>{snapshot.process.managedMemoryMb.toFixed(1)} MB managed</small></div>
      <div><span>Active work</span><strong>{active}</strong><small>{recent} observed operations in 5 minutes</small></div>
      <div><span>Health</span><strong className={alerts ? 'engine-attention' : 'engine-healthy'}>{!connected ? 'Unavailable' : alerts ? `${alerts} alert${alerts > 1 ? 's' : ''}` : 'No recent errors'}</strong><small>{snapshot.process.threadCount} threads · uptime {uptime(snapshot.process.uptimeSeconds)}</small></div>
    </div>

    <EngineHistoryPanel period={historyPeriod} onPeriodChange={setHistoryPeriod} />
    {historyPeriod === 'live' && <div className="engine-chart-card">
      <header><div><h3>Live engine activity</h3><p>10-minute history. Busy % = time with observed work in each 30-second window; not CPU or capacity usage. ATS includes the shared background queue; other engines show this API process's observed work. CPU/memory are for this instance.</p></div><span>{snapshot.generatedAtUtc ? `Updated ${new Date(snapshot.generatedAtUtc).toLocaleTimeString()}` : 'Connecting…'}</span></header>
      <div className="engine-chart-controls"><Segmented aria-label="Activity measurement" options={[{ label: 'Completed work', value: 'requests' }, { label: 'Busy time (%)', value: 'busy' }]} value={chartMetric} onChange={value => { if (value === 'requests' || value === 'busy') setChartMetric(value) }} /><span>{chartMetric === 'requests' ? 'Completed operations per 30 seconds, including failed attempts. Brief operations remain visible here.' : 'Measured busy time per 30 seconds. Very short operations can round to 0%; this is not CPU load.'}</span></div>
      <div className="engine-chart-wrap" data-testid="engine-activity-chart" data-metric={chartMetric}><Line aria-label={chartMetric === 'requests' ? 'Completed engine operations' : 'Engine busy time percentage'} role="img" data={chartData} options={{ responsive: true, maintainAspectRatio: false, animation: false, interaction: { mode: 'index', intersect: false }, scales: { y: { beginAtZero: true, max: chartMetric === 'busy' ? 100 : undefined, suggestedMax: chartMetric === 'requests' ? 1 : undefined, title: { display: true, text: chartMetric === 'requests' ? 'Completed operations / 30s' : 'Busy time (%)' }, ticks: { precision: 0, callback: value => chartMetric === 'busy' ? `${value}%` : value } } }, plugins: { legend: { position: 'bottom' } } }} /></div>
    </div>

    }
    <div className="engine-card-grid">
      {snapshot.engines.map(engine => <article className="engine-card" data-testid={`engine-card-${engine.code}`} key={engine.code}>
        <header><div><span>{engine.category}</span><h3>{engine.name}</h3></div><Tag color={stateColor(engine.state)}>{engine.state}</Tag></header>
        <p>{engine.description}</p>
        <div className="engine-load-row"><strong data-testid={`engine-load-${engine.code}`}>{engine.loadPercent}% {engine.code === 'frevopilot' ? 'workflow occupancy' : 'busy'}</strong><span>{engine.coverage}</span></div>
        {engine.code === 'frevopilot' && <small>Time with an active dashboard in the last 30s, including AI and database waits; not CPU usage.</small>}
        {engine.loadPercent === 0 && engine.requestsLastFiveMinutes > 0 && <small>Completed work was recorded. Short or older operations may show 0% current busy time.</small>}
        <Progress percent={engine.loadPercent} showInfo={false} strokeColor={engine.state === 'Attention' ? '#e5484d' : engine.loadPercent > 70 ? '#f59e0b' : '#0f9f6e'} />
        <dl><div><dt>{engine.code === 'frevopilot' ? 'Active dashboards' : 'Active'}</dt><dd data-testid={`engine-active-${engine.code}`}>{engine.activeRequests}</dd></div><div><dt>{engine.code === 'frevopilot' ? '5m dashboards' : engine.code === 'ats-scoring' ? '5m jobs' : '5m operations'}</dt><dd data-testid={`engine-requests-${engine.code}`}>{engine.requestsLastFiveMinutes}</dd></div><div><dt>Success</dt><dd>{engine.successRate.toFixed(1)}%</dd></div><div><dt>Avg / p95</dt><dd>{duration(engine.averageDurationMs)} / {duration(engine.p95DurationMs)}</dd></div></dl>
        <footer>{Boolean(engine.queuedRequests) && <Tag color="blue">{engine.queuedRequests} queued/retry</Tag>}{engine.lastActivityUtc ? `Last activity ${new Date(engine.lastActivityUtc).toLocaleTimeString()}` : 'No completed work in the last 10 minutes'}{engine.lastError && <Tooltip title={engine.lastError}><Tag color="error">Last error</Tag></Tooltip>}</footer>
      </article>)}
    </div>
    <EngineActivityLog engines={snapshot.engines} />
  </section>
}
