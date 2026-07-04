import type { Client, ClientBillingConfiguration, ClientBillingModule, Drop, Employee, EmployeeActionRequest, EmployeeAuditTrail, EmployeeInfotypeRecord, Org, Setup, WorkLocation } from '../types/payroll'
import { deleteJson, getBlob, getJson, postForm, postJson, type ApiOptions } from './apiClient'

export type BulkImportStatus = { jobId: string; state: 'Queued' | 'Processing' | 'Completed' | 'Failed'; totalRows: number; completedRows: number; inserted: number; updated: number; errors: string[] }
export type EmployeeDeletePreview = { employeeId: number; employeeCode: string; employeeName: string; links: string[]; canDelete: boolean }

export const getOrganization = (fallback: Org) => getJson<Org>('/api/organization', fallback)
export const saveOrganization = (organization: Org) => postJson('/api/organization', organization, organization)
export const getSetup = (fallback: Setup) => getJson<Setup>('/api/setup', fallback)
export const saveSetup = (setup: Setup, options: ApiOptions = {}) => postJson('/api/setup', setup, setup, options)
export const getClientBillingModule = () => getJson<ClientBillingModule>('/api/client-billing/module', { isEnabled: false })
export const saveClientBillingModule = (module: ClientBillingModule) => postJson('/api/client-billing/module', module, module, { toast: false })
export const getClientBillingConfigurations = () => getJson<ClientBillingConfiguration[]>('/api/client-billing/configurations', [])
export const saveClientBillingConfiguration = (row: ClientBillingConfiguration) => postJson('/api/client-billing/configurations', row, { id: row.id }, { toast: false })
export const saveClient = (client: Client, options: ApiOptions = {}) => postJson('/api/clients', client, { id: client.id }, options)
export const getWorkLocations = () => getJson<WorkLocation[]>('/api/work-locations', [])
export const saveWorkLocation = (location: WorkLocation) => postJson('/api/work-locations', location, { id: location.id })
export const getDropdowns = () => getJson<Drop[]>('/api/dropdowns', [])
export const saveDropdown = (drop: Drop, options: ApiOptions = {}) => postJson('/api/dropdowns', drop, { id: drop.id }, options)
export const saveEmployee = (employee: Employee, infotypeCode?: string, changeReason = '') => {
  const query = new URLSearchParams()
  if (infotypeCode) query.set('infotypeCode', infotypeCode)
  if (changeReason.trim()) query.set('changeReason', changeReason.trim())
  return postJson(`/api/employees${query.size ? `?${query}` : ''}`, employee, { id: employee.id })
}
export const getEmployeeInfotypes = (id: number, activeOnly = false) => getJson<EmployeeInfotypeRecord[]>(`/api/employees/${id}/infotypes?activeOnly=${activeOnly}`, [])
export const getEmployeeAuditTrail = (id: number) => getJson<EmployeeAuditTrail[]>(`/api/employees/${id}/audit`, [])
export const getActiveEmployeeInfotypes = (clientId: number) => getJson<EmployeeInfotypeRecord[]>(`/api/employees/infotypes/active?clientId=${clientId}`, [])
export const processEmployeeAction = (request: EmployeeActionRequest) => postJson<EmployeeActionRequest, Employee | null>('/api/employees/actions', request, null)
export const getEmployeeDeletePreview = (id: number) => getJson<EmployeeDeletePreview>(`/api/employees/${id}/delete-preview`, { employeeId: id, employeeCode: '', employeeName: '', links: ['Unable to validate employee links.'], canDelete: false })
export const deleteEmployee = (id: number) => deleteJson(`/api/employees/${id}`, null, { toast: false })
export const downloadEmployeeImportTemplate = (clientId: number) => getBlob(`/api/employees/import-template?clientId=${clientId}`)
export const startEmployeeImport = (clientId: number, file: File) => {
  const body = new FormData()
  body.append('clientId', String(clientId))
  body.append('file', file)
  return postForm<BulkImportStatus>('/api/employees/import-jobs', body, { jobId: '', state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: [] }, { toast: false })
}
export const getEmployeeImportJob = (jobId: string) => getJson<BulkImportStatus>(`/api/employees/import-jobs/${jobId}`, { jobId, state: 'Failed', totalRows: 0, completedRows: 0, inserted: 0, updated: 0, errors: ['Import job not found.'] })
