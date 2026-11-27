import { getJsonResult, type ApiOptions, type ApiResult } from './apiClient'

export type EmployeeEmailReminder = {
  employeeId: number
  employeeCode: string
  employeeName: string
  clientId: number
  clientName: string
  emailIssue: 'Missing' | 'Invalid'
}
export type EmployeeEmailReminderPage = { items: EmployeeEmailReminder[]; total: number; offset: number; limit: number }
export const employeeEmailReminderKey = (item: Pick<EmployeeEmailReminder, 'clientId' | 'employeeId'>) => `${item.clientId}:${item.employeeId}`

export function validEmployeeEmailReminderPage(value: unknown, offset: number, limit: number): value is EmployeeEmailReminderPage {
  if (!value || typeof value !== 'object') return false
  const page = value as EmployeeEmailReminderPage
  return page.offset === offset && page.limit === limit && Number.isSafeInteger(page.total) && page.total >= 0
    && Array.isArray(page.items) && page.items.length === Math.min(limit, Math.max(0, page.total - offset))
    && page.items.every(item => item && typeof item === 'object'
      && Number.isSafeInteger(item.employeeId) && item.employeeId > 0 && Number.isSafeInteger(item.clientId) && item.clientId > 0
      && typeof item.employeeCode === 'string' && typeof item.employeeName === 'string'
      && typeof item.clientName === 'string' && ['Missing', 'Invalid'].includes(item.emailIssue))
    && new Set(page.items.map(employeeEmailReminderKey)).size === page.items.length
}

export async function getEmployeeEmailReminders(offset = 0, limit = 100, options: Pick<ApiOptions, 'signal'> = {}): Promise<ApiResult<EmployeeEmailReminderPage>> {
  const empty = { items: [], total: 0, offset, limit }
  if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(limit) || limit < 1 || limit > 100) {
    return { ok: false, data: empty, error: 'Invalid email reminder page.', status: 400 }
  }
  const result = await getJsonResult<EmployeeEmailReminderPage>(`/api/employees/email-reminders?offset=${offset}&limit=${limit}`, empty, { ...options, loader: false, toast: false })
  return result.ok && !validEmployeeEmailReminderPage(result.data, offset, limit)
    ? { ...result, ok: false, data: empty, error: 'Email reminders could not be verified. Refresh to try again.' }
    : result
}
