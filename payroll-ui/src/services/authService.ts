import type { AuthUser } from '../types/payroll'
import { apiRequest, postEmpty, postJson } from './apiClient'

export async function getCurrentUser() {
  const response = await apiRequest('/api/auth/me')
  return response.ok ? response.json() as Promise<AuthUser> : null
}

export async function login(email: string, password: string) {
  const response = await postJson('/api/auth/login', { email, password }, null as { token: string; user: AuthUser } | null, { toast: false })
  return response.ok ? response.data : null
}

export const logout = () => postEmpty('/api/auth/logout', null, { toast: false })
