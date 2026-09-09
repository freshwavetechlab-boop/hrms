import { useState, type ReactNode } from 'react'
import {
  ArrowDownOutlined,
  ArrowRightOutlined,
  ArrowUpOutlined,
  BarChartOutlined,
  DownloadOutlined,
  EyeOutlined,
  LineChartOutlined,
  PieChartOutlined,
  ReloadOutlined,
} from '@ant-design/icons'
import { Button, Drawer, Empty, Select, Skeleton, Tooltip } from 'antd'
import {
  ArcElement,
  BarElement,
  CategoryScale,
  Chart as ChartJS,
  Filler,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip as ChartTooltip,
  type ChartEvent,
} from 'chart.js'
import { Bar, Doughnut, Line } from 'react-chartjs-2'
import './DashboardEngine.css'

ChartJS.register(ArcElement, BarElement, CategoryScale, Filler, Legend, LinearScale, LineElement, PointElement, ChartTooltip)

const dashboardPalette = ['#6546e8', '#2f80ed', '#20a46b', '#f59e0b', '#e25563', '#17a2b8', '#8b5cf6', '#64748b']

export type DashboardOption = { value: string | number; label: string }
export type DashboardFilter = {
  key: string
  label: string
  value?: string | number
  placeholder?: string
  options: DashboardOption[]
  searchable?: boolean
  allowClear?: boolean
  disabled?: boolean
}

export type DashboardKpi = {
  key: string
  label: string
  value: string | number
  helper: string
  icon: ReactNode
  delta?: number | null
  sparkline?: number[]
  tone?: 'primary' | 'success' | 'warning' | 'danger' | 'neutral'
  onClick?: () => void
}

export type DashboardFunnelStage = {
  key: string
  label: string
  value: number
  conversion?: number | null
  conversionLabel?: string
  onClick?: () => void
}

export type DashboardChartKind = 'bar' | 'horizontalBar' | 'line' | 'area' | 'doughnut'
export type DashboardChartSeries = { label: string; values: number[]; color?: string }
export type DashboardChartSpec = {
  id: string
  title: string
  subtitle: string
  labels: string[]
  series: DashboardChartSeries[]
  kind: DashboardChartKind
  compatibleKinds: DashboardChartKind[]
  stacked?: boolean
  valueSuffix?: string
  emptyText?: string
  onPointClick?: (label: string, series: string) => void
}

export type DashboardQueueItem = {
  id: string
  title: string
  meta: string
  owner: string
  age: string
  priority: 'High' | 'Medium' | 'Normal'
  icon: ReactNode
  actionLabel: string
  onAction: () => void
}

export function DashboardEngine({ children, loading = false }: { children: ReactNode; loading?: boolean }) {
  if (loading) return <DashboardSkeleton />
  return <div className="dashboard-engine">{children}</div>
}

export function DashboardFilterBar({
  eyebrow,
  title,
  filters,
  refreshedAt,
  refreshing,
  onChange,
  onRefresh,
  onClear,
  extraControls,
}: {
  eyebrow: string
  title: string
  filters: DashboardFilter[]
  refreshedAt?: Date | null
  refreshing?: boolean
  onChange: (key: string, value?: string | number) => void
  onRefresh: () => void
  onClear: (key?: string) => void
  extraControls?: ReactNode
}) {
  const active = filters.filter(filter => filter.value !== undefined && filter.value !== '' && filter.value !== 'all')
  return <section className="dashboard-filter-shell" data-testid="dashboard-filter-bar">
    <div className="dashboard-filter-heading">
      <div><span>{eyebrow}</span><h2>{title}</h2></div>
      <div className="dashboard-refresh">
        <small>{refreshedAt ? `Updated ${refreshedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : 'Not refreshed yet'}</small>
        <Tooltip title="Refresh dashboard data"><Button aria-label="Refresh dashboard" icon={<ReloadOutlined spin={refreshing} />} onClick={onRefresh}>Refresh</Button></Tooltip>
      </div>
    </div>
    <div className="dashboard-filter-grid">
      {filters.map(filter => <label key={filter.key}>
        <span>{filter.label}</span>
        <Select
          data-testid={`dashboard-filter-${filter.key}`}
          aria-label={filter.label}
          allowClear={filter.allowClear !== false}
          disabled={filter.disabled}
          showSearch={filter.searchable}
          optionFilterProp="label"
          value={filter.value === '' ? undefined : filter.value}
          placeholder={filter.placeholder || `All ${filter.label.toLowerCase()}`}
          options={filter.options}
          onChange={value => onChange(filter.key, value)}
        />
      </label>)}
      {extraControls}
    </div>
    {active.length > 0 && <div className="dashboard-active-filters" aria-label="Active dashboard filters">
      <span>Active</span>
      {active.map(filter => <button key={filter.key} type="button" onClick={() => onClear(filter.key)}>{filter.label}: <b>{filter.options.find(option => option.value === filter.value)?.label || filter.value}</b><i>×</i></button>)}
      {active.length > 1 && <Button type="link" size="small" onClick={() => onClear()}>Clear all</Button>}
    </div>}
  </section>
}

export function DashboardKpiGrid({ items }: { items: DashboardKpi[] }) {
  return <section className="dashboard-kpi-grid" aria-label="Primary hiring metrics">
    {items.map(item => <button data-testid="dashboard-kpi" type="button" key={item.key} className={`dashboard-kpi tone-${item.tone || 'primary'}`} onClick={item.onClick} disabled={!item.onClick}>
      <span className="dashboard-kpi-icon">{item.icon}</span>
      <span className="dashboard-kpi-copy"><small>{item.label}</small><strong>{item.value}</strong><em>{item.helper}</em></span>
      {item.delta != null && <span className={`dashboard-kpi-delta ${item.delta > 0 ? 'up' : item.delta < 0 ? 'down' : 'flat'}`}>{item.delta > 0 ? <ArrowUpOutlined /> : item.delta < 0 ? <ArrowDownOutlined /> : null}{Math.abs(item.delta).toFixed(0)}%</span>}
      {item.sparkline && item.sparkline.length > 1 && <MiniSparkline values={item.sparkline} />}
    </button>)}
  </section>
}

function MiniSparkline({ values }: { values: number[] }) {
  const max = Math.max(...values, 1)
  const points = values.map((value, index) => `${(index / (values.length - 1)) * 78 + 1},${22 - (value / max) * 18}`).join(' ')
  return <svg className="dashboard-sparkline" viewBox="0 0 80 24" aria-hidden="true"><polyline points={points} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" /></svg>
}

export function DashboardFunnel({ title, subtitle, stages }: { title: string; subtitle: string; stages: DashboardFunnelStage[] }) {
  return <section className="dashboard-section-card dashboard-funnel-card" aria-label="Hiring lifecycle">
    <header className="dashboard-section-heading"><div><h3>{title}</h3><p>{subtitle}</p></div><span>Stage conversion</span></header>
    <div className="dashboard-funnel">
      {stages.map((stage, index) => <button type="button" key={stage.key} onClick={stage.onClick} disabled={!stage.onClick}>
        <span className="dashboard-funnel-index">{index + 1}</span>
        <span className="dashboard-funnel-copy"><small>{stage.label}</small><strong>{stage.value.toLocaleString('en-IN')}</strong>{index > 0 && <em>{stage.conversionLabel || `${stage.conversion == null ? '—' : `${stage.conversion.toFixed(0)}%`} conversion`}</em>}</span>
        {index < stages.length - 1 && <ArrowRightOutlined className="dashboard-funnel-arrow" />}
      </button>)}
    </div>
  </section>
}

const chartKindOptions: Record<DashboardChartKind, { label: string; icon: ReactNode }> = {
  bar: { label: 'Bar', icon: <BarChartOutlined /> },
  horizontalBar: { label: 'Horizontal', icon: <BarChartOutlined /> },
  line: { label: 'Line', icon: <LineChartOutlined /> },
  area: { label: 'Area', icon: <LineChartOutlined /> },
  doughnut: { label: 'Donut', icon: <PieChartOutlined /> },
}

export function DashboardChartGrid({ children }: { children: ReactNode }) {
  return <section className="dashboard-chart-grid" aria-label="Hiring analytics charts">{children}</section>
}

export function DashboardChartCard({ spec }: { spec: DashboardChartSpec }) {
  const storageKey = `dashboard.chart.${spec.id}`
  const [kind, setKind] = useState<DashboardChartKind>(() => {
    const saved = localStorage.getItem(storageKey) as DashboardChartKind | null
    return saved && spec.compatibleKinds.includes(saved) ? saved : spec.kind
  })
  const [showData, setShowData] = useState(false)
  const hasData = spec.series.some(series => series.values.some(value => Number(value || 0) !== 0))
  const colors = spec.series.length === 1
    ? spec.labels.map((_, index) => dashboardPalette[index % dashboardPalette.length])
    : spec.series.map((series, index) => series.color || dashboardPalette[index % dashboardPalette.length])
  const datasets = spec.series.map((series, seriesIndex) => ({
    label: series.label,
    data: hasData ? series.values : spec.labels.map(() => 0),
    backgroundColor: kind === 'doughnut' ? colors : (series.color || dashboardPalette[seriesIndex % dashboardPalette.length]),
    borderColor: kind === 'area' || kind === 'line' ? (series.color || dashboardPalette[seriesIndex % dashboardPalette.length]) : '#ffffff',
    borderWidth: kind === 'doughnut' ? 3 : kind === 'line' || kind === 'area' ? 2 : 0,
    borderRadius: kind === 'bar' || kind === 'horizontalBar' ? 5 : 0,
    borderSkipped: false as const,
    fill: kind === 'area',
    tension: .34,
    pointRadius: kind === 'line' || kind === 'area' ? 2.5 : 0,
    pointHoverRadius: 5,
    maxBarThickness: 30,
  }))
  const chartData = { labels: spec.labels.length ? spec.labels : ['No data'], datasets }
  const handlePoint = (_: ChartEvent, elements: { index: number; datasetIndex: number }[]) => {
    if (!elements.length || !spec.onPointClick) return
    const item = elements[0]
    spec.onPointClick(spec.labels[item.index], spec.series[item.datasetIndex]?.label || '')
  }
  const commonOptions = {
    responsive: true,
    maintainAspectRatio: false,
    animation: false as const,
    onClick: handlePoint,
    plugins: {
      legend: {
        display: hasData && (spec.series.length > 1 || kind === 'doughnut'),
        position: kind === 'doughnut' ? 'right' as const : 'bottom' as const,
        labels: { boxWidth: 8, boxHeight: 8, usePointStyle: true, pointStyle: 'circle' as const, padding: 12, color: '#59647a', font: { size: 10, weight: 600 as const } },
      },
      tooltip: { enabled: hasData, callbacks: { label: (context: { dataset: { label?: string }; formattedValue: string }) => `${context.dataset.label || spec.title}: ${context.formattedValue}${spec.valueSuffix || ''}` } },
    },
  }
  const cartesianOptions = {
    ...commonOptions,
    indexAxis: kind === 'horizontalBar' ? 'y' as const : 'x' as const,
    scales: {
      x: { stacked: spec.stacked, beginAtZero: true, grid: { display: kind !== 'horizontalBar', color: '#eef1f6' }, ticks: { color: '#758097', font: { size: 9 }, maxRotation: 35, minRotation: 0 } },
      y: { stacked: spec.stacked, beginAtZero: true, grid: { display: kind !== 'horizontalBar', color: '#eef1f6' }, ticks: { precision: 0, color: '#758097', font: { size: 9 } } },
    },
  }
  const selectKind = (next: DashboardChartKind) => { setKind(next); localStorage.setItem(storageKey, next) }
  const exportCsv = () => {
    const header = ['Label', ...spec.series.map(series => series.label)]
    const rows = spec.labels.map((label, index) => [label, ...spec.series.map(series => series.values[index] || 0)])
    const csv = [header, ...rows].map(row => row.map(value => `"${String(value).replace(/"/g, '""')}"`).join(',')).join('\n')
    const link = document.createElement('a')
    link.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }))
    link.download = `${spec.id}.csv`
    link.click()
    URL.revokeObjectURL(link.href)
  }
  return <article className="dashboard-chart-card" data-testid="dashboard-chart-card">
    <header>
      <div><h3>{spec.title}</h3><p>{spec.subtitle}</p></div>
      <Select
        data-testid={`dashboard-chart-type-${spec.id}`}
        aria-label={`${spec.title} chart type`}
        size="small"
        value={kind}
        options={spec.compatibleKinds.map(value => ({ value, label: <span className="dashboard-chart-type">{chartKindOptions[value].icon}{chartKindOptions[value].label}</span> }))}
        onChange={selectKind}
      />
    </header>
    <div className={`dashboard-chart-canvas kind-${kind}`} aria-label={`${spec.title} chart`}>
      {kind === 'doughnut'
        ? <Doughnut data={chartData} options={{ ...commonOptions, cutout: '62%' }} />
        : kind === 'line' || kind === 'area'
          ? <Line data={chartData} options={cartesianOptions} />
          : <Bar data={chartData} options={cartesianOptions} />}
      {!hasData && <div className="dashboard-chart-empty"><span>◇</span><b>{spec.emptyText || 'No data for the active filters'}</b><small>Try a wider date range or another client.</small></div>}
    </div>
    <footer><button type="button" onClick={() => setShowData(true)}><EyeOutlined /> View data</button>{spec.onPointClick && <span>Click a chart point to drill down</span>}</footer>
    <Drawer className="dashboard-data-drawer" title={spec.title} open={showData} width="min(620px, 94vw)" onClose={() => setShowData(false)} extra={<Button icon={<DownloadOutlined />} onClick={exportCsv}>Export CSV</Button>}>
      <p className="dashboard-data-subtitle">{spec.subtitle}</p>
      {spec.labels.length ? <div className="dashboard-data-table"><table><thead><tr><th>Group</th>{spec.series.map(series => <th key={series.label}>{series.label}</th>)}</tr></thead><tbody>{spec.labels.map((label, index) => <tr key={`${label}-${index}`}><td>{label}</td>{spec.series.map(series => <td key={series.label}>{Number(series.values[index] || 0).toLocaleString('en-IN')}{spec.valueSuffix || ''}</td>)}</tr>)}</tbody></table></div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />}
    </Drawer>
  </article>
}

export function DashboardActionQueue({ title, subtitle, items, emptyText }: { title: string; subtitle: string; items: DashboardQueueItem[]; emptyText: string }) {
  return <section className="dashboard-section-card dashboard-action-card" aria-label={title}>
    <header className="dashboard-section-heading"><div><h3>{title}</h3><p>{subtitle}</p></div><span>{items.length} open</span></header>
    {items.length ? <div className="dashboard-engine-action-list">{items.map(item => <article key={item.id}>
      <span className={`dashboard-action-icon priority-${item.priority.toLowerCase()}`}>{item.icon}</span>
      <div><strong>{item.title}</strong><p>{item.meta}</p><small>{item.owner} · {item.age}</small></div>
      <span className={`dashboard-priority priority-${item.priority.toLowerCase()}`}>{item.priority}</span>
      <Button size="small" onClick={item.onAction}>{item.actionLabel}<ArrowRightOutlined /></Button>
    </article>)}</div> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyText} />}
  </section>
}

function DashboardSkeleton() {
  return <div className="dashboard-engine dashboard-loading" data-testid="dashboard-loading">
    <Skeleton active paragraph={{ rows: 2 }} />
    <div className="dashboard-kpi-grid">{Array.from({ length: 8 }, (_, index) => <div className="dashboard-kpi" key={index}><Skeleton active title paragraph={{ rows: 1 }} /></div>)}</div>
    <div className="dashboard-chart-grid">{Array.from({ length: 6 }, (_, index) => <div className="dashboard-chart-card" key={index}><Skeleton active paragraph={{ rows: 5 }} /></div>)}</div>
  </div>
}
