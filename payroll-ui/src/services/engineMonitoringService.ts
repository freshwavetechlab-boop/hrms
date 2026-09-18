import { apiUrl, getJsonResult } from './apiClient'

export type EngineTrendPoint = { timeUtc: string; loadPercent: number; requests: number; failures: number; averageDurationMs: number }
export type EngineRuntimeMetric = {
  code: string
  name: string
  category: string
  description: string
  coverage: string
  state: 'Idle' | 'Active' | 'Observed' | 'Attention'
  loadPercent: number
  activeRequests: number
  requestsLastFiveMinutes: number
  successRate: number
  averageDurationMs: number
  p95DurationMs: number
  lastActivityUtc?: string | null
  lastError: string
  trend: EngineTrendPoint[]
}
export type EngineMonitoringSnapshot = {
  generatedAtUtc: string
  refreshAfterSeconds: number
  isReadOnly: boolean
  accessPolicy: string
  process: { cpuPercent: number; workingSetMb: number; managedMemoryMb: number; threadCount: number; uptimeSeconds: number }
  engines: EngineRuntimeMetric[]
}

export const emptyEngineMonitoringSnapshot = (): EngineMonitoringSnapshot => ({
  generatedAtUtc: '', refreshAfterSeconds: 5, isReadOnly: true, accessPolicy: 'Super admin only',
  process: { cpuPercent: 0, workingSetMb: 0, managedMemoryMb: 0, threadCount: 0, uptimeSeconds: 0 }, engines: [],
})

export const getEngineMonitoring = () => getJsonResult('/api/system/engine-monitoring', emptyEngineMonitoringSnapshot(), { loader: false, timeoutMs: 8000 })
export const engineMonitoringStreamUrl = () => apiUrl('/api/system/engine-monitoring/stream')
