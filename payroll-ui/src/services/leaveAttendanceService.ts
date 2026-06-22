import type { AttendanceSettings, EmployeeDailyAttendance, EmployeeMonthlyAttendance, Holiday, LeaveAttendancePreferences, LeaveAttendanceSetup, LeaveBalanceImportMapping, LeaveBalanceImportPreview, LeaveBalanceImportResult, LeaveType, SetupStatus } from '../types/payroll'
import { api, getJson, postJson, putJson, readError } from './apiClient'

const fallback: LeaveAttendanceSetup = { clientId: 0, isEnabled: false, steps: [] }
const preferencesFallback: LeaveAttendancePreferences = { id: 0, clientId: 0, attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null }
const attendanceFallback: AttendanceSettings = { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 }

export const getLeaveAttendanceSetup = (clientId: number) => getJson<LeaveAttendanceSetup>(`/api/leave-attendance/setup?clientId=${clientId}`, fallback)
export const setLeaveAttendanceEnabled = (clientId: number, isEnabled: boolean) => postJson('/api/leave-attendance/module', { clientId, isEnabled }, fallback)
export const updateLeaveAttendanceStep = (clientId: number, stepCode: string, status: SetupStatus) => putJson(`/api/leave-attendance/setup/${stepCode}`, { clientId, status }, fallback)
export const getLeaveAttendancePreferences = (clientId: number) => getJson<LeaveAttendancePreferences>(`/api/leave-attendance/preferences?clientId=${clientId}`, preferencesFallback)
export async function saveLeaveAttendancePreferences(preferences: Omit<LeaveAttendancePreferences, 'id' | 'createdAt' | 'updatedAt'>) {
  const response = await fetch(`${api}/api/leave-attendance/preferences`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(preferences) })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveAttendancePreferences : preferencesFallback, error: response.ok ? '' : await readError(response) }
}
export const getAttendanceSettings = (clientId: number) => getJson<AttendanceSettings>(`/api/leave-attendance/attendance-settings?clientId=${clientId}`, attendanceFallback)
export async function saveAttendanceSettings(settings: AttendanceSettings) {
  const response = await fetch(`${api}/api/leave-attendance/attendance-settings`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settings) })
  return { ok: response.ok, data: response.ok ? await response.json() as AttendanceSettings : attendanceFallback, error: response.ok ? '' : await readError(response) }
}
export const getMonthlyAttendance = (clientId: number, month: string) => getJson<EmployeeMonthlyAttendance[]>(`/api/leave-attendance/attendance/monthly?clientId=${clientId}&month=${month}`, [])
export async function saveMonthlyAttendance(clientId: number, month: string, rows: EmployeeMonthlyAttendance[]) {
  const response = await fetch(`${api}/api/leave-attendance/attendance/monthly`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientId, month, rows }) })
  return { ok: response.ok, data: response.ok ? await response.json() as EmployeeMonthlyAttendance[] : [], error: response.ok ? '' : await readError(response) }
}
export const getDailyAttendance = (clientId: number, employeeId: number, month: string) => getJson<EmployeeDailyAttendance[]>(`/api/leave-attendance/attendance/daily?clientId=${clientId}&employeeId=${employeeId}&month=${month}`, [])
export async function saveDailyAttendance(clientId: number, employeeId: number, month: string, rows: EmployeeDailyAttendance[]) {
  const response = await fetch(`${api}/api/leave-attendance/attendance/daily`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientId, employeeId, month, rows }) })
  return { ok: response.ok, data: response.ok ? await response.json() as EmployeeDailyAttendance[] : [], error: response.ok ? '' : await readError(response) }
}
export const getLeaveTypes = (clientId: number) => getJson<LeaveType[]>(`/api/leave-attendance/leave-types?clientId=${clientId}`, [])
export async function saveLeaveType(leaveType: LeaveType) {
  const response = await fetch(`${api}/api/leave-attendance/leave-types`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(leaveType) })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveType : null, error: response.ok ? '' : await readError(response) }
}
export async function setLeaveTypeStatus(clientId: number, id: number, isActive: boolean) {
  const response = await fetch(`${api}/api/leave-attendance/leave-types/${id}/status?clientId=${clientId}&isActive=${isActive}`, { method: 'POST' })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveType : null, error: response.ok ? '' : await readError(response) }
}
export async function deleteLeaveType(clientId: number, id: number) {
  const response = await fetch(`${api}/api/leave-attendance/leave-types/${id}?clientId=${clientId}`, { method: 'DELETE' })
  return { ok: response.ok, error: response.ok ? '' : await readError(response) }
}
export const getHolidays = (clientId: number, year?: number, workLocationId?: number) => getJson<Holiday[]>(`/api/leave-attendance/holidays?${new URLSearchParams({ clientId: String(clientId), ...(year ? { year: String(year) } : {}), ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export async function saveHoliday(holiday: Holiday) {
  const response = await fetch(`${api}/api/leave-attendance/holidays`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(holiday) })
  return { ok: response.ok, data: response.ok ? await response.json() as Holiday : null, error: response.ok ? '' : await readError(response) }
}
export async function deleteHoliday(clientId: number, id: number) {
  const response = await fetch(`${api}/api/leave-attendance/holidays/${id}?clientId=${clientId}`, { method: 'DELETE' })
  return { ok: response.ok, error: response.ok ? '' : await readError(response) }
}
export const leaveBalanceSampleUrl = (clientId: number) => `${api}/api/leave-attendance/import-balances/sample?clientId=${clientId}`
export async function previewLeaveBalanceImport(clientId: number, file: File, encoding: string, mapping?: LeaveBalanceImportMapping) {
  const body = new FormData()
  body.append('file', file)
  body.append('encoding', encoding)
  body.append('clientId', String(clientId))
  if (mapping) body.append('mappingJson', JSON.stringify(mapping))
  const response = await fetch(`${api}/api/leave-attendance/import-balances/preview`, { method: 'POST', body })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveBalanceImportPreview : null, error: response.ok ? '' : await readError(response) }
}
export async function finalizeLeaveBalanceImport(clientId: number, preview: LeaveBalanceImportPreview, encoding: string) {
  const response = await fetch(`${api}/api/leave-attendance/import-balances/finalize`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientId, fileName: preview.fileName, encoding, mapping: preview.mapping, validRecords: preview.validRecords, errorRecords: preview.errorRecords }) })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveBalanceImportResult : null, error: response.ok ? '' : await readError(response) }
}
