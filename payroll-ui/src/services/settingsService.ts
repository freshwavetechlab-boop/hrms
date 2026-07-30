import type { Client, ClientBillingAdvancedSetup, ClientBillingConfiguration, ClientBillingCostRuleHeader, ClientBillingCostRuleLine, ClientBillingModule, Drop, Employee, EmployeeActionRequest, EmployeeAuditTrail, EmployeeInfotypeRecord, EssClientSetting, Org, PayTravelAdvanceRequest, RecoverTravelAdvanceRequest, RecruitmentAdminSetup, RecruitmentApprovalMapping, RecruitmentAssignmentRule, RecruitmentDocumentChecklist, RecruitmentMasterValue, RecruitmentPartner, RecruitmentSetting, RecruitmentSlaRule, RecruitmentTemplate, ScheduledJob, ScheduledJobAction, ScheduledJobHandlerOption, ScheduledJobRun, SettleTravelAdvanceRequest, Setup, TravelAdvance, TravelExpenseCategory, TravelExpenseClientSetting, TravelExpenseSetup, TravelPolicy, TravelPolicyAssignment, TravelPolicyRule, WorkLocation, WorkflowApprover } from '../types/payroll'
import { apiRequest, deleteJson, getBlob, getJson, getJsonResult, postForm, postJson, type ApiOptions, type ApiResult } from './apiClient'

export type BulkImportStatus = { jobId: string; state: 'Queued' | 'Processing' | 'NeedsConfirmation' | 'Completed' | 'Failed'; totalRows: number; completedRows: number; inserted: number; updated: number; errors: string[] }
export type EmployeeDeletePreview = { employeeId: number; employeeCode: string; employeeName: string; links: string[]; canDelete: boolean }
export type EmployeeImportReviewChange = {
  field: string
  label: string
  oldValue: string
  newValue: string
  sensitive?: boolean
  payrollImpact?: boolean
}
export type EmployeeImportCandidateEmployee = {
  employeeId: number
  employeeCode: string
  employeeName: string
  matchReasons: string[]
  changes: EmployeeImportReviewChange[]
}
export type EmployeeImportIdentityEvidenceCandidate = {
  employeeId: number
  employeeCode: string
  employeeName: string
  existingValue: string
}
export type EmployeeImportIdentityEvidence = {
  field: string
  label: string
  uploadedValue: string
  sensitive: boolean
  candidates: EmployeeImportIdentityEvidenceCandidate[]
}
export type EmployeeImportReviewRow = {
  rowNumber: number
  sheet: string
  proposedEmployeeCode: string
  matchStatus: string
  matchedEmployeeId?: number | null
  matchedEmployeeCode?: string | null
  matchedEmployeeName?: string | null
  matchReasons: string[]
  blockingReasons: string[]
  changes: EmployeeImportReviewChange[]
  candidateEmployees?: EmployeeImportCandidateEmployee[]
  identityEvidence?: EmployeeImportIdentityEvidence[]
  canResolveConflict?: boolean
}
export type EmployeeImportPreflight = {
  reviewToken: string
  totalRows: number
  canImport: boolean
  requiresConfirmation: boolean
  rows: EmployeeImportReviewRow[]
  errors?: string[]
}
export type EmployeeImportDecision = {
  rowNumber: number
  sheet: string
  action: 'update' | 'insert' | 'skip'
  employeeId?: number
  fieldChoices?: Record<string, 'keepExisting' | 'useImported'>
}

export const getOrganization = (fallback: Org) => getJson<Org>('/api/organization', fallback)
export const saveOrganization = (organization: Org) => postJson('/api/organization', organization, organization)
export const getSetup = (fallback: Setup) => getJson<Setup>('/api/setup', fallback)
export const saveSetup = (setup: Setup, options: ApiOptions = {}) => postJson('/api/setup', setup, setup, options)
export const getClientBillingModule = () => getJson<ClientBillingModule>('/api/client-billing/module', { isEnabled: false, advancedCostingEnabled: false })
export const saveClientBillingModule = (module: ClientBillingModule) => postJson('/api/client-billing/module', module, module, { toast: false })
export const getClientBillingConfigurations = () => getJson<ClientBillingConfiguration[]>('/api/client-billing/configurations', [])
export const saveClientBillingConfiguration = (row: ClientBillingConfiguration) => postJson('/api/client-billing/configurations', row, { id: row.id }, { toast: false })
export const getClientBillingAdvanced = () => getJson<ClientBillingAdvancedSetup>('/api/client-billing/advanced', { headers: [], lines: [] })
export const saveClientBillingAdvancedHeader = (row: ClientBillingCostRuleHeader) => postJson('/api/client-billing/advanced/headers', row, { id: row.id }, { toast: false })
export const saveClientBillingAdvancedLine = (row: ClientBillingCostRuleLine) => postJson('/api/client-billing/advanced/lines', row, { id: row.id }, { toast: false })
export const createClientBillingStandardTemplate = (clientId: number, workLocationId?: number | null, commissionPercent = 5, gstRatePercent = 18) => postJson('/api/client-billing/advanced/templates/standard', { clientId, workLocationId: workLocationId || null, commissionPercent, gstRatePercent }, { id: 0 }, { toast: false })
export const downloadClientBillingImportTemplate = () => getBlob('/api/client-billing/configurations/import-template')
export const startClientBillingImport = (file: File) => {
  const body = new FormData()
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/client-billing/configurations/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getClientBillingImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/client-billing/configurations/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const getTravelExpenseSetup = () => getJson<TravelExpenseSetup>('/api/travel-expense/setup', { clientSettings: [], policies: [], assignments: [], rules: [], categories: [], audit: [] })
export const saveTravelPolicy = (row: TravelPolicy) => postJson('/api/travel-expense/policies', row, { id: row.id }, { successMessage: 'Travel policy saved.' })
export const saveTravelExpenseClientSetting = (row: TravelExpenseClientSetting) => postJson('/api/travel-expense/client-settings', row, row, { successMessage: 'Travel & Expense client setting saved.' })
export const saveTravelPolicyAssignment = (row: TravelPolicyAssignment) => postJson('/api/travel-expense/assignments', row, { id: row.id }, { successMessage: 'Policy assignment saved.' })
export const saveTravelPolicyRule = (row: TravelPolicyRule) => postJson('/api/travel-expense/rules', row, { id: row.id }, { successMessage: 'Policy rule saved.' })
export const saveTravelExpenseCategory = (row: TravelExpenseCategory) => postJson('/api/travel-expense/categories', row, { id: row.id }, { successMessage: 'Expense category saved.' })
export const getTravelAdvances = (clientId = 0, status = '') => getJson<TravelAdvance[]>(`/api/travel-advances?${new URLSearchParams({ ...(clientId ? { clientId: String(clientId) } : {}), ...(status ? { status } : {}) })}`, [])
export const payTravelAdvance = (id: number, request: PayTravelAdvanceRequest) => postJson(`/api/travel-advances/${id}/pay`, request, null as TravelAdvance | null, { successMessage: 'Travel advance paid.' })
export const settleTravelAdvance = (id: number, request: SettleTravelAdvanceRequest) => postJson(`/api/travel-advances/${id}/settle`, request, null as TravelAdvance | null, { successMessage: 'Travel advance settled.' })
export const recoverTravelAdvance = (id: number, request: RecoverTravelAdvanceRequest) => postJson(`/api/travel-advances/${id}/recover`, request, null as TravelAdvance | null, { successMessage: 'Travel advance marked recoverable.' })
export const getRecruitmentAdminSetup = () => getJson<RecruitmentAdminSetup>('/api/recruitment-admin', { settings: [], masters: [], consultants: [], vendors: [], assignmentRules: [], slaRules: [], documentChecklist: [], approvalMappings: [], templates: [] })
export const saveRecruitmentSetting = (row: RecruitmentSetting) => postJson('/api/recruitment-admin/settings', row, row, { successMessage: 'Recruitment settings saved.' })
export const saveRecruitmentMaster = (row: RecruitmentMasterValue) => postJson('/api/recruitment-admin/masters', row, row, { successMessage: 'Recruitment master saved.' })
export const saveRecruitmentPartner = (row: RecruitmentPartner) => postJson('/api/recruitment-admin/partners', row, row, { successMessage: 'Recruitment partner saved.' })
export const saveRecruitmentAssignmentRule = (row: RecruitmentAssignmentRule) => postJson('/api/recruitment-admin/assignment-rules', row, row, { successMessage: 'Recruiter assignment rule saved.' })
export const saveRecruitmentSlaRule = (row: RecruitmentSlaRule) => postJson('/api/recruitment-admin/sla-rules', row, row, { successMessage: 'Recruitment SLA saved.' })
export const saveRecruitmentDocumentChecklist = (row: RecruitmentDocumentChecklist) => postJson('/api/recruitment-admin/document-checklist', row, row, { successMessage: 'Document checklist saved.' })
export const saveRecruitmentApprovalMapping = (row: RecruitmentApprovalMapping) => postJson('/api/recruitment-admin/approval-mappings', row, row, { successMessage: 'Approval mapping saved.' })
export const saveRecruitmentTemplate = (row: RecruitmentTemplate) => postJson('/api/recruitment-admin/templates', row, row, { successMessage: 'Recruitment template saved.' })
export const deleteRecruitmentAdminConfiguration = (kind: string, id: number) => deleteJson(`/api/recruitment-admin/${kind}/${id}`, null, { successMessage: 'Recruitment configuration deleted.' })
export const getEssClientSettings = () => getJson<EssClientSetting[]>('/api/ess-admin/settings', [])
export const saveEssClientSetting = (row: EssClientSetting) => postJson('/api/ess-admin/settings', row, row, { successMessage: 'ESS settings saved.' })
export const getScheduledJobs = () => getJson<ScheduledJob[]>('/api/scheduled-jobs', [])
export const getScheduledJobActions = () => getJson<ScheduledJobAction[]>('/api/scheduled-jobs/actions', [])
export const saveScheduledJobAction = (action: ScheduledJobAction) => postJson<ScheduledJobAction, ScheduledJobAction | null>('/api/scheduled-jobs/actions', action, null)
export const getScheduledJobHandlers = () => getJson<ScheduledJobHandlerOption[]>('/api/scheduled-jobs/handlers', [])
export const getScheduledJobRuns = (jobId?: number, limit = 100) => getJson<ScheduledJobRun[]>(`/api/scheduled-jobs/runs?${new URLSearchParams({ ...(jobId ? { jobId: String(jobId) } : {}), limit: String(limit) })}`, [])
export const saveScheduledJob = (job: ScheduledJob) => postJson<ScheduledJob, ScheduledJob | null>('/api/scheduled-jobs', job, null)
export const setScheduledJobEnabled = (id: number, isEnabled: boolean) => postJson(`/api/scheduled-jobs/${id}/enabled?isEnabled=${isEnabled}`, {}, null as ScheduledJob | null)
export const runScheduledJobNow = (id: number) => postJson(`/api/scheduled-jobs/${id}/run-now`, {}, null as ScheduledJobRun | null)
export const saveClient = (client: Client, options: ApiOptions = {}) => postJson('/api/clients', client, { id: client.id }, options)
export const downloadClientImportTemplate = () => getBlob('/api/clients/import-template')
export const startClientImport = (file: File) => {
  const body = new FormData()
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/clients/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getClientImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/clients/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const getWorkLocations = () => getJson<WorkLocation[]>('/api/work-locations', [])
export const saveWorkLocation = (location: WorkLocation) => postJson('/api/work-locations', location, { id: location.id })
export const downloadWorkLocationImportTemplate = () => getBlob('/api/work-locations/import-template')
export const startWorkLocationImport = (file: File) => {
  const body = new FormData()
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/work-locations/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getWorkLocationImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/work-locations/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const getDropdowns = () => getJson<Drop[]>('/api/dropdowns', [])
export const getDropdownsResult = () => getJsonResult<Drop[]>('/api/dropdowns', [])
export const saveDropdown = (drop: Drop, options: ApiOptions = {}) => postJson('/api/dropdowns', drop, { id: drop.id }, options)
export const downloadDropdownImportTemplate = () => getBlob('/api/dropdowns/import-template')
export const startDropdownImport = (file: File) => {
  const body = new FormData()
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/dropdowns/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getDropdownImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/dropdowns/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const downloadSalaryComponentImportTemplate = () => getBlob('/api/salary-components/import-template')
export const startSalaryComponentImport = (file: File) => {
  const body = new FormData()
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/salary-components/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getSalaryComponentImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/salary-components/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const downloadSalaryTemplateImportTemplate = () => getBlob('/api/salary-templates/import-template')
export const startSalaryTemplateImport = (file: File) => {
  const body = new FormData()
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/salary-templates/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getSalaryTemplateImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/salary-templates/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
export const saveEmployee = (employee: Employee, infotypeCode?: string, changeReason = '') => {
  const query = new URLSearchParams()
  if (infotypeCode) query.set('infotypeCode', infotypeCode)
  if (changeReason.trim()) query.set('changeReason', changeReason.trim())
  return postJson(`/api/employees${query.size ? `?${query}` : ''}`, employee, { id: employee.id })
}
export const getEmployeeManagerUsers = () => getJson<WorkflowApprover[]>('/api/employees/manager-users', [])
export const getEmployeeInfotypes = (id: number, activeOnly = false) => getJson<EmployeeInfotypeRecord[]>(`/api/employees/${id}/infotypes?activeOnly=${activeOnly}`, [])
export const getEmployeeAuditTrail = (id: number) => getJson<EmployeeAuditTrail[]>(`/api/employees/${id}/audit`, [])
export const getActiveEmployeeInfotypes = (clientId: number) => getJson<EmployeeInfotypeRecord[]>(`/api/employees/infotypes/active?clientId=${clientId}`, [])
export const processEmployeeAction = (request: EmployeeActionRequest) => postJson<EmployeeActionRequest, Employee | null>('/api/employees/actions', request, null)
export const getEmployeeDeletePreview = (id: number) => getJson<EmployeeDeletePreview>(`/api/employees/${id}/delete-preview`, { employeeId: id, employeeCode: '', employeeName: '', links: ['Unable to validate employee links.'], canDelete: false })
export const deleteEmployee = (id: number) => deleteJson(`/api/employees/${id}`, null, { toast: false })
export const downloadEmployeeImportTemplate = (clientId: number) => getBlob(`/api/employees/import-template?clientId=${clientId}`)
const emptyEmployeeImportPreflight: EmployeeImportPreflight = { reviewToken: '', totalRows: 0, canImport: false, requiresConfirmation: false, rows: [], errors: [] }
export const preflightEmployeeImport = async (clientId: number, file: File, mode: 'insert' | 'update' | 'upsert' = 'upsert'): Promise<ApiResult<EmployeeImportPreflight>> => {
  const body = new FormData()
  body.append('clientId', String(clientId))
  body.append('mode', mode)
  body.append('file', file)
  try {
    const response = await apiRequest('/api/employees/import-preflight', { method: 'POST', body, toast: false })
    const text = await response.text()
    const data: EmployeeImportPreflight & { error?: string; detail?: string; message?: string } = text
      ? JSON.parse(text) as EmployeeImportPreflight & { error?: string; detail?: string; message?: string }
      : { ...emptyEmployeeImportPreflight }
    // 422 is an actionable preflight result (for example all rows blocked),
    // not a transport failure. Keep its review rows so the user can see why.
    if (response.ok || response.status === 422) return { ok: true, data, error: '', status: response.status }
    return { ok: false, data: emptyEmployeeImportPreflight, error: data.error || data.detail || data.message || `Employee identity preflight failed with status ${response.status}.`, status: response.status }
  } catch (error) {
    return { ok: false, data: emptyEmployeeImportPreflight, error: error instanceof Error ? error.message : 'Employee identity preflight failed.', status: 0 }
  }
}
export const startEmployeeImport = (clientId: number, file: File, mode: 'insert' | 'update' | 'upsert' = 'upsert', reviewToken = '', decisions: EmployeeImportDecision[] = []) => {
  const body = new FormData()
  body.append('clientId', String(clientId))
  body.append('mode', mode)
  body.append('file', file)
  if (reviewToken) body.append('reviewToken', reviewToken)
  if (decisions.length) body.append('decisionsJson', JSON.stringify(decisions))
  return postForm<BulkImportStatus>('/api/employees/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getEmployeeImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/employees/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
