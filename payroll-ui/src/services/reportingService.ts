import { getJson } from './apiClient'
export type ReportResult = { title: string; columns: string[]; rows: Record<string, string | number | boolean | null>[] }
export type ReportFilters = { month?: string; fromDate?: string; toDate?: string; payRunId?: number; employeeId?: number; componentCode?: string }
export const runReport = (code: string, clientId: number, filters: ReportFilters = {}) => {
  const params = new URLSearchParams({ clientId: String(clientId) })
  if (filters.month) params.set('month', filters.month)
  if (filters.fromDate) params.set('fromDate', filters.fromDate)
  if (filters.toDate) params.set('toDate', filters.toDate)
  if (filters.payRunId) params.set('payRunId', String(filters.payRunId))
  if (filters.employeeId) params.set('employeeId', String(filters.employeeId))
  if (filters.componentCode) params.set('componentCode', filters.componentCode)
  return getJson<ReportResult>(`/api/reports/${code}?${params}`, { title: code, columns: [], rows: [] })
}
