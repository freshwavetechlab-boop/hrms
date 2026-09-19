import { useEffect, useState } from 'react'
import { Alert, Button, Collapse, Drawer, Empty, Input, Progress, Select, Space, Switch, Table, Tag } from 'antd'
import { RobotOutlined, SendOutlined } from '@ant-design/icons'
import { Bar, Doughnut, Line } from 'react-chartjs-2'
import { ArcElement, BarElement, CategoryScale, Chart as ChartJS, Legend, LinearScale, LineElement, PointElement, Tooltip } from 'chart.js'
import type { ChartData } from 'chart.js'
import { getJsonResult, postJson } from '../services/apiClient'
import { getClients } from '../services/payrollService'
import { getAiIntegration } from '../services/aiIntegrationService'
import './FrevoPilot.css'

ChartJS.register(ArcElement, BarElement, CategoryScale, Legend, LinearScale, LineElement, PointElement, Tooltip)
type Plot = { type: string; title: string; labels: string[]; datasets: { label: string; data: (number | null)[]; backgroundColor: string | string[]; borderColor: string }[] }
type Result = {
  title: string; summary: string; model: string; scope: string; explanation: string; sql: string;
  summarySource?: 'ai' | 'database-only' | 'verified-data'; summaryWarning?: string;
  planSource?: 'validated-memory' | 'verified-contract';
  generatedAtUtc: string; rows: Record<string, unknown>[]; knowledgeSources: string[];
  chart: { widgets: Plot[]; valueFields: string[] };
  insights: { type: string; title: string; narrative: string; evidence: string[] }[]
}
type Run = { id: string; question: string; status: string; progress: number; message: string; result?: Result; createdAtUtc: string }
const samples = ['Count active employees by client.', 'Count active RRU employees by Gender.', 'Count candidate applications by job title.', 'Sum requested leave Days by Status across all clients and all dates.']
const formatted = (value: unknown) => value == null ? 'Not recorded' : typeof value === 'number' ? value.toLocaleString('en-IN', { maximumFractionDigits: 2 }) : String(value)

function PlotView({ plot }: { plot: Plot }) {
  const horizontal = plot.type === 'bar' && (plot.labels.length > 8 || plot.labels.some(label => label.length > 28))
  const labels = plot.labels.map(label => label.match(/.{1,38}(?:\s|$)|\S{1,38}/g)?.map(part => part.trim()) || label)
  const data = { labels, datasets: plot.datasets }
  if (plot.type === 'table' || plot.type === 'kpi') return null
  const common = { responsive: true, maintainAspectRatio: false, animation: false as const, plugins: { legend: { position: 'bottom' as const } } }
  return <section className="pilot-chart"><h4>{plot.title}</h4><div style={horizontal ? { height: Math.max(300, plot.labels.length * 48) } : undefined}>
    {plot.type === 'doughnut' ? <Doughnut data={data as ChartData<'doughnut'>} options={common} />
      : plot.type === 'line' ? <Line data={data as ChartData<'line'>} options={common} />
        : <Bar data={data as ChartData<'bar'>} options={{ ...common, indexAxis: horizontal ? 'y' : 'x', scales: { [horizontal ? 'x' : 'y']: { beginAtZero: true } } }} />}
  </div></section>
}

export default function FrevoPilot() {
  const [open, setOpen] = useState(false)
  const [question, setQuestion] = useState('')
  const [clientId, setClientId] = useState<number>()
  const [clients, setClients] = useState<{ value: number; label: string }[]>([])
  const [modelId, setModelId] = useState<number>()
  const [models, setModels] = useState<{ value: number; label: string }[]>([])
  const [useFastMetrics, setUseFastMetrics] = useState(true)
  const [run, setRun] = useState<Run>()
  const [history, setHistory] = useState<Run[]>([])
  const [error, setError] = useState('')
  const [starting, setStarting] = useState(false)
  const [enabled, setEnabled] = useState(true)
  const processing = starting || run?.status === 'Processing'

  useEffect(() => {
    if (!open) return
    let disposed = false
    void getJsonResult('/api/frevopilot/analytics/status', { enabled: false }, { loader: false }).then(response => {
      if (!disposed) { setEnabled(response.ok && response.data.enabled); if (!response.ok) setError(response.error) }
    })
    void getClients().then(rows => { if (!disposed) setClients(rows.map(row => ({ value: row.id, label: row.name }))) })
    void getAiIntegration().then(pool => {
      if (disposed) return
      const available = pool.models.filter(model => model.clientId === 0 && model.hasApiKey && model.credentialStatus === 'Ready')
      setModels(available.map(model => ({ value: model.id, label: `Preview saved model: ${model.modelName} (${model.providerCode})${!model.enableAiScoring || !model.isActive ? ' — disabled' : ''}` })))
      setModelId(previous => available.some(model => model.id === previous) ? previous : undefined)
    })
    return () => { disposed = true }
  }, [open])

  useEffect(() => {
    if (!run || run.status !== 'Processing') return
    let disposed = false
    let timer: ReturnType<typeof setTimeout>
    const poll = async () => {
      const response = await getJsonResult<Run | null>(`/api/frevopilot/analytics/runs/${run.id}`, null, { loader: false, timeoutMs: 12000 })
      if (disposed) return
      if (response.ok && response.data) {
        setRun(response.data)
        setError('')
        if (response.data.status !== 'Processing') {
          const completed = response.data
          setHistory(previous => [completed, ...previous.filter(item => item.id !== completed.id)].slice(0, 12))
          return
        }
      } else {
        setError(response.error || 'Status could not be refreshed. Retrying…')
        if ([401, 403, 404].includes(response.status)) { setRun({ ...run, status: 'Failed', message: response.error }); return }
      }
      timer = setTimeout(() => void poll(), 1500)
    }
    timer = setTimeout(() => void poll(), 500)
    return () => { disposed = true; clearTimeout(timer) }
  }, [run?.id, run?.status])

  const ask = async () => {
    if (processing || question.trim().length < 4) return
    setStarting(true); setError('')
    const response = await postJson('/api/frevopilot/analytics/runs', { question: question.trim(), clientId: clientId ?? null, useFastMetrics, ...(modelId ? { modelId } : {}) }, null as Run | null, { loader: false, toast: false })
    setStarting(false)
    if (response.ok && response.data) setRun(response.data)
    else setError(response.error)
  }
  const result = run?.status === 'Completed' ? run.result : undefined
  const reusedValidatedQuery = result?.planSource === 'validated-memory'
  const compiledVerifiedMetric = result?.planSource === 'verified-contract'
  const fields = result?.rows.length ? Object.keys(result.rows[0]) : []
  return <>
    <Button className="topbar-icon-btn" icon={<RobotOutlined />} aria-label="Open FrevoPilot" title="FrevoPilot · super admin analytics" onClick={() => setOpen(true)} />
    <Drawer title={<Space><RobotOutlined /><span>FrevoPilot</span><Tag color="purple">Super admin</Tag><Tag color="green">Read-only</Tag></Space>} open={open} onClose={() => setOpen(false)} width="min(1400px, 100vw)" className="pilot-drawer">
      <div className="pilot-workspace" data-testid="frevopilot-workspace">
        <div className="pilot-intro"><h2>Ask your HRMS.</h2><p>Live database totals, traceable answers and dashboards. Business records are never changed.</p></div>
        <div style={{ display: 'grid', gap: 6 }}>
          <Select aria-label="FrevoPilot model" value={modelId ?? 0} onChange={value => setModelId(value || undefined)} options={[{ value: 0, label: 'Active model + configured fallback' }, ...models]} disabled={processing} showSearch optionFilterProp="label" />
          <small>Only this analysis; does not change the active provider. Saved disabled models can be previewed here.</small>
          <Space wrap><Switch size="small" aria-label="Fast verified metrics" checked={useFastMetrics} onChange={setUseFastMetrics} disabled={processing} /><span>Fast verified metrics</span><small>For supported local-model questions; complex questions still use AI.</small></Space>
        </div>
        <div className="pilot-composer">
          <Select aria-label="FrevoPilot client scope" placeholder="All clients" allowClear showSearch optionFilterProp="label" value={clientId} onChange={setClientId} options={clients} disabled={processing} />
          <Input.TextArea aria-label="FrevoPilot question" placeholder="For example: RRU active employee headcount by gender" value={question} maxLength={2000} autoSize={{ minRows: 2, maxRows: 5 }} onChange={event => setQuestion(event.target.value)} onPressEnter={event => { if (event.ctrlKey || event.metaKey) void ask() }} />
          <Button aria-label="Ask FrevoPilot" type="primary" icon={<SendOutlined />} loading={processing} disabled={!enabled || question.trim().length < 4} onClick={() => void ask()}>Ask FrevoPilot</Button>
        </div>
        {!run && <Space wrap>{samples.map(sample => <Button key={sample} onClick={() => setQuestion(sample)}>{sample}</Button>)}</Space>}
        {!enabled && !error && <Alert type="warning" message="FrevoPilot is disabled on this API." />}
        {error && <Alert type="error" showIcon message={error} />}
        {run && <div className="pilot-run-status" data-testid="frevopilot-status"><strong>{run.question}</strong><span>{run.message}</span>{run.status === 'Processing' && <Progress percent={run.progress} status="active" />}{run.status === 'Failed' && <Alert type="warning" showIcon message={run.message} description="No business records were changed. Adjust the question or retry." />}</div>}
        {result && <div className="pilot-result" data-testid="frevopilot-result">
          {result.summaryWarning && <Alert type="warning" showIcon message={result.summaryWarning} description="The live query completed. Charts and backing data remain available; AI recommendations and risks were not generated." />}
          <header><div><h2>{result.title}</h2><p>{result.summary}</p></div><Space wrap><Tag>{result.scope}</Tag>{compiledVerifiedMetric ? <Tag color="green">Verified metric · fresh data</Tag> : reusedValidatedQuery ? <Tag color="green">Verified query · fresh data</Tag> : <Tag color={result.model.includes('deterministic') ? 'orange' : 'blue'}>{result.model}</Tag>}</Space></header>
          {compiledVerifiedMetric && <p className="pilot-freshness">The server compiled and checked your exact requested metric, then read fresh database results. No AI narrative was generated.</p>}
          {reusedValidatedQuery && <p className="pilot-freshness">FrevoPilot reused a previously validated query and read the database again for this result. No AI narrative was generated.</p>}
          <p className="pilot-freshness">Database snapshot: {new Date(result.generatedAtUtc).toLocaleString('en-IN')} · {result.rows.length} result groups · Ask again to refresh</p>
          {!result.rows.length ? <Empty description="No matching records in this scope. This does not prove zero activity." /> : <>
            <div className="pilot-metrics">{result.chart.valueFields.slice(0, 4).map(field => {
              const values = result.rows.filter(row => row[field] != null && row[field] !== '').map(row => Number(row[field])).filter(Number.isFinite)
              return <section key={field}><span>{field.replaceAll('_', ' ')}</span><strong>{values.length === 1 ? formatted(values[0]) : `${formatted(Math.min(...values))} – ${formatted(Math.max(...values))}`}</strong><small>{values.length === 1 ? 'Returned value' : 'Range across returned groups · not a sum'}</small></section>
            })}</div>
            <div className="pilot-charts">{result.chart.widgets.filter(plot => ['bar', 'line', 'doughnut'].includes(plot.type)).slice(0, 2).map((plot, index) => <PlotView key={`${run?.id}-${index}`} plot={plot} />)}</div>
            <Table data-testid="frevopilot-data" size="small" rowKey={(_, index) => String(index)} dataSource={result.rows} columns={fields.map(field => ({ title: field.replaceAll('_', ' '), dataIndex: field, render: formatted }))} pagination={{ pageSize: 10, showSizeChanger: true }} scroll={{ x: 'max-content' }} />
          </>}
          <div className="pilot-insights">{result.insights.map((insight, index) => <section key={index}><Tag color={insight.type === 'Risk' ? 'orange' : 'cyan'}>{insight.type}</Tag><h4>{insight.title}</h4><p>{insight.narrative}</p><small>{insight.evidence.join(' · ')}</small></section>)}</div>
          <Collapse items={[{ key: 'source', label: 'Source query, business rules and explanation', children: <><p>{result.explanation}</p><pre>{result.sql}</pre><p>{result.knowledgeSources.join(' · ') || 'Live schema and governed business definitions'}</p></> }]} />
        </div>}
        {!!history.length && <Collapse items={[{ key: 'history', label: 'This session’s recent questions', children: <Space direction="vertical">{history.map(item => <Button key={item.id} disabled={processing} type="link" onClick={() => setRun(item)}>{item.question} · {item.status}</Button>)}</Space> }]} />}
      </div>
    </Drawer>
  </>
}
