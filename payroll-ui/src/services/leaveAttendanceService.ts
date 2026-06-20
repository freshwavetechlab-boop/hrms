import type { LeaveAttendanceSetup, SetupStatus } from '../types/payroll'
import { getJson, postJson, putJson } from './apiClient'

const fallback: LeaveAttendanceSetup = { isEnabled: false, steps: [] }

export const getLeaveAttendanceSetup = () => getJson<LeaveAttendanceSetup>('/api/leave-attendance/setup', fallback)
export const setLeaveAttendanceEnabled = (isEnabled: boolean) => postJson('/api/leave-attendance/module', { isEnabled }, fallback)
export const updateLeaveAttendanceStep = (stepCode: string, status: SetupStatus) => putJson(`/api/leave-attendance/setup/${stepCode}`, { status }, fallback)
