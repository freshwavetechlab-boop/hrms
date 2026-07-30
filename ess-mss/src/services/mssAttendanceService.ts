import type { AttendanceBatchJobStatus, AttendanceReviewContext, Drop, EmployeeDailyAttendance, EmployeeMonthlyAttendance, LeaveType } from '../types'
import { essFetch } from './essApi'

type ApiResult<T> = { ok: boolean; data: T; error: string; status: number }

async function result<T>(response: Response, fallback: T): Promise<ApiResult<T>> {
  if (response.ok) return { ok: true, data: await response.json() as T, error: '', status: response.status }
  const body = await response.json().catch(() => ({})) as { error?: string | { message?: string } }
  const error = typeof body.error === 'string' ? body.error : body.error?.message || `Request failed (${response.status}).`
  return { ok: false, data: fallback, error, status: response.status }
}

const query = (month: string, workLocationId = 0) => new URLSearchParams({ month, ...(workLocationId ? { workLocationId: String(workLocationId) } : {}) })
const emptyJob = (jobId = '', clientId = 0, month = ''): AttendanceBatchJobStatus => ({ jobId, clientId, month, state: 'Failed', stage: 'Failed', totalRows: 0, completedRows: 0, savedRows: 0, errors: [] })

export const getAttendanceReviewContextResult = (_clientId: number, month: string, workLocationId = 0) =>
  essFetch(`/api/ess/mss/attendance/context?${query(month, workLocationId)}`).then(response => result(response, {} as AttendanceReviewContext))

export const getMonthlyAttendanceResult = (_clientId: number, month: string, workLocationId = 0) =>
  essFetch(`/api/ess/mss/attendance/monthly?${query(month, workLocationId)}`).then(response => result(response, [] as EmployeeMonthlyAttendance[]))

export const getDailyAttendanceGridResult = (_clientId: number, month: string, workLocationId = 0) =>
  essFetch(`/api/ess/mss/attendance/daily-grid?${query(month, workLocationId)}`).then(response => result(response, [] as EmployeeDailyAttendance[]))

export const getLeaveTypesResult = (_clientId: number) =>
  essFetch('/api/ess/mss/attendance/leave-types').then(response => result(response, [] as LeaveType[]))

export const getDropdownsResult = () =>
  essFetch('/api/ess/mss/attendance/dropdowns').then(response => result(response, [] as Drop[]))

export const startDailyAttendanceBatchJob = (clientId: number, month: string, rows: EmployeeDailyAttendance[], rollupEmployeeIds: number[]) =>
  essFetch('/api/ess/mss/attendance/daily/batch-jobs', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientId, month, rows, rollupEmployeeIds }),
  }).then(response => result(response, emptyJob('', clientId, month)))

export const getDailyAttendanceBatchJob = (jobId: string) =>
  essFetch(`/api/ess/mss/attendance/daily/batch-jobs/${jobId}`).then(response => result(response, emptyJob(jobId)))
