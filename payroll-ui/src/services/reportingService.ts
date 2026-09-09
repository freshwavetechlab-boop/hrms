import { getJson, getJsonResult } from './apiClient'
export type ReportResult = { title: string; columns: string[]; rows: Record<string, string | number | boolean | null>[] }
export type ReportFilters = { month?: string; fromDate?: string; toDate?: string; payRunId?: number; employeeId?: number; componentCode?: string }
const reportPath = (code: string, clientId: number, filters: ReportFilters = {}) => {
  const params = new URLSearchParams({ clientId: String(clientId) })
  if (filters.month) params.set('month', filters.month)
  if (filters.fromDate) params.set('fromDate', filters.fromDate)
  if (filters.toDate) params.set('toDate', filters.toDate)
  if (filters.payRunId) params.set('payRunId', String(filters.payRunId))
  if (filters.employeeId) params.set('employeeId', String(filters.employeeId))
  if (filters.componentCode) params.set('componentCode', filters.componentCode)
  return `/api/reports/${code}?${params}`
}
const emptyReport = (code: string): ReportResult => ({ title: code, columns: [], rows: [] })
export const runReport = (code: string, clientId: number, filters: ReportFilters = {}) => getJson<ReportResult>(reportPath(code, clientId, filters), emptyReport(code))
export const runReportResult = (code: string, clientId: number, filters: ReportFilters = {}) => getJsonResult<ReportResult>(reportPath(code, clientId, filters), emptyReport(code), { toast: false })
