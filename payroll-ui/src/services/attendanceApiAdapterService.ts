import { deleteJson, getJsonResult, postJson } from './apiClient'

export type AdapterPunch = { punchId: string; employeeCode: string; action: string; capturedAt: string }
export type AdapterRun = {
  success: boolean; preview: boolean; httpStatus?: number; fetched: number; mapped: number; skipped: number
  accepted: number; duplicates: number; rejected: number; message: string; errors: string[]; samples: AdapterPunch[]
  startedAt: string; durationMs: number
}
export type AttendanceApiAdapter = {
  id: string; clientId: number; revision: string; name: string; isActive: boolean; deviceId: string; sourceDeviceId: string
  url: string; method: 'GET' | 'POST'; authType: 'None' | 'ApiKey' | 'Bearer' | 'Basic'; authHeader: string; username: string
  hasSecret: boolean; hasDeviceToken: boolean; allowPrivateNetwork: boolean; headers: Record<string, string>; query: Record<string, string>
  bodyTemplate: string; requestDateFormat: string; recordsPath: string; successPath: string; successValue: string
  employeePath: string; devicePath: string; timePath: string; actionPath: string; punchIdPath: string; punchTimeFormat: string
  timeZoneId: string; inValues: string[]; outValues: string[]; employeeMap: Record<string, string>
  pagination: 'None' | 'Page' | 'Offset'; firstPage: number; pageSize: number; maxPages: number; timeoutSeconds: number
  syncIntervalMinutes: number; lookbackDays: number; lastAttemptAt?: string; lastSuccessAt?: string; lastRun?: AdapterRun
}
export const emptyAttendanceAdapter = (clientId: number): AttendanceApiAdapter => ({
  id: '', clientId, revision: '', name: '', isActive: false, deviceId: '', sourceDeviceId: '', url: '', method: 'POST',
  authType: 'ApiKey', authHeader: 'X-Api-Key', username: '', hasSecret: false, hasDeviceToken: false,
  allowPrivateNetwork: false, headers: {}, query: {}, bodyTemplate: '{}', requestDateFormat: 'yyyy-MM-dd', recordsPath: 'Data',
  successPath: 'Success', successValue: 'true', employeePath: 'UserID', devicePath: 'DeviceID', timePath: 'PunchTime',
  actionPath: 'InOutMode', punchIdPath: '', punchTimeFormat: '', timeZoneId: 'Asia/Kolkata', inValues: ['IN'], outValues: ['OUT'],
  employeeMap: {}, pagination: 'None', firstPage: 1, pageSize: 500, maxPages: 20, timeoutSeconds: 20,
  syncIntervalMinutes: 0, lookbackDays: 2,
})
export const idmsAttendanceAdapter = (clientId: number): AttendanceApiAdapter => ({
  ...emptyAttendanceAdapter(clientId), name: 'iDMS / TimeWatch', allowPrivateNetwork: true,
  inValues: [], outValues: [],
  url: 'http://192.168.1.66:8001/TimeWatchAPI/GetPunchData',
  bodyTemplate: JSON.stringify({ FromDate: '{{fromDate}}', ToDate: '{{toDate}}', DeviceID: '{{deviceId}}', UserID: '' }, null, 2),
})
export const attendanceAdapterSetupIssues = (row: AttendanceApiAdapter): string[] => [
  ...(!row.deviceId ? ['registered HRMS machine'] : []),
  ...(!row.sourceDeviceId ? ['vendor device identifier'] : []),
  ...(!row.inValues.length || !row.outValues.length ? ['verified IN/OUT values'] : []),
  ...(row.authType !== 'None' && !row.hasSecret ? ['vendor credential'] : []),
  ...(!row.hasDeviceToken ? ['HRMS machine API key'] : []),
]
export const attendanceAdapterStatus = (row: AttendanceApiAdapter) => row.isActive ? 'Enabled' : attendanceAdapterSetupIssues(row).length ? 'Draft' : 'Paused'
const root = '/api/integrations/attendance/adapters'
export const getAttendanceAdapters = (clientId: number) => getJsonResult<AttendanceApiAdapter[]>(`${root}?clientId=${clientId}`, [], { loader: false })
export const saveAttendanceAdapter = (configuration: AttendanceApiAdapter, secret: string, deviceToken: string, clearSecret: boolean, clearDeviceToken: boolean) =>
  postJson(root, { configuration, secret, deviceToken, clearSecret, clearDeviceToken }, null as AttendanceApiAdapter | null, { loader: false, toast: false })
export const deleteAttendanceAdapter = (row: AttendanceApiAdapter) => deleteJson(`${root}/${row.id}?clientId=${row.clientId}`, null, { loader: false, toast: false })
export const runAttendanceAdapter = (row: AttendanceApiAdapter, preview: boolean, fromDate: string, toDate: string) =>
  postJson(`${root}/${row.id}/${preview ? 'test' : 'sync'}?clientId=${row.clientId}`, { fromDate, toDate }, null as AdapterRun | null,
    { loader: false, toast: false, timeoutMs: 180000, timeoutMessage: 'The run is still taking time. Refresh its status before retrying.' })
