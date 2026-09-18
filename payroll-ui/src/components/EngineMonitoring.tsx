import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Progress, Tag, Tooltip } from 'antd'
import { Line } from 'react-chartjs-2'
import { CategoryScale, Chart as ChartJS, Filler, Legend, LinearScale, LineElement, PointElement, Tooltip as ChartTooltip } from 'chart.js'
import { emptyEngineMonitoringSnapshot, engineMonitoringStreamUrl, getEngineMonitoring, type EngineMonitoringSnapshot, type EngineRuntimeMetric } from '../services/engineMonitoringService'
import './EngineMonitoring.css'

ChartJS.register(CategoryScale, Filler, Legend, LinearScale, LineElement, PointElement, ChartTooltip)

const palette = ['#635bff', '#0f9f6e', '#1677ff', '#f59e0b', '#e5484d', '#06b6d4', '#8b5cf6', '#64748b']
const stateColor = (state: EngineRuntimeMetric['state']) => state === 'Attention' ? 'error' : state === 'Active' ? 'processing' : state === 'Observed' ? 'success' : 'default'
const duration = (value: number) => value >= 1000 ? `${(value / 1000).toFixed(1)}s` : `${Math.round(value)}ms`
const uptime = (seconds: number) => `${Math.floor(seconds / 86400)}d ${Math.floor(seconds % 86400 / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`

export default function EngineMonitoring() {
  const [snapshot, setSnapshot] = useState<EngineMonitoringSnapshot>(emptyEngineMonitoringSnapshot())
  const [error, setError] = useState('')
  const [refreshing, setRefreshing] = useState(false)

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
        try { setSnapshot(JSON.parse((event as MessageEvent<string>).data) as EngineMonitoringSnapshot); setError('') }
        catch { /* Ignore a single malformed snapshot and keep the stream alive. */ }
      })
      stream.onerror = () => {
        stream?.close()
        if (active) retryTimer = window.setTimeout(connect, 3000)
      }
    }
    void connect()
    return () => { active = false; stream?.close(); window.clearTimeout(retryTimer) }
  }, [])

  const chartData = useMemo(() => ({
    labels: snapshot.engines[0]?.trend.map(point => new Date(point.timeUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })) ?? [],
    datasets: snapshot.engines.map((engine, index) => ({
      label: engine.name,
      data: engine.trend.map(point => point.loadPercent),
      borderColor: palette[index % palette.length],
      backgroundColor: `${palette[index % palette.length]}18`,
      borderWidth: 2,
      pointRadius: 0,
      tension: .32,
      fill: false,
    })),
  }), [snapshot.engines])

  const active = snapshot.engines.reduce((sum, engine) => sum + engine.activeRequests, 0)
  const recent = snapshot.engines.reduce((sum, engine) => sum + engine.requestsLastFiveMinutes, 0)
  const alerts = snapshot.engines.filter(engine => engine.state === 'Attention').length

  return <section className="engine-monitoring">
    <div className="engine-monitoring-toolbar">
      <div><span className="eyebrow purple">Read-only control plane</span><h2>Engine Control Center</h2><p>Live API workload and process health without changing engine behaviour.</p></div>
      <div className="engine-monitoring-actions"><Tag color="purple">Super Admin only</Tag><Tag color="blue">Live · 1s</Tag><Button loading={refreshing} onClick={() => void refresh(true)}>Refresh</Button></div>
    </div>

    {error && <Alert type="error" showIcon message="Monitoring unavailable" description={error} />}
    <Alert type="info" showIcon message="Safe monitoring phase" description="Enable/disable and user access controls are intentionally locked until monitoring is validated. Existing engines continue unchanged." />

    <div className="engine-process-grid">
      <div className="engine-gauge"><Progress type="dashboard" percent={Number(snapshot.process.cpuPercent)} strokeColor="#635bff" size={120} format={value => `${value ?? 0}%`} /><span>API CPU</span></div>
      <div><span>Memory</span><strong>{snapshot.process.workingSetMb.toFixed(1)} MB</strong><small>{snapshot.process.managedMemoryMb.toFixed(1)} MB managed</small></div>
      <div><span>Active work</span><strong>{active}</strong><small>{recent} requests in 5 minutes</small></div>
      <div><span>Health</span><strong className={alerts ? 'engine-attention' : 'engine-healthy'}>{alerts ? `${alerts} alert${alerts > 1 ? 's' : ''}` : 'Healthy'}</strong><small>{snapshot.process.threadCount} threads · uptime {uptime(snapshot.process.uptimeSeconds)}</small></div>
    </div>

    <div className="engine-chart-card">
      <header><div><h3>Live engine load</h3><p>Rolling 10-minute request pressure; 0 means no observed workload.</p></div><span>{snapshot.generatedAtUtc ? `Updated ${new Date(snapshot.generatedAtUtc).toLocaleTimeString()}` : 'Connecting…'}</span></header>
      <div className="engine-chart-wrap"><Line data={chartData} options={{ responsive: true, maintainAspectRatio: false, animation: false, interaction: { mode: 'index', intersect: false }, scales: { y: { beginAtZero: true, max: 100, ticks: { callback: value => `${value}%` } } }, plugins: { legend: { position: 'bottom' } } }} /></div>
    </div>

    <div className="engine-card-grid">
      {snapshot.engines.map(engine => <article className="engine-card" data-testid={`engine-card-${engine.code}`} key={engine.code}>
        <header><div><span>{engine.category}</span><h3>{engine.name}</h3></div><Tag color={stateColor(engine.state)}>{engine.state}</Tag></header>
        <p>{engine.description}</p>
        <div className="engine-load-row"><strong data-testid={`engine-load-${engine.code}`}>{engine.loadPercent}% load</strong><span>{engine.coverage}</span></div>
        <Progress percent={engine.loadPercent} showInfo={false} strokeColor={engine.state === 'Attention' ? '#e5484d' : engine.loadPercent > 70 ? '#f59e0b' : '#0f9f6e'} />
        <dl><div><dt>Active</dt><dd data-testid={`engine-active-${engine.code}`}>{engine.activeRequests}</dd></div><div><dt>5m requests</dt><dd data-testid={`engine-requests-${engine.code}`}>{engine.requestsLastFiveMinutes}</dd></div><div><dt>Success</dt><dd>{engine.successRate.toFixed(1)}%</dd></div><div><dt>Avg / p95</dt><dd>{duration(engine.averageDurationMs)} / {duration(engine.p95DurationMs)}</dd></div></dl>
        <footer>{engine.lastActivityUtc ? `Last activity ${new Date(engine.lastActivityUtc).toLocaleTimeString()}` : 'No activity observed since API start'}{engine.lastError && <Tooltip title={engine.lastError}><Tag color="error">Last error</Tag></Tooltip>}</footer>
      </article>)}
    </div>
  </section>
}
