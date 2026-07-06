import type { AuditLog, AuthPermission, AuthRole, AuthUser, Client, Employee, EmployeeLoginProvisionPreview, EmployeeLoginProvisionResponse } from '../types/payroll'
import { deleteJson, getJson, postJson } from './apiClient'

export const loadSecurityData = async () => ({
  users: await getJson<AuthUser[]>('/api/security/users', []),
  roles: await getJson<AuthRole[]>('/api/security/roles', []),
  permissions: await getJson<AuthPermission[]>('/api/security/permissions', []),
  auditLogs: await getJson<AuditLog[]>('/api/audit-logs?limit=75', []),
  clients: await getJson<Client[]>('/api/clients', []),
  employees: await getJson<Employee[]>('/api/employees', [])
})

export async function saveSecurityUser(body: unknown) {
  const response = await postJson('/api/security/users', body, null)
  return { ok: response.ok, error: response.error }
}

export async function saveSecurityRole(body: unknown) {
  const response = await postJson('/api/security/roles', body, null)
  return { ok: response.ok, error: response.error }
}

export async function deleteSecurityUser(id: number) {
  const response = await deleteJson(`/api/security/users/${id}`, null)
  return { ok: response.ok, error: response.error }
}

export async function deleteSecurityRole(id: number) {
  const response = await deleteJson(`/api/security/roles/${id}`, null)
  return { ok: response.ok, error: response.error }
}

export async function loadEmployeeProvisionPreview(clientId?: string | number) {
  const query = clientId ? `?clientId=${encodeURIComponent(String(clientId))}` : ''
  return getJson<EmployeeLoginProvisionPreview[]>(`/api/security/users/employee-provision-preview${query}`, [])
}

export async function provisionEmployeeLogins(body: unknown) {
  return postJson('/api/security/users/provision-employees', body, null as EmployeeLoginProvisionResponse | null)
}
