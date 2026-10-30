import { getJsonResult, postJson } from './apiClient'
export type OverviewScope = { clientId: number; batchId: string }
export type OverviewBatch = OverviewScope & { clientName: string; month: string; fileName: string; sheetName: string; rowCount: number; createdAtUtc: string; hasCalculationSource: boolean }
export type OverviewRow = OverviewScope & {
  key: string; clientName: string; month: string; fileName: string; sheetName: string; sourceRow: number; employeeCode: string; employeeName: string
  masterEmployeeId: number | null; masterStatus: 'Matched' | 'Unmatched' | 'Conflict' | 'Missing code'; masterReason: string
  readiness: 'Ready' | 'Review' | 'Blocked'; reasons: string[]; scopeOverlap: boolean; hasCalculationSource: boolean; setupDecision: string
}
export type OverviewRequest = { scopes: OverviewScope[]; search: string; readiness: string; masterStatus: string; page: number; pageSize: number; overlapOnly?: boolean }
export type OverviewTotals = {
  sourceRows: number; readyRows: number; reviewRows: number; blockedRows: number; uniqueScopedCodeGroups: number; matchedMasterRecords: number; matchedRows: number; unmatchedRows: number; conflictRows: number; missingCodeRows: number; overlapRows: number
  sourceNetPay?: number; sourceGrossEarnings?: number; sourceDeductions?: number; monetaryRoundingBasis?: string
  uncountedEmployeeRows?: number
}
export type OverviewResult = { scopeLabel: string; snapshot: string; observedAtUtc: string; scopeBatchCount: number; scopeTotalRows: number; page: number; pageSize: number; totals: OverviewTotals; rows: OverviewRow[] }
export const getOverviewScopes = (signal?: AbortSignal) => getJsonResult<OverviewBatch[]>('/api/excel-payslips/overview-scopes', [], {
  toast: false,
  timeoutMessage: 'Saved batch list took too long to load. Retry the client/batch list.',
  ...(signal ? { signal } : {}),
})
export const previewOverview = (request: OverviewRequest, signal?: AbortSignal) => postJson<OverviewRequest, OverviewResult | null>('/api/excel-payslips/overview-preview', request, null, { toast: false, loader: false, timeoutMs: 120000, signal })
