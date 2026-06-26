import type { AttendanceSettings, EmployeeDailyAttendance, EmployeeMonthlyAttendance, GeoFenceRule, GeoFenceScope, Holiday, LeaveAttendancePreferences, LeaveAttendanceSetup, LeaveBalanceImportMapping, LeaveBalanceImportPreview, LeaveBalanceImportResult, LeaveType, SetupStatus } from '../types/payroll'
import { apiUrl, deleteJson, getBlob, getJson, postFormWithProgress, postJson, putJson } from './apiClient'

const fallback: LeaveAttendanceSetup = { clientId: 0, isEnabled: false, steps: [] }
const preferencesFallback: LeaveAttendancePreferences = { id: 0, clientId: 0, attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null }
const attendanceFallback: AttendanceSettings = { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 }
export const geoFenceFallback: GeoFenceRule = { id: 0, clientId: 0, name: '', scopeType: 'Work Location', workLocationId: null, workLocationName: '', employeeIds: [], employeeNames: '', latitude: 0, longitude: 0, radiusMeters: 100, gpsToleranceMeters: 30, strictness: 'Block outside fence', allowCheckIn: true, allowCheckOut: true, effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: null, isActive: true, priority: 20 }

export const getLeaveAttendanceSetup = (clientId: number) => getJson<LeaveAttendanceSetup>(`/api/leave-attendance/setup?clientId=${clientId}`, fallback)
export const setLeaveAttendanceEnabled = (clientId: number, isEnabled: boolean) => postJson('/api/leave-attendance/module', { clientId, isEnabled }, fallback)
export const updateLeaveAttendanceStep = (clientId: number, stepCode: string, status: SetupStatus) => putJson(`/api/leave-attendance/setup/${stepCode}`, { clientId, status }, fallback)
export const getLeaveAttendancePreferences = (clientId: number) => getJson<LeaveAttendancePreferences>(`/api/leave-attendance/preferences?clientId=${clientId}`, preferencesFallback)
export async function saveLeaveAttendancePreferences(preferences: Omit<LeaveAttendancePreferences, 'id' | 'createdAt' | 'updatedAt'>) {
  return postJson('/api/leave-attendance/preferences', preferences, preferencesFallback)
}
export const getAttendanceSettings = (clientId: number) => getJson<AttendanceSettings>(`/api/leave-attendance/attendance-settings?clientId=${clientId}`, attendanceFallback)
export async function saveAttendanceSettings(settings: AttendanceSettings) {
  return postJson('/api/leave-attendance/attendance-settings', settings, attendanceFallback)
}
export const getGeoFenceRules = (clientId: number, scopeType?: GeoFenceScope) => getJson<GeoFenceRule[]>(`/api/leave-attendance/geo-fences?${new URLSearchParams({ clientId: String(clientId), ...(scopeType ? { scopeType } : {}) })}`, [])
export const getApplicableGeoFenceRule = (clientId: number, employeeId: number, onDate?: string) => getJson<GeoFenceRule | null>(`/api/leave-attendance/geo-fences/applicable?${new URLSearchParams({ clientId: String(clientId), employeeId: String(employeeId), ...(onDate ? { onDate } : {}) })}`, null)
export async function saveGeoFenceRule(rule: GeoFenceRule) {
  return postJson('/api/leave-attendance/geo-fences', rule, null as GeoFenceRule | null)
}
export async function deleteGeoFenceRule(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/geo-fences/${id}?clientId=${clientId}`, null)
  return { ok: response.ok, error: response.error }
}
export const getMonthlyAttendance = (clientId: number, month: string) => getJson<EmployeeMonthlyAttendance[]>(`/api/leave-attendance/attendance/monthly?clientId=${clientId}&month=${month}`, [])
export async function saveMonthlyAttendance(clientId: number, month: string, rows: EmployeeMonthlyAttendance[]) {
  return postJson('/api/leave-attendance/attendance/monthly', { clientId, month, rows }, [])
}
export const getDailyAttendance = (clientId: number, employeeId: number, month: string) => getJson<EmployeeDailyAttendance[]>(`/api/leave-attendance/attendance/daily?clientId=${clientId}&employeeId=${employeeId}&month=${month}`, [])
export async function saveDailyAttendance(clientId: number, employeeId: number, month: string, rows: EmployeeDailyAttendance[]) {
  return postJson('/api/leave-attendance/attendance/daily', { clientId, employeeId, month, rows }, [])
}
export const getLeaveTypes = (clientId: number) => getJson<LeaveType[]>(`/api/leave-attendance/leave-types?clientId=${clientId}`, [])
export async function saveLeaveType(leaveType: LeaveType) {
  return postJson('/api/leave-attendance/leave-types', leaveType, null as LeaveType | null)
}
export async function setLeaveTypeStatus(clientId: number, id: number, isActive: boolean) {
  return postJson(`/api/leave-attendance/leave-types/${id}/status?clientId=${clientId}&isActive=${isActive}`, {}, null as LeaveType | null)
}
export async function deleteLeaveType(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/leave-types/${id}?clientId=${clientId}`, null)
  return { ok: response.ok, error: response.error }
}
export const getHolidays = (clientId: number, year?: number, workLocationId?: number) => getJson<Holiday[]>(`/api/leave-attendance/holidays?${new URLSearchParams({ clientId: String(clientId), ...(year ? { year: String(year) } : {}), ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export async function saveHoliday(holiday: Holiday) {
  return postJson('/api/leave-attendance/holidays', holiday, null as Holiday | null)
}
export async function deleteHoliday(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/holidays/${id}?clientId=${clientId}`, null)
  return { ok: response.ok, error: response.error }
}
export const leaveBalanceSampleUrl = (clientId: number) => apiUrl(`/api/leave-attendance/import-balances/sample?clientId=${clientId}`)
export const downloadLeaveBalanceSample = (clientId: number) => getBlob(`/api/leave-attendance/import-balances/sample?clientId=${clientId}`)
export async function previewLeaveBalanceImport(clientId: number, file: File, encoding: string, mapping?: LeaveBalanceImportMapping, onProgress: (percent: number) => void = () => {}) {
  const body = new FormData()
  body.append('file', file)
  body.append('encoding', encoding)
  body.append('clientId', String(clientId))
  if (mapping) body.append('mappingJson', JSON.stringify(mapping))
  return postFormWithProgress('/api/leave-attendance/import-balances/preview', body, null as LeaveBalanceImportPreview | null, onProgress)
}
export async function finalizeLeaveBalanceImport(clientId: number, preview: LeaveBalanceImportPreview, encoding: string) {
  return postJson('/api/leave-attendance/import-balances/finalize', { clientId, fileName: preview.fileName, encoding, mapping: preview.mapping, validRecords: preview.validRecords, errorRecords: preview.errorRecords }, null as LeaveBalanceImportResult | null)
}
