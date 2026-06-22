import type { Client, Employee, PayRun, RunEmployee } from '../types/payroll'
import { getJson, postEmpty, postJson, putJson } from './apiClient'

export const getClients = () => getJson<Client[]>('/api/clients', [])
export const getEmployees = () => getJson<Employee[]>('/api/employees', [])
export const getPayRuns = () => getJson<PayRun[]>('/api/pay-runs', [])
export const getPayRun = (id: number) => getJson<PayRun | null>(`/api/pay-runs/${id}`, null)
export const createPayRun = (body: { clientId: number; payPeriod: string; payDate: string; totalWorkingDays: number; excludedEmployeeIds: number[] }) => postJson<typeof body, PayRun | null>('/api/pay-runs', body, null)
export const updatePayRunEmployee = (payRunId: number, employee: RunEmployee) => putJson(`/api/pay-runs/${payRunId}/employees/${employee.employeeId}`, { presentDays: employee.presentDays, oneTimeEarnings: employee.oneTimeEarnings, oneTimeDeductions: employee.oneTimeDeductions, manualTds: employee.manualTds, isSkipped: employee.isSkipped }, null)
export const runPayRunAction = (id: number, path: string) => postEmpty<PayRun | null>(`/api/pay-runs/${id}/${path}`, null)
export const recordPayRunPayments = (id: number, body: { employeeIds: number[]; paymentDate: string }) => postJson<typeof body, PayRun | null>(`/api/pay-runs/${id}/payments`, body, null)
export const exportPayRunUrl = (id: number) => `${import.meta.env.VITE_API_URL ?? 'http://localhost:5062'}/api/pay-runs/${id}/export`
