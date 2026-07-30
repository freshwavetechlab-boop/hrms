import type { AttendanceBatchJobStatus, AttendanceGroup, AttendanceReviewContext, AttendanceSettings, EmployeeDailyAttendance, EmployeeMonthlyAttendance, GeoFenceEmployeeOption, GeoFenceRule, GeoFenceScope, Holiday, LeaveAttendancePreferences, LeaveAttendanceSetup, LeaveBalanceImportMapping, LeaveBalanceImportPreview, LeaveBalanceImportResult, LeaveType, SetupStatus } from '../types/payroll'
import { apiUrl, deleteJson, getBlob, getJson, getJsonResult, postForm, postFormWithProgress, postJson, putJson } from './apiClient'
import type { BulkImportStatus } from './settingsService'

const fallback: LeaveAttendanceSetup = { clientId: 0, isEnabled: false, steps: [] }
const preferencesFallback: LeaveAttendancePreferences = { id: 0, clientId: 0, workLocationId: null, workLocationName: 'All locations', workWeek: '', attendanceCycleStartDay: 1, attendanceCycleEndDay: 25, payrollReportGenerationDay: 28, includeLeaveEncashmentInPayRun: false, leaveEncashmentSalaryComponentId: null }
const attendanceFallback: AttendanceSettings = { id: 0, clientId: 0, checkInTime: '09:00:00', checkOutTime: '18:00:00', workingHoursCalculation: 'First check-in and last check-out', minimumHoursForHalfDay: 4, minimumHoursForFullDay: 8, maximumHoursAllowedForFullDay: 12, allowRegularizationRequests: true, regularizationWindow: 'Anytime', pastDaysAllowed: 7, restrictRegularizationRequestsPerMonth: false, maxRegularizationRequestsPerMonth: 3 }
export const geoFenceFallback: GeoFenceRule = { id: 0, clientId: 0, name: '', scopeType: 'Work Location', workLocationId: null, workLocationName: '', employeeIds: [], employeeNames: '', latitude: 0, longitude: 0, radiusMeters: 100, gpsToleranceMeters: 30, strictness: 'Block outside fence', allowCheckIn: true, allowCheckOut: true, effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: null, isActive: true, priority: 20 }
const reviewContextFallback: AttendanceReviewContext = { settings: attendanceFallback, schedule: { workWeek: '', salaryDays: 'Actual days', fixedDays: '30', payDay: 'Last working day', firstPayPeriod: '' }, preferences: preferencesFallback, holidays: [], leaveBalances: [] }

export const getLeaveAttendanceSetup = (clientId: number) => getJson<LeaveAttendanceSetup>(`/api/leave-attendance/setup?clientId=${clientId}`, fallback)
export const setLeaveAttendanceEnabled = (clientId: number, isEnabled: boolean) => postJson('/api/leave-attendance/module', { clientId, isEnabled }, fallback)
export const updateLeaveAttendanceStep = (clientId: number, stepCode: string, status: SetupStatus) => putJson(`/api/leave-attendance/setup/${stepCode}`, { clientId, status }, fallback)
export const getLeaveAttendancePreferences = (clientId: number, workLocationId = 0) => getJson<LeaveAttendancePreferences>(`/api/leave-attendance/preferences?${new URLSearchParams({ clientId: String(clientId), ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, preferencesFallback)
export async function saveLeaveAttendancePreferences(preferences: Omit<LeaveAttendancePreferences, 'id' | 'createdAt' | 'updatedAt'>) {
  return postJson('/api/leave-attendance/preferences', preferences, preferencesFallback)
}
export const getAttendanceSettings = (clientId: number) => getJson<AttendanceSettings>(`/api/leave-attendance/attendance-settings?clientId=${clientId}`, attendanceFallback)
export async function saveAttendanceSettings(settings: AttendanceSettings) {
  return postJson('/api/leave-attendance/attendance-settings', settings, attendanceFallback)
}
export const getGeoFenceRules = (clientId: number, scopeType?: GeoFenceScope) => getJson<GeoFenceRule[]>(`/api/leave-attendance/geo-fences?${new URLSearchParams({ clientId: String(clientId), ...(scopeType ? { scopeType } : {}) })}`, [])
export const getGeoFenceEmployees = (clientId: number, workLocationId: number) => getJson<GeoFenceEmployeeOption[]>(`/api/leave-attendance/geo-fences/employees?${new URLSearchParams({ clientId: String(clientId), workLocationId: String(workLocationId) })}`, [])
export const getApplicableGeoFenceRule = (clientId: number, employeeId: number, onDate?: string) => getJson<GeoFenceRule | null>(`/api/leave-attendance/geo-fences/applicable?${new URLSearchParams({ clientId: String(clientId), employeeId: String(employeeId), ...(onDate ? { onDate } : {}) })}`, null)
export async function saveGeoFenceRule(rule: GeoFenceRule) {
  return postJson('/api/leave-attendance/geo-fences', rule, null as GeoFenceRule | null)
}
export async function deleteGeoFenceRule(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/geo-fences/${id}?clientId=${clientId}`, null)
  return { ok: response.ok, error: response.error }
}
export const getAttendanceGroups = (clientId = 0) => getJson<AttendanceGroup[]>(`/api/leave-attendance/groups${clientId ? `?clientId=${clientId}` : ''}`, [])
export async function saveAttendanceGroup(group: AttendanceGroup) {
  return postJson('/api/leave-attendance/groups', group, null as AttendanceGroup | null)
}
export async function saveAttendanceGroupBatch(group: Pick<AttendanceGroup, 'policyBatchId' | 'clientId' | 'name' | 'workWeek' | 'attendanceCycleStartDay' | 'attendanceCycleEndDay' | 'payrollReportGenerationDay' | 'isActive' | 'employeeIds'> & { workLocationIds: number[]; departments: string[]; designations: string[] }) {
  return postJson('/api/leave-attendance/groups/batch', group, [] as AttendanceGroup[])
}
export async function deleteAttendanceGroup(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/groups/${id}?clientId=${clientId}`, null)
  return { ok: response.ok, error: response.error }
}
export const getMonthlyAttendance = (clientId: number, month: string, workLocationId = 0) => getJson<EmployeeMonthlyAttendance[]>(`/api/leave-attendance/attendance/monthly?${new URLSearchParams({ clientId: String(clientId), month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export const getAttendanceReviewContext = (clientId: number, month: string, workLocationId = 0) => getJson<AttendanceReviewContext>(`/api/leave-attendance/attendance/context?${new URLSearchParams({ clientId: String(clientId), month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, reviewContextFallback)
export const getMonthlyAttendanceResult = (clientId: number, month: string, workLocationId = 0) => getJsonResult<EmployeeMonthlyAttendance[]>(`/api/leave-attendance/attendance/monthly?${new URLSearchParams({ clientId: String(clientId), month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export const getAttendanceReviewContextResult = (clientId: number, month: string, workLocationId = 0) => getJsonResult<AttendanceReviewContext>(`/api/leave-attendance/attendance/context?${new URLSearchParams({ clientId: String(clientId), month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, reviewContextFallback)
export async function saveMonthlyAttendance(clientId: number, month: string, rows: EmployeeMonthlyAttendance[]) {
  return postJson('/api/leave-attendance/attendance/monthly', { clientId, month, rows }, [])
}
export const getDailyAttendance = (clientId: number, employeeId: number, month: string) => getJson<EmployeeDailyAttendance[]>(`/api/leave-attendance/attendance/daily?clientId=${clientId}&employeeId=${employeeId}&month=${month}`, [])
export const getDailyAttendanceGrid = (clientId: number, month: string, workLocationId = 0) => getJson<EmployeeDailyAttendance[]>(`/api/leave-attendance/attendance/daily-grid?${new URLSearchParams({ clientId: String(clientId), month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export const getDailyAttendanceGridResult = (clientId: number, month: string, workLocationId = 0) => getJsonResult<EmployeeDailyAttendance[]>(`/api/leave-attendance/attendance/daily-grid?${new URLSearchParams({ clientId: String(clientId), month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export async function saveDailyAttendance(clientId: number, employeeId: number, month: string, rows: EmployeeDailyAttendance[]) {
  return postJson('/api/leave-attendance/attendance/daily', { clientId, employeeId, month, rows }, [])
}
export async function saveDailyAttendanceBatch(clientId: number, month: string, rows: EmployeeDailyAttendance[]) {
  return postJson('/api/leave-attendance/attendance/daily/batch', { clientId, month, rows }, [])
}
const attendanceBatchJobFallback = (jobId = '', clientId = 0, month = '', errors: string[] = []): AttendanceBatchJobStatus => ({ jobId, clientId, month, state: 'Failed', stage: 'Failed', totalRows: 0, completedRows: 0, savedRows: 0, errors })
export async function startDailyAttendanceBatchJob(clientId: number, month: string, rows: EmployeeDailyAttendance[], rollupEmployeeIds: number[]) {
  return postJson('/api/leave-attendance/attendance/daily/batch-jobs', { clientId, month, rows, rollupEmployeeIds }, attendanceBatchJobFallback('', clientId, month), { toast: false, loader: false, timeoutMs: 120000 })
}
export const getDailyAttendanceBatchJob = (jobId: string) => getJsonResult<AttendanceBatchJobStatus>(`/api/leave-attendance/attendance/daily/batch-jobs/${jobId}`, attendanceBatchJobFallback(jobId, 0, '', ['Attendance save job was not found.']), { loader: false, timeoutMs: 15000 })
export const getLeaveTypes = (clientId: number) => getJson<LeaveType[]>(`/api/leave-attendance/leave-types?clientId=${clientId}`, [])
export const getLeaveTypesResult = (clientId: number) => getJsonResult<LeaveType[]>(`/api/leave-attendance/leave-types?clientId=${clientId}`, [])
export async function saveLeaveType(leaveType: LeaveType) {
  return postJson('/api/leave-attendance/leave-types', leaveType, null as LeaveType | null, { toast: false })
}
export async function setLeaveTypeStatus(clientId: number, id: number, isActive: boolean) {
  return postJson(`/api/leave-attendance/leave-types/${id}/status?clientId=${clientId}&isActive=${isActive}`, {}, null as LeaveType | null, { toast: false })
}
export async function deleteLeaveType(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/leave-types/${id}?clientId=${clientId}`, null, { toast: false })
  return { ok: response.ok, error: response.error }
}
export const downloadLeaveTypeImportTemplate = (clientId: number) => getBlob(`/api/leave-attendance/leave-types/import-template?clientId=${clientId}`)
export const startLeaveTypeImport = (clientId: number, file: File) => {
  const body = new FormData()
  body.append('clientId', String(clientId))
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/leave-attendance/leave-types/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getLeaveTypeImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/leave-attendance/leave-types/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const getHolidays = (clientId: number, year?: number, workLocationId?: number) => getJson<Holiday[]>(`/api/leave-attendance/holidays?${new URLSearchParams({ clientId: String(clientId), ...(year ? { year: String(year) } : {}), ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })}`, [])
export async function saveHoliday(holiday: Holiday) {
  return postJson('/api/leave-attendance/holidays', holiday, null as Holiday | null, { toast: false })
}
export async function deleteHoliday(clientId: number, id: number) {
  const response = await deleteJson(`/api/leave-attendance/holidays/${id}?clientId=${clientId}`, null, { toast: false })
  return { ok: response.ok, error: response.error }
}
export const downloadHolidayImportTemplate = (clientId: number) => getBlob(`/api/leave-attendance/holidays/import-template?clientId=${clientId}`)
export const startHolidayImport = (clientId: number, file: File) => {
  const body = new FormData()
  body.append('clientId', String(clientId))
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/leave-attendance/holidays/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getHolidayImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/leave-attendance/holidays/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
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
