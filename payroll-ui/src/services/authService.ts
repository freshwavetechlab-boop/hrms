import type { AuthUser } from '../types/payroll'
import { apiRequest, postEmpty, postJson } from './apiClient'

type LoginData = { token: string; user: AuthUser }

export async function getCurrentUser() {
  const response = await apiRequest('/api/auth/me')
  return response.ok ? response.json() as Promise<AuthUser> : null
}

export async function login(email: string, password: string) {
  return postJson('/api/auth/login', { email, password, portal: 'Admin' }, null as LoginData | null, { toast: false })
}

export const logout = () => postEmpty('/api/auth/logout', null, { toast: false })
