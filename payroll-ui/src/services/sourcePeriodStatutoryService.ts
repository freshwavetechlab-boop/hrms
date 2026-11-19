import { getJsonResult, postJson } from './apiClient'

export type SourcePeriodStatutoryRow = {
  employeeId: number; sourceRowId: string; mode: 'ReviewedAmount'; amount: 0;
  sourceZeroStatutoryColumns?: Record<string, number>
}
export type SourcePeriodStatutoryRequest = {
  sourcePeriodReviewVersion: 2; clientId: number; payPeriod: string; sourceBatchId: string;
  sourceHash: string; reason: string; rows: SourcePeriodStatutoryRow[]
}
export type SourcePeriodStatutoryPreview = {
  planHash: string; sourceHash: string; request: SourcePeriodStatutoryRequest; canSave: boolean; blockers: string[];
  rows: { employeeId: number; employeeCode: string; employeeName: string; sourceRowId: string; blockers: string[] }[]
}
const base = '/api/payroll-calculation/period-professional-tax'
export const sourcePeriodStatutoryCapabilities = (clientId: number) => getJsonResult<{ version: number; sourceZeroStatutorySupported: boolean; sourceAttendanceOnlySupported?: boolean; payableOnlyAttendanceEnabled?: boolean } | null>(`${base}/source-period-capabilities?clientId=${clientId}`, null)
export const previewSourcePeriodStatutory = (request: SourcePeriodStatutoryRequest) => postJson(`${base}/source-period/preview`, request, null as SourcePeriodStatutoryPreview | null)
export const saveSourcePeriodStatutory = (preview: SourcePeriodStatutoryPreview, requestId: string) => postJson(`${base}/source-period/save`, {
  request: preview.request, expectedPlanHash: preview.planHash, requestId, acknowledged: true,
}, null as { requestId: string; savedEmployees: number; replayed: boolean } | null)

export type SourceMonthAttendanceRequest = {
  version: 1; clientId: number; payPeriod: string; sourceBatchId: string; sourceHash: string;
  divisorColumn: number; payableColumn: number; cycleStartDay: number; cycleEndDay: number;
  reviewedExtraPaidUnits: boolean; reason: string; rows: { employeeId: number; sourceRowId: string }[]
}
export type SourceMonthAttendancePreview = {
  request: SourceMonthAttendanceRequest; planHash: string; canSave: boolean; blockers: string[];
  rows: { employeeId: number; employeeCode: string; sourceRowId: string; divisor: number; payableUnits: number; blockers: string[] }[]
}
export const previewSourceMonthAttendance = (request: SourceMonthAttendanceRequest) => postJson(`${base}/source-attendance/preview`,request,null as SourceMonthAttendancePreview | null)
export const saveSourceMonthAttendance = (preview: SourceMonthAttendancePreview, requestId: string) => postJson(`${base}/source-attendance/save`,{
  request: preview.request, expectedPlanHash: preview.planHash, requestId, acknowledged: true,
},null as {requestId:string;savedEmployees:number;replayed:boolean}|null)
