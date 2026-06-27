import type { AttendanceSummary, Birthday, DailyAttendance, Holiday, LeaveBalance, LeaveRequest, Payslip, ProfileData, Task, TaxPortal, User, WorkflowTrail } from '../types'

export const apiBase = import.meta.env.VITE_API_URL ?? 'http://localhost:5062'
const tokenKey = 'ess.auth.token'

export function getToken() {
  return localStorage.getItem(tokenKey)
}

export function setToken(token: string) {
  localStorage.setItem(tokenKey, token)
}

export function clearToken() {
  localStorage.removeItem(tokenKey)
}

export async function essFetch(path: string, init?: RequestInit) {
  const token = getToken()
  const headers = new Headers(init?.headers)
  if (token) headers.set('Authorization', `Bearer ${token}`)
  return fetch(`${apiBase}${path}`, { ...init, headers })
}

async function jsonOrThrow<T>(response: Response): Promise<T> {
  const data = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(data.error || 'Request failed.')
  return data as T
}

export async function login(email: string, password: string) {
  const response = await fetch(`${apiBase}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })
  return jsonOrThrow<{ token: string; user: User }>(response)
}

export async function me() {
  const response = await essFetch('/api/auth/me')
  return response.ok ? (response.json() as Promise<User>) : null
}

export const essApi = {
  profile: () => essFetch('/api/ess/profile').then(r => r.ok ? r.json() as Promise<ProfileData> : null),
  leaveBalances: () => essFetch('/api/ess/leave/balances').then(r => r.ok ? r.json() as Promise<LeaveBalance[]> : []),
  leaveRequests: () => essFetch('/api/ess/leave/requests').then(r => r.ok ? r.json() as Promise<LeaveRequest[]> : []),
  leaveTrail: (id: number) => essFetch(`/api/ess/leave/requests/${id}/trail`).then(r => r.ok ? r.json() as Promise<WorkflowTrail> : Promise.reject()),
  tasks: () => essFetch('/api/workflows/tasks/pending').then(r => r.ok ? r.json() as Promise<Task[]> : []),
  birthdays: () => essFetch('/api/ess/dashboard/birthdays').then(r => r.ok ? r.json() as Promise<Birthday[]> : []),
  attendance: (month: string) => essFetch(`/api/ess/dashboard/attendance?month=${month}`).then(r => r.ok ? r.json() as Promise<AttendanceSummary> : null),
  dailyAttendance: (month: string) => essFetch(`/api/ess/dashboard/attendance/daily?month=${month}`).then(r => r.ok ? r.json() as Promise<DailyAttendance[]> : []),
  holidays: (month: string) => essFetch(`/api/ess/dashboard/holidays?month=${month}`).then(r => r.ok ? r.json() as Promise<Holiday[]> : []),
  payslips: () => essFetch('/api/ess/pay/payslips').then(r => r.ok ? r.json() as Promise<Payslip[]> : Promise.reject()),
  taxPortal: () => essFetch('/api/ess/tax').then(r => r.ok ? r.json() as Promise<TaxPortal> : Promise.reject()),
  saveTaxRegime: (regime: 'Old' | 'New') => essFetch('/api/ess/tax/regime', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ regime }) }).then(jsonOrThrow<unknown>),
  saveTaxDeclarations: (phase: 'Planned' | 'Actual', lines: { sectionId: number; amount: number; remarks: string }[]) => essFetch('/api/ess/tax/declarations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ phase, lines }) }).then(jsonOrThrow<unknown>),
  createLeaveRequest: (request: { leaveCode: string; fromDate: string; toDate: string; reason: string }) => essFetch('/api/ess/leave/requests', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  }).then(jsonOrThrow<LeaveRequest>),
}
