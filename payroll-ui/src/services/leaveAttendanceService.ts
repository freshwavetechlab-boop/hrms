import type { AttendanceSettings, Holiday, LeaveAttendancePreferences, LeaveAttendanceSetup, LeaveBalanceImportMapping, LeaveBalanceImportPreview, LeaveBalanceImportResult, LeaveType, SetupStatus } from '../types/payroll'
import { api, getJson, postJson, putJson, readError } from './apiClient'

const fallback: LeaveAttendanceSetup = { isEnabled: false, steps: [] }
const preferencesFallback: LeaveAttendancePreferences = { id: 0, attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null }
const attendanceFallback: AttendanceSettings = { id: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 }

export const getLeaveAttendanceSetup = () => getJson<LeaveAttendanceSetup>('/api/leave-attendance/setup', fallback)
export const setLeaveAttendanceEnabled = (isEnabled: boolean) => postJson('/api/leave-attendance/module', { isEnabled }, fallback)
export const updateLeaveAttendanceStep = (stepCode: string, status: SetupStatus) => putJson(`/api/leave-attendance/setup/${stepCode}`, { status }, fallback)
export const getLeaveAttendancePreferences = () => getJson<LeaveAttendancePreferences>('/api/leave-attendance/preferences', preferencesFallback)
export async function saveLeaveAttendancePreferences(preferences: Omit<LeaveAttendancePreferences, 'id' | 'createdAt' | 'updatedAt'>) {
  const response = await fetch(`${api}/api/leave-attendance/preferences`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(preferences) })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveAttendancePreferences : preferencesFallback, error: response.ok ? '' : await readError(response) }
}
export const getAttendanceSettings = () => getJson<AttendanceSettings>('/api/leave-attendance/attendance-settings', attendanceFallback)
export async function saveAttendanceSettings(settings: AttendanceSettings) {
  const response = await fetch(`${api}/api/leave-attendance/attendance-settings`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settings) })
  return { ok: response.ok, data: response.ok ? await response.json() as AttendanceSettings : attendanceFallback, error: response.ok ? '' : await readError(response) }
}
export const getLeaveTypes = () => getJson<LeaveType[]>('/api/leave-attendance/leave-types', [])
export async function saveLeaveType(leaveType: LeaveType) {
  const response = await fetch(`${api}/api/leave-attendance/leave-types`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(leaveType) })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveType : null, error: response.ok ? '' : await readError(response) }
}
export async function setLeaveTypeStatus(id: number, isActive: boolean) {
  const response = await fetch(`${api}/api/leave-attendance/leave-types/${id}/status?isActive=${isActive}`, { method: 'POST' })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveType : null, error: response.ok ? '' : await readError(response) }
}
export async function deleteLeaveType(id: number) {
  const response = await fetch(`${api}/api/leave-attendance/leave-types/${id}`, { method: 'DELETE' })
  return { ok: response.ok, error: response.ok ? '' : await readError(response) }
}
export const getHolidays = (year?: number, workLocationId?: number) => getJson<Holiday[]>(`/api/leave-attendance/holidays?${new URLSearchParams({ ...(year ? { year: String(year) } : {}), ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export async function saveHoliday(holiday: Holiday) {
  const response = await fetch(`${api}/api/leave-attendance/holidays`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(holiday) })
  return { ok: response.ok, data: response.ok ? await response.json() as Holiday : null, error: response.ok ? '' : await readError(response) }
}
export async function deleteHoliday(id: number) {
  const response = await fetch(`${api}/api/leave-attendance/holidays/${id}`, { method: 'DELETE' })
  return { ok: response.ok, error: response.ok ? '' : await readError(response) }
}
export const leaveBalanceSampleUrl = () => `${api}/api/leave-attendance/import-balances/sample`
export async function previewLeaveBalanceImport(file: File, encoding: string, mapping?: LeaveBalanceImportMapping) {
  const body = new FormData()
  body.append('file', file)
  body.append('encoding', encoding)
  if (mapping) body.append('mappingJson', JSON.stringify(mapping))
  const response = await fetch(`${api}/api/leave-attendance/import-balances/preview`, { method: 'POST', body })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveBalanceImportPreview : null, error: response.ok ? '' : await readError(response) }
}
export async function finalizeLeaveBalanceImport(preview: LeaveBalanceImportPreview, encoding: string) {
  const response = await fetch(`${api}/api/leave-attendance/import-balances/finalize`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fileName: preview.fileName, encoding, mapping: preview.mapping, validRecords: preview.validRecords, errorRecords: preview.errorRecords }) })
  return { ok: response.ok, data: response.ok ? await response.json() as LeaveBalanceImportResult : null, error: response.ok ? '' : await readError(response) }
}
