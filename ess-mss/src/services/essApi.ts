import type { AttendanceSummary, Birthday, DailyAttendance, ExpenseClaim, ExpenseDashboard, ExpenseOptions, FeatureAccess, Holiday, LeaveBalance, LeaveRequest, OrganizationBrand, Payslip, PayslipDocument, ProfileData, RecruitmentDashboard, RecruitmentEmployeeReferral, RecruitmentInternalOpening, RecruitmentOptions, RecruitmentRequisition, SaveEmployeeReferral, SaveExpenseClaim, SaveProfileData, SaveRecruitmentRequisition, SaveTravelRequest, Task, TaxPortal, TravelDashboard, TravelOptions, TravelRequest, User, WorkflowTrail } from '../types'

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
    body: JSON.stringify({ email, password, portal: 'ESS' }),
  })
  return jsonOrThrow<{ token: string; user: User }>(response)
}

export async function me() {
  const response = await essFetch('/api/auth/me')
  return response.ok ? (response.json() as Promise<User>) : null
}

export async function changePassword(currentPassword: string, newPassword: string) {
  const response = await essFetch('/api/auth/change-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ currentPassword, newPassword }),
  })
  return jsonOrThrow<User>(response)
}

export async function organizationBrand() {
  const response = await fetch(`${apiBase}/api/public/organization-brand`)
  return response.ok ? (response.json() as Promise<OrganizationBrand>) : null
}

export const essApi = {
  features: () => essFetch('/api/ess/features').then(r => r.ok ? r.json() as Promise<FeatureAccess> : { travelExpenseEnabled: false }),
  profile: () => essFetch('/api/ess/profile').then(r => r.ok ? r.json() as Promise<ProfileData> : null),
  saveProfile: (request: SaveProfileData) => essFetch('/api/ess/profile', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) }).then(jsonOrThrow<ProfileData>),
  leaveBalances: () => essFetch('/api/ess/leave/balances').then(r => r.ok ? r.json() as Promise<LeaveBalance[]> : []),
  leaveRequests: () => essFetch('/api/ess/leave/requests').then(r => r.ok ? r.json() as Promise<LeaveRequest[]> : []),
  leaveTrail: (id: number) => essFetch(`/api/ess/leave/requests/${id}/trail`).then(r => r.ok ? r.json() as Promise<WorkflowTrail> : Promise.reject()),
  tasks: (view: 'pending' | 'actioned' = 'pending') => essFetch(`/api/workflows/tasks/${view}`).then(r => r.ok ? r.json() as Promise<Task[]> : []),
  taskAction: (taskId: number, action: 'Approved' | 'Rejected' | 'Sent Back', comment: string) => essFetch(`/api/workflows/tasks/${taskId}/${encodeURIComponent(action)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ comment }),
  }).then(async response => {
    if (response.ok) return
    const data = await response.json().catch(() => ({}))
    throw new Error(data.error || 'Unable to update task.')
  }),
  birthdays: () => essFetch('/api/ess/dashboard/birthdays').then(r => r.ok ? r.json() as Promise<Birthday[]> : []),
  attendance: (month: string) => essFetch(`/api/ess/dashboard/attendance?month=${month}`).then(r => r.ok ? r.json() as Promise<AttendanceSummary> : null),
  dailyAttendance: (month: string) => essFetch(`/api/ess/dashboard/attendance/daily?month=${month}`).then(r => r.ok ? r.json() as Promise<DailyAttendance[]> : []),
  holidays: (month: string) => essFetch(`/api/ess/dashboard/holidays?month=${month}`).then(r => r.ok ? r.json() as Promise<Holiday[]> : []),
  payslips: () => essFetch('/api/ess/pay/payslips').then(r => r.ok ? r.json() as Promise<Payslip[]> : Promise.reject()),
  payslipDocument: (payRunId: number) => essFetch(`/api/ess/pay/payslips/${payRunId}`).then(r => r.ok ? r.json() as Promise<PayslipDocument> : Promise.reject()),
  taxPortal: () => essFetch('/api/ess/tax').then(r => r.ok ? r.json() as Promise<TaxPortal> : Promise.reject()),
  saveTaxRegime: (regime: 'Old' | 'New') => essFetch('/api/ess/tax/regime', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ regime }) }).then(jsonOrThrow<unknown>),
  saveTaxDeclarations: (phase: 'Planned' | 'Actual', lines: { sectionId: number; amount: number; remarks: string }[]) => essFetch('/api/ess/tax/declarations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ phase, lines }) }).then(jsonOrThrow<unknown>),
  createLeaveRequest: (request: { leaveCode: string; fromDate: string; toDate: string; dayType: string; reason: string }) => essFetch('/api/ess/leave/requests', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  }).then(jsonOrThrow<LeaveRequest>),
  travelOptions: () => essFetch('/api/ess/travel/options').then(r => r.ok ? r.json() as Promise<TravelOptions> : Promise.reject()),
  travelDashboard: () => essFetch('/api/ess/travel/dashboard').then(r => r.ok ? r.json() as Promise<TravelDashboard> : Promise.reject()),
  travelRequests: () => essFetch('/api/ess/travel/requests').then(r => r.ok ? r.json() as Promise<TravelRequest[]> : []),
  travelTrail: (id: number) => essFetch(`/api/ess/travel/requests/${id}/trail`).then(r => r.ok ? r.json() as Promise<WorkflowTrail> : Promise.reject()),
  saveTravelRequest: (request: SaveTravelRequest) => essFetch('/api/ess/travel/requests', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) }).then(jsonOrThrow<TravelRequest>),
  submitTravelRequest: (id: number) => essFetch(`/api/ess/travel/requests/${id}/submit`, { method: 'POST' }).then(jsonOrThrow<TravelRequest>),
  withdrawTravelRequest: (id: number) => essFetch(`/api/ess/travel/requests/${id}/withdraw`, { method: 'POST' }).then(async r => { if (!r.ok) await jsonOrThrow<unknown>(r) }),
  cancelTravelRequest: (id: number, reason: string) => essFetch(`/api/ess/travel/requests/${id}/cancel`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }) }).then(async r => { if (!r.ok) await jsonOrThrow<unknown>(r) }),
  expenseOptions: () => essFetch('/api/ess/expenses/options').then(r => r.ok ? r.json() as Promise<ExpenseOptions> : Promise.reject()),
  expenseDashboard: () => essFetch('/api/ess/expenses/dashboard').then(r => r.ok ? r.json() as Promise<ExpenseDashboard> : Promise.reject()),
  expenseClaims: () => essFetch('/api/ess/expenses/claims').then(r => r.ok ? r.json() as Promise<ExpenseClaim[]> : []),
  expenseClaim: (id: number) => essFetch(`/api/ess/expenses/claims/${id}`).then(r => r.ok ? r.json() as Promise<ExpenseClaim> : Promise.reject()),
  expenseTrail: (id: number) => essFetch(`/api/ess/expenses/claims/${id}/trail`).then(r => r.ok ? r.json() as Promise<WorkflowTrail> : Promise.reject()),
  saveExpenseClaim: (claim: SaveExpenseClaim) => essFetch('/api/ess/expenses/claims', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(claim) }).then(jsonOrThrow<ExpenseClaim>),
  submitExpenseClaim: (id: number) => essFetch(`/api/ess/expenses/claims/${id}/submit`, { method: 'POST' }).then(jsonOrThrow<ExpenseClaim>),
  recruitmentOptions: () => essFetch('/api/ess/recruitment/options').then(r => r.ok ? r.json() as Promise<RecruitmentOptions> : Promise.reject()),
  recruitmentDashboard: () => essFetch('/api/ess/recruitment/dashboard').then(r => r.ok ? r.json() as Promise<RecruitmentDashboard> : Promise.reject()),
  recruitmentRequisitions: () => essFetch('/api/ess/recruitment/requisitions').then(r => r.ok ? r.json() as Promise<RecruitmentRequisition[]> : []),
  recruitmentTrail: (id: number) => essFetch(`/api/ess/recruitment/requisitions/${id}/trail`).then(r => r.ok ? r.json() as Promise<WorkflowTrail> : Promise.reject()),
  saveRecruitmentRequisition: (request: SaveRecruitmentRequisition) => essFetch('/api/ess/recruitment/requisitions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) }).then(jsonOrThrow<RecruitmentRequisition>),
  submitRecruitmentRequisition: (id: number) => essFetch(`/api/ess/recruitment/requisitions/${id}/submit`, { method: 'POST' }).then(jsonOrThrow<RecruitmentRequisition>),
  withdrawRecruitmentRequisition: (id: number) => essFetch(`/api/ess/recruitment/requisitions/${id}/withdraw`, { method: 'POST' }).then(async r => { if (!r.ok) await jsonOrThrow<unknown>(r) }),
  deleteRecruitmentDraft: (id: number) => essFetch(`/api/ess/recruitment/requisitions/${id}`, { method: 'DELETE' }).then(async r => { if (!r.ok) await jsonOrThrow<unknown>(r) }),
  recruitmentInternalOpenings: () => essFetch('/api/ess/recruitment/internal-openings').then(r => r.ok ? r.json() as Promise<RecruitmentInternalOpening[]> : []),
  recruitmentReferrals: () => essFetch('/api/ess/recruitment/referrals').then(r => r.ok ? r.json() as Promise<RecruitmentEmployeeReferral[]> : []),
  submitRecruitmentReferral: (request: SaveEmployeeReferral) => essFetch('/api/ess/recruitment/referrals', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) }).then(jsonOrThrow<RecruitmentEmployeeReferral>),
}
