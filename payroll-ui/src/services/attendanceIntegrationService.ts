import { deleteJson, getJsonResult, postJson } from './apiClient'

export type AttendanceDevice = { deviceId: string; name: string; clientId: number; workLocationId: number; isActive: boolean; hasToken?: boolean; tokenHint?: string; tokenExpiresAt?: string | null }
export type AttendanceConfigurationGap = { key: string; clientId: number; clientName: string; title: string; detail: string; route: string; requiredPermissions: string[] }
export const getAttendanceGaps = (clientId?: number) => getJsonResult<AttendanceConfigurationGap[]>(`/api/leave-attendance/configuration-gaps${clientId ? `?clientId=${clientId}` : ''}`, [], { loader: false, toast: false })
export const getAttendanceDevices = (clientId: number) => getJsonResult<AttendanceDevice[]>(`/api/integrations/attendance/devices?clientId=${clientId}`, [])
export const saveAttendanceDevice = (device: AttendanceDevice) => postJson('/api/integrations/attendance/devices', device, null)
export const generateAttendanceToken = (device: AttendanceDevice, validDays: number) => postJson(`/api/integrations/attendance/devices/${encodeURIComponent(device.deviceId)}/token`, { clientId: device.clientId, validDays }, { token: '' }, { toast: false })
export const revokeAttendanceToken = (device: AttendanceDevice) => deleteJson(`/api/integrations/attendance/devices/${encodeURIComponent(device.deviceId)}/token?clientId=${device.clientId}`, null)
export const attendanceActionsChanged = () => window.dispatchEvent(new Event('hrms:actions-changed'))
