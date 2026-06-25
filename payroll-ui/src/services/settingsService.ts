import type { Client, Drop, Employee, EmployeeImportResult, Org, Setup, WorkLocation } from '../types/payroll'
import { deleteJson, getBlob, getJson, postForm, postJson } from './apiClient'

export const getOrganization = (fallback: Org) => getJson<Org>('/api/organization', fallback)
export const saveOrganization = (organization: Org) => postJson('/api/organization', organization, organization)
export const getSetup = (fallback: Setup) => getJson<Setup>('/api/setup', fallback)
export const saveSetup = (setup: Setup) => postJson('/api/setup', setup, setup)
export const saveClient = (client: Client) => postJson('/api/clients', client, { id: client.id })
export const deleteClient = (id: number) => deleteJson(`/api/clients/${id}`, null)
export const getWorkLocations = () => getJson<WorkLocation[]>('/api/work-locations', [])
export const saveWorkLocation = (location: WorkLocation) => postJson('/api/work-locations', location, { id: location.id })
export const deleteWorkLocation = (id: number) => deleteJson(`/api/work-locations/${id}`, null)
export const getDropdowns = () => getJson<Drop[]>('/api/dropdowns', [])
export const saveDropdown = (drop: Drop) => postJson('/api/dropdowns', drop, { id: drop.id })
export const deleteDropdown = (id: number) => deleteJson(`/api/dropdowns/${id}`, null)
export const saveEmployee = (employee: Employee) => postJson('/api/employees', employee, { id: employee.id })
export const deleteEmployee = (id: number) => deleteJson(`/api/employees/${id}`, null)
export const downloadEmployeeImportSample = () => getBlob('/api/employees/import/sample')
export const importEmployees = (clientId: number, file: File) => {
  const body = new FormData()
  body.append('clientId', String(clientId))
  body.append('file', file)
  return postForm('/api/employees/import', body, { importedCount: 0, updatedCount: 0, skippedCount: 0, errors: [] } as EmployeeImportResult)
}
