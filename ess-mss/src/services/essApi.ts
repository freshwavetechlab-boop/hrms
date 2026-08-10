import type { AdminMaintenanceResult, AttachmentAccessTicket, AttachmentFieldConfiguration, AttendanceBatchJob, AttendanceGroup, AttendanceSummary, Birthday, DailyAttendance, EntityAttachment, ExpenseClaim, ExpenseDashboard, ExpenseOptions, FeatureAccess, Holiday, LeaveBalance, LeaveRequest, ManagedDailyAttendance, ManagedMonthlyAttendance, OrganizationBrand, Payslip, PayslipDocument, ProfileData, RecruitmentDashboard, RecruitmentEmployeeReferral, RecruitmentInternalOpening, RecruitmentOptions, RecruitmentRequisition, SaveEmployeeReferral, SaveExpenseClaim, SaveProfileData, SaveRecruitmentRequisition, SaveTravelRequest, Task, TaxPortal, TravelDashboard, TravelOptions, TravelRequest, User, WorkflowTrail } from '../types'

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
  if (!response.ok) throw new Error(data.error || data.message || 'Request failed.')
  return data as T
}

async function jsonOrDefault<T>(response: Response, fallback: T): Promise<T> {
  if (!response.ok) return fallback
  const body = await response.text()
  return body.trim() ? JSON.parse(body) as T : fallback
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
  attendanceGroups: (_clientId: number) => essFetch('/api/ess/mss/attendance/groups').then(jsonOrThrow<AttendanceGroup[]>),
  managedMonthlyAttendance: (_clientId: number, month: string) => essFetch(`/api/ess/mss/attendance/monthly?month=${month}`).then(jsonOrThrow<ManagedMonthlyAttendance[]>),
  managedDailyAttendance: (_clientId: number, month: string) => essFetch(`/api/ess/mss/attendance/daily-grid?month=${month}`).then(jsonOrThrow<ManagedDailyAttendance[]>),
  saveManagedAttendance: (clientId: number, month: string, rows: ManagedDailyAttendance[], rollupEmployeeIds: number[]) => essFetch('/api/ess/mss/attendance/daily/batch-jobs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clientId, month, rows, rollupEmployeeIds }) }).then(jsonOrThrow<AttendanceBatchJob>),
  attendanceBatchJob: (jobId: string) => essFetch(`/api/ess/mss/attendance/daily/batch-jobs/${jobId}`).then(jsonOrThrow<AttendanceBatchJob>),
  features: () => essFetch('/api/ess/features').then(r => r.ok ? r.json() as Promise<FeatureAccess> : { travelExpenseEnabled: false }),
  profile: () => essFetch('/api/ess/profile').then(r => r.ok ? r.json() as Promise<ProfileData> : null),
  saveProfile: (request: SaveProfileData) => essFetch('/api/ess/profile', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) }).then(jsonOrThrow<ProfileData>),
  attachmentConfigurations: async (clientId: number) => {
    const forms = ['EMPLOYEE_PROFILE', 'EMPLOYEE_CREATE_EDIT']
    const responses = await Promise.all(forms.map(formCode =>
      essFetch(`/api/attachment-configurations/effective?clientId=${clientId}&moduleCode=EMPLOYEE&formCode=${formCode}`)
        .then(response => response.ok ? response.json() as Promise<AttachmentFieldConfiguration[]> : [])
    ))
    return [...new Map(responses.flat().map(row => [row.id, row])).values()]
  },
  attachments: (employeeId: number) => essFetch(`/api/attachments?entityType=EMPLOYEE&entityId=${employeeId}`).then(r => r.ok ? r.json() as Promise<EntityAttachment[]> : []),
  uploadAttachment: (configurationId: number, employeeId: number, file: File, metadata: { documentNumber: string; issueDate: string; expiryDate: string }) => {
    const body = new FormData()
    body.append('fieldConfigurationId', String(configurationId))
    body.append('entityType', 'EMPLOYEE')
    body.append('entityId', String(employeeId))
    body.append('documentNumber', metadata.documentNumber.trim())
    if (metadata.issueDate) body.append('issueDate', metadata.issueDate)
    if (metadata.expiryDate) body.append('expiryDate', metadata.expiryDate)
    body.append('file', file)
    return essFetch('/api/attachments', { method: 'POST', body }).then(jsonOrThrow<EntityAttachment>)
  },
  deleteAttachment: (publicId: string) => essFetch(`/api/attachments/${publicId}`, { method: 'DELETE' }).then(async response => {
    if (!response.ok) await jsonOrThrow<unknown>(response)
  }),
  attachmentTicket: (publicId: string, purpose: 'Preview' | 'Download') => essFetch(`/api/attachments/${publicId}/access-ticket`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ purpose }),
  }).then(jsonOrThrow<AttachmentAccessTicket>),
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
  birthdays: () => essFetch('/api/ess/dashboard/birthdays').then(r => jsonOrDefault<Birthday[]>(r, [])),
  attendance: (month: string) => essFetch(`/api/ess/dashboard/attendance?month=${month}`).then(r => jsonOrDefault<AttendanceSummary | null>(r, null)),
  dailyAttendance: (month: string) => essFetch(`/api/ess/dashboard/attendance/daily?month=${month}`).then(r => jsonOrDefault<DailyAttendance[]>(r, [])),
  holidays: (month: string) => essFetch(`/api/ess/dashboard/holidays?month=${month}`).then(r => jsonOrDefault<Holiday[]>(r, [])),
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
  adminTravelRequests: () => essFetch('/api/ess/admin/travel/requests').then(jsonOrThrow<TravelRequest[]>),
  adminRevertTravelRequest: (id: number, reason: string) => essFetch(`/api/ess/admin/travel/requests/${id}/revert`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }) }).then(jsonOrThrow<AdminMaintenanceResult>),
  adminDeleteTravelRequest: (id: number, reason: string) => essFetch(`/api/ess/admin/travel/requests/${id}`, { method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }) }).then(jsonOrThrow<AdminMaintenanceResult>),
  expenseOptions: () => essFetch('/api/ess/expenses/options').then(r => r.ok ? r.json() as Promise<ExpenseOptions> : Promise.reject()),
  expenseDashboard: () => essFetch('/api/ess/expenses/dashboard').then(r => r.ok ? r.json() as Promise<ExpenseDashboard> : Promise.reject()),
  expenseClaims: () => essFetch('/api/ess/expenses/claims').then(r => r.ok ? r.json() as Promise<ExpenseClaim[]> : []),
  expenseClaim: (id: number) => essFetch(`/api/ess/expenses/claims/${id}`).then(r => r.ok ? r.json() as Promise<ExpenseClaim> : Promise.reject()),
  expenseTrail: (id: number) => essFetch(`/api/ess/expenses/claims/${id}/trail`).then(r => r.ok ? r.json() as Promise<WorkflowTrail> : Promise.reject()),
  saveExpenseClaim: (claim: SaveExpenseClaim) => essFetch('/api/ess/expenses/claims', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(claim) }).then(jsonOrThrow<ExpenseClaim>),
  submitExpenseClaim: (id: number) => essFetch(`/api/ess/expenses/claims/${id}/submit`, { method: 'POST' }).then(jsonOrThrow<ExpenseClaim>),
  adminExpenseClaims: () => essFetch('/api/ess/admin/expenses/claims').then(jsonOrThrow<ExpenseClaim[]>),
  adminRevertExpenseClaim: (id: number, reason: string) => essFetch(`/api/ess/admin/expenses/claims/${id}/revert`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }) }).then(jsonOrThrow<AdminMaintenanceResult>),
  adminDeleteExpenseClaim: (id: number, reason: string) => essFetch(`/api/ess/admin/expenses/claims/${id}`, { method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }) }).then(jsonOrThrow<AdminMaintenanceResult>),
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
  recruitmentAttachmentConfigurations: (clientId: number) => essFetch(`/api/attachment-configurations/effective?clientId=${clientId}&moduleCode=RECRUITMENT&formCode=EMPLOYEE_REFERRAL`).then(response => response.ok ? response.json() as Promise<AttachmentFieldConfiguration[]> : []),
  uploadReferralResume: (referralId: number, configurationId: number, file: File) => {
    const body = new FormData(); body.append('fieldConfigurationId', String(configurationId)); body.append('file', file)
    return essFetch(`/api/ess/recruitment/referrals/${referralId}/resume`, { method: 'POST', body }).then(jsonOrThrow<unknown>)
  },
}
