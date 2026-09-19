
export type EngineChartMetric = 'requests' | 'busy'
const palette = ['#635bff', '#0f9f6e', '#1677ff', '#f59e0b', '#e5484d', '#06b6d4', '#8b5cf6', '#64748b']

// Show measured completed work separately: a 100ms operation can correctly
// round to 0% busy in a 30-second window, but must still count as one operation.
export function engineActivityData(engines: Array<{ name: string; trend: Array<{ timeUtc: string; requests: number | null; loadPercent: number | null }> }>, metric: EngineChartMetric, includeDate = false) {
  return {
    labels: engines[0]?.trend.map(point => new Date(point.timeUtc).toLocaleString([], { ...(includeDate ? { month: 'short', day: '2-digit' } as const : {}), hour: '2-digit', minute: '2-digit', ...(includeDate ? {} : { second: '2-digit' as const }) })) ?? [],
    datasets: engines.map((engine, index) => ({
      label: engine.name,
      data: engine.trend.map(point => metric === 'requests' ? point.requests : point.loadPercent),
      borderColor: palette[index % palette.length],
      backgroundColor: `${palette[index % palette.length]}18`,
      borderWidth: 2,
      pointRadius: metric === 'requests' ? 3 : 0,
      tension: 0,
      stepped: metric === 'requests',
      fill: false,
      spanGaps: false,
    })),
  }
}
