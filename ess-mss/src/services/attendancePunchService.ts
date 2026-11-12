import { essFetch } from './essApi'
import type { AttendanceToday, BrowserPunchRequest, BrowserPunchResult, SavedAttendancePunch } from '../utils/attendancePunch'

export class PunchRequestError extends Error {
  uncertain: boolean
  constructor(message: string, uncertain = false) { super(message); this.name = 'PunchRequestError'; this.uncertain = uncertain }
}

export async function getAttendanceToday(): Promise<AttendanceToday> {
  const response = await essFetch('/api/ess/attendance/today', { cache: 'no-store' })
  const data = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(data.error || data.message || 'Unable to load today’s attendance.')
  if (!data.attendanceDate || !['CheckIn', 'CheckOut', 'Completed', 'Unavailable', 'WaitForApproval'].includes(data.nextExpectedAction)) throw new Error('Today’s attendance state is unavailable. Refresh before punching.')
  return data as AttendanceToday
}

export async function getSavedAttendancePunches(date: string): Promise<SavedAttendancePunch[]> {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new Error('Choose a valid attendance date.')
  const response = await essFetch(`/api/ess/attendance/punches?date=${date}`, { cache: 'no-store' })
  const data = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(data.error || data.message || 'Unable to load saved punch locations.')
  if (!Array.isArray(data) || data.some(row => !Number.isSafeInteger(row.id) || !['CheckIn', 'CheckOut'].includes(row.action) || ![row.latitude, row.longitude, row.accuracyMeters].every(Number.isFinite) || typeof row.capturedAt !== 'string')) throw new Error('Saved punch locations could not be read. Refresh and try again.')
  return data as SavedAttendancePunch[]
}

export async function getPunchWorkspace() {
  const today = await getAttendanceToday()
  try { return { today, punches: await getSavedAttendancePunches(today.attendanceDate.slice(0, 10)), logError: '' } }
  catch (error) { return { today, punches: [] as SavedAttendancePunch[], logError: error instanceof Error ? error.message : 'Unable to load saved punch locations.' } }
}

export async function recordBrowserPunch(request: BrowserPunchRequest): Promise<BrowserPunchResult> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), 30000)
  try {
    const response = await essFetch('/api/ess/attendance/punch/browser', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request), signal: controller.signal, cache: 'no-store' })
    const data = await response.json().catch(() => null)
    if (response.status >= 500) throw new PunchRequestError('The server could not confirm this punch. Retry the same submission to check whether it was recorded.', true)
    if (!data || typeof data.allowed !== 'boolean' || typeof data.punchRecorded !== 'boolean' || typeof data.status !== 'string') {
      if (response.status === 401) throw new PunchRequestError('Your session has expired. Sign in again before punching.')
      if (response.status === 403) throw new PunchRequestError('You do not have permission to mark attendance for this employee.')
      throw new PunchRequestError(data?.error || data?.message || 'The server did not confirm this punch. Retry the same submission.', response.ok)
    }
    const retryAfter = Number(data.retryAfterSeconds || response.headers.get('Retry-After') || 0)
    return { ...data, retryAfterSeconds: Number.isFinite(retryAfter) ? retryAfter : 0 } as BrowserPunchResult
  } catch (error) {
    if (error instanceof PunchRequestError) throw error
    throw new PunchRequestError('The connection ended before this punch was confirmed. Reconnect and retry the same submission; it will not create a second punch.', true)
  } finally { clearTimeout(timer) }
}
