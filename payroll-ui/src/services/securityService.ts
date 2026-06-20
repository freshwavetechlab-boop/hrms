import type { AuditLog, AuthPermission, AuthRole, AuthUser, Client, Employee } from '../types/payroll'
import { api, getJson, readError } from './apiClient'

export const loadSecurityData = async () => ({
  users: await getJson<AuthUser[]>('/api/security/users', []),
  roles: await getJson<AuthRole[]>('/api/security/roles', []),
  permissions: await getJson<AuthPermission[]>('/api/security/permissions', []),
  auditLogs: await getJson<AuditLog[]>('/api/audit-logs?limit=75', []),
  clients: await getJson<Client[]>('/api/clients', []),
  employees: await getJson<Employee[]>('/api/employees', [])
})

export async function saveSecurityUser(body: unknown) {
  const response = await fetch(`${api}/api/security/users`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  return { ok: response.ok, error: response.ok ? '' : await readError(response) }
}

export async function saveSecurityRole(body: unknown) {
  const response = await fetch(`${api}/api/security/roles`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  return { ok: response.ok, error: response.ok ? '' : await readError(response) }
}
