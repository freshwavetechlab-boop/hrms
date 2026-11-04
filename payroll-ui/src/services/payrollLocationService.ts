import { getJsonResult, postJson } from './apiClient'

export type LocationAttendanceRow = {
  key: string; assignmentId: string; referenceAssignmentId?: string | null; employeeId: number; employeeCode: string; employeeName: string
  workLocationId: number; workLocationName: string; templateId: string; templateName: string; rulesHash: string; inputVersion: string; nativePart: boolean
  payrollDays: number; monthlyPayrollDays: number; presentDays: number | null; payableDays: number | null; lockedPayRunId?: number | null; issue: string; recorded: boolean
}
export type LocationAttendanceContext = { clientId: number; month: string; policyBatchId: string; groupIds: number[]; version: string; storageAvailable: boolean; rows: LocationAttendanceRow[] }
export type LocationAttendanceInput = Pick<LocationAttendanceRow, 'key' | 'employeeId' | 'workLocationId' | 'payrollDays' | 'inputVersion'> & { presentDays: number; payableDays: number }
export type LocationAttendanceRequest = { requestId: string; clientId: number; month: string; policyBatchId: string; groupIds: number[]; workLocationId: number; contextVersion: string; rows: LocationAttendanceInput[] }
export type LocationAttendanceResult = { requestId: string; savedParts: number; publishedEmployees: number; pendingEmployees: number; context: LocationAttendanceContext }
export const getPayrollLocationAttendance = (clientId: number, month: string, policyBatchId: string, groupIds: number[]) =>
  getJsonResult<LocationAttendanceContext | null>(`/api/leave-attendance/attendance/payroll-locations?${new URLSearchParams({ clientId: String(clientId), month, policyBatchId, groupIds: groupIds.join(',') })}`, null, { loader: false, toast: false })
export const savePayrollLocationAttendance = (request: LocationAttendanceRequest) =>
  postJson<LocationAttendanceRequest, LocationAttendanceResult | null>('/api/leave-attendance/attendance/payroll-locations', request, null, { loader: false, toast: false, timeoutMs: 180000 })
