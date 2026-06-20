import type { Client, Drop, Employee, Org, Setup, WorkLocation } from '../types/payroll'
import { getJson, postJson } from './apiClient'

export const getOrganization = (fallback: Org) => getJson<Org>('/api/organization', fallback)
export const saveOrganization = (organization: Org) => postJson('/api/organization', organization, organization)
export const getSetup = (fallback: Setup) => getJson<Setup>('/api/setup', fallback)
export const saveSetup = (setup: Setup) => postJson('/api/setup', setup, setup)
export const saveClient = (client: Client) => postJson('/api/clients', client, { id: client.id })
export const getWorkLocations = () => getJson<WorkLocation[]>('/api/work-locations', [])
export const saveWorkLocation = (location: WorkLocation) => postJson('/api/work-locations', location, { id: location.id })
export const getDropdowns = () => getJson<Drop[]>('/api/dropdowns', [])
export const saveDropdown = (drop: Drop) => postJson('/api/dropdowns', drop, { id: drop.id })
export const saveEmployee = (employee: Employee) => postJson('/api/employees', employee, { id: employee.id })
