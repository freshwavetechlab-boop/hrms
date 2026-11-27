import { getJsonResult, postJson } from './apiClient'
import type { BulkImportStatus } from './settingsService'

export type EmployeeImportJob = BulkImportStatus & {
  clientId: number
  clientName: string
  kind: 'employee-import' | 'salary-template-assignment' | 'salary-template-assignment-cleanup'
  operation: 'insert' | 'update' | 'upsert'
}

const jobsUrl = '/api/employees/import-jobs'
const quiet = { loader: false, toast: false } as const

export const listEmployeeImportJobs = () => getJsonResult<{ items: EmployeeImportJob[] }>(jobsUrl, { items: [] }, quiet)
export const readEmployeeImportJob = (id: string, clientId: number, signal: AbortSignal) =>
  getJsonResult<EmployeeImportJob | null>(`${jobsUrl}/${encodeURIComponent(id)}?clientId=${clientId}`, null, { ...quiet, signal })
export const dismissEmployeeImportJob = (id: string, clientId: number) =>
  postJson(`${jobsUrl}/${encodeURIComponent(id)}/dismiss?clientId=${clientId}`, {}, { ok: false }, quiet)

export const employeeJobIsRunning = (job: BulkImportStatus) => job.state === 'Queued' || job.state === 'Processing'
export const employeeJobTitle = (job: Pick<EmployeeImportJob, 'kind' | 'operation'>) =>
  job.kind === 'salary-template-assignment-cleanup' ? 'Salary template assignment and test payroll removal' :
  job.kind === 'salary-template-assignment' ? 'Salary template assignment' : job.operation === 'update' ? 'Employee bulk update' : 'Employee bulk upload'
