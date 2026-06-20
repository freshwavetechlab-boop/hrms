import type { AuthUser } from '../types/payroll'
import { api } from './apiClient'

export async function getCurrentUser() {
  const response = await fetch(`${api}/api/auth/me`)
  return response.ok ? response.json() as Promise<AuthUser> : null
}

export async function login(email: string, password: string) {
  const response = await fetch(`${api}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) })
  return response.ok ? response.json() as Promise<{ token: string; user: AuthUser }> : null
}

export const logout = () => fetch(`${api}/api/auth/logout`, { method: 'POST' })
