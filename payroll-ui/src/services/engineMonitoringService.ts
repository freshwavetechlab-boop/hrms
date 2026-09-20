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
  queuedRequests?: number
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

export type EngineActivity = {
  id: string; engineCode: string; operation: string; referenceType: string; referenceId: string
  applicationId: number | null; candidateName: string; applicationCode: string; positionTitle: string; attempt: number | null
  startedAtUtc: string; completedAtUtc: string | null; updatedAtUtc: string
  durationMs: number | null; queueWaitMs: number | null; aiDurationMs: number | null; aiStatus: string
  status: string; httpStatus: number | null; failureCode: string; failureReason?: string
}
export type EngineActivityPage = { deployment: string; recordingEnabled: boolean; retentionDays: number; droppedRecords: number; warning: string; hasMore: boolean; items: EngineActivity[] }
export const getEngineActivity = (params: { from: string; until: string; engine?: string; status?: string; before?: string; beforeId?: string }) => getJsonResult<EngineActivityPage>(
  `/api/system/engine-monitoring/activity?${new URLSearchParams(params)}`,
  { deployment: '', recordingEnabled: false, retentionDays: 7, droppedRecords: 0, warning: '', hasMore: false, items: [] },
  { loader: false, timeoutMs: 15000 })

export type EngineHistory = {
  deployment: string; fromUtc: string; untilUtc: string; bucketMinutes: number; recordingEnabled: boolean
  lastSavedAtUtc: string | null; droppedBuckets: number; warning: string
  engines: Array<{ code: string; name: string; trend: Array<{ timeUtc: string; requests: number | null; failures: number | null;
    loadPercent: number | null; averageDurationMs: number | null; maxDurationMs: number | null; observedInstanceSeconds: number }> }>
}
export const getEngineHistory = (from: string, until: string) => getJsonResult<EngineHistory>(
  `/api/system/engine-monitoring/history?${new URLSearchParams({ from, until })}`,
  { deployment: '', fromUtc: from, untilUtc: until, bucketMinutes: 5, recordingEnabled: false, lastSavedAtUtc: null, droppedBuckets: 0, warning: '', engines: [] },
  { loader: false, timeoutMs: 15000 })
