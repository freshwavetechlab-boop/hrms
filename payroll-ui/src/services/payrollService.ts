import type { Client, Employee, PayRun, PayrollAdjustment, RunEmployee } from '../types/payroll'
import { apiUrl, deleteJson, getJson, postEmpty, postJson, putJson } from './apiClient'

export const getClients = () => getJson<Client[]>('/api/clients', [])
export const getEmployees = () => getJson<Employee[]>('/api/employees', [])
export const getPayRuns = () => getJson<PayRun[]>('/api/pay-runs', [])
export const getPayRun = (id: number) => getJson<PayRun | null>(`/api/pay-runs/${id}`, null)
export const createPayRun = (body: { clientId: number; payPeriod: string; payDate: string; totalWorkingDays: number; runType?: string; runName?: string; reason?: string; excludedEmployeeIds?: number[]; includedEmployeeIds?: number[]; adjustmentIds?: number[] }) => postJson<typeof body, PayRun | null>('/api/pay-runs', body, null)
export const updatePayRunEmployee = (payRunId: number, employee: RunEmployee) => putJson(`/api/pay-runs/${payRunId}/employees/${employee.employeeId}`, { presentDays: employee.presentDays, oneTimeEarnings: employee.oneTimeEarnings, oneTimeDeductions: employee.oneTimeDeductions, manualTds: employee.manualTds, isSkipped: employee.isSkipped }, null)
export const runPayRunAction = (id: number, path: string) => postEmpty<PayRun | null>(`/api/pay-runs/${id}/${path}`, null)
export const recordPayRunPayments = (id: number, body: { employeeIds: number[]; paymentDate: string }) => postJson<typeof body, PayRun | null>(`/api/pay-runs/${id}/payments`, body, null)
export const exportPayRunUrl = (id: number) => apiUrl(`/api/pay-runs/${id}/export`)
export const getPayrollAdjustments = (query: { clientId?: number; payPeriod?: string; status?: string } = {}) => {
  const params = new URLSearchParams()
  if (query.clientId) params.set('clientId', String(query.clientId))
  if (query.payPeriod) params.set('payPeriod', query.payPeriod)
  if (query.status) params.set('status', query.status)
  return getJson<PayrollAdjustment[]>(`/api/payroll-adjustments${params.size ? `?${params}` : ''}`, [])
}
export const savePayrollAdjustment = (body: PayrollAdjustment) => postJson<PayrollAdjustment, PayrollAdjustment | null>('/api/payroll-adjustments', body, null)
export const cancelPayrollAdjustment = (id: number) => deleteJson(`/api/payroll-adjustments/${id}`, null)
