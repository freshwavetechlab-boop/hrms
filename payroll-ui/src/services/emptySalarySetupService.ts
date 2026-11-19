import { getJsonResult, postJson } from './apiClient'

export type EmptySalarySetupPreview = {
  employeeId: number; clientId: number; employeeCode: string; payPeriod: string; planHash: string;
  templateId: string; annualCtc: number; emptyEffectiveDates: string[]; blockers: string[]; canApply: boolean
}
export type EmptySalarySetupResult = {
  employeeId: number; clientId: number; payPeriod: string; requestId: string; repairedMarkers: number; replayed: boolean
}
export const previewEmptySalarySetup = (employeeId: number, clientId: number, payPeriod: string) =>
  getJsonResult<EmptySalarySetupPreview | null>(`/api/employees/${employeeId}/salary-setup-cleanup?${new URLSearchParams({ clientId: String(clientId), payPeriod })}`, null)
export const applyEmptySalarySetup = (preview: EmptySalarySetupPreview, requestId: string, reason: string) =>
  postJson('/api/employees/' + preview.employeeId + '/salary-setup-cleanup', {
    clientId: preview.clientId, payPeriod: preview.payPeriod, expectedPlanHash: preview.planHash,
    requestId, reason, acknowledged: true,
  }, null as EmptySalarySetupResult | null)
