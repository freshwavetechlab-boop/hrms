import { getJson } from './apiClient'
export type ReportResult = { title: string; columns: string[]; rows: Record<string, string | number | boolean | null>[] }
export type ReportFilters = { month?: string; fromDate?: string; toDate?: string }
export const runReport = (code: string, clientId: number, filters: ReportFilters = {}) => {
  const params = new URLSearchParams({ clientId: String(clientId) })
  if (filters.month) params.set('month', filters.month)
  if (filters.fromDate) params.set('fromDate', filters.fromDate)
  if (filters.toDate) params.set('toDate', filters.toDate)
  return getJson<ReportResult>(`/api/reports/${code}?${params}`, { title: code, columns: [], rows: [] })
}
