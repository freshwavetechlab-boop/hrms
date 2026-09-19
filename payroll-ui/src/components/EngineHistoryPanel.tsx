import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Segmented, Select, Spin, Tag } from 'antd'
import { Line } from 'react-chartjs-2'
import { getEngineHistory, type EngineHistory } from '../services/engineMonitoringService'
import { engineActivityData, type EngineChartMetric } from './engineActivityChart'
import { engineHistoryRange, localDateInput, type HistoryPeriod } from './engineHistoryRange'

export default function EngineHistoryPanel({ period, onPeriodChange }: { period: HistoryPeriod; onPeriodChange: (period: HistoryPeriod) => void }) {
  const [start, setStart] = useState(localDateInput(new Date()))
  const [end, setEnd] = useState(localDateInput(new Date()))
  const [reload, setReload] = useState(0)
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<EngineHistory | null>(null)
  const [error, setError] = useState('')
  const [metric, setMetric] = useState<EngineChartMetric>('requests')
  useEffect(() => {
    let active = true
    if (period === 'live') return
    setData(null); setError(''); setLoading(true)
    const load = async () => {
      try {
        const range = engineHistoryRange(period, start, end)
        const result = await getEngineHistory(range.from, range.until)
        if (!active) return
        if (result.ok) setData(result.data)
        else setError(result.error)
      } catch (e) { if (active) setError(e instanceof Error ? e.message : 'History unavailable.') }
      finally { if (active) setLoading(false) }
    }
    void load()
    return () => { active = false }
  }, [period, start, end, reload])
  const chart = useMemo(() => engineActivityData(data?.engines ?? [], metric, true), [data, metric])
  const points = data?.engines.flatMap(engine => engine.trend) ?? []
  const total = points.reduce((sum, point) => sum + (point.requests ?? 0), 0)
  const failures = points.reduce((sum, point) => sum + (point.failures ?? 0), 0)
  const covered = points.some(point => point.requests !== null)
  return <div className="engine-chart-card" data-testid="engine-history-panel">
    <div className="engine-chart-controls">
      <label htmlFor="engine-history-period">View history</label>
      <Select<HistoryPeriod> id="engine-history-period" value={period} onChange={onPeriodChange} style={{ minWidth: 160 }} options={[
        { value: 'live', label: 'Live (10 minutes)' }, { value: 'today', label: 'Today' }, { value: 'yesterday', label: 'Yesterday' }, { value: 'week', label: 'Last 7 days' }, { value: 'custom', label: 'Date range' },
      ]} />
      {period === 'custom' && <><label>From <input type="date" value={start} onChange={e => setStart(e.target.value)} /></label><label>Through <input type="date" value={end} onChange={e => setEnd(e.target.value)} /></label></>}
      {period !== 'live' && <Button onClick={() => setReload(value => value + 1)} loading={loading}>Refresh history</Button>}
      <span>30-day retention · Numeric summaries only · Checkpointed every minute</span>
    </div>
    {period !== 'live' && <>
      {error && <Alert type="warning" showIcon message="Saved history unavailable" description={error} />}
      {loading && <Spin tip="Loading saved history"><div style={{ minHeight: 60 }} /></Spin>}
      {data && <>
        <p>Saved activity: {data.deployment}. {data.bucketMinutes}-minute buckets; busy time is the average occupancy of observed API instances, not CPU. Gaps mean no saved observation, not zero work. Times use your browser timezone.</p>
        {(!data.recordingEnabled || data.warning || data.droppedBuckets > 0) && <Alert type="warning" showIcon message={!data.recordingEnabled ? 'History recording is disabled' : 'History coverage is incomplete'} description={data.warning || 'An extended storage outage exceeded the bounded retry buffer.'} />}
        <div className="engine-chart-controls"><Tag color="blue">{total} completed operations</Tag><Tag color={failures ? 'error' : 'green'}>{failures} failed attempts</Tag><Segmented value={metric} onChange={value => { if (value === 'requests' || value === 'busy') setMetric(value) }} options={[{ value: 'requests', label: 'Completed work' }, { value: 'busy', label: 'Busy time (%)' }]} /></div>
        {!covered ? <Alert type="info" showIcon message="No saved observations for this range" description="History starts when recording is enabled; previous unsaved graphs cannot be recovered. Wait up to a minute after new work, then refresh." />
          : <div className="engine-chart-wrap" data-testid="engine-history-chart"><Line role="img" aria-label="Saved engine history" data={chart} options={{ responsive: true, maintainAspectRatio: false, animation: false, interaction: { mode: 'index', intersect: false }, scales: { y: { beginAtZero: true, max: metric === 'busy' ? 100 : undefined, suggestedMax: metric === 'requests' ? 1 : undefined } }, plugins: { legend: { position: 'bottom' } } }} /></div>}
      </>}
      <p>Cards below remain live; selected-date totals are shown here.</p>
    </>}
  </div>
}
